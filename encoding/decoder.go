package encoding

import (
	"io"
)

// Decoder reads a PAKT document from an input source and emits [Event] values
// one at a time, similar to [encoding/json.Decoder].
type Decoder struct {
	r    *reader
	sm   *stateMachine
	done bool // true after document fully parsed
}

// NewDecoder returns a Decoder that reads PAKT input from r.
func NewDecoder(r io.Reader) *Decoder {
	rd := newReader(r)
	return &Decoder{
		r:  rd,
		sm: newStateMachine(rd),
	}
}

// Close releases internal resources (such as pooled buffers) back to their
// pools. Callers should defer Close after creating a Decoder. It is safe to
// call Close multiple times.
func (d *Decoder) Close() {
	if d.sm != nil {
		d.sm.release()
		d.sm = nil
	}
	if d.r != nil {
		d.r.release()
	}
}

// Decode reads the next event from the PAKT source.
//
// On each call it returns the next [Event] in document order. When the
// document is fully consumed, it returns a zero Event and [io.EOF].
func (d *Decoder) Decode() (Event, error) {
	if d.done {
		return Event{}, io.EOF
	}
	if d.sm == nil {
		d.sm = newStateMachine(d.r)
	}

	ev, err := d.sm.step()
	if err != nil {
		if err == io.EOF {
			d.done = true
			d.r.release()
			return Event{}, io.EOF
		}
		d.done = true
		d.r.release()
		return Event{}, err
	}

	return ev, nil
}
