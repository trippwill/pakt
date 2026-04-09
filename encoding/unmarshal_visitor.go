package encoding

import (
	"fmt"
	"io"
	"reflect"
)

// unmarshalValue reads the next value from the reader using the given type
// information and writes it directly into target, bypassing Event creation.
func (sm *stateMachine) unmarshalValue(typ Type, target reflect.Value) error {
	sm.r.skipWS()

	// Handle nullable types.
	if typ.Nullable {
		if sm.r.peekNil() {
			return sm.r.readNilInto(target)
		}
	} else if sm.r.peekNil() {
		return sm.r.wrapf(ErrNilNonNullable, "nil value for non-nullable type %s", typ.String())
	}

	switch {
	case typ.Scalar != nil:
		return sm.r.readScalarInto(*typ.Scalar, target)

	case typ.AtomSet != nil:
		return sm.r.readAtomInto(typ.AtomSet.Members, target)

	case typ.Struct != nil:
		return sm.unmarshalStruct(typ.Struct, target)

	case typ.Tuple != nil:
		return sm.unmarshalTuple(typ.Tuple, target)

	case typ.List != nil:
		return sm.unmarshalList(typ.List, target)

	case typ.Map != nil:
		return sm.unmarshalMap(typ.Map, target)

	default:
		return sm.r.errorf("unknown type: no type variant set")
	}
}

// unmarshalStruct reads { value, value, ... } into target using positional
// field matching from the StructType definition.
func (sm *stateMachine) unmarshalStruct(st *StructType, target reflect.Value) error {
	sm.r.skipWS()
	if err := sm.r.expectByte('{'); err != nil {
		return err
	}

	target = allocPtr(target)

	if target.Kind() == reflect.Map {
		return sm.unmarshalStructIntoMap(st, target)
	}

	if target.Kind() != reflect.Struct {
		return fmt.Errorf("cannot unmarshal struct into %s", target.Type())
	}

	info, err := cachedStructFields(target.Type())
	if err != nil {
		return err
	}

	for i, field := range st.Fields {
		if i == 0 {
			sm.r.skipInsignificant(true)
		}

		b, err := sm.r.peekByte()
		if err != nil {
			return sm.r.wrapf(ErrUnexpectedEOF, "unterminated struct value")
		}
		if b == '}' {
			return sm.r.errorf("too few values in struct: expected %d fields, got %d",
				len(st.Fields), i)
		}

		fi, ok := info.fieldMap[field.Name]
		if ok {
			if err := sm.unmarshalValue(field.Type, target.Field(fi.Index)); err != nil {
				return fmt.Errorf("field %q: %w", field.Name, err)
			}
		} else {
			// Skip unknown field — read and discard value.
			if _, _, err := sm.skipTypedValue(field.Type); err != nil {
				return err
			}
		}

		if i < len(st.Fields)-1 {
			sep, err := sm.r.readSep()
			if err != nil {
				return err
			}
			if !sep {
				sm.r.skipWS()
				b, err := sm.r.peekByte()
				if err != nil {
					return sm.r.wrapf(ErrUnexpectedEOF, "unterminated struct value")
				}
				if b == '}' {
					return sm.r.errorf("too few values in struct: expected %d fields, got %d",
						len(st.Fields), i+1)
				}
				return sm.r.errorf("expected separator between struct fields")
			}
		}
	}

	// Consume optional trailing separator and closing brace.
	sm.r.readSep() //nolint:errcheck
	sm.r.skipInsignificant(true)
	return sm.r.expectByte('}')
}

// unmarshalStructIntoMap reads a PAKT struct into a Go map[string]T.
func (sm *stateMachine) unmarshalStructIntoMap(st *StructType, target reflect.Value) error {
	if target.IsNil() {
		target.Set(reflect.MakeMap(target.Type()))
	}
	valType := target.Type().Elem()

	for i, field := range st.Fields {
		if i == 0 {
			sm.r.skipInsignificant(true)
		}

		val := reflect.New(valType).Elem()
		if err := sm.unmarshalValue(field.Type, val); err != nil {
			return fmt.Errorf("map key %q: %w", field.Name, err)
		}
		target.SetMapIndex(reflect.ValueOf(field.Name), val)

		if i < len(st.Fields)-1 {
			sep, err := sm.r.readSep()
			if err != nil {
				return err
			}
			if !sep {
				return sm.r.errorf("expected separator between struct fields")
			}
		}
	}

	sm.r.readSep() //nolint:errcheck
	sm.r.skipInsignificant(true)
	return sm.r.expectByte('}')
}

