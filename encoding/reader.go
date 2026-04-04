package encoding

import (
	"bufio"
	"fmt"
	"io"
	"strings"
	"unicode/utf8"
)

// reader is the hybrid lexer/parser that reads PAKT input directly from bytes
// and emits Events. It is unexported; the public API is [Decoder].
type reader struct {
	buf        *bufio.Reader
	pos        Pos
	lastPos    Pos
	bomChecked bool
	events     []Event
	seen       map[string]struct{}
}

func newReader(r io.Reader) *reader {
	return &reader{
		buf:  bufio.NewReader(r),
		pos:  Pos{Line: 1, Col: 1},
		seen: make(map[string]struct{}),
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
	if b == '\n' {
		r.pos.Line++
		r.pos.Col = 1
	} else if b == '\r' {
		// Consume a following \n if present (\r\n → single newline).
		if nb, perr := r.buf.Peek(1); perr == nil && nb[0] == '\n' {
			r.buf.ReadByte() //nolint:errcheck
		}
		r.pos.Line++
		r.pos.Col = 1
		b = '\n' // normalise
	} else {
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

// expectByte reads one byte and returns an error if it does not match.
func (r *reader) expectByte(expected byte) error {
	b, err := r.readByte()
	if err != nil {
		return r.errorf("expected %q, got EOF", rune(expected))
	}
	if b != expected {
		r.unreadByte()
		return r.errorf("expected %q, got %q", rune(expected), rune(b))
	}
	return nil
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
		return "", r.errorf("expected identifier, got EOF")
	}
	if !isAlpha(b) && b != '_' {
		r.unreadByte()
		return "", r.errorf("expected identifier, got %q", rune(b))
	}

	var sb strings.Builder
	sb.WriteByte(b)
	for {
		b, err = r.peekByte()
		if err != nil {
			break
		}
		if isAlpha(b) || isDigit(b) || b == '_' || b == '-' {
			r.readByte() //nolint:errcheck
			sb.WriteByte(b)
		} else {
			break
		}
	}
	return sb.String(), nil
}

// ---------------------------------------------------------------------------
// String reading
// ---------------------------------------------------------------------------

// readString reads a single-quoted, double-quoted, or triple-quoted string.
func (r *reader) readString() (string, error) {
	quote, err := r.readByte()
	if err != nil {
		return "", r.errorf("expected string, got EOF")
	}
	if quote != '\'' && quote != '"' {
		r.unreadByte()
		return "", r.errorf("expected string, got %q", rune(quote))
	}

	// Check for triple-quote opening.
	if p, perr := r.buf.Peek(2); perr == nil && p[0] == quote && p[1] == quote {
		r.readByte() //nolint:errcheck // second quote
		r.readByte() //nolint:errcheck // third quote
		return r.readMultiLineString(quote)
	}

	// Single-line string.
	var sb strings.Builder
	for {
		b, err := r.readByte()
		if err != nil {
			return "", r.errorf("unterminated string")
		}
		if b == quote {
			return sb.String(), nil
		}
		if b == '\\' {
			ch, err := r.readEscape()
			if err != nil {
				return "", err
			}
			sb.WriteRune(ch)
			continue
		}
		if b == '\n' {
			return "", r.errorf("newline in single-line string")
		}
		if b == 0 {
			return "", r.errorf("null byte in string")
		}
		sb.WriteByte(b)
	}
}

// readEscape reads the character after '\' and returns the decoded rune.
func (r *reader) readEscape() (rune, error) {
	b, err := r.readByte()
	if err != nil {
		return 0, r.errorf("unterminated escape sequence")
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
	var val rune
	for i := 0; i < n; i++ {
		b, err := r.readByte()
		if err != nil {
			return 0, r.errorf("unterminated unicode escape")
		}
		d := hexVal(b)
		if d < 0 {
			return 0, r.errorf("invalid hex digit in unicode escape: %q", rune(b))
		}
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
func (r *reader) readMultiLineString(quote byte) (string, error) {
	// Next byte must be a newline.
	b, err := r.readByte()
	if err != nil {
		return "", r.errorf("expected newline after opening triple-quote, got EOF")
	}
	if b != '\n' {
		return "", r.errorf("expected newline after opening triple-quote, got %q", rune(b))
	}

	closingDelim := string([]byte{quote, quote, quote})

	// Collect raw source lines until the closing delimiter.
	var rawLines []string
	baseline := 0
	for {
		line, lerr := r.readRawLine()
		if lerr != nil {
			return "", r.errorf("unterminated multi-line string")
		}
		trimmed := strings.TrimLeft(line, " \t")
		if trimmed == closingDelim {
			baseline = len(line) - len(trimmed)
			break
		}
		rawLines = append(rawLines, line)
	}

	// Strip baseline indentation and process escapes.
	var result strings.Builder
	for i, line := range rawLines {
		if i > 0 {
			result.WriteByte('\n')
		}
		// Blank lines are exempt from the indentation check.
		if strings.TrimSpace(line) == "" {
			if len(line) > baseline {
				result.WriteString(line[baseline:])
			}
			continue
		}
		leading := countLeadingWS(line)
		if leading < baseline {
			return "", r.errorf("insufficient indentation in multi-line string (line %d)", i+1)
		}
		stripped := line[baseline:]
		processed, perr := processEscapes(stripped)
		if perr != "" {
			return "", r.errorf("%s", perr)
		}
		result.WriteString(processed)
	}
	return result.String(), nil
}

// readRawLine reads bytes until a newline (or EOF) without escape processing.
// If bytes were read before EOF, the partial line is returned without error.
func (r *reader) readRawLine() (string, error) {
	var sb strings.Builder
	for {
		b, err := r.readByte()
		if err != nil {
			if sb.Len() > 0 {
				return sb.String(), nil
			}
			return "", err
		}
		if b == '\n' {
			return sb.String(), nil
		}
		sb.WriteByte(b)
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
				return "", "incomplete \\u escape"
			}
			val, ok := parseHexDigits(s[i+1 : i+5])
			if !ok {
				return "", "invalid hex digit in \\u escape"
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
				return "", "incomplete \\U escape"
			}
			val, ok := parseHexDigits(s[i+1 : i+9])
			if !ok {
				return "", "invalid hex digit in \\U escape"
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
	if err != nil || !isDigit(b) {
		if err == nil {
			r.unreadByte()
		}
		return r.errorf("expected digit")
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
		if err != nil || !isDigit(b) {
			if err == nil {
				r.unreadByte()
			}
			return r.errorf("expected digit")
		}
		sb.WriteByte(b)
	}
	return nil
}

// readExactHex reads exactly n hex digits.
func (r *reader) readExactHex(sb *strings.Builder, n int) error {
	for i := 0; i < n; i++ {
		b, err := r.readByte()
		if err != nil || !isHex(b) {
			if err == nil {
				r.unreadByte()
			}
			return r.errorf("expected hex digit")
		}
		sb.WriteByte(b)
	}
	return nil
}

// readPrefixedDigits reads digits for 0x/0b/0o literals.
// check validates whether a byte is a valid digit for the given base.
func (r *reader) readPrefixedDigits(sb *strings.Builder, check func(byte) bool) error {
	b, err := r.readByte()
	if err != nil || !check(b) {
		if err == nil {
			r.unreadByte()
		}
		return r.errorf("expected digit after base prefix")
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
	var sb strings.Builder

	// Optional negative sign.
	if b, err := r.peekByte(); err == nil && b == '-' {
		r.readByte() //nolint:errcheck
		sb.WriteByte('-')
	}

	// Peek at first digit.
	first, err := r.peekByte()
	if err != nil || !isDigit(first) {
		return "", r.errorf("expected digit in integer")
	}

	if first == '0' {
		r.readByte() //nolint:errcheck
		sb.WriteByte('0')
		// Check for base prefix.
		if b, err := r.peekByte(); err == nil {
			switch b {
			case 'x':
				r.readByte() //nolint:errcheck
				sb.WriteByte('x')
				if err := r.readPrefixedDigits(&sb, isHex); err != nil {
					return "", err
				}
				return sb.String(), nil
			case 'b':
				r.readByte() //nolint:errcheck
				sb.WriteByte('b')
				if err := r.readPrefixedDigits(&sb, isBin); err != nil {
					return "", err
				}
				return sb.String(), nil
			case 'o':
				r.readByte() //nolint:errcheck
				sb.WriteByte('o')
				if err := r.readPrefixedDigits(&sb, isOct); err != nil {
					return "", err
				}
				return sb.String(), nil
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
				sb.WriteByte(b)
			} else {
				break
			}
		}
		return sb.String(), nil
	}

	// Regular decimal DIGIT_SEP.
	if err := r.readDigitSep(&sb); err != nil {
		return "", err
	}
	return sb.String(), nil
}

// ---------------------------------------------------------------------------
// Decimal reading
// ---------------------------------------------------------------------------

// readDec reads DEC = ['-'] DIGIT_SEP '.' DIGIT_SEP.
func (r *reader) readDec() (string, error) {
	var sb strings.Builder

	if b, err := r.peekByte(); err == nil && b == '-' {
		r.readByte() //nolint:errcheck
		sb.WriteByte('-')
	}
	if err := r.readDigitSep(&sb); err != nil {
		return "", err
	}
	if err := r.expectByte('.'); err != nil {
		return "", err
	}
	sb.WriteByte('.')
	if err := r.readDigitSep(&sb); err != nil {
		return "", err
	}
	return sb.String(), nil
}

// ---------------------------------------------------------------------------
// Float reading
// ---------------------------------------------------------------------------

// readFloat reads FLOAT = ['-'] DIGIT_SEP ('.' DIGIT_SEP)? ('e'|'E') [+-]? DIGIT+.
func (r *reader) readFloat() (string, error) {
	var sb strings.Builder

	if b, err := r.peekByte(); err == nil && b == '-' {
		r.readByte() //nolint:errcheck
		sb.WriteByte('-')
	}
	if err := r.readDigitSep(&sb); err != nil {
		return "", err
	}

	// Optional '.' DIGIT_SEP.
	if b, err := r.peekByte(); err == nil && b == '.' {
		r.readByte() //nolint:errcheck
		sb.WriteByte('.')
		if err := r.readDigitSep(&sb); err != nil {
			return "", err
		}
	}

	// Mandatory exponent.
	b, err := r.peekByte()
	if err != nil || (b != 'e' && b != 'E') {
		return "", r.errorf("expected exponent ('e' or 'E') in float")
	}
	r.readByte() //nolint:errcheck
	sb.WriteByte(b)

	// Optional sign.
	if b, err := r.peekByte(); err == nil && (b == '+' || b == '-') {
		r.readByte() //nolint:errcheck
		sb.WriteByte(b)
	}

	// DIGIT+ (no underscores in exponent per spec).
	count := 0
	for {
		b, err := r.peekByte()
		if err != nil || !isDigit(b) {
			break
		}
		r.readByte() //nolint:errcheck
		sb.WriteByte(b)
		count++
	}
	if count == 0 {
		return "", r.errorf("expected digits in float exponent")
	}
	return sb.String(), nil
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
	var sb strings.Builder
	if err := r.readExactDigits(&sb, 4); err != nil {
		return "", err
	}
	if err := r.expectByte('-'); err != nil {
		return "", err
	}
	sb.WriteByte('-')
	if err := r.readExactDigits(&sb, 2); err != nil {
		return "", err
	}
	if err := r.expectByte('-'); err != nil {
		return "", err
	}
	sb.WriteByte('-')
	if err := r.readExactDigits(&sb, 2); err != nil {
		return "", err
	}
	return sb.String(), nil
}

// readTime reads TIME = DIGIT{2}:DIGIT{2}:DIGIT{2}(.DIGIT+)? TZ.
func (r *reader) readTime() (string, error) {
	var sb strings.Builder
	if err := r.readExactDigits(&sb, 2); err != nil {
		return "", err
	}
	if err := r.expectByte(':'); err != nil {
		return "", err
	}
	sb.WriteByte(':')
	if err := r.readExactDigits(&sb, 2); err != nil {
		return "", err
	}
	if err := r.expectByte(':'); err != nil {
		return "", err
	}
	sb.WriteByte(':')
	if err := r.readExactDigits(&sb, 2); err != nil {
		return "", err
	}

	// Optional fractional seconds.
	if b, err := r.peekByte(); err == nil && b == '.' {
		r.readByte() //nolint:errcheck
		sb.WriteByte('.')
		count := 0
		for {
			b, err := r.peekByte()
			if err != nil || !isDigit(b) {
				break
			}
			r.readByte() //nolint:errcheck
			sb.WriteByte(b)
			count++
		}
		if count == 0 {
			return "", r.errorf("expected digits after '.' in time")
		}
	}

	// Timezone.
	b, err := r.peekByte()
	if err != nil {
		return "", r.errorf("expected timezone in time")
	}
	if b == 'Z' {
		r.readByte() //nolint:errcheck
		sb.WriteByte('Z')
	} else if b == '+' || b == '-' {
		r.readByte() //nolint:errcheck
		sb.WriteByte(b)
		if err := r.readExactDigits(&sb, 2); err != nil {
			return "", err
		}
		if err := r.expectByte(':'); err != nil {
			return "", err
		}
		sb.WriteByte(':')
		if err := r.readExactDigits(&sb, 2); err != nil {
			return "", err
		}
	} else {
		return "", r.errorf("expected timezone (Z or ±HH:MM) in time, got %q", rune(b))
	}
	return sb.String(), nil
}

// readDateTime reads DATETIME = DATE 'T' TIME.
func (r *reader) readDateTime() (string, error) {
	var sb strings.Builder
	date, err := r.readDate()
	if err != nil {
		return "", err
	}
	sb.WriteString(date)
	if err := r.expectByte('T'); err != nil {
		return "", err
	}
	sb.WriteByte('T')
	t, err := r.readTime()
	if err != nil {
		return "", err
	}
	sb.WriteString(t)
	return sb.String(), nil
}

// ---------------------------------------------------------------------------
// UUID reading
// ---------------------------------------------------------------------------

// readUUID reads UUID = HEX{8}-HEX{4}-HEX{4}-HEX{4}-HEX{12}.
func (r *reader) readUUID() (string, error) {
	var sb strings.Builder
	segments := [5]int{8, 4, 4, 4, 12}
	for i, n := range segments {
		if i > 0 {
			if err := r.expectByte('-'); err != nil {
				return "", err
			}
			sb.WriteByte('-')
		}
		if err := r.readExactHex(&sb, n); err != nil {
			return "", err
		}
	}
	return sb.String(), nil
}

// ---------------------------------------------------------------------------
// Atom reading
// ---------------------------------------------------------------------------

// readAtom reads an identifier and validates it against the allowed set.
func (r *reader) readAtom(allowed []string) (string, error) {
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
