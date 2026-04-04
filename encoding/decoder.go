package encoding

import "io"

// Decoder reads a PAKT document from an input stream and emits [Event] values
// one at a time, similar to [encoding/json.Decoder]. An optional spec
// projection may be applied via [Decoder.SetSpec] to filter and validate the
// stream against a .spec.pakt definition.
type Decoder struct {
	scanner *Scanner
	spec    io.Reader
}

// NewDecoder returns a Decoder that reads PAKT input from r.
func NewDecoder(r io.Reader) *Decoder {
	return &Decoder{
		scanner: NewScanner(r),
	}
}

// SetSpec applies a spec projection to the decoder. The spec is read from r,
// which should contain a valid .spec.pakt document. Fields matching the spec
// are parsed and emitted; unmatched fields are skipped.
//
// TODO: implement — currently stores the reader for future use.
func (d *Decoder) SetSpec(r io.Reader) error {
	d.spec = r
	return nil
}

// Decode reads the next event from the PAKT stream.
//
// TODO: implement — currently returns a zero Event and io.EOF.
func (d *Decoder) Decode() (Event, error) {
	return Event{}, io.EOF
}
