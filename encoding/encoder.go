package encoding

import (
	"encoding/hex"
	"fmt"
	"io"
	"reflect"
	"regexp"
	"strconv"
	"strings"
)

// Encoder writes PAKT-formatted data to an output stream.
type Encoder struct {
	w      io.Writer
	indent string // indentation unit (e.g., "  " or "\t"); empty = compact
	level  int    // current nesting depth for pretty printing
	err    error  // sticky error
}

// NewEncoder returns an Encoder that writes to w.
func NewEncoder(w io.Writer) *Encoder {
	return &Encoder{w: w}
}

// SetIndent configures indentation for pretty-printing.
// An empty string produces compact (inline) output.
func (e *Encoder) SetIndent(indent string) {
	e.indent = indent
}

func (e *Encoder) pretty() bool { return e.indent != "" }

func (e *Encoder) write(s string) {
	if e.err != nil {
		return
	}
	_, e.err = io.WriteString(e.w, s)
}

func (e *Encoder) writeIndent() {
	if e.err != nil || !e.pretty() {
		return
	}
	for i := 0; i < e.level; i++ {
		e.write(e.indent)
	}
}

func (e *Encoder) newline() {
	if e.err != nil {
		return
	}
	e.write("\n")
}

// Encode writes a single top-level PAKT assignment: name:type = value.
func (e *Encoder) Encode(name string, typ Type, v any) error {
	if e.err != nil {
		return e.err
	}
	e.write(name)
	e.write(":")
	e.writeType(typ)
	e.write(" = ")
	e.writeValue(typ, v)
	e.newline()
	return e.err
}

func (e *Encoder) writeType(typ Type) {
	if e.err != nil {
		return
	}
	e.write(typ.String())
}

func (e *Encoder) writeValue(typ Type, v any) {
	if e.err != nil {
		return
	}

	// Dereference pointers.
	v = derefPtr(v)

	// Handle nil for nullable types.
	if typ.Nullable && v == nil {
		e.write("nil")
		return
	}

	if v == nil && !typ.Nullable {
		e.err = fmt.Errorf("nil value for non-nullable type %s", typ.String())
		return
	}

	switch {
	case typ.Scalar != nil:
		e.writeScalar(*typ.Scalar, v)
	case typ.AtomSet != nil:
		s, ok := v.(string)
		if !ok {
			e.err = fmt.Errorf("atom set value must be string, got %T", v)
			return
		}
		e.write(s)
	case typ.Struct != nil:
		e.writeStructValue(typ.Struct, v)
	case typ.Tuple != nil:
		e.writeTupleValue(typ.Tuple, v)
	case typ.List != nil:
		e.writeListValue(typ.List, v)
	case typ.Map != nil:
		e.writeMapValue(typ.Map, v)
	default:
		e.err = fmt.Errorf("unknown type: %s", typ.String())
	}
}

func (e *Encoder) writeScalar(kind TypeKind, v any) {
	if e.err != nil {
		return
	}
	switch kind {
	case TypeStr:
		s, ok := v.(string)
		if !ok {
			e.err = fmt.Errorf("str value must be string, got %T", v)
			return
		}
		e.writeString(s)
	case TypeInt:
		switch n := v.(type) {
		case int64:
			e.write(strconv.FormatInt(n, 10))
		case int:
			e.write(strconv.FormatInt(int64(n), 10))
		default:
			e.err = fmt.Errorf("int value must be int64 or int, got %T", v)
		}
	case TypeDec:
		s, ok := v.(string)
		if !ok {
			e.err = fmt.Errorf("dec value must be string, got %T", v)
			return
		}
		e.write(s)
	case TypeFloat:
		f, ok := v.(float64)
		if !ok {
			e.err = fmt.Errorf("float value must be float64, got %T", v)
			return
		}
		e.write(formatFloat(f))
	case TypeBool:
		b, ok := v.(bool)
		if !ok {
			e.err = fmt.Errorf("bool value must be bool, got %T", v)
			return
		}
		if b {
			e.write("true")
		} else {
			e.write("false")
		}
	case TypeUUID:
		s, ok := v.(string)
		if !ok {
			e.err = fmt.Errorf("uuid value must be string, got %T", v)
			return
		}
		if !uuidRe.MatchString(s) {
			e.err = fmt.Errorf("invalid uuid format: %q", s)
			return
		}
		e.write(s)
	case TypeDate:
		s, ok := v.(string)
		if !ok {
			e.err = fmt.Errorf("date value must be string, got %T", v)
			return
		}
		e.write(s)
	case TypeTime:
		s, ok := v.(string)
		if !ok {
			e.err = fmt.Errorf("time value must be string, got %T", v)
			return
		}
		e.write(s)
	case TypeDateTime:
		s, ok := v.(string)
		if !ok {
			e.err = fmt.Errorf("datetime value must be string, got %T", v)
			return
		}
		e.write(s)
	case TypeBin:
		switch data := v.(type) {
		case []byte:
			e.writeBin(data)
		case string:
			e.writeBin([]byte(data))
		default:
			e.err = fmt.Errorf("bin value must be []byte or string, got %T", v)
		}
	default:
		e.err = fmt.Errorf("unknown scalar kind: %d", int(kind))
	}
}

