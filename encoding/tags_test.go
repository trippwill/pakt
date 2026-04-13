package encoding

import (
	"reflect"
	"testing"
	"time"
)

func TestParseTag(t *testing.T) {
	tests := []struct {
		tag       string
		name      string
		omitEmpty bool
		skip      bool
	}{
		{"", "", false, false},
		{"-", "", false, true},
		{"field_name", "field_name", false, false},
		{",omitempty", "", true, false},
		{"field_name,omitempty", "field_name", true, false},
		{"name,omitempty,extra", "name", true, false},
	}

	for _, tt := range tests {
		name, omitEmpty, skip := parseTag(tt.tag)
		if name != tt.name || omitEmpty != tt.omitEmpty || skip != tt.skip {
			t.Errorf("parseTag(%q) = (%q, %v, %v), want (%q, %v, %v)",
				tt.tag, name, omitEmpty, skip, tt.name, tt.omitEmpty, tt.skip)
		}
	}
}

func TestTypeOf_Scalars(t *testing.T) {
	tests := []struct {
		name string
		val  any
		want string
	}{
		{"string", "hello", "str"},
		{"int", 42, "int"},
		{"int8", int8(1), "int"},
		{"int16", int16(1), "int"},
		{"int32", int32(1), "int"},
		{"int64", int64(1), "int"},
		{"uint", uint(1), "int"},
		{"uint8", uint8(1), "int"},
		{"uint16", uint16(1), "int"},
		{"uint32", uint32(1), "int"},
		{"uint64", uint64(1), "int"},
		{"float32", float32(1.0), "float"},
		{"float64", float64(1.0), "float"},
		{"bool", true, "bool"},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			typ, err := TypeOf(tt.val)
			if err != nil {
				t.Fatalf("TypeOf(%T): %v", tt.val, err)
			}
			if got := typ.String(); got != tt.want {
				t.Errorf("TypeOf(%T) = %s, want %s", tt.val, got, tt.want)
			}
		})
	}
}

func TestTypeOf_TimeTime(t *testing.T) {
	typ, err := TypeOf(time.Now())
	if err != nil {
		t.Fatalf("TypeOf(time.Time): %v", err)
	}
	if got := typ.String(); got != "ts" {
		t.Errorf("TypeOf(time.Time) = %s, want ts", got)
	}
}

func TestTypeOf_ByteSlice(t *testing.T) {
	typ, err := TypeOf([]byte("hello"))
	if err != nil {
		t.Fatalf("TypeOf([]byte): %v", err)
	}
	if got := typ.String(); got != "bin" {
		t.Errorf("TypeOf([]byte) = %s, want bin", got)
	}
}

func TestTypeOf_Pointer(t *testing.T) {
	s := "hello"
	typ, err := TypeOf(&s)
	if err != nil {
		t.Fatalf("TypeOf(*string): %v", err)
	}
	if got := typ.String(); got != "str?" {
		t.Errorf("TypeOf(*string) = %s, want str?", got)
	}
	if !typ.Nullable {
		t.Error("pointer type should be nullable")
	}
}

func TestTypeOf_Slice(t *testing.T) {
	typ, err := TypeOf([]int{1, 2, 3})
	if err != nil {
		t.Fatalf("TypeOf([]int): %v", err)
	}
	if got := typ.String(); got != "[int]" {
		t.Errorf("TypeOf([]int) = %s, want [int]", got)
	}
}

func TestTypeOf_Map(t *testing.T) {
	typ, err := TypeOf(map[string]int{"a": 1})
	if err != nil {
		t.Fatalf("TypeOf(map[string]int): %v", err)
	}
	if got := typ.String(); got != "<str ; int>" {
		t.Errorf("TypeOf(map[string]int) = %s, want <str ; int>", got)
	}
}

func TestTypeOf_Nil(t *testing.T) {
	_, err := TypeOf(nil)
	if err == nil {
		t.Error("TypeOf(nil) should return error")
	}
}

func TestTypeOf_Interface(t *testing.T) {
	type hasInterface struct {
		X any
	}
	_, err := TypeOf(hasInterface{})
	if err == nil {
		t.Error("TypeOf(struct with interface field) should return error")
	}
}

func TestTypeOf_SimpleStruct(t *testing.T) {
	type Person struct {
		Name string
		Age  int
	}
	typ, err := TypeOf(Person{})
	if err != nil {
		t.Fatalf("TypeOf(Person): %v", err)
	}
	if typ.Struct == nil {
		t.Fatal("expected struct type")
	}
	if got := typ.String(); got != "{name:str, age:int}" {
		t.Errorf("TypeOf(Person) = %s, want {name:str, age:int}", got)
	}
}

