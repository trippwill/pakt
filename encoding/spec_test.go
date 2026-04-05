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
meta:<str ; str>
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
nested:{items:[<str ; {x:int, y:int}>], count:int} = {
    <
        'alpha' ; { 10, 20 }
        'beta'  = { 30, 40 }
    >
    2
}
wanted:str = 'found'`
	spec := "simple:int\nwanted:str"
	events := decodeAllWithSpec(t, doc, spec)
	// simple: 3, wanted: 3 ; 6
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
	doc := "level:|dev, staging, prod| = |prod\ncount:int = 3"
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
	doc := `meta:<str ; str> = <
    'owner' ; 'team'
    'region' ; 'us-east'
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
	defer func() { _ = specFile.Close() }()

	docFile, err := os.Open("../testdata/valid/full.pakt")
	if err != nil {
		t.Fatalf("cannot open full.pakt: %v", err)
	}
	defer func() { _ = docFile.Close() }()

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
meta:<str ; str> = <>
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

// ---------------------------------------------------------------------------
// skipCompositeInner — deeply nested composites
// ---------------------------------------------------------------------------

func TestProjectionSkipTupleWithAllInnerTypes(t *testing.T) {
	// Tuple containing struct, list, map — exercises
	// skipComposite('(', ')') hitting '{', '[', '<' and comments.
	doc := `data:(int, {x:int, y:int}, [int], <str ; int>) = (
    1
    # comment inside tuple
    { 10, 20 }
    [1, 2]
    <'a' ; 5>
)
wanted:int = 99`
	spec := "wanted:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "99" {
		t.Fatalf("expected '99', got %q", events[1].Value)
	}
}

func TestProjectionSkipListWithAllInnerTypes(t *testing.T) {
	// List containing struct with tuple and map inside — exercises
	// skipComposite('[', ']') hitting '{' → skipCompositeInner,
	// then '(' and '<' within inner.
	doc := `data:[{a:int, b:(int, int), c:<str ; int>}] = [
    # comment inside list
    {
        1
        (2, 3)
        <'k' ; 4>
    }
]
wanted:int = 88`
	spec := "wanted:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "88" {
		t.Fatalf("expected '88', got %q", events[1].Value)
	}
}

func TestProjectionSkipMapWithAllInnerTypes(t *testing.T) {
	// Map containing struct values with tuple and list — exercises
	// skipComposite('<', '>') hitting '{' → skipCompositeInner,
	// then '(' and '[' within inner.
	doc := `data:<str ; {a:int, b:(int, int), c:[int]}> = <
    # comment inside map
    'key' ; {
        1
        (2, 3)
        [4, 5]
    }
>
wanted:int = 77`
	spec := "wanted:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "77" {
		t.Fatalf("expected '77', got %q", events[1].Value)
	}
}

func TestProjectionSkipDeeplyNestedFiveLevels(t *testing.T) {
	// 5 levels: struct → list → map → struct → tuple
	// Exercises skipCompositeInner recursively with all delimiter types.
	doc := `deep:{items:[<str ; {point:(int, int)}>]} = {
    [
        <
            'alpha' ; { (10, 20) }
            'beta'  = { (30, 40) }
        >
    ]
}
wanted:int = 1`
	spec := "wanted:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "1" {
		t.Fatalf("expected '1', got %q", events[1].Value)
	}
}

func TestProjectionSkipMixedCompositesAtSameLevel(t *testing.T) {
	// Struct containing a list, map, and tuple at the same level.
	doc := `server:{ports:[int], labels:<str ; str>, version:(int, int, int)} = {
    [8080, 8443]
    <'env' ; 'prod'>
    (1, 2, 3)
}
wanted:int = 1`
	spec := "wanted:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "1" {
		t.Fatalf("expected '1', got %q", events[1].Value)
	}
}

func TestProjectionSkipInnerCompositeWithStringsAndComments(t *testing.T) {
	// Struct → list → map → struct with strings containing delimiters
	// and comments inside skipCompositeInner paths.
	doc := `deep:{items:[<str ; {point:(int, int)}>]} = {
    [
        <
            'key with {brackets} and [more] and (parens) and <angles>' ; {
                # comment with > and ) and ] delimiters
                (10, 20)
            }
        >
    ]
}
wanted:int = 1`
	spec := "wanted:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "1" {
		t.Fatalf("expected '1', got %q", events[1].Value)
	}
}

