package encoding

import (
	"strings"
	"testing"
)

// ---------------------------------------------------------------------------
// helpers
// ---------------------------------------------------------------------------

func scalarType(k TypeKind) Type {
	return Type{Scalar: &k}
}

func nullableScalar(k TypeKind) Type {
	return Type{Scalar: &k, Nullable: true}
}

func atomSetType(members ...string) Type {
	return Type{AtomSet: &AtomSet{Members: members}}
}

func structType(fields ...Field) Type {
	return Type{Struct: &StructType{Fields: fields}}
}

func tupleType(elems ...Type) Type {
	return Type{Tuple: &TupleType{Elements: elems}}
}

func listType(elem Type) Type {
	return Type{List: &ListType{Element: elem}}
}

func mapType(key, val Type) Type {
	return Type{Map: &MapType{Key: key, Value: val}}
}

func field(name string, typ Type) Field {
	return Field{Name: name, Type: typ}
}

// readValueEvents creates a reader from input, reads a value of the given type,
// and returns the emitted events.
func readValueEvents(t *testing.T, input string, typ Type) []Event {
	t.Helper()
	r := newReader(strings.NewReader(input))
	if err := r.readValue(typ, ""); err != nil {
		t.Fatalf("readValue(%q): %v", input, err)
	}
	return r.events
}

// expectEvents asserts that the given events match the expected kinds and values.
func expectEvents(t *testing.T, events []Event, expected []Event) {
	t.Helper()
	if len(events) != len(expected) {
		t.Fatalf("expected %d events, got %d:\n  got:  %v\n  want: %v", len(expected), len(events), events, expected)
	}
	for i, ev := range events {
		exp := expected[i]
		if ev.Kind != exp.Kind {
			t.Errorf("event[%d]: kind=%s, want %s", i, ev.Kind, exp.Kind)
		}
		if exp.Name != "" && ev.Name != exp.Name {
			t.Errorf("event[%d]: name=%q, want %q", i, ev.Name, exp.Name)
		}
		if exp.Value != "" && ev.Value != exp.Value {
			t.Errorf("event[%d]: value=%q, want %q", i, ev.Value, exp.Value)
		}
		if exp.Type != "" && ev.Type != exp.Type {
			t.Errorf("event[%d]: type=%q, want %q", i, ev.Type, exp.Type)
		}
	}
}

// ---------------------------------------------------------------------------
// Scalar values — all 9 types
// ---------------------------------------------------------------------------

func TestReadScalarValues(t *testing.T) {
	tests := []struct {
		name  string
		input string
		kind  TypeKind
		value string
	}{
		{"str", "'hello'", TypeStr, "hello"},
		{"int", "42", TypeInt, "42"},
		{"int_neg", "-7", TypeInt, "-7"},
		{"int_hex", "0xFF", TypeInt, "0xFF"},
		{"dec", "3.14", TypeDec, "3.14"},
		{"float", "6.022e23", TypeFloat, "6.022e23"},
		{"bool_true", "true", TypeBool, "true"},
		{"bool_false", "false", TypeBool, "false"},
		{"uuid", "550e8400-e29b-41d4-a716-446655440000", TypeUUID, "550e8400-e29b-41d4-a716-446655440000"},
		{"date", "2026-06-01", TypeDate, "2026-06-01"},
		{"time", "14:30:00Z", TypeTime, "14:30:00Z"},
		{"time_offset", "14:30:00-04:00", TypeTime, "14:30:00-04:00"},
		{"datetime", "2026-06-01T14:30:00Z", TypeDateTime, "2026-06-01T14:30:00Z"},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			events := readValueEvents(t, tc.input, scalarType(tc.kind))
			if len(events) != 1 {
				t.Fatalf("expected 1 event, got %d", len(events))
			}
			if events[0].Kind != EventScalarValue {
				t.Fatalf("expected ScalarValue, got %s", events[0].Kind)
			}
			if events[0].Value != tc.value {
				t.Fatalf("value=%q, want %q", events[0].Value, tc.value)
			}
			if events[0].Type != tc.kind.String() {
				t.Fatalf("type=%q, want %q", events[0].Type, tc.kind.String())
			}
		})
	}
}

// ---------------------------------------------------------------------------
// Nil values
// ---------------------------------------------------------------------------

