package encoding

import (
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"io"
	"reflect"
	"strconv"
	"strings"
	"unsafe"
)

// ReadValue reads the current statement's value (or current pack element)
// and deserializes it into a new value of type T.
//
// For assign statements: reads the single value.
// For pack statements: reads the next element. Call within [PackItems] loop.
func ReadValue[T any](sr *UnitReader) (T, error) {
	var zero T
	target := reflect.New(reflect.TypeOf(&zero).Elem()).Elem()
	if err := readValueReflect(sr, target); err != nil {
		return zero, err
	}
	return target.Interface().(T), nil
}

// ReadValueInto reads the current value into an existing target.
// This enables buffer reuse in hot pack-processing loops.
func ReadValueInto[T any](sr *UnitReader, target *T) error {
	rv := reflect.ValueOf(target).Elem()
	return readValueReflect(sr, rv)
}

// readValueReflect is the core event-consuming value reader.
// It reads events from the UnitReader's decoder and populates target.
func readValueReflect(sr *UnitReader, target reflect.Value) error {
	ev, err := sr.nextEvent()
	if err != nil {
		return err
	}

	// Handle nil before pointer allocation.
	if ev.Kind == EventScalarValue && ev.IsNilValue() {
		return setNil(target)
	}

	// Check for registered converter before default path.
	if sr.opts != nil && sr.opts.converters != nil {
		baseType := target.Type()
		for baseType.Kind() == reflect.Pointer {
			baseType = baseType.Elem()
		}
		if conv, ok := sr.opts.converters.byType[baseType]; ok {
			vr := &ValueReader{sr: sr, event: ev}
			return invokeConverter(conv, vr, ev, target)
		}
	}

	// Allocate through pointer indirections.
	target = allocPtr(target)

	switch ev.Kind {
	case EventScalarValue:
		return setScalarFromEvent(ev, target)

	case EventStructStart:
		return readStructFromEvents(sr, ev, target)

	case EventTupleStart:
		return readTupleFromEvents(sr, ev, target)

	case EventListStart:
		return readListFromEvents(sr, ev, target)

	case EventMapStart:
		return readMapFromEvents(sr, ev, target)

	default:
		return &DeserializeError{
			Pos:     ev.Pos,
			Message: fmt.Sprintf("unexpected event %s while reading value", ev.Kind),
		}
	}
}

// invokeConverter calls a type-erased ValueConverter using reflection.
func invokeConverter(conv any, vr *ValueReader, ev Event, target reflect.Value) error {
	// The converter implements ValueConverter[T] which has FromPakt(*ValueReader, Type) (T, error).
	// We call it via reflection since the type is erased at registration time.
	convVal := reflect.ValueOf(conv)
	var paktType Type
	if ev.Type != nil {
		paktType = *ev.Type
	}
	results := convVal.MethodByName("FromPakt").Call([]reflect.Value{
		reflect.ValueOf(vr),
		reflect.ValueOf(paktType),
	})
	if !results[1].IsNil() {
		return results[1].Interface().(error)
	}
	// Set the result.
	result := results[0]
	target = allocPtr(target)
	target.Set(result)
	return nil
}

// setScalarFromEvent maps a ScalarValue event to a Go reflect.Value.
func setScalarFromEvent(ev Event, target reflect.Value) error {
	// Handle nil
	if ev.IsNilValue() {
		return setNil(target)
	}

	switch ev.ScalarType {
	case TypeStr, TypeAtom, TypeUUID:
		// String-like types: the target retains the value, so we must allocate.
		return setString(target, string(ev.Value))

	case TypeInt:
		// Zero-copy string view — parsed immediately, not retained.
		return setInt(target, unsafeString(ev.Value))

	case TypeFloat:
		return setFloat(target, unsafeString(ev.Value))

	case TypeDec:
		return setDec(target, unsafeString(ev.Value))

	case TypeBool:
		return setBool(target, unsafeString(ev.Value))

	case TypeDate, TypeTs:
		return setTemporalString(target, unsafeString(ev.Value), target.Kind())

	case TypeBin:
		return setBinFromEvent(target, unsafeString(ev.Value))

	case TypeNone:
		return setNil(target)

	default:
		return fmt.Errorf("unsupported scalar type: %s", ev.ScalarType)
	}
}

// unsafeString returns a zero-copy string view of a byte slice.
// The caller must not retain the string beyond the lifetime of the byte slice.
func unsafeString(b []byte) string {
	if len(b) == 0 {
		return ""
	}
	return unsafe.String(unsafe.SliceData(b), len(b))
}

