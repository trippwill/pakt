package encoding

import (
	"io"
	"sync"
)

var smPool = sync.Pool{
	New: func() any {
		return &stateMachine{
			stack: make([]frame, 0, 8),
		}
	},
}

type parserState int

const (
	stateTop parserState = iota
	stateAssignStart
	statePackStart
	stateValue
	stateStructOpen
	stateStructField
	stateStructSep
	stateStructClose
	stateTupleOpen
	stateTupleElem
	stateTupleSep
	stateTupleClose
	stateListOpen
	stateListElem
	stateListSep
	stateListClose
	stateMapOpen
	stateMapKey
	stateMapAfterKey
	stateMapAssign
	stateMapEntry
	stateMapClose
	statePackListItem
	statePackListSep
	statePackMapKey
	statePackMapAfterKey
	statePackMapSep
	stateAssignEnd
	statePackEnd
)

type frameKind int

const (
	frameAssign frameKind = iota
	framePack
	frameStruct
	frameTuple
	frameList
	frameMap
)

type frame struct {
	kind        frameKind
	resume      parserState
	childResume parserState
	name        string
	pos         Pos

	st       *StructType
	fieldIdx int

	tt      *TupleType
	elemIdx int

	lt *ListType

	mt     *MapType
	keyStr string
}

type statementHeader struct {
	pos  Pos
	name string
	typ  Type
	pack bool
}

type stateMachine struct {
	r       *reader
	stack   []frame
	state   parserState
	valType Type
	valName string
}

func newStateMachine(r *reader) *stateMachine {
	sm := smPool.Get().(*stateMachine)
	sm.r = r
	sm.stack = sm.stack[:0]
	sm.state = stateTop
	sm.valType = Type{}
	sm.valName = ""
	return sm
}

func (sm *stateMachine) release() {
	sm.r = nil
	sm.stack = sm.stack[:0]
	smPool.Put(sm)
}

func (sm *stateMachine) atTop() bool {
	return sm.state == stateTop && len(sm.stack) == 0
}

func (sm *stateMachine) current() *frame {
	return &sm.stack[len(sm.stack)-1]
}

func (sm *stateMachine) push(fr frame) {
	sm.stack = append(sm.stack, fr)
}

func (sm *stateMachine) pop() frame {
	last := len(sm.stack) - 1
	fr := sm.stack[last]
	sm.stack = sm.stack[:last]
	return fr
}

func (sm *stateMachine) currentChildResume() parserState {
	if len(sm.stack) == 0 {
		return stateTop
	}
	return sm.stack[len(sm.stack)-1].childResume
}

func (sm *stateMachine) readStatementHeader() (statementHeader, error) {
	sm.r.skipInsignificant(true)

	b, err := sm.r.peekByte()
	if err != nil {
		return statementHeader{}, err
	}
	// NUL byte at top level is end-of-unit (spec §10.1).
	if b == 0 {
		sm.r.readByte() //nolint:errcheck
		sm.r.hitNUL = true
		return statementHeader{}, io.EOF
	}

	identPos := sm.r.pos
	name, err := sm.r.readIdent()
	if err != nil {
		return statementHeader{}, err
	}

	typ, err := sm.r.readTypeAnnot()
	if err != nil {
		return statementHeader{}, err
	}

	sm.r.skipWS()
	switch b, err := sm.r.peekByte(); {
	case err != nil:
		return statementHeader{}, sm.r.wrapf(ErrUnexpectedEOF, "expected '=' or '<<' after statement header")
	case b == '=':
		sm.r.readByte() //nolint:errcheck
	case b == '<':
		p, perr := sm.r.src.Peek(2)
		if perr != nil || len(p) < 2 || p[0] != '<' || p[1] != '<' {
			return statementHeader{}, sm.r.errorf("expected '=' or '<<' after statement header")
		}
		sm.r.readByte() //nolint:errcheck
		sm.r.readByte() //nolint:errcheck
		if typ.List == nil && typ.Map == nil {
			return statementHeader{}, sm.r.errorf("pack type must be list or map, got %s", typ.String())
		}
		sm.r.skipWS()
		return statementHeader{
			pos:  identPos,
			name: name,
			typ:  typ,
			pack: true,
		}, nil
	default:
		return statementHeader{}, sm.r.errorf("expected '=' or '<<' after statement header")
	}
	sm.r.skipWS()

	return statementHeader{
		pos:  identPos,
		name: name,
		typ:  typ,
	}, nil
}