func TestTypeOf_AllScalarFieldStruct(t *testing.T) {
	type AllScalars struct {
		S  string
		I  int
		I8 int8
		U  uint
		F  float64
		B  bool
		T  time.Time
	}
	typ, err := TypeOf(AllScalars{})
	if err != nil {
		t.Fatalf("TypeOf(AllScalars): %v", err)
	}
	want := "{s:str, i:int, i8:int, u:int, f:float, b:bool, t:ts}"
	if got := typ.String(); got != want {
		t.Errorf("TypeOf(AllScalars) = %s, want %s", got, want)
	}
}

func TestTypeOf_StructWithTags(t *testing.T) {
	type Tagged struct {
		FirstName string `pakt:"first_name"`
		LastName  string `pakt:"last_name"`
		Age       int    `pakt:"age"`
	}
	typ, err := TypeOf(Tagged{})
	if err != nil {
		t.Fatalf("TypeOf(Tagged): %v", err)
	}
	want := "{first_name:str, last_name:str, age:int}"
	if got := typ.String(); got != want {
		t.Errorf("TypeOf(Tagged) = %s, want %s", got, want)
	}
}

func TestTypeOf_StructWithSkip(t *testing.T) {
	type WithSkip struct {
		Name    string
		Secret  string `pakt:"-"`
		Visible int
	}
	typ, err := TypeOf(WithSkip{})
	if err != nil {
		t.Fatalf("TypeOf(WithSkip): %v", err)
	}
	if typ.Struct == nil {
		t.Fatal("expected struct type")
	}
	if len(typ.Struct.Fields) != 2 {
		t.Fatalf("expected 2 fields, got %d", len(typ.Struct.Fields))
	}
	if typ.Struct.Fields[0].Name != "name" {
		t.Errorf("field 0 name = %q, want %q", typ.Struct.Fields[0].Name, "name")
	}
	if typ.Struct.Fields[1].Name != "visible" {
		t.Errorf("field 1 name = %q, want %q", typ.Struct.Fields[1].Name, "visible")
	}
}

func TestTypeOf_StructWithPointerFields(t *testing.T) {
	type WithPointers struct {
		Name  *string
		Count *int
	}
	typ, err := TypeOf(WithPointers{})
	if err != nil {
		t.Fatalf("TypeOf(WithPointers): %v", err)
	}
	want := "{name:str?, count:int?}"
	if got := typ.String(); got != want {
		t.Errorf("TypeOf(WithPointers) = %s, want %s", got, want)
	}
}

func TestTypeOf_NestedStruct(t *testing.T) {
	type Address struct {
		City  string
		State string
	}
	type Person struct {
		Name    string
		Address Address
	}
	typ, err := TypeOf(Person{})
	if err != nil {
		t.Fatalf("TypeOf(Person): %v", err)
	}
	want := "{name:str, address:{city:str, state:str}}"
	if got := typ.String(); got != want {
		t.Errorf("TypeOf(Person) = %s, want %s", got, want)
	}
}

func TestTypeOf_SliceField(t *testing.T) {
	type WithSlice struct {
		Tags []string
	}
	typ, err := TypeOf(WithSlice{})
	if err != nil {
		t.Fatalf("TypeOf(WithSlice): %v", err)
	}
	want := "{tags:[str]}"
	if got := typ.String(); got != want {
		t.Errorf("TypeOf(WithSlice) = %s, want %s", got, want)
	}
}

func TestTypeOf_MapField(t *testing.T) {
	type WithMap struct {
		Labels map[string]string
	}
	typ, err := TypeOf(WithMap{})
	if err != nil {
		t.Fatalf("TypeOf(WithMap): %v", err)
	}
	want := "{labels:<str ; str>}"
	if got := typ.String(); got != want {
		t.Errorf("TypeOf(WithMap) = %s, want %s", got, want)
	}
}

func TestTypeOf_UnexportedFieldsSkipped(t *testing.T) {
	type WithUnexported struct {
		Name   string
		secret string //nolint:unused
		Age    int
	}
	typ, err := TypeOf(WithUnexported{})
	if err != nil {
		t.Fatalf("TypeOf(WithUnexported): %v", err)
	}
	if typ.Struct == nil {
		t.Fatal("expected struct type")
	}
	if len(typ.Struct.Fields) != 2 {
		t.Fatalf("expected 2 fields, got %d", len(typ.Struct.Fields))
	}
	if typ.Struct.Fields[0].Name != "name" {
		t.Errorf("field 0 name = %q, want %q", typ.Struct.Fields[0].Name, "name")
	}
	if typ.Struct.Fields[1].Name != "age" {
		t.Errorf("field 1 name = %q, want %q", typ.Struct.Fields[1].Name, "age")
	}
}

