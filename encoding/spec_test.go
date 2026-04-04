package encoding

import (
	"io"
	"os"
	"strings"
	"testing"
)

// ---------------------------------------------------------------------------
// ParseSpec tests
// ---------------------------------------------------------------------------

func TestParseSpecSimple(t *testing.T) {
	spec, err := ParseSpec(strings.NewReader("name:str\ncount:int"))
	if err != nil {
		t.Fatalf("ParseSpec: %v", err)
	}
	if len(spec.Fields) != 2 {
		t.Fatalf("expected 2 fields, got %d", len(spec.Fields))
	}
	if spec.Fields["name"].Scalar == nil || *spec.Fields["name"].Scalar != TypeStr {
		t.Fatalf("expected name:str, got %v", spec.Fields["name"])
	}
	if spec.Fields["count"].Scalar == nil || *spec.Fields["count"].Scalar != TypeInt {
		t.Fatalf("expected count:int, got %v", spec.Fields["count"])
	}
}

func TestParseSpecCompositeTypes(t *testing.T) {
	spec, err := ParseSpec(strings.NewReader(
		"deploy:{level:|dev, staging, prod|, date:date}"))
	if err != nil {
		t.Fatalf("ParseSpec: %v", err)
	}
	if len(spec.Fields) != 1 {
		t.Fatalf("expected 1 field, got %d", len(spec.Fields))
	}
	dt := spec.Fields["deploy"]
	if dt.Struct == nil {
		t.Fatalf("expected struct type for deploy")
	}
	if len(dt.Struct.Fields) != 2 {
		t.Fatalf("expected 2 struct fields, got %d", len(dt.Struct.Fields))
	}
	if dt.Struct.Fields[0].Name != "level" || dt.Struct.Fields[0].Type.AtomSet == nil {
		t.Fatalf("expected field level:|dev, staging, prod|, got %v", dt.Struct.Fields[0])
	}
	if dt.Struct.Fields[1].Name != "date" || dt.Struct.Fields[1].Type.Scalar == nil {
		t.Fatalf("expected field date:date, got %v", dt.Struct.Fields[1])
	}
}

func TestParseSpecAllTypeForms(t *testing.T) {
	input := `name:str
count:int
ratio:dec
rate:float
active:bool
id:uuid
created:date
started:time
updated:datetime
level:|dev, staging, prod|
config:{host:str, port:int}
version:(int, int, int)
tags:[str]
meta:<str = str>
nickname:str?
`
	spec, err := ParseSpec(strings.NewReader(input))
	if err != nil {
		t.Fatalf("ParseSpec: %v", err)
	}
	if len(spec.Fields) != 15 {
		t.Fatalf("expected 15 fields, got %d", len(spec.Fields))
	}
	// Spot-check a few
	if spec.Fields["version"].Tuple == nil {
		t.Fatal("expected tuple type for version")
	}
	if spec.Fields["tags"].List == nil {
		t.Fatal("expected list type for tags")
	}
	if spec.Fields["meta"].Map == nil {
		t.Fatal("expected map type for meta")
	}
	if !spec.Fields["nickname"].Nullable {
		t.Fatal("expected nickname to be nullable")
	}
}

func TestParseSpecDuplicateNameError(t *testing.T) {
	_, err := ParseSpec(strings.NewReader("name:str\nname:int"))
	if err == nil {
		t.Fatal("expected error for duplicate name")
	}
	if !strings.Contains(err.Error(), "duplicate") {
		t.Fatalf("expected duplicate error, got: %v", err)
	}
}

func TestParseSpecEmpty(t *testing.T) {
	spec, err := ParseSpec(strings.NewReader(""))
	if err != nil {
		t.Fatalf("ParseSpec: %v", err)
	}
	if len(spec.Fields) != 0 {
		t.Fatalf("expected 0 fields, got %d", len(spec.Fields))
	}
}

func TestParseSpecWithComments(t *testing.T) {
	input := `# This is a spec file
name:str
# counts things
count:int
`
	spec, err := ParseSpec(strings.NewReader(input))
	if err != nil {
		t.Fatalf("ParseSpec: %v", err)
	}
	if len(spec.Fields) != 2 {
		t.Fatalf("expected 2 fields, got %d", len(spec.Fields))
	}
}

