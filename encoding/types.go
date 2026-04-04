package encoding

import "fmt"

// TypeKind identifies a scalar type in the PAKT type system.
type TypeKind int

const (
	TypeStr      TypeKind = iota // str  — quoted string
	TypeInt                      // int  — signed 64-bit integer
	TypeDec                      // dec  — arbitrary-precision decimal
	TypeFloat                    // float — IEEE 754 binary64
	TypeBool                     // bool — true / false
	TypeUUID                     // uuid
	TypeDate                     // date — ISO date
	TypeTime                     // time — ISO time with timezone
	TypeDateTime                 // datetime — ISO datetime with timezone
)

var typeKindNames = [...]string{
	TypeStr:      "str",
	TypeInt:      "int",
	TypeDec:      "dec",
	TypeFloat:    "float",
	TypeBool:     "bool",
	TypeUUID:     "uuid",
	TypeDate:     "date",
	TypeTime:     "time",
	TypeDateTime: "datetime",
}

// String returns the PAKT keyword for the scalar type.
func (k TypeKind) String() string {
	if int(k) >= 0 && int(k) < len(typeKindNames) {
		return typeKindNames[k]
	}
	return fmt.Sprintf("TypeKind(%d)", int(k))
}

// Type represents any type expressible in PAKT, including scalars,
// composites, atom sets, and nullable wrappers.
type Type struct {
	// Exactly one of the following groups is set.

	// Scalar is non-nil when the type is a scalar.
	Scalar *TypeKind

	// AtomSet is non-nil when the type is an atom set (e.g. |dev, staging, prod|).
	AtomSet *AtomSet

	// Struct is non-nil when the type is a struct.
	Struct *StructType

	// Tuple is non-nil when the type is a tuple.
	Tuple *TupleType

	// List is non-nil when the type is a list.
	List *ListType

	// Map is non-nil when the type is a map.
	Map *MapType

	// Nullable is true when the type has a trailing '?'.
	Nullable bool
}

// String returns the PAKT type annotation syntax for this type.
func (t Type) String() string {
	var s string
	switch {
	case t.Scalar != nil:
		s = t.Scalar.String()
	case t.AtomSet != nil:
		s = t.AtomSet.String()
	case t.Struct != nil:
		s = t.Struct.String()
	case t.Tuple != nil:
		s = t.Tuple.String()
	case t.List != nil:
		s = t.List.String()
	case t.Map != nil:
		s = t.Map.String()
	default:
		s = "<unknown>"
	}
	if t.Nullable {
		s += "?"
	}
	return s
}

// AtomSet is a constrained set of bareword identifiers (e.g. |dev, staging, prod|).
type AtomSet struct {
	Members []string
}

// String returns the PAKT syntax for the atom set.
func (a *AtomSet) String() string {
	s := "|"
	for i, m := range a.Members {
		if i > 0 {
			s += ", "
		}
		s += m
	}
	s += "|"
	return s
}

// StructType is a composite type with named, heterogeneously-typed fields.
type StructType struct {
	Fields []Field
}

// String returns the PAKT syntax for the struct type.
func (st *StructType) String() string {
	s := "{"
	for i, f := range st.Fields {
		if i > 0 {
			s += ", "
		}
		s += f.Name + ":" + f.Type.String()
	}
	s += "}"
	return s
}

// Field describes a single field in a [StructType].
type Field struct {
	Name string // field identifier
	Type Type   // field type (may itself be nullable or composite)
}

// TupleType is a fixed-length composite of heterogeneous positional types.
type TupleType struct {
	Elements []Type
}

// String returns the PAKT syntax for the tuple type.
func (tt *TupleType) String() string {
	s := "("
	for i, e := range tt.Elements {
		if i > 0 {
			s += ", "
		}
		s += e.String()
	}
	s += ")"
	return s
}

// ListType is a homogeneous, variable-length sequence.
type ListType struct {
	Element Type
}

// String returns the PAKT syntax for the list type.
func (lt *ListType) String() string {
	return "[" + lt.Element.String() + "]"
}

// MapType is a homogeneous key-value collection.
type MapType struct {
	Key   Type
	Value Type
}

// String returns the PAKT syntax for the map type.
func (mt *MapType) String() string {
	return "<" + mt.Key.String() + " = " + mt.Value.String() + ">"
}