var uuidRe = regexp.MustCompile(`^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$`)

// formatFloat produces a PAKT-valid float literal with mandatory exponent.
func formatFloat(f float64) string {
	s := strconv.FormatFloat(f, 'e', -1, 64)
	// Go uses "e+00" style; PAKT expects 'e' or 'E' with optional sign.
	// strconv already uses 'e', which is fine.
	return s
}

// writeString writes a PAKT string literal with appropriate quoting.
func (e *Encoder) writeString(s string) {
	if e.err != nil {
		return
	}

	// If the string contains newlines, use multi-line triple-quoted form.
	if strings.Contains(s, "\n") {
		e.writeMultiLineString(s)
		return
	}

	// Choose quote character: prefer single, use double if value contains single quotes.
	quote := byte('\'')
	if strings.ContainsRune(s, '\'') && !strings.ContainsRune(s, '"') {
		quote = '"'
	}

	e.write(string(quote))
	for _, r := range s {
		switch r {
		case '\\':
			e.write(`\\`)
		case '\t':
			e.write(`\t`)
		case '\r':
			e.write(`\r`)
		case rune(quote):
			e.write(`\` + string(quote))
		default:
			e.write(string(r))
		}
	}
	e.write(string(quote))
}

func (e *Encoder) writeBin(data []byte) {
	e.write("x'")
	e.write(hex.EncodeToString(data))
	e.write("'")
}

// writeMultiLineString writes a triple-quoted string with indentation stripping.
func (e *Encoder) writeMultiLineString(s string) {
	// Use single-quote triple quotes unless content contains '''.
	quote := "'''"
	if strings.Contains(s, "'''") {
		quote = `"""`
	}

	quoteChar := quote[0]

	e.write(quote)
	e.newline()

	lines := strings.Split(s, "\n")
	for _, line := range lines {
		e.writeIndent()
		// Escape special chars within the line.
		for _, r := range line {
			switch r {
			case '\\':
				e.write(`\\`)
			case '\t':
				e.write(`\t`)
			case '\r':
				e.write(`\r`)
			case rune(quoteChar):
				// Only escape if three in a row could appear — be safe and escape all.
				e.write(`\` + string(rune(quoteChar)))
			default:
				e.write(string(r))
			}
		}
		e.newline()
	}
	e.writeIndent()
	e.write(quote)
}

func (e *Encoder) writeStructValue(st *StructType, v any) {
	if e.err != nil {
		return
	}

	vals, err := structValues(st, v)
	if err != nil {
		e.err = err
		return
	}

	if e.pretty() {
		e.write("{")
		e.newline()
		e.level++
		for i, field := range st.Fields {
			if i >= len(vals) {
				break
			}
			e.writeIndent()
			e.writeValue(field.Type, vals[i])
			e.newline()
		}
		e.level--
		e.writeIndent()
		e.write("}")
	} else {
		e.write("{")
		for i, field := range st.Fields {
			if i > 0 {
				e.write(", ")
			}
			if i >= len(vals) {
				break
			}
			e.writeValue(field.Type, vals[i])
		}
		e.write("}")
	}
}

func (e *Encoder) writeTupleValue(tt *TupleType, v any) {
	if e.err != nil {
		return
	}

	items, ok := toSlice(v)
	if !ok {
		e.err = fmt.Errorf("tuple value must be a slice, got %T", v)
		return
	}
	if len(items) != len(tt.Elements) {
		e.err = fmt.Errorf("tuple has %d elements, got %d values", len(tt.Elements), len(items))
		return
	}

	if e.pretty() {
		e.write("(")
		e.newline()
		e.level++
		for i, elem := range tt.Elements {
			e.writeIndent()
			e.writeValue(elem, items[i])
			e.newline()
		}
		e.level--
		e.writeIndent()
		e.write(")")
	} else {
		e.write("(")
		for i, elem := range tt.Elements {
			if i > 0 {
				e.write(", ")
			}
			e.writeValue(elem, items[i])
		}
		e.write(")")
	}
}

func (e *Encoder) writeListValue(lt *ListType, v any) {
	if e.err != nil {
		return
	}

	items, ok := toSlice(v)
	if !ok {
		e.err = fmt.Errorf("list value must be a slice, got %T", v)
		return
	}

	if len(items) == 0 {
		e.write("[]")
		return
	}

	if e.pretty() {
		e.write("[")
		e.newline()
		e.level++
		for _, item := range items {
			e.writeIndent()
			e.writeValue(lt.Element, item)
			e.newline()
		}
		e.level--
		e.writeIndent()
		e.write("]")
	} else {
		e.write("[")
		for i, item := range items {
			if i > 0 {
				e.write(", ")
			}
			e.writeValue(lt.Element, item)
		}
		e.write("]")
	}
}

func (e *Encoder) writeMapValue(mt *MapType, v any) {
	if e.err != nil {
		return
	}

	entries, err := mapEntries(mt, v)
	if err != nil {
		e.err = err
		return
	}

	if len(entries) == 0 {
		e.write("<>")
		return
	}

	if e.pretty() {
		e.write("<")
		e.newline()
		e.level++
		for _, entry := range entries {
			e.writeIndent()
			e.writeValue(mt.Key, entry.key)
			e.write(" ; ")
			e.writeValue(mt.Value, entry.val)
			e.newline()
		}
		e.level--
		e.writeIndent()
		e.write(">")
	} else {
		e.write("<")
		for i, entry := range entries {
			if i > 0 {
				e.write(", ")
			}
			e.writeValue(mt.Key, entry.key)
			e.write(" ; ")
			e.writeValue(mt.Value, entry.val)
		}
		e.write(">")
	}
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

type mapEntry struct {
	key any
	val any
}

// mapEntries extracts ordered key-value pairs from a map value.
func mapEntries(mt *MapType, v any) ([]mapEntry, error) {
	rv := reflect.ValueOf(v)
	if rv.Kind() != reflect.Map {
		return nil, fmt.Errorf("map value must be a map, got %T", v)
	}
	var entries []mapEntry
	for _, k := range rv.MapKeys() {
		entries = append(entries, mapEntry{
			key: k.Interface(),
			val: rv.MapIndex(k).Interface(),
		})
	}
	return entries, nil
}

// structValues extracts ordered field values from a map or struct.
func structValues(st *StructType, v any) ([]any, error) {
	switch m := v.(type) {
	case map[string]any:
		vals := make([]any, len(st.Fields))
		for i, f := range st.Fields {
			vals[i] = m[f.Name]
		}
		return vals, nil
	default:
		// Try reflection for struct types.
		rv := reflect.ValueOf(v)
		if rv.Kind() == reflect.Ptr {
			rv = rv.Elem()
		}
		if rv.Kind() != reflect.Struct {
			return nil, fmt.Errorf("struct value must be map[string]any or struct, got %T", v)
		}
		vals := make([]any, len(st.Fields))
		rt := rv.Type()
		for i, f := range st.Fields {
			found := false
			for j := 0; j < rt.NumField(); j++ {
				sf := rt.Field(j)
				tag := sf.Tag.Get("pakt")
				name := sf.Name
				if tag != "" {
					name = tag
				}
				if name == f.Name {
					vals[i] = rv.Field(j).Interface()
					found = true
					break
				}
			}
			if !found {
				// Try case-insensitive match.
				for j := 0; j < rt.NumField(); j++ {
					if strings.EqualFold(rt.Field(j).Name, f.Name) {
						vals[i] = rv.Field(j).Interface()
						found = true
						break
					}
				}
			}
			if !found {
				vals[i] = nil
			}
		}
		return vals, nil
	}
}

// toSlice converts a value to []any via type assertion or reflection.
func toSlice(v any) ([]any, bool) {
	if s, ok := v.([]any); ok {
		return s, true
	}
	rv := reflect.ValueOf(v)
	if rv.Kind() != reflect.Slice {
		return nil, false
	}
	result := make([]any, rv.Len())
	for i := 0; i < rv.Len(); i++ {
		result[i] = rv.Index(i).Interface()
	}
	return result, true
}

// derefPtr dereferences a pointer value, returning nil if the pointer is nil.
func derefPtr(v any) any {
	if v == nil {
		return nil
	}
	rv := reflect.ValueOf(v)
	for rv.Kind() == reflect.Ptr {
		if rv.IsNil() {
			return nil
		}
		rv = rv.Elem()
	}
	return rv.Interface()
}
