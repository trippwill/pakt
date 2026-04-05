package encoding

import (
	"bytes"
	"encoding/hex"
	"fmt"
	"io"
	"math"
	"reflect"
	"strconv"
	"strings"
	"time"
)

// Unmarshal parses PAKT data and stores the result in the value pointed to by v.
// v must be a pointer to a struct. Each top-level PAKT statement is matched
// to struct fields by name (using pakt struct tags or lowercase field names).
func Unmarshal(data []byte, v any) error {
	if v == nil {
		return fmt.Errorf("pakt: Unmarshal requires a non-nil pointer")
	}
	rv := reflect.ValueOf(v)
	if rv.Kind() != reflect.Pointer {
		return fmt.Errorf("pakt: Unmarshal requires a pointer, got %s", rv.Type())
	}
	if rv.IsNil() {
		return fmt.Errorf("pakt: Unmarshal requires a non-nil pointer")
	}
	rv = rv.Elem()
	if rv.Kind() != reflect.Struct {
		return fmt.Errorf("pakt: Unmarshal requires a pointer to a struct, got pointer to %s", rv.Type())
	}

	info, err := cachedStructFields(rv.Type())
	if err != nil {
		return err
	}

	dec := NewDecoder(bytes.NewReader(data))
	defer dec.Close()

	for {
		ev, err := dec.Decode()
		if err != nil {
			if err == io.EOF {
				return nil
			}
			return err
		}
		if ev.Kind == EventError {
			return ev.Err
		}

		if ev.Kind != EventAssignStart && !ev.Kind.IsStreamStart() {
			continue
		}

		fi, ok := info.fieldMap[ev.Name]
		if !ok {
			// Unknown field — skip by consuming until matching end.
			if err := skipStatement(dec, ev); err != nil {
				return err
			}
			continue
		}

		target := rv.Field(fi.Index)
		switch ev.Kind {
		case EventAssignStart:
			if err := streamConsumeAssign(dec, ev.Name, target); err != nil {
				return fmt.Errorf("pakt: field %q: %w", ev.Name, err)
			}
		case EventListStreamStart:
			if err := streamConsumeListStream(dec, target); err != nil {
				return fmt.Errorf("pakt: field %q: %w", ev.Name, err)
			}
		case EventMapStreamStart:
			if err := streamConsumeMapStream(dec, target); err != nil {
				return fmt.Errorf("pakt: field %q: %w", ev.Name, err)
			}
		}
	}
}

// skipStatement consumes events until the matching end event for a top-level statement.
func skipStatement(dec *Decoder, start Event) error {
	var endKind EventKind
	switch start.Kind {
	case EventAssignStart:
		endKind = EventAssignEnd
	case EventListStreamStart:
		endKind = EventListStreamEnd
	case EventMapStreamStart:
		endKind = EventMapStreamEnd
	default:
		return nil
	}
	depth := 1
	for depth > 0 {
		ev, err := dec.Decode()
		if err != nil {
			return err
		}
		if ev.Kind == endKind && ev.Name == start.Name {
			depth--
		} else if ev.Kind == start.Kind && ev.Name == start.Name {
			depth++
		}
	}
	return nil
}

// streamConsumeAssign reads the value(s) between AssignStart and AssignEnd.
func streamConsumeAssign(dec *Decoder, name string, target reflect.Value) error {
	ev, err := dec.Decode()
	if err != nil {
		return err
	}
	if ev.Kind == EventAssignEnd {
		return nil // empty assignment
	}
	if err := streamConsumeValue(dec, ev, target); err != nil {
		return err
	}
	// Consume the AssignEnd
	ev, err = dec.Decode()
	if err != nil {
		return err
	}
	if ev.Kind != EventAssignEnd {
		return fmt.Errorf("expected AssignEnd, got %s", ev.Kind)
	}
	return nil
}

