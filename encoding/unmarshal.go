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
	if rv.Kind() != reflect.Ptr {
		return fmt.Errorf("pakt: Unmarshal requires a pointer, got %s", rv.Type())
	}
	if rv.IsNil() {
		return fmt.Errorf("pakt: Unmarshal requires a non-nil pointer")
	}
	rv = rv.Elem()
	if rv.Kind() != reflect.Struct {
		return fmt.Errorf("pakt: Unmarshal requires a pointer to a struct, got pointer to %s", rv.Type())
	}

	fields, err := StructFields(rv.Type())
	if err != nil {
		return err
	}

	fieldMap := make(map[string]FieldInfo, len(fields))
	for _, fi := range fields {
		fieldMap[fi.Name] = fi
	}

	// Collect all events from the decoder.
	events, err := unmarshalDecodeAll(data)
	if err != nil {
		return err
	}

	// Walk events: find statement start events, collect until the matching end,
	// and set the destination field.
	i := 0
	for i < len(events) {
		ev := events[i]
		if ev.Kind != EventAssignStart && !ev.Kind.IsStreamStart() {
			i++
			continue
		}

		assignName := ev.Name

		endIdx := findRootEnd(events, i, assignName)
		if endIdx < 0 {
			switch {
			case ev.Kind == EventAssignStart:
				return fmt.Errorf("pakt: missing AssignEnd for %q", assignName)
			case ev.Kind.IsStreamStart():
				return fmt.Errorf("pakt: missing StreamEnd for %q", assignName)
			}
		}

		fi, ok := fieldMap[assignName]
		if !ok {
			// Unknown field — skip silently.
			i = endIdx + 1
			continue
		}

		target := rv.Field(fi.Index)
		switch ev.Kind {
		case EventAssignStart:
			// The value events are between AssignStart and AssignEnd (exclusive).
			valueEvents := events[i+1 : endIdx]
			if len(valueEvents) == 0 {
				i = endIdx + 1
				continue
			}
			if _, err := consumeValue(valueEvents, 0, target); err != nil {
				return fmt.Errorf("pakt: field %q: %w", assignName, err)
			}
		case EventListStreamStart:
			if _, err := consumeStreamList(events, i, target); err != nil {
				return fmt.Errorf("pakt: field %q: %w", assignName, err)
			}
		case EventMapStreamStart:
			if _, err := consumeStreamMap(events, i, target); err != nil {
				return fmt.Errorf("pakt: field %q: %w", assignName, err)
			}
		}

		i = endIdx + 1
	}

	return nil
}

// unmarshalDecodeAll reads all events from a PAKT byte slice.
func unmarshalDecodeAll(data []byte) ([]Event, error) {
	dec := NewDecoder(bytes.NewReader(data))
	var events []Event
	for {
		ev, err := dec.Decode()
		if err != nil {
			if err == io.EOF {
				break
			}
			return nil, err
		}
		if ev.Kind == EventError {
			return nil, ev.Err
		}
		events = append(events, ev)
	}
	return events, nil
}

// findRootEnd finds the index of the root-end event matching the given name.
func findRootEnd(events []Event, start int, name string) int {
	var endKind EventKind
	switch events[start].Kind {
	case EventAssignStart:
		endKind = EventAssignEnd
	case EventListStreamStart:
		endKind = EventListStreamEnd
	case EventMapStreamStart:
		endKind = EventMapStreamEnd
	default:
		return -1
	}
	for i := start + 1; i < len(events); i++ {
		if events[i].Kind == endKind && events[i].Name == name {
			return i
		}
	}
	return -1
}

// consumeValue reads a single value from events starting at index i,
// sets the target reflect.Value, and returns the next index.
func consumeValue(events []Event, i int, target reflect.Value) (int, error) {
	if i >= len(events) {
		return i, fmt.Errorf("unexpected end of events")
	}

	ev := events[i]

	switch ev.Kind {
	case EventScalarValue:
		return i + 1, setScalar(target, ev.ScalarType, ev.Value)
	case EventStructStart:
		return consumeStruct(events, i, target)
	case EventTupleStart:
		return consumeTuple(events, i, target)
	case EventListStart:
		return consumeList(events, i, target)
	case EventMapStart:
		return consumeMap(events, i, target)
	default:
		return i + 1, nil
	}
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
	if target.Kind() == reflect.Ptr || target.Kind() == reflect.Map ||
		target.Kind() == reflect.Slice || target.Kind() == reflect.Interface {
		target.Set(reflect.Zero(target.Type()))
		return nil
	}
	target.Set(reflect.Zero(target.Type()))
	return nil
}

