package encoding

import (
	"io"
	"strings"
	"testing"
)

// ---------------------------------------------------------------------------
// helpers
// ---------------------------------------------------------------------------

func decodeAll(t *testing.T, input string) []Event {
	t.Helper()
	d := NewDecoder(strings.NewReader(input))
	var events []Event
	for {
		ev, err := d.Decode()
		if err == io.EOF {
			break
		}
		if err != nil {
			t.Fatalf("Decode(): %v", err)
		}
		events = append(events, ev)
	}
	return events
}

// ---------------------------------------------------------------------------
// Simple scalar assignments
// ---------------------------------------------------------------------------

func TestDecodeSimpleStr(t *testing.T) {
	events := decodeAll(t, "name:str = 'hello'")
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[0].Kind != EventAssignStart || events[0].Name != "name" {
		t.Fatalf("event[0] = %v", events[0])
	}
	if events[0].Type != "str" {
		t.Fatalf("event[0].Type = %q, want %q", events[0].Type, "str")
	}
	if events[1].Kind != EventScalarValue || events[1].Value != "hello" {
		t.Fatalf("event[1] = %v", events[1])
	}
	if events[1].Name != "name" {
		t.Fatalf("event[1].Name = %q, want %q", events[1].Name, "name")
	}
	if events[2].Kind != EventAssignEnd || events[2].Name != "name" {
		t.Fatalf("event[2] = %v", events[2])
	}
}

func TestDecodeSimpleInt(t *testing.T) {
	events := decodeAll(t, "count:int = 42")
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d", len(events))
	}
	if events[1].Value != "42" {
		t.Fatalf("value = %q, want %q", events[1].Value, "42")
	}
}

func TestDecodeSimpleBool(t *testing.T) {
	events := decodeAll(t, "active:bool = true")
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d", len(events))
	}
	if events[1].Value != "true" {
		t.Fatalf("value = %q", events[1].Value)
	}
}

// ---------------------------------------------------------------------------
// Multiple assignments
// ---------------------------------------------------------------------------

func TestDecodeMultipleAssignments(t *testing.T) {
	input := "name:str = 'midwatch'\nversion:int = 1\nactive:bool = true"
	events := decodeAll(t, input)
	// 3 assignments × 3 events each = 9
	if len(events) != 9 {
		t.Fatalf("expected 9 events, got %d", len(events))
	}
	names := []string{"name", "version", "active"}
	for i, name := range names {
		idx := i * 3
		if events[idx].Kind != EventAssignStart || events[idx].Name != name {
			t.Errorf("event[%d]: expected AssignStart name=%q, got %v", idx, name, events[idx])
		}
	}
}

// ---------------------------------------------------------------------------
// Composite assignment
// ---------------------------------------------------------------------------

func TestDecodeStructAssignment(t *testing.T) {
	input := "server:{host:str, port:int} = { 'localhost', 8080 }"
	events := decodeAll(t, input)
	// AssignStart, CompositeStart, ScalarValue(host), ScalarValue(port), CompositeEnd, AssignEnd
	if len(events) != 6 {
		t.Fatalf("expected 6 events, got %d: %v", len(events), events)
	}
	if events[0].Kind != EventAssignStart || events[0].Name != "server" {
		t.Fatalf("event[0] = %v", events[0])
	}
	if events[1].Kind != EventCompositeStart {
		t.Fatalf("event[1] = %v", events[1])
	}
	if events[2].Kind != EventScalarValue || events[2].Name != "host" || events[2].Value != "localhost" {
		t.Fatalf("event[2] = %v", events[2])
	}
	if events[3].Kind != EventScalarValue || events[3].Name != "port" || events[3].Value != "8080" {
		t.Fatalf("event[3] = %v", events[3])
	}
	if events[4].Kind != EventCompositeEnd {
		t.Fatalf("event[4] = %v", events[4])
	}
	if events[5].Kind != EventAssignEnd {
		t.Fatalf("event[5] = %v", events[5])
	}
}

func TestDecodeTupleAssignment(t *testing.T) {
	input := "version:(int, int, int) = (1, 0, 0)"
	events := decodeAll(t, input)
	// AssignStart, CompositeStart, ScalarValue×3, CompositeEnd, AssignEnd
	if len(events) != 7 {
		t.Fatalf("expected 7 events, got %d: %v", len(events), events)
	}
	if events[2].Name != "[0]" || events[2].Value != "1" {
		t.Fatalf("event[2] = %v", events[2])
	}
}

func TestDecodeListAssignment(t *testing.T) {
	input := "ports:[int] = [80, 443, 8080]"
	events := decodeAll(t, input)
	// AssignStart, CompositeStart, ScalarValue×3, CompositeEnd, AssignEnd
	if len(events) != 7 {
		t.Fatalf("expected 7 events, got %d: %v", len(events), events)
	}
}

