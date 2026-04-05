package encoding

import (
	"bufio"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"io"
	"strings"
	"sync"
	"unicode/utf8"
)

// bufPool reuses bufio.Readers to avoid 4KB allocations per Decoder.
var bufPool = sync.Pool{
	New: func() any {
		return bufio.NewReaderSize(nil, 4096)
	},
}

// reader is the hybrid lexer/parser that reads PAKT input directly from bytes
// and emits Events. It is unexported; the public API is [Decoder].
type reader struct {
	buf        *bufio.Reader
	pos        Pos
	lastPos    Pos
	bomChecked bool
	seen       map[string]struct{}
	sb         strings.Builder // reusable builder to avoid per-read allocations
}

func newReader(r io.Reader) *reader {
	br := bufPool.Get().(*bufio.Reader)
	br.Reset(r)
	return &reader{
		buf:  br,
		pos:  Pos{Line: 1, Col: 1},
		seen: make(map[string]struct{}, 16),
	}
}

// release returns the pooled bufio.Reader.
func (r *reader) release() {
	if r.buf != nil {
		r.buf.Reset(nil)
		bufPool.Put(r.buf)
		r.buf = nil
	}
}

// ---------------------------------------------------------------------------
// Byte-level operations
// ---------------------------------------------------------------------------

func (r *reader) skipBOM() {
	p, err := r.buf.Peek(3)
	if err == nil && p[0] == 0xEF && p[1] == 0xBB && p[2] == 0xBF {
		r.buf.Discard(3) //nolint:errcheck
	}
}

func (r *reader) ensureBOM() {
	if !r.bomChecked {
		r.bomChecked = true
		r.skipBOM()
	}
}

// peekByte returns the next byte without consuming it.
func (r *reader) peekByte() (byte, error) {
	r.ensureBOM()
	p, err := r.buf.Peek(1)
	if err != nil {
		return 0, err
	}
	return p[0], nil
}

// readByte reads and consumes one byte, updating pos.
// \r\n is normalised to \n; bare \r is also treated as a newline.
func (r *reader) readByte() (byte, error) {
	r.ensureBOM()
	b, err := r.buf.ReadByte()
	if err != nil {
		return 0, err
	}
	r.lastPos = r.pos
	switch b {
	case '\n':
		r.pos.Line++
		r.pos.Col = 1
	case '\r':
		// Consume a following \n if present (\r\n → single newline).
		if nb, perr := r.buf.Peek(1); perr == nil && nb[0] == '\n' {
			r.buf.ReadByte() //nolint:errcheck
		}
		r.pos.Line++
		r.pos.Col = 1
		b = '\n' // normalise
	default:
		r.pos.Col++
	}
	return b, nil
}

// unreadByte pushes the last byte back and restores the previous position.
// Only valid for one consecutive unread.
func (r *reader) unreadByte() {
	_ = r.buf.UnreadByte()
	r.pos = r.lastPos
}

// skipWS skips spaces and tabs (NOT newlines).
func (r *reader) skipWS() {
	for {
		b, err := r.peekByte()
		if err != nil || (b != ' ' && b != '\t') {
			return
		}
		r.readByte() //nolint:errcheck
	}
}

// skipWSAndNewlines skips spaces, tabs, and newlines.
func (r *reader) skipWSAndNewlines() {
	for {
		b, err := r.peekByte()
		if err != nil {
			return
		}
		if b == ' ' || b == '\t' || b == '\n' || b == '\r' {
			r.readByte() //nolint:errcheck
		} else {
			return
		}
	}
}

// skipComment skips a line comment. If the current byte is '#', everything
// through to (and including) the newline is consumed.
func (r *reader) skipComment() {
	b, err := r.peekByte()
	if err != nil || b != '#' {
		return
	}
	for {
		b, err = r.readByte()
		if err != nil || b == '\n' {
			return
		}
	}
}

