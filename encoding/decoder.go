package encoding

import (
	"io"
	"reflect"
)

// Decoder reads a PAKT document from an input source and emits [Event] values
// one at a time, similar to [encoding/json.Decoder]. An optional spec
// projection may be applied via [Decoder.SetSpec] to filter and validate the
// source against a .spec.pakt definition.
type Decoder struct {
	r    *reader
	sm   *stateMachine
	spec *Spec
	done bool // true after document fully parsed

	// pack unmarshal state
	inPack   bool // true while inside a pack statement
	packList *ListType
	packMap  *MapType
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

// Decode reads the next event from the PAKT source.
//
// On each call it returns the next [Event] in document order. When the
// document is fully consumed, it returns a zero Event and [io.EOF].
// If a spec is active, unmatched fields are silently skipped.
func (d *Decoder) Decode() (Event, error) {
	if d.spec != nil {
		return d.decodeWithSpec()
	}
	return d.decodeDirect()
}

func (d *Decoder) decodeDirect() (Event, error) {
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

// UnmarshalNext reads the next top-level statement from the PAKT source and
// stores the result in the value pointed to by v. It uses a visitor-driven
// path that bypasses Event creation, writing parsed values directly into
// struct fields.
//
// For assignment statements (name:type = value), v must be a pointer to a
// struct with a matching field. For pack statements (name:type <<), the
// first call reads the pack header; subsequent calls read one element at
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

	// If we're mid-pack, read the next pack element.
	if d.inPack {
		return d.unmarshalNextPackElement(rv)
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

	if h.pack {
		// Enter pack mode.
		d.inPack = true
		if h.typ.List != nil {
			d.packList = h.typ.List
		} else {
			d.packMap = h.typ.Map
		}
		// For a struct target, try to set the pack into a matching field.
		if rv.Kind() == reflect.Struct {
			return d.unmarshalPackIntoField(h, rv)
		}
		// For a slice/map target, unmarshal the entire pack.
		return d.unmarshalWholePack(h, rv)
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

// More reports whether there are more elements to read. When inside a pack
// statement, it reports whether additional pack elements remain. When at
// the top level, it reports whether more statements exist.
func (d *Decoder) More() bool {
	if d.done {
		return false
	}
	if d.inPack {
		d.r.skipInsignificant(true)
		b, err := d.r.peekByte()
		if err != nil {
			d.inPack = false
			d.packList = nil
			d.packMap = nil
			return false
		}
		// NUL byte terminates the pack (end-of-unit per spec §10.1).
		if b == 0 || !d.r.canStartValueInPack(b) {
			d.inPack = false
			d.packList = nil
			d.packMap = nil
			return false
		}
		return true
	}
	d.r.skipInsignificant(true)
	b, err := d.r.peekByte()
	if err != nil {
		return false
	}
	// NUL byte at top level is end-of-unit (spec §10.1).
	return b != 0
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

func (d *Decoder) unmarshalPackIntoField(h statementHeader, rv reflect.Value) error {
	info, err := cachedStructFields(rv.Type())
	if err != nil {
		return err
	}
	fi, ok := info.fieldMap[h.name]
	if !ok {
		// Skip entire pack.
		d.inPack = false
		d.packList = nil
		d.packMap = nil
		return d.r.skipPackBody(h.typ)
	}
	target := rv.Field(fi.Index)
	if d.packList != nil {
		err = d.sm.unmarshalPackList(d.packList, target)
	} else {
		err = d.sm.unmarshalPackMap(d.packMap, target)
	}
	d.inPack = false
	d.packList = nil
	d.packMap = nil
	return err
}

func (d *Decoder) unmarshalWholePack(h statementHeader, rv reflect.Value) error {
	var err error
	if d.packList != nil {
		err = d.sm.unmarshalPackList(d.packList, rv)
	} else {
		err = d.sm.unmarshalPackMap(d.packMap, rv)
	}
	d.inPack = false
	d.packList = nil
	d.packMap = nil
	return err
}

func (d *Decoder) unmarshalNextPackElement(rv reflect.Value) error {
	if d.packList != nil {
		return d.sm.unmarshalValue(d.packList.Element, rv)
	}
	if d.packMap != nil {
		// For map packs, caller gets key-value pairs.
		return d.sm.unmarshalValue(d.packMap.Value, rv)
	}
	return &ParseError{Message: "pakt: not in a pack"}
}
