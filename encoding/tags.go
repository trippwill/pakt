package encoding

import (
	"encoding"
	"fmt"
	"reflect"
	"strings"
	"sync"
	"time"
)

var (
	timeType          = reflect.TypeFor[time.Time]()
	textMarshalerType = reflect.TypeFor[encoding.TextMarshaler]()
	byteSliceType     = reflect.TypeFor[[]byte]()
)

// cachedStructInfo holds precomputed struct field metadata.
type cachedStructInfo struct {
	fields   []FieldInfo
	fieldMap map[string]FieldInfo
}

var structInfoCache sync.Map // reflect.Type → *cachedStructInfo

// FieldInfo describes a Go struct field's PAKT mapping.
type FieldInfo struct {
	Index     int    // field index in the Go struct
	Name      string // PAKT field name
	OmitEmpty bool   // whether to omit when zero
	Type      Type   // resolved PAKT type
}

// TypeOf returns the PAKT Type for a Go value, using struct tags for struct fields.
func TypeOf(v any) (Type, error) {
	if v == nil {
		return Type{}, fmt.Errorf("pakt: cannot determine type of nil")
	}
	t := reflect.TypeOf(v)
	return typeOfReflect(t, nil)
}

// typeOfReflect is the internal recursive implementation.
// seen tracks visited struct types to prevent infinite recursion.
func typeOfReflect(t reflect.Type, seen map[reflect.Type]bool) (Type, error) {
	if seen == nil {
		seen = make(map[reflect.Type]bool)
	}

	// Unwrap pointer: *T → T? (nullable)
	if t.Kind() == reflect.Pointer {
		inner, err := typeOfReflect(t.Elem(), seen)
		if err != nil {
			return Type{}, err
		}
		inner.Nullable = true
		return inner, nil
	}

	// Check for time.Time before other struct handling.
	if t == timeType {
		k := TypeTs
		return Type{Scalar: &k}, nil
	}

	// Check for encoding.TextMarshaler (on the type itself, not pointer).
	if t.Implements(textMarshalerType) || reflect.PointerTo(t).Implements(textMarshalerType) {
		k := TypeStr
		return Type{Scalar: &k}, nil
	}

	// []byte → bin
	if t == byteSliceType {
		k := TypeBin
		return Type{Scalar: &k}, nil
	}

	switch t.Kind() {
	case reflect.String:
		k := TypeStr
		return Type{Scalar: &k}, nil

	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		k := TypeInt
		return Type{Scalar: &k}, nil

	case reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
		k := TypeInt
		return Type{Scalar: &k}, nil

	case reflect.Float32, reflect.Float64:
		k := TypeFloat
		return Type{Scalar: &k}, nil

	case reflect.Bool:
		k := TypeBool
		return Type{Scalar: &k}, nil

	case reflect.Slice:
		elemType, err := typeOfReflect(t.Elem(), seen)
		if err != nil {
			return Type{}, fmt.Errorf("pakt: slice element: %w", err)
		}
		return Type{List: &ListType{Element: elemType}}, nil

	case reflect.Array:
		elemType, err := typeOfReflect(t.Elem(), seen)
		if err != nil {
			return Type{}, fmt.Errorf("pakt: array element: %w", err)
		}
		return Type{List: &ListType{Element: elemType}}, nil

	case reflect.Map:
		keyType, err := typeOfReflect(t.Key(), seen)
		if err != nil {
			return Type{}, fmt.Errorf("pakt: map key: %w", err)
		}
		valType, err := typeOfReflect(t.Elem(), seen)
		if err != nil {
			return Type{}, fmt.Errorf("pakt: map value: %w", err)
		}
		return Type{Map: &MapType{Key: keyType, Value: valType}}, nil

	case reflect.Struct:
		if seen[t] {
			return Type{}, fmt.Errorf("pakt: recursive type %s", t.Name())
		}
		seen[t] = true
		defer delete(seen, t)

		fields, err := structFieldsImpl(t, seen)
		if err != nil {
			return Type{}, err
		}
		paktFields := make([]Field, len(fields))
		for i, fi := range fields {
			paktFields[i] = Field{Name: fi.Name, Type: fi.Type}
		}
		return Type{Struct: &StructType{Fields: paktFields}}, nil

	case reflect.Interface:
		return Type{}, fmt.Errorf("pakt: cannot determine PAKT type for interface type %s", t.Name())

	default:
		return Type{}, fmt.Errorf("pakt: unsupported Go type %s (kind: %s)", t.Name(), t.Kind())
	}
}

