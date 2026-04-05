package encoding

import (
	"io"
	"strings"
	"testing"
)

// ---------------------------------------------------------------------------
// helpers
// ---------------------------------------------------------------------------

func mkReader(s string) *reader {
	return newReader(strings.NewReader(s))
}

// ---------------------------------------------------------------------------
// BOM handling
// ---------------------------------------------------------------------------

func TestSkipBOM(t *testing.T) {
	bom := "\xEF\xBB\xBFhello"
	r := mkReader(bom)
	b, err := r.readByte()
	if err != nil {
		t.Fatal(err)
	}
	if b != 'h' {
		t.Fatalf("expected 'h' after BOM, got %q", b)
	}
	if r.pos != (Pos{1, 2}) {
		t.Fatalf("expected pos 1:2, got %d:%d", r.pos.Line, r.pos.Col)
	}
}

func TestNoBOM(t *testing.T) {
	r := mkReader("hi")
	b, _ := r.readByte()
	if b != 'h' {
		t.Fatalf("expected 'h', got %q", b)
	}
}

// ---------------------------------------------------------------------------
// Whitespace and comment skipping
// ---------------------------------------------------------------------------

func TestSkipWS(t *testing.T) {
	r := mkReader("  \tabc")
	r.skipWS()
	b, _ := r.readByte()
	if b != 'a' {
		t.Fatalf("expected 'a', got %q", b)
	}
}

func TestSkipWSDoesNotSkipNewline(t *testing.T) {
	r := mkReader(" \n a")
	r.skipWS()
	b, _ := r.readByte()
	if b != '\n' {
		t.Fatalf("expected newline, got %q", b)
	}
}

func TestSkipWSAndNewlines(t *testing.T) {
	r := mkReader("  \n\n\t x")
	r.skipWSAndNewlines()
	b, _ := r.readByte()
	if b != 'x' {
		t.Fatalf("expected 'x', got %q", b)
	}
}

func TestSkipComment(t *testing.T) {
	r := mkReader("# this is a comment\nnext")
	r.skipComment()
	b, _ := r.readByte()
	if b != 'n' {
		t.Fatalf("expected 'n', got %q", b)
	}
}

func TestSkipCommentNoHash(t *testing.T) {
	r := mkReader("abc")
	r.skipComment()
	b, _ := r.readByte()
	if b != 'a' {
		t.Fatalf("expected 'a' (comment skip should be no-op), got %q", b)
	}
}

func TestSkipInsignificant(t *testing.T) {
	r := mkReader("  # comment\n  \n  val")
	r.skipInsignificant(true)
	b, _ := r.readByte()
	if b != 'v' {
		t.Fatalf("expected 'v', got %q", b)
	}
}

func TestSkipInsignificantNoNewlines(t *testing.T) {
	r := mkReader("  # comment\nval")
	r.skipInsignificant(false)
	// Should have skipped WS and the comment (up to and including newline)
	// but then stop because 'v' is not WS/NL/comment.
	b, _ := r.readByte()
	if b != 'v' {
		t.Fatalf("expected 'v', got %q", b)
	}
}

// ---------------------------------------------------------------------------
// Position tracking
// ---------------------------------------------------------------------------

func TestPositionTracking(t *testing.T) {
	r := mkReader("ab\ncd")
	// Read 'a' at 1:1 → pos becomes 1:2
	b, _ := r.readByte()
	if b != 'a' || r.pos != (Pos{1, 2}) {
		t.Fatalf("after 'a': byte=%q pos=%v", b, r.pos)
	}
	// Read 'b' at 1:2 → pos becomes 1:3
	b, _ = r.readByte()
	if b != 'b' || r.pos != (Pos{1, 3}) {
		t.Fatalf("after 'b': byte=%q pos=%v", b, r.pos)
	}
	// Read '\n' at 1:3 → pos becomes 2:1
	b, _ = r.readByte()
	if b != '\n' || r.pos != (Pos{2, 1}) {
		t.Fatalf("after newline: byte=%q pos=%v", b, r.pos)
	}
	// Read 'c' at 2:1 → pos becomes 2:2
	b, _ = r.readByte()
	if b != 'c' || r.pos != (Pos{2, 2}) {
		t.Fatalf("after 'c': byte=%q pos=%v", b, r.pos)
	}
}

