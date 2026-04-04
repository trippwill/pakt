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
// Projection-aware assignment reading
// ---------------------------------------------------------------------------

// readAssignmentWithSpec reads a top-level assignment, skipping the value if
// the field name is not in the spec. Returns the field name and whether it was
// found in the spec.
func (r *reader) readAssignmentWithSpec(spec *Spec) (name string, matched bool, err error) {
	r.skipInsignificant(true)

	if _, err := r.peekByte(); err != nil {
		return "", false, err // io.EOF propagates
	}

	identPos := r.pos
	name, err = r.readIdent()
	if err != nil {
		return "", false, err
	}

	if _, dup := r.seen[name]; dup {
		return "", false, Wrapf(identPos, ErrDuplicateName, "duplicate root name %q", name)
	}
	r.seen[name] = struct{}{}

	typ, err := r.readTypeAnnot()
	if err != nil {
		return "", false, err
	}

	r.skipWS()
	if err := r.expectByte('='); err != nil {
		return "", false, err
	}
	r.skipWS()

	if _, ok := spec.Fields[name]; !ok {
		// Not in spec — skip the value without emitting events.
		if err := r.skipValue(); err != nil {
			return name, false, err
		}
		return name, false, nil
	}

	// In spec — parse normally.
	typeStr := typ.String()
	r.emit(EventAssignStart, identPos, name, typeStr, "")

	if err := r.readValue(typ, name); err != nil {
		return name, true, err
	}

	r.emit(EventAssignEnd, r.pos, name, typeStr, "")
	return name, true, nil
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

// skipString skips a single-line or triple-quoted string.
func (r *reader) skipString() error {
	quote, err := r.readByte()
	if err != nil {
		return r.wrapf(ErrUnexpectedEOF, "expected string, got EOF")
	}

	// Check for triple-quote.
	if p, perr := r.buf.Peek(2); perr == nil && p[0] == quote && p[1] == quote {
		r.readByte() //nolint:errcheck
		r.readByte() //nolint:errcheck
		return r.skipTripleQuotedString(quote)
	}

	// Single-line string: skip until matching unescaped quote.
	for {
		b, err := r.readByte()
		if err != nil {
			return r.wrapf(ErrUnexpectedEOF, "unterminated string")
		}
		if b == '\\' {
			// Skip the escaped character.
			if _, err := r.readByte(); err != nil {
				return r.wrapf(ErrUnexpectedEOF, "unterminated escape in string")
			}
			continue
		}
		if b == quote {
			return nil
		}
	}
}

// skipTripleQuotedString skips past the closing triple-quote delimiter.
func (r *reader) skipTripleQuotedString(quote byte) error {
	// The opening triple-quote has been consumed. Skip until closing triple-quote.
	consecutive := 0
	for {
		b, err := r.readByte()
		if err != nil {
			return r.wrapf(ErrUnexpectedEOF, "unterminated triple-quoted string")
		}
		if b == '\\' {
			// Skip escaped character.
			if _, err := r.readByte(); err != nil {
				return r.wrapf(ErrUnexpectedEOF, "unterminated escape in triple-quoted string")
			}
			consecutive = 0
			continue
		}
		if b == quote {
			consecutive++
			if consecutive == 3 {
				return nil
			}
		} else {
			consecutive = 0
		}
	}
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

// ---------------------------------------------------------------------------
// Decoder integration
// ---------------------------------------------------------------------------

// decodeWithSpec reads the next assignment using spec-based projection.
// It returns io.EOF when the document is fully consumed and all spec fields
// have been accounted for.
func (d *Decoder) decodeWithSpec() (Event, error) {
	// Return buffered events first.
	if d.idx < len(d.r.events) {
		ev := d.r.events[d.idx]
		d.idx++
		if ev.Kind == EventError {
			return ev, ev.Err
		}
		return ev, nil
	}

	if d.done {
		return Event{}, io.EOF
	}

	// Keep reading assignments until we get one that matches the spec or EOF.
	for {
		d.r.events = d.r.events[:0]
		d.idx = 0

		name, matched, err := d.r.readAssignmentWithSpec(d.spec)
		if err != nil {
			if err == io.EOF {
				// Document fully consumed. Check for missing spec fields.
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

		// Track which spec fields were seen.
		d.specSeen[name] = struct{}{}

		if !matched {
			continue // skip, try next assignment
		}

		if len(d.r.events) == 0 {
			continue
		}

		ev := d.r.events[d.idx]
		d.idx++
		if ev.Kind == EventError {
			return ev, ev.Err
		}
		return ev, nil
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