func TestReadNilValue(t *testing.T) {
	tests := []struct {
		name  string
		input string
		typ   Type
	}{
		{"nullable_str", "nil", nullableScalar(TypeStr)},
		{"nullable_int", "nil", nullableScalar(TypeInt)},
		{"nullable_bool", "nil", nullableScalar(TypeBool)},
		{"nullable_atom", "nil", Type{AtomSet: &AtomSet{Members: []string{"a", "b"}}, Nullable: true}},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			events := readValueEvents(t, tc.input, tc.typ)
			if len(events) != 1 {
				t.Fatalf("expected 1 event, got %d", len(events))
			}
			if events[0].Kind != EventScalarValue {
				t.Fatalf("expected ScalarValue, got %s", events[0].Kind)
			}
			if events[0].Value != "nil" {
				t.Fatalf("value=%q, want %q", events[0].Value, "nil")
			}
		})
	}
}

func TestReadNilNonNullableError(t *testing.T) {
	r := newReader(strings.NewReader("nil"))
	err := r.readValue(scalarType(TypeStr), "")
	if err == nil {
		t.Fatal("expected error for nil on non-nullable type")
	}
}

// ---------------------------------------------------------------------------
// Atom values
// ---------------------------------------------------------------------------

func TestReadAtomValues(t *testing.T) {
	tests := []struct {
		name    string
		input   string
		members []string
		value   string
	}{
		{"valid_first", "dev", []string{"dev", "staging", "prod"}, "dev"},
		{"valid_last", "prod", []string{"dev", "staging", "prod"}, "prod"},
		{"single_member", "only", []string{"only"}, "only"},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			events := readValueEvents(t, tc.input, atomSetType(tc.members...))
			if len(events) != 1 {
				t.Fatalf("expected 1 event, got %d", len(events))
			}
			if events[0].Kind != EventScalarValue {
				t.Fatalf("expected ScalarValue, got %s", events[0].Kind)
			}
			if events[0].Value != tc.value {
				t.Fatalf("value=%q, want %q", events[0].Value, tc.value)
			}
		})
	}
}

func TestReadAtomValueInvalid(t *testing.T) {
	r := newReader(strings.NewReader("test"))
	err := r.readValue(atomSetType("dev", "staging", "prod"), "")
	if err == nil {
		t.Fatal("expected error for invalid atom value")
	}
}

// ---------------------------------------------------------------------------
// Struct values
// ---------------------------------------------------------------------------

func TestReadStructInline(t *testing.T) {
	typ := structType(
		field("host", scalarType(TypeStr)),
		field("port", scalarType(TypeInt)),
	)
	events := readValueEvents(t, "{ 'localhost', 8080 }", typ)
	expectEvents(t, events, []Event{
		{Kind: EventCompositeStart, Type: typ.String()},
		{Kind: EventScalarValue, Name: "host", Value: "localhost", Type: "str"},
		{Kind: EventScalarValue, Name: "port", Value: "8080", Type: "int"},
		{Kind: EventCompositeEnd, Type: typ.String()},
	})
}

func TestReadStructBlock(t *testing.T) {
	typ := structType(
		field("level", scalarType(TypeStr)),
		field("release", scalarType(TypeInt)),
	)
	input := "{\n'platform'\n26\n}"
	events := readValueEvents(t, input, typ)
	expectEvents(t, events, []Event{
		{Kind: EventCompositeStart, Type: typ.String()},
		{Kind: EventScalarValue, Name: "level", Value: "platform", Type: "str"},
		{Kind: EventScalarValue, Name: "release", Value: "26", Type: "int"},
		{Kind: EventCompositeEnd, Type: typ.String()},
	})
}

func TestReadStructSingleField(t *testing.T) {
	typ := structType(field("name", scalarType(TypeStr)))
	events := readValueEvents(t, "{ 'solo' }", typ)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d", len(events))
	}
	if events[1].Value != "solo" {
		t.Fatalf("value=%q, want %q", events[1].Value, "solo")
	}
}

func TestReadStructTrailingSep(t *testing.T) {
	typ := structType(
		field("a", scalarType(TypeInt)),
		field("b", scalarType(TypeInt)),
	)
	events := readValueEvents(t, "{ 1, 2, }", typ)
	if len(events) != 4 {
		t.Fatalf("expected 4 events, got %d", len(events))
	}
}

func TestReadStructTooFewFields(t *testing.T) {
	typ := structType(
		field("a", scalarType(TypeInt)),
		field("b", scalarType(TypeInt)),
		field("c", scalarType(TypeInt)),
	)
	r := newReader(strings.NewReader("{ 1, 2 }"))
	err := r.readValue(typ, "")
	if err == nil {
		t.Fatal("expected error for too few struct fields")
	}
}

