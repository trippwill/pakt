package encoding

import (
	"fmt"
	"io"
	"maps"
	"reflect"
	"slices"
)

// UnmarshalNew deserializes a complete PAKT unit from bytes into a struct of type T.
// This is convenience sugar over [UnitReader].
//
// T must be a struct type. Each top-level PAKT property is matched to struct
// fields by name (using pakt struct tags or lowercase field names).
func UnmarshalNew[T any](data []byte, opts ...Option) (T, error) {
	var result T
	if err := UnmarshalNewInto(data, &result, opts...); err != nil {
		return result, err
	}
	return result, nil
}

// UnmarshalNewFrom deserializes a complete PAKT unit from a reader into a struct of type T.
func UnmarshalNewFrom[T any](r io.Reader, opts ...Option) (T, error) {
	var result T
	rv := reflect.ValueOf(&result).Elem()
	if rv.Kind() != reflect.Struct {
		return result, fmt.Errorf("pakt: UnmarshalNewFrom requires a struct type, got %s", rv.Type())
	}

	sr := NewUnitReader(r, opts...)
	defer sr.Close()

	if err := unmarshalIntoStruct(sr, rv); err != nil {
		return result, err
	}
	return result, nil
}

// UnmarshalNewInto deserializes a complete PAKT unit from bytes into an existing value.
// Useful when reusing buffers or populating embedded structs.
func UnmarshalNewInto[T any](data []byte, target *T, opts ...Option) error {
	if target == nil {
		return fmt.Errorf("pakt: UnmarshalNewInto requires a non-nil pointer")
	}
	rv := reflect.ValueOf(target).Elem()
	if rv.Kind() != reflect.Struct {
		return fmt.Errorf("pakt: UnmarshalNewInto requires a pointer to a struct, got pointer to %s", rv.Type())
	}

	sr := NewUnitReaderFromBytes(data, opts...)
	defer sr.Close()

	return unmarshalIntoStruct(sr, rv)
}

// unmarshalIntoStruct iterates properties and maps them to struct fields.
func unmarshalIntoStruct(sr *UnitReader, rv reflect.Value) error {
	info, err := cachedStructFields(rv.Type())
	if err != nil {
		return err
	}

	seen := make(map[string]bool)

	for stmt := range sr.Properties() {
		fi, ok := info.fieldMap[stmt.Name]
		if !ok {
			// Apply unknown field policy.
			if sr.opts.unknownFields == ErrorUnknown {
				return &DeserializeError{
					Pos:      stmt.Pos,
					Property: stmt.Name,
					Message:  fmt.Sprintf("unknown property %q", stmt.Name),
				}
			}
			continue // auto-skipped by Properties iterator
		}

		// Handle duplicates.
		if seen[stmt.Name] {
			switch sr.opts.duplicates {
			case ErrorDupes:
				return &DeserializeError{
					Pos:      stmt.Pos,
					Property: stmt.Name,
					Message:  fmt.Sprintf("duplicate property %q", stmt.Name),
				}
			case FirstWins:
				continue // skip, auto-skipped by iterator
			case LastWins:
				// fall through — overwrite
			case Accumulate:
				return &DeserializeError{
					Pos:      stmt.Pos,
					Property: stmt.Name,
					Message:  "Accumulate duplicate policy is not yet implemented",
				}
			}
		}
		seen[stmt.Name] = true

		target := rv.Field(fi.Index)
		if stmt.IsPack {
			// For pack properties, collect all elements into the target.
			if err := unmarshalPackIntoTarget(sr, stmt, target); err != nil {
				return err
			}
		} else {
			if err := readValueReflect(sr, target); err != nil {
				return fmt.Errorf("pakt: field %q: %w", stmt.Name, err)
			}
		}
	}

	if err := sr.Err(); err != nil {
		return err
	}

	// Check missing fields.
	if sr.opts.missingFields == ErrorMissing {
		for _, name := range slices.Sorted(maps.Keys(info.fieldMap)) {
			if !seen[name] {
				return &DeserializeError{
					Field:   name,
					Message: fmt.Sprintf("missing property for field %q", name),
				}
			}
		}
	}

	return nil
}

// unmarshalPackIntoTarget reads all pack elements into a slice or map field.
func unmarshalPackIntoTarget(sr *UnitReader, stmt Property, target reflect.Value) error {
	target = allocPtr(target)

	switch target.Kind() {
	case reflect.Slice:
		elemType := target.Type().Elem()
		target.Set(reflect.MakeSlice(target.Type(), 0, 64))

		endKind := sr.endKindForCurrent()
		for {
			ev, err := sr.dec.Decode()
			if err != nil {
				if err == io.EOF {
					sr.current = nil
					return nil
				}
				return err
			}
			if ev.Kind == endKind {
				sr.current = nil
				return nil
			}

			target.Grow(1)
			target.SetLen(target.Len() + 1)
			elem := target.Index(target.Len() - 1)
			if elemType.Kind() == reflect.Ptr || elemType.Kind() == reflect.Map || elemType.Kind() == reflect.Slice {
				elem.Set(reflect.New(elemType).Elem())
			}
			elem = allocPtr(elem)
			if err := handleValueEvent(sr, ev, elem); err != nil {
				return fmt.Errorf("pakt: field %q: %w", stmt.Name, err)
			}
		}

	case reflect.Map:
		if target.IsNil() {
			target.Set(reflect.MakeMap(target.Type()))
		}
		keyType := target.Type().Key()
		valType := target.Type().Elem()

		endKind := sr.endKindForCurrent()
		for {
			// Read key
			keyEv, err := sr.dec.Decode()
			if err != nil {
				if err == io.EOF {
					sr.current = nil
					return nil
				}
				return err
			}
			if keyEv.Kind == endKind {
				sr.current = nil
				return nil
			}

			key := reflect.New(keyType).Elem()
			if err := handleValueEvent(sr, keyEv, key); err != nil {
				return fmt.Errorf("pakt: field %q key: %w", stmt.Name, err)
			}

			// Read value
			valEv, err := sr.dec.Decode()
			if err != nil {
				return fmt.Errorf("pakt: field %q value: %w", stmt.Name, err)
			}
			val := reflect.New(valType).Elem()
			if err := handleValueEvent(sr, valEv, val); err != nil {
				return fmt.Errorf("pakt: field %q value: %w", stmt.Name, err)
			}

			target.SetMapIndex(key, val)
		}

	default:
		return fmt.Errorf("pakt: field %q: cannot unmarshal pack into %s (need slice or map)", stmt.Name, target.Type())
	}
}
