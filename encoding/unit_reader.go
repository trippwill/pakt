package encoding

import (
	"bytes"
	"io"
	"iter"
)

// Property represents a top-level PAKT property header.
// It is valid only until the next call to [UnitReader.Properties] iteration
// or [UnitReader.Close].
type Property struct {
	Name   string // property name (e.g., "server", "events")
	Type   Type   // declared PAKT type annotation
	Pos    Pos    // source position of the property
	IsPack bool   // true if << (pack statement)
}

// UnitReader reads PAKT statements one at a time from a stream.
// It is the primary deserialization interface, wrapping a [Decoder] and
// providing statement-level navigation with iterator-based pack streaming.
type UnitReader struct {
	dec     *Decoder
	opts    *options
	err     error  // first error encountered during iteration
	current *Event // most recently yielded statement-start event, or nil
	depth   int    // nesting depth within current statement (0 = at statement level)
	inPack  bool   // true while iterating pack elements
	pending *Event // one-event pushback for navigation helpers
}

// NewUnitReader creates a UnitReader from any [io.Reader].
func NewUnitReader(r io.Reader, opts ...Option) *UnitReader {
	return &UnitReader{
		dec:  NewDecoder(r),
		opts: buildOptions(opts),
	}
}

// NewUnitReaderFromBytes creates a UnitReader from a byte slice.
func NewUnitReaderFromBytes(data []byte, opts ...Option) *UnitReader {
	return NewUnitReader(bytes.NewReader(data), opts...)
}

// Close releases all resources held by the UnitReader.
// It is safe to call Close multiple times.
func (sr *UnitReader) Close() {
	if sr.dec != nil {
		sr.dec.Close()
	}
}

// Err returns the first error encountered during iteration, or nil if
// iteration completed successfully or hasn't started.
func (sr *UnitReader) Err() error {
	return sr.err
}

// Properties returns an iterator over the top-level properties in the PAKT unit.
// Each [Property] is valid only for the current iteration step.
//
// On error, iteration stops. Call [UnitReader.Err] after the loop to
// check for errors.
//
// Within each iteration step, the caller should read the property's value
// using [ReadValue], [PackItems], or [UnitReader.Skip].
// If the caller does not consume the property's value, Properties
// automatically skips to the next property.
func (sr *UnitReader) Properties() iter.Seq[Property] {
	return func(yield func(Property) bool) {
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

			stmt := Property{
				Name:   ev.Name,
				Type:   typ,
				Pos:    ev.Pos,
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
func (sr *UnitReader) Skip() error {
	return sr.skipCurrent()
}

// skipCurrent consumes all remaining events for the current statement.
func (sr *UnitReader) skipCurrent() error {
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
func (sr *UnitReader) endKindForCurrent() EventKind {
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
func (sr *UnitReader) setErr(err error) {
	if sr.err == nil {
		sr.err = err
	}
}

// pushBack stores an event for the next nextEvent() call.
func (sr *UnitReader) pushBack(ev Event) {
	sr.pending = &ev
}

// nextEvent reads the next event from the decoder, tracking nesting depth.
// It returns io.EOF when the current statement/pack is exhausted.
// If a pending event was pushed back, it is returned first.
func (sr *UnitReader) nextEvent() (Event, error) {
	var ev Event
	var err error

	if sr.pending != nil {
		ev = *sr.pending
		sr.pending = nil
	} else {
		ev, err = sr.dec.Decode()
		if err != nil {
			return Event{}, err
		}
	}

	endKind := sr.endKindForCurrent()

	// Check for end of current statement.
	if ev.Kind == endKind && sr.depth == 0 {
		sr.current = nil
		return Event{}, io.EOF
	}

	// Track nesting depth for composite values within the statement.
	if ev.Kind.IsCompositeStart() || ev.Kind.IsPackStart() {
		sr.depth++
	} else if ev.Kind.IsCompositeEnd() || ev.Kind.IsPackEnd() {
		sr.depth--
	}

	return ev, nil
}