// ---------------------------------------------------------------------------
// skipTripleQuotedString edge cases
// ---------------------------------------------------------------------------

func TestProjectionSkipTripleQuotedWithEmbeddedQuote(t *testing.T) {
	// Triple-quoted string containing the quote character inside.
	doc := "msg:str = '''\nit's a test\n'''\nwanted:int = 1"
	spec := "wanted:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "1" {
		t.Fatalf("expected '1', got %q", events[1].Value)
	}
}

func TestProjectionSkipTripleQuotedWithEscapedBackslash(t *testing.T) {
	// Triple-quoted with backslash-escaped backslash before the closing quotes.
	doc := "msg:str = '''\nline\\\\\n'''\nwanted:int = 2"
	spec := "wanted:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "2" {
		t.Fatalf("expected '2', got %q", events[1].Value)
	}
}

func TestProjectionSkipTripleQuotedWithEscapedQuoteBeforeClose(t *testing.T) {
	// Backslash-quote inside triple-quoted — the \' should not start closing.
	doc := "msg:str = '''\ndon\\'t stop\n'''\nwanted:int = 3"
	spec := "wanted:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "3" {
		t.Fatalf("expected '3', got %q", events[1].Value)
	}
}

func TestProjectionSkipTripleDoubleQuotedWithEmbeddedQuotes(t *testing.T) {
	// Triple double-quoted string containing a double quote inside.
	doc := "msg:str = \"\"\"\nhello \"world\"\n\"\"\"\nwanted:int = 4"
	spec := "wanted:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "4" {
		t.Fatalf("expected '4', got %q", events[1].Value)
	}
}

func TestProjectionSkipEmptyTripleQuotedString(t *testing.T) {
	doc := "msg:str = '''\n'''\nwanted:int = 5"
	spec := "wanted:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "5" {
		t.Fatalf("expected '5', got %q", events[1].Value)
	}
}

func TestProjectionSkipTripleQuotedWithTwoConsecutiveQuotesThenOther(t *testing.T) {
	// Two consecutive quotes that don't form a closing triple.
	doc := "msg:str = '''\nab''cd\n'''\nwanted:int = 6"
	spec := "wanted:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "6" {
		t.Fatalf("expected '6', got %q", events[1].Value)
	}
}

// ---------------------------------------------------------------------------
// skipComposite with strings containing delimiters
// ---------------------------------------------------------------------------

func TestProjectionSkipCompositeWithAllDelimitersInString(t *testing.T) {
	// String value containing all delimiter characters.
	doc := `greeting:str = 'hello {world} [foo] (bar) <baz>'
count:int = 10`
	spec := "count:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "10" {
		t.Fatalf("expected '10', got %q", events[1].Value)
	}
}

func TestProjectionSkipMultiLineStringInSkippedStruct(t *testing.T) {
	// Triple-quoted string inside a struct that is being skipped.
	doc := "config:{msg:str, n:int} = {\n    '''\n    hello\n    world\n    '''\n    5\n}\nwanted:int = 9"
	spec := "wanted:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "9" {
		t.Fatalf("expected '9', got %q", events[1].Value)
	}
}

func TestProjectionSkipMapWithEqualsInStringValues(t *testing.T) {
	// Map where string values contain '=' signs.
	doc := `env:<str ; str> = <
    'PATH' ; '/usr/bin=/usr/local/bin'
    'OPTS' ; '--key=value --flag=true'
>
wanted:int = 5`
	spec := "wanted:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "5" {
		t.Fatalf("expected '5', got %q", events[1].Value)
	}
}

func TestProjectionSkipNestedCompositeWithDelimiterStrings(t *testing.T) {
	// Inside a list (inner composite), strings with all delimiter chars.
	doc := `data:{items:[str]} = {
    ['hello } world { and [more] (stuff) <angles>']
}
wanted:int = 7`
	spec := "wanted:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "7" {
		t.Fatalf("expected '7', got %q", events[1].Value)
	}
}