func TestDecodeMapAssignment(t *testing.T) {
	input := "headers:<str = str> = < 'Content-Type' = 'text/html', 'Accept' = '*/*' >"
	events := decodeAll(t, input)
	// AssignStart, CompositeStart, key1, val1, key2, val2, CompositeEnd, AssignEnd
	if len(events) != 8 {
		t.Fatalf("expected 8 events, got %d: %v", len(events), events)
	}
}

// ---------------------------------------------------------------------------
// Duplicate root name → error
// ---------------------------------------------------------------------------

func TestDecodeDuplicateRootName(t *testing.T) {
	input := "name:str = 'a'\nname:str = 'b'"
	d := NewDecoder(strings.NewReader(input))
	// Read first assignment (3 events).
	for i := 0; i < 3; i++ {
		_, err := d.Decode()
		if err != nil {
			t.Fatalf("unexpected error reading first assignment: %v", err)
		}
	}
	// Next decode should fail.
	_, err := d.Decode()
	if err == nil {
		t.Fatal("expected error for duplicate root name")
	}
	if !strings.Contains(err.Error(), "duplicate") {
		t.Fatalf("error should mention 'duplicate': %v", err)
	}
}

// ---------------------------------------------------------------------------
// Empty document → immediate io.EOF
// ---------------------------------------------------------------------------

func TestDecodeEmptyDocument(t *testing.T) {
	d := NewDecoder(strings.NewReader(""))
	_, err := d.Decode()
	if err != io.EOF {
		t.Fatalf("expected io.EOF, got %v", err)
	}
}

func TestDecodeWhitespaceOnly(t *testing.T) {
	d := NewDecoder(strings.NewReader("   \n\n  \t  "))
	_, err := d.Decode()
	if err != io.EOF {
		t.Fatalf("expected io.EOF, got %v", err)
	}
}

func TestDecodeCommentOnly(t *testing.T) {
	d := NewDecoder(strings.NewReader("# just a comment\n"))
	_, err := d.Decode()
	if err != io.EOF {
		t.Fatalf("expected io.EOF, got %v", err)
	}
}

// ---------------------------------------------------------------------------
// Comments
// ---------------------------------------------------------------------------

func TestDecodeWithComments(t *testing.T) {
	input := `# header comment
name:str = 'hello' # inline comment
# another comment
count:int = 42`
	events := decodeAll(t, input)
	if len(events) != 6 {
		t.Fatalf("expected 6 events, got %d: %v", len(events), events)
	}
	if events[0].Name != "name" || events[3].Name != "count" {
		t.Fatalf("names: %q, %q", events[0].Name, events[3].Name)
	}
}

// ---------------------------------------------------------------------------
// Block vs inline equivalence
// ---------------------------------------------------------------------------

func TestDecodeBlockInlineEquivalence(t *testing.T) {
	inline := "deploy:{level:str, release:int} = { 'platform', 26 }"
	block := "deploy:{level:str, release:int} = {\n'platform'\n26\n}"

	inlineEvents := decodeAll(t, inline)
	blockEvents := decodeAll(t, block)

	if len(inlineEvents) != len(blockEvents) {
		t.Fatalf("event count mismatch: inline=%d, block=%d", len(inlineEvents), len(blockEvents))
	}
	for i := range inlineEvents {
		if inlineEvents[i].Kind != blockEvents[i].Kind {
			t.Errorf("event[%d] kind: inline=%s, block=%s", i, inlineEvents[i].Kind, blockEvents[i].Kind)
		}
		if inlineEvents[i].Name != blockEvents[i].Name {
			t.Errorf("event[%d] name: inline=%q, block=%q", i, inlineEvents[i].Name, blockEvents[i].Name)
		}
		if inlineEvents[i].Value != blockEvents[i].Value {
			t.Errorf("event[%d] value: inline=%q, block=%q", i, inlineEvents[i].Value, blockEvents[i].Value)
		}
		if inlineEvents[i].Type != blockEvents[i].Type {
			t.Errorf("event[%d] type: inline=%q, block=%q", i, inlineEvents[i].Type, blockEvents[i].Type)
		}
	}
}

func TestDecodeTupleBlockInlineEquivalence(t *testing.T) {
	inline := "version:(int, int, int) = (3, 45, 5678)"
	block := "version:(int, int, int) = (\n3\n45\n5678\n)"

	inlineEvents := decodeAll(t, inline)
	blockEvents := decodeAll(t, block)

	if len(inlineEvents) != len(blockEvents) {
		t.Fatalf("event count mismatch: inline=%d, block=%d", len(inlineEvents), len(blockEvents))
	}
	for i := range inlineEvents {
		if inlineEvents[i].Kind != blockEvents[i].Kind {
			t.Errorf("event[%d] kind: inline=%s, block=%s", i, inlineEvents[i].Kind, blockEvents[i].Kind)
		}
		if inlineEvents[i].Value != blockEvents[i].Value {
			t.Errorf("event[%d] value: inline=%q, block=%q", i, inlineEvents[i].Value, blockEvents[i].Value)
		}
	}
}

// ---------------------------------------------------------------------------
// Full realistic document
// ---------------------------------------------------------------------------