// setFloat parses a PAKT float literal into a Go float target.
func setFloat(target reflect.Value, raw string) error {
	switch target.Kind() {
	case reflect.Float32, reflect.Float64:
		f, err := parseFloatLiteral(raw)
		if err != nil {
			return err
		}
		target.SetFloat(f)
		return nil
	case reflect.String:
		target.SetString(strings.Clone(raw))
		return nil
	default:
		return fmt.Errorf("cannot set float into %s", target.Type())
	}
}

// parseFloatLiteral parses a PAKT float literal, stripping underscores.
func parseFloatLiteral(raw string) (float64, error) {
	s := raw
	for i := 0; i < len(s); i++ {
		if s[i] == '_' {
			s = removeUnderscores(s)
			break
		}
	}
	f, err := parseFloat64(s)
	if err != nil {
		return 0, fmt.Errorf("invalid float literal %q: %w", raw, err)
	}
	return f, nil
}

func removeUnderscores(s string) string {
	buf := make([]byte, 0, len(s))
	for i := 0; i < len(s); i++ {
		if s[i] != '_' {
			buf = append(buf, s[i])
		}
	}
	return string(buf)
}

func parseFloat64(s string) (float64, error) {
	return strconv.ParseFloat(s, 64)
}

// setBool sets a boolean value from a string.
func setBool(target reflect.Value, raw string) error {
	switch target.Kind() {
	case reflect.Bool:
		switch raw {
		case "true":
			target.SetBool(true)
		case "false":
			target.SetBool(false)
		default:
			return fmt.Errorf("invalid bool value: %q", raw)
		}
		return nil
	case reflect.String:
		target.SetString(strings.Clone(raw))
		return nil
	default:
		return fmt.Errorf("cannot set bool into %s", target.Type())
	}
}

// setBinFromEvent handles bin values from the event stream.
// The event Value contains the raw decoded content (hex or base64 prefix stripped).
func setBinFromEvent(target reflect.Value, raw string) error {
	// The decoder already strips the x'' or b'' wrapper but the value
	// may still be hex-encoded or base64-encoded based on format.
	// Try hex first (the event stream delivers the inner content).
	data, err := hex.DecodeString(raw)
	if err != nil {
		// Try base64
		data, err = base64.StdEncoding.DecodeString(raw)
		if err != nil {
			// Treat as raw bytes
			data = []byte(raw)
		}
	}

	switch target.Kind() {
	case reflect.Slice:
		if target.Type().Elem().Kind() == reflect.Uint8 {
			target.SetBytes(data)
			return nil
		}
	case reflect.String:
		target.SetString(string(data))
		return nil
	}
	return fmt.Errorf("cannot set bin into %s", target.Type())
}

// readStructFromEvents reads struct events into a Go struct or map.
func readStructFromEvents(sr *UnitReader, startEv Event, target reflect.Value) error {
	if target.Kind() == reflect.Map {
		return readStructIntoMapFromEvents(sr, target)
	}

	if target.Kind() != reflect.Struct {
		return fmt.Errorf("cannot unmarshal struct into %s", target.Type())
	}

	info, err := cachedStructFields(target.Type())
	if err != nil {
		return err
	}

	for {
		ev, err := sr.nextEvent()
		if err != nil {
			if err == io.EOF {
				return &DeserializeError{Pos: startEv.Pos, Message: "unterminated struct"}
			}
			return err
		}

		if ev.Kind == EventStructEnd {
			return nil
		}

		// ev should be a value event for the next positional field.
		// The field name comes from ev.Name (set by the state machine).
		fieldName := ev.Name
		fi, ok := info.fieldMap[fieldName]
		if ok {
			fieldTarget := target.Field(fi.Index)
			fieldTarget = allocPtr(fieldTarget)
			if err := handleValueEvent(sr, ev, fieldTarget); err != nil {
				return fmt.Errorf("field %q: %w", fieldName, err)
			}
		} else {
			// Unknown field — skip its value
			if err := skipValueEvent(sr, ev); err != nil {
				return err
			}
		}
	}
}

// readStructIntoMapFromEvents reads struct events into a Go map[string]T.
func readStructIntoMapFromEvents(sr *UnitReader, target reflect.Value) error {
	if target.IsNil() {
		target.Set(reflect.MakeMap(target.Type()))
	}
	valType := target.Type().Elem()

	for {
		ev, err := sr.nextEvent()
		if err != nil {
			return err
		}
		if ev.Kind == EventStructEnd {
			return nil
		}

		val := reflect.New(valType).Elem()
		if err := handleValueEvent(sr, ev, val); err != nil {
			return fmt.Errorf("map key %q: %w", ev.Name, err)
		}
		target.SetMapIndex(reflect.ValueOf(ev.Name), val)
	}
}

