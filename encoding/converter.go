package encoding

import (
	"reflect"
)

// ValueConverter converts PAKT values to/from a specific Go type.
// Implementations receive a scoped [ValueReader] positioned at the value,
// not the full [UnitReader].
type ValueConverter[T any] interface {
	// FromPakt reads a PAKT value and returns T.
	// The ValueReader is positioned at the start of the value.
	// The converter MUST consume exactly one complete value.
	FromPakt(vr *ValueReader, paktType Type) (T, error)

	// ToPakt writes a value of type T to the encoder.
	ToPakt(enc *Encoder, value T) error
}

// RegisterConverter registers a [ValueConverter] for type T.
// When deserializing into T, the converter is used instead of the
// default reflection-based mapping.
func RegisterConverter[T any](c ValueConverter[T]) Option {
	return func(o *options) {
		reg := o.ensureConverters()
		var zero T
		reg.byType[reflect.TypeOf(&zero).Elem()] = c
	}
}

// RegisterNamedConverter registers a converter by name for use with the
// `converter=name` struct tag option.
func RegisterNamedConverter(name string, c any) Option {
	return func(o *options) {
		reg := o.ensureConverters()
		reg.byName[name] = c
	}
}

// ValueReader is a scoped view of the stream, positioned at a single value.
// It provides read access for scalars and navigation for composites.
// A ValueReader is only valid for the duration of the converter call.
type ValueReader struct {
	sr    *UnitReader
	event Event // the initial event for this value
}

// StringValue returns the scalar string value.
func (vr *ValueReader) StringValue() (string, error) {
	if vr.event.Kind != EventScalarValue {
		return "", &DeserializeError{Pos: vr.event.Pos, Message: "not a scalar value"}
	}
	return vr.event.ValueString(), nil
}

// IntValue returns the scalar integer value.
func (vr *ValueReader) IntValue() (int64, error) {
	if vr.event.Kind != EventScalarValue {
		return 0, &DeserializeError{Pos: vr.event.Pos, Message: "not a scalar value"}
	}
	return parseIntLiteral(vr.event.ValueString())
}

// FloatValue returns the scalar float value.
func (vr *ValueReader) FloatValue() (float64, error) {
	if vr.event.Kind != EventScalarValue {
		return 0, &DeserializeError{Pos: vr.event.Pos, Message: "not a scalar value"}
	}
	return parseFloatLiteral(vr.event.ValueString())
}

// BoolValue returns the scalar boolean value.
func (vr *ValueReader) BoolValue() (bool, error) {
	if vr.event.Kind != EventScalarValue {
		return false, &DeserializeError{Pos: vr.event.Pos, Message: "not a scalar value"}
	}
	switch string(vr.event.Value) {
	case "true":
		return true, nil
	case "false":
		return false, nil
	default:
		return false, &DeserializeError{Pos: vr.event.Pos, Message: "invalid bool: " + vr.event.ValueString()}
	}
}

// DecValue returns the scalar decimal value as a string (preserving precision).
func (vr *ValueReader) DecValue() (string, error) {
	if vr.event.Kind != EventScalarValue {
		return "", &DeserializeError{Pos: vr.event.Pos, Message: "not a scalar value"}
	}
	return vr.event.ValueString(), nil
}

// BytesValue returns the scalar binary value as decoded bytes.
func (vr *ValueReader) BytesValue() ([]byte, error) {
	if vr.event.Kind != EventScalarValue {
		return nil, &DeserializeError{Pos: vr.event.Pos, Message: "not a scalar value"}
	}
	// The event value is hex-encoded for bin
	target := reflect.New(reflect.TypeOf([]byte{})).Elem()
	if err := setBinFromEvent(target, vr.event.ValueString()); err != nil {
		return nil, err
	}
	return target.Bytes(), nil
}

// IsNil returns true if the current value is nil.
func (vr *ValueReader) IsNil() bool {
	return vr.event.Kind == EventScalarValue && vr.event.IsNilValue()
}

// Skip consumes and discards the current value.
func (vr *ValueReader) Skip() error {
	return skipValueEvent(vr.sr, vr.event)
}

// Err returns the UnitReader's accumulated error.
func (vr *ValueReader) Err() error {
	return vr.sr.Err()
}

// ReadAs deserializes the current child value using the framework's
// type mapping, converters, and options. This is how converters compose:
// they delegate child values back to the framework.
func ReadAs[T any](vr *ValueReader) (T, error) {
	// Read the next event from the stream for the child value.
	ev, err := vr.sr.nextEvent()
	if err != nil {
		var zero T
		return zero, err
	}

	var val T
	target := reflect.ValueOf(&val).Elem()
	if ev.Kind == EventScalarValue && ev.IsNilValue() {
		if err := setNil(target); err != nil {
			return val, err
		}
		return val, nil
	}
	target = allocPtr(target)
	if err := handleValueEvent(vr.sr, ev, target); err != nil {
		return val, err
	}
	return val, nil
}
