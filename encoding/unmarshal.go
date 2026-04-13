package encoding

import (
	"fmt"
	"math"
	"reflect"
	"strconv"
	"strings"
	"time"
)

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
		// Clone to ensure the string is independently allocated
		// (raw may be an unsafe view of borrowed bytes).
		target.SetString(strings.Clone(raw))
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
		return -int64(val), nil //nolint:gosec // overflow checked: val <= MaxInt64+1
	}
	if val > math.MaxInt64 {
		return 0, fmt.Errorf("int literal %q overflows int64", raw)
	}
	return int64(val), nil
}

func setDec(target reflect.Value, raw string) error {
	switch target.Kind() {
	case reflect.String:
		target.SetString(strings.Clone(raw))
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

func setTemporalString(target reflect.Value, raw string, kind reflect.Kind) error {
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
		target.SetString(strings.Clone(raw))
		return nil
	}

	return fmt.Errorf("cannot set date/ts into %s", target.Type())
}

// parseTime parses ISO 8601 date or timestamp strings.
func parseTime(s string) (time.Time, error) {
	// Try timestamp formats first (has 'T' separator).
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

	return time.Time{}, fmt.Errorf("cannot parse date/ts %q", s)
}