func TestDecodeRealisticDocument(t *testing.T) {
	input := `# Application config
name:str = 'midwatch'
version:(int, int, int) = (1, 0, 0)
env:|dev, staging, prod| = prod
server:{host:str, port:int} = {
  'localhost'
  8080
}
tags:[str] = ['web', 'api', 'v1']
headers:<str = str> = <
  'Content-Type' = 'application/json'
  'Accept' = '*/*'
>
`
	events := decodeAll(t, input)

	// Count AssignStart events.
	starts := 0
	for _, ev := range events {
		if ev.Kind == EventAssignStart {
			starts++
		}
	}
	if starts != 6 {
		t.Fatalf("expected 6 assignments, got %d", starts)
	}

	// Verify all assignment names.
	expectedNames := []string{"name", "version", "env", "server", "tags", "headers"}
	nameIdx := 0
	for _, ev := range events {
		if ev.Kind == EventAssignStart {
			if ev.Name != expectedNames[nameIdx] {
				t.Errorf("assignment %d: name=%q, want %q", nameIdx, ev.Name, expectedNames[nameIdx])
			}
			nameIdx++
		}
	}
}

// ---------------------------------------------------------------------------
// Nullable assignment
// ---------------------------------------------------------------------------

func TestDecodeNullableScalar(t *testing.T) {
	input := "opt:str? = nil"
	events := decodeAll(t, input)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "nil" {
		t.Fatalf("value = %q, want %q", events[1].Value, "nil")
	}
	if events[1].Type != "str?" {
		t.Fatalf("type = %q, want %q", events[1].Type, "str?")
	}
}

func TestDecodeNullableWithValue(t *testing.T) {
	input := "opt:str? = 'hello'"
	events := decodeAll(t, input)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d", len(events))
	}
	if events[1].Value != "hello" {
		t.Fatalf("value = %q, want %q", events[1].Value, "hello")
	}
}

// ---------------------------------------------------------------------------
// Event stream verification
// ---------------------------------------------------------------------------

func TestDecodeEventStream(t *testing.T) {
	input := "point:{x:int, y:int} = { 10, 20 }"
	events := decodeAll(t, input)

	expected := []struct {
		kind  EventKind
		name  string
		typ   string
		value string
	}{
		{EventAssignStart, "point", "{x:int, y:int}", ""},
		{EventCompositeStart, "point", "{x:int, y:int}", ""},
		{EventScalarValue, "x", "int", "10"},
		{EventScalarValue, "y", "int", "20"},
		{EventCompositeEnd, "", "{x:int, y:int}", ""},
		{EventAssignEnd, "point", "{x:int, y:int}", ""},
	}

	if len(events) != len(expected) {
		t.Fatalf("expected %d events, got %d:\n%v", len(expected), len(events), events)
	}
	for i, exp := range expected {
		ev := events[i]
		if ev.Kind != exp.kind {
			t.Errorf("event[%d]: kind=%s, want %s", i, ev.Kind, exp.kind)
		}
		if exp.name != "" && ev.Name != exp.name {
			t.Errorf("event[%d]: name=%q, want %q", i, ev.Name, exp.name)
		}
		if ev.Type != exp.typ {
			t.Errorf("event[%d]: type=%q, want %q", i, ev.Type, exp.typ)
		}
		if ev.Value != exp.value {
			t.Errorf("event[%d]: value=%q, want %q", i, ev.Value, exp.value)
		}
	}
}

// ---------------------------------------------------------------------------
// Atom assignment
// ---------------------------------------------------------------------------

func TestDecodeAtomAssignment(t *testing.T) {
	input := "status:|active, inactive| = active"
	events := decodeAll(t, input)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "active" {
		t.Fatalf("value = %q, want %q", events[1].Value, "active")
	}
}

// ---------------------------------------------------------------------------
// Decoder EOF behavior
// ---------------------------------------------------------------------------

func TestDecodeMultipleEOF(t *testing.T) {
	d := NewDecoder(strings.NewReader("x:int = 1"))
	// Drain all events.
	for {
		_, err := d.Decode()
		if err == io.EOF {
			break
		}
		if err != nil {
			t.Fatal(err)
		}
	}
	// Additional calls should return EOF.
	for i := 0; i < 3; i++ {
		_, err := d.Decode()
		if err != io.EOF {
			t.Fatalf("call %d: expected io.EOF, got %v", i, err)
		}
	}
}

// ---------------------------------------------------------------------------
// No whitespace around =
// ---------------------------------------------------------------------------

func TestDecodeNoWhitespaceAroundEquals(t *testing.T) {
	events := decodeAll(t, "x:int=42")
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d", len(events))
	}
	if events[1].Value != "42" {
		t.Fatalf("value = %q", events[1].Value)
	}
}

// ---------------------------------------------------------------------------
// BOM handling through decoder
// ---------------------------------------------------------------------------

func TestDecodeWithBOM(t *testing.T) {
	input := "\xEF\xBB\xBFname:str = 'hello'"
	events := decodeAll(t, input)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d", len(events))
	}
}