// readTupleFromEvents reads tuple events into a Go slice.
func readTupleFromEvents(sr *UnitReader, startEv Event, target reflect.Value) error {
	if target.Kind() != reflect.Slice {
		return fmt.Errorf("cannot unmarshal tuple into %s", target.Type())
	}

	elemType := target.Type().Elem()
	target.Set(reflect.MakeSlice(target.Type(), 0, 4))

	for {
		ev, err := sr.nextEvent()
		if err != nil {
			if err == io.EOF {
				return &DeserializeError{Pos: startEv.Pos, Message: "unterminated tuple"}
			}
			return err
		}
		if ev.Kind == EventTupleEnd {
			return nil
		}

		target.Grow(1)
		target.SetLen(target.Len() + 1)
		elem := target.Index(target.Len() - 1)
		if elemType.Kind() == reflect.Ptr || elemType.Kind() == reflect.Map || elemType.Kind() == reflect.Slice {
			elem.Set(reflect.New(elemType).Elem())
		}
		if err := handleValueEvent(sr, ev, elem); err != nil {
			return err
		}
	}
}

// readListFromEvents reads list events into a Go slice.
func readListFromEvents(sr *UnitReader, startEv Event, target reflect.Value) error {
	if target.Kind() != reflect.Slice {
		return fmt.Errorf("cannot unmarshal list into %s", target.Type())
	}

	elemType := target.Type().Elem()
	target.Set(reflect.MakeSlice(target.Type(), 0, 8))

	for {
		ev, err := sr.nextEvent()
		if err != nil {
			if err == io.EOF {
				return &DeserializeError{Pos: startEv.Pos, Message: "unterminated list"}
			}
			return err
		}
		if ev.Kind == EventListEnd {
			return nil
		}

		target.Grow(1)
		target.SetLen(target.Len() + 1)
		elem := target.Index(target.Len() - 1)
		if elemType.Kind() == reflect.Ptr || elemType.Kind() == reflect.Map || elemType.Kind() == reflect.Slice {
			elem.Set(reflect.New(elemType).Elem())
		}
		if err := handleValueEvent(sr, ev, elem); err != nil {
			return err
		}
	}
}

// readMapFromEvents reads map events into a Go map.
func readMapFromEvents(sr *UnitReader, startEv Event, target reflect.Value) error {
	if target.Kind() != reflect.Map {
		return fmt.Errorf("cannot unmarshal map into %s", target.Type())
	}

	if target.IsNil() {
		target.Set(reflect.MakeMap(target.Type()))
	}

	keyType := target.Type().Key()
	valType := target.Type().Elem()

	// Map events alternate: key (ScalarValue) → value → key → value → MapEnd
	for {
		// Read key
		keyEv, err := sr.nextEvent()
		if err != nil {
			if err == io.EOF {
				return &DeserializeError{Pos: startEv.Pos, Message: "unterminated map"}
			}
			return err
		}
		if keyEv.Kind == EventMapEnd {
			return nil
		}

		key := reflect.New(keyType).Elem()
		if err := handleValueEvent(sr, keyEv, key); err != nil {
			return fmt.Errorf("map key: %w", err)
		}

		// Read value
		valEv, err := sr.nextEvent()
		if err != nil {
			return fmt.Errorf("map value: %w", err)
		}

		val := reflect.New(valType).Elem()
		if err := handleValueEvent(sr, valEv, val); err != nil {
			return fmt.Errorf("map value: %w", err)
		}

		target.SetMapIndex(key, val)
	}
}

// handleValueEvent processes a single value event (which may be a scalar
// or the start of a composite), writing the result into target.
func handleValueEvent(sr *UnitReader, ev Event, target reflect.Value) error {
	target = allocPtr(target)

	switch ev.Kind {
	case EventScalarValue:
		return setScalarFromEvent(ev, target)
	case EventStructStart:
		return readStructFromEvents(sr, ev, target)
	case EventTupleStart:
		return readTupleFromEvents(sr, ev, target)
	case EventListStart:
		return readListFromEvents(sr, ev, target)
	case EventMapStart:
		return readMapFromEvents(sr, ev, target)
	default:
		return &DeserializeError{
			Pos:     ev.Pos,
			Message: fmt.Sprintf("unexpected event %s in value position", ev.Kind),
		}
	}
}

// skipValueEvent skips a value event and any nested events it contains.
func skipValueEvent(sr *UnitReader, ev Event) error {
	switch {
	case ev.Kind == EventScalarValue:
		return nil // scalar — nothing more to consume
	case ev.Kind.IsCompositeStart():
		return skipComposite(sr, ev.Kind)
	default:
		return nil
	}
}

// skipComposite reads and discards events until the matching end event.
func skipComposite(sr *UnitReader, startKind EventKind) error {
	depth := 1
	for depth > 0 {
		ev, err := sr.nextEvent()
		if err != nil {
			return err
		}
		if ev.Kind.IsCompositeStart() {
			depth++
		} else if ev.Kind.IsCompositeEnd() {
			depth--
		}
	}
	return nil
}