func (sm *stateMachine) beginAssignment(h statementHeader) {
	sm.push(frame{
		kind:        frameAssign,
		resume:      stateTop,
		childResume: stateAssignEnd,
		name:        h.name,
		pos:         h.pos,
	})
	sm.valType = h.typ
	sm.valName = h.name
	sm.state = stateAssignStart
}

func (sm *stateMachine) beginPack(h statementHeader) {
	fr := frame{
		kind:   framePack,
		resume: stateTop,
		name:   h.name,
		pos:    h.pos,
	}
	if h.typ.List != nil {
		fr.lt = h.typ.List
	} else {
		fr.mt = h.typ.Map
	}
	sm.push(fr)
	sm.state = statePackStart
}

func (sm *stateMachine) beginStatement(h statementHeader) {
	if h.pack {
		sm.beginPack(h)
		return
	}
	sm.beginAssignment(h)
}

func (sm *stateMachine) primeNextMatchedStatement(spec *Spec) (string, error) {
	for {
		h, err := sm.readStatementHeader()
		if err != nil {
			return "", err
		}

		specType, ok := spec.Fields[h.name]
		if !ok {
			if err := sm.r.skipStatementBody(h); err != nil {
				return "", err
			}
			continue
		}

		if specType.String() != h.typ.String() {
			return "", Wrapf(h.pos, ErrTypeMismatch, "spec field %q expected type %s, got %s", h.name, specType.String(), h.typ.String())
		}

		sm.beginStatement(h)
		return h.name, nil
	}
}

// packTerminated checks whether the pack has ended (EOF, NUL, or next
// top-level statement). With the '|' prefix on atom values and reserved
// keywords for booleans/nil, a bare identifier always starts a new statement.
func (sm *stateMachine) packTerminated() (bool, error) {
	sm.r.skipInsignificant(true)
	b, err := sm.r.peekByte()
	if err != nil {
		if err == io.EOF {
			return true, nil
		}
		return false, err
	}
	// NUL byte terminates the pack (end-of-unit per spec §10.1).
	if b == 0 {
		sm.r.readByte() //nolint:errcheck
		sm.r.hitNUL = true
		return true, nil
	}
	return !sm.r.canStartValueInPack(b), nil
}

// canStartValue reports whether b can be the first byte of any PAKT value.
// This is the simple single-byte check used in skip paths where two-byte
// lookahead is handled separately.
func canStartValue(b byte) bool {
	switch {
	case b == '\'' || b == '"':
		return true // string
	case b == '{':
		return true // struct
	case b == '(':
		return true // tuple
	case b == '[':
		return true // list
	case b == '<':
		return true // map
	case b == '|':
		return true // atom value
	case b == '.':
		return true // leading-dot decimal
	case b == '-':
		return true // negative number
	case isDigit(b):
		return true // number/date/time/uuid
	default:
		return false
	}
}

// canStartValueInPack reports whether the byte at the reader's current
// position begins a value (as opposed to a new statement header).
// For ambiguous single-byte cases (r, b, x, t, f, n), it performs
// additional lookahead checks.
func (r *reader) canStartValueInPack(b byte) bool {
	if canStartValue(b) {
		return true
	}
	switch b {
	case 't':
		return r.peekKeyword("true") || r.peekKeyword("false") // 't' only valid as true
	case 'f':
		return r.peekKeyword("false")
	case 'n':
		return r.peekKeyword("nil")
	case 'r':
		return r.peekRawStringStart()
	case 'b', 'x':
		return r.peekBinLiteralStart()
	}
	return false
}

// peekKeyword reports whether the next bytes match the given keyword exactly,
// followed by a non-identifier byte (or EOF).
func (r *reader) peekKeyword(kw string) bool {
	p, err := r.src.Peek(len(kw) + 1)
	if err != nil && len(p) < len(kw) {
		return false
	}
	for i := 0; i < len(kw); i++ {
		if p[i] != kw[i] {
			return false
		}
	}
	if len(p) > len(kw) {
		next := p[len(kw)]
		if isAlpha(next) || isDigit(next) || next == '_' || next == '-' {
			return false
		}
	}
	return true
}

