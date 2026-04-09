package encoding

import (
	"bytes"
	"encoding"
	"fmt"
	"reflect"
	"time"
)

// Marshal returns the PAKT encoding of v as a top-level assignment.
// The name parameter is used as the assignment name.
// v's type is inspected via reflection and struct tags to determine the PAKT type.
func Marshal(name string, v any) ([]byte, error) {
	return marshal(name, v, "")
}

// MarshalIndent is like Marshal but applies indentation for readability.
func MarshalIndent(name string, v any, indent string) ([]byte, error) {
	return marshal(name, v, indent)
}

func marshal(name string, v any, indent string) ([]byte, error) {
	if v == nil {
		return nil, fmt.Errorf("pakt: Marshal requires a non-nil value")
	}

	typ, err := TypeOf(v)
	if err != nil {
		return nil, err
	}

	prepared, err := prepareValue(typ, reflect.ValueOf(v))
	if err != nil {
		return nil, err
	}

	var buf bytes.Buffer
	enc := NewEncoder(&buf)
	if indent != "" {
		enc.SetIndent(indent)
	}
	if err := enc.Encode(name, typ, prepared); err != nil {
		return nil, err
	}
	return buf.Bytes(), nil
}

// prepareValue converts a Go value to the format expected by Encoder.Encode,
// using struct tag info to map fields.
func prepareValue(typ Type, v reflect.Value) (any, error) {
	// Dereference pointers.
	for v.Kind() == reflect.Pointer || v.Kind() == reflect.Interface {
		if v.IsNil() {
			return nil, nil
		}
		v = v.Elem()
	}

	// Handle time.Time → RFC3339 string for ts.
	if v.Type() == timeType {
		t := v.Interface().(time.Time)
		return t.Format(time.RFC3339), nil
	}

	// Handle encoding.TextMarshaler → string.
	if v.Type().Implements(textMarshalerType) {
		tm := v.Interface().(encoding.TextMarshaler)
		b, err := tm.MarshalText()
		if err != nil {
			return nil, fmt.Errorf("pakt: MarshalText: %w", err)
		}
		return string(b), nil
	}
	if v.CanAddr() && v.Addr().Type().Implements(textMarshalerType) {
		tm := v.Addr().Interface().(encoding.TextMarshaler)
		b, err := tm.MarshalText()
		if err != nil {
			return nil, fmt.Errorf("pakt: MarshalText: %w", err)
		}
		return string(b), nil
	}

	// []byte → binary data.
	if v.Type() == byteSliceType {
		return bytes.Clone(v.Bytes()), nil
	}

	switch v.Kind() {
	case reflect.String:
		return v.String(), nil

	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		return v.Int(), nil

	case reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
		return int64(v.Uint()), nil

	case reflect.Float32, reflect.Float64:
		return v.Float(), nil

	case reflect.Bool:
		return v.Bool(), nil

	case reflect.Slice:
		return prepareSlice(typ, v)

	case reflect.Array:
		return prepareSlice(typ, v)

	case reflect.Map:
		return prepareMap(typ, v)

	case reflect.Struct:
		return prepareStruct(typ, v)

	default:
		return nil, fmt.Errorf("pakt: unsupported value kind %s", v.Kind())
	}
}

func prepareStruct(typ Type, v reflect.Value) (map[string]any, error) {
	if typ.Struct == nil {
		return nil, fmt.Errorf("pakt: expected struct type, got %s", typ.String())
	}

	fields, err := StructFields(v.Type())
	if err != nil {
		return nil, err
	}

	result := make(map[string]any, len(fields))
	for _, fi := range fields {
		fv := v.Field(fi.Index)

		if fi.OmitEmpty && isZero(fv) {
			continue
		}

		prepared, err := prepareValue(fi.Type, fv)
		if err != nil {
			return nil, fmt.Errorf("pakt: field %s: %w", fi.Name, err)
		}
		result[fi.Name] = prepared
	}

	// Rebuild the PAKT struct type to only include non-omitted fields.
	newFields := make([]Field, 0, len(result))
	for _, f := range typ.Struct.Fields {
		if _, ok := result[f.Name]; ok {
			newFields = append(newFields, f)
		}
	}
	typ.Struct.Fields = newFields

	return result, nil
}

func prepareSlice(typ Type, v reflect.Value) ([]any, error) {
	if typ.List == nil {
		return nil, fmt.Errorf("pakt: expected list type for slice value, got %s", typ.String())
	}

	result := make([]any, v.Len())
	for i := 0; i < v.Len(); i++ {
		prepared, err := prepareValue(typ.List.Element, v.Index(i))
		if err != nil {
			return nil, fmt.Errorf("pakt: list index %d: %w", i, err)
		}
		result[i] = prepared
	}
	return result, nil
}

func prepareMap(typ Type, v reflect.Value) (map[any]any, error) {
	if typ.Map == nil {
		return nil, fmt.Errorf("pakt: expected map type for map value, got %s", typ.String())
	}

	result := make(map[any]any, v.Len())
	for _, key := range v.MapKeys() {
		pk, err := prepareValue(typ.Map.Key, key)
		if err != nil {
			return nil, fmt.Errorf("pakt: map key: %w", err)
		}
		pv, err := prepareValue(typ.Map.Value, v.MapIndex(key))
		if err != nil {
			return nil, fmt.Errorf("pakt: map value: %w", err)
		}
		result[pk] = pv
	}
	return result, nil
}

// isZero reports whether v is the zero value for its type.
func isZero(v reflect.Value) bool {
	switch v.Kind() {
	case reflect.Pointer, reflect.Interface:
		return v.IsNil()
	case reflect.Slice, reflect.Map:
		return v.IsNil() || v.Len() == 0
	default:
		return v.IsZero()
	}
}
