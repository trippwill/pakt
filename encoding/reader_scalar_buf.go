package encoding

import (
	"encoding/base64"
	"encoding/hex"
)

// readIntTo reads an integer literal into w (zero-copy variant of readInt).
func (r *reader) readIntTo(w byteAppender) error {
	if b, err := r.peekByte(); err == nil && b == '-' {
		r.readByte()     //nolint:errcheck
		w.WriteByte('-') //nolint:errcheck
	}

	first, err := r.peekByte()
	if err != nil {
		return r.wrapf(ErrUnexpectedEOF, "expected digit in integer, got EOF")
	}
	if !isDigit(first) {
		return r.errorf("expected digit in integer, got %q", rune(first))
	}

	if first == '0' {
		r.readByte()     //nolint:errcheck
		w.WriteByte('0') //nolint:errcheck
		if b, err := r.peekByte(); err == nil {
			switch b {
			case 'x':
				r.readByte()     //nolint:errcheck
				w.WriteByte('x') //nolint:errcheck
				return r.readPrefixedDigits(w, isHex)
			case 'b':
				r.readByte()     //nolint:errcheck
				w.WriteByte('b') //nolint:errcheck
				return r.readPrefixedDigits(w, isBin)
			case 'o':
				r.readByte()     //nolint:errcheck
				w.WriteByte('o') //nolint:errcheck
				return r.readPrefixedDigits(w, isOct)
			}
		}
		for {
			b, err := r.peekByte()
			if err != nil {
				break
			}
			if isDigit(b) || b == '_' {
				r.readByte()   //nolint:errcheck
				w.WriteByte(b) //nolint:errcheck
			} else {
				break
			}
		}
		return nil //nolint:nilerr // EOF on peek means int ended at EOF

	}

	return r.readDigitSep(w)
}

// readDecTo reads a decimal literal into w.
func (r *reader) readDecTo(w byteAppender) error {
	if b, err := r.peekByte(); err == nil && b == '-' {
		r.readByte()     //nolint:errcheck
		w.WriteByte('-') //nolint:errcheck
	}
	if b, err := r.peekByte(); err == nil && b != '.' {
		if err := r.readDigitSep(w); err != nil {
			return err
		}
	}
	if err := r.expectByte('.'); err != nil {
		return err
	}
	w.WriteByte('.') //nolint:errcheck
	return r.readDigitSep(w)
}

// readFloatTo reads a float literal into w.
func (r *reader) readFloatTo(w byteAppender) error {
	if b, err := r.peekByte(); err == nil && b == '-' {
		r.readByte()     //nolint:errcheck
		w.WriteByte('-') //nolint:errcheck
	}
	if b, err := r.peekByte(); err == nil && b != '.' && b != 'e' && b != 'E' {
		if err := r.readDigitSep(w); err != nil {
			return err
		}
	}

	if b, err := r.peekByte(); err == nil && b == '.' {
		r.readByte()     //nolint:errcheck
		w.WriteByte('.') //nolint:errcheck
		if err := r.readDigitSep(w); err != nil {
			return err
		}
	}

	b, err := r.peekByte()
	if err != nil {
		return r.wrapf(ErrUnexpectedEOF, "expected exponent ('e' or 'E') in float, got EOF")
	}
	if b != 'e' && b != 'E' {
		return r.errorf("expected exponent ('e' or 'E') in float, got %q", rune(b))
	}
	r.readByte()   //nolint:errcheck
	w.WriteByte(b) //nolint:errcheck

	if b, err := r.peekByte(); err == nil && (b == '+' || b == '-') {
		r.readByte()   //nolint:errcheck
		w.WriteByte(b) //nolint:errcheck
	}

	b, err = r.readByte()
	if err != nil {
		return r.wrapf(ErrUnexpectedEOF, "expected digit in float exponent, got EOF")
	}
	if !isDigit(b) {
		r.unreadByte()
		return r.errorf("expected digit in float exponent, got %q", rune(b))
	}
	w.WriteByte(b) //nolint:errcheck
	for {
		b, err = r.peekByte()
		if err != nil {
			break
		}
		if isDigit(b) {
			r.readByte()   //nolint:errcheck
			w.WriteByte(b) //nolint:errcheck
		} else {
			break
		}
	}
	return nil //nolint:nilerr // EOF on peek means float exponent ended at EOF

}

// readBoolTo reads a boolean keyword into w.
func (r *reader) readBoolTo(w byteAppender) error {
	id, err := r.readIdent()
	if err != nil {
		return err
	}
	if id != "true" && id != "false" {
		return r.errorf("expected 'true' or 'false', got %q", id)
	}
	for i := range len(id) {
		w.WriteByte(id[i]) //nolint:errcheck
	}
	return nil
}

// readDateTo reads DATE = DIGIT{4}-DIGIT{2}-DIGIT{2} into w.
func (r *reader) readDateTo(w byteAppender) error {
	if err := r.readExactDigits(w, 4); err != nil {
		return err
	}
	if err := r.expectByte('-'); err != nil {
		return err
	}
	w.WriteByte('-') //nolint:errcheck
	if err := r.readExactDigits(w, 2); err != nil {
		return err
	}
	if err := r.expectByte('-'); err != nil {
		return err
	}
	w.WriteByte('-') //nolint:errcheck
	return r.readExactDigits(w, 2)
}

