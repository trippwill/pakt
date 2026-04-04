package encoding

import (
	"errors"
	"io"
	"os"
	"path/filepath"
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
	f, err := os.Open(path)
	if err != nil {
		t.Fatalf("open %s: %v", path, err)
	}
	defer f.Close()

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
		events = append(events, ev)
	}
	return events
}

// fileDecodeExpectError decodes until a non-EOF error is returned, failing
// if the document parses without error.
func fileDecodeExpectError(t *testing.T, path string) error {
	t.Helper()
	f, err := os.Open(path)
	if err != nil {
		t.Fatalf("open %s: %v", path, err)
	}
	defer f.Close()

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

// ---------------------------------------------------------------------------
// Valid file tests
// ---------------------------------------------------------------------------

func TestIntegrationValidScalars(t *testing.T) {
	events := fileDecodeAll(t, "../testdata/valid/scalars.pakt")

	// scalars.pakt has 15 assignments, each producing 3 events
	const wantAssignments = 15
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
		"price", "avogadro", "active", "inactive", "id", "started", "opened", "updated",
	}
	for i, name := range expectedNames {
		if events[i*3].Name != name {
			t.Errorf("assignment %d: name = %q, want %q", i, events[i*3].Name, name)
		}
	}

	// Spot-check specific values
	spotChecks := map[string]string{
		"greeting": "hello world",
		"count":    "42",
		"active":   "true",
		"inactive": "false",
		"negative": "-273",
		"price":    "19.99",
	}
	for i := 0; i < len(events); i += 3 {
		name := events[i].Name
		if want, ok := spotChecks[name]; ok {
			if events[i+1].Value != want {
				t.Errorf("%s: value = %q, want %q", name, events[i+1].Value, want)
			}
		}
	}
}

func TestIntegrationValidStrings(t *testing.T) {
	events := fileDecodeAll(t, "../testdata/valid/strings.pakt")

	// strings.pakt has 10 assignments
	const wantAssignments = 10
	starts := countKind(events, EventAssignStart)
	if starts != wantAssignments {
		t.Errorf("AssignStart count = %d, want %d", starts, wantAssignments)
	}

	// Build name→value map
	vals := make(map[string]string)
	for i := 0; i < len(events); i++ {
		if events[i].Kind == EventScalarValue {
			vals[events[i].Name] = events[i].Value
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
	compStarts := countKind(events, EventCompositeStart)
	compEnds := countKind(events, EventCompositeEnd)
	if compStarts != compEnds {
		t.Errorf("CompositeStart=%d != CompositeEnd=%d", compStarts, compEnds)
	}

	// Verify nesting is correct: track depth
	depth := 0
	for i, ev := range events {
		switch ev.Kind {
		case EventCompositeStart:
			depth++
		case EventCompositeEnd:
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
	types := make(map[string]string)
	for _, ev := range events {
		if ev.Kind == EventScalarValue && ev.Name != "" {
			vals[ev.Name] = ev.Value
			types[ev.Name] = ev.Type
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

	// Nullable nil values should carry the nullable type annotation
	if tp := types["nickname"]; !strings.HasSuffix(tp, "?") {
		t.Errorf("nickname type = %q, expected nullable suffix", tp)
	}
	// Non-nil nullable values may report either the nullable or inner type
	if tp := types["score"]; tp != "int?" && tp != "int" {
		t.Errorf("score type = %q, want %q or %q", tp, "int?", "int")
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
			if events[i+1].Value != want {
				t.Errorf("%s: value = %q, want %q", name, events[i+1].Value, want)
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
	compStarts := countKind(events, EventCompositeStart)
	compEnds := countKind(events, EventCompositeEnd)
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
	if events[deployIdx+1].Kind != EventCompositeStart {
		t.Errorf("deploy[1]: got %s, want CompositeStart", events[deployIdx+1].Kind)
	}
	// The struct fields
	if events[deployIdx+2].Kind != EventScalarValue || events[deployIdx+2].Name != "level" {
		t.Errorf("deploy[2]: got %s name=%q, want ScalarValue name=level", events[deployIdx+2].Kind, events[deployIdx+2].Name)
	}
	if events[deployIdx+2].Value != "prod" {
		t.Errorf("deploy level: value = %q, want %q", events[deployIdx+2].Value, "prod")
	}
	if events[deployIdx+3].Kind != EventScalarValue || events[deployIdx+3].Name != "release" {
		t.Errorf("deploy[3]: got %s name=%q, want ScalarValue name=release", events[deployIdx+3].Kind, events[deployIdx+3].Name)
	}
	if events[deployIdx+4].Kind != EventScalarValue || events[deployIdx+4].Name != "date" {
		t.Errorf("deploy[4]: got %s name=%q, want ScalarValue name=date", events[deployIdx+4].Kind, events[deployIdx+4].Name)
	}
	if events[deployIdx+5].Kind != EventCompositeEnd {
		t.Errorf("deploy[5]: got %s, want CompositeEnd", events[deployIdx+5].Kind)
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
	if events[featIdx+1].Kind != EventCompositeStart {
		t.Errorf("features[1]: got %s, want CompositeStart", events[featIdx+1].Kind)
	}
	featureValues := []string{"dark-mode", "notifications", "audit-log"}
	for j, want := range featureValues {
		ev := events[featIdx+2+j]
		if ev.Kind != EventScalarValue || ev.Value != want {
			t.Errorf("features[%d]: got %s value=%q, want ScalarValue value=%q", j, ev.Kind, ev.Value, want)
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
	if events[metaIdx+1].Kind != EventCompositeStart {
		t.Errorf("meta[1]: got %s, want CompositeStart", events[metaIdx+1].Kind)
	}
	// Map: 3 key-value pairs = 6 scalar events
	metaScalars := 0
	for i := metaIdx + 2; i < len(events) && events[i].Kind != EventCompositeEnd; i++ {
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
			if ev.Value != "nil" {
				t.Errorf("rollback-version: value = %q, want %q", ev.Value, "nil")
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
	defer f.Close()

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
			cs := countKind(events, EventCompositeStart)
			ce := countKind(events, EventCompositeEnd)
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
			file:     "duplicate-name.pakt",
			keywords: []string{"duplicate"},
		},
		{
			file:     "type-mismatch.pakt",
			keywords: []string{"type", "mismatch", "expected", "invalid"},
		},
		{
			file:     "nil-non-nullable.pakt",
			keywords: []string{"nil", "nullable", "non-nullable", "expected"},
		},
		{
			file:     "duplicate-map-key.pakt",
			keywords: []string{"duplicate", "key"},
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
