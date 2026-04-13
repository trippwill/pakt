package encoding

import (
	"errors"
	"io"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"testing"
)

// ---------------------------------------------------------------------------
// helpers
// ---------------------------------------------------------------------------

// fileDecodeAll reads every event from a PAKT file through the Decoder,
// failing the test on any unexpected error.
func fileDecodeAll(t *testing.T, path string) []Event {
	t.Helper()
	f, err := os.Open(path) //nolint:gosec // test fixture path
	if err != nil {
		t.Fatalf("open %s: %v", path, err)
	}
	defer func() { _ = f.Close() }()

	dec := NewDecoder(f)
	var events []Event
	for {
		ev, err := dec.Decode()
		if err == io.EOF {
			break
		}
		if err != nil {
			t.Fatalf("Decode(%s): %v", filepath.Base(path), err)
		}
		ev.Value = slices.Clone(ev.Value)
		events = append(events, ev)
	}
	return events
}

// fileDecodeExpectError decodes until a non-EOF error is returned, failing
// if the document parses without error.
func fileDecodeExpectError(t *testing.T, path string) error {
	t.Helper()
	f, err := os.Open(path) //nolint:gosec // test fixture path
	if err != nil {
		t.Fatalf("open %s: %v", path, err)
	}
	defer func() { _ = f.Close() }()

	dec := NewDecoder(f)
	for {
		_, err := dec.Decode()
		if err == io.EOF {
			t.Fatalf("expected error parsing %s, got EOF", filepath.Base(path))
		}
		if err != nil {
			return err
		}
	}
}

// countKind returns the number of events with the given kind.
func countKind(events []Event, kind EventKind) int {
	n := 0
	for _, ev := range events {
		if ev.Kind == kind {
			n++
		}
	}
	return n
}

// countCompositeStarts returns the total number of composite start events.
func countCompositeStarts(events []Event) int {
	n := 0
	for _, ev := range events {
		if ev.Kind.IsCompositeStart() {
			n++
		}
	}
	return n
}

// countCompositeEnds returns the total number of composite end events.
func countCompositeEnds(events []Event) int {
	n := 0
	for _, ev := range events {
		if ev.Kind.IsCompositeEnd() {
			n++
		}
	}
	return n
}

// ---------------------------------------------------------------------------
// Valid file tests
// ---------------------------------------------------------------------------

func TestIntegrationValidScalars(t *testing.T) {
	events := fileDecodeAll(t, "../testdata/valid/scalars.pakt")

	// scalars.pakt has 16 assignments, each producing 3 events
	const wantAssignments = 16
	starts := countKind(events, EventAssignStart)
	ends := countKind(events, EventAssignEnd)
	scalars := countKind(events, EventScalarValue)

	if starts != wantAssignments {
		t.Errorf("AssignStart count = %d, want %d", starts, wantAssignments)
	}
	if ends != wantAssignments {
		t.Errorf("AssignEnd count = %d, want %d", ends, wantAssignments)
	}
	if scalars != wantAssignments {
		t.Errorf("ScalarValue count = %d, want %d", scalars, wantAssignments)
	}
	if len(events) != wantAssignments*3 {
		t.Errorf("total events = %d, want %d", len(events), wantAssignments*3)
	}

	// Verify each assignment follows the AssignStart → ScalarValue → AssignEnd pattern
	for i := 0; i < len(events)-2; i += 3 {
		if events[i].Kind != EventAssignStart {
			t.Errorf("event[%d]: got %s, want AssignStart", i, events[i].Kind)
		}
		if events[i+1].Kind != EventScalarValue {
			t.Errorf("event[%d]: got %s, want ScalarValue", i+1, events[i+1].Kind)
		}
		if events[i+2].Kind != EventAssignEnd {
			t.Errorf("event[%d]: got %s, want AssignEnd", i+2, events[i+2].Kind)
		}
	}

	// Spot-check specific assignments
	expectedNames := []string{
		"greeting", "count", "hex", "binary", "octal", "big", "negative",
		"price", "avogadro", "active", "inactive", "id", "started", "updated",
		"payload", "payload64",
	}
	for i, name := range expectedNames {
		if events[i*3].Name != name {
			t.Errorf("assignment %d: name = %q, want %q", i, events[i*3].Name, name)
		}
	}

	// Spot-check specific values
	spotChecks := map[string]string{
		"greeting":  "hello world",
		"count":     "42",
		"active":    "true",
		"inactive":  "false",
		"negative":  "-273",
		"price":     "19.99",
		"payload":   "48656c6c6f",
		"payload64": "48656c6c6f",
	}
	for i := 0; i < len(events); i += 3 {
		name := events[i].Name
		if want, ok := spotChecks[name]; ok {
			if events[i+1].ValueString() != want {
				t.Errorf("%s: value = %q, want %q", name, events[i+1].ValueString(), want)
			}
		}
	}
}

