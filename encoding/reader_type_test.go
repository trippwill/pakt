package encoding

import (
	"strings"
	"testing"
)

// typeReader creates a reader positioned to read a type annotation.
func typeReader(s string) *reader {
	return newReader(strings.NewReader(s))
}

// ---------------------------------------------------------------------------
// Scalar types
// ---------------------------------------------------------------------------

func TestReadScalarTypes(t *testing.T) {
	scalars := []struct {
		input string
		kind  TypeKind
	}{
		{":str", TypeStr},
		{":int", TypeInt},
		{":dec", TypeDec},
		{":float", TypeFloat},
		{":bool", TypeBool},
		{":uuid", TypeUUID},
		{":date", TypeDate},
		{":time", TypeTime},
		{":datetime", TypeDateTime},
	}
	for _, tc := range scalars {
		r := typeReader(tc.input)
		got, err := r.readTypeAnnot()
		if err != nil {
			t.Errorf("readTypeAnnot(%q): %v", tc.input, err)
			continue
		}
		if got.Scalar == nil || *got.Scalar != tc.kind {
			t.Errorf("readTypeAnnot(%q): got %v, want scalar %v", tc.input, got, tc.kind)
		}
		if got.Nullable {
			t.Errorf("readTypeAnnot(%q): expected non-nullable", tc.input)
		}
	}
}

func TestReadScalarNullable(t *testing.T) {
	r := typeReader(":str?")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.Scalar == nil || *got.Scalar != TypeStr {
		t.Fatalf("expected str scalar, got %v", got)
	}
	if !got.Nullable {
		t.Fatal("expected nullable")
	}
}

// ---------------------------------------------------------------------------
// Atom set
// ---------------------------------------------------------------------------

func TestReadAtomSetSingle(t *testing.T) {
	r := typeReader(":|dev|")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.AtomSet == nil {
		t.Fatal("expected atom set")
	}
	if len(got.AtomSet.Members) != 1 || got.AtomSet.Members[0] != "dev" {
		t.Fatalf("got %v", got.AtomSet.Members)
	}
}

func TestReadAtomSetMultiple(t *testing.T) {
	r := typeReader(":|dev, staging, prod|")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.AtomSet == nil {
		t.Fatal("expected atom set")
	}
	want := []string{"dev", "staging", "prod"}
	if len(got.AtomSet.Members) != len(want) {
		t.Fatalf("got %v, want %v", got.AtomSet.Members, want)
	}
	for i, m := range got.AtomSet.Members {
		if m != want[i] {
			t.Fatalf("member %d: got %q, want %q", i, m, want[i])
		}
	}
}

func TestReadAtomSetNullable(t *testing.T) {
	r := typeReader(":|a, b|?")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.AtomSet == nil || !got.Nullable {
		t.Fatalf("expected nullable atom set, got %v", got)
	}
}

// ---------------------------------------------------------------------------
// Struct type
// ---------------------------------------------------------------------------

func TestReadStructSingle(t *testing.T) {
	r := typeReader(":{name:str}")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.Struct == nil {
		t.Fatal("expected struct")
	}
	if len(got.Struct.Fields) != 1 {
		t.Fatalf("expected 1 field, got %d", len(got.Struct.Fields))
	}
	f := got.Struct.Fields[0]
	if f.Name != "name" || f.Type.Scalar == nil || *f.Type.Scalar != TypeStr {
		t.Fatalf("got field %+v", f)
	}
}

func TestReadStructMultiple(t *testing.T) {
	r := typeReader(":{host:str, port:int}")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.Struct == nil || len(got.Struct.Fields) != 2 {
		t.Fatalf("expected 2 fields, got %v", got)
	}
	if got.Struct.Fields[0].Name != "host" {
		t.Fatalf("field 0: %+v", got.Struct.Fields[0])
	}
	if got.Struct.Fields[1].Name != "port" {
		t.Fatalf("field 1: %+v", got.Struct.Fields[1])
	}
}