// ---------------------------------------------------------------------------
// Projection tests (via Decoder)
// ---------------------------------------------------------------------------

// decodeAllWithSpec is a test helper that creates a decoder with a spec and
// collects all events.
func decodeAllWithSpec(t *testing.T, doc, specDoc string) []Event {
	t.Helper()
	d := NewDecoder(strings.NewReader(doc))
	if err := d.SetSpec(strings.NewReader(specDoc)); err != nil {
		t.Fatalf("SetSpec: %v", err)
	}
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

func decodeExpectErrorWithSpec(t *testing.T, doc, specDoc string) error {
	t.Helper()
	d := NewDecoder(strings.NewReader(doc))
	if err := d.SetSpec(strings.NewReader(specDoc)); err != nil {
		return err
	}
	for {
		_, err := d.Decode()
		if err == io.EOF {
			t.Fatal("expected error but got EOF")
		}
		if err != nil {
			return err
		}
	}
}

func TestProjectionAllFieldsMatch(t *testing.T) {
	doc := "name:str = 'hello'\ncount:int = 42"
	spec := "name:str\ncount:int"
	events := decodeAllWithSpec(t, doc, spec)
	// 3 events per assignment (start, value, end) = 6
	if len(events) != 6 {
		t.Fatalf("expected 6 events, got %d: %v", len(events), events)
	}
	if events[0].Kind != EventAssignStart || events[0].Name != "name" {
		t.Fatalf("event[0] = %v", events[0])
	}
	if events[1].Kind != EventScalarValue || events[1].Value != "hello" {
		t.Fatalf("event[1] = %v", events[1])
	}
	if events[3].Kind != EventAssignStart || events[3].Name != "count" {
		t.Fatalf("event[3] = %v", events[3])
	}
	if events[4].Kind != EventScalarValue || events[4].Value != "42" {
		t.Fatalf("event[4] = %v", events[4])
	}
}

func TestProjectionSubsetFields(t *testing.T) {
	doc := "name:str = 'hello'\ncount:int = 42\nactive:bool = true"
	spec := "count:int"
	events := decodeAllWithSpec(t, doc, spec)
	// Only count field emitted: 3 events
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[0].Kind != EventAssignStart || events[0].Name != "count" {
		t.Fatalf("event[0] = %v", events[0])
	}
	if events[1].Kind != EventScalarValue || events[1].Value != "42" {
		t.Fatalf("event[1] = %v", events[1])
	}
}

func TestProjectionMissingFieldError(t *testing.T) {
	doc := "name:str = 'hello'"
	spec := "name:str\ncount:int"
	err := decodeExpectErrorWithSpec(t, doc, spec)
	if err == nil {
		t.Fatal("expected error for missing spec field")
	}
	if !strings.Contains(err.Error(), "count") {
		t.Fatalf("expected error about 'count', got: %v", err)
	}
}

func TestProjectionSkipComplexComposite(t *testing.T) {
	doc := `name:str = 'hello'
config:{host:str, port:int, tags:[str]} = {
    'localhost'
    8080
    ['a', 'b', 'c']
}
count:int = 99`
	spec := "name:str\ncount:int"
	events := decodeAllWithSpec(t, doc, spec)
	// name: 3 events, count: 3 events = 6
	if len(events) != 6 {
		t.Fatalf("expected 6 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "hello" {
		t.Fatalf("expected 'hello', got %q", events[1].Value)
	}
	if events[4].Value != "99" {
		t.Fatalf("expected '99', got %q", events[4].Value)
	}
}

func TestProjectionSkipStringWithDelimiters(t *testing.T) {
	doc := `greeting:str = 'hello { world }'
count:int = 5`
	spec := "count:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "5" {
		t.Fatalf("expected '5', got %q", events[1].Value)
	}
}

func TestProjectionSkipMultiLineString(t *testing.T) {
	doc := "msg:str = '''\n    hello\n    world\n    '''\ncount:int = 7"
	spec := "count:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "7" {
		t.Fatalf("expected '7', got %q", events[1].Value)
	}
}

