package encoding

import (
	"bufio"
	"fmt"
	"io"
	"slices"
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
	src     byteSource
	bufSrc  *bufioSource // non-nil when src is a bufioSource (for pool return)
	pos     Pos
	lastPos Pos
	hitNUL  bool            // true after consuming a NUL byte (end-of-unit per spec §10.1)
	sb      strings.Builder // reusable builder for identifiers
	valBuf  []byte          // reusable buffer for scalar values (borrow semantics)
}

// byteAppender is the interface for writing bytes during scalar parsing.
// Both strings.Builder (for idents) and the valBuf adapter (for scalar
// values) satisfy this interface.
type byteAppender interface {
	WriteByte(c byte) error
	WriteRune(r rune) (int, error)
}

// valBufAdapter adapts *reader's valBuf as a byteAppender.
type valBufAdapter struct {
	r *reader
}

func (a valBufAdapter) WriteByte(c byte) error {
	a.r.valBuf = append(a.r.valBuf, c)
	return nil
}

func (a valBufAdapter) WriteRune(ch rune) (int, error) {
	if ch < utf8.RuneSelf {
		a.r.valBuf = append(a.r.valBuf, byte(ch)) //nolint:gosec // ch < utf8.RuneSelf (128), fits in byte
		return 1, nil
	}
	var buf [4]byte
	n := utf8.EncodeRune(buf[:], ch)
	a.r.valBuf = append(a.r.valBuf, buf[:n]...)
	return n, nil
}

func (r *reader) valBufAppender() valBufAdapter {
	return valBufAdapter{r: r}
}

func newReader(r io.Reader) *reader {
	br := bufPool.Get().(*bufio.Reader)
	br.Reset(r)
	bs := &bufioSource{br: br}
	rd := &reader{
		src:    bs,
		bufSrc: bs,
		pos:    Pos{Line: 1, Col: 1},
	}
	rd.skipBOM()
	return rd
}

// release returns the pooled bufio.Reader.
func (r *reader) release() {
	if r.bufSrc != nil {
		r.bufSrc.br.Reset(nil)
		bufPool.Put(r.bufSrc.br)
		r.bufSrc = nil
	}
	r.src = nil
}

// resetValBuf resets the value buffer for reuse.
func (r *reader) resetValBuf() {
	r.valBuf = r.valBuf[:0]
}

// valBufBytes returns the current value buffer content.
// The returned slice is valid until the next resetValBuf call.
func (r *reader) valBufBytes() []byte {
	return r.valBuf
}

// ---------------------------------------------------------------------------
// Byte-level operations
// ---------------------------------------------------------------------------

func (r *reader) skipBOM() {
	p, err := r.src.Peek(3)
	if err == nil && p[0] == 0xEF && p[1] == 0xBB && p[2] == 0xBF {
		r.src.Discard(3) //nolint:errcheck
	}
}

// peekByte returns the next byte without consuming it.
func (r *reader) peekByte() (byte, error) {
	if r.hitNUL {
		return 0, io.EOF
	}
	return r.src.PeekByte()
}

// readByte reads and consumes one byte, updating pos.
// \r\n is normalised to \n; bare \r is also treated as a newline.
func (r *reader) readByte() (byte, error) {
	b, err := r.src.ReadByte()
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
		if nb, perr := r.src.Peek(1); perr == nil && nb[0] == '\n' {
			r.src.ReadByte() //nolint:errcheck
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
	_ = r.src.UnreadByte()
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
	p, err := r.src.Peek(2)
	return err == nil && p[0] == 'r' && (p[1] == '\'' || p[1] == '"')
}

func (r *reader) peekBinLiteralStart() bool {
	p, err := r.src.Peek(2)
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
	return r.sb.String(), nil //nolint:nilerr // EOF on peek means ident ended at EOF

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
	if p, perr := r.src.Peek(2); perr == nil && p[0] == quote && p[1] == quote {
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
	for range n {
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
		val = val*16 + rune(d) //nolint:gosec // d is 0-15 from hexVal
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
		val = val*16 + rune(d) //nolint:gosec // d is 0-15 from hexVal
	}
	return val, true
}

// ---------------------------------------------------------------------------
// Number reading helpers
// ---------------------------------------------------------------------------

// readDigitSep reads DIGIT_SEP = DIGIT (DIGIT | '_')*.
func (r *reader) readDigitSep(sb byteAppender) error {
	b, err := r.readByte()
	if err != nil {
		return r.wrapf(ErrUnexpectedEOF, "expected digit, got EOF")
	}
	if !isDigit(b) {
		r.unreadByte()
		return r.errorf("expected digit, got %q", rune(b))
	}
	sb.WriteByte(b) //nolint:errcheck
	for {
		b, err = r.peekByte()
		if err != nil {
			break
		}
		if isDigit(b) || b == '_' {
			r.readByte()    //nolint:errcheck
			sb.WriteByte(b) //nolint:errcheck
		} else {
			break
		}
	}
	return nil //nolint:nilerr // EOF on peek means digits ended at EOF

}

// readExactDigits reads exactly n decimal digits.
func (r *reader) readExactDigits(sb byteAppender, n int) error {
	for range n {
		b, err := r.readByte()
		if err != nil {
			return r.wrapf(ErrUnexpectedEOF, "expected digit, got EOF")
		}
		if !isDigit(b) {
			r.unreadByte()
			return r.errorf("expected digit, got %q", rune(b))
		}
		sb.WriteByte(b) //nolint:errcheck
	}
	return nil
}

// readExactHex reads exactly n hex digits.
func (r *reader) readExactHex(sb byteAppender, n int) error {
	for range n {
		b, err := r.readByte()
		if err != nil {
			return r.wrapf(ErrUnexpectedEOF, "expected hex digit, got EOF")
		}
		if !isHex(b) {
			r.unreadByte()
			return r.errorf("expected hex digit, got %q", rune(b))
		}
		sb.WriteByte(b) //nolint:errcheck
	}
	return nil
}

// readPrefixedDigits reads digits for 0x/0b/0o literals.
// check validates whether a byte is a valid digit for the given base.
func (r *reader) readPrefixedDigits(sb byteAppender, check func(byte) bool) error {
	b, err := r.readByte()
	if err != nil {
		return r.wrapf(ErrUnexpectedEOF, "expected digit after base prefix, got EOF")
	}
	if !check(b) {
		r.unreadByte()
		return r.errorf("expected digit after base prefix, got %q", rune(b))
	}
	sb.WriteByte(b) //nolint:errcheck
	for {
		b, err = r.peekByte()
		if err != nil {
			break
		}
		if check(b) || b == '_' {
			r.readByte()    //nolint:errcheck
			sb.WriteByte(b) //nolint:errcheck
		} else {
			break
		}
	}
	return nil //nolint:nilerr // EOF on peek means digits ended at EOF

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
	if slices.Contains(allowed, id) {
		return id, nil
	}
	return "", r.errorf("atom %q not in allowed set %v", id, allowed)
}