// ---------------------------------------------------------------------------
// skipValue for all scalar skip paths
// ---------------------------------------------------------------------------

func TestProjectionSkipFalseValue(t *testing.T) {
	// Specifically skip 'false' to cover the b == 'f' branch in skipValue.
	doc := "flag:bool = false\nwanted:int = 11"
	spec := "wanted:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "11" {
		t.Fatalf("expected '11', got %q", events[1].Value)
	}
}

func TestProjectionSkipAllScalarTypes(t *testing.T) {
	// Skip every scalar type in one document to exercise all skipValue paths.
	doc := `flag-t:bool = true
flag-f:bool = false
nothing:str? = nil
neg:int = -42
id:uuid = 550e8400-e29b-41d4-a716-446655440000
d:date = 2026-01-15
t:time = 14:30:00Z
dt:datetime = 2026-06-01T14:30:00Z
level:|dev, staging, prod| = |staging
wanted:int = 100`
	spec := "wanted:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "100" {
		t.Fatalf("expected '100', got %q", events[1].Value)
	}
}

// ---------------------------------------------------------------------------
// Projection with complex documents
// ---------------------------------------------------------------------------

func TestProjectionFirstFieldSkippedSecondCaptured(t *testing.T) {
	// The first field is skipped (deeply nested), second is captured.
	doc := `complex:{items:[<str ; {n:int}>]} = {
    [
        <
            'key' ; { 42 }
        >
    ]
}
wanted:str = 'captured'`
	spec := "wanted:str"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[0].Kind != EventAssignStart || events[0].Name != "wanted" {
		t.Fatalf("expected AssignStart for 'wanted', got %v", events[0])
	}
	if events[1].Value != "captured" {
		t.Fatalf("expected 'captured', got %q", events[1].Value)
	}
}

func TestProjectionComplexDocWithNestedDelimiterStrings(t *testing.T) {
	// Skipped field has deeply nested composites with strings containing
	// all delimiter types; the second field is captured.
	doc := `config:{servers:[<str ; {desc:str, port:int}>]} = {
    [
        <
            'prod' ; {
                'server {prod} on port [443] via (tls) at <edge>'
                443
            }
            'staging' ; {
                'server {staging} on port [8443]'
                8443
            }
        >
    ]
}
result:str = 'ok'`
	spec := "result:str"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "ok" {
		t.Fatalf("expected 'ok', got %q", events[1].Value)
	}
}

func TestProjectionSkipMultipleComplexFieldsCaptureMiddle(t *testing.T) {
	// First and last fields are skipped; only the middle field is captured.
	doc := `before:{items:[int]} = {
    [1, 2, 3]
}
wanted:str = 'middle'
after:<str ; (int, int)> = <
    'a' ; (1, 2)
    'b' ; (3, 4)
>`
	spec := "wanted:str"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "middle" {
		t.Fatalf("expected 'middle', got %q", events[1].Value)
	}
}

func TestProjectionSkipTripleQuotedInsideNestedComposite(t *testing.T) {
	// Triple-quoted string inside a nested composite being skipped.
	doc := "data:{items:[str]} = {\n    [\n        '''\n        multi-line with 'quotes' inside\n        '''\n    ]\n}\nwanted:int = 42"
	spec := "wanted:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "42" {
		t.Fatalf("expected '42', got %q", events[1].Value)
	}
}

func TestProjectionSkipCommentsWithDelimitersInNestedComposite(t *testing.T) {
	// Comments containing delimiter chars inside nested composites.
	doc := `data:{items:[{n:int}]} = {
    [
        {
            # comment: } ] > ) won't close anything
            42
        }
    ]
}
wanted:int = 1`
	spec := "wanted:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "1" {
		t.Fatalf("expected '1', got %q", events[1].Value)
	}
}

// ---------------------------------------------------------------------------
// Nested same-type delimiters (covers depth++ in skipComposite/Inner)
// ---------------------------------------------------------------------------