func TestReadStructMixed(t *testing.T) {
	typ := structType(
		field("a", scalarType(TypeStr)),
		field("b", scalarType(TypeInt)),
		field("c", scalarType(TypeBool)),
	)
	// Mixed: comma after first, newline after second.
	input := "{ 'hello', 42\ntrue }"
	events := readValueEvents(t, input, typ)
	if len(events) != 5 {
		t.Fatalf("expected 5 events, got %d", len(events))
	}
}

// ---------------------------------------------------------------------------
// Tuple values
// ---------------------------------------------------------------------------

func TestReadTupleInline(t *testing.T) {
	typ := tupleType(scalarType(TypeInt), scalarType(TypeInt), scalarType(TypeInt))
	events := readValueEvents(t, "(3, 45, 5678)", typ)
	expectEvents(t, events, []Event{
		{Kind: EventCompositeStart, Type: typ.String()},
		{Kind: EventScalarValue, Name: "[0]", Value: "3", Type: "int"},
		{Kind: EventScalarValue, Name: "[1]", Value: "45", Type: "int"},
		{Kind: EventScalarValue, Name: "[2]", Value: "5678", Type: "int"},
		{Kind: EventCompositeEnd, Type: typ.String()},
	})
}

func TestReadTupleBlock(t *testing.T) {
	typ := tupleType(scalarType(TypeInt), scalarType(TypeStr))
	input := "(\n42\n'hello'\n)"
	events := readValueEvents(t, input, typ)
	if len(events) != 4 {
		t.Fatalf("expected 4 events, got %d", len(events))
	}
	if events[1].Value != "42" || events[2].Value != "hello" {
		t.Fatalf("unexpected values: %v, %v", events[1], events[2])
	}
}

func TestReadTupleSingleElement(t *testing.T) {
	typ := tupleType(scalarType(TypeBool))
	events := readValueEvents(t, "(true)", typ)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d", len(events))
	}
	if events[1].Value != "true" {
		t.Fatalf("value=%q, want %q", events[1].Value, "true")
	}
}

func TestReadTupleTrailingSep(t *testing.T) {
	typ := tupleType(scalarType(TypeInt), scalarType(TypeInt))
	events := readValueEvents(t, "(1, 2,)", typ)
	if len(events) != 4 {
		t.Fatalf("expected 4 events, got %d", len(events))
	}
}

func TestReadTupleTooFewElements(t *testing.T) {
	typ := tupleType(scalarType(TypeInt), scalarType(TypeInt), scalarType(TypeInt))
	r := newReader(strings.NewReader("(1, 2)"))
	err := r.readValue(typ, "")
	if err == nil {
		t.Fatal("expected error for too few tuple elements")
	}
}

// ---------------------------------------------------------------------------
// List values
// ---------------------------------------------------------------------------

func TestReadListInline(t *testing.T) {
	typ := listType(scalarType(TypeInt))
	events := readValueEvents(t, "[1, 2, 3]", typ)
	expectEvents(t, events, []Event{
		{Kind: EventCompositeStart, Type: typ.String()},
		{Kind: EventScalarValue, Name: "[0]", Value: "1", Type: "int"},
		{Kind: EventScalarValue, Name: "[1]", Value: "2", Type: "int"},
		{Kind: EventScalarValue, Name: "[2]", Value: "3", Type: "int"},
		{Kind: EventCompositeEnd, Type: typ.String()},
	})
}

func TestReadListBlock(t *testing.T) {
	typ := listType(scalarType(TypeStr))
	input := "[\n'a'\n'b'\n'c'\n]"
	events := readValueEvents(t, input, typ)
	if len(events) != 5 {
		t.Fatalf("expected 5 events, got %d", len(events))
	}
}

func TestReadListEmpty(t *testing.T) {
	typ := listType(scalarType(TypeInt))
	events := readValueEvents(t, "[]", typ)
	if len(events) != 2 {
		t.Fatalf("expected 2 events (start+end), got %d", len(events))
	}
	if events[0].Kind != EventCompositeStart || events[1].Kind != EventCompositeEnd {
		t.Fatalf("expected CompositeStart/End, got %s/%s", events[0].Kind, events[1].Kind)
	}
}

func TestReadListSingleElement(t *testing.T) {
	typ := listType(scalarType(TypeBool))
	events := readValueEvents(t, "[true]", typ)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d", len(events))
	}
	if events[1].Value != "true" {
		t.Fatalf("value=%q, want %q", events[1].Value, "true")
	}
}

func TestReadListTrailingSep(t *testing.T) {
	typ := listType(scalarType(TypeInt))
	events := readValueEvents(t, "[1, 2, 3,]", typ)
	if len(events) != 5 {
		t.Fatalf("expected 5 events, got %d", len(events))
	}
}

