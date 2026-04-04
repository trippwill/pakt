package encoding

import "fmt"

// ---------------------------------------------------------------------------
// Pre-computed index name table for tuples and lists
// ---------------------------------------------------------------------------

var indexNames [64]string

func init() {
	for i := range indexNames {
		indexNames[i] = fmt.Sprintf("[%d]", i)
	}
}

func indexName(i int) string {
	if i < len(indexNames) {
		return indexNames[i]
	}
	return fmt.Sprintf("[%d]", i)
}

// ---------------------------------------------------------------------------
// Event emission helpers
// ---------------------------------------------------------------------------

func (r *reader) emit(kind EventKind, pos Pos, name, typ, value string) {
	r.events = append(r.events, Event{
		Kind:  kind,
		Pos:   pos,
		Name:  name,
		Type:  typ,
		Value: value,
	})
}

// ---------------------------------------------------------------------------
// SEP handling
// ---------------------------------------------------------------------------

// readSep attempts to consume a separator (comma or newline).
// It first skips WS and comments (but not newlines). Then:
//   - if the next byte is ',' → consume it, skip trailing WS/NL/comments, return true
//   - if the next byte is '\n' or '\r' → consume it, skip trailing WS/NL/comments, return true
//   - otherwise → return false
func (r *reader) readSep() (bool, error) {
	r.skipInsignificant(false) // skip WS and comments, but not newlines
	b, err := r.peekByte()
	if err != nil {
		return false, nil // EOF is not an error for SEP
	}
	if b == ',' {
		r.readByte()              //nolint:errcheck
		r.skipInsignificant(true) // skip WS, NL, comments after comma
		return true, nil
	}
	if b == '\n' || b == '\r' {
		r.readByte()              //nolint:errcheck
		r.skipInsignificant(true) // skip WS, NL, comments after newline
		return true, nil
	}
	return false, nil
}

// ---------------------------------------------------------------------------
// Value reading — main dispatch
// ---------------------------------------------------------------------------

// readValue reads a value according to the given type and emits events.
// The name parameter is threaded through to the first emitted event.
func (r *reader) readValue(typ Type, name string) error {
	// Check for nil on nullable types by peeking ahead for "nil" keyword.
	if typ.Nullable {
		if r.peekNil() {
			pos := r.pos
			if err := r.readNil(); err != nil {
				return err
			}
			r.emit(EventScalarValue, pos, name, typ.String(), "nil")
			return nil
		}
	} else if r.peekNil() {
		return r.wrapf(ErrNilNonNullable, "nil value for non-nullable type %s", typ.String())
	}

	switch {
	case typ.Scalar != nil:
		return r.readScalarValue(*typ.Scalar, name)
	case typ.AtomSet != nil:
		return r.readAtomValue(typ.AtomSet, name)
	case typ.Struct != nil:
		return r.readStructValue(typ.Struct, name)
	case typ.Tuple != nil:
		return r.readTupleValue(typ.Tuple, name)
	case typ.List != nil:
		return r.readListValue(typ.List, name)
	case typ.Map != nil:
		return r.readMapValue(typ.Map, name)
	default:
		return r.errorf("unknown type: no type variant set")
	}
}

// ---------------------------------------------------------------------------
// Scalar value reading
// ---------------------------------------------------------------------------

func (r *reader) readScalarValue(kind TypeKind, name string) error {
	val, pos, err := r.readScalarDirect(kind)
	if err != nil {
		return err
	}

	r.emit(EventScalarValue, pos, name, kind.String(), val)
	return nil
}

// ---------------------------------------------------------------------------
// Atom value reading
// ---------------------------------------------------------------------------

func (r *reader) readAtomValue(atoms *AtomSet, name string) error {
	pos := r.pos
	val, err := r.readAtom(atoms.Members)
	if err != nil {
		return err
	}
	r.emit(EventScalarValue, pos, name, atoms.String(), val)
	return nil
}

// ---------------------------------------------------------------------------
// Struct value reading
// ---------------------------------------------------------------------------

func (r *reader) readStructValue(st *StructType, name string) error {
	r.skipWS()
	pos := r.pos
	if err := r.expectByte('{'); err != nil {
		return err
	}
	r.emit(EventCompositeStart, pos, name, st.String(), "")

	for i, field := range st.Fields {
		if i == 0 {
			r.skipInsignificant(true)
		}

		// Check for premature closing.
		b, err := r.peekByte()
		if err != nil {
			return r.wrapf(ErrUnexpectedEOF, "unterminated struct value")
		}
		if b == '}' {
			return r.errorf("too few values in struct: expected %d fields, got %d", len(st.Fields), i)
		}

		if err := r.readValue(field.Type, field.Name); err != nil {
			return err
		}

		// After each value (except the last), consume SEP.
		if i < len(st.Fields)-1 {
			sep, err := r.readSep()
			if err != nil {
				return err
			}
			if !sep {
				// No separator — next must be '}'.
				r.skipWS()
				b, err = r.peekByte()
				if err != nil {
					return r.wrapf(ErrUnexpectedEOF, "unterminated struct value")
				}
				if b == '}' {
					return r.errorf("too few values in struct: expected %d fields, got %d", len(st.Fields), i+1)
				}
				return r.errorf("expected separator between struct fields")
			}
		}
	}

	// Consume optional trailing SEP and insignificant content.
	r.readSep() //nolint:errcheck
	r.skipInsignificant(true)

	pos = r.pos
	if err := r.expectByte('}'); err != nil {
		return err
	}
	r.emit(EventCompositeEnd, pos, "", st.String(), "")
	return nil
}