func TestUnreadByte(t *testing.T) {
	r := mkReader("abc")
	r.readByte()   //nolint:errcheck
	r.readByte()   //nolint:errcheck
	r.unreadByte() // undo 'b'
	if r.pos != (Pos{1, 2}) {
		t.Fatalf("expected pos 1:2 after unread, got %v", r.pos)
	}
	b, _ := r.readByte()
	if b != 'b' {
		t.Fatalf("expected 'b' after unread, got %q", b)
	}
}

// ---------------------------------------------------------------------------
// Identifiers
// ---------------------------------------------------------------------------

func TestReadIdent(t *testing.T) {
	tests := []struct {
		input string
		want  string
	}{
		{"hello", "hello"},
		{"my-field", "my-field"},
		{"with_under", "with_under"},
		{"_private", "_private"},
		{"camelCase123", "camelCase123"},
		{"a", "a"},
		{"_", "_"},
		{"A-B-C", "A-B-C"},
	}
	for _, tc := range tests {
		r := mkReader(tc.input)
		got, err := r.readIdent()
		if err != nil {
			t.Errorf("readIdent(%q): %v", tc.input, err)
			continue
		}
		if got != tc.want {
			t.Errorf("readIdent(%q) = %q, want %q", tc.input, got, tc.want)
		}
	}
}

func TestReadIdentBadStart(t *testing.T) {
	r := mkReader("123bad")
	_, err := r.readIdent()
	if err == nil {
		t.Fatal("expected error for identifier starting with digit")
	}
}

// ---------------------------------------------------------------------------
// Strings — single-line
// ---------------------------------------------------------------------------

func TestReadStringSingleQuoted(t *testing.T) {
	r := mkReader(`'hello world'`)
	got, err := r.readString()
	if err != nil {
		t.Fatal(err)
	}
	if got != "hello world" {
		t.Fatalf("got %q, want %q", got, "hello world")
	}
}

func TestReadStringDoubleQuoted(t *testing.T) {
	r := mkReader(`"hello world"`)
	got, err := r.readString()
	if err != nil {
		t.Fatal(err)
	}
	if got != "hello world" {
		t.Fatalf("got %q, want %q", got, "hello world")
	}
}

func TestReadStringEscapes(t *testing.T) {
	tests := []struct {
		input string
		want  string
	}{
		{`'a\\b'`, `a\b`},
		{`'a\'b'`, `a'b`},
		{`"a\"b"`, `a"b`},
		{`'a\nb'`, "a\nb"},
		{`'a\rb'`, "a\rb"},
		{`'a\tb'`, "a\tb"},
		{`'\u0041'`, "A"},
		{`'\U00000041'`, "A"},
		{`'\u00e9'`, "é"},
	}
	for _, tc := range tests {
		r := mkReader(tc.input)
		got, err := r.readString()
		if err != nil {
			t.Errorf("readString(%s): %v", tc.input, err)
			continue
		}
		if got != tc.want {
			t.Errorf("readString(%s) = %q, want %q", tc.input, got, tc.want)
		}
	}
}

func TestReadStringRejectNullByte(t *testing.T) {
	r := mkReader(`'\u0000'`)
	_, err := r.readString()
	if err == nil {
		t.Fatal("expected error for null byte in string")
	}
}

func TestReadStringRejectInvalidEscape(t *testing.T) {
	r := mkReader(`'\q'`)
	_, err := r.readString()
	if err == nil {
		t.Fatal("expected error for invalid escape")
	}
}

func TestReadStringUnterminated(t *testing.T) {
	r := mkReader(`'no end`)
	_, err := r.readString()
	if err == nil {
		t.Fatal("expected error for unterminated string")
	}
}

// ---------------------------------------------------------------------------
// Strings — multi-line
// ---------------------------------------------------------------------------

