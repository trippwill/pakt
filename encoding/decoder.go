package encoding

import (
	"io"
	"reflect"
)

// Decoder reads a PAKT document from an input stream and emits [Event] values
// one at a time, similar to [encoding/json.Decoder]. An optional spec
// projection may be applied via [Decoder.SetSpec] to filter and validate the
// stream against a .spec.pakt definition.
type Decoder struct {
	r    *reader
	sm   *stateMachine
	spec *Spec
	done bool // true after document fully parsed

	// streaming unmarshal state
	inStream   bool // true while inside a stream statement
	streamList *ListType
	streamMap  *MapType
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
// are parsed and emitted; unmatched fields are skipped. Type mismatches between
// the document and spec produce an error.
//
// NOTE: The spec API is experimental and its contract may evolve. Currently,
// specs act as advisory filters — they control which fields are parsed and
// validate types, but do not enforce presence of fields. Use pointer struct
// fields to detect absent values.
func (d *Decoder) SetSpec(r io.Reader) error {
	spec, err := ParseSpec(r)
	if err != nil {
		return err
	}
	d.spec = spec
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
// If a spec is active, unmatched fields are silently skipped.
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

// UnmarshalNext reads the next top-level statement from the PAKT stream and
// stores the result in the value pointed to by v. It uses a visitor-driven
// path that bypasses Event creation, writing parsed values directly into
// struct fields.
//
// For assignment statements (name:type = value), v must be a pointer to a
// struct with a matching field. For stream statements (name:type <<), the
// first call reads the stream header; subsequent calls read one element at
// a time when used with [Decoder.More].
//
// Returns [io.EOF] when no more statements remain.
func (d *Decoder) UnmarshalNext(v any) error {
	if d.done {
		return io.EOF
	}
	if d.sm == nil {
		d.sm = newStateMachine(d.r)
	}

	rv := reflect.ValueOf(v)
	if rv.Kind() != reflect.Pointer || rv.IsNil() {
		return &ParseError{Message: "pakt: UnmarshalNext requires a non-nil pointer"}
	}
	rv = rv.Elem()

	// If we're mid-stream, read the next stream element.
	if d.inStream {
		return d.unmarshalNextStreamElement(rv)
	}

	// Read the next statement header.
	var h statementHeader
	var err error

	if d.spec != nil {
		h, err = d.nextMatchedHeader()
	} else {
		d.r.skipInsignificant(true)
		h, err = d.sm.readStatementHeader()
	}
	if err != nil {
		if err == io.EOF {
			d.done = true
			return io.EOF
		}
		d.done = true
		return err
	}

	if h.stream {
		// Enter stream mode.
		d.inStream = true
		if h.typ.List != nil {
			d.streamList = h.typ.List
		} else {
			d.streamMap = h.typ.Map
		}
		// For a struct target, try to set the stream into a matching field.
		if rv.Kind() == reflect.Struct {
			return d.unmarshalStreamIntoField(h, rv)
		}
		// For a slice/map target, unmarshal the entire stream.
		return d.unmarshalWholeStream(h, rv)
	}

	// Assignment statement — unmarshal into matching struct field or directly.
	if rv.Kind() == reflect.Struct {
		info, cerr := cachedStructFields(rv.Type())
		if cerr != nil {
			return cerr
		}
		fi, ok := info.fieldMap[h.name]
		if !ok {
			// Skip unknown statement body.
			return d.r.skipStatementBody(h)
		}
		d.r.skipWS()
		return d.sm.unmarshalValue(h.typ, rv.Field(fi.Index))
	}

	// Direct target — unmarshal the value into it.
	d.r.skipWS()
	return d.sm.unmarshalValue(h.typ, rv)
}

// More reports whether there are more elements to read. When inside a stream
// statement, it reports whether additional stream elements remain. When at
// the top level, it reports whether more statements exist.
func (d *Decoder) More() bool {
	if d.done {
		return false
	}
	if d.inStream {
		d.r.skipInsignificant(true)
		b, err := d.r.peekByte()
		if err != nil {
			d.inStream = false
			d.streamList = nil
			d.streamMap = nil
			return false
		}
		if !d.r.canStartValueInStream(b) {
			d.inStream = false
			d.streamList = nil
			d.streamMap = nil
			return false
		}
		return true
	}
	d.r.skipInsignificant(true)
	_, err := d.r.peekByte()
	return err == nil
}

func (d *Decoder) nextMatchedHeader() (statementHeader, error) {
	for {
		d.r.skipInsignificant(true)
		h, err := d.sm.readStatementHeader()
		if err != nil {
			return h, err
		}
		specType, ok := d.spec.Fields[h.name]
		if !ok {
			if err := d.r.skipStatementBody(h); err != nil {
				return statementHeader{}, err
			}
			continue
		}
		if specType.String() != h.typ.String() {
			return statementHeader{}, Wrapf(h.pos, ErrTypeMismatch,
				"spec field %q expected type %s, got %s", h.name, specType.String(), h.typ.String())
		}
		return h, nil
	}
}

func (d *Decoder) unmarshalStreamIntoField(h statementHeader, rv reflect.Value) error {
	info, err := cachedStructFields(rv.Type())
	if err != nil {
		return err
	}
	fi, ok := info.fieldMap[h.name]
	if !ok {
		// Skip entire stream.
		d.inStream = false
		d.streamList = nil
		d.streamMap = nil
		return d.r.skipStreamBody(h.typ)
	}
	target := rv.Field(fi.Index)
	if d.streamList != nil {
		err = d.sm.unmarshalStreamList(d.streamList, target)
	} else {
		err = d.sm.unmarshalStreamMap(d.streamMap, target)
	}
	d.inStream = false
	d.streamList = nil
	d.streamMap = nil
	return err
}

func (d *Decoder) unmarshalWholeStream(h statementHeader, rv reflect.Value) error {
	var err error
	if d.streamList != nil {
		err = d.sm.unmarshalStreamList(d.streamList, rv)
	} else {
		err = d.sm.unmarshalStreamMap(d.streamMap, rv)
	}
	d.inStream = false
	d.streamList = nil
	d.streamMap = nil
	return err
}

func (d *Decoder) unmarshalNextStreamElement(rv reflect.Value) error {
	if d.streamList != nil {
		return d.sm.unmarshalValue(d.streamList.Element, rv)
	}
	if d.streamMap != nil {
		// For map streams, caller gets key-value pairs.
		return d.sm.unmarshalValue(d.streamMap.Value, rv)
	}
	return &ParseError{Message: "pakt: not in a stream"}
}