func TestTypeOf_EmbeddedStruct(t *testing.T) {
	type Base struct {
		ID   int
		Name string
	}
	type Extended struct {
		Base
		Extra string
	}
	typ, err := TypeOf(Extended{})
	if err != nil {
		t.Fatalf("TypeOf(Extended): %v", err)
	}
	if typ.Struct == nil {
		t.Fatal("expected struct type")
	}
	// Embedded struct should be flattened.
	if len(typ.Struct.Fields) != 3 {
		t.Fatalf("expected 3 fields (flattened), got %d: %s", len(typ.Struct.Fields), typ.String())
	}
	names := make([]string, len(typ.Struct.Fields))
	for i, f := range typ.Struct.Fields {
		names[i] = f.Name
	}
	if names[0] != "id" || names[1] != "name" || names[2] != "extra" {
		t.Errorf("field names = %v, want [id name extra]", names)
	}
}

func TestTypeOf_NoTags_AutoDetect(t *testing.T) {
	type AutoDetect struct {
		MyField    string
		AnotherOne int
	}
	typ, err := TypeOf(AutoDetect{})
	if err != nil {
		t.Fatalf("TypeOf(AutoDetect): %v", err)
	}
	want := "{myfield:str, anotherone:int}"
	if got := typ.String(); got != want {
		t.Errorf("TypeOf(AutoDetect) = %s, want %s", got, want)
	}
}

func TestTypeOf_ComplexNested(t *testing.T) {
	type User struct {
		Name string
		Age  int
	}
	type Org struct {
		Users []User
	}
	typ, err := TypeOf(Org{})
	if err != nil {
		t.Fatalf("TypeOf(Org): %v", err)
	}
	want := "{users:[{name:str, age:int}]}"
	if got := typ.String(); got != want {
		t.Errorf("TypeOf(Org) = %s, want %s", got, want)
	}
}

func TestStructFields_OmitEmpty(t *testing.T) {
	type WithOmit struct {
		Name  string `pakt:",omitempty"`
		Value int    `pakt:"val,omitempty"`
	}
	fields, err := ReflectStructFields(reflect.TypeOf(WithOmit{}))
	if err != nil {
		t.Fatalf("StructFields: %v", err)
	}
	if len(fields) != 2 {
		t.Fatalf("expected 2 fields, got %d", len(fields))
	}
	if !fields[0].OmitEmpty {
		t.Error("field 0 should have OmitEmpty=true")
	}
	if fields[0].Name != "name" {
		t.Errorf("field 0 name = %q, want %q", fields[0].Name, "name")
	}
	if !fields[1].OmitEmpty {
		t.Error("field 1 should have OmitEmpty=true")
	}
	if fields[1].Name != "val" {
		t.Errorf("field 1 name = %q, want %q", fields[1].Name, "val")
	}
}

func TestStructFields_PointerToStruct(t *testing.T) {
	type S struct {
		X int
	}
	fields, err := ReflectStructFields(reflect.TypeOf(&S{}))
	if err != nil {
		t.Fatalf("ReflectStructFields(*S): %v", err)
	}
	if len(fields) != 1 {
		t.Fatalf("expected 1 field, got %d", len(fields))
	}
	if fields[0].Name != "x" {
		t.Errorf("field name = %q, want %q", fields[0].Name, "x")
	}
}

func TestStructFields_NonStruct(t *testing.T) {
	_, err := ReflectStructFields(reflect.TypeOf("hello"))
	if err == nil {
		t.Error("ReflectStructFields(string) should return error")
	}
}

func TestTypeOf_RecursiveType(t *testing.T) {
	type Node struct {
		Value int
		Next  *Node
	}
	// Recursive types should error rather than infinite loop.
	_, err := TypeOf(Node{})
	if err == nil {
		t.Error("TypeOf(recursive type) should return error")
	}
}

func TestTypeOf_MapOfSlice(t *testing.T) {
	type Config struct {
		Hosts map[string][]string
	}
	typ, err := TypeOf(Config{})
	if err != nil {
		t.Fatalf("TypeOf(Config): %v", err)
	}
	want := "{hosts:<str ; [str]>}"
	if got := typ.String(); got != want {
		t.Errorf("TypeOf(Config) = %s, want %s", got, want)
	}
}

func TestTypeOf_SliceOfPointers(t *testing.T) {
	type List struct {
		Items []*string
	}
	typ, err := TypeOf(List{})
	if err != nil {
		t.Fatalf("TypeOf(List): %v", err)
	}
	want := "{items:[str?]}"
	if got := typ.String(); got != want {
		t.Errorf("TypeOf(List) = %s, want %s", got, want)
	}
}

func TestStructFields_Index(t *testing.T) {
	type S struct {
		A string
		B int
		C bool
	}
	fields, err := ReflectStructFields(reflect.TypeOf(S{}))
	if err != nil {
		t.Fatalf("StructFields: %v", err)
	}
	for i, fi := range fields {
		if fi.Index != i {
			t.Errorf("field %d: Index = %d, want %d", i, fi.Index, i)
		}
	}
}