func TestReadStructNullableField(t *testing.T) {
	r := typeReader(":{name:str?}")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.Struct == nil || !got.Struct.Fields[0].Type.Nullable {
		t.Fatal("expected nullable field")
	}
}

func TestReadStructNullable(t *testing.T) {
	r := typeReader(":{x:int}?")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.Struct == nil || !got.Nullable {
		t.Fatal("expected nullable struct")
	}
}

// ---------------------------------------------------------------------------
// Tuple type
// ---------------------------------------------------------------------------

func TestReadTupleSingle(t *testing.T) {
	r := typeReader(":(int)")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.Tuple == nil || len(got.Tuple.Elements) != 1 {
		t.Fatalf("expected 1-element tuple, got %v", got)
	}
}

func TestReadTupleMultiple(t *testing.T) {
	r := typeReader(":(int, str, bool)")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.Tuple == nil || len(got.Tuple.Elements) != 3 {
		t.Fatalf("expected 3-element tuple, got %v", got)
	}
	expected := []TypeKind{TypeInt, TypeStr, TypeBool}
	for i, e := range got.Tuple.Elements {
		if e.Scalar == nil || *e.Scalar != expected[i] {
			t.Fatalf("element %d: got %v, want %v", i, e, expected[i])
		}
	}
}

func TestReadTupleNullable(t *testing.T) {
	r := typeReader(":(int, str)?")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.Tuple == nil || !got.Nullable {
		t.Fatal("expected nullable tuple")
	}
}

// ---------------------------------------------------------------------------
// List type
// ---------------------------------------------------------------------------

func TestReadListSimple(t *testing.T) {
	r := typeReader(":[int]")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.List == nil {
		t.Fatal("expected list")
	}
	if got.List.Element.Scalar == nil || *got.List.Element.Scalar != TypeInt {
		t.Fatalf("expected [int], got %v", got)
	}
}

func TestReadListNullableElement(t *testing.T) {
	r := typeReader(":[str?]")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.List == nil {
		t.Fatal("expected list")
	}
	if !got.List.Element.Nullable {
		t.Fatal("expected nullable element")
	}
}

func TestReadListNullableType(t *testing.T) {
	r := typeReader(":[int]?")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.List == nil || !got.Nullable {
		t.Fatal("expected nullable list")
	}
}

// ---------------------------------------------------------------------------
// Map type
// ---------------------------------------------------------------------------

func TestReadMapScalar(t *testing.T) {
	r := typeReader(":<str = int>")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.Map == nil {
		t.Fatal("expected map")
	}
	if got.Map.Key.Scalar == nil || *got.Map.Key.Scalar != TypeStr {
		t.Fatalf("key: %v", got.Map.Key)
	}
	if got.Map.Value.Scalar == nil || *got.Map.Value.Scalar != TypeInt {
		t.Fatalf("value: %v", got.Map.Value)
	}
}

func TestReadMapCompositeValue(t *testing.T) {
	r := typeReader(":<str = {name:str, age:int}>")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.Map == nil || got.Map.Value.Struct == nil {
		t.Fatal("expected map with struct value")
	}
}

func TestReadMapNullable(t *testing.T) {
	r := typeReader(":<str = int>?")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.Map == nil || !got.Nullable {
		t.Fatal("expected nullable map")
	}
}

// ---------------------------------------------------------------------------
// Nested composites
// ---------------------------------------------------------------------------