func TestReadMultiLineStringBasic(t *testing.T) {
	input := "'''\nline one\nline two\n'''"
	r := mkReader(input)
	got, err := r.readString()
	if err != nil {
		t.Fatal(err)
	}
	want := "line one\nline two"
	if got != want {
		t.Fatalf("got %q, want %q", got, want)
	}
}

func TestReadMultiLineStringIndented(t *testing.T) {
	input := "'''\n    SELECT id\n    FROM users\n    '''"
	r := mkReader(input)
	got, err := r.readString()
	if err != nil {
		t.Fatal(err)
	}
	want := "SELECT id\nFROM users"
	if got != want {
		t.Fatalf("got %q, want %q", got, want)
	}
}

func TestReadMultiLineStringDoubleQuote(t *testing.T) {
	input := "\"\"\"\nhi\nthere\n\"\"\""
	r := mkReader(input)
	got, err := r.readString()
	if err != nil {
		t.Fatal(err)
	}
	want := "hi\nthere"
	if got != want {
		t.Fatalf("got %q, want %q", got, want)
	}
}

func TestReadMultiLineStringEscapes(t *testing.T) {
	input := "'''\nhello\\nworld\n'''"
	r := mkReader(input)
	got, err := r.readString()
	if err != nil {
		t.Fatal(err)
	}
	want := "hello\nworld"
	if got != want {
		t.Fatalf("got %q, want %q", got, want)
	}
}

func TestReadMultiLineStringInsufficientIndent(t *testing.T) {
	input := "'''\n    first\n  second\n    '''"
	r := mkReader(input)
	_, err := r.readString()
	if err == nil {
		t.Fatal("expected error for insufficient indentation")
	}
}

func TestReadMultiLineStringNoNewlineAfterOpening(t *testing.T) {
	input := "'''text\n'''"
	r := mkReader(input)
	_, err := r.readString()
	if err == nil {
		t.Fatal("expected error when no newline after opening triple-quote")
	}
}

func TestReadMultiLineStringBlankLine(t *testing.T) {
	input := "'''\n    line1\n\n    line2\n    '''"
	r := mkReader(input)
	got, err := r.readString()
	if err != nil {
		t.Fatal(err)
	}
	want := "line1\n\nline2"
	if got != want {
		t.Fatalf("got %q, want %q", got, want)
	}
}

func TestReadRawStringSingleLine(t *testing.T) {
	r := mkReader(`r'C:\Users\alice\Documents'`)
	got, err := r.readString()
	if err != nil {
		t.Fatal(err)
	}
	if got != `C:\Users\alice\Documents` {
		t.Fatalf("got %q", got)
	}
}

func TestReadRawStringMultiLine(t *testing.T) {
	input := "r'''\n    Hello \\n World\n    '''"
	r := mkReader(input)
	got, err := r.readString()
	if err != nil {
		t.Fatal(err)
	}
	if got != `Hello \n World` {
		t.Fatalf("got %q", got)
	}
}

func TestReadBinHex(t *testing.T) {
	r := mkReader(`x'48656C6C6F'`)
	got, err := r.readBin()
	if err != nil {
		t.Fatal(err)
	}
	if got != "48656c6c6f" {
		t.Fatalf("got %q", got)
	}
}

func TestReadBinBase64(t *testing.T) {
	r := mkReader(`b'SGVsbG8='`)
	got, err := r.readBin()
	if err != nil {
		t.Fatal(err)
	}
	if got != "48656c6c6f" {
		t.Fatalf("got %q", got)
	}
}

// ---------------------------------------------------------------------------
// Integers
// ---------------------------------------------------------------------------

func TestReadInt(t *testing.T) {
	tests := []struct {
		input string
		want  string
	}{
		{"42", "42"},
		{"-7", "-7"},
		{"0", "0"},
		{"1_000", "1_000"},
		{"0xFF", "0xFF"},
		{"0xff", "0xff"},
		{"0xDEAD_BEEF", "0xDEAD_BEEF"},
		{"0b1010", "0b1010"},
		{"0b1111_0000", "0b1111_0000"},
		{"0o77", "0o77"},
		{"0o77_00", "0o77_00"},
		{"-0xFF", "-0xFF"},
		{"-0b1010", "-0b1010"},
		{"-0o77", "-0o77"},
		{"01", "01"},
		{"007", "007"},
	}
	for _, tc := range tests {
		r := mkReader(tc.input)
		got, err := r.readInt()
		if err != nil {
			t.Errorf("readInt(%q): %v", tc.input, err)
			continue
		}
		if got != tc.want {
			t.Errorf("readInt(%q) = %q, want %q", tc.input, got, tc.want)
		}
	}
}

