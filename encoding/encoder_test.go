package encoding

import (
	"bytes"
	"io"
	"strings"
	"testing"
)

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

func encodeCompact(t *testing.T, name string, typ Type, v any) string {
	t.Helper()
	var buf bytes.Buffer
	enc := NewEncoder(&buf)
	if err := enc.Encode(name, typ, v); err != nil {
		t.Fatalf("Encode(%q): %v", name, err)
	}
	return buf.String()
}

func encodePretty(t *testing.T, name string, typ Type, v any, indent string) string {
	t.Helper()
	var buf bytes.Buffer
	enc := NewEncoder(&buf)
	enc.SetIndent(indent)
	if err := enc.Encode(name, typ, v); err != nil {
		t.Fatalf("Encode(%q): %v", name, err)
	}
	return buf.String()
}

// roundTrip encodes a value then decodes it, returning all events.
func roundTrip(t *testing.T, name string, typ Type, v any) []Event {
	t.Helper()
	var buf bytes.Buffer
	enc := NewEncoder(&buf)
	if err := enc.Encode(name, typ, v); err != nil {
		t.Fatalf("Encode: %v", err)
	}

	dec := NewDecoder(&buf)
	var events []Event
	for {
		ev, err := dec.Decode()
		if err == io.EOF {
			break
		}
		if err != nil {
			t.Fatalf("Decode failed on input %q: %v", buf.String(), err)
		}
		events = append(events, ev)
	}
	return events
}

// ---------------------------------------------------------------------------
// 1. Scalar encoding — all 10 scalar types
// ---------------------------------------------------------------------------

func TestEncodeStr(t *testing.T) {
	got := encodeCompact(t, "x", scalarType(TypeStr), "hello")
	want := "x:str = 'hello'\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeInt(t *testing.T) {
	got := encodeCompact(t, "n", scalarType(TypeInt), int64(42))
	want := "n:int = 42\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeIntNegative(t *testing.T) {
	got := encodeCompact(t, "n", scalarType(TypeInt), int64(-7))
	want := "n:int = -7\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeIntFromPlainInt(t *testing.T) {
	got := encodeCompact(t, "n", scalarType(TypeInt), 100)
	want := "n:int = 100\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeDec(t *testing.T) {
	got := encodeCompact(t, "d", scalarType(TypeDec), "3.14")
	want := "d:dec = 3.14\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeFloat(t *testing.T) {
	got := encodeCompact(t, "f", scalarType(TypeFloat), 6.022e23)
	if !strings.HasPrefix(got, "f:float = ") {
		t.Errorf("unexpected prefix: %q", got)
	}
	// Must contain an exponent marker.
	if !strings.Contains(got, "e") && !strings.Contains(got, "E") {
		t.Errorf("float output lacks exponent: %q", got)
	}
}