func TestProjectionSkipNestedSameTypeList(t *testing.T) {
	// List of lists — skipComposite('[', ']') sees inner '[' → depth++.
	doc := `data:[[int]] = [[1, 2], [3, 4]]
wanted:int = 1`
	spec := "wanted:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "1" {
		t.Fatalf("expected '1', got %q", events[1].Value)
	}
}

func TestProjectionSkipNestedSameTypeInInnerComposite(t *testing.T) {
	// Struct containing list of lists — skipCompositeInner('[',']')
	// sees another '[' → depth++ inside skipCompositeInner.
	doc := `data:{matrix:[[int]]} = {
    [[1, 2], [3, 4]]
}
wanted:int = 1`
	spec := "wanted:int"
	events := decodeAllWithSpec(t, doc, spec)
	if len(events) != 3 {
		t.Fatalf("expected 3 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "1" {
		t.Fatalf("expected '1', got %q", events[1].Value)
	}
}

// ---------------------------------------------------------------------------
// Error paths for skip functions (unterminated values)
// ---------------------------------------------------------------------------

func TestProjectionSkipUnterminatedComposite(t *testing.T) {
	doc := "data:[int] = [1, 2"
	spec := "wanted:int"
	err := decodeExpectErrorWithSpec(t, doc, spec)
	if err == nil {
		t.Fatal("expected error for unterminated composite")
	}
}

func TestProjectionSkipUnterminatedInnerComposite(t *testing.T) {
	// Struct containing an unterminated list — triggers error return
	// from skipCompositeInner propagated through skipComposite.
	doc := "data:{items:[int]} = { [1, 2"
	spec := "wanted:int"
	err := decodeExpectErrorWithSpec(t, doc, spec)
	if err == nil {
		t.Fatal("expected error for unterminated inner composite")
	}
}

func TestProjectionSkipUnterminatedDeeplyNestedInner(t *testing.T) {
	// Struct → list → struct (unterminated) — triggers error return
	// from skipCompositeInner recursive call.
	doc := "data:{items:[{n:int}]} = { [{ 42"
	spec := "wanted:int"
	err := decodeExpectErrorWithSpec(t, doc, spec)
	if err == nil {
		t.Fatal("expected error for unterminated deeply nested composite")
	}
}

func TestProjectionSkipUnterminatedString(t *testing.T) {
	doc := "data:str = 'unterminated"
	spec := "wanted:int"
	err := decodeExpectErrorWithSpec(t, doc, spec)
	if err == nil {
		t.Fatal("expected error for unterminated string")
	}
}

func TestProjectionSkipUnterminatedStringEscape(t *testing.T) {
	doc := "data:str = 'test\\"
	spec := "wanted:int"
	err := decodeExpectErrorWithSpec(t, doc, spec)
	if err == nil {
		t.Fatal("expected error for unterminated escape in string")
	}
}

func TestProjectionSkipUnterminatedTripleQuoted(t *testing.T) {
	doc := "data:str = '''unterminated content"
	spec := "wanted:int"
	err := decodeExpectErrorWithSpec(t, doc, spec)
	if err == nil {
		t.Fatal("expected error for unterminated triple-quoted string")
	}
}

func TestProjectionSkipUnterminatedTripleQuotedEscape(t *testing.T) {
	doc := "data:str = '''content\\"
	spec := "wanted:int"
	err := decodeExpectErrorWithSpec(t, doc, spec)
	if err == nil {
		t.Fatal("expected error for unterminated escape in triple-quoted string")
	}
}

func TestProjectionSkipUnterminatedStringInComposite(t *testing.T) {
	// String error inside skipComposite — covers return err from skipString.
	doc := "data:{msg:str} = { 'unterminated"
	spec := "wanted:int"
	err := decodeExpectErrorWithSpec(t, doc, spec)
	if err == nil {
		t.Fatal("expected error for unterminated string in composite")
	}
}

func TestProjectionSkipUnterminatedStringInInnerComposite(t *testing.T) {
	// String error inside skipCompositeInner — covers return err from
	// skipString within the inner composite path.
	doc := "data:{items:[str]} = { ['unterminated"
	spec := "wanted:int"
	err := decodeExpectErrorWithSpec(t, doc, spec)
	if err == nil {
		t.Fatal("expected error for unterminated string in inner composite")
	}
}
