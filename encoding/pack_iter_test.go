package encoding

import (
	"strings"
	"testing"
)

func TestPackItemsBasic(t *testing.T) {
	sr := NewStatementReader(strings.NewReader("items:[int] <<\n10\n20\n30\n"))
	defer sr.Close()

	var items []int64
	for stmt := range sr.Statements() {
		if stmt.Name == "items" && stmt.IsPack {
			for item := range PackItems[int64](sr) {
				items = append(items, item)
			}
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
	if len(items) != 3 || items[0] != 10 || items[1] != 20 || items[2] != 30 {
		t.Errorf("expected [10, 20, 30], got %v", items)
	}
}

func TestPackItemsStruct(t *testing.T) {
	type Entry struct {
		Name string `pakt:"name"`
		Size int64  `pakt:"size"`
	}

	input := "files:[{name:str, size:int}] <<\n{'readme.md', 100}\n{'main.go', 500}\n"
	sr := NewStatementReader(strings.NewReader(input))
	defer sr.Close()

	var entries []Entry
	for stmt := range sr.Statements() {
		if stmt.IsPack {
			for entry := range PackItems[Entry](sr) {
				entries = append(entries, entry)
			}
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
	if len(entries) != 2 {
		t.Fatalf("expected 2 entries, got %d", len(entries))
	}
	if entries[0].Name != "readme.md" || entries[0].Size != 100 {
		t.Errorf("entry 0: %+v", entries[0])
	}
	if entries[1].Name != "main.go" || entries[1].Size != 500 {
		t.Errorf("entry 1: %+v", entries[1])
	}
}

func TestPackItemsEarlyBreak(t *testing.T) {
	input := "nums:[int] <<\n1\n2\n3\n4\n5\nname:str = 'after'\n"
	sr := NewStatementReader(strings.NewReader(input))
	defer sr.Close()

	var firstTwo []int64
	var afterName string
	for stmt := range sr.Statements() {
		switch stmt.Name {
		case "nums":
			count := 0
			for item := range PackItems[int64](sr) {
				firstTwo = append(firstTwo, item)
				count++
				if count >= 2 {
					break
				}
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

	if len(firstTwo) != 2 || firstTwo[0] != 1 || firstTwo[1] != 2 {
		t.Errorf("expected [1, 2], got %v", firstTwo)
	}
	if afterName != "after" {
		t.Errorf("expected 'after', got %q", afterName)
	}
}

func TestPackItemsIntoReuse(t *testing.T) {
	sr := NewStatementReader(strings.NewReader("items:[str] <<\n'a'\n'b'\n'c'\n"))
	defer sr.Close()

	var collected []string
	var buf string
	for stmt := range sr.Statements() {
		if stmt.IsPack {
			for p := range PackItemsInto[string](sr, &buf) {
				collected = append(collected, *p)
			}
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
	if len(collected) != 3 || collected[0] != "a" || collected[1] != "b" || collected[2] != "c" {
		t.Errorf("expected [a, b, c], got %v", collected)
	}
}

func TestPackItemsEmpty(t *testing.T) {
	// Empty pack followed by another statement
	input := "items:[int] <<\nname:str = 'after'\n"
	sr := NewStatementReader(strings.NewReader(input))
	defer sr.Close()

	var packCount int
	var afterName string
	for stmt := range sr.Statements() {
		switch stmt.Name {
		case "items":
			for range PackItems[int64](sr) {
				packCount++
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
	if packCount != 0 {
		t.Errorf("expected 0 pack items, got %d", packCount)
	}
	if afterName != "after" {
		t.Errorf("expected 'after', got %q", afterName)
	}
}
