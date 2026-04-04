package encoding

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

// AtomSet is a constrained set of bareword identifiers (e.g. |dev, staging, prod|).
type AtomSet struct {
	Members []string
}

// StructType is a composite type with named, heterogeneously-typed fields.
type StructType struct {
	Fields []Field
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

// ListType is a homogeneous, variable-length sequence.
type ListType struct {
	Element Type
}

// MapType is a homogeneous key-value collection.
type MapType struct {
	Key   Type
	Value Type
}