// ---------------------------------------------------------------------------
// Map values
// ---------------------------------------------------------------------------

func TestReadMapInline(t *testing.T) {
	typ := mapType(scalarType(TypeStr), scalarType(TypeInt))
	events := readValueEvents(t, "< 'host' = 8080, 'port' = 9090 >", typ)
	if len(events) != 6 {
		t.Fatalf("expected 6 events, got %d: %v", len(events), events)
	}
	if events[0].Kind != EventCompositeStart {
		t.Fatalf("event[0]: expected CompositeStart, got %s", events[0].Kind)
	}
	// Key events
	if events[1].Kind != EventScalarValue || events[1].Value != "host" {
		t.Fatalf("event[1]: %v", events[1])
	}
	if events[2].Kind != EventScalarValue || events[2].Value != "8080" {
		t.Fatalf("event[2]: %v", events[2])
	}
	if events[3].Kind != EventScalarValue || events[3].Value != "port" {
		t.Fatalf("event[3]: %v", events[3])
	}
	if events[4].Kind != EventScalarValue || events[4].Value != "9090" {
		t.Fatalf("event[4]: %v", events[4])
	}
	if events[5].Kind != EventCompositeEnd {
		t.Fatalf("event[5]: expected CompositeEnd, got %s", events[5].Kind)
	}
}

func TestReadMapBlock(t *testing.T) {
	typ := mapType(scalarType(TypeStr), scalarType(TypeInt))
	input := "<\n'a' = 1\n'b' = 2\n>"
	events := readValueEvents(t, input, typ)
	if len(events) != 6 {
		t.Fatalf("expected 6 events, got %d", len(events))
	}
}

func TestReadMapEmpty(t *testing.T) {
	typ := mapType(scalarType(TypeStr), scalarType(TypeInt))
	events := readValueEvents(t, "<>", typ)
	if len(events) != 2 {
		t.Fatalf("expected 2 events (start+end), got %d", len(events))
	}
}

func TestReadMapTrailingSep(t *testing.T) {
	typ := mapType(scalarType(TypeStr), scalarType(TypeInt))
	events := readValueEvents(t, "< 'a' = 1, 'b' = 2, >", typ)
	if len(events) != 6 {
		t.Fatalf("expected 6 events, got %d", len(events))
	}
}

func TestReadMapDuplicateKeyError(t *testing.T) {
	typ := mapType(scalarType(TypeStr), scalarType(TypeInt))
	r := newReader(strings.NewReader("< 'a' = 1, 'a' = 2 >"))
	err := r.readValue(typ, "")
	if err == nil {
		t.Fatal("expected error for duplicate map key")
	}
	if !strings.Contains(err.Error(), "duplicate") {
		t.Fatalf("error should mention 'duplicate': %v", err)
	}
}

func TestReadMapCompositeStructValue(t *testing.T) {
	valType := structType(
		field("name", scalarType(TypeStr)),
		field("age", scalarType(TypeInt)),
	)
	typ := mapType(scalarType(TypeStr), valType)
	input := "< 'alice' = { 'Alice', 30 } >"
	events := readValueEvents(t, input, typ)
	// CompositeStart(map), ScalarValue(key), CompositeStart(struct), ScalarValue, ScalarValue, CompositeEnd(struct), CompositeEnd(map)
	if len(events) != 7 {
		t.Fatalf("expected 7 events, got %d: %v", len(events), events)
	}
}

// ---------------------------------------------------------------------------
// Nested composites
// ---------------------------------------------------------------------------

func TestReadNestedStructWithList(t *testing.T) {
	inner := listType(scalarType(TypeInt))
	typ := structType(
		field("name", scalarType(TypeStr)),
		field("scores", inner),
	)
	input := "{ 'alice', [90, 85, 92] }"
	events := readValueEvents(t, input, typ)
	// Struct start, scalar(name), list start, scalar, scalar, scalar, list end, struct end
	if len(events) != 8 {
		t.Fatalf("expected 8 events, got %d: %v", len(events), events)
	}
	if events[0].Kind != EventCompositeStart {
		t.Fatalf("event[0] kind=%s", events[0].Kind)
	}
	if events[1].Name != "name" || events[1].Value != "alice" {
		t.Fatalf("event[1] = %v", events[1])
	}
	if events[2].Kind != EventCompositeStart && events[2].Name != "scores" {
		t.Fatalf("event[2] = %v", events[2])
	}
}

func TestReadNestedStructWithTuple(t *testing.T) {
	inner := tupleType(scalarType(TypeInt), scalarType(TypeStr))
	typ := listType(inner)
	input := "[(1, 'a'), (2, 'b')]"
	events := readValueEvents(t, input, typ)
	// list start, (tuple start, int, str, tuple end) x2, list end
	if len(events) != 10 {
		t.Fatalf("expected 10 events, got %d: %v", len(events), events)
	}
}