func TestProjectionSkipNestedComposites(t *testing.T) {
	doc := `simple:int = 1
nested:{items:[<str = {x:int, y:int}>], count:int} = {
    <
        'alpha' = { 10, 20 }
        'beta'  = { 30, 40 }
    >
    2
}
wanted:str = 'found'`
	spec := "simple:int\nwanted:str"
	events := decodeAllWithSpec(t, doc, spec)
	// simple: 3, wanted: 3 = 6
	if len(events) != 6 {
		t.Fatalf("expected 6 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "1" {
		t.Fatalf("expected '1', got %q", events[1].Value)
	}
	if events[4].Value != "found" {
		t.Fatalf("expected 'found', got %q", events[4].Value)
	}
}

func TestProjectionSkipAtomValue(t *testing.T) {
	doc := "level:|dev, staging, prod| = prod\ncount:int = 3"
	spec := "count:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "3" {
		t.Fatalf("expected '3', got %q", events[1].Value)
	}
}

func TestProjectionSkipBoolAndNil(t *testing.T) {
	doc := "active:bool = true\nmaybe:str? = nil\ncount:int = 10"
	spec := "count:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "10" {
		t.Fatalf("expected '10', got %q", events[1].Value)
	}
}

func TestProjectionSkipUUID(t *testing.T) {
	doc := "id:uuid = 550e8400-e29b-41d4-a716-446655440000\nname:str = 'test'"
	spec := "name:str"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "test" {
		t.Fatalf("expected 'test', got %q", events[1].Value)
	}
}

func TestProjectionSkipDateTimeValues(t *testing.T) {
	doc := "started:datetime = 2026-06-01T14:30:00Z\ncount:int = 1"
	spec := "count:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "1" {
		t.Fatalf("expected '1', got %q", events[1].Value)
	}
}

func TestProjectionSkipNegativeNumber(t *testing.T) {
	doc := "offset:int = -42\nname:str = 'ok'"
	spec := "name:str"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "ok" {
		t.Fatalf("expected 'ok', got %q", events[1].Value)
	}
}

func TestProjectionSkipMapValue(t *testing.T) {
	doc := `meta:<str = str> = <
    'owner' = 'team'
    'region' = 'us-east'
>
count:int = 5`
	spec := "count:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "5" {
		t.Fatalf("expected '5', got %q", events[1].Value)
	}
}

func TestProjectionSkipTupleValue(t *testing.T) {
	doc := "version:(int, int, int) = (2, 14, 0)\nname:str = 'app'"
	spec := "name:str"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "app" {
		t.Fatalf("expected 'app', got %q", events[1].Value)
	}
}

func TestProjectionSkipListValue(t *testing.T) {
	doc := `features:[str] = ['dark-mode', 'notifications']
count:int = 2`
	spec := "count:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "2" {
		t.Fatalf("expected '2', got %q", events[1].Value)
	}
}

func TestProjectionSkipStringWithEscapedQuotes(t *testing.T) {
	doc := `msg:str = 'it\'s a \"test\"'
count:int = 1`
	spec := "count:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "1" {
		t.Fatalf("expected '1', got %q", events[1].Value)
	}
}

func TestProjectionSkipStringWithEscapedBackslash(t *testing.T) {
	doc := `path:str = 'C:\\'
count:int = 1`
	spec := "count:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "1" {
		t.Fatalf("expected '1', got %q", events[1].Value)
	}
}

func TestProjectionEmptySpec(t *testing.T) {
	doc := "name:str = 'hello'\ncount:int = 42"
	spec := ""
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 0 {
		t.Fatalf("expected 0 events with empty spec, got %d: %v", len(events), events)
	}
}

func TestProjectionWithComments(t *testing.T) {
	doc := `# header comment
name:str = 'hello'  # inline
count:int = 42`
	spec := "name:str\ncount:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 6 {
		t.Fatalf("expected 6 events, got %d: %v", len(events), events)
	}
}