// skipInsignificant skips whitespace, comments, and (optionally) newlines.
func (r *reader) skipInsignificant(skipNL bool) {
	for {
		b, err := r.peekByte()
		if err != nil {
			return
		}
		switch {
		case b == ' ' || b == '\t':
			r.readByte() //nolint:errcheck
		case b == '#':
			r.skipComment()
		case skipNL && (b == '\n' || b == '\r'):
			r.readByte() //nolint:errcheck
		default:
			return
		}
	}
}

// errorf creates a *ParseError at the reader's current position.
func (r *reader) errorf(format string, args ...any) *ParseError {
	return Errorf(r.pos, format, args...)
}

// wrapf creates a *ParseError at the reader's current position wrapping a sentinel.
func (r *reader) wrapf(sentinel ErrorCode, format string, args ...any) *ParseError {
	return Wrapf(r.pos, sentinel, format, args...)
}

// expectByte reads one byte and returns an error if it does not match.
func (r *reader) expectByte(expected byte) error {
	b, err := r.readByte()
	if err != nil {
		return r.wrapf(ErrUnexpectedEOF, "expected %q, got EOF", rune(expected))
	}
	if b != expected {
		r.unreadByte()
		return r.errorf("expected %q, got %q", rune(expected), rune(b))
	}
	return nil
}

func (r *reader) peekRawStringStart() bool {
	r.ensureBOM()
	p, err := r.buf.Peek(2)
	return err == nil && p[0] == 'r' && (p[1] == '\'' || p[1] == '"')
}

func (r *reader) peekBinLiteralStart() bool {
	r.ensureBOM()
	p, err := r.buf.Peek(2)
	return err == nil && (p[0] == 'x' || p[0] == 'b') && p[1] == '\''
}

// ---------------------------------------------------------------------------
// Character classification helpers
// ---------------------------------------------------------------------------