// unmarshalTuple reads ( value, value, ... ) into target.
func (sm *stateMachine) unmarshalTuple(tt *TupleType, target reflect.Value) error {
	sm.r.skipWS()
	if err := sm.r.expectByte('('); err != nil {
		return err
	}

	target = allocPtr(target)
	if target.Kind() != reflect.Slice {
		return fmt.Errorf("cannot unmarshal tuple into %s", target.Type())
	}

	elemType := target.Type().Elem()
	target.Set(reflect.MakeSlice(target.Type(), 0, len(tt.Elements)))

	for i, elemTyp := range tt.Elements {
		if i == 0 {
			sm.r.skipInsignificant(true)
		}

		target.Grow(1)
		target.SetLen(target.Len() + 1)
		elem := target.Index(target.Len() - 1)
		if elemType.Kind() == reflect.Ptr || elemType.Kind() == reflect.Map || elemType.Kind() == reflect.Slice {
			elem.Set(reflect.New(elemType).Elem())
		}

		if err := sm.unmarshalValue(elemTyp, elem); err != nil {
			return err
		}

		if i < len(tt.Elements)-1 {
			sep, err := sm.r.readSep()
			if err != nil {
				return err
			}
			if !sep {
				return sm.r.errorf("expected separator between tuple elements")
			}
		}
	}

	sm.r.readSep() //nolint:errcheck
	sm.r.skipInsignificant(true)
	return sm.r.expectByte(')')
}

// unmarshalList reads [ value, value, ... ] into target.
func (sm *stateMachine) unmarshalList(lt *ListType, target reflect.Value) error {
	sm.r.skipWS()
	if err := sm.r.expectByte('['); err != nil {
		return err
	}

	target = allocPtr(target)
	if target.Kind() != reflect.Slice {
		return fmt.Errorf("cannot unmarshal list into %s", target.Type())
	}

	elemType := target.Type().Elem()
	target.Set(reflect.MakeSlice(target.Type(), 0, 8))

	sm.r.skipInsignificant(true)
	b, err := sm.r.peekByte()
	if err != nil {
		return sm.r.wrapf(ErrUnexpectedEOF, "unterminated list value")
	}
	if b == ']' {
		sm.r.readByte() //nolint:errcheck
		return nil
	}

	for {
		target.Grow(1)
		target.SetLen(target.Len() + 1)
		elem := target.Index(target.Len() - 1)
		if elemType.Kind() == reflect.Ptr || elemType.Kind() == reflect.Map || elemType.Kind() == reflect.Slice {
			elem.Set(reflect.New(elemType).Elem())
		}

		if err := sm.unmarshalValue(lt.Element, elem); err != nil {
			return err
		}

		sep, err := sm.r.readSep()
		if err != nil {
			return err
		}
		if !sep {
			sm.r.skipWS()
			b, err := sm.r.peekByte()
			if err != nil {
				return sm.r.wrapf(ErrUnexpectedEOF, "unterminated list value")
			}
			if b != ']' {
				return sm.r.errorf("expected ',' or ']' in list, got %q", rune(b))
			}
			sm.r.readByte() //nolint:errcheck
			return nil
		}

		sm.r.skipInsignificant(true)
		if b, err := sm.r.peekByte(); err == nil && b == ']' {
			sm.r.readByte() //nolint:errcheck
			return nil
		}
	}
}

