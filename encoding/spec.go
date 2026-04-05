package encoding

import (
	"fmt"
	"io"
)

// Spec represents a parsed .spec.pakt file — a map of expected field names to
// their types. A spec enables projection: only matching fields are fully parsed
// while unmatched fields are skipped.
type Spec struct {
	Fields map[string]Type
}

// ParseSpec reads a .spec.pakt document from r and returns a [Spec].
// The format is `(IDENT COLON type)*` — like assignments but without `= value`.
// Duplicate field names cause an error.
func ParseSpec(r io.Reader) (*Spec, error) {
	rd := newReader(r)
	fields := make(map[string]Type)

	for {
		rd.skipInsignificant(true)

		if _, err := rd.peekByte(); err != nil {
			break // EOF
		}

		identPos := rd.pos
		name, err := rd.readIdent()
		if err != nil {
			return nil, err
		}

		if _, dup := fields[name]; dup {
			return nil, Wrapf(identPos, ErrDuplicateName, "duplicate field %q in spec", name)
		}

		typ, err := rd.readTypeAnnot()
		if err != nil {
			return nil, err
		}

		fields[name] = typ
	}

	return &Spec{Fields: fields}, nil
}

// ---------------------------------------------------------------------------
// skipValue — fast skip past any value form without allocating or emitting
// ---------------------------------------------------------------------------

func (r *reader) skipValue() error {
	r.skipWS()
	b, err := r.peekByte()
	if err != nil {
		return r.wrapf(ErrUnexpectedEOF, "expected value, got EOF")
	}

	switch {
	case r.peekRawStringStart():
		return r.skipString()
	case r.peekBinLiteralStart():
		return r.skipBinLiteral()
	case b == '\'' || b == '"':
		return r.skipString()
	case b == '{':
		return r.skipComposite('{', '}')
	case b == '(':
		return r.skipComposite('(', ')')
	case b == '[':
		return r.skipComposite('[', ']')
	case b == '<':
		return r.skipComposite('<', '>')
	case b == '|':
		return r.skipAtom()
	case b == '.':
		return r.skipNumberLike()
	case b == 't', b == 'f', b == 'n':
		return r.skipKeywordOrAtom()
	case isDigit(b) || b == '-':
		return r.skipNumberLike()
	case isAlpha(b) || b == '_':
		return r.skipKeywordOrAtom()
	default:
		return r.errorf("unexpected byte %q at start of value", rune(b))
	}
}

// skipString skips a single-line or triple-quoted string, including raw forms.
func (r *reader) skipString() error {
	raw := false
	start, err := r.readByte()
	if err != nil {
		return r.wrapf(ErrUnexpectedEOF, "expected string, got EOF")
	}
	quote := start
	if start == 'r' {
		raw = true
		quote, err = r.readByte()
		if err != nil {
			return r.wrapf(ErrUnexpectedEOF, "expected quote after raw string prefix, got EOF")
		}
	}
	if quote != '\'' && quote != '"' {
		if raw {
			return r.errorf("expected quote after raw string prefix, got %q", rune(quote))
		}
		r.unreadByte()
		return r.errorf("expected string, got %q", rune(quote))
	}

	// Check for triple-quote.
	if p, perr := r.buf.Peek(2); perr == nil && p[0] == quote && p[1] == quote {
		r.readByte() //nolint:errcheck
		r.readByte() //nolint:errcheck
		return r.skipTripleQuotedString(quote, raw)
	}

	// Single-line string: skip until matching unescaped quote.
	for {
		b, err := r.readByte()
		if err != nil {
			return r.wrapf(ErrUnexpectedEOF, "unterminated string")
		}
		if !raw && b == '\\' {
			// Skip the escaped character.
			if _, err := r.readByte(); err != nil {
				return r.wrapf(ErrUnexpectedEOF, "unterminated escape in string")
			}
			continue
		}
		if b == quote {
			return nil
		}
		if b == '\n' {
			return r.errorf("newline in single-line string")
		}
		if b == 0 {
			return r.errorf("null byte in string")
		}
	}
}

// skipTripleQuotedString skips past the closing triple-quote delimiter.
func (r *reader) skipTripleQuotedString(quote byte, raw bool) error {
	return r.consumeMultiLineString(quote, raw, nil)
}

