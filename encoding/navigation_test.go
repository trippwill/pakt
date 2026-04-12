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
