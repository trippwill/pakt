package encoding

// ---------------------------------------------------------------------------
// Type annotation reading
// ---------------------------------------------------------------------------

// lookupScalarType maps a scalar keyword to its TypeKind.
func lookupScalarType(name string) (TypeKind, bool) {
	switch name {
	case "str":
		return TypeStr, true
	case "int":
		return TypeInt, true
	case "dec":
		return TypeDec, true
	case "float":
		return TypeFloat, true
	case "bool":
		return TypeBool, true
	case "uuid":
		return TypeUUID, true
	case "date":
		return TypeDate, true
	case "time":
		return TypeTime, true
	case "datetime":
		return TypeDateTime, true
	case "bin":
		return TypeBin, true
	default:
		return 0, false
	}
}

// readTypeAnnot reads ':' type '?'?. The colon must be the next byte (no
// leading whitespace — per spec §7 whitespace around ':' is not permitted).
func (r *reader) readTypeAnnot() (Type, error) {
	if err := r.expectByte(':'); err != nil {
		return Type{}, err
	}
	typ, err := r.readType()
	if err != nil {
		return Type{}, err
	}
	if b, perr := r.peekByte(); perr == nil && b == '?' {
		r.readByte() //nolint:errcheck
		typ.Nullable = true
	}
	return typ, nil
}

// readType performs LL(1) dispatch on the next byte to parse a type.
func (r *reader) readType() (Type, error) {
	r.skipWSAndNewlines()
	b, err := r.peekByte()
	if err != nil {
		return Type{}, r.wrapf(ErrUnexpectedEOF, "expected type, got EOF")
	}
	switch {
	case b == '|':
		return r.readAtomSetType()
	case b == '{':
		return r.readStructType()
	case b == '(':
		return r.readTupleType()
	case b == '[':
		return r.readListType()
	case b == '<':
		return r.readMapType()
	case isAlpha(b) || b == '_':
		return r.readScalarType()
	default:
		return Type{}, r.errorf("unexpected character in type: %q", rune(b))
	}
}

// readScalarType reads a scalar keyword (str, int, dec, …) and returns the
// corresponding Type.
func (r *reader) readScalarType() (Type, error) {
	ident, err := r.readIdent()
	if err != nil {
		return Type{}, err
	}
	kind, ok := lookupScalarType(ident)
	if !ok {
		return Type{}, r.errorf("unknown scalar type %q", ident)
	}
	k := kind // addressable copy
	return Type{Scalar: &k}, nil
}

// readAtomSetType reads PIPE IDENT (COMMA IDENT)* PIPE.
func (r *reader) readAtomSetType() (Type, error) {
	if err := r.expectByte('|'); err != nil {
		return Type{}, err
	}
	r.skipWSAndNewlines()
	first, err := r.readIdent()
	if err != nil {
		return Type{}, r.wrapf(ErrUnexpectedEOF, "expected atom in atom set, got EOF")
	}
	members := []string{first}
	for {
		r.skipWSAndNewlines()
		b, err := r.peekByte()
		if err != nil {
			return Type{}, r.wrapf(ErrUnexpectedEOF, "unterminated atom set")
		}
		if b == '|' {
			r.readByte() //nolint:errcheck
			break
		}
		if b != ',' {
			return Type{}, r.errorf("expected ',' or '|' in atom set, got %q", rune(b))
		}
		r.readByte() //nolint:errcheck // consume comma
		r.skipWSAndNewlines()
		ident, err := r.readIdent()
		if err != nil {
			return Type{}, r.wrapf(ErrUnexpectedEOF, "expected atom after ',' in atom set, got EOF")
		}
		members = append(members, ident)
	}
	return Type{AtomSet: &AtomSet{Members: members}}, nil
}

