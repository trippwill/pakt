package encoding

import (
	"encoding/json"
	"errors"
	"strings"
	"testing"
)

func TestEventKindMarshalRoundTrip(t *testing.T) {
	kinds := []struct {
		kind EventKind
		want string
	}{
		{EventAssignStart, `"AssignStart"`},
		{EventAssignEnd, `"AssignEnd"`},
		{EventListPackStart, `"ListPackStart"`},
		{EventListPackEnd, `"ListPackEnd"`},
		{EventMapPackStart, `"MapPackStart"`},
		{EventMapPackEnd, `"MapPackEnd"`},
		{EventScalarValue, `"ScalarValue"`},
		{EventStructStart, `"StructStart"`},
		{EventStructEnd, `"StructEnd"`},
		{EventTupleStart, `"TupleStart"`},
		{EventTupleEnd, `"TupleEnd"`},
		{EventListStart, `"ListStart"`},
		{EventListEnd, `"ListEnd"`},
		{EventMapStart, `"MapStart"`},
		{EventMapEnd, `"MapEnd"`},
		{EventError, `"Error"`},
	}

	for _, tc := range kinds {
		t.Run(tc.want, func(t *testing.T) {
			data, err := json.Marshal(tc.kind)
			if err != nil {
				t.Fatalf("MarshalJSON: %v", err)
			}
			if string(data) != tc.want {
				t.Fatalf("got %s, want %s", data, tc.want)
			}

			var got EventKind
			if err := json.Unmarshal(data, &got); err != nil {
				t.Fatalf("UnmarshalJSON: %v", err)
			}
			if got != tc.kind {
				t.Fatalf("round-trip: got %v, want %v", got, tc.kind)
			}
		})
	}
}

func TestEventKindUnmarshalUnknown(t *testing.T) {
	var k EventKind
	err := json.Unmarshal([]byte(`"Bogus"`), &k)
	if err == nil {
		t.Fatal("expected error for unknown EventKind")
	}
	if !strings.Contains(err.Error(), "unknown EventKind") {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestEventMarshalScalar(t *testing.T) {
	e := Event{
		Kind:       EventScalarValue,
		Pos:        Pos{Line: 1, Col: 16},
		Name:       "greeting",
		ScalarType: TypeStr,
		Value:      "'hello world'",
	}

	data, err := json.Marshal(e)
	if err != nil {
		t.Fatalf("MarshalJSON: %v", err)
	}

	want := `{"kind":"ScalarValue","pos":{"line":1,"col":16},"name":"greeting","scalarType":"str","value":"'hello world'"}`
	if string(data) != want {
		t.Fatalf("got:\n  %s\nwant:\n  %s", data, want)
	}
}

func TestEventMarshalWithError(t *testing.T) {
	e := Event{
		Kind: EventError,
		Pos:  Pos{Line: 2, Col: 1},
		Err:  errors.New(`2:1: duplicate root name "greeting"`),
	}

	data, err := json.Marshal(e)
	if err != nil {
		t.Fatalf("MarshalJSON: %v", err)
	}

	want := `{"kind":"Error","pos":{"line":2,"col":1},"error":"2:1: duplicate root name \"greeting\""}`
	if string(data) != want {
		t.Fatalf("got:\n  %s\nwant:\n  %s", data, want)
	}
}

func TestEventMarshalOmitsErrorWhenNil(t *testing.T) {
	e := Event{
		Kind: EventAssignEnd,
		Pos:  Pos{Line: 3, Col: 1},
	}

	data, err := json.Marshal(e)
	if err != nil {
		t.Fatalf("MarshalJSON: %v", err)
	}
	if strings.Contains(string(data), `"error"`) {
		t.Fatalf("expected no error field, got: %s", data)
	}
}

func TestEventMarshalOmitsEmptyFields(t *testing.T) {
	e := Event{
		Kind: EventStructStart,
		Pos:  Pos{Line: 5, Col: 10},
	}

	data, err := json.Marshal(e)
	if err != nil {
		t.Fatalf("MarshalJSON: %v", err)
	}
	s := string(data)
	for _, field := range []string{`"name"`, `"scalarType"`, `"value"`} {
		if strings.Contains(s, field) {
			t.Fatalf("expected %s to be omitted, got: %s", field, s)
		}
	}
}

func TestIsPackStartEnd(t *testing.T) {
	packStarts := []EventKind{EventListPackStart, EventMapPackStart}
	packEnds := []EventKind{EventListPackEnd, EventMapPackEnd}
	nonPack := []EventKind{EventAssignStart, EventAssignEnd, EventScalarValue, EventStructStart, EventListStart, EventMapStart}

	for _, k := range packStarts {
		if !k.IsPackStart() {
			t.Errorf("%s.IsPackStart() = false, want true", k)
		}
		if k.IsPackEnd() {
			t.Errorf("%s.IsPackEnd() = true, want false", k)
		}
	}
	for _, k := range packEnds {
		if !k.IsPackEnd() {
			t.Errorf("%s.IsPackEnd() = false, want true", k)
		}
		if k.IsPackStart() {
			t.Errorf("%s.IsPackStart() = true, want false", k)
		}
	}
	for _, k := range nonPack {
		if k.IsPackStart() {
			t.Errorf("%s.IsPackStart() = true, want false", k)
		}
		if k.IsPackEnd() {
			t.Errorf("%s.IsPackEnd() = true, want false", k)
		}
	}
}

func TestEventRoundTrip(t *testing.T) {
	orig := Event{
		Kind:       EventScalarValue,
		Pos:        Pos{Line: 7, Col: 3},
		Name:       "count",
		ScalarType: TypeInt,
		Value:      "42",
	}

	data, err := json.Marshal(orig)
	if err != nil {
		t.Fatalf("Marshal: %v", err)
	}

	// Unmarshal into a struct matching the JSON shape (Err is excluded via json:"-").
	var got Event
	if err := json.Unmarshal(data, &got); err != nil {
		t.Fatalf("Unmarshal: %v", err)
	}

	if got.Kind != orig.Kind {
		t.Errorf("Kind: got %v, want %v", got.Kind, orig.Kind)
	}
	if got.Pos != orig.Pos {
		t.Errorf("Pos: got %v, want %v", got.Pos, orig.Pos)
	}
	if got.Name != orig.Name {
		t.Errorf("Name: got %q, want %q", got.Name, orig.Name)
	}
	if got.ScalarType != orig.ScalarType {
		t.Errorf("ScalarType: got %q, want %q", got.ScalarType, orig.ScalarType)
	}
	if got.Value != orig.Value {
		t.Errorf("Value: got %q, want %q", got.Value, orig.Value)
	}
}