func (sm *stateMachine) beginMapKeyValue(keyType Type, after parserState) (Event, bool, error) {
	fr := sm.current()

	switch {
	case keyType.Nullable && sm.r.peekNil():
		pos := sm.r.pos
		if err := sm.r.readNil(); err != nil {
			return Event{}, false, err
		}
		fr.keyStr = "nil"
		sm.state = after
		return Event{
			Kind:       EventScalarValue,
			Pos:        pos,
			Name:       fr.keyStr,
			ScalarType: scalarTypeKind(keyType),
			Value:      fr.keyStr,
		}, true, nil

	case !keyType.Nullable && sm.r.peekNil():
		return Event{}, false, sm.r.wrapf(ErrNilNonNullable, "nil value for non-nullable type %s", keyType.String())

	case keyType.Scalar != nil:
		val, pos, err := sm.r.readScalarDirect(*keyType.Scalar)
		if err != nil {
			return Event{}, false, err
		}
		fr.keyStr = val
		sm.state = after
		return Event{
			Kind:       EventScalarValue,
			Pos:        pos,
			Name:       val,
			ScalarType: *keyType.Scalar,
			Value:      val,
		}, true, nil

	case keyType.AtomSet != nil:
		pos := sm.r.pos
		val, err := sm.r.readAtom(keyType.AtomSet.Members)
		if err != nil {
			return Event{}, false, err
		}
		fr.keyStr = val
		sm.state = after
		return Event{
			Kind:       EventScalarValue,
			Pos:        pos,
			Name:       val,
			ScalarType: TypeAtom,
			Value:      val,
		}, true, nil
	}

	fr.keyStr = ""
	fr.childResume = after
	sm.valType = keyType
	sm.valName = ""
	sm.state = stateValue
	return Event{}, false, nil
}

func scalarTypeKind(t Type) TypeKind {
	switch {
	case t.Scalar != nil:
		return *t.Scalar
	case t.AtomSet != nil:
		return TypeAtom
	default:
		return TypeNone
	}
}

