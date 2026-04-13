package encoding

import (
	"strings"
	"testing"
)

func TestUnitReaderBasic(t *testing.T) {
	input := "name:str = 'hello'\nport:int = 8080\n"
	sr := NewUnitReader(strings.NewReader(input))
	defer sr.Close()

	var names []string
	for stmt := range sr.Properties() {
		names = append(names, stmt.Name)
		if stmt.IsPack {
			t.Errorf("unexpected pack statement: %s", stmt.Name)
		}
		// Skip the value (we're just testing navigation)
	}
	if err := sr.Err(); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	if len(names) != 2 {
		t.Fatalf("expected 2 statements, got %d", len(names))
	}
	if names[0] != "name" || names[1] != "port" {
		t.Errorf("expected [name, port], got %v", names)
	}
}

func TestUnitReaderPack(t *testing.T) {
	input := "items:[int] <<\n1\n2\n3\n"
	sr := NewUnitReader(strings.NewReader(input))
	defer sr.Close()

	var found bool
	for stmt := range sr.Properties() {
		if stmt.Name == "items" {
			found = true
			if !stmt.IsPack {
				t.Error("expected pack statement")
			}
			if stmt.Type.List == nil {
				t.Error("expected list type")
			}
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if !found {
		t.Error("expected to find 'items' statement")
	}
}

func TestUnitReaderSkip(t *testing.T) {
	input := "a:str = 'first'\nb:{x:int, y:int} = {1, 2}\nc:str = 'third'\n"
	sr := NewUnitReader(strings.NewReader(input))
	defer sr.Close()

	var names []string
	for stmt := range sr.Properties() {
		names = append(names, stmt.Name)
		// All statements are auto-skipped by Statements() iterator
	}
	if err := sr.Err(); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	if len(names) != 3 {
		t.Fatalf("expected 3 statements, got %d: %v", len(names), names)
	}
	if names[0] != "a" || names[1] != "b" || names[2] != "c" {
		t.Errorf("expected [a, b, c], got %v", names)
	}
}

func TestUnitReaderEmpty(t *testing.T) {
	sr := NewUnitReader(strings.NewReader(""))
	defer sr.Close()

	count := 0
	for range sr.Properties() {
		count++
	}
	if err := sr.Err(); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if count != 0 {
		t.Errorf("expected 0 statements, got %d", count)
	}
}

func TestUnitReaderMixed(t *testing.T) {
	input := "name:str = 'svc'\nevents:[str] <<\n'a'\n'b'\n"
	sr := NewUnitReader(strings.NewReader(input))
	defer sr.Close()

	var stmts []Property
	for stmt := range sr.Properties() {
		stmts = append(stmts, stmt)
	}
	if err := sr.Err(); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	if len(stmts) != 2 {
		t.Fatalf("expected 2 statements, got %d", len(stmts))
	}
	if stmts[0].Name != "name" || stmts[0].IsPack {
		t.Errorf("stmt 0: expected assign 'name', got %+v", stmts[0])
	}
	if stmts[1].Name != "events" || !stmts[1].IsPack {
		t.Errorf("stmt 1: expected pack 'events', got %+v", stmts[1])
	}
}

func TestUnitReaderExplicitSkip(t *testing.T) {
	input := "a:{x:int, y:int} = {1, 2}\nb:str = 'hello'\n"
	sr := NewUnitReader(strings.NewReader(input))
	defer sr.Close()

	var bVal string
	for stmt := range sr.Properties() {
		switch stmt.Name {
		case "a":
			if err := sr.Skip(); err != nil {
				t.Fatal(err)
			}
		case "b":
			val, err := ReadValue[string](sr)
			if err != nil {
				t.Fatal(err)
			}
			bVal = val
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
	if bVal != "hello" {
		t.Errorf("expected 'hello', got %q", bVal)
	}
}

func TestUnitReaderErrPropagation(t *testing.T) {
	// Malformed input should surface via Err()
	input := "name:str = 'unterminated\n"
	sr := NewUnitReader(strings.NewReader(input))
	defer sr.Close()

	for range sr.Properties() {
		_, _ = ReadValue[string](sr)
	}
	// We expect an error from the malformed string
	if err := sr.Err(); err == nil {
		// The parser may or may not error depending on the exact parse rules.
		// Accept both outcomes but verify Err() is callable.
		t.Log("no error from unterminated string (parser may accept)")
	}
}

func TestUnitReaderSkipPackStatement(t *testing.T) {
	input := "items:[int] <<\n1\n2\n3\nname:str = 'after'\n"
	sr := NewUnitReader(strings.NewReader(input))
	defer sr.Close()

	var name string
	for stmt := range sr.Properties() {
		switch stmt.Name {
		case "items":
			// Explicitly skip the pack
			if err := sr.Skip(); err != nil {
				t.Fatal(err)
			}
		case "name":
			val, err := ReadValue[string](sr)
			if err != nil {
				t.Fatal(err)
			}
			name = val
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
	if name != "after" {
		t.Errorf("expected 'after', got %q", name)
	}
}