// unmarshalMap reads < key ; value, ... > into target.
func (sm *stateMachine) unmarshalMap(mt *MapType, target reflect.Value) error {
	sm.r.skipWS()
	if err := sm.r.expectByte('<'); err != nil {
		return err
	}

	target = allocPtr(target)
	if target.Kind() != reflect.Map {
		return fmt.Errorf("cannot unmarshal map into %s", target.Type())
	}

	if target.IsNil() {
		target.Set(reflect.MakeMap(target.Type()))
	}

	keyType := target.Type().Key()
	valType := target.Type().Elem()

	sm.r.skipInsignificant(true)
	b, err := sm.r.peekByte()
	if err != nil {
		return sm.r.wrapf(ErrUnexpectedEOF, "unterminated map value")
	}
	if b == '>' {
		sm.r.readByte() //nolint:errcheck
		return nil
	}

	for {
		key := reflect.New(keyType).Elem()
		if err := sm.unmarshalValue(mt.Key, key); err != nil {
			return fmt.Errorf("map key: %w", err)
		}

		sm.r.skipWS()
		if err := sm.r.expectByte(';'); err != nil {
			return err
		}
		sm.r.skipWS()

		val := reflect.New(valType).Elem()
		if err := sm.unmarshalValue(mt.Value, val); err != nil {
			return fmt.Errorf("map value: %w", err)
		}

		target.SetMapIndex(key, val)

		sep, err := sm.r.readSep()
		if err != nil {
			return err
		}
		if !sep {
			sm.r.skipWS()
			b, err := sm.r.peekByte()
			if err != nil {
				return sm.r.wrapf(ErrUnexpectedEOF, "unterminated map value")
			}
			if b != '>' {
				return sm.r.errorf("expected ',' or '>' in map, got %q", rune(b))
			}
			sm.r.readByte() //nolint:errcheck
			return nil
		}

		sm.r.skipInsignificant(true)
		if b, err := sm.r.peekByte(); err == nil && b == '>' {
			sm.r.readByte() //nolint:errcheck
			return nil
		}
	}
}

// unmarshalPackList reads pack list elements (<<) into target.
func (sm *stateMachine) unmarshalPackList(lt *ListType, target reflect.Value) error {
	target = allocPtr(target)
	if target.Kind() != reflect.Slice {
		return fmt.Errorf("cannot unmarshal list pack into %s", target.Type())
	}

	elemType := target.Type().Elem()
	target.Set(reflect.MakeSlice(target.Type(), 0, 64))

	for {
		sm.r.skipInsignificant(true)
		b, err := sm.r.peekByte()
		if err != nil {
			if err == io.EOF {
				return nil
			}
			return err
		}
		if !sm.r.canStartValueInPack(b) {
			return nil
		}

		target.Grow(1)
		target.SetLen(target.Len() + 1)
		elem := target.Index(target.Len() - 1)
		if elemType.Kind() == reflect.Ptr || elemType.Kind() == reflect.Map || elemType.Kind() == reflect.Slice {
			elem.Set(reflect.New(elemType).Elem())
		}

		if err := sm.unmarshalValue(lt.Element, elem); err != nil {
			return err
		}

		sep, err := sm.r.readSep()
		if err != nil {
			return err
		}
		if !sep {
			sm.r.skipInsignificant(true)
			b, err := sm.r.peekByte()
			if err != nil {
				if err == io.EOF {
					return nil
				}
				return err
			}
			if !sm.r.canStartValueInPack(b) {
				return nil
			}
			return sm.r.errorf("expected separator between pack items")
		}
	}
}

// unmarshalPackMap reads pack map entries (<<) into target.
func (sm *stateMachine) unmarshalPackMap(mt *MapType, target reflect.Value) error {
	target = allocPtr(target)
	if target.Kind() != reflect.Map {
		return fmt.Errorf("cannot unmarshal map pack into %s", target.Type())
	}

	if target.IsNil() {
		target.Set(reflect.MakeMap(target.Type()))
	}

	keyType := target.Type().Key()
	valType := target.Type().Elem()

	for {
		sm.r.skipInsignificant(true)
		b, err := sm.r.peekByte()
		if err != nil {
			if err == io.EOF {
				return nil
			}
			return err
		}
		if !sm.r.canStartValueInPack(b) {
			return nil
		}

		key := reflect.New(keyType).Elem()
		if err := sm.unmarshalValue(mt.Key, key); err != nil {
			return fmt.Errorf("pack map key: %w", err)
		}

		sm.r.skipWS()
		if err := sm.r.expectByte(';'); err != nil {
			return err
		}
		sm.r.skipWS()

		val := reflect.New(valType).Elem()
		if err := sm.unmarshalValue(mt.Value, val); err != nil {
			return fmt.Errorf("pack map value: %w", err)
		}

		target.SetMapIndex(key, val)

		sep, err := sm.r.readSep()
		if err != nil {
			return err
		}
		if !sep {
			sm.r.skipInsignificant(true)
			b, err := sm.r.peekByte()
			if err != nil {
				if err == io.EOF {
					return nil
				}
				return err
			}
			if !sm.r.canStartValueInPack(b) {
				return nil
			}
			return sm.r.errorf("expected separator between pack map entries")
		}
	}
}