// streamConsumeValue handles a single value event (scalar or composite start).
func streamConsumeValue(dec *Decoder, ev Event, target reflect.Value) error {
	switch ev.Kind {
	case EventScalarValue:
		return setScalar(target, ev.ScalarType, ev.Value)
	case EventStructStart:
		return streamConsumeStruct(dec, target)
	case EventTupleStart:
		return streamConsumeTuple(dec, target)
	case EventListStart:
		return streamConsumeList(dec, target)
	case EventMapStart:
		return streamConsumeMap(dec, target)
	default:
		return nil
	}
}

// streamConsumeStruct reads events between StructStart (already consumed) and StructEnd.
func streamConsumeStruct(dec *Decoder, target reflect.Value) error {
	target = allocPtr(target)

	if target.Kind() == reflect.Map {
		return streamConsumeStructIntoMap(dec, target)
	}

	if target.Kind() != reflect.Struct {
		return fmt.Errorf("cannot unmarshal struct into %s", target.Type())
	}

	info, err := cachedStructFields(target.Type())
	if err != nil {
		return err
	}

	for {
		ev, err := dec.Decode()
		if err != nil {
			return err
		}
		if ev.Kind == EventStructEnd {
			return nil
		}

		fi, ok := info.fieldMap[ev.Name]
		if !ok {
			// Skip unknown field value by consuming the event.
			if err := streamSkipValue(dec, ev); err != nil {
				return err
			}
			continue
		}

		field := target.Field(fi.Index)
		if err := streamConsumeValue(dec, ev, field); err != nil {
			return fmt.Errorf("field %q: %w", ev.Name, err)
		}
	}
}

// streamConsumeStructIntoMap reads struct events into a map[string]T.
func streamConsumeStructIntoMap(dec *Decoder, target reflect.Value) error {
	if target.IsNil() {
		target.Set(reflect.MakeMap(target.Type()))
	}
	valType := target.Type().Elem()

	for {
		ev, err := dec.Decode()
		if err != nil {
			return err
		}
		if ev.Kind == EventStructEnd {
			return nil
		}

		val := reflect.New(valType).Elem()
		if err := streamConsumeValue(dec, ev, val); err != nil {
			return fmt.Errorf("map key %q: %w", ev.Name, err)
		}
		target.SetMapIndex(reflect.ValueOf(ev.Name), val)
	}
}

// streamConsumeList reads events between ListStart (already consumed) and ListEnd.
func streamConsumeList(dec *Decoder, target reflect.Value) error {
	if target.Kind() != reflect.Slice {
		return fmt.Errorf("cannot unmarshal list into %s", target.Type())
	}

	elemType := target.Type().Elem()
	// Set target to an empty slice with initial capacity.
	target.Set(reflect.MakeSlice(target.Type(), 0, 8))

	for {
		ev, err := dec.Decode()
		if err != nil {
			return err
		}
		if ev.Kind == EventListEnd {
			return nil
		}

		// Grow by 1 and write directly into the new slot.
		target.Grow(1)
		target.SetLen(target.Len() + 1)
		elem := target.Index(target.Len() - 1)
		if elemType.Kind() == reflect.Ptr || elemType.Kind() == reflect.Map || elemType.Kind() == reflect.Slice {
			elem.Set(reflect.New(elemType).Elem())
		}
		if err := streamConsumeValue(dec, ev, elem); err != nil {
			return err
		}
	}
}