func (r *reader) skipBinLiteral() error {
	_, err := r.readBin()
	return err
}

// skipComposite skips a balanced-delimiter composite value. It handles nested
// composites and strings containing delimiter characters.
func (r *reader) skipComposite(open, close byte) error {
	if _, err := r.readByte(); err != nil {
		return r.wrapf(ErrUnexpectedEOF, "expected %q, got EOF", rune(open))
	}
	depth := 1
	for depth > 0 {
		b, err := r.readByte()
		if err != nil {
			return r.wrapf(ErrUnexpectedEOF, "unterminated composite value (expected %q)", rune(close))
		}

		switch b {
		case open:
			depth++
		case close:
			depth--
		case '\'', '"':
			// Must skip string content to avoid false delimiter matches.
			r.unreadByte()
			if err := r.skipString(); err != nil {
				return err
			}
		case 'r':
			if p, err := r.buf.Peek(1); err == nil && (p[0] == '\'' || p[0] == '"') {
				r.unreadByte()
				if err := r.skipString(); err != nil {
					return err
				}
			}
		case 'x', 'b':
			if p, err := r.buf.Peek(1); err == nil && p[0] == '\'' {
				r.unreadByte()
				if err := r.skipBinLiteral(); err != nil {
					return err
				}
			}
		case '#':
			// Skip comment to avoid false matches in comment text.
			r.skipToNewline()
		// Also handle other composite delimiters inside the value.
		case '{':
			if open != '{' {
				if err := r.skipCompositeInner('{', '}'); err != nil {
					return err
				}
			}
		case '(':
			if open != '(' {
				if err := r.skipCompositeInner('(', ')'); err != nil {
					return err
				}
			}
		case '[':
			if open != '[' {
				if err := r.skipCompositeInner('[', ']'); err != nil {
					return err
				}
			}
		case '<':
			if open != '<' {
				if err := r.skipCompositeInner('<', '>'); err != nil {
					return err
				}
			}
		}
	}
	return nil
}

// skipCompositeInner skips a nested composite that uses different delimiters
// than the outer composite being skipped.
func (r *reader) skipCompositeInner(open, close byte) error {
	depth := 1
	for depth > 0 {
		b, err := r.readByte()
		if err != nil {
			return r.wrapf(ErrUnexpectedEOF, "unterminated nested composite value")
		}
		switch b {
		case open:
			depth++
		case close:
			depth--
		case '\'', '"':
			r.unreadByte()
			if err := r.skipString(); err != nil {
				return err
			}
		case 'r':
			if p, err := r.buf.Peek(1); err == nil && (p[0] == '\'' || p[0] == '"') {
				r.unreadByte()
				if err := r.skipString(); err != nil {
					return err
				}
			}
		case 'x', 'b':
			if p, err := r.buf.Peek(1); err == nil && p[0] == '\'' {
				r.unreadByte()
				if err := r.skipBinLiteral(); err != nil {
					return err
				}
			}
		case '#':
			r.skipToNewline()
		case '{':
			if open != '{' {
				if err := r.skipCompositeInner('{', '}'); err != nil {
					return err
				}
			}
		case '(':
			if open != '(' {
				if err := r.skipCompositeInner('(', ')'); err != nil {
					return err
				}
			}
		case '[':
			if open != '[' {
				if err := r.skipCompositeInner('[', ']'); err != nil {
					return err
				}
			}
		case '<':
			if open != '<' {
				if err := r.skipCompositeInner('<', '>'); err != nil {
					return err
				}
			}
		}
	}
	return nil
}

// skipToNewline consumes bytes until a newline or EOF.
func (r *reader) skipToNewline() {
	for {
		b, err := r.readByte()
		if err != nil || b == '\n' {
			return
		}
	}
}

// skipKeywordOrAtom skips a keyword (true, false, nil) or bare atom identifier.
func (r *reader) skipKeywordOrAtom() error {
	// Read until non-identifier char.
	b, err := r.readByte()
	if err != nil {
		return r.wrapf(ErrUnexpectedEOF, "expected identifier, got EOF")
	}
	if !isAlpha(b) && b != '_' {
		r.unreadByte()
		return r.errorf("expected identifier, got %q", rune(b))
	}
	for {
		b, err = r.peekByte()
		if err != nil {
			return nil
		}
		if isAlpha(b) || isDigit(b) || b == '_' || b == '-' {
			r.readByte() //nolint:errcheck
		} else {
			return nil
		}
	}
}