// readStructType reads LBRACE field (COMMA field)* RBRACE.
func (r *reader) readStructType() (Type, error) {
	if err := r.expectByte('{'); err != nil {
		return Type{}, err
	}
	r.skipWSAndNewlines()
	first, err := r.readFieldDecl()
	if err != nil {
		return Type{}, err
	}
	fields := []Field{first}
	for {
		r.skipWSAndNewlines()
		b, err := r.peekByte()
		if err != nil {
			return Type{}, r.wrapf(ErrUnexpectedEOF, "unterminated struct type")
		}
		if b == '}' {
			r.readByte() //nolint:errcheck
			break
		}
		if b != ',' {
			return Type{}, r.errorf("expected ',' or '}' in struct type, got %q", rune(b))
		}
		r.readByte() //nolint:errcheck // consume comma
		r.skipWSAndNewlines()
		f, ferr := r.readFieldDecl()
		if ferr != nil {
			return Type{}, ferr
		}
		fields = append(fields, f)
	}
	return Type{Struct: &StructType{Fields: fields}}, nil
}

// readFieldDecl reads IDENT COLON type '?'?.
func (r *reader) readFieldDecl() (Field, error) {
	name, err := r.readIdent()
	if err != nil {
		return Field{}, r.wrapf(ErrUnexpectedEOF, "expected field name, got EOF")
	}
	// Colon must immediately follow the name (no WS).
	if err := r.expectByte(':'); err != nil {
		return Field{}, err
	}
	typ, err := r.readType()
	if err != nil {
		return Field{}, err
	}
	if b, perr := r.peekByte(); perr == nil && b == '?' {
		r.readByte() //nolint:errcheck
		typ.Nullable = true
	}
	return Field{Name: name, Type: typ}, nil
}

// readTupleType reads LPAREN type (COMMA type)* RPAREN.
func (r *reader) readTupleType() (Type, error) {
	if err := r.expectByte('('); err != nil {
		return Type{}, err
	}
	r.skipWSAndNewlines()
	first, err := r.readType()
	if err != nil {
		return Type{}, err
	}
	elements := []Type{first}
	for {
		r.skipWSAndNewlines()
		b, err := r.peekByte()
		if err != nil {
			return Type{}, r.wrapf(ErrUnexpectedEOF, "unterminated tuple type")
		}
		if b == ')' {
			r.readByte() //nolint:errcheck
			break
		}
		if b != ',' {
			return Type{}, r.errorf("expected ',' or ')' in tuple type, got %q", rune(b))
		}
		r.readByte() //nolint:errcheck // consume comma
		r.skipWSAndNewlines()
		t, terr := r.readType()
		if terr != nil {
			return Type{}, terr
		}
		elements = append(elements, t)
	}
	return Type{Tuple: &TupleType{Elements: elements}}, nil
}

// readListType reads LBRACK type '?'? RBRACK.
func (r *reader) readListType() (Type, error) {
	if err := r.expectByte('['); err != nil {
		return Type{}, err
	}
	r.skipWSAndNewlines()
	elemType, err := r.readType()
	if err != nil {
		return Type{}, err
	}
	// Nullable element?
	if b, perr := r.peekByte(); perr == nil && b == '?' {
		r.readByte() //nolint:errcheck
		elemType.Nullable = true
	}
	r.skipWSAndNewlines()
	if err := r.expectByte(']'); err != nil {
		return Type{}, err
	}
	return Type{List: &ListType{Element: elemType}}, nil
}

// readMapType reads LANGLE type SEMI type RANGLE.
func (r *reader) readMapType() (Type, error) {
	if err := r.expectByte('<'); err != nil {
		return Type{}, err
	}
	r.skipWSAndNewlines()
	keyType, err := r.readType()
	if err != nil {
		return Type{}, err
	}
	r.skipWSAndNewlines()
	if err := r.expectByte(';'); err != nil {
		return Type{}, err
	}
	r.skipWSAndNewlines()
	valType, err := r.readType()
	if err != nil {
		return Type{}, err
	}
	r.skipWSAndNewlines()
	if err := r.expectByte('>'); err != nil {
		return Type{}, err
	}
	return Type{Map: &MapType{Key: keyType, Value: valType}}, nil
}
