package encoding

import "io"

// Decoder reads a PAKT document from an input stream and emits [Event] values
// one at a time, similar to [encoding/json.Decoder]. An optional spec
// projection may be applied via [Decoder.SetSpec] to filter and validate the
// stream against a .spec.pakt definition.
type Decoder struct {
	r        *reader
	sm       *stateMachine
	spec     *Spec
	specSeen map[string]struct{} // tracks which spec fields were seen
	done     bool                // true after document fully parsed
}

// NewDecoder returns a Decoder that reads PAKT input from r.
func NewDecoder(r io.Reader) *Decoder {
	rd := newReader(r)
	return &Decoder{
		r:  rd,
		sm: newStateMachine(rd),
	}
}

// SetSpec applies a spec projection to the decoder. The spec is parsed from r,
// which should contain a valid .spec.pakt document. Fields matching the spec
// are parsed and emitted; unmatched fields are skipped. After the document is
// fully parsed, missing spec fields cause an error.
func (d *Decoder) SetSpec(r io.Reader) error {
	spec, err := ParseSpec(r)
	if err != nil {
		return err
	}
	d.spec = spec
	d.specSeen = make(map[string]struct{})
	return nil
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

// Decode reads the next event from the PAKT stream.
//
// On each call it returns the next [Event] in document order. When the
// document is fully consumed, it returns a zero Event and [io.EOF].
// If a spec is active, unmatched fields are silently skipped and missing
// spec fields produce an error at EOF.
func (d *Decoder) Decode() (Event, error) {
	if d.spec != nil {
		return d.decodeWithSpec()
	}
	return d.decodeStreaming()
}

func (d *Decoder) decodeStreaming() (Event, error) {
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
