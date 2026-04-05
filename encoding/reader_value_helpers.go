package encoding

import "fmt"

// ---------------------------------------------------------------------------
// Pre-computed index name table for tuples and lists
// ---------------------------------------------------------------------------

var indexNames [64]string

func init() {
	for i := range indexNames {
		indexNames[i] = fmt.Sprintf("[%d]", i)
	}
}

func indexName(i int) string {
	if i < len(indexNames) {
		return indexNames[i]
	}
	return fmt.Sprintf("[%d]", i)
}

// ---------------------------------------------------------------------------
// SEP handling
// ---------------------------------------------------------------------------

// readSep attempts to consume a separator (comma or newline).
// It first skips WS and comments (but not newlines). Then:
//   - if the next byte is ',' → consume it, skip trailing WS/NL/comments, return true
//   - if the next byte is '\n' or '\r' → consume it, skip trailing WS/NL/comments, return true
//   - otherwise → return false
func (r *reader) readSep() (bool, error) {
	r.skipInsignificant(false) // skip WS and comments, but not newlines
	b, err := r.peekByte()
	if err != nil {
		return false, nil // EOF is not an error for SEP
	}
	if b == ',' {
		r.readByte()              //nolint:errcheck
		r.skipInsignificant(true) // skip WS, NL, comments after comma
		return true, nil
	}
	if b == '\n' || b == '\r' {
		r.readByte()              //nolint:errcheck
		r.skipInsignificant(true) // skip WS, NL, comments after newline
		return true, nil
	}
	return false, nil
}

// ---------------------------------------------------------------------------
// Scalar value helpers
// ---------------------------------------------------------------------------

// readScalarDirect reads a scalar value and returns it without emitting an event.
func (r *reader) readScalarDirect(kind TypeKind) (string, Pos, error) {
	pos := r.pos
	var val string
	var err error

	switch kind {
	case TypeStr:
		val, err = r.readString()
	case TypeInt:
		val, err = r.readInt()
	case TypeDec:
		val, err = r.readDec()
	case TypeFloat:
		val, err = r.readFloat()
	case TypeBool:
		val, err = r.readBool()
	case TypeUUID:
		val, err = r.readUUID()
	case TypeDate:
		val, err = r.readDate()
	case TypeTime:
		val, err = r.readTime()
	case TypeDateTime:
		val, err = r.readDateTime()
	case TypeBin:
		val, err = r.readBin()
	default:
		return "", pos, r.errorf("unknown scalar type kind %d", int(kind))
	}
	return val, pos, err
}

// peekNil checks whether the next non-WS content is the keyword "nil" followed
// by a non-identifier byte. It does not consume any input.
func (r *reader) peekNil() bool {
	p, err := r.src.Peek(256) // peek a generous amount
	if err != nil && len(p) == 0 {
		return false
	}
	i := 0
	for i < len(p) && (p[i] == ' ' || p[i] == '\t') {
		i++
	}
	if i+3 > len(p) {
		return false
	}
	if p[i] != 'n' || p[i+1] != 'i' || p[i+2] != 'l' {
		return false
	}
	if i+3 < len(p) {
		next := p[i+3]
		if isAlpha(next) || isDigit(next) || next == '_' || next == '-' {
			return false
		}
	}
	return true
}