func TestReadNestedStructList(t *testing.T) {
	r := typeReader(":{users:[{name:str, age:int}], count:int}")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.Struct == nil || len(got.Struct.Fields) != 2 {
		t.Fatalf("expected 2-field struct, got %v", got)
	}

	// First field: users:[{name:str, age:int}]
	users := got.Struct.Fields[0]
	if users.Name != "users" {
		t.Fatalf("first field name: %q", users.Name)
	}
	if users.Type.List == nil {
		t.Fatal("expected list type for users")
	}
	elemType := users.Type.List.Element
	if elemType.Struct == nil || len(elemType.Struct.Fields) != 2 {
		t.Fatalf("expected struct element with 2 fields, got %v", elemType)
	}
	if elemType.Struct.Fields[0].Name != "name" {
		t.Fatalf("inner field 0: %+v", elemType.Struct.Fields[0])
	}
	if elemType.Struct.Fields[1].Name != "age" {
		t.Fatalf("inner field 1: %+v", elemType.Struct.Fields[1])
	}

	// Second field: count:int
	count := got.Struct.Fields[1]
	if count.Name != "count" || count.Type.Scalar == nil || *count.Type.Scalar != TypeInt {
		t.Fatalf("second field: %+v", count)
	}
}

func TestReadTupleOfLists(t *testing.T) {
	r := typeReader(":([int], [str])")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.Tuple == nil || len(got.Tuple.Elements) != 2 {
		t.Fatalf("expected 2-element tuple, got %v", got)
	}
	if got.Tuple.Elements[0].List == nil {
		t.Fatal("element 0: expected list")
	}
	if got.Tuple.Elements[1].List == nil {
		t.Fatal("element 1: expected list")
	}
}

func TestReadMapOfListToStruct(t *testing.T) {
	r := typeReader(":<str = [int]>")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.Map == nil {
		t.Fatal("expected map")
	}
	if got.Map.Value.List == nil {
		t.Fatal("expected list value type")
	}
}

// ---------------------------------------------------------------------------
// Unknown and error cases
// ---------------------------------------------------------------------------

func TestReadTypeAnnotMissingColon(t *testing.T) {
	r := typeReader("str")
	_, err := r.readTypeAnnot()
	if err == nil {
		t.Fatal("expected error for missing colon")
	}
}

func TestReadUnknownScalar(t *testing.T) {
	r := typeReader(":foobar")
	_, err := r.readTypeAnnot()
	if err == nil {
		t.Fatal("expected error for unknown scalar type")
	}
}

func TestReadTypeWithWSInsideStruct(t *testing.T) {
	r := typeReader(":{ host:str , port:int }")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.Struct == nil || len(got.Struct.Fields) != 2 {
		t.Fatalf("expected 2-field struct, got %v", got)
	}
}

func TestReadAtomSetTypeNoSpace(t *testing.T) {
	r := typeReader(":|a,b,c|")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.AtomSet == nil || len(got.AtomSet.Members) != 3 {
		t.Fatalf("expected 3-member atom set, got %v", got)
	}
}

func TestReadListOfNullableStruct(t *testing.T) {
	r := typeReader(":[{name:str}?]")
	got, err := r.readTypeAnnot()
	if err != nil {
		t.Fatal(err)
	}
	if got.List == nil {
		t.Fatal("expected list")
	}
	if !got.List.Element.Nullable {
		t.Fatal("expected nullable element")
	}
	if got.List.Element.Struct == nil {
		t.Fatal("expected struct element")
	}
}

func TestReadTypeString(t *testing.T) {
	tests := []struct {
		input string
		want  string
	}{
		{":str", "str"},
		{":int?", "int?"},
		{":|a, b|", "|a, b|"},
		{":{x:int}", "{x:int}"},
		{":(int, str)", "(int, str)"},
		{":[bool]", "[bool]"},
		{":<str = int>", "<str = int>"},
	}
	for _, tc := range tests {
		r := typeReader(tc.input)
		got, err := r.readTypeAnnot()
		if err != nil {
			t.Errorf("readTypeAnnot(%q): %v", tc.input, err)
			continue
		}
		if s := got.String(); s != tc.want {
			t.Errorf("readTypeAnnot(%q).String() = %q, want %q", tc.input, s, tc.want)
		}
	}
}