// allocPtr allocates through pointer indirections.
func allocPtr(v reflect.Value) reflect.Value {
	for v.Kind() == reflect.Ptr {
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
	s := strings.ReplaceAll(raw, "_", "")
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
		s := strings.ReplaceAll(raw, "_", "")
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
	s := strings.ReplaceAll(raw, "_", "")
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

func consumeStruct(events []Event, i int, target reflect.Value) (int, error) {
	i++ // skip StructStart

	target = allocPtr(target)

	if target.Kind() == reflect.Map {
		return consumeStructIntoMap(events, i, target)
	}

	if target.Kind() != reflect.Struct {
		return i, fmt.Errorf("cannot unmarshal struct into %s", target.Type())
	}

	// Build field name → index mapping for the target struct.
	fields, err := StructFields(target.Type())
	if err != nil {
		return i, err
	}
	fmap := make(map[string]FieldInfo, len(fields))
	for _, fi := range fields {
		fmap[fi.Name] = fi
	}

	for i < len(events) {
		if events[i].Kind == EventStructEnd {
			return i + 1, nil
		}

		name := events[i].Name
		fi, ok := fmap[name]
		if !ok {
			// Skip this value.
			var err error
			i, err = skipValue(events, i)
			if err != nil {
				return i, err
			}
			continue
		}

		field := target.Field(fi.Index)
		i, err = consumeValue(events, i, field)
		if err != nil {
			return i, fmt.Errorf("field %q: %w", name, err)
		}
	}

	return i, fmt.Errorf("unexpected end of events in struct")
}

func consumeStructIntoMap(events []Event, i int, target reflect.Value) (int, error) {
	if target.IsNil() {
		target.Set(reflect.MakeMap(target.Type()))
	}

	valType := target.Type().Elem()

	for i < len(events) {
		if events[i].Kind == EventStructEnd {
			return i + 1, nil
		}

		name := events[i].Name
		val := reflect.New(valType).Elem()
		var err error
		i, err = consumeValue(events, i, val)
		if err != nil {
			return i, fmt.Errorf("map key %q: %w", name, err)
		}
		target.SetMapIndex(reflect.ValueOf(name), val)
	}

	return i, fmt.Errorf("unexpected end of events in map-from-struct")
}

func consumeList(events []Event, i int, target reflect.Value) (int, error) {
	i++ // skip ListStart

	if target.Kind() != reflect.Slice {
		return i, fmt.Errorf("cannot unmarshal list into %s", target.Type())
	}

	elemType := target.Type().Elem()
	slice := reflect.MakeSlice(target.Type(), 0, 0)

	for i < len(events) {
		if events[i].Kind == EventListEnd {
			target.Set(slice)
			return i + 1, nil
		}

		elem := reflect.New(elemType).Elem()
		var err error
		i, err = consumeValue(events, i, elem)
		if err != nil {
			return i, err
		}
		slice = reflect.Append(slice, elem)
	}

	return i, fmt.Errorf("unexpected end of events in list")
}

func consumeTuple(events []Event, i int, target reflect.Value) (int, error) {
	i++ // skip TupleStart

	if target.Kind() != reflect.Slice {
		return i, fmt.Errorf("cannot unmarshal tuple into %s", target.Type())
	}

	elemType := target.Type().Elem()
	slice := reflect.MakeSlice(target.Type(), 0, 0)

	for i < len(events) {
		if events[i].Kind == EventTupleEnd {
			target.Set(slice)
			return i + 1, nil
		}

		elem := reflect.New(elemType).Elem()
		var err error
		i, err = consumeValue(events, i, elem)
		if err != nil {
			return i, err
		}
		slice = reflect.Append(slice, elem)
	}

	return i, fmt.Errorf("unexpected end of events in tuple")
}

func consumeMap(events []Event, i int, target reflect.Value) (int, error) {
	i++ // skip MapStart

	if target.Kind() != reflect.Map {
		return i, fmt.Errorf("cannot unmarshal map into %s", target.Type())
	}

	if target.IsNil() {
		target.Set(reflect.MakeMap(target.Type()))
	}

	keyType := target.Type().Key()
	valType := target.Type().Elem()

	for i < len(events) {
		if events[i].Kind == EventMapEnd {
			return i + 1, nil
		}

		// In the event stream, map entries are emitted as key-value pairs:
		// ScalarValue name=keyStr (key event)
		// ScalarValue name=keyStr (value event)
		// We need to consume the key, then the value.
		key := reflect.New(keyType).Elem()
		var err error
		i, err = consumeValue(events, i, key)
		if err != nil {
			return i, fmt.Errorf("map key: %w", err)
		}

		if i >= len(events) {
			return i, fmt.Errorf("unexpected end of events: missing map value")
		}

		val := reflect.New(valType).Elem()
		i, err = consumeValue(events, i, val)
		if err != nil {
			return i, fmt.Errorf("map value: %w", err)
		}

		target.SetMapIndex(key, val)
	}

	return i, fmt.Errorf("unexpected end of events in map")
}

func consumeStreamList(events []Event, i int, target reflect.Value) (int, error) {
	i++ // skip ListStreamStart

	target = allocPtr(target)

	if target.Kind() != reflect.Slice {
		return i, fmt.Errorf("cannot unmarshal list stream into %s", target.Type())
	}

	elemType := target.Type().Elem()
	slice := reflect.MakeSlice(target.Type(), 0, 0)

	for i < len(events) {
		if events[i].Kind == EventListStreamEnd {
			target.Set(slice)
			return i + 1, nil
		}

		elem := reflect.New(elemType).Elem()
		var err error
		i, err = consumeValue(events, i, elem)
		if err != nil {
			return i, err
		}
		slice = reflect.Append(slice, elem)
	}

	return i, fmt.Errorf("unexpected end of events in list stream")
}

func consumeStreamMap(events []Event, i int, target reflect.Value) (int, error) {
	i++ // skip MapStreamStart

	target = allocPtr(target)

	if target.Kind() != reflect.Map {
		return i, fmt.Errorf("cannot unmarshal map stream into %s", target.Type())
	}

	if target.IsNil() {
		target.Set(reflect.MakeMap(target.Type()))
	}

	keyType := target.Type().Key()
	valType := target.Type().Elem()

	for i < len(events) {
		if events[i].Kind == EventMapStreamEnd {
			return i + 1, nil
		}

		key := reflect.New(keyType).Elem()
		var err error
		i, err = consumeValue(events, i, key)
		if err != nil {
			return i, fmt.Errorf("stream map key: %w", err)
		}

		if i >= len(events) || events[i].Kind == EventMapStreamEnd {
			return i, fmt.Errorf("unexpected end of events: missing stream map value")
		}

		val := reflect.New(valType).Elem()
		i, err = consumeValue(events, i, val)
		if err != nil {
			return i, fmt.Errorf("stream map value: %w", err)
		}

		target.SetMapIndex(key, val)
	}

	return i, fmt.Errorf("unexpected end of events in map stream")
}

// skipValue skips over a single value (scalar or composite) in the event slice.
func skipValue(events []Event, i int) (int, error) {
	if i >= len(events) {
		return i, fmt.Errorf("unexpected end of events during skip")
	}

	switch {
	case events[i].Kind == EventScalarValue:
		return i + 1, nil
	case events[i].Kind.IsCompositeStart():
		depth := 1
		i++
		for i < len(events) && depth > 0 {
			switch {
			case events[i].Kind.IsCompositeStart():
				depth++
			case events[i].Kind.IsCompositeEnd():
				depth--
			}
			i++
		}
		if depth != 0 {
			return i, fmt.Errorf("unbalanced composite during skip")
		}
		return i, nil
	default:
		return i + 1, nil
	}
}