func (sm *stateMachine) step() (Event, error) {
	for {
		switch sm.state {
		case stateTop:
			h, err := sm.readStatementHeader()
			if err != nil {
				return Event{}, err
			}
			sm.beginStatement(h)

		case stateAssignStart:
			fr := sm.current()
			sm.state = stateValue
			return Event{
				Kind: EventAssignStart,
				Pos:  fr.pos,
				Name: fr.name,
			}, nil

		case statePackStart:
			fr := sm.current()
			var kind EventKind
			if fr.lt != nil {
				fr.elemIdx = 0
				sm.state = statePackListItem
				kind = EventListPackStart
			} else {
				fr.keyStr = ""
				sm.state = statePackMapKey
				kind = EventMapPackStart
			}
			return Event{
				Kind: kind,
				Pos:  fr.pos,
				Name: fr.name,
			}, nil

		case stateValue:
			sm.r.skipWS()

			typ := sm.valType
			name := sm.valName

			if typ.Nullable {
				if sm.r.peekNil() {
					pos := sm.r.pos
					if err := sm.r.readNil(); err != nil {
						return Event{}, err
					}
					sm.state = sm.currentChildResume()
					return Event{
						Kind:       EventScalarValue,
						Pos:        pos,
						Name:       name,
						ScalarType: scalarTypeKind(typ),
						Value:      "nil",
					}, nil
				}
			} else if sm.r.peekNil() {
				return Event{}, sm.r.wrapf(ErrNilNonNullable, "nil value for non-nullable type %s", typ.String())
			}

			switch {
			case typ.Scalar != nil:
				val, pos, err := sm.r.readScalarDirect(*typ.Scalar)
				if err != nil {
					return Event{}, err
				}
				sm.state = sm.currentChildResume()
				return Event{
					Kind:       EventScalarValue,
					Pos:        pos,
					Name:       name,
					ScalarType: *typ.Scalar,
					Value:      val,
				}, nil

			case typ.AtomSet != nil:
				pos := sm.r.pos
				val, err := sm.r.readAtom(typ.AtomSet.Members)
				if err != nil {
					return Event{}, err
				}
				sm.state = sm.currentChildResume()
				return Event{
					Kind:       EventScalarValue,
					Pos:        pos,
					Name:       name,
					ScalarType: TypeAtom,
					Value:      val,
				}, nil

			case typ.Struct != nil:
				sm.push(frame{
					kind:   frameStruct,
					resume: sm.currentChildResume(),
					name:   name,
					st:     typ.Struct,
				})
				sm.state = stateStructOpen

			case typ.Tuple != nil:
				sm.push(frame{
					kind:   frameTuple,
					resume: sm.currentChildResume(),
					name:   name,
					tt:     typ.Tuple,
				})
				sm.state = stateTupleOpen

			case typ.List != nil:
				sm.push(frame{
					kind:   frameList,
					resume: sm.currentChildResume(),
					name:   name,
					lt:     typ.List,
				})
				sm.state = stateListOpen

			case typ.Map != nil:
				sm.push(frame{
					kind:   frameMap,
					resume: sm.currentChildResume(),
					name:   name,
					mt:     typ.Map,
				})
				sm.state = stateMapOpen

			default:
				return Event{}, sm.r.errorf("unknown type: no type variant set")
			}

		case stateStructOpen:
			fr := sm.current()
			sm.r.skipWS()
			fr.pos = sm.r.pos
			if err := sm.r.expectByte('{'); err != nil {
				return Event{}, err
			}
			fr.fieldIdx = 0
			if len(fr.st.Fields) == 0 {
				sm.state = stateStructClose
			} else {
				sm.state = stateStructField
			}
			return Event{
				Kind: EventStructStart,
				Pos:  fr.pos,
				Name: fr.name,
			}, nil

		case stateStructField:
			fr := sm.current()
			if fr.fieldIdx == 0 {
				sm.r.skipInsignificant(true)
			}

			b, err := sm.r.peekByte()
			if err != nil {
				return Event{}, sm.r.wrapf(ErrUnexpectedEOF, "unterminated struct value")
			}
			if b == '}' {
				return Event{}, sm.r.errorf(
					"too few values in struct: expected %d fields, got %d",
					len(fr.st.Fields),
					fr.fieldIdx,
				)
			}

			field := fr.st.Fields[fr.fieldIdx]
			fr.childResume = stateStructSep
			sm.valType = field.Type
			sm.valName = field.Name
			sm.state = stateValue

		case stateStructSep:
			fr := sm.current()
			fr.fieldIdx++

			if fr.fieldIdx < len(fr.st.Fields) {
				sep, err := sm.r.readSep()
				if err != nil {
					return Event{}, err
				}
				if !sep {
					sm.r.skipWS()
					b, err := sm.r.peekByte()
					if err != nil {
						return Event{}, sm.r.wrapf(ErrUnexpectedEOF, "unterminated struct value")
					}
					if b == '}' {
						return Event{}, sm.r.errorf(
							"too few values in struct: expected %d fields, got %d",
							len(fr.st.Fields),
							fr.fieldIdx,
						)
					}
					return Event{}, sm.r.errorf("expected separator between struct fields")
				}
				sm.state = stateStructField
				continue
			}

			sm.r.readSep() //nolint:errcheck
			sm.r.skipInsignificant(true)
			sm.state = stateStructClose

		case stateStructClose:
			pos := sm.r.pos
			if err := sm.r.expectByte('}'); err != nil {
				return Event{}, err
			}
			fr := sm.pop()
			sm.state = fr.resume
			return Event{
				Kind: EventStructEnd,
				Pos:  pos,
			}, nil

		case stateTupleOpen:
			fr := sm.current()
			sm.r.skipWS()
			fr.pos = sm.r.pos
			if err := sm.r.expectByte('('); err != nil {
				return Event{}, err
			}
			fr.elemIdx = 0
			if len(fr.tt.Elements) == 0 {
				sm.state = stateTupleClose
			} else {
				sm.state = stateTupleElem
			}
			return Event{
				Kind: EventTupleStart,
				Pos:  fr.pos,
				Name: fr.name,
			}, nil

		case stateTupleElem:
			fr := sm.current()
			if fr.elemIdx == 0 {
				sm.r.skipInsignificant(true)
			}

			b, err := sm.r.peekByte()
			if err != nil {
				return Event{}, sm.r.wrapf(ErrUnexpectedEOF, "unterminated tuple value")
			}
			if b == ')' {
				return Event{}, sm.r.errorf(
					"too few values in tuple: expected %d elements, got %d",
					len(fr.tt.Elements),
					fr.elemIdx,
				)
			}

			fr.childResume = stateTupleSep
			sm.valType = fr.tt.Elements[fr.elemIdx]
			sm.valName = indexName(fr.elemIdx)
			sm.state = stateValue

		case stateTupleSep:
			fr := sm.current()
			fr.elemIdx++

			if fr.elemIdx < len(fr.tt.Elements) {
				sep, err := sm.r.readSep()
				if err != nil {
					return Event{}, err
				}
				if !sep {
					sm.r.skipWS()
					b, err := sm.r.peekByte()
					if err != nil {
						return Event{}, sm.r.wrapf(ErrUnexpectedEOF, "unterminated tuple value")
					}
					if b == ')' {
						return Event{}, sm.r.errorf(
							"too few values in tuple: expected %d elements, got %d",
							len(fr.tt.Elements),
							fr.elemIdx,
						)
					}
					return Event{}, sm.r.errorf("expected separator between tuple elements")
				}
				sm.state = stateTupleElem
				continue
			}

			sm.r.readSep() //nolint:errcheck
			sm.r.skipInsignificant(true)
			sm.state = stateTupleClose

		case stateTupleClose:
			pos := sm.r.pos
			if err := sm.r.expectByte(')'); err != nil {
				return Event{}, err
			}
			fr := sm.pop()
			sm.state = fr.resume
			return Event{
				Kind: EventTupleEnd,
				Pos:  pos,
			}, nil

		case stateListOpen:
			fr := sm.current()
			sm.r.skipWS()
			fr.pos = sm.r.pos
			if err := sm.r.expectByte('['); err != nil {
				return Event{}, err
			}
			fr.elemIdx = 0
			sm.state = stateListElem
			return Event{
				Kind: EventListStart,
				Pos:  fr.pos,
				Name: fr.name,
			}, nil

		case stateListElem:
			fr := sm.current()
			sm.r.skipInsignificant(true)

			b, err := sm.r.peekByte()
			if err != nil {
				return Event{}, sm.r.wrapf(ErrUnexpectedEOF, "unterminated list value")
			}
			if b == ']' {
				sm.state = stateListClose
				continue
			}

			fr.childResume = stateListSep
			sm.valType = fr.lt.Element
			sm.valName = indexName(fr.elemIdx)
			sm.state = stateValue

		case stateListSep:
			fr := sm.current()
			fr.elemIdx++

			sep, err := sm.r.readSep()
			if err != nil {
				return Event{}, err
			}
			if !sep {
				sm.r.skipWS()
				b, err := sm.r.peekByte()
				if err != nil {
					return Event{}, sm.r.wrapf(ErrUnexpectedEOF, "unterminated list value")
				}
				if b != ']' {
					return Event{}, sm.r.errorf("expected ',' or ']' in list, got %q", rune(b))
				}
				sm.state = stateListClose
				continue
			}

			sm.state = stateListElem

		case stateListClose:
			pos := sm.r.pos
			if err := sm.r.expectByte(']'); err != nil {
				return Event{}, err
			}
			fr := sm.pop()
			sm.state = fr.resume
			return Event{
				Kind: EventListEnd,
				Pos:  pos,
			}, nil

		case stateMapOpen:
			fr := sm.current()
			sm.r.skipWS()
			fr.pos = sm.r.pos
			if err := sm.r.expectByte('<'); err != nil {
				return Event{}, err
			}
			fr.keyStr = ""
			sm.state = stateMapKey
			return Event{
				Kind: EventMapStart,
				Pos:  fr.pos,
				Name: fr.name,
			}, nil

		case stateMapKey:
			fr := sm.current()
			keyType := fr.mt.Key

			sm.r.skipInsignificant(true)
			b, err := sm.r.peekByte()
			if err != nil {
				return Event{}, sm.r.wrapf(ErrUnexpectedEOF, "unterminated map value")
			}
			if b == '>' {
				sm.state = stateMapClose
				continue
			}

			ev, emitted, err := sm.beginMapKeyValue(keyType, stateMapAfterKey)
			if err != nil {
				return Event{}, err
			}
			if emitted {
				return ev, nil
			}

		case stateMapAfterKey:
			sm.state = stateMapAssign

		case stateMapAssign:
			fr := sm.current()
			sm.r.skipWS()
			if err := sm.r.expectByte(';'); err != nil {
				return Event{}, err
			}
			sm.r.skipWS()
			fr.childResume = stateMapEntry
			sm.valType = fr.mt.Value
			sm.valName = fr.keyStr
			sm.state = stateValue

		case stateMapEntry:
			sep, err := sm.r.readSep()
			if err != nil {
				return Event{}, err
			}
			if !sep {
				sm.r.skipWS()
				b, err := sm.r.peekByte()
				if err != nil {
					return Event{}, sm.r.wrapf(ErrUnexpectedEOF, "unterminated map value")
				}
				if b != '>' {
					return Event{}, sm.r.errorf("expected ',' or '>' in map, got %q", rune(b))
				}
				sm.state = stateMapClose
				continue
			}

			sm.state = stateMapKey

		case statePackListItem:
			fr := sm.current()
			done, err := sm.packTerminated()
			if err != nil {
				return Event{}, err
			}
			if done {
				sm.state = statePackEnd
				continue
			}

			fr.childResume = statePackListSep
			sm.valType = fr.lt.Element
			sm.valName = indexName(fr.elemIdx)
			sm.state = stateValue

		case statePackListSep:
			fr := sm.current()
			fr.elemIdx++

			sep, err := sm.r.readSep()
			if err != nil {
				return Event{}, err
			}
			if !sep {
				done, err := sm.packTerminated()
				if err != nil {
					return Event{}, err
				}
				if done {
					sm.state = statePackEnd
					continue
				}
				return Event{}, sm.r.errorf("expected separator between pack items")
			}

			sm.state = statePackListItem

		case statePackMapKey:
			fr := sm.current()
			done, err := sm.packTerminated()
			if err != nil {
				return Event{}, err
			}
			if done {
				sm.state = statePackEnd
				continue
			}

			ev, emitted, err := sm.beginMapKeyValue(fr.mt.Key, statePackMapAfterKey)
			if err != nil {
				return Event{}, err
			}
			if emitted {
				return ev, nil
			}

		case statePackMapAfterKey:
			fr := sm.current()
			sm.r.skipWS()
			if err := sm.r.expectByte(';'); err != nil {
				return Event{}, err
			}
			sm.r.skipWS()
			fr.childResume = statePackMapSep
			sm.valType = fr.mt.Value
			sm.valName = fr.keyStr
			sm.state = stateValue

		case statePackMapSep:
			sep, err := sm.r.readSep()
			if err != nil {
				return Event{}, err
			}
			if !sep {
				done, err := sm.packTerminated()
				if err != nil {
					return Event{}, err
				}
				if done {
					sm.state = statePackEnd
					continue
				}
				return Event{}, sm.r.errorf("expected separator between pack map entries")
			}

			sm.state = statePackMapKey

		case stateMapClose:
			pos := sm.r.pos
			if err := sm.r.expectByte('>'); err != nil {
				return Event{}, err
			}
			fr := sm.pop()
			sm.state = fr.resume
			return Event{
				Kind: EventMapEnd,
				Pos:  pos,
			}, nil

		case statePackEnd:
			pos := sm.r.pos
			fr := sm.pop()
			sm.state = stateTop
			var kind EventKind
			if fr.lt != nil {
				kind = EventListPackEnd
			} else {
				kind = EventMapPackEnd
			}
			return Event{
				Kind: kind,
				Pos:  pos,
				Name: fr.name,
			}, nil

		case stateAssignEnd:
			pos := sm.r.pos
			fr := sm.pop()
			sm.state = stateTop
			return Event{
				Kind: EventAssignEnd,
				Pos:  pos,
				Name: fr.name,
			}, nil
		}
	}
}