// skipTypedValue reads and discards a value of the given type.
func (sm *stateMachine) skipTypedValue(typ Type) (string, Pos, error) {
	sm.r.skipWS()

	if typ.Nullable && sm.r.peekNil() {
		pos := sm.r.pos
		if err := sm.r.readNil(); err != nil {
			return "", pos, err
		}
		return "nil", pos, nil
	}

	switch {
	case typ.Scalar != nil:
		return sm.r.readScalarDirect(*typ.Scalar)
	case typ.AtomSet != nil:
		pos := sm.r.pos
		val, err := sm.r.readAtom(typ.AtomSet.Members)
		return val, pos, err
	case typ.Struct != nil:
		return "", sm.r.pos, sm.skipStruct(typ.Struct)
	case typ.Tuple != nil:
		return "", sm.r.pos, sm.skipTuple(typ.Tuple)
	case typ.List != nil:
		return "", sm.r.pos, sm.skipList(typ.List)
	case typ.Map != nil:
		return "", sm.r.pos, sm.skipMap(typ.Map)
	default:
		return "", sm.r.pos, sm.r.errorf("unknown type in skip")
	}
}

func (sm *stateMachine) skipStruct(st *StructType) error {
	sm.r.skipWS()
	if err := sm.r.expectByte('{'); err != nil {
		return err
	}
	for i, field := range st.Fields {
		if i == 0 {
			sm.r.skipInsignificant(true)
		}
		if _, _, err := sm.skipTypedValue(field.Type); err != nil {
			return err
		}
		if i < len(st.Fields)-1 {
			sm.r.readSep() //nolint:errcheck
		}
	}
	sm.r.readSep() //nolint:errcheck
	sm.r.skipInsignificant(true)
	return sm.r.expectByte('}')
}

func (sm *stateMachine) skipTuple(tt *TupleType) error {
	sm.r.skipWS()
	if err := sm.r.expectByte('('); err != nil {
		return err
	}
	for i, elemTyp := range tt.Elements {
		if i == 0 {
			sm.r.skipInsignificant(true)
		}
		if _, _, err := sm.skipTypedValue(elemTyp); err != nil {
			return err
		}
		if i < len(tt.Elements)-1 {
			sm.r.readSep() //nolint:errcheck
		}
	}
	sm.r.readSep() //nolint:errcheck
	sm.r.skipInsignificant(true)
	return sm.r.expectByte(')')
}

func (sm *stateMachine) skipList(lt *ListType) error {
	sm.r.skipWS()
	if err := sm.r.expectByte('['); err != nil {
		return err
	}
	sm.r.skipInsignificant(true)
	if b, err := sm.r.peekByte(); err == nil && b == ']' {
		sm.r.readByte() //nolint:errcheck
		return nil
	}
	for {
		if _, _, err := sm.skipTypedValue(lt.Element); err != nil {
			return err
		}
		sep, err := sm.r.readSep()
		if err != nil {
			return err
		}
		if !sep {
			sm.r.skipWS()
			return sm.r.expectByte(']')
		}
		sm.r.skipInsignificant(true)
		if b, err := sm.r.peekByte(); err == nil && b == ']' {
			sm.r.readByte() //nolint:errcheck
			return nil
		}
	}
}

func (sm *stateMachine) skipMap(mt *MapType) error {
	sm.r.skipWS()
	if err := sm.r.expectByte('<'); err != nil {
		return err
	}
	sm.r.skipInsignificant(true)
	if b, err := sm.r.peekByte(); err == nil && b == '>' {
		sm.r.readByte() //nolint:errcheck
		return nil
	}
	for {
		if _, _, err := sm.skipTypedValue(mt.Key); err != nil {
			return err
		}
		sm.r.skipWS()
		if err := sm.r.expectByte(';'); err != nil {
			return err
		}
		sm.r.skipWS()
		if _, _, err := sm.skipTypedValue(mt.Value); err != nil {
			return err
		}
		sep, err := sm.r.readSep()
		if err != nil {
			return err
		}
		if !sep {
			sm.r.skipWS()
			return sm.r.expectByte('>')
		}
		sm.r.skipInsignificant(true)
		if b, err := sm.r.peekByte(); err == nil && b == '>' {
			sm.r.readByte() //nolint:errcheck
			return nil
		}
	}
}