// StructFields returns the PAKT field mapping for a Go struct type.
// t must be a struct type (or pointer to struct); otherwise an error is returned.
// Results are cached per type for subsequent calls.
func StructFields(t reflect.Type) ([]FieldInfo, error) {
	for t.Kind() == reflect.Pointer {
		t = t.Elem()
	}
	if t.Kind() != reflect.Struct {
		return nil, fmt.Errorf("pakt: StructFields requires struct type, got %s", t.Kind())
	}
	info, err := cachedStructFields(t)
	if err != nil {
		return nil, err
	}
	return info.fields, nil
}

// cachedStructFields returns the cached struct info, computing it on first access.
func cachedStructFields(t reflect.Type) (*cachedStructInfo, error) {
	if v, ok := structInfoCache.Load(t); ok {
		return v.(*cachedStructInfo), nil
	}
	fields, err := structFieldsImpl(t, nil)
	if err != nil {
		return nil, err
	}
	info := &cachedStructInfo{
		fields:   fields,
		fieldMap: make(map[string]FieldInfo, len(fields)),
	}
	for _, fi := range fields {
		info.fieldMap[fi.Name] = fi
	}
	actual, _ := structInfoCache.LoadOrStore(t, info)
	return actual.(*cachedStructInfo), nil
}

// structFieldsImpl collects exported struct fields, flattening embedded structs.
func structFieldsImpl(t reflect.Type, seen map[reflect.Type]bool) ([]FieldInfo, error) {
	if seen == nil {
		seen = make(map[reflect.Type]bool)
	}
	var result []FieldInfo
	for i := 0; i < t.NumField(); i++ {
		sf := t.Field(i)

		// Skip unexported fields (unless embedded).
		if !sf.IsExported() {
			continue
		}

		tag := sf.Tag.Get("pakt")
		name, omitEmpty, skip := parseTag(tag)
		if skip {
			continue
		}

		// Handle embedded (anonymous) structs: flatten their fields.
		if sf.Anonymous {
			ft := sf.Type
			if ft.Kind() == reflect.Pointer {
				ft = ft.Elem()
			}
			if ft.Kind() == reflect.Struct && ft != timeType {
				// Check for TextMarshaler — if the embedded type implements it,
				// treat as a field, not flattened.
				if ft.Implements(textMarshalerType) || reflect.PointerTo(ft).Implements(textMarshalerType) {
					// Fall through to normal field handling below.
				} else {
					embedded, err := structFieldsImpl(ft, seen)
					if err != nil {
						return nil, err
					}
					result = append(result, embedded...)
					continue
				}
			}
		}

		if name == "" {
			name = strings.ToLower(sf.Name)
		}

		fieldType, err := typeOfReflect(sf.Type, seen)
		if err != nil {
			return nil, fmt.Errorf("pakt: field %s: %w", sf.Name, err)
		}

		result = append(result, FieldInfo{
			Index:     i,
			Name:      name,
			OmitEmpty: omitEmpty,
			Type:      fieldType,
		})
	}
	return result, nil
}

// parseTag parses a `pakt:"..."` struct tag value.
// Returns the field name (empty means use default), omitEmpty flag, and skip flag.
func parseTag(tag string) (name string, omitEmpty bool, skip bool) {
	if tag == "" {
		return "", false, false
	}
	if tag == "-" {
		return "", false, true
	}

	parts := strings.Split(tag, ",")
	name = parts[0]
	for _, opt := range parts[1:] {
		if strings.TrimSpace(opt) == "omitempty" {
			omitEmpty = true
		}
	}
	return name, omitEmpty, false
}
