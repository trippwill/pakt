package encoding

import (
	"bytes"
	"io"
	"iter"
)

// Statement represents a top-level PAKT statement header.
// It is valid only until the next call to [StatementReader.Statements] iteration
// or [StatementReader.Close].
type Statement struct {
	Name   string // statement name (e.g., "server", "events")
	Type   Type   // declared PAKT type annotation
	IsPack bool   // true if << (pack statement)
}

// StatementReader reads PAKT statements one at a time from a stream.
// It is the primary deserialization interface, wrapping a [Decoder] and
// providing statement-level navigation with iterator-based pack streaming.
type StatementReader struct {
	dec     *Decoder
	opts    *options
	err     error  // first error encountered during iteration
	current *Event // most recently yielded statement-start event, or nil
	depth   int    // nesting depth within current statement (0 = at statement level)
	inPack  bool   // true while iterating pack elements
}

// NewStatementReader creates a StatementReader from any [io.Reader].
func NewStatementReader(r io.Reader, opts ...Option) *StatementReader {
	return &StatementReader{
		dec:  NewDecoder(r),
		opts: buildOptions(opts),
	}
}

// NewStatementReaderFromBytes creates a StatementReader from a byte slice.
func NewStatementReaderFromBytes(data []byte, opts ...Option) *StatementReader {
	return NewStatementReader(bytes.NewReader(data), opts...)
}

// Close releases all resources held by the StatementReader.
// It is safe to call Close multiple times.
func (sr *StatementReader) Close() {
	if sr.dec != nil {
		sr.dec.Close()
	}
}

// Err returns the first error encountered during iteration, or nil if
// iteration completed successfully or hasn't started.
func (sr *StatementReader) Err() error {
	return sr.err
}

// Statements returns an iterator over the top-level statements in the PAKT unit.
// Each [Statement] is valid only for the current iteration step.
//
// On error, iteration stops. Call [StatementReader.Err] after the loop to
// check for errors.
//
// Within each iteration step, the caller should read the statement's value
// using [ReadValue], [PackItems], or [StatementReader.Skip].
// If the caller does not consume the statement's value, Statements
// automatically skips to the next statement.
func (sr *StatementReader) Statements() iter.Seq[Statement] {
	return func(yield func(Statement) bool) {
		for {
			// If there's an unconsumed statement from the previous iteration,
			// skip its remaining events.
			if sr.current != nil {
				if err := sr.skipCurrent(); err != nil {
					sr.setErr(err)
					return
				}
			}

			ev, err := sr.dec.Decode()
			if err != nil {
				if err != io.EOF {
					sr.setErr(err)
				}
				return
			}

			// We expect statement-start events at the top level.
			switch ev.Kind {
			case EventAssignStart, EventListPackStart, EventMapPackStart:
				// Good — this is a statement header.
			default:
				sr.setErr(&DeserializeError{
					Pos:     ev.Pos,
					Message: "expected statement start event, got " + ev.Kind.String(),
				})
				return
			}

			sr.current = &ev
			sr.depth = 0
			sr.inPack = ev.Kind.IsPackStart()

			var typ Type
			if ev.Type != nil {
				typ = *ev.Type
			}

			stmt := Statement{
				Name:   ev.Name,
				Type:   typ,
				IsPack: sr.inPack,
			}

			if !yield(stmt) {
				return
			}
		}
	}
}

// Skip advances past the current statement or pack element without
// deserializing. Use for unknown or unwanted statements.
func (sr *StatementReader) Skip() error {
	return sr.skipCurrent()
}

// skipCurrent consumes all remaining events for the current statement.
func (sr *StatementReader) skipCurrent() error {
	if sr.current == nil {
		return nil
	}

	endKind := sr.endKindForCurrent()

	for {
		ev, err := sr.dec.Decode()
		if err != nil {
			if err == io.EOF {
				sr.current = nil
				return nil
			}
			sr.current = nil
			return err
		}

		if ev.Kind == endKind && sr.depth == 0 {
			sr.current = nil
			return nil
		}

		// Track nesting depth for composite values within the statement.
		if ev.Kind.IsCompositeStart() || ev.Kind.IsPackStart() {
			sr.depth++
		} else if ev.Kind.IsCompositeEnd() || ev.Kind.IsPackEnd() {
			sr.depth--
		}
	}
}

// endKindForCurrent returns the EventKind that terminates the current statement.
func (sr *StatementReader) endKindForCurrent() EventKind {
	if sr.current == nil {
		return EventError
	}
	switch sr.current.Kind {
	case EventAssignStart:
		return EventAssignEnd
	case EventListPackStart:
		return EventListPackEnd
	case EventMapPackStart:
		return EventMapPackEnd
	default:
		return EventError
	}
}

// setErr records the first error.
func (sr *StatementReader) setErr(err error) {
	if sr.err == nil {
		sr.err = err
	}
}

// nextEvent reads the next event from the decoder, tracking nesting depth.
// It returns io.EOF when the current statement/pack is exhausted.
func (sr *StatementReader) nextEvent() (Event, error) {
	ev, err := sr.dec.Decode()
	if err != nil {
		return Event{}, err
	}

	endKind := sr.endKindForCurrent()

	// Check for end of current statement.
	if ev.Kind == endKind && sr.depth == 0 {
		sr.current = nil
		return Event{}, io.EOF
	}

	return ev, nil
}