// skipAtom skips a '|'-prefixed atom value.
func (r *reader) skipAtom() error {
	if _, err := r.readByte(); err != nil { // consume '|'
		return err
	}
	return r.skipKeywordOrAtom()
}

// skipNumberLike skips a number, date, time, datetime, or UUID literal.
// Reads until whitespace, newline, comma, closing delimiter, comment, or EOF.
func (r *reader) skipNumberLike() error {
	count := 0
	for {
		b, err := r.peekByte()
		if err != nil {
			if count == 0 {
				return r.wrapf(ErrUnexpectedEOF, "expected value, got EOF")
			}
			return nil
		}
		if b == ' ' || b == '\t' || b == '\n' || b == '\r' ||
			b == ',' || b == '}' || b == ')' || b == ']' || b == '>' || b == '#' {
			return nil
		}
		r.readByte() //nolint:errcheck
		count++
	}
}

func (r *reader) skipStatementBody(h statementHeader) error {
	if h.stream {
		return r.skipStreamBody(h.typ)
	}
	return r.skipValue()
}

func (r *reader) skipStreamBody(typ Type) error {
	switch {
	case typ.List != nil:
		return r.skipListStreamBody()
	case typ.Map != nil:
		return r.skipMapStreamBody()
	default:
		return r.errorf("stream type must be list or map, got %s", typ.String())
	}
}

func (r *reader) skipListStreamBody() error {
	for {
		r.skipInsignificant(true)
		b, err := r.peekByte()
		if err != nil {
			return nil
		}
		if !r.canStartValueInStream(b) {
			return nil
		}

		if err := r.skipValue(); err != nil {
			return err
		}

		sep, err := r.readSep()
		if err != nil {
			return err
		}
		if sep {
			continue
		}

		r.skipInsignificant(true)
		b, err = r.peekByte()
		if err != nil {
			return nil
		}
		if !r.canStartValueInStream(b) {
			return nil
		}
		return r.errorf("expected separator between stream items")
	}
}

func (r *reader) skipMapStreamBody() error {
	for {
		r.skipInsignificant(true)
		b, err := r.peekByte()
		if err != nil {
			return nil
		}
		if !r.canStartValueInStream(b) {
			return nil
		}

		if err := r.skipValue(); err != nil {
			return err
		}

		r.skipWS()
		if err := r.expectByte(';'); err != nil {
			return err
		}
		r.skipWS()

		if err := r.skipValue(); err != nil {
			return err
		}

		sep, err := r.readSep()
		if err != nil {
			return err
		}
		if sep {
			continue
		}

		r.skipInsignificant(true)
		b, err = r.peekByte()
		if err != nil {
			return nil
		}
		if !r.canStartValueInStream(b) {
			return nil
		}
		return r.errorf("expected separator between stream map entries")
	}
}

// ---------------------------------------------------------------------------
// Decoder integration
// ---------------------------------------------------------------------------

func (d *Decoder) decodeWithSpec() (Event, error) {
	if d.done {
		return Event{}, io.EOF
	}
	if d.sm == nil {
		d.sm = newStateMachine(d.r)
	}

	for {
		if !d.sm.atTop() {
			ev, err := d.sm.step()
			if err != nil {
				d.done = true
				d.r.release()
				return Event{}, err
			}
			return ev, nil
		}

		name, err := d.sm.primeNextMatchedStatement(d.spec)
		if err != nil {
			if err == io.EOF {
				if missingErr := d.checkMissingSpecFields(); missingErr != nil {
					d.done = true
					d.r.release()
					return Event{}, missingErr
				}
				d.done = true
				d.r.release()
				return Event{}, io.EOF
			}
			d.done = true
			d.r.release()
			return Event{}, err
		}

		d.specSeen[name] = struct{}{}
	}
}

// checkMissingSpecFields returns an error if any spec field was not seen
// in the document.
func (d *Decoder) checkMissingSpecFields() error {
	for name := range d.spec.Fields {
		if _, seen := d.specSeen[name]; !seen {
			return &ParseError{
				Pos:     Pos{},
				Message: fmt.Sprintf("spec field %q not found in document", name),
			}
		}
	}
	return nil
}