// ---------------------------------------------------------------------------
// Tuple value reading
// ---------------------------------------------------------------------------

func (r *reader) readTupleValue(tt *TupleType, name string) error {
	r.skipWS()
	pos := r.pos
	if err := r.expectByte('('); err != nil {
		return err
	}
	r.emit(EventCompositeStart, pos, name, tt.String(), "")

	for i, elem := range tt.Elements {
		if i == 0 {
			r.skipInsignificant(true)
		}

		b, err := r.peekByte()
		if err != nil {
			return r.wrapf(ErrUnexpectedEOF, "unterminated tuple value")
		}
		if b == ')' {
			return r.errorf("too few values in tuple: expected %d elements, got %d", len(tt.Elements), i)
		}

		if err := r.readValue(elem, indexName(i)); err != nil {
			return err
		}

		if i < len(tt.Elements)-1 {
			sep, err := r.readSep()
			if err != nil {
				return err
			}
			if !sep {
				r.skipWS()
				b, err = r.peekByte()
				if err != nil {
					return r.wrapf(ErrUnexpectedEOF, "unterminated tuple value")
				}
				if b == ')' {
					return r.errorf("too few values in tuple: expected %d elements, got %d", len(tt.Elements), i+1)
				}
				return r.errorf("expected separator between tuple elements")
			}
		}
	}

	r.readSep() //nolint:errcheck
	r.skipInsignificant(true)

	pos = r.pos
	if err := r.expectByte(')'); err != nil {
		return err
	}
	r.emit(EventCompositeEnd, pos, "", tt.String(), "")
	return nil
}

// ---------------------------------------------------------------------------
// List value reading
// ---------------------------------------------------------------------------

func (r *reader) readListValue(lt *ListType, name string) error {
	r.skipWS()
	pos := r.pos
	if err := r.expectByte('['); err != nil {
		return err
	}
	r.emit(EventCompositeStart, pos, name, lt.String(), "")

	idx := 0
	for {
		r.skipInsignificant(true)

		b, err := r.peekByte()
		if err != nil {
			return r.wrapf(ErrUnexpectedEOF, "unterminated list value")
		}
		if b == ']' {
			break
		}

		if err := r.readValue(lt.Element, indexName(idx)); err != nil {
			return err
		}
		idx++

		// Consume optional SEP; if no SEP, must be at ']'.
		sep, err := r.readSep()
		if err != nil {
			return err
		}
		if !sep {
			// No separator — next must be ']'.
			r.skipWS()
			b, err = r.peekByte()
			if err != nil {
				return r.wrapf(ErrUnexpectedEOF, "unterminated list value")
			}
			if b != ']' {
				return r.errorf("expected ',' or ']' in list, got %q", rune(b))
			}
		}
	}

	pos = r.pos
	r.readByte() //nolint:errcheck // consume ']'
	r.emit(EventCompositeEnd, pos, "", lt.String(), "")
	return nil
}

// ---------------------------------------------------------------------------
// Map value reading
// ---------------------------------------------------------------------------

