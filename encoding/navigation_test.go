package encoding

import (
	"strings"
	"testing"
)

func TestListElements(t *testing.T) {
	input := "tags:[str] = ['alpha', 'beta', 'gamma']\n"
	sr := NewUnitReader(strings.NewReader(input))
	defer sr.Close()

	var items []string
	for stmt := range sr.Properties() {
		_ = stmt
		// Consume the ListStart event first
		ev, err := sr.nextEvent()
		if err != nil {
			t.Fatal(err)
		}
		if ev.Kind != EventListStart {
			t.Fatalf("expected ListStart, got %s", ev.Kind)
		}
		for item := range ListElements[string](sr) {
			items = append(items, item)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
	if len(items) != 3 || items[0] != "alpha" || items[1] != "beta" || items[2] != "gamma" {
		t.Errorf("expected [alpha, beta, gamma], got %v", items)
	}
}

func TestMapEntries(t *testing.T) {
	input := "scores:<str ; int> = <'alice' ; 100, 'bob' ; 200>\n"
	sr := NewUnitReader(strings.NewReader(input))
	defer sr.Close()

	result := make(map[string]int64)
	for stmt := range sr.Properties() {
		_ = stmt
		ev, err := sr.nextEvent()
		if err != nil {
			t.Fatal(err)
		}
		if ev.Kind != EventMapStart {
			t.Fatalf("expected MapStart, got %s", ev.Kind)
		}
		for entry := range MapEntries[string, int64](sr) {
			result[entry.Key] = entry.Value
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
	if result["alice"] != 100 || result["bob"] != 200 {
		t.Errorf("unexpected: %v", result)
	}
}

func TestListElementsEarlyBreak(t *testing.T) {
	input := "nums:[int] = [1, 2, 3, 4, 5]\nname:str = 'after'\n"
	sr := NewUnitReader(strings.NewReader(input))
	defer sr.Close()

	var first int64
	var name string
	for stmt := range sr.Properties() {
		switch stmt.Name {
		case "nums":
			ev, _ := sr.nextEvent() // ListStart
			_ = ev
			for item := range ListElements[int64](sr) {
				first = item
				break // early break — should drain remaining
			}
		case "name":
			var err error
			name, err = ReadValue[string](sr)
			if err != nil {
				t.Fatal(err)
			}
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
	if first != 1 {
		t.Errorf("expected first=1, got %d", first)
	}
	if name != "after" {
		t.Errorf("expected name='after', got %q", name)
	}
}

func TestStructFields(t *testing.T) {
	input := "cfg:{host:str, port:int} = {'localhost', 8080}\n"
	sr := NewUnitReader(strings.NewReader(input))
	defer sr.Close()

	for stmt := range sr.Properties() {
		_ = stmt
		// Consume the StructStart event
		ev, err := sr.nextEvent()
		if err != nil {
			t.Fatal(err)
		}
		if ev.Kind != EventStructStart {
			t.Fatalf("expected StructStart, got %s", ev.Kind)
		}

		var fieldNames []string
		for field := range StructFields(sr) {
			fieldNames = append(fieldNames, field.Name)
			// StructFields identifies the field and leaves its value event pending on
			// the UnitReader so callers can consume it with ReadValue or Skip.
		}
		if err := sr.Err(); err != nil {
			t.Fatal(err)
		}
		if len(fieldNames) != 2 || fieldNames[0] != "host" || fieldNames[1] != "port" {
			t.Errorf("expected [host, port], got %v", fieldNames)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

func TestStructFieldsEarlyBreak(t *testing.T) {
	input := "cfg:{a:str, b:str, c:str} = {'one', 'two', 'three'}\nname:str = 'after'\n"
	sr := NewUnitReader(strings.NewReader(input))
	defer sr.Close()

	var firstName string
	var afterName string
	for stmt := range sr.Properties() {
		switch stmt.Name {
		case "cfg":
			ev, _ := sr.nextEvent() // StructStart
			_ = ev
			for field := range StructFields(sr) {
				firstName = field.Name
				break // early break — should drain remaining struct
			}
		case "name":
			var err error
			afterName, err = ReadValue[string](sr)
			if err != nil {
				t.Fatal(err)
			}
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
	if firstName != "a" {
		t.Errorf("expected first field 'a', got %q", firstName)
	}
	if afterName != "after" {
		t.Errorf("expected afterName='after', got %q", afterName)
	}
}

func TestTupleElements(t *testing.T) {
	input := "point:(int, int, int) = (10, 20, 30)\n"
	sr := NewUnitReader(strings.NewReader(input))
	defer sr.Close()

	for stmt := range sr.Properties() {
		_ = stmt
		// Consume the TupleStart event
		ev, err := sr.nextEvent()
		if err != nil {
			t.Fatal(err)
		}
		if ev.Kind != EventTupleStart {
			t.Fatalf("expected TupleStart, got %s", ev.Kind)
		}

		var indices []int
		for elem := range TupleElements(sr) {
			indices = append(indices, elem.Index)
			// TupleElements already consumed the element's event (scalar).
			// For scalar elements, no further read is needed.
		}
		if err := sr.Err(); err != nil {
			t.Fatal(err)
		}
		if len(indices) != 3 || indices[0] != 0 || indices[1] != 1 || indices[2] != 2 {
			t.Errorf("expected indices [0,1,2], got %v", indices)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

func TestTupleElementsEarlyBreak(t *testing.T) {
	input := "point:(int, int, int) = (10, 20, 30)\nname:str = 'after'\n"
	sr := NewUnitReader(strings.NewReader(input))
	defer sr.Close()

	var firstIdx int
	var name string
	for stmt := range sr.Properties() {
		switch stmt.Name {
		case "point":
			ev, _ := sr.nextEvent() // TupleStart
			_ = ev
			for elem := range TupleElements(sr) {
				firstIdx = elem.Index
				break // early break — should drain remaining tuple
			}
		case "name":
			var err error
			name, err = ReadValue[string](sr)
			if err != nil {
				t.Fatal(err)
			}
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
	if firstIdx != 0 {
		t.Errorf("expected first index 0, got %d", firstIdx)
	}
	if name != "after" {
		t.Errorf("expected name='after', got %q", name)
	}
}