func TestEncodeBoolTrue(t *testing.T) {
	got := encodeCompact(t, "b", scalarType(TypeBool), true)
	want := "b:bool = true\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeBoolFalse(t *testing.T) {
	got := encodeCompact(t, "b", scalarType(TypeBool), false)
	want := "b:bool = false\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeUUID(t *testing.T) {
	got := encodeCompact(t, "id", scalarType(TypeUUID), "550e8400-e29b-41d4-a716-446655440000")
	want := "id:uuid = 550e8400-e29b-41d4-a716-446655440000\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeDate(t *testing.T) {
	got := encodeCompact(t, "d", scalarType(TypeDate), "2026-06-01")
	want := "d:date = 2026-06-01\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeTs(t *testing.T) {
	got := encodeCompact(t, "dt", scalarType(TypeTs), "2026-06-01T14:30:00Z")
	want := "dt:ts = 2026-06-01T14:30:00Z\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeBin(t *testing.T) {
	got := encodeCompact(t, "payload", scalarType(TypeBin), []byte("Hello"))
	want := "payload:bin = x'48656c6c6f'\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

// ---------------------------------------------------------------------------
// 2. String encoding — escapes, quoting, multi-line
// ---------------------------------------------------------------------------

func TestEncodeStrSingleQuote(t *testing.T) {
	got := encodeCompact(t, "s", scalarType(TypeStr), "hello world")
	want := "s:str = 'hello world'\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeStrWithSingleQuote(t *testing.T) {
	// String containing single quotes should use double quotes.
	got := encodeCompact(t, "s", scalarType(TypeStr), "it's fine")
	want := "s:str = \"it's fine\"\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeStrWithEscapes(t *testing.T) {
	got := encodeCompact(t, "s", scalarType(TypeStr), "line1\tline2")
	want := "s:str = 'line1\\tline2'\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeStrWithBackslash(t *testing.T) {
	got := encodeCompact(t, "s", scalarType(TypeStr), `a\b`)
	want := "s:str = 'a\\\\b'\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeStrMultiLine(t *testing.T) {
	got := encodeCompact(t, "s", scalarType(TypeStr), "line one\nline two")
	// Should use triple-quote form.
	if !strings.Contains(got, "'''") && !strings.Contains(got, `"""`) {
		t.Errorf("expected triple-quoted string, got %q", got)
	}
}

// ---------------------------------------------------------------------------
// 3. Struct encoding — inline and pretty
// ---------------------------------------------------------------------------

func makeStructType(fields ...Field) Type {
	return Type{Struct: &StructType{Fields: fields}}
}

func TestEncodeStructInline(t *testing.T) {
	typ := makeStructType(
		Field{Name: "host", Type: scalarType(TypeStr)},
		Field{Name: "port", Type: scalarType(TypeInt)},
	)
	v := map[string]any{"host": "localhost", "port": int64(8080)}
	got := encodeCompact(t, "srv", typ, v)
	want := "srv:{host:str, port:int} = {'localhost', 8080}\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeStructPretty(t *testing.T) {
	typ := makeStructType(
		Field{Name: "host", Type: scalarType(TypeStr)},
		Field{Name: "port", Type: scalarType(TypeInt)},
	)
	v := map[string]any{"host": "localhost", "port": int64(8080)}
	got := encodePretty(t, "srv", typ, v, "  ")
	want := "srv:{host:str, port:int} = {\n  'localhost'\n  8080\n}\n"
	if got != want {
		t.Errorf("got:\n%s\nwant:\n%s", got, want)
	}
}

func TestEncodeStructSingleField(t *testing.T) {
	typ := makeStructType(Field{Name: "name", Type: scalarType(TypeStr)})
	v := map[string]any{"name": "test"}
	got := encodeCompact(t, "s", typ, v)
	want := "s:{name:str} = {'test'}\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

// ---------------------------------------------------------------------------
// 4. Tuple encoding — inline and pretty
// ---------------------------------------------------------------------------

func makeTupleType(elems ...Type) Type {
	return Type{Tuple: &TupleType{Elements: elems}}
}

func TestEncodeTupleInline(t *testing.T) {
	typ := makeTupleType(scalarType(TypeInt), scalarType(TypeInt), scalarType(TypeInt))
	v := []any{int64(1), int64(0), int64(0)}
	got := encodeCompact(t, "ver", typ, v)
	want := "ver:(int, int, int) = (1, 0, 0)\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeTuplePretty(t *testing.T) {
	typ := makeTupleType(scalarType(TypeInt), scalarType(TypeInt))
	v := []any{int64(3), int64(45)}
	got := encodePretty(t, "pair", typ, v, "  ")
	want := "pair:(int, int) = (\n  3\n  45\n)\n"
	if got != want {
		t.Errorf("got:\n%s\nwant:\n%s", got, want)
	}
}

// ---------------------------------------------------------------------------
// 5. List encoding — inline, pretty, empty
// ---------------------------------------------------------------------------

func makeListType(elem Type) Type {
	return Type{List: &ListType{Element: elem}}
}

func TestEncodeListInline(t *testing.T) {
	typ := makeListType(scalarType(TypeInt))
	v := []any{int64(1), int64(2), int64(3)}
	got := encodeCompact(t, "nums", typ, v)
	want := "nums:[int] = [1, 2, 3]\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeListPretty(t *testing.T) {
	typ := makeListType(scalarType(TypeStr))
	v := []any{"alpha", "bravo"}
	got := encodePretty(t, "tags", typ, v, "  ")
	want := "tags:[str] = [\n  'alpha'\n  'bravo'\n]\n"
	if got != want {
		t.Errorf("got:\n%s\nwant:\n%s", got, want)
	}
}

func TestEncodeListEmpty(t *testing.T) {
	typ := makeListType(scalarType(TypeInt))
	v := []any{}
	got := encodeCompact(t, "empty", typ, v)
	want := "empty:[int] = []\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

// ---------------------------------------------------------------------------
// 6. Map encoding — inline, pretty, empty
// ---------------------------------------------------------------------------

func makeMapType(key, val Type) Type {
	return Type{Map: &MapType{Key: key, Value: val}}
}

func TestEncodeMapInline(t *testing.T) {
	typ := makeMapType(scalarType(TypeStr), scalarType(TypeInt))
	v := map[any]any{"a": int64(1)}
	got := encodeCompact(t, "m", typ, v)
	want := "m:<str ; int> = <'a' ; 1>\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeMapPretty(t *testing.T) {
	typ := makeMapType(scalarType(TypeStr), scalarType(TypeInt))
	v := map[any]any{"x": int64(10)}
	got := encodePretty(t, "m", typ, v, "  ")
	want := "m:<str ; int> = <\n  'x' ; 10\n>\n"
	if got != want {
		t.Errorf("got:\n%s\nwant:\n%s", got, want)
	}
}

func TestEncodeMapEmpty(t *testing.T) {
	typ := makeMapType(scalarType(TypeStr), scalarType(TypeInt))
	v := map[any]any{}
	got := encodeCompact(t, "m", typ, v)
	want := "m:<str ; int> = <>\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

// ---------------------------------------------------------------------------
// 7. Nullable — nil and non-nil
// ---------------------------------------------------------------------------

func TestEncodeNullableNil(t *testing.T) {
	got := encodeCompact(t, "x", nullableScalar(TypeStr), nil)
	want := "x:str? = nil\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeNullableNonNil(t *testing.T) {
	got := encodeCompact(t, "x", nullableScalar(TypeStr), "hello")
	want := "x:str? = 'hello'\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeNullablePointerNil(t *testing.T) {
	var p *string
	got := encodeCompact(t, "x", nullableScalar(TypeStr), p)
	want := "x:str? = nil\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeNullablePointerNonNil(t *testing.T) {
	s := "world"
	got := encodeCompact(t, "x", nullableScalar(TypeStr), &s)
	want := "x:str? = 'world'\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

// ---------------------------------------------------------------------------
// 8. Nested composites
// ---------------------------------------------------------------------------

func TestEncodeNestedStructWithList(t *testing.T) {
	inner := makeListType(scalarType(TypeInt))
	typ := makeStructType(
		Field{Name: "name", Type: scalarType(TypeStr)},
		Field{Name: "values", Type: inner},
	)
	v := map[string]any{
		"name":   "test",
		"values": []any{int64(1), int64(2)},
	}
	got := encodeCompact(t, "data", typ, v)
	want := "data:{name:str, values:[int]} = {'test', [1, 2]}\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeNestedStructWithMap(t *testing.T) {
	inner := makeMapType(scalarType(TypeStr), scalarType(TypeInt))
	typ := makeStructType(
		Field{Name: "label", Type: scalarType(TypeStr)},
		Field{Name: "counts", Type: inner},
	)
	v := map[string]any{
		"label":  "stats",
		"counts": map[any]any{"hits": int64(42)},
	}
	got := encodeCompact(t, "s", typ, v)
	want := "s:{label:str, counts:<str ; int>} = {'stats', <'hits' ; 42>}\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestEncodeListOfStructs(t *testing.T) {
	st := makeStructType(
		Field{Name: "x", Type: scalarType(TypeInt)},
		Field{Name: "y", Type: scalarType(TypeInt)},
	)
	typ := Type{List: &ListType{Element: st}}
	v := []any{
		map[string]any{"x": int64(1), "y": int64(2)},
		map[string]any{"x": int64(3), "y": int64(4)},
	}
	got := encodeCompact(t, "pts", typ, v)
	want := "pts:[{x:int, y:int}] = [{1, 2}, {3, 4}]\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

// ---------------------------------------------------------------------------
// 9. Round-trip tests — Encode → Decode
// ---------------------------------------------------------------------------

func TestRoundTripStr(t *testing.T) {
	events := roundTrip(t, "name", scalarType(TypeStr), "hello")
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d", len(events))
	}
	if events[0].Kind != EventAssignStart || events[0].Name != "name" {
		t.Errorf("event[0] = %v", events[0])
	}
	if events[1].Kind != EventScalarValue || events[1].Value != "hello" {
		t.Errorf("event[1] = %v", events[1])
	}
	if events[2].Kind != EventAssignEnd {
		t.Errorf("event[2] = %v", events[2])
	}
}

func TestRoundTripInt(t *testing.T) {
	events := roundTrip(t, "n", scalarType(TypeInt), int64(-42))
	if events[1].Value != "-42" {
		t.Errorf("got value %q, want %q", events[1].Value, "-42")
	}
}

func TestRoundTripBool(t *testing.T) {
	events := roundTrip(t, "b", scalarType(TypeBool), true)
	if events[1].Value != "true" {
		t.Errorf("got value %q, want %q", events[1].Value, "true")
	}
}

func TestRoundTripDec(t *testing.T) {
	events := roundTrip(t, "d", scalarType(TypeDec), "1000.50")
	if events[1].Value != "1000.50" {
		t.Errorf("got value %q, want %q", events[1].Value, "1000.50")
	}
}

func TestRoundTripFloat(t *testing.T) {
	events := roundTrip(t, "f", scalarType(TypeFloat), 1.5e-10)
	// The decoded value should parse as a float.
	if events[1].Kind != EventScalarValue {
		t.Errorf("expected ScalarValue, got %v", events[1].Kind)
	}
}

func TestRoundTripUUID(t *testing.T) {
	uuid := "550e8400-e29b-41d4-a716-446655440000"
	events := roundTrip(t, "id", scalarType(TypeUUID), uuid)
	if events[1].Value != uuid {
		t.Errorf("got value %q, want %q", events[1].Value, uuid)
	}
}

func TestRoundTripDate(t *testing.T) {
	events := roundTrip(t, "d", scalarType(TypeDate), "2026-06-01")
	if events[1].Value != "2026-06-01" {
		t.Errorf("got value %q, want %q", events[1].Value, "2026-06-01")
	}
}

func TestRoundTripTs(t *testing.T) {
	events := roundTrip(t, "dt", scalarType(TypeTs), "2026-06-01T14:30:00Z")
	if events[1].Value != "2026-06-01T14:30:00Z" {
		t.Errorf("got value %q, want %q", events[1].Value, "2026-06-01T14:30:00Z")
	}
}

func TestRoundTripNullable(t *testing.T) {
	events := roundTrip(t, "x", nullableScalar(TypeInt), nil)
	if events[1].Value != "nil" {
		t.Errorf("got value %q, want %q", events[1].Value, "nil")
	}
}

func TestRoundTripAtomSet(t *testing.T) {
	typ := Type{AtomSet: &AtomSet{Members: []string{"dev", "staging", "prod"}}}
	events := roundTrip(t, "env", typ, "prod")
	if events[1].Value != "prod" {
		t.Errorf("got value %q, want %q", events[1].Value, "prod")
	}
}

func TestRoundTripStruct(t *testing.T) {
	typ := makeStructType(
		Field{Name: "host", Type: scalarType(TypeStr)},
		Field{Name: "port", Type: scalarType(TypeInt)},
	)
	v := map[string]any{"host": "localhost", "port": int64(8080)}
	events := roundTrip(t, "srv", typ, v)

	// AssignStart, CompositeStart, host scalar, port scalar, CompositeEnd, AssignEnd
	if len(events) != 6 {
		t.Fatalf("expected 6 events, got %d: %v", len(events), events)
	}
	if events[2].Kind != EventScalarValue || events[2].Value != "localhost" {
		t.Errorf("host event = %v", events[2])
	}
	if events[3].Kind != EventScalarValue || events[3].Value != "8080" {
		t.Errorf("port event = %v", events[3])
	}
}

func TestRoundTripTuple(t *testing.T) {
	typ := makeTupleType(scalarType(TypeInt), scalarType(TypeInt), scalarType(TypeInt))
	v := []any{int64(1), int64(0), int64(0)}
	events := roundTrip(t, "ver", typ, v)

	// AssignStart, CompositeStart, 3×ScalarValue, CompositeEnd, AssignEnd
	if len(events) != 7 {
		t.Fatalf("expected 7 events, got %d: %v", len(events), events)
	}
	if events[2].Value != "1" || events[3].Value != "0" || events[4].Value != "0" {
		t.Errorf("values: %q %q %q", events[2].Value, events[3].Value, events[4].Value)
	}
}

func TestRoundTripList(t *testing.T) {
	typ := makeListType(scalarType(TypeStr))
	v := []any{"alpha", "bravo", "charlie"}
	events := roundTrip(t, "tags", typ, v)

	// AssignStart, CompositeStart, 3×ScalarValue, CompositeEnd, AssignEnd
	if len(events) != 7 {
		t.Fatalf("expected 7 events, got %d", len(events))
	}
	if events[2].Value != "alpha" || events[3].Value != "bravo" || events[4].Value != "charlie" {
		t.Errorf("values: %q %q %q", events[2].Value, events[3].Value, events[4].Value)
	}
}

func TestRoundTripListEmpty(t *testing.T) {
	typ := makeListType(scalarType(TypeInt))
	v := []any{}
	events := roundTrip(t, "empty", typ, v)

	// AssignStart, CompositeStart, CompositeEnd, AssignEnd
	if len(events) != 4 {
		t.Fatalf("expected 4 events, got %d: %v", len(events), events)
	}
}

func TestRoundTripMap(t *testing.T) {
	typ := makeMapType(scalarType(TypeStr), scalarType(TypeInt))
	v := map[any]any{"x": int64(10)}
	events := roundTrip(t, "m", typ, v)

	// AssignStart, CompositeStart, key scalar, value scalar, CompositeEnd, AssignEnd
	if len(events) != 6 {
		t.Fatalf("expected 6 events, got %d: %v", len(events), events)
	}
	if events[2].Kind != EventScalarValue || events[2].Value != "x" {
		t.Errorf("key event = %v", events[2])
	}
	if events[3].Kind != EventScalarValue || events[3].Value != "10" {
		t.Errorf("value event = %v", events[3])
	}
}

func TestRoundTripMapEmpty(t *testing.T) {
	typ := makeMapType(scalarType(TypeStr), scalarType(TypeInt))
	v := map[any]any{}
	events := roundTrip(t, "m", typ, v)

	// AssignStart, CompositeStart, CompositeEnd, AssignEnd
	if len(events) != 4 {
		t.Fatalf("expected 4 events, got %d: %v", len(events), events)
	}
}

// ---------------------------------------------------------------------------
// 10. Compact vs pretty — same data, both parse correctly
// ---------------------------------------------------------------------------

func TestCompactVsPrettyStruct(t *testing.T) {
	typ := makeStructType(
		Field{Name: "a", Type: scalarType(TypeStr)},
		Field{Name: "b", Type: scalarType(TypeInt)},
	)
	v := map[string]any{"a": "hello", "b": int64(42)}

	compact := encodeCompact(t, "s", typ, v)
	pretty := encodePretty(t, "s", typ, v, "  ")

	if compact == pretty {
		t.Error("compact and pretty should differ")
	}

	// Both should decode successfully.
	for _, input := range []string{compact, pretty} {
		dec := NewDecoder(strings.NewReader(input))
		var events []Event
		for {
			ev, err := dec.Decode()
			if err == io.EOF {
				break
			}
			if err != nil {
				t.Fatalf("Decode(%q): %v", input, err)
			}
			events = append(events, ev)
		}
		if len(events) != 6 {
			t.Errorf("expected 6 events from %q, got %d", input, len(events))
		}
	}
}

func TestCompactVsPrettyList(t *testing.T) {
	typ := makeListType(scalarType(TypeInt))
	v := []any{int64(1), int64(2), int64(3)}

	compact := encodeCompact(t, "l", typ, v)
	pretty := encodePretty(t, "l", typ, v, "\t")

	if compact == pretty {
		t.Error("compact and pretty should differ")
	}

	// Both should decode successfully with same values.
	for _, input := range []string{compact, pretty} {
		dec := NewDecoder(strings.NewReader(input))
		var events []Event
		for {
			ev, err := dec.Decode()
			if err == io.EOF {
				break
			}
			if err != nil {
				t.Fatalf("Decode(%q): %v", input, err)
			}
			events = append(events, ev)
		}
		// AssignStart + CompositeStart + 3 values + CompositeEnd + AssignEnd
		if len(events) != 7 {
			t.Errorf("expected 7 events from %q, got %d", input, len(events))
		}
	}
}

func TestCompactVsPrettyTuple(t *testing.T) {
	typ := makeTupleType(scalarType(TypeStr), scalarType(TypeInt))
	v := []any{"hello", int64(42)}

	compact := encodeCompact(t, "t", typ, v)
	pretty := encodePretty(t, "t", typ, v, "  ")

	if compact == pretty {
		t.Error("compact and pretty should differ")
	}

	for _, input := range []string{compact, pretty} {
		dec := NewDecoder(strings.NewReader(input))
		var events []Event
		for {
			ev, err := dec.Decode()
			if err == io.EOF {
				break
			}
			if err != nil {
				t.Fatalf("Decode(%q): %v", input, err)
			}
			events = append(events, ev)
		}
		if len(events) != 6 {
			t.Errorf("expected 6 events from %q, got %d", input, len(events))
		}
	}
}

func TestCompactVsPrettyMap(t *testing.T) {
	typ := makeMapType(scalarType(TypeStr), scalarType(TypeInt))
	v := map[any]any{"k": int64(99)}

	compact := encodeCompact(t, "m", typ, v)
	pretty := encodePretty(t, "m", typ, v, "  ")

	if compact == pretty {
		t.Error("compact and pretty should differ")
	}

	for _, input := range []string{compact, pretty} {
		dec := NewDecoder(strings.NewReader(input))
		var events []Event
		for {
			ev, err := dec.Decode()
			if err == io.EOF {
				break
			}
			if err != nil {
				t.Fatalf("Decode(%q): %v", input, err)
			}
			events = append(events, ev)
		}
		if len(events) != 6 {
			t.Errorf("expected 6 events from %q, got %d", input, len(events))
		}
	}
}

// ---------------------------------------------------------------------------
// Error cases
// ---------------------------------------------------------------------------

func TestEncodeNilNonNullable(t *testing.T) {
	var buf bytes.Buffer
	enc := NewEncoder(&buf)
	err := enc.Encode("x", scalarType(TypeStr), nil)
	if err == nil {
		t.Error("expected error for nil non-nullable")
	}
}

func TestEncodeInvalidUUID(t *testing.T) {
	var buf bytes.Buffer
	enc := NewEncoder(&buf)
	err := enc.Encode("x", scalarType(TypeUUID), "not-a-uuid")
	if err == nil {
		t.Error("expected error for invalid UUID")
	}
}

func TestEncodeTupleLengthMismatch(t *testing.T) {
	typ := makeTupleType(scalarType(TypeInt), scalarType(TypeInt))
	var buf bytes.Buffer
	enc := NewEncoder(&buf)
	err := enc.Encode("x", typ, []any{int64(1)})
	if err == nil {
		t.Error("expected error for tuple length mismatch")
	}
}

func TestEncodeStrTypeMismatch(t *testing.T) {
	var buf bytes.Buffer
	enc := NewEncoder(&buf)
	err := enc.Encode("x", scalarType(TypeStr), 42)
	if err == nil {
		t.Error("expected error for type mismatch")
	}
}

// ---------------------------------------------------------------------------
// Round-trip with string containing escapes
// ---------------------------------------------------------------------------

func TestRoundTripStrWithTab(t *testing.T) {
	events := roundTrip(t, "s", scalarType(TypeStr), "hello\tworld")
	if events[1].Value != "hello\tworld" {
		t.Errorf("got value %q, want %q", events[1].Value, "hello\tworld")
	}
}

func TestRoundTripStrWithBackslash(t *testing.T) {
	events := roundTrip(t, "s", scalarType(TypeStr), `path\to\file`)
	if events[1].Value != `path\to\file` {
		t.Errorf("got value %q, want %q", events[1].Value, `path\to\file`)
	}
}

func TestRoundTripStrWithQuotes(t *testing.T) {
	events := roundTrip(t, "s", scalarType(TypeStr), "it's fine")
	if events[1].Value != "it's fine" {
		t.Errorf("got value %q, want %q", events[1].Value, "it's fine")
	}
}

func TestRoundTripMultiLineStr(t *testing.T) {
	val := "line one\nline two"
	events := roundTrip(t, "s", scalarType(TypeStr), val)
	if events[1].Value != val {
		t.Errorf("got value %q, want %q", events[1].Value, val)
	}
}

// ---------------------------------------------------------------------------
// Nested round-trip
// ---------------------------------------------------------------------------

func TestRoundTripNestedPretty(t *testing.T) {
	inner := makeListType(scalarType(TypeInt))
	typ := makeStructType(
		Field{Name: "name", Type: scalarType(TypeStr)},
		Field{Name: "vals", Type: inner},
	)
	v := map[string]any{
		"name": "test",
		"vals": []any{int64(10), int64(20)},
	}

	var buf bytes.Buffer
	enc := NewEncoder(&buf)
	enc.SetIndent("  ")
	if err := enc.Encode("data", typ, v); err != nil {
		t.Fatalf("Encode: %v", err)
	}

	dec := NewDecoder(&buf)
	var events []Event
	for {
		ev, err := dec.Decode()
		if err == io.EOF {
			break
		}
		if err != nil {
			t.Fatalf("Decode: %v", err)
		}
		events = append(events, ev)
	}

	// AssignStart, StructStart, name-scalar, ListStart, 2×scalar, ListEnd, StructEnd, AssignEnd
	if len(events) != 9 {
		t.Fatalf("expected 9 events, got %d: %v", len(events), events)
	}
	if events[2].Value != "test" {
		t.Errorf("name value = %q", events[2].Value)
	}
	if events[4].Value != "10" || events[5].Value != "20" {
		t.Errorf("list values = %q, %q", events[4].Value, events[5].Value)
	}
}