func TestReadIntBad(t *testing.T) {
	r := mkReader("abc")
	_, err := r.readInt()
	if err == nil {
		t.Fatal("expected error for non-integer")
	}
}

// ---------------------------------------------------------------------------
// Decimals
// ---------------------------------------------------------------------------

func TestReadDec(t *testing.T) {
	tests := []struct {
		input string
		want  string
	}{
		{"3.14", "3.14"},
		{"-1.5", "-1.5"},
		{"1_000.50", "1_000.50"},
		{"0.0", "0.0"},
		{".5", ".5"},
		{"-.75", "-.75"},
		{".123", ".123"},
	}
	for _, tc := range tests {
		r := mkReader(tc.input)
		got, err := r.readDec()
		if err != nil {
			t.Errorf("readDec(%q): %v", tc.input, err)
			continue
		}
		if got != tc.want {
			t.Errorf("readDec(%q) = %q, want %q", tc.input, got, tc.want)
		}
	}
}

// ---------------------------------------------------------------------------
// Floats
// ---------------------------------------------------------------------------

func TestReadFloat(t *testing.T) {
	tests := []struct {
		input string
		want  string
	}{
		{"6e23", "6e23"},
		{"6.022e23", "6.022e23"},
		{"1.5E-10", "1.5E-10"},
		{"-3.14e+2", "-3.14e+2"},
		{"1e0", "1e0"},
		{"1_000.5e3", "1_000.5e3"},
		{".5e2", ".5e2"},
	}
	for _, tc := range tests {
		r := mkReader(tc.input)
		got, err := r.readFloat()
		if err != nil {
			t.Errorf("readFloat(%q): %v", tc.input, err)
			continue
		}
		if got != tc.want {
			t.Errorf("readFloat(%q) = %q, want %q", tc.input, got, tc.want)
		}
	}
}

func TestReadFloatMissingExponent(t *testing.T) {
	r := mkReader("3.14")
	_, err := r.readFloat()
	if err == nil {
		t.Fatal("expected error when exponent is missing")
	}
}

// ---------------------------------------------------------------------------
// Keywords
// ---------------------------------------------------------------------------

func TestReadBool(t *testing.T) {
	for _, kw := range []string{"true", "false"} {
		r := mkReader(kw)
		got, err := r.readBool()
		if err != nil {
			t.Errorf("readBool(%q): %v", kw, err)
			continue
		}
		if got != kw {
			t.Errorf("readBool(%q) = %q", kw, got)
		}
	}
}

func TestReadBoolBad(t *testing.T) {
	r := mkReader("maybe")
	_, err := r.readBool()
	if err == nil {
		t.Fatal("expected error for non-bool keyword")
	}
}

func TestReadNil(t *testing.T) {
	r := mkReader("nil")
	if err := r.readNil(); err != nil {
		t.Fatal(err)
	}
}

func TestReadNilBad(t *testing.T) {
	r := mkReader("null")
	if err := r.readNil(); err == nil {
		t.Fatal("expected error for 'null'")
	}
}

// ---------------------------------------------------------------------------
// Temporal
// ---------------------------------------------------------------------------

func TestReadDate(t *testing.T) {
	r := mkReader("2026-06-01")
	got, err := r.readDate()
	if err != nil {
		t.Fatal(err)
	}
	if got != "2026-06-01" {
		t.Fatalf("got %q", got)
	}
}

func TestReadTimeZ(t *testing.T) {
	r := mkReader("14:30:00Z")
	got, err := r.readTime()
	if err != nil {
		t.Fatal(err)
	}
	if got != "14:30:00Z" {
		t.Fatalf("got %q", got)
	}
}