// readTsTo reads a timestamp into w.
func (r *reader) readTsTo(w byteAppender) error {
	if err := r.readDateTo(w); err != nil {
		return err
	}
	if err := r.expectByte('T'); err != nil {
		return err
	}
	w.WriteByte('T') //nolint:errcheck
	if err := r.readExactDigits(w, 2); err != nil {
		return err
	}
	if err := r.expectByte(':'); err != nil {
		return err
	}
	w.WriteByte(':') //nolint:errcheck
	if err := r.readExactDigits(w, 2); err != nil {
		return err
	}
	if err := r.expectByte(':'); err != nil {
		return err
	}
	w.WriteByte(':') //nolint:errcheck
	if err := r.readExactDigits(w, 2); err != nil {
		return err
	}
	// Optional fractional seconds.
	if b, err := r.peekByte(); err == nil && b == '.' {
		r.readByte()     //nolint:errcheck
		w.WriteByte('.') //nolint:errcheck
		for {
			b, err := r.peekByte()
			if err != nil || !isDigit(b) {
				break
			}
			r.readByte()   //nolint:errcheck
			w.WriteByte(b) //nolint:errcheck
		}
	}
	// Timezone.
	b, err := r.peekByte()
	if err != nil {
		return r.wrapf(ErrUnexpectedEOF, "expected timezone in timestamp")
	}
	if b == 'Z' {
		r.readByte()     //nolint:errcheck
		w.WriteByte('Z') //nolint:errcheck
		return nil
	}
	if b == '+' || b == '-' {
		r.readByte()   //nolint:errcheck
		w.WriteByte(b) //nolint:errcheck
		if err := r.readExactDigits(w, 2); err != nil {
			return err
		}
		if err := r.expectByte(':'); err != nil {
			return err
		}
		w.WriteByte(':') //nolint:errcheck
		return r.readExactDigits(w, 2)
	}
	return r.errorf("expected timezone ('Z' or '+'/'-'), got %q", rune(b))
}

// readUUIDTo reads UUID into w.
func (r *reader) readUUIDTo(w byteAppender) error {
	if err := r.readExactHex(w, 8); err != nil {
		return err
	}
	if err := r.expectByte('-'); err != nil {
		return err
	}
	w.WriteByte('-') //nolint:errcheck
	if err := r.readExactHex(w, 4); err != nil {
		return err
	}
	if err := r.expectByte('-'); err != nil {
		return err
	}
	w.WriteByte('-') //nolint:errcheck
	if err := r.readExactHex(w, 4); err != nil {
		return err
	}
	if err := r.expectByte('-'); err != nil {
		return err
	}
	w.WriteByte('-') //nolint:errcheck
	if err := r.readExactHex(w, 4); err != nil {
		return err
	}
	if err := r.expectByte('-'); err != nil {
		return err
	}
	w.WriteByte('-') //nolint:errcheck
	return r.readExactHex(w, 12)
}

// readStringTo reads a quoted string value into w.
// Strings require escape processing so this delegates to readString
// and copies the result. Future optimization: scan the peek buffer
// and avoid the intermediate string for escape-free strings.
func (r *reader) readStringTo(w byteAppender) error {
	val, err := r.readString()
	if err != nil {
		return err
	}
	for i := range len(val) {
		w.WriteByte(val[i]) //nolint:errcheck
	}
	return nil
}

// readBinTo reads a binary literal directly into w.
// No escape processing needed — bin literals contain only hex/base64 chars.
func (r *reader) readBinTo(w byteAppender) error {
	prefix, err := r.readByte()
	if err != nil {
		return r.wrapf(ErrUnexpectedEOF, "expected binary literal, got EOF")
	}
	if prefix != 'x' && prefix != 'b' {
		r.unreadByte()
		return r.errorf("expected binary literal, got %q", rune(prefix))
	}
	if err := r.expectByte('\''); err != nil {
		return err
	}

	// Scan the raw content between quotes into a temporary slice.
	// We need the raw content to validate hex/base64 before writing
	// the normalized hex output to w.
	r.sb.Reset()
	for {
		ch, err := r.readByte()
		if err != nil {
			return r.wrapf(ErrUnexpectedEOF, "unterminated binary literal")
		}
		if ch == '\'' {
			break
		}
		if ch == '\n' {
			return r.errorf("newline in binary literal")
		}
		if ch == 0 {
			return r.errorf("null byte in binary literal")
		}
		r.sb.WriteByte(ch)
	}

	lit := r.sb.String()
	switch prefix {
	case 'x':
		if len(lit)%2 != 0 {
			return r.errorf("hex binary literal must contain an even number of digits")
		}
		data, derr := hex.DecodeString(lit)
		if derr != nil {
			return r.errorf("invalid hex binary literal")
		}
		encoded := hex.EncodeToString(data)
		for i := range len(encoded) {
			w.WriteByte(encoded[i]) //nolint:errcheck
		}
		return nil
	case 'b':
		data, derr := base64.StdEncoding.Strict().DecodeString(lit)
		if derr != nil {
			return r.errorf("invalid base64 binary literal")
		}
		encoded := hex.EncodeToString(data)
		for i := range len(encoded) {
			w.WriteByte(encoded[i]) //nolint:errcheck
		}
		return nil
	default:
		return r.errorf("unknown binary literal prefix %q", rune(prefix))
	}
}