func TestProjectionSkipCompositeWithStringContainingDelimiters(t *testing.T) {
	doc := `config:{msg:str, level:int} = { 'hello } world { foo', 5 }
count:int = 3`
	spec := "count:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "3" {
		t.Fatalf("expected '3', got %q", events[1].Value)
	}
}

// ---------------------------------------------------------------------------
// Integration test with test data files
// ---------------------------------------------------------------------------

func TestProjectionIntegrationWithTestData(t *testing.T) {
	specFile, err := os.Open("../testdata/valid/spec-example.spec.pakt")
	if err != nil {
		t.Skipf("skipping integration test: %v", err)
	}
	defer specFile.Close()

	docFile, err := os.Open("../testdata/valid/full.pakt")
	if err != nil {
		t.Fatalf("cannot open full.pakt: %v", err)
	}
	defer docFile.Close()

	d := NewDecoder(docFile)
	if err := d.SetSpec(specFile); err != nil {
		t.Fatalf("SetSpec: %v", err)
	}

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

	// The spec requests deploy and version.
	// Verify that we got events for both.
	foundDeploy := false
	foundVersion := false
	for _, ev := range events {
		if ev.Kind == EventAssignStart && ev.Name == "deploy" {
			foundDeploy = true
		}
		if ev.Kind == EventAssignStart && ev.Name == "version" {
			foundVersion = true
		}
	}
	if !foundDeploy {
		t.Fatal("expected deploy assignment in projected output")
	}
	if !foundVersion {
		t.Fatal("expected version assignment in projected output")
	}

	// Verify no other top-level assignments are present.
	for _, ev := range events {
		if ev.Kind == EventAssignStart {
			if ev.Name != "deploy" && ev.Name != "version" {
				t.Fatalf("unexpected assignment %q in projected output", ev.Name)
			}
		}
	}
}

// ---------------------------------------------------------------------------
// skipValue edge-case tests
// ---------------------------------------------------------------------------

func TestSkipValueHexInt(t *testing.T) {
	doc := "val:int = 0xFF\nname:str = 'ok'"
	spec := "name:str"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d", len(events))
	}
	if events[1].Value != "ok" {
		t.Fatalf("expected 'ok', got %q", events[1].Value)
	}
}

func TestSkipValueDecimal(t *testing.T) {
	doc := "ratio:dec = 3.14\nname:str = 'ok'"
	spec := "name:str"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d", len(events))
	}
}

func TestSkipValueFloat(t *testing.T) {
	doc := "rate:float = 1.5e10\nname:str = 'ok'"
	spec := "name:str"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d", len(events))
	}
}

func TestSkipValueDate(t *testing.T) {
	doc := "d:date = 2026-01-15\nname:str = 'ok'"
	spec := "name:str"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d", len(events))
	}
}

func TestSkipValueTime(t *testing.T) {
	doc := "t:time = 14:30:00Z\nname:str = 'ok'"
	spec := "name:str"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d", len(events))
	}
}

func TestProjectionSkipDoubleQuotedString(t *testing.T) {
	doc := "msg:str = \"hello world\"\ncount:int = 1"
	spec := "count:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "1" {
		t.Fatalf("expected '1', got %q", events[1].Value)
	}
}

func TestProjectionSkipTripleDoubleQuotedString(t *testing.T) {
	doc := "msg:str = \"\"\"\n    hello\n    world\n    \"\"\"\ncount:int = 7"
	spec := "count:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "7" {
		t.Fatalf("expected '7', got %q", events[1].Value)
	}
}

func TestProjectionSkipEmptyComposites(t *testing.T) {
	doc := `items:[str] = []
meta:<str = str> = <>
count:int = 1`
	spec := "count:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "1" {
		t.Fatalf("expected '1', got %q", events[1].Value)
	}
}

func TestProjectionSkipWithInlineComments(t *testing.T) {
	doc := `name:str = 'hello'  # skip this
count:int = 42  # and this`
	spec := "count:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "42" {
		t.Fatalf("expected '42', got %q", events[1].Value)
	}
}

func TestProjectionSkipBlockCompositeWithComments(t *testing.T) {
	doc := `config:{host:str, port:int} = {
    # the host
    'localhost'
    # the port
    8080
}
count:int = 1`
	spec := "count:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
}