func TestReadTimeOffset(t *testing.T) {
	r := mkReader("14:30:00-04:00")
	got, err := r.readTime()
	if err != nil {
		t.Fatal(err)
	}
	if got != "14:30:00-04:00" {
		t.Fatalf("got %q", got)
	}
}

func TestReadTimeFractional(t *testing.T) {
	r := mkReader("14:30:00.123Z")
	got, err := r.readTime()
	if err != nil {
		t.Fatal(err)
	}
	if got != "14:30:00.123Z" {
		t.Fatalf("got %q", got)
	}
}

func TestReadDateTime(t *testing.T) {
	r := mkReader("2026-06-01T14:30:00Z")
	got, err := r.readDateTime()
	if err != nil {
		t.Fatal(err)
	}
	if got != "2026-06-01T14:30:00Z" {
		t.Fatalf("got %q", got)
	}
}

func TestReadDateTimeOffset(t *testing.T) {
	r := mkReader("2026-06-01T14:30:00.500+05:30")
	got, err := r.readDateTime()
	if err != nil {
		t.Fatal(err)
	}
	if got != "2026-06-01T14:30:00.500+05:30" {
		t.Fatalf("got %q", got)
	}
}

// ---------------------------------------------------------------------------
// UUID
// ---------------------------------------------------------------------------

func TestReadUUID(t *testing.T) {
	r := mkReader("550e8400-e29b-41d4-a716-446655440000")
	got, err := r.readUUID()
	if err != nil {
		t.Fatal(err)
	}
	if got != "550e8400-e29b-41d4-a716-446655440000" {
		t.Fatalf("got %q", got)
	}
}

func TestReadUUIDBad(t *testing.T) {
	r := mkReader("550e8400-e29b-41d4-a716-44665544000") // too short
	_, err := r.readUUID()
	if err == nil {
		t.Fatal("expected error for short UUID")
	}
}

// ---------------------------------------------------------------------------
// Atoms
// ---------------------------------------------------------------------------

func TestReadAtomValid(t *testing.T) {
	allowed := []string{"dev", "staging", "prod"}
	r := mkReader("|staging")
	got, err := r.readAtom(allowed)
	if err != nil {
		t.Fatal(err)
	}
	if got != "staging" {
		t.Fatalf("got %q", got)
	}
}

func TestReadAtomInvalid(t *testing.T) {
	allowed := []string{"dev", "staging", "prod"}
	r := mkReader("|test")
	_, err := r.readAtom(allowed)
	if err == nil {
		t.Fatal("expected error for atom not in set")
	}
}

// ---------------------------------------------------------------------------
// Edge cases
// ---------------------------------------------------------------------------

func TestReadByteEOF(t *testing.T) {
	r := mkReader("")
	_, err := r.readByte()
	if err != io.EOF {
		t.Fatalf("expected io.EOF, got %v", err)
	}
}

func TestPeekByteEOF(t *testing.T) {
	r := mkReader("")
	_, err := r.peekByte()
	if err == nil {
		t.Fatal("expected error for peek on empty reader")
	}
}

func TestCRLFNormalization(t *testing.T) {
	r := mkReader("a\r\nb")
	b, _ := r.readByte()
	if b != 'a' {
		t.Fatalf("expected 'a', got %q", b)
	}
	b, _ = r.readByte()
	if b != '\n' {
		t.Fatalf("expected normalized '\\n', got %q", b)
	}
	if r.pos != (Pos{2, 1}) {
		t.Fatalf("expected pos 2:1, got %v", r.pos)
	}
	b, _ = r.readByte()
	if b != 'b' {
		t.Fatalf("expected 'b', got %q", b)
	}
}

func TestExpectByteMismatch(t *testing.T) {
	r := mkReader("a")
	err := r.expectByte('b')
	if err == nil {
		t.Fatal("expected error for byte mismatch")
	}
	// Byte should be unread
	b, _ := r.readByte()
	if b != 'a' {
		t.Fatalf("expected 'a' after failed expectByte, got %q", b)
	}
}