// streamConsumeTuple reads events between TupleStart (already consumed) and TupleEnd.
func streamConsumeTuple(dec *Decoder, target reflect.Value) error {
	if target.Kind() != reflect.Slice {
		return fmt.Errorf("cannot unmarshal tuple into %s", target.Type())
	}

	elemType := target.Type().Elem()
	target.Set(reflect.MakeSlice(target.Type(), 0, 8))

	for {
		ev, err := dec.Decode()
		if err != nil {
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
		if err := streamConsumeValue(dec, ev, elem); err != nil {
			return err
		}
	}
}

// streamConsumeMap reads events between MapStart (already consumed) and MapEnd.
func streamConsumeMap(dec *Decoder, target reflect.Value) error {
	if target.Kind() != reflect.Map {
		return fmt.Errorf("cannot unmarshal map into %s", target.Type())
	}

	if target.IsNil() {
		target.Set(reflect.MakeMap(target.Type()))
	}

	keyType := target.Type().Key()
	valType := target.Type().Elem()

	for {
		ev, err := dec.Decode()
		if err != nil {
			return err
		}
		if ev.Kind == EventMapEnd {
			return nil
		}

		// Key
		key := reflect.New(keyType).Elem()
		if err := streamConsumeValue(dec, ev, key); err != nil {
			return fmt.Errorf("map key: %w", err)
		}

		// Value
		ev, err = dec.Decode()
		if err != nil {
			return fmt.Errorf("map value: unexpected end")
		}
		val := reflect.New(valType).Elem()
		if err := streamConsumeValue(dec, ev, val); err != nil {
			return fmt.Errorf("map value: %w", err)
		}

		target.SetMapIndex(key, val)
	}
}

// streamConsumeListStream reads events for a top-level list stream (<<).
func streamConsumeListStream(dec *Decoder, target reflect.Value) error {
	target = allocPtr(target)

	if target.Kind() != reflect.Slice {
		return fmt.Errorf("cannot unmarshal list stream into %s", target.Type())
	}

	elemType := target.Type().Elem()
	target.Set(reflect.MakeSlice(target.Type(), 0, 64))

	for {
		ev, err := dec.Decode()
		if err != nil {
			return err
		}
		if ev.Kind == EventListStreamEnd {
			return nil
		}

		target.Grow(1)
		target.SetLen(target.Len() + 1)
		elem := target.Index(target.Len() - 1)
		if elemType.Kind() == reflect.Ptr || elemType.Kind() == reflect.Map || elemType.Kind() == reflect.Slice {
			elem.Set(reflect.New(elemType).Elem())
		}
		if err := streamConsumeValue(dec, ev, elem); err != nil {
			return err
		}
	}
}

// streamConsumeMapStream reads events for a top-level map stream (<<).
func streamConsumeMapStream(dec *Decoder, target reflect.Value) error {
	target = allocPtr(target)

	if target.Kind() != reflect.Map {
		return fmt.Errorf("cannot unmarshal map stream into %s", target.Type())
	}

	if target.IsNil() {
		target.Set(reflect.MakeMap(target.Type()))
	}

	keyType := target.Type().Key()
	valType := target.Type().Elem()

	for {
		ev, err := dec.Decode()
		if err != nil {
			return err
		}
		if ev.Kind == EventMapStreamEnd {
			return nil
		}

		key := reflect.New(keyType).Elem()
		if err := streamConsumeValue(dec, ev, key); err != nil {
			return fmt.Errorf("stream map key: %w", err)
		}

		ev, err = dec.Decode()
		if err != nil {
			return fmt.Errorf("stream map value: unexpected end")
		}
		if ev.Kind == EventMapStreamEnd {
			return fmt.Errorf("stream map value: unexpected end")
		}

		val := reflect.New(valType).Elem()
		if err := streamConsumeValue(dec, ev, val); err != nil {
			return fmt.Errorf("stream map value: %w", err)
		}

		target.SetMapIndex(key, val)
	}
}

// streamSkipValue skips over one complete value in the event stream.
func streamSkipValue(dec *Decoder, ev Event) error {
	if ev.Kind == EventScalarValue {
		return nil // already consumed
	}
	if !ev.Kind.IsCompositeStart() {
		return nil
	}
	depth := 1
	for depth > 0 {
		next, err := dec.Decode()
		if err != nil {
			return err
		}
		if next.Kind.IsCompositeStart() {
			depth++
		} else if next.Kind.IsCompositeEnd() {
			depth--
		}
	}
	return nil
}

// setScalar sets a reflect.Value from a PAKT scalar event.
func setScalar(target reflect.Value, scalarType TypeKind, rawValue string) error {
	// Handle nil.
	if rawValue == "nil" {
		return setNil(target)
	}

	// Allocate pointer if needed.
	target = allocPtr(target)

	kind := target.Kind()

	switch scalarType {
	case TypeStr:
		return setString(target, rawValue)

	case TypeInt:
		return setInt(target, rawValue)

	case TypeDec:
		return setDec(target, rawValue)

	case TypeFloat:
		return setFloat(target, rawValue)

	case TypeBool:
		return setBool(target, rawValue)

	case TypeUUID:
		return setString(target, rawValue)

	case TypeDate:
		return setDateTimeString(target, rawValue, kind)

	case TypeTime:
		return setDateTimeString(target, rawValue, kind)

	case TypeDateTime:
		return setDateTimeString(target, rawValue, kind)

	case TypeBin:
		return setBin(target, rawValue)

	case TypeAtom:
		return setString(target, rawValue)

	default:
		return fmt.Errorf("unsupported PAKT type %s", scalarType)
	}
}

// setNil sets a value to its zero value, or nil for pointers/maps/slices.
func setNil(target reflect.Value) error {
	if target.Kind() == reflect.Pointer || target.Kind() == reflect.Map ||
		target.Kind() == reflect.Slice || target.Kind() == reflect.Interface {
		target.Set(reflect.Zero(target.Type()))
		return nil
	}
	target.Set(reflect.Zero(target.Type()))
	return nil
}

// allocPtr allocates through pointer indirections.
func allocPtr(v reflect.Value) reflect.Value {
	for v.Kind() == reflect.Pointer {
		if v.IsNil() {
			v.Set(reflect.New(v.Type().Elem()))
		}
		v = v.Elem()
	}
	return v
}

func setString(target reflect.Value, val string) error {
	if target.Kind() == reflect.String {
		target.SetString(val)
		return nil
	}
	if target.Kind() == reflect.Slice && target.Type().Elem().Kind() == reflect.Uint8 {
		target.SetBytes([]byte(val))
		return nil
	}
	return fmt.Errorf("cannot set string into %s", target.Type())
}

func setBin(target reflect.Value, raw string) error {
	data, err := hex.DecodeString(raw)
	if err != nil {
		return fmt.Errorf("invalid bin value %q: %w", raw, err)
	}
	if target.Kind() == reflect.Slice && target.Type().Elem().Kind() == reflect.Uint8 {
		target.SetBytes(data)
		return nil
	}
	if target.Kind() == reflect.String {
		target.SetString(string(data))
		return nil
	}
	return fmt.Errorf("cannot set bin into %s", target.Type())
}

func setInt(target reflect.Value, raw string) error {
	n, err := parseIntLiteral(raw)
	if err != nil {
		return err
	}

	switch target.Kind() {
	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		if target.OverflowInt(n) {
			return fmt.Errorf("value %d overflows %s", n, target.Type())
		}
		target.SetInt(n)
		return nil
	case reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
		if n < 0 {
			return fmt.Errorf("cannot set negative value %d into %s", n, target.Type())
		}
		u := uint64(n)
		if target.OverflowUint(u) {
			return fmt.Errorf("value %d overflows %s", n, target.Type())
		}
		target.SetUint(u)
		return nil
	case reflect.Float32, reflect.Float64:
		target.SetFloat(float64(n))
		return nil
	case reflect.String:
		target.SetString(raw)
		return nil
	default:
		return fmt.Errorf("cannot set int into %s", target.Type())
	}
}

// parseIntLiteral parses a PAKT integer literal, handling hex, binary, octal, and underscores.
func parseIntLiteral(raw string) (int64, error) {
	s := raw
	if strings.IndexByte(s, '_') >= 0 {
		s = strings.ReplaceAll(s, "_", "")
	}
	if s == "" {
		return 0, fmt.Errorf("empty int literal")
	}

	neg := false
	switch s[0] {
	case '-':
		neg = true
		s = s[1:]
	case '+':
		s = s[1:]
	}

	var val uint64
	var err error

	if strings.HasPrefix(s, "0x") || strings.HasPrefix(s, "0X") {
		val, err = strconv.ParseUint(s[2:], 16, 64)
	} else if strings.HasPrefix(s, "0b") || strings.HasPrefix(s, "0B") {
		val, err = strconv.ParseUint(s[2:], 2, 64)
	} else if strings.HasPrefix(s, "0o") || strings.HasPrefix(s, "0O") {
		val, err = strconv.ParseUint(s[2:], 8, 64)
	} else {
		val, err = strconv.ParseUint(s, 10, 64)
	}
	if err != nil {
		return 0, fmt.Errorf("invalid int literal %q: %w", raw, err)
	}

	if neg {
		if val > math.MaxInt64+1 {
			return 0, fmt.Errorf("int literal %q overflows int64", raw)
		}
		return -int64(val), nil
	}
	if val > math.MaxInt64 {
		return 0, fmt.Errorf("int literal %q overflows int64", raw)
	}
	return int64(val), nil
}

func setDec(target reflect.Value, raw string) error {
	switch target.Kind() {
	case reflect.String:
		target.SetString(raw)
		return nil
	case reflect.Float32, reflect.Float64:
		s := raw
		if strings.IndexByte(s, '_') >= 0 {
			s = strings.ReplaceAll(s, "_", "")
		}
		f, err := strconv.ParseFloat(s, 64)
		if err != nil {
			return fmt.Errorf("invalid dec literal %q: %w", raw, err)
		}
		target.SetFloat(f)
		return nil
	default:
		return fmt.Errorf("cannot set dec into %s", target.Type())
	}
}

func setFloat(target reflect.Value, raw string) error {
	s := raw
	if strings.IndexByte(s, '_') >= 0 {
		s = strings.ReplaceAll(s, "_", "")
	}
	f, err := strconv.ParseFloat(s, 64)
	if err != nil {
		return fmt.Errorf("invalid float literal %q: %w", raw, err)
	}

	switch target.Kind() {
	case reflect.Float32, reflect.Float64:
		target.SetFloat(f)
		return nil
	case reflect.String:
		target.SetString(raw)
		return nil
	default:
		return fmt.Errorf("cannot set float into %s", target.Type())
	}
}

func setBool(target reflect.Value, raw string) error {
	b := raw == "true"
	if raw != "true" && raw != "false" {
		return fmt.Errorf("invalid bool literal %q", raw)
	}

	switch target.Kind() {
	case reflect.Bool:
		target.SetBool(b)
		return nil
	default:
		return fmt.Errorf("cannot set bool into %s", target.Type())
	}
}

func setDateTimeString(target reflect.Value, raw string, kind reflect.Kind) error {
	// If target is time.Time, parse.
	if target.Type() == timeType {
		t, err := parseTime(raw)
		if err != nil {
			return err
		}
		target.Set(reflect.ValueOf(t))
		return nil
	}

	if kind == reflect.String {
		target.SetString(raw)
		return nil
	}

	return fmt.Errorf("cannot set date/time into %s", target.Type())
}

// parseTime parses ISO 8601 date, time, or datetime strings.
func parseTime(s string) (time.Time, error) {
	// Try datetime formats first (has 'T' separator).
	formats := []string{
		time.RFC3339Nano,
		time.RFC3339,
		"2006-01-02T15:04:05Z07:00",
		"2006-01-02T15:04:05.999999999Z07:00",
	}
	for _, f := range formats {
		if t, err := time.Parse(f, s); err == nil {
			return t, nil
		}
	}

	// Date only.
	if t, err := time.Parse("2006-01-02", s); err == nil {
		return t, nil
	}

	// Time only (with timezone): try "15:04:05Z07:00" and fractional variants.
	timeFormats := []string{
		"15:04:05Z07:00",
		"15:04:05.999999999Z07:00",
		"15:04:05Z",
	}
	for _, f := range timeFormats {
		if t, err := time.Parse(f, s); err == nil {
			return t, nil
		}
	}

	return time.Time{}, fmt.Errorf("cannot parse time %q", s)
}
