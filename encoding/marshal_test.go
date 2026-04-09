package encoding

import (
	"bytes"
	"io"
	"strings"
	"testing"
	"time"
)

// ---------------------------------------------------------------------------
// 1. Simple scalars
// ---------------------------------------------------------------------------

func TestMarshalString(t *testing.T) {
	b, err := Marshal("greeting", "hello")
	if err != nil {
		t.Fatal(err)
	}
	got := string(b)
	want := "greeting:str = 'hello'\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestMarshalInt(t *testing.T) {
	b, err := Marshal("count", 42)
	if err != nil {
		t.Fatal(err)
	}
	got := string(b)
	want := "count:int = 42\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestMarshalFloat64(t *testing.T) {
	b, err := Marshal("ratio", 3.14)
	if err != nil {
		t.Fatal(err)
	}
	got := string(b)
	if !strings.HasPrefix(got, "ratio:float = ") {
		t.Errorf("unexpected prefix: %q", got)
	}
	if !strings.Contains(got, "e") && !strings.Contains(got, "E") {
		t.Errorf("float output lacks exponent: %q", got)
	}
}

func TestMarshalBool(t *testing.T) {
	b, err := Marshal("flag", true)
	if err != nil {
		t.Fatal(err)
	}
	got := string(b)
	want := "flag:bool = true\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

// ---------------------------------------------------------------------------
// 2. Struct
// ---------------------------------------------------------------------------

func TestMarshalStruct(t *testing.T) {
	type Person struct {
		Name string `pakt:"name"`
		Age  int    `pakt:"age"`
	}
	b, err := Marshal("person", Person{Name: "Alice", Age: 30})
	if err != nil {
		t.Fatal(err)
	}
	got := string(b)
	if !strings.Contains(got, "person:{name:str, age:int}") {
		t.Errorf("unexpected type annotation: %q", got)
	}
	if !strings.Contains(got, "'Alice'") {
		t.Errorf("missing name value: %q", got)
	}
	if !strings.Contains(got, "30") {
		t.Errorf("missing age value: %q", got)
	}
}

// ---------------------------------------------------------------------------
// 3. Nested struct
// ---------------------------------------------------------------------------

func TestMarshalNestedStruct(t *testing.T) {
	type Address struct {
		City string `pakt:"city"`
	}
	type Person struct {
		Name string  `pakt:"name"`
		Addr Address `pakt:"addr"`
	}

	b, err := Marshal("person", Person{
		Name: "Bob",
		Addr: Address{City: "NYC"},
	})
	if err != nil {
		t.Fatal(err)
	}
	got := string(b)
	if !strings.Contains(got, "addr:{city:str}") {
		t.Errorf("missing nested struct type: %q", got)
	}
	if !strings.Contains(got, "'NYC'") {
		t.Errorf("missing nested value: %q", got)
	}
}

// ---------------------------------------------------------------------------
// 4. Slice → list
// ---------------------------------------------------------------------------

func TestMarshalSlice(t *testing.T) {
	b, err := Marshal("nums", []int{1, 2, 3})
	if err != nil {
		t.Fatal(err)
	}
	got := string(b)
	want := "nums:[int] = [1, 2, 3]\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestMarshalEmptySlice(t *testing.T) {
	b, err := Marshal("empty", []int{})
	if err != nil {
		t.Fatal(err)
	}
	got := string(b)
	want := "empty:[int] = []\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

// ---------------------------------------------------------------------------
// 5. Map → map
// ---------------------------------------------------------------------------

func TestMarshalMap(t *testing.T) {
	// Use a single-entry map to avoid ordering issues.
	b, err := Marshal("scores", map[string]int{"alice": 100})
	if err != nil {
		t.Fatal(err)
	}
	got := string(b)
	if !strings.Contains(got, "scores:<str ; int>") {
		t.Errorf("unexpected type: %q", got)
	}
	if !strings.Contains(got, "'alice'") || !strings.Contains(got, "100") {
		t.Errorf("missing map entry: %q", got)
	}
}

// ---------------------------------------------------------------------------
// 6. Pointer → nullable
// ---------------------------------------------------------------------------

func TestMarshalPointerWithValue(t *testing.T) {
	s := "hello"
	b, err := Marshal("opt", &s)
	if err != nil {
		t.Fatal(err)
	}
	got := string(b)
	want := "opt:str? = 'hello'\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestMarshalPointerNil(t *testing.T) {
	var s *string
	b, err := Marshal("opt", s)
	if err != nil {
		t.Fatal(err)
	}
	got := string(b)
	want := "opt:str? = nil\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

// ---------------------------------------------------------------------------
// 7. Omitempty
// ---------------------------------------------------------------------------

func TestMarshalOmitempty(t *testing.T) {
	type Config struct {
		Host string `pakt:"host"`
		Port int    `pakt:"port,omitempty"`
	}

	b, err := Marshal("cfg", Config{Host: "localhost"})
	if err != nil {
		t.Fatal(err)
	}
	got := string(b)
	// Port is zero and tagged omitempty, should be excluded from both type and value.
	if strings.Contains(got, "port") {
		t.Errorf("omitempty field should be excluded: %q", got)
	}
	if !strings.Contains(got, "host:str") {
		t.Errorf("host field should be present: %q", got)
	}
}

func TestMarshalOmitemptyRetained(t *testing.T) {
	type Config struct {
		Host string `pakt:"host"`
		Port int    `pakt:"port,omitempty"`
	}

	b, err := Marshal("cfg", Config{Host: "localhost", Port: 8080})
	if err != nil {
		t.Fatal(err)
	}
	got := string(b)
	if !strings.Contains(got, "port") {
		t.Errorf("non-zero omitempty field should be present: %q", got)
	}
	if !strings.Contains(got, "8080") {
		t.Errorf("port value should be present: %q", got)
	}
}

// ---------------------------------------------------------------------------
// 8. time.Time → ts
// ---------------------------------------------------------------------------

func TestMarshalTime(t *testing.T) {
	ts := time.Date(2024, 6, 15, 10, 30, 0, 0, time.UTC)
	b, err := Marshal("created", ts)
	if err != nil {
		t.Fatal(err)
	}
	got := string(b)
	if !strings.Contains(got, "created:ts") {
		t.Errorf("expected ts type: %q", got)
	}
	if !strings.Contains(got, "2024-06-15T10:30:00Z") {
		t.Errorf("expected RFC3339 value: %q", got)
	}
}

// ---------------------------------------------------------------------------
// 9. Round-trip: Marshal → Decode events
// ---------------------------------------------------------------------------

func TestMarshalRoundTrip(t *testing.T) {
	type Item struct {
		Name  string `pakt:"name"`
		Count int    `pakt:"count"`
	}

	b, err := Marshal("item", Item{Name: "widget", Count: 5})
	if err != nil {
		t.Fatal(err)
	}

	dec := NewDecoder(bytes.NewReader(b))
	var events []Event
	for {
		ev, err := dec.Decode()
		if err == io.EOF {
			break
		}
		if err != nil {
			t.Fatalf("Decode error: %v", err)
		}
		events = append(events, ev)
	}

	// Expect: AssignStart, CompositeStart, ScalarValue(name), ScalarValue(count), CompositeEnd, AssignEnd
	if len(events) < 3 {
		t.Fatalf("expected at least 3 events, got %d", len(events))
	}
	if events[0].Kind != EventAssignStart {
		t.Errorf("event[0]: got %s, want AssignStart", events[0].Kind)
	}
	if events[0].Name != "item" {
		t.Errorf("event[0].Name: got %q, want %q", events[0].Name, "item")
	}
	if events[len(events)-1].Kind != EventAssignEnd {
		t.Errorf("last event: got %s, want AssignEnd", events[len(events)-1].Kind)
	}
}

// ---------------------------------------------------------------------------
// 10. MarshalIndent
// ---------------------------------------------------------------------------

func TestMarshalIndent(t *testing.T) {
	type Item struct {
		Name  string `pakt:"name"`
		Count int    `pakt:"count"`
	}

	compact, err := Marshal("item", Item{Name: "a", Count: 1})
	if err != nil {
		t.Fatal(err)
	}

	indented, err := MarshalIndent("item", Item{Name: "a", Count: 1}, "  ")
	if err != nil {
		t.Fatal(err)
	}

	// Indented output should be longer due to newlines and spaces.
	if len(indented) <= len(compact) {
		t.Errorf("indented output (%d bytes) should be longer than compact (%d bytes)", len(indented), len(compact))
	}
	if !strings.Contains(string(indented), "\n") {
		t.Errorf("indented output should contain newlines: %q", string(indented))
	}
	if !strings.Contains(string(indented), "  ") {
		t.Errorf("indented output should contain indent spaces: %q", string(indented))
	}
}

// ---------------------------------------------------------------------------
// 11. Complex nested: struct with slice of structs, map values
// ---------------------------------------------------------------------------

func TestMarshalComplexNested(t *testing.T) {
	type Tag struct {
		Key   string `pakt:"key"`
		Value string `pakt:"value"`
	}
	type Resource struct {
		Name   string            `pakt:"name"`
		Tags   []Tag             `pakt:"tags"`
		Labels map[string]string `pakt:"labels"`
	}

	v := Resource{
		Name: "myapp",
		Tags: []Tag{
			{Key: "env", Value: "prod"},
			{Key: "team", Value: "backend"},
		},
		Labels: map[string]string{"app": "myapp"},
	}

	b, err := Marshal("resource", v)
	if err != nil {
		t.Fatal(err)
	}
	got := string(b)

	if !strings.Contains(got, "name:str") {
		t.Errorf("missing name type: %q", got)
	}
	if !strings.Contains(got, "tags:[{key:str, value:str}]") {
		t.Errorf("missing tags type: %q", got)
	}
	if !strings.Contains(got, "labels:<str ; str>") {
		t.Errorf("missing labels type: %q", got)
	}
	if !strings.Contains(got, "'myapp'") {
		t.Errorf("missing name value: %q", got)
	}
	if !strings.Contains(got, "'env'") {
		t.Errorf("missing tag key: %q", got)
	}

	// Verify it round-trips through decode without error.
	dec := NewDecoder(bytes.NewReader(b))
	for {
		_, err := dec.Decode()
		if err == io.EOF {
			break
		}
		if err != nil {
			t.Fatalf("round-trip Decode error: %v", err)
		}
	}
}

// ---------------------------------------------------------------------------
// 12. Error cases
// ---------------------------------------------------------------------------

func TestMarshalNilError(t *testing.T) {
	_, err := Marshal("x", nil)
	if err == nil {
		t.Error("expected error for nil value")
	}
}

func TestMarshalUnsupportedType(t *testing.T) {
	_, err := Marshal("ch", make(chan int))
	if err == nil {
		t.Error("expected error for channel type")
	}
}

func TestMarshalFuncError(t *testing.T) {
	_, err := Marshal("fn", func() {})
	if err == nil {
		t.Error("expected error for func type")
	}
}

// ---------------------------------------------------------------------------
// Additional: TextMarshaler interface
// ---------------------------------------------------------------------------

type customText struct {
	data string
}

func (c customText) MarshalText() ([]byte, error) {
	return []byte("custom:" + c.data), nil
}

func TestMarshalTextMarshaler(t *testing.T) {
	b, err := Marshal("ct", customText{data: "hello"})
	if err != nil {
		t.Fatal(err)
	}
	got := string(b)
	if !strings.Contains(got, "ct:str") {
		t.Errorf("expected str type for TextMarshaler: %q", got)
	}
	if !strings.Contains(got, "custom:hello") {
		t.Errorf("expected MarshalText output: %q", got)
	}
}

// ---------------------------------------------------------------------------
// Additional: []byte → bin
// ---------------------------------------------------------------------------

func TestMarshalByteSlice(t *testing.T) {
	b, err := Marshal("data", []byte("binary"))
	if err != nil {
		t.Fatal(err)
	}
	got := string(b)
	want := "data:bin = x'62696e617279'\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

// ---------------------------------------------------------------------------
// Additional: Uint types
// ---------------------------------------------------------------------------

func TestMarshalUint(t *testing.T) {
	b, err := Marshal("n", uint(7))
	if err != nil {
		t.Fatal(err)
	}
	got := string(b)
	want := "n:int = 7\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}