func TestReadMapWithStructValues(t *testing.T) {
	valType := structType(field("x", scalarType(TypeInt)))
	typ := mapType(scalarType(TypeStr), valType)
	input := "<\n'k1' = { 1 }\n'k2' = { 2 }\n>"
	events := readValueEvents(t, input, typ)
	// map start, key1, struct start, int, struct end, key2, struct start, int, struct end, map end
	if len(events) != 10 {
		t.Fatalf("expected 10 events, got %d: %v", len(events), events)
	}
}

// ---------------------------------------------------------------------------
// SEP edge cases
// ---------------------------------------------------------------------------

func TestReadStructCommaWithSpaces(t *testing.T) {
	typ := structType(
		field("a", scalarType(TypeStr)),
		field("b", scalarType(TypeStr)),
	)
	events := readValueEvents(t, "{ 'x' , 'y' }", typ)
	if len(events) != 4 {
		t.Fatalf("expected 4 events, got %d", len(events))
	}
}

func TestReadStructCommaAndNewline(t *testing.T) {
	typ := structType(
		field("a", scalarType(TypeStr)),
		field("b", scalarType(TypeStr)),
	)
	// Comma + newline together = one separation.
	events := readValueEvents(t, "{ 'x',\n'y' }", typ)
	if len(events) != 4 {
		t.Fatalf("expected 4 events, got %d", len(events))
	}
}

func TestReadListBlankLinesBetween(t *testing.T) {
	typ := listType(scalarType(TypeInt))
	input := "[\n1\n\n\n2\n]"
	events := readValueEvents(t, input, typ)
	if len(events) != 4 {
		t.Fatalf("expected 4 events, got %d", len(events))
	}
}

func TestReadListCommentsBetween(t *testing.T) {
	typ := listType(scalarType(TypeInt))
	input := "[\n1\n# a comment\n2\n]"
	events := readValueEvents(t, input, typ)
	if len(events) != 4 {
		t.Fatalf("expected 4 events, got %d", len(events))
	}
}

// ---------------------------------------------------------------------------
// readSep unit tests
// ---------------------------------------------------------------------------

func TestReadSepComma(t *testing.T) {
	r := mkReader(", next")
	ok, err := r.readSep()
	if err != nil {
		t.Fatal(err)
	}
	if !ok {
		t.Fatal("expected separator")
	}
	b, _ := r.readByte()
	if b != 'n' {
		t.Fatalf("expected 'n' after sep, got %q", b)
	}
}

func TestReadSepNewline(t *testing.T) {
	r := mkReader("\nnext")
	ok, err := r.readSep()
	if err != nil {
		t.Fatal(err)
	}
	if !ok {
		t.Fatal("expected separator from newline")
	}
	b, _ := r.readByte()
	if b != 'n' {
		t.Fatalf("expected 'n' after sep, got %q", b)
	}
}

func TestReadSepNone(t *testing.T) {
	r := mkReader("next")
	ok, _ := r.readSep()
	if ok {
		t.Fatal("expected no separator")
	}
}

func TestReadSepCommaNewline(t *testing.T) {
	r := mkReader(",\n  next")
	ok, err := r.readSep()
	if err != nil {
		t.Fatal(err)
	}
	if !ok {
		t.Fatal("expected separator")
	}
	b, _ := r.readByte()
	if b != 'n' {
		t.Fatalf("expected 'n' after sep, got %q", b)
	}
}

// ---------------------------------------------------------------------------
// peekNil unit tests
// ---------------------------------------------------------------------------

func TestPeekNilTrue(t *testing.T) {
	r := mkReader("nil")
	if !r.peekNil() {
		t.Fatal("expected peekNil to return true")
	}
}

func TestPeekNilFalsePrefix(t *testing.T) {
	r := mkReader("nilable")
	if r.peekNil() {
		t.Fatal("expected peekNil to return false for 'nilable'")
	}
}

func TestPeekNilWithSpaces(t *testing.T) {
	r := mkReader("  nil")
	if !r.peekNil() {
		t.Fatal("expected peekNil to return true with leading spaces")
	}
}

func TestPeekNilNotConsuming(t *testing.T) {
	r := mkReader("nil")
	r.peekNil()
	b, err := r.readByte()
	if err != nil {
		t.Fatal(err)
	}
	if b != 'n' {
		t.Fatalf("peekNil should not consume; got %q", b)
	}
}