func isAlpha(b byte) bool { return (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') }
func isDigit(b byte) bool { return b >= '0' && b <= '9' }
func isHex(b byte) bool {
	return isDigit(b) || (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F')
}
func isBin(b byte) bool { return b == '0' || b == '1' }
func isOct(b byte) bool { return b >= '0' && b <= '7' }

func hexVal(b byte) int {
	switch {
	case b >= '0' && b <= '9':
		return int(b - '0')
	case b >= 'a' && b <= 'f':
		return int(b-'a') + 10
	case b >= 'A' && b <= 'F':
		return int(b-'A') + 10
	default:
		return -1
	}
}

// ---------------------------------------------------------------------------
// Identifier reading
// ---------------------------------------------------------------------------

// readIdent reads IDENT = (ALPHA | '_') (ALPHA | DIGIT | '_' | '-')*
func (r *reader) readIdent() (string, error) {
	b, err := r.readByte()
	if err != nil {
		return "", r.wrapf(ErrUnexpectedEOF, "expected identifier, got EOF")
	}
	if !isAlpha(b) && b != '_' {
		r.unreadByte()
		return "", r.errorf("expected identifier, got %q", rune(b))
	}

	r.sb.Reset()
	r.sb.WriteByte(b)
	for {
		b, err = r.peekByte()
		if err != nil {
			break
		}
		if isAlpha(b) || isDigit(b) || b == '_' || b == '-' {
			r.readByte() //nolint:errcheck
			r.sb.WriteByte(b)
		} else {
			break
		}
	}
	return r.sb.String(), nil
}

// ---------------------------------------------------------------------------
// String reading
// ---------------------------------------------------------------------------

// readString reads a quoted or raw string, including triple-quoted forms.
func (r *reader) readString() (string, error) {
	raw := false
	start, err := r.readByte()
	if err != nil {
		return "", r.wrapf(ErrUnexpectedEOF, "expected string, got EOF")
	}
	quote := start
	if start == 'r' {
		raw = true
		quote, err = r.readByte()
		if err != nil {
			return "", r.wrapf(ErrUnexpectedEOF, "expected quote after raw string prefix, got EOF")
		}
	}
	if quote != '\'' && quote != '"' {
		if raw {
			return "", r.errorf("expected quote after raw string prefix, got %q", rune(quote))
		}
		r.unreadByte()
		return "", r.errorf("expected string, got %q", rune(quote))
	}

	// Check for triple-quote opening.
	if p, perr := r.buf.Peek(2); perr == nil && p[0] == quote && p[1] == quote {
		r.readByte() //nolint:errcheck // second quote
		r.readByte() //nolint:errcheck // third quote
		return r.readMultiLineString(quote, raw)
	}

	// Single-line string.
	r.sb.Reset()
	for {
		b, err := r.readByte()
		if err != nil {
			return "", r.wrapf(ErrUnexpectedEOF, "unterminated string")
		}
		if b == quote {
			return r.sb.String(), nil
		}
		if !raw && b == '\\' {
			ch, err := r.readEscape()
			if err != nil {
				return "", err
			}
			r.sb.WriteRune(ch)
			continue
		}
		if b == '\n' {
			return "", r.errorf("newline in single-line string")
		}
		if b == 0 {
			return "", r.errorf("null byte in string")
		}
		r.sb.WriteByte(b)
	}
}

// readEscape reads the character after '\' and returns the decoded rune.
func (r *reader) readEscape() (rune, error) {
	b, err := r.readByte()
	if err != nil {
		return 0, r.wrapf(ErrUnexpectedEOF, "unterminated escape sequence")
	}
	switch b {
	case '\\':
		return '\\', nil
	case '\'':
		return '\'', nil
	case '"':
		return '"', nil
	case 'n':
		return '\n', nil
	case 'r':
		return '\r', nil
	case 't':
		return '\t', nil
	case 'u':
		return r.readUnicodeEscape(4)
	case 'U':
		return r.readUnicodeEscape(8)
	default:
		return 0, r.errorf("invalid escape sequence: \\%c", rune(b))
	}
}

// readUnicodeEscape reads n hex digits and returns the corresponding rune.
func (r *reader) readUnicodeEscape(n int) (rune, error) {
	prefix := "\\u"
	if n == 8 {
		prefix = "\\U"
	}
	var val rune
	var digits strings.Builder
	for i := 0; i < n; i++ {
		b, err := r.readByte()
		if err != nil {
			return 0, r.wrapf(ErrUnexpectedEOF, "incomplete %s escape: found %q", prefix, prefix+digits.String())
		}
		d := hexVal(b)
		if d < 0 {
			digits.WriteByte(b)
			return 0, r.errorf("invalid hex digit in %s escape: found %q", prefix, prefix+digits.String())
		}
		digits.WriteByte(b)
		val = val*16 + rune(d)
	}
	if val == 0 {
		return 0, r.errorf("null byte (U+0000) not permitted in strings")
	}
	if !utf8.ValidRune(val) {
		return 0, r.errorf("invalid unicode code point: U+%08X", val)
	}
	return val, nil
}

// readMultiLineString reads the body of a triple-quoted string.
// The opening triple-quote has already been consumed.
func (r *reader) readMultiLineString(quote byte, raw bool) (string, error) {
	var out strings.Builder
	if err := r.consumeMultiLineString(quote, raw, &out); err != nil {
		return "", err
	}
	return out.String(), nil
}

func (r *reader) consumeMultiLineString(quote byte, raw bool, out *strings.Builder) error {
	// Next byte must be a newline.
	b, err := r.readByte()
	if err != nil {
		return r.wrapf(ErrUnexpectedEOF, "expected newline after opening triple-quote, got EOF")
	}
	if b != '\n' {
		return r.errorf("expected newline after opening triple-quote, got %q", rune(b))
	}

	closingDelim := string([]byte{quote, quote, quote})
	baseline := 0
	baselineSet := false
	lineCount := 0

	writeLine := func(line string) error {
		if out != nil {
			if lineCount > 0 {
				out.WriteByte('\n')
			}
		}
		lineCount++
		if line == "" {
			return nil
		}
		if raw {
			if strings.IndexByte(line, 0) >= 0 {
				return r.errorf("null byte in string")
			}
			if out != nil {
				out.WriteString(line)
			}
			return nil
		}
		processed, perr := processEscapes(line)
		if perr != "" {
			return r.errorf("%s", perr)
		}
		if out != nil {
			out.WriteString(processed)
		}
		return nil
	}

	for {
		line, lerr := r.readRawLine()
		if lerr != nil {
			return r.wrapf(ErrUnexpectedEOF, "unterminated multi-line string")
		}
		trimmed := strings.TrimLeft(line, " \t")
		if trimmed == closingDelim {
			return nil
		}
		if strings.TrimSpace(line) == "" {
			if err := writeLine(""); err != nil {
				return err
			}
			continue
		}

		leading := countLeadingWS(line)
		if !baselineSet {
			baseline = leading
			baselineSet = true
		}
		if leading < baseline {
			return r.errorf("insufficient indentation in multi-line string")
		}
		if err := writeLine(line[baseline:]); err != nil {
			return err
		}
	}
}

// readBin reads a binary literal and returns its canonical lower-case hex value.
func (r *reader) readBin() (string, error) {
	prefix, err := r.readByte()
	if err != nil {
		return "", r.wrapf(ErrUnexpectedEOF, "expected binary literal, got EOF")
	}
	if prefix != 'x' && prefix != 'b' {
		r.unreadByte()
		return "", r.errorf("expected binary literal, got %q", rune(prefix))
	}
	if err := r.expectByte('\''); err != nil {
		return "", err
	}

	r.sb.Reset()
	for {
		b, err := r.readByte()
		if err != nil {
			return "", r.wrapf(ErrUnexpectedEOF, "unterminated binary literal")
		}
		if b == '\'' {
			break
		}
		if b == '\n' {
			return "", r.errorf("newline in binary literal")
		}
		if b == 0 {
			return "", r.errorf("null byte in binary literal")
		}
		r.sb.WriteByte(b)
	}

	lit := r.sb.String()
	switch prefix {
	case 'x':
		if len(lit)%2 != 0 {
			return "", r.errorf("hex binary literal must contain an even number of digits")
		}
		data, err := hex.DecodeString(lit)
		if err != nil {
			return "", r.errorf("invalid hex binary literal")
		}
		return hex.EncodeToString(data), nil
	case 'b':
		data, err := base64.StdEncoding.Strict().DecodeString(lit)
		if err != nil {
			return "", r.errorf("invalid base64 binary literal")
		}
		return hex.EncodeToString(data), nil
	default:
		return "", r.errorf("unknown binary literal prefix %q", rune(prefix))
	}
}

// readRawLine reads bytes until a newline (or EOF) without escape processing.
// If bytes were read before EOF, the partial line is returned without error.
func (r *reader) readRawLine() (string, error) {
	r.sb.Reset()
	for {
		b, err := r.readByte()
		if err != nil {
			if r.sb.Len() > 0 {
				return r.sb.String(), nil
			}
			return "", err
		}
		if b == '\n' {
			return r.sb.String(), nil
		}
		r.sb.WriteByte(b)
	}
}

func countLeadingWS(s string) int {
	for i := 0; i < len(s); i++ {
		if s[i] != ' ' && s[i] != '\t' {
			return i
		}
	}
	return len(s)
}

// processEscapes processes escape sequences in s.
// Returns (result, errorMessage). errorMessage is empty on success.
func processEscapes(s string) (string, string) {
	var sb strings.Builder
	i := 0
	for i < len(s) {
		if s[i] == 0 {
			return "", "null byte in string"
		}
		if s[i] != '\\' {
			sb.WriteByte(s[i])
			i++
			continue
		}
		i++ // skip backslash
		if i >= len(s) {
			return "", "unterminated escape sequence in multi-line string"
		}
		switch s[i] {
		case '\\':
			sb.WriteByte('\\')
		case '\'':
			sb.WriteByte('\'')
		case '"':
			sb.WriteByte('"')
		case 'n':
			sb.WriteByte('\n')
		case 'r':
			sb.WriteByte('\r')
		case 't':
			sb.WriteByte('\t')
		case 'u':
			if i+4 >= len(s) {
				return "", fmt.Sprintf("incomplete \\u escape: found %q", "\\u"+s[i+1:])
			}
			hexStr := s[i+1 : i+5]
			val, ok := parseHexDigits(hexStr)
			if !ok {
				return "", fmt.Sprintf("invalid hex digit in \\u escape: found %q", "\\u"+hexStr)
			}
			if val == 0 {
				return "", "null byte (U+0000) not permitted in strings"
			}
			if !utf8.ValidRune(val) {
				return "", fmt.Sprintf("invalid unicode code point: U+%04X", val)
			}
			sb.WriteRune(val)
			i += 4
		case 'U':
			if i+8 >= len(s) {
				return "", fmt.Sprintf("incomplete \\U escape: found %q", "\\U"+s[i+1:])
			}
			hexStr := s[i+1 : i+9]
			val, ok := parseHexDigits(hexStr)
			if !ok {
				return "", fmt.Sprintf("invalid hex digit in \\U escape: found %q", "\\U"+hexStr)
			}
			if val == 0 {
				return "", "null byte (U+0000) not permitted in strings"
			}
			if !utf8.ValidRune(val) {
				return "", fmt.Sprintf("invalid unicode code point: U+%08X", val)
			}
			sb.WriteRune(val)
			i += 8
		default:
			return "", fmt.Sprintf("invalid escape sequence: \\%c", rune(s[i]))
		}
		i++
	}
	return sb.String(), ""
}

func parseHexDigits(s string) (rune, bool) {
	var val rune
	for i := 0; i < len(s); i++ {
		d := hexVal(s[i])
		if d < 0 {
			return 0, false
		}
		val = val*16 + rune(d)
	}
	return val, true
}

// ---------------------------------------------------------------------------
// Number reading helpers
// ---------------------------------------------------------------------------

// readDigitSep reads DIGIT_SEP = DIGIT (DIGIT | '_')*.
func (r *reader) readDigitSep(sb *strings.Builder) error {
	b, err := r.readByte()
	if err != nil {
		return r.wrapf(ErrUnexpectedEOF, "expected digit, got EOF")
	}
	if !isDigit(b) {
		r.unreadByte()
		return r.errorf("expected digit, got %q", rune(b))
	}
	sb.WriteByte(b)
	for {
		b, err = r.peekByte()
		if err != nil {
			break
		}
		if isDigit(b) || b == '_' {
			r.readByte() //nolint:errcheck
			sb.WriteByte(b)
		} else {
			break
		}
	}
	return nil
}

// readExactDigits reads exactly n decimal digits.
func (r *reader) readExactDigits(sb *strings.Builder, n int) error {
	for i := 0; i < n; i++ {
		b, err := r.readByte()
		if err != nil {
			return r.wrapf(ErrUnexpectedEOF, "expected digit, got EOF")
		}
		if !isDigit(b) {
			r.unreadByte()
			return r.errorf("expected digit, got %q", rune(b))
		}
		sb.WriteByte(b)
	}
	return nil
}

// readExactHex reads exactly n hex digits.
func (r *reader) readExactHex(sb *strings.Builder, n int) error {
	for i := 0; i < n; i++ {
		b, err := r.readByte()
		if err != nil {
			return r.wrapf(ErrUnexpectedEOF, "expected hex digit, got EOF")
		}
		if !isHex(b) {
			r.unreadByte()
			return r.errorf("expected hex digit, got %q", rune(b))
		}
		sb.WriteByte(b)
	}
	return nil
}

// readPrefixedDigits reads digits for 0x/0b/0o literals.
// check validates whether a byte is a valid digit for the given base.
func (r *reader) readPrefixedDigits(sb *strings.Builder, check func(byte) bool) error {
	b, err := r.readByte()
	if err != nil {
		return r.wrapf(ErrUnexpectedEOF, "expected digit after base prefix, got EOF")
	}
	if !check(b) {
		r.unreadByte()
		return r.errorf("expected digit after base prefix, got %q", rune(b))
	}
	sb.WriteByte(b)
	for {
		b, err = r.peekByte()
		if err != nil {
			break
		}
		if check(b) || b == '_' {
			r.readByte() //nolint:errcheck
			sb.WriteByte(b)
		} else {
			break
		}
	}
	return nil
}

// ---------------------------------------------------------------------------
// Integer reading
// ---------------------------------------------------------------------------

// readInt reads INT = ['-'] DIGIT_SEP | ['-'] '0x' HEX_SEP | etc.
func (r *reader) readInt() (string, error) {
	r.sb.Reset()

	// Optional negative sign.
	if b, err := r.peekByte(); err == nil && b == '-' {
		r.readByte() //nolint:errcheck
		r.sb.WriteByte('-')
	}

	// Peek at first digit.
	first, err := r.peekByte()
	if err != nil {
		return "", r.wrapf(ErrUnexpectedEOF, "expected digit in integer, got EOF")
	}
	if !isDigit(first) {
		return "", r.errorf("expected digit in integer, got %q", rune(first))
	}

	if first == '0' {
		r.readByte() //nolint:errcheck
		r.sb.WriteByte('0')
		// Check for base prefix.
		if b, err := r.peekByte(); err == nil {
			switch b {
			case 'x':
				r.readByte() //nolint:errcheck
				r.sb.WriteByte('x')
				if err := r.readPrefixedDigits(&r.sb, isHex); err != nil {
					return "", err
				}
				return r.sb.String(), nil
			case 'b':
				r.readByte() //nolint:errcheck
				r.sb.WriteByte('b')
				if err := r.readPrefixedDigits(&r.sb, isBin); err != nil {
					return "", err
				}
				return r.sb.String(), nil
			case 'o':
				r.readByte() //nolint:errcheck
				r.sb.WriteByte('o')
				if err := r.readPrefixedDigits(&r.sb, isOct); err != nil {
					return "", err
				}
				return r.sb.String(), nil
			}
		}
		// Plain decimal that starts with 0. Continue reading digits.
		for {
			b, err := r.peekByte()
			if err != nil {
				break
			}
			if isDigit(b) || b == '_' {
				r.readByte() //nolint:errcheck
				r.sb.WriteByte(b)
			} else {
				break
			}
		}
		return r.sb.String(), nil
	}

	// Regular decimal DIGIT_SEP.
	if err := r.readDigitSep(&r.sb); err != nil {
		return "", err
	}
	return r.sb.String(), nil
}

// ---------------------------------------------------------------------------
// Decimal reading
// ---------------------------------------------------------------------------

// readDec reads DEC = ['-'] DIGIT_SEP? '.' DIGIT_SEP.
func (r *reader) readDec() (string, error) {
	r.sb.Reset()

	if b, err := r.peekByte(); err == nil && b == '-' {
		r.readByte() //nolint:errcheck
		r.sb.WriteByte('-')
	}
	// Leading digits are optional: .5 is valid
	if b, err := r.peekByte(); err == nil && b != '.' {
		if err := r.readDigitSep(&r.sb); err != nil {
			return "", err
		}
	}
	if err := r.expectByte('.'); err != nil {
		return "", err
	}
	r.sb.WriteByte('.')
	if err := r.readDigitSep(&r.sb); err != nil {
		return "", err
	}
	return r.sb.String(), nil
}

// ---------------------------------------------------------------------------
// Float reading
// ---------------------------------------------------------------------------

// readFloat reads FLOAT = ['-'] DIGIT_SEP? ('.' DIGIT_SEP)? ('e'|'E') [+-]? DIGIT+.
func (r *reader) readFloat() (string, error) {
	r.sb.Reset()

	if b, err := r.peekByte(); err == nil && b == '-' {
		r.readByte() //nolint:errcheck
		r.sb.WriteByte('-')
	}
	// Leading digits are optional when followed by '.' or exponent.
	if b, err := r.peekByte(); err == nil && b != '.' && b != 'e' && b != 'E' {
		if err := r.readDigitSep(&r.sb); err != nil {
			return "", err
		}
	}

	// Optional '.' DIGIT_SEP.
	if b, err := r.peekByte(); err == nil && b == '.' {
		r.readByte() //nolint:errcheck
		r.sb.WriteByte('.')
		if err := r.readDigitSep(&r.sb); err != nil {
			return "", err
		}
	}

	// Mandatory exponent.
	b, err := r.peekByte()
	if err != nil {
		return "", r.wrapf(ErrUnexpectedEOF, "expected exponent ('e' or 'E') in float, got EOF")
	}
	if b != 'e' && b != 'E' {
		return "", r.errorf("expected exponent ('e' or 'E') in float, got %q", rune(b))
	}
	r.readByte() //nolint:errcheck
	r.sb.WriteByte(b)

	// Optional sign.
	if b, err := r.peekByte(); err == nil && (b == '+' || b == '-') {
		r.readByte() //nolint:errcheck
		r.sb.WriteByte(b)
	}

	// DIGIT+ (no underscores in exponent per spec).
	count := 0
	for {
		b, err := r.peekByte()
		if err != nil || !isDigit(b) {
			break
		}
		r.readByte() //nolint:errcheck
		r.sb.WriteByte(b)
		count++
	}
	if count == 0 {
		if b, err := r.peekByte(); err != nil {
			return "", r.wrapf(ErrUnexpectedEOF, "expected digits in float exponent, got EOF")
		} else {
			return "", r.errorf("expected digits in float exponent, got %q", rune(b))
		}
	}
	return r.sb.String(), nil
}

// ---------------------------------------------------------------------------
// Keyword reading
// ---------------------------------------------------------------------------

// readBool reads "true" or "false".
func (r *reader) readBool() (string, error) {
	id, err := r.readIdent()
	if err != nil {
		return "", err
	}
	if id != "true" && id != "false" {
		return "", r.errorf("expected 'true' or 'false', got %q", id)
	}
	return id, nil
}

// readNil reads the keyword "nil".
func (r *reader) readNil() error {
	id, err := r.readIdent()
	if err != nil {
		return err
	}
	if id != "nil" {
		return r.errorf("expected 'nil', got %q", id)
	}
	return nil
}

// ---------------------------------------------------------------------------
// Temporal reading
// ---------------------------------------------------------------------------

// readDate reads DATE = DIGIT{4}-DIGIT{2}-DIGIT{2}.
func (r *reader) readDate() (string, error) {
	r.sb.Reset()
	if err := r.readExactDigits(&r.sb, 4); err != nil {
		return "", err
	}
	if err := r.expectByte('-'); err != nil {
		return "", err
	}
	r.sb.WriteByte('-')
	if err := r.readExactDigits(&r.sb, 2); err != nil {
		return "", err
	}
	if err := r.expectByte('-'); err != nil {
		return "", err
	}
	r.sb.WriteByte('-')
	if err := r.readExactDigits(&r.sb, 2); err != nil {
		return "", err
	}
	return r.sb.String(), nil
}

// readTime reads TIME = DIGIT{2}:DIGIT{2}:DIGIT{2}(.DIGIT+)? TZ.
func (r *reader) readTime() (string, error) {
	r.sb.Reset()
	if err := r.readExactDigits(&r.sb, 2); err != nil {
		return "", err
	}
	if err := r.expectByte(':'); err != nil {
		return "", err
	}
	r.sb.WriteByte(':')
	if err := r.readExactDigits(&r.sb, 2); err != nil {
		return "", err
	}
	if err := r.expectByte(':'); err != nil {
		return "", err
	}
	r.sb.WriteByte(':')
	if err := r.readExactDigits(&r.sb, 2); err != nil {
		return "", err
	}

	// Optional fractional seconds.
	if b, err := r.peekByte(); err == nil && b == '.' {
		r.readByte() //nolint:errcheck
		r.sb.WriteByte('.')
		count := 0
		for {
			b, err := r.peekByte()
			if err != nil || !isDigit(b) {
				break
			}
			r.readByte() //nolint:errcheck
			r.sb.WriteByte(b)
			count++
		}
		if count == 0 {
			if b, err := r.peekByte(); err != nil {
				return "", r.wrapf(ErrUnexpectedEOF, "expected digits after '.' in time, got EOF")
			} else {
				return "", r.errorf("expected digits after '.' in time, got %q", rune(b))
			}
		}
	}

	// Timezone.
	b, err := r.peekByte()
	if err != nil {
		return "", r.wrapf(ErrUnexpectedEOF, "expected timezone in time, got EOF")
	}
	switch b {
	case 'Z':
		r.readByte() //nolint:errcheck
		r.sb.WriteByte('Z')
	case '+', '-':
		r.readByte() //nolint:errcheck
		r.sb.WriteByte(b)
		if err := r.readExactDigits(&r.sb, 2); err != nil {
			return "", err
		}
		if err := r.expectByte(':'); err != nil {
			return "", err
		}
		r.sb.WriteByte(':')
		if err := r.readExactDigits(&r.sb, 2); err != nil {
			return "", err
		}
	default:
		return "", r.errorf("expected timezone (Z or ±HH:MM) in time, got %q", rune(b))
	}
	return r.sb.String(), nil
}

// readDateTime reads DATETIME = DATE 'T' TIME.
func (r *reader) readDateTime() (string, error) {
	date, err := r.readDate()
	if err != nil {
		return "", err
	}
	if err := r.expectByte('T'); err != nil {
		return "", err
	}
	t, err := r.readTime()
	if err != nil {
		return "", err
	}
	r.sb.Reset()
	r.sb.WriteString(date)
	r.sb.WriteByte('T')
	r.sb.WriteString(t)
	return r.sb.String(), nil
}

// ---------------------------------------------------------------------------
// UUID reading
// ---------------------------------------------------------------------------

// readUUID reads UUID = HEX{8}-HEX{4}-HEX{4}-HEX{4}-HEX{12}.
func (r *reader) readUUID() (string, error) {
	r.sb.Reset()
	segments := [5]int{8, 4, 4, 4, 12}
	for i, n := range segments {
		if i > 0 {
			if err := r.expectByte('-'); err != nil {
				return "", err
			}
			r.sb.WriteByte('-')
		}
		if err := r.readExactHex(&r.sb, n); err != nil {
			return "", err
		}
	}
	return r.sb.String(), nil
}

// ---------------------------------------------------------------------------
// Atom reading
// ---------------------------------------------------------------------------

// readAtom expects a '|' prefix, reads an identifier, and validates it against the allowed set.
func (r *reader) readAtom(allowed []string) (string, error) {
	if err := r.expectByte('|'); err != nil {
		return "", err
	}
	id, err := r.readIdent()
	if err != nil {
		return "", err
	}
	for _, a := range allowed {
		if id == a {
			return id, nil
		}
	}
	return "", r.errorf("atom %q not in allowed set %v", id, allowed)
}