func (r *reader) readMapValue(mt *MapType, name string) error {
	r.skipWS()
	pos := r.pos
	if err := r.expectByte('<'); err != nil {
		return err
	}
	r.emit(EventCompositeStart, pos, name, mt.String(), "")

	seen := make(map[string]struct{})

	for {
		r.skipInsignificant(true)

		b, err := r.peekByte()
		if err != nil {
			return r.wrapf(ErrUnexpectedEOF, "unterminated map value")
		}
		if b == '>' {
			break
		}

		// Read key — for scalar/atom keys, read directly to capture the key string.
		var keyStr string
		switch {
		case mt.Key.Nullable && r.peekNil():
			keyPos := r.pos
			if err := r.readNil(); err != nil {
				return err
			}
			keyStr = "nil"
			r.emit(EventScalarValue, keyPos, keyStr, mt.Key.String(), keyStr)
		case !mt.Key.Nullable && r.peekNil():
			return r.wrapf(ErrNilNonNullable, "nil value for non-nullable type %s", mt.Key.String())
		case mt.Key.Scalar != nil:
			keyVal, keyPos, err := r.readScalarDirect(*mt.Key.Scalar)
			if err != nil {
				return err
			}
			keyStr = keyVal
			r.emit(EventScalarValue, keyPos, keyStr, mt.Key.Scalar.String(), keyStr)
		case mt.Key.AtomSet != nil:
			keyPos := r.pos
			keyVal, err := r.readAtom(mt.Key.AtomSet.Members)
			if err != nil {
				return err
			}
			keyStr = keyVal
			r.emit(EventScalarValue, keyPos, keyStr, mt.Key.AtomSet.String(), keyStr)
		default:
			// Composite key — rare; read normally with empty name.
			if err := r.readValue(mt.Key, ""); err != nil {
				return err
			}
		}

		if _, dup := seen[keyStr]; dup {
			return r.wrapf(ErrDuplicateKey, "duplicate map key: %s", keyStr)
		}
		seen[keyStr] = struct{}{}

		// Expect '=' between key and value.
		r.skipWS()
		if err := r.expectByte('='); err != nil {
			return err
		}
		r.skipWS()

		// Read value, named with the key string.
		if err := r.readValue(mt.Value, keyStr); err != nil {
			return err
		}

		// Consume optional SEP.
		sep, err := r.readSep()
		if err != nil {
			return err
		}
		if !sep {
			r.skipWS()
			b, err = r.peekByte()
			if err != nil {
				return r.wrapf(ErrUnexpectedEOF, "unterminated map value")
			}
			if b != '>' {
				return r.errorf("expected ',' or '>' in map, got %q", rune(b))
			}
		}
	}

	pos = r.pos
	r.readByte() //nolint:errcheck // consume '>'
	r.emit(EventCompositeEnd, pos, "", mt.String(), "")
	return nil
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

// readScalarDirect reads a scalar value and returns it without emitting an event.
func (r *reader) readScalarDirect(kind TypeKind) (string, Pos, error) {
	pos := r.pos
	var val string
	var err error

	switch kind {
	case TypeStr:
		val, err = r.readString()
	case TypeInt:
		val, err = r.readInt()
	case TypeDec:
		val, err = r.readDec()
	case TypeFloat:
		val, err = r.readFloat()
	case TypeBool:
		val, err = r.readBool()
	case TypeUUID:
		val, err = r.readUUID()
	case TypeDate:
		val, err = r.readDate()
	case TypeTime:
		val, err = r.readTime()
	case TypeDateTime:
		val, err = r.readDateTime()
	default:
		return "", pos, r.errorf("unknown scalar type kind %d", int(kind))
	}
	return val, pos, err
}

// peekNil checks whether the next non-WS content is the keyword "nil" followed
// by a non-identifier byte. It does not consume any input.
func (r *reader) peekNil() bool {
	// We need to peek at up to 3 bytes (n, i, l) plus check the byte after.
	// But first we need to skip WS in the peek buffer without consuming.
	// Use the bufio Peek to look ahead.
	p, err := r.buf.Peek(256) // peek a generous amount
	if err != nil && len(p) == 0 {
		return false
	}
	i := 0
	// skip WS (spaces and tabs only)
	for i < len(p) && (p[i] == ' ' || p[i] == '\t') {
		i++
	}
	// Check for "nil"
	if i+3 > len(p) {
		return false
	}
	if p[i] != 'n' || p[i+1] != 'i' || p[i+2] != 'l' {
		return false
	}
	// Ensure "nil" is not a prefix of a longer identifier.
	if i+3 < len(p) {
		next := p[i+3]
		if isAlpha(next) || isDigit(next) || next == '_' || next == '-' {
			return false
		}
	}
	return true
}

// ---------------------------------------------------------------------------
// Assignment reading
// ---------------------------------------------------------------------------

// readAssignment reads a top-level assignment: IDENT type_annot '=' value.
// It emits AssignStart, value events, and AssignEnd.
func (r *reader) readAssignment() error {
	r.skipInsignificant(true)

	// Check for EOF.
	if _, err := r.peekByte(); err != nil {
		return err // io.EOF propagates
	}

	// Read identifier.
	identPos := r.pos
	name, err := r.readIdent()
	if err != nil {
		return err
	}

	// Check root uniqueness.
	if _, dup := r.seen[name]; dup {
		return Wrapf(identPos, ErrDuplicateName, "duplicate root name %q", name)
	}
	r.seen[name] = struct{}{}

	// Read type annotation.
	typ, err := r.readTypeAnnot()
	if err != nil {
		return err
	}

	// Expect '='.
	r.skipWS()
	if err := r.expectByte('='); err != nil {
		return err
	}
	r.skipWS()

	// Emit AssignStart.
	typeStr := typ.String()
	r.emit(EventAssignStart, identPos, name, typeStr, "")

	// Read value.
	if err := r.readValue(typ, name); err != nil {
		return err
	}

	// Emit AssignEnd.
	r.emit(EventAssignEnd, r.pos, name, typeStr, "")

	return nil
}