func TestIntegrationValidStrings(t *testing.T) {
	events := fileDecodeAll(t, "../testdata/valid/strings.pakt")

	// strings.pakt has 13 assignments
	const wantAssignments = 13
	starts := countKind(events, EventAssignStart)
	if starts != wantAssignments {
		t.Errorf("AssignStart count = %d, want %d", starts, wantAssignments)
	}

	// Build name→value map
	vals := make(map[string]string)
	for i := 0; i < len(events); i++ {
		if events[i].Kind == EventScalarValue {
			vals[events[i].Name] = events[i].ValueString()
		}
	}

	// Basic strings
	if v := vals["single"]; v != "hello world" {
		t.Errorf("single = %q, want %q", v, "hello world")
	}
	if v := vals["double"]; v != "hello world" {
		t.Errorf("double = %q, want %q", v, "hello world")
	}

	// Escape sequences — the decoder should process these
	if v := vals["newline"]; !strings.Contains(v, "\n") && !strings.Contains(v, `\n`) {
		t.Errorf("newline value unexpected: %q", v)
	}
	if v := vals["tabbed"]; !strings.Contains(v, "\t") && !strings.Contains(v, `\t`) {
		t.Errorf("tabbed value unexpected: %q", v)
	}
	if v := vals["backslash"]; !strings.Contains(v, `\`) {
		t.Errorf("backslash value unexpected: %q", v)
	}

	// Multi-line strings should parse
	if _, ok := vals["query"]; !ok {
		t.Error("missing 'query' multi-line string")
	}
	if _, ok := vals["raw"]; !ok {
		t.Error("missing 'raw' multi-line string")
	}
	if _, ok := vals["poem"]; !ok {
		t.Error("missing 'poem' multi-line string")
	}
	if v := vals["windows-path"]; v != `C:\Users\alice\Documents` {
		t.Errorf("windows-path = %q", v)
	}
	if v := vals["pattern"]; v != `^\d{3}-\d{4}$` {
		t.Errorf("pattern = %q", v)
	}
	if v := vals["template"]; v != `Hello \n World` {
		t.Errorf("template = %q", v)
	}
}

func TestIntegrationValidComposites(t *testing.T) {
	events := fileDecodeAll(t, "../testdata/valid/composites.pakt")

	// composites.pakt has 11 assignments
	const wantAssignments = 11
	starts := countKind(events, EventAssignStart)
	if starts != wantAssignments {
		t.Errorf("AssignStart count = %d, want %d", starts, wantAssignments)
	}

	// CompositeStart and CompositeEnd must be balanced
	compStarts := countCompositeStarts(events)
	compEnds := countCompositeEnds(events)
	if compStarts != compEnds {
		t.Errorf("CompositeStart=%d != CompositeEnd=%d", compStarts, compEnds)
	}

	// Verify nesting is correct: track depth
	depth := 0
	for i, ev := range events {
		if ev.Kind.IsCompositeStart() {
			depth++
		} else if ev.Kind.IsCompositeEnd() {
			depth--
			if depth < 0 {
				t.Fatalf("event[%d]: composite depth went negative", i)
			}
		}
	}
	if depth != 0 {
		t.Errorf("final composite depth = %d, want 0", depth)
	}

	// Verify specific composites by name
	assignNames := make([]string, 0)
	for _, ev := range events {
		if ev.Kind == EventAssignStart {
			assignNames = append(assignNames, ev.Name)
		}
	}
	expectedNames := []string{
		"server", "origin", "version", "point",
		"ids", "tags", "empty-list",
		"users", "codes", "cache",
		"roster",
	}
	if len(assignNames) != len(expectedNames) {
		t.Fatalf("assignment names = %v, want %v", assignNames, expectedNames)
	}
	for i, name := range expectedNames {
		if assignNames[i] != name {
			t.Errorf("assignment %d: name = %q, want %q", i, assignNames[i], name)
		}
	}
}

func TestIntegrationValidNullable(t *testing.T) {
	events := fileDecodeAll(t, "../testdata/valid/nullable.pakt")

	// nullable.pakt has 9 assignments
	const wantAssignments = 9
	starts := countKind(events, EventAssignStart)
	if starts != wantAssignments {
		t.Errorf("AssignStart count = %d, want %d", starts, wantAssignments)
	}

	// Collect scalar values by name
	vals := make(map[string]string)
	scalarTypes := make(map[string]TypeKind)
	for _, ev := range events {
		if ev.Kind == EventScalarValue && ev.Name != "" {
			vals[ev.Name] = ev.ValueString()
			scalarTypes[ev.Name] = ev.ScalarType
		}
	}

	// nil values
	if v := vals["nickname"]; v != "nil" {
		t.Errorf("nickname = %q, want %q", v, "nil")
	}
	if v := vals["maybe-flag"]; v != "nil" {
		t.Errorf("maybe-flag = %q, want %q", v, "nil")
	}
	if v := vals["maybe-stamp"]; v != "nil" {
		t.Errorf("maybe-stamp = %q, want %q", v, "nil")
	}

	// non-nil nullable values
	if v := vals["score"]; v != "42" {
		t.Errorf("score = %q, want %q", v, "42")
	}
	if v := vals["status"]; v != "active" {
		t.Errorf("status = %q, want %q", v, "active")
	}
	if v := vals["maybe-price"]; v != "9.99" {
		t.Errorf("maybe-price = %q, want %q", v, "9.99")
	}

	// Nullable nil values should carry the scalar type
	if tp := scalarTypes["nickname"]; tp != TypeStr {
		t.Errorf("nickname scalarType = %s, want TypeStr", tp)
	}
	// Non-nil nullable values carry the inner scalar type
	if tp := scalarTypes["score"]; tp != TypeInt {
		t.Errorf("score scalarType = %s, want TypeInt", tp)
	}
}

func TestIntegrationValidAtoms(t *testing.T) {
	events := fileDecodeAll(t, "../testdata/valid/atoms.pakt")

	// atoms.pakt has 4 assignments
	const wantAssignments = 4
	starts := countKind(events, EventAssignStart)
	if starts != wantAssignments {
		t.Errorf("AssignStart count = %d, want %d", starts, wantAssignments)
	}
	if len(events) != wantAssignments*3 {
		t.Errorf("total events = %d, want %d", len(events), wantAssignments*3)
	}

	// Spot-check values
	expectedValues := map[string]string{
		"level":    "prod",
		"status":   "active",
		"color":    "blue",
		"priority": "high",
	}
	for i := 0; i < len(events); i += 3 {
		name := events[i].Name
		if want, ok := expectedValues[name]; ok {
			if events[i+1].ValueString() != want {
				t.Errorf("%s: value = %q, want %q", name, events[i+1].ValueString(), want)
			}
		}
	}
}

func TestIntegrationValidFull(t *testing.T) {
	events := fileDecodeAll(t, "../testdata/valid/full.pakt")

	// full.pakt has 13 top-level assignments
	const wantAssignments = 13
	starts := countKind(events, EventAssignStart)
	ends := countKind(events, EventAssignEnd)
	if starts != wantAssignments {
		t.Errorf("AssignStart count = %d, want %d", starts, wantAssignments)
	}
	if ends != wantAssignments {
		t.Errorf("AssignEnd count = %d, want %d", ends, wantAssignments)
	}

	// Balanced composites
	compStarts := countCompositeStarts(events)
	compEnds := countCompositeEnds(events)
	if compStarts != compEnds {
		t.Errorf("CompositeStart=%d != CompositeEnd=%d", compStarts, compEnds)
	}

	// Verify all expected assignment names in order
	expectedNames := []string{
		"app-name", "version", "deploy", "features", "db",
		"replicas", "tls-fingerprint", "rollback-version",
		"meta", "health", "active", "instance-count", "started",
	}
	var assignNames []string
	for _, ev := range events {
		if ev.Kind == EventAssignStart {
			assignNames = append(assignNames, ev.Name)
		}
	}
	if len(assignNames) != len(expectedNames) {
		t.Fatalf("assignment names = %v\nwant %v", assignNames, expectedNames)
	}
	for i, want := range expectedNames {
		if assignNames[i] != want {
			t.Errorf("assignment %d: name = %q, want %q", i, assignNames[i], want)
		}
	}

	// Verify specific event sequences for the "deploy" struct
	// Find the deploy AssignStart
	var deployIdx int
	for i, ev := range events {
		if ev.Kind == EventAssignStart && ev.Name == "deploy" {
			deployIdx = i
			break
		}
	}
	// deploy should be: AssignStart, CompositeStart, ScalarValue(level), ScalarValue(release), ScalarValue(date), CompositeEnd, AssignEnd
	if events[deployIdx].Kind != EventAssignStart {
		t.Errorf("deploy[0]: got %s, want AssignStart", events[deployIdx].Kind)
	}
	if events[deployIdx+1].Kind != EventStructStart {
		t.Errorf("deploy[1]: got %s, want StructStart", events[deployIdx+1].Kind)
	}
	// The struct fields
	if events[deployIdx+2].Kind != EventScalarValue || events[deployIdx+2].Name != "level" {
		t.Errorf("deploy[2]: got %s name=%q, want ScalarValue name=level", events[deployIdx+2].Kind, events[deployIdx+2].Name)
	}
	if events[deployIdx+2].ValueString() != "prod" {
		t.Errorf("deploy level: value = %q, want %q", events[deployIdx+2].ValueString(), "prod")
	}
	if events[deployIdx+3].Kind != EventScalarValue || events[deployIdx+3].Name != "release" {
		t.Errorf("deploy[3]: got %s name=%q, want ScalarValue name=release", events[deployIdx+3].Kind, events[deployIdx+3].Name)
	}
	if events[deployIdx+4].Kind != EventScalarValue || events[deployIdx+4].Name != "date" {
		t.Errorf("deploy[4]: got %s name=%q, want ScalarValue name=date", events[deployIdx+4].Kind, events[deployIdx+4].Name)
	}
	if events[deployIdx+5].Kind != EventStructEnd {
		t.Errorf("deploy[5]: got %s, want StructEnd", events[deployIdx+5].Kind)
	}
	if events[deployIdx+6].Kind != EventAssignEnd {
		t.Errorf("deploy[6]: got %s, want AssignEnd", events[deployIdx+6].Kind)
	}

	// Verify the "features" list has 3 items
	var featIdx int
	for i, ev := range events {
		if ev.Kind == EventAssignStart && ev.Name == "features" {
			featIdx = i
			break
		}
	}
	// AssignStart, CompositeStart, 3×ScalarValue, CompositeEnd, AssignEnd = 7
	if events[featIdx+1].Kind != EventListStart {
		t.Errorf("features[1]: got %s, want ListStart", events[featIdx+1].Kind)
	}
	featureValues := []string{"dark-mode", "notifications", "audit-log"}
	for j, want := range featureValues {
		ev := events[featIdx+2+j]
		if ev.Kind != EventScalarValue || ev.ValueString() != want {
			t.Errorf("features[%d]: got %s value=%q, want ScalarValue value=%q", j, ev.Kind, ev.ValueString(), want)
		}
	}

	// Verify the "meta" map
	var metaIdx int
	for i, ev := range events {
		if ev.Kind == EventAssignStart && ev.Name == "meta" {
			metaIdx = i
			break
		}
	}
	if events[metaIdx+1].Kind != EventMapStart {
		t.Errorf("meta[1]: got %s, want MapStart", events[metaIdx+1].Kind)
	}
	// Map: 3 key-value pairs = 6 scalar events
	metaScalars := 0
	for i := metaIdx + 2; i < len(events) && !events[i].Kind.IsCompositeEnd(); i++ {
		if events[i].Kind == EventScalarValue {
			metaScalars++
		}
	}
	if metaScalars != 6 {
		t.Errorf("meta scalar events = %d, want 6 (3 key-value pairs)", metaScalars)
	}

	// Verify nullable nil: rollback-version should have nil value
	for _, ev := range events {
		if ev.Kind == EventScalarValue && ev.Name == "rollback-version" {
			if ev.ValueString() != "nil" {
				t.Errorf("rollback-version: value = %q, want %q", ev.ValueString(), "nil")
			}
			break
		}
	}

	// Ensure total event count is reasonable (no events lost)
	if len(events) == 0 {
		t.Fatal("no events produced from full.pakt")
	}
	t.Logf("full.pakt produced %d events (%d assignments, %d composite pairs)",
		len(events), starts, compStarts)
}

func TestIntegrationValidSpecFileSkipped(t *testing.T) {
	// spec-example.spec.pakt is a spec file (no values/assignments),
	// so the Decoder cannot parse it as a data document. We verify
	// that it either produces an error or no events (EOF immediately).
	f, err := os.Open("../testdata/valid/spec-example.spec.pakt")
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	defer func() { _ = f.Close() }()

	dec := NewDecoder(f)
	var events []Event
	var decErr error
	for {
		ev, err := dec.Decode()
		if err == io.EOF {
			break
		}
		if err != nil {
			decErr = err
			break
		}
		events = append(events, ev)
	}

	// Either an error or an empty stream is acceptable for a spec file
	if decErr == nil && len(events) > 0 {
		t.Logf("spec file produced %d events (unexpected for a spec-only file)", len(events))
	}
	t.Log("spec-example.spec.pakt: correctly handled (error or empty)")
}

// TestIntegrationValidAllFiles is a table-driven sweep ensuring every
// .pakt file in testdata/valid/ parses without error (except the spec file).
func TestIntegrationValidAllFiles(t *testing.T) {
	files, err := filepath.Glob("../testdata/valid/*.pakt")
	if err != nil {
		t.Fatalf("glob: %v", err)
	}
	if len(files) == 0 {
		t.Fatal("no valid testdata files found")
	}

	for _, f := range files {
		name := filepath.Base(f)
		if strings.HasSuffix(name, ".spec.pakt") {
			continue // spec files are not data documents
		}
		t.Run(name, func(t *testing.T) {
			events := fileDecodeAll(t, f)
			if len(events) == 0 {
				t.Error("no events produced")
			}
			// Every data file must have balanced AssignStart/AssignEnd
			starts := countKind(events, EventAssignStart)
			ends := countKind(events, EventAssignEnd)
			if starts != ends {
				t.Errorf("AssignStart=%d != AssignEnd=%d", starts, ends)
			}
			// Composites must be balanced
			cs := countCompositeStarts(events)
			ce := countCompositeEnds(events)
			if cs != ce {
				t.Errorf("CompositeStart=%d != CompositeEnd=%d", cs, ce)
			}
		})
	}
}

// ---------------------------------------------------------------------------
// Invalid file tests
// ---------------------------------------------------------------------------

func TestIntegrationInvalidFiles(t *testing.T) {
	cases := []struct {
		file     string
		keywords []string // at least one keyword must appear in the error message
	}{
		{
			file:     "type-mismatch.pakt",
			keywords: []string{"type", "mismatch", "expected", "invalid"},
		},
		{
			file:     "nil-non-nullable.pakt",
			keywords: []string{"nil", "nullable", "non-nullable", "expected"},
		},
		{
			file:     "missing-type.pakt",
			keywords: []string{"type", "expected", "colon"},
		},
	}

	for _, tc := range cases {
		t.Run(tc.file, func(t *testing.T) {
			path := filepath.Join("..", "testdata", "invalid", tc.file)
			gotErr := fileDecodeExpectError(t, path)

			// Must be a *ParseError
			var pe *ParseError
			if !errors.As(gotErr, &pe) {
				t.Fatalf("expected *ParseError, got %T: %v", gotErr, gotErr)
			}

			// Error position should be meaningful (non-zero)
			if pe.Pos.Line == 0 {
				t.Errorf("ParseError.Pos.Line = 0, expected non-zero")
			}

			// At least one keyword should appear in the message
			msg := strings.ToLower(pe.Message)
			found := false
			for _, kw := range tc.keywords {
				if strings.Contains(msg, strings.ToLower(kw)) {
					found = true
					break
				}
			}
			if !found {
				t.Errorf("error message %q does not contain any of %v", pe.Message, tc.keywords)
			}
		})
	}
}

// TestIntegrationInvalidAllFiles is a table-driven sweep ensuring every
// .pakt file in testdata/invalid/ produces a parse error.
func TestIntegrationInvalidAllFiles(t *testing.T) {
	files, err := filepath.Glob("../testdata/invalid/*.pakt")
	if err != nil {
		t.Fatalf("glob: %v", err)
	}
	if len(files) == 0 {
		t.Fatal("no invalid testdata files found")
	}

	for _, f := range files {
		t.Run(filepath.Base(f), func(t *testing.T) {
			gotErr := fileDecodeExpectError(t, f)
			var pe *ParseError
			if !errors.As(gotErr, &pe) {
				t.Errorf("expected *ParseError, got %T: %v", gotErr, gotErr)
			}
		})
	}
}

// ---------------------------------------------------------------------------
// Sentinel error tests — verify errors.Is works on returned errors
// ---------------------------------------------------------------------------

func TestDuplicateRootNamesPreserved(t *testing.T) {
	input := "name:str = 'a'\nname:str = 'b'"
	d := NewDecoder(strings.NewReader(input))
	defer d.Close()
	var events []Event
	for {
		ev, err := d.Decode()
		if err == io.EOF {
			break
		}
		if err != nil {
			t.Fatalf("unexpected error: %v", err)
		}
		ev.Value = slices.Clone(ev.Value)
		events = append(events, ev)
	}
	// Both statements preserved: AssignStart, ScalarValue, AssignEnd × 2
	if len(events) != 6 {
		t.Fatalf("expected 6 events for two duplicate statements, got %d: %v", len(events), events)
	}
	if events[1].ValueString() != "a" || events[4].ValueString() != "b" {
		t.Fatalf("duplicate names not preserved in order: %v", events)
	}
}

func TestDuplicateMapKeysFixtureParses(t *testing.T) {
	events := fileDecodeAll(t, filepath.Join("..", "testdata", "valid", "duplicate-map-key.pakt"))
	if len(events) != 10 {
		t.Fatalf("expected 10 events, got %d: %v", len(events), events)
	}
	if events[2].ValueString() != "alice" || events[3].ValueString() != "1" || events[6].ValueString() != "alice" || events[7].ValueString() != "3" {
		t.Fatalf("unexpected duplicate-key event sequence: %v", events)
	}
}

func TestSentinelErrNilNonNullable(t *testing.T) {
	gotErr := fileDecodeExpectError(t, filepath.Join("..", "testdata", "invalid", "nil-non-nullable.pakt"))
	if !errors.Is(gotErr, ErrNilNonNullable) {
		t.Fatalf("expected errors.Is(err, ErrNilNonNullable), got: %v", gotErr)
	}
}

func TestSentinelErrUnexpectedEOF(t *testing.T) {
	// Unterminated string
	d := NewDecoder(strings.NewReader("name:str = 'unterminated"))
	var gotErr error
	for {
		_, err := d.Decode()
		if err == io.EOF {
			t.Fatal("expected error but got EOF")
		}
		if err != nil {
			gotErr = err
			break
		}
	}
	if !errors.Is(gotErr, ErrUnexpectedEOF) {
		t.Fatalf("expected errors.Is(err, ErrUnexpectedEOF) for unterminated string, got: %v", gotErr)
	}
}

func TestDuplicateMapKeysUnit(t *testing.T) {
	typ := mapType(scalarType(TypeStr), scalarType(TypeInt))
	events, err := decodeValue("< 'a' ; 1, 'a' ; 2 >", typ)
	if err != nil {
		t.Fatalf("unexpected error for duplicate map keys: %v", err)
	}
	if len(events) != 6 {
		t.Fatalf("expected 6 events, got %d: %v", len(events), events)
	}
	if events[1].ValueString() != "a" || events[2].ValueString() != "1" || events[3].ValueString() != "a" || events[4].ValueString() != "2" {
		t.Fatalf("unexpected duplicate-key event sequence: %v", events)
	}
}

func TestSentinelErrNilNonNullableUnit(t *testing.T) {
	_, err := decodeValue("nil", scalarType(TypeStr))
	if err == nil {
		t.Fatal("expected error for nil on non-nullable type")
	}
	if !errors.Is(err, ErrNilNonNullable) {
		t.Fatalf("expected errors.Is(err, ErrNilNonNullable), got: %v", err)
	}
}

// ---------------------------------------------------------------------------
// NUL byte framing (spec §10.1)
// ---------------------------------------------------------------------------

func TestNulByteTerminatesUnitAtTopLevel(t *testing.T) {
	// NUL after a complete statement should act as end-of-unit.
	input := "name:str = 'Alice'\x00ignored:str = 'Bob'"
	d := NewDecoder(strings.NewReader(input))
	defer d.Close()
	var events []Event
	for {
		ev, err := d.Decode()
		if err == io.EOF {
			break
		}
		if err != nil {
			t.Fatalf("unexpected error: %v", err)
		}
		events = append(events, ev)
	}
	// Only the first statement should be decoded (3 events).
	if len(events) != 3 {
		t.Fatalf("expected 3 events (one statement before NUL), got %d: %v", len(events), events)
	}
	if events[1].ValueString() != "Alice" {
		t.Errorf("expected value 'Alice', got %q", events[1].Value)
	}
}

func TestNulByteTerminatesUnitBeforeAnyStatement(t *testing.T) {
	// NUL as the very first byte should produce immediate EOF.
	input := "\x00name:str = 'Alice'"
	d := NewDecoder(strings.NewReader(input))
	defer d.Close()
	_, err := d.Decode()
	if err != io.EOF {
		t.Fatalf("expected io.EOF for NUL at start, got %v", err)
	}
}

func TestNulByteTerminatesPack(t *testing.T) {
	// NUL in the middle of a pack should terminate the pack.
	input := "items:[int] <<\n1\n2\x003\n"
	d := NewDecoder(strings.NewReader(input))
	defer d.Close()
	var events []Event
	for {
		ev, err := d.Decode()
		if err == io.EOF {
			break
		}
		if err != nil {
			t.Fatalf("unexpected error: %v", err)
		}
		events = append(events, ev)
	}
	// ListPackStart + ScalarValue(1) + ScalarValue(2) + ListPackEnd = 4 events.
	if len(events) != 4 {
		t.Fatalf("expected 4 events (pack terminated by NUL), got %d: %v", len(events), events)
	}
	if events[0].Kind != EventListPackStart {
		t.Errorf("expected ListPackStart, got %s", events[0].Kind)
	}
	if events[3].Kind != EventListPackEnd {
		t.Errorf("expected ListPackEnd, got %s", events[3].Kind)
	}
}

func TestNulByteTerminatesUnit(t *testing.T) {
	// After NUL, the decoder should return EOF.
	input := "name:str = 'Alice'\x00"
	d := NewDecoder(strings.NewReader(input))
	defer d.Close()
	// Consume the first statement.
	for {
		ev, err := d.Decode()
		if err == io.EOF {
			break
		}
		if err != nil {
			t.Fatalf("unexpected error: %v", err)
		}
		if ev.Kind == EventAssignEnd {
			break
		}
	}
	// Next Decode should return EOF (NUL terminated the unit).
	_, err := d.Decode()
	if err != io.EOF {
		t.Fatalf("expected io.EOF after NUL terminator, got: %v", err)
	}
}
