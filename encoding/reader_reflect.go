package encoding

import (
	"encoding/hex"
	"fmt"
	"math"
	"reflect"
	"strconv"
	"time"
)

// readIntInto parses a PAKT integer literal directly into target without
// allocating an intermediate string. Falls back to string path for hex/bin/oct
// or underscore-containing literals.
func (r *reader) readIntInto(target reflect.Value) error {
	// Peek ahead to decide: fast decimal path or fallback.
	// We need to check for negative sign and base prefixes without consuming.
	p, _ := r.src.Peek(3)
	offset := 0
	if len(p) > 0 && p[0] == '-' {
		offset = 1
	}
	// If it starts with 0 followed by a base prefix, use fallback.
	if offset < len(p) && p[offset] == '0' && offset+1 < len(p) {
		next := p[offset+1]
		if next == 'x' || next == 'X' || next == 'b' || next == 'B' || next == 'o' || next == 'O' {
			val, err := r.readInt()
			if err != nil {
				return err
			}
			return setInt(target, val)
		}
	}

	// Fast path: decimal integer, accumulate value directly.
	neg := false
	if b, err := r.peekByte(); err == nil && b == '-' {
		r.readByte() //nolint:errcheck
		neg = true
	}

	first, err := r.peekByte()
	if err != nil {
		return r.wrapf(ErrUnexpectedEOF, "expected digit in integer, got EOF")
	}
	if !isDigit(first) {
		return r.errorf("expected digit in integer, got %q", rune(first))
	}

	var val uint64
	for {
		b, err := r.peekByte()
		if err != nil {
			break
		}
		if isDigit(b) {
			r.readByte() //nolint:errcheck
			val = val*10 + uint64(b-'0')
		} else if b == '_' {
			r.readByte() //nolint:errcheck
			// skip underscores
		} else {
			break
		}
	}

	if neg {
		if val > math.MaxInt64+1 {
			return r.errorf("integer literal overflows int64")
		}
		return setIntDirect(target, -int64(val))
	}
	if val > math.MaxInt64 {
		return r.errorf("integer literal overflows int64")
	}
	return setIntDirect(target, int64(val))
}

// setIntDirect sets a reflect.Value from an already-parsed int64.
func setIntDirect(target reflect.Value, n int64) error {
	target = allocPtr(target)
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
		target.SetString(strconv.FormatInt(n, 10))
		return nil
	default:
		return fmt.Errorf("cannot set int into %s", target.Type())
	}
}

// readBoolInto parses a PAKT bool directly into target.
func (r *reader) readBoolInto(target reflect.Value) error {
	id, err := r.readIdent()
	if err != nil {
		return err
	}
	if id != "true" && id != "false" {
		return r.errorf("expected 'true' or 'false', got %q", id)
	}
	target = allocPtr(target)
	if target.Kind() != reflect.Bool {
		return fmt.Errorf("cannot set bool into %s", target.Type())
	}
	target.SetBool(id == "true")
	return nil
}

// readFloatInto parses a PAKT float literal directly into target.
func (r *reader) readFloatInto(target reflect.Value) error {
	val, err := r.readFloat()
	if err != nil {
		return err
	}
	target = allocPtr(target)
	f, ferr := strconv.ParseFloat(val, 64)
	if ferr != nil {
		return fmt.Errorf("invalid float literal %q: %w", val, ferr)
	}
	switch target.Kind() {
	case reflect.Float32, reflect.Float64:
		target.SetFloat(f)
		return nil
	case reflect.String:
		target.SetString(val)
		return nil
	default:
		return fmt.Errorf("cannot set float into %s", target.Type())
	}
}

// readDecInto parses a PAKT decimal literal directly into target.
func (r *reader) readDecInto(target reflect.Value) error {
	val, err := r.readDec()
	if err != nil {
		return err
	}
	return setDec(target, val)
}

// readStringInto reads a PAKT string directly into target.
func (r *reader) readStringInto(target reflect.Value) error {
	val, err := r.readString()
	if err != nil {
		return err
	}
	return setString(allocPtr(target), val)
}

// readTsInto reads a PAKT timestamp directly into target.
func (r *reader) readTsInto(target reflect.Value) error {
	val, err := r.readTs()
	if err != nil {
		return err
	}
	return setDateTimeString(allocPtr(target), val, allocPtr(target).Kind())
}

// readDateInto reads a PAKT date directly into target.
func (r *reader) readDateInto(target reflect.Value) error {
	val, err := r.readDate()
	if err != nil {
		return err
	}
	return setDateTimeString(allocPtr(target), val, allocPtr(target).Kind())
}

// readUUIDInto reads a PAKT UUID directly into target.
func (r *reader) readUUIDInto(target reflect.Value) error {
	val, err := r.readUUID()
	if err != nil {
		return err
	}
	return setString(allocPtr(target), val)
}

// readBinInto reads a PAKT bin literal directly into target.
func (r *reader) readBinInto(target reflect.Value) error {
	val, err := r.readBin()
	if err != nil {
		return err
	}
	return setBin(allocPtr(target), val)
}

// readScalarInto dispatches to the appropriate read*Into method.
func (r *reader) readScalarInto(kind TypeKind, target reflect.Value) error {
	switch kind {
	case TypeStr:
		return r.readStringInto(target)
	case TypeInt:
		return r.readIntInto(target)
	case TypeDec:
		return r.readDecInto(target)
	case TypeFloat:
		return r.readFloatInto(target)
	case TypeBool:
		return r.readBoolInto(target)
	case TypeUUID:
		return r.readUUIDInto(target)
	case TypeDate:
		return r.readDateInto(target)
	case TypeTs:
		return r.readTsInto(target)
	case TypeBin:
		return r.readBinInto(target)
	default:
		return r.errorf("unknown scalar type kind %d", int(kind))
	}
}

// readNilInto sets target to its zero value.
func (r *reader) readNilInto(target reflect.Value) error {
	if err := r.readNil(); err != nil {
		return err
	}
	return setNil(target)
}

// readAtomInto reads an atom value directly into target.
func (r *reader) readAtomInto(allowed []string, target reflect.Value) error {
	val, err := r.readAtom(allowed)
	if err != nil {
		return err
	}
	return setString(allocPtr(target), val)
}

// Ensure time-related imports are available.
var _ = time.RFC3339
var _ = hex.DecodeString
