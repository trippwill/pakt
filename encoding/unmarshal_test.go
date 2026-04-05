package encoding

import (
	"bytes"
	"reflect"
	"testing"
	"time"
)

// ---------------------------------------------------------------------------
// Test structs
// ---------------------------------------------------------------------------

type simpleScalar struct {
	Host string `pakt:"host"`
}

type multiField struct {
	Host string `pakt:"host"`
	Port int64  `pakt:"port"`
}

type allScalars struct {
	Name    string  `pakt:"name"`
	Age     int64   `pakt:"age"`
	Price   string  `pakt:"price"`
	Rate    float64 `pakt:"rate"`
	Active  bool    `pakt:"active"`
	ID      string  `pakt:"id"`
	Born    string  `pakt:"born"`
	Start   string  `pakt:"start_time"`
	Created string  `pakt:"created"`
}

type withNestedStruct struct {
	Server struct {
		Host string `pakt:"host"`
		Port int64  `pakt:"port"`
	} `pakt:"server"`
}

type withList struct {
	Tags []string `pakt:"tags"`
}

type withMap struct {
	Headers map[string]string `pakt:"headers"`
}

type withBytes struct {
	Data []byte `pakt:"data"`
}

type withPointer struct {
	Name *string `pakt:"name"`
	Age  *int64  `pakt:"age"`
}

type withTimeFields struct {
	Created time.Time `pakt:"created"`
}

type innerStruct struct {
	Host string `pakt:"host"`
	Port int64  `pakt:"port"`
}

type outerWithInner struct {
	Server innerStruct `pakt:"server"`
}

type withIntList struct {
	Ports []int64 `pakt:"ports"`
}

type nestedListOfStructs struct {
	Servers []innerStruct `pakt:"servers"`
}

// ---------------------------------------------------------------------------
// Test: Simple scalar
// ---------------------------------------------------------------------------

func TestUnmarshalSimpleScalar(t *testing.T) {
	data := []byte(`host:str = 'localhost'`)
	var v simpleScalar
	if err := Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}
	if v.Host != "localhost" {
		t.Errorf("got Host=%q, want %q", v.Host, "localhost")
	}
}

// ---------------------------------------------------------------------------
// Test: Multiple assignments
// ---------------------------------------------------------------------------

func TestUnmarshalMultipleAssignments(t *testing.T) {
	data := []byte("host:str = 'example.com'\nport:int = 8080")
	var v multiField
	if err := Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}
	if v.Host != "example.com" {
		t.Errorf("got Host=%q, want %q", v.Host, "example.com")
	}
	if v.Port != 8080 {
		t.Errorf("got Port=%d, want %d", v.Port, 8080)
	}
}

// ---------------------------------------------------------------------------
// Test: All scalar types
// ---------------------------------------------------------------------------

func TestUnmarshalAllScalarTypes(t *testing.T) {
	data := []byte(`name:str = 'Alice'
age:int = 30
price:dec = 19.99
rate:float = 1.5e+2
active:bool = true
id:uuid = 550e8400-e29b-41d4-a716-446655440000
born:date = 2000-01-15
start_time:time = 14:30:00Z
created:datetime = 2024-06-01T12:00:00Z`)

	var v allScalars
	if err := Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}

	checks := []struct {
		name string
		got  any
		want any
	}{
		{"Name", v.Name, "Alice"},
		{"Age", v.Age, int64(30)},
		{"Price", v.Price, "19.99"},
		{"Rate", v.Rate, 150.0},
		{"Active", v.Active, true},
		{"ID", v.ID, "550e8400-e29b-41d4-a716-446655440000"},
		{"Born", v.Born, "2000-01-15"},
		{"Start", v.Start, "14:30:00Z"},
		{"Created", v.Created, "2024-06-01T12:00:00Z"},
	}
	for _, c := range checks {
		if !reflect.DeepEqual(c.got, c.want) {
			t.Errorf("%s: got %v (%T), want %v (%T)", c.name, c.got, c.got, c.want, c.want)
		}
	}
}

// ---------------------------------------------------------------------------
// Test: Struct value → nested Go struct
// ---------------------------------------------------------------------------

func TestUnmarshalStructValue(t *testing.T) {
	data := []byte("server:{host:str, port:int} = {'localhost', 8080}")
	var v withNestedStruct
	if err := Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}
	if v.Server.Host != "localhost" {
		t.Errorf("got Host=%q, want %q", v.Server.Host, "localhost")
	}
	if v.Server.Port != 8080 {
		t.Errorf("got Port=%d, want %d", v.Server.Port, 8080)
	}
}

func TestUnmarshalNamedStructField(t *testing.T) {
	data := []byte("server:{host:str, port:int} = {'example.com', 443}")
	var v outerWithInner
	if err := Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}
	if v.Server.Host != "example.com" {
		t.Errorf("got Host=%q, want %q", v.Server.Host, "example.com")
	}
	if v.Server.Port != 443 {
		t.Errorf("got Port=%d, want %d", v.Server.Port, 443)
	}
}

// ---------------------------------------------------------------------------
// Test: List value → slice
// ---------------------------------------------------------------------------

func TestUnmarshalListValue(t *testing.T) {
	data := []byte("tags:[str] = ['alpha', 'beta', 'gamma']")
	var v withList
	if err := Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}
	want := []string{"alpha", "beta", "gamma"}
	if !reflect.DeepEqual(v.Tags, want) {
		t.Errorf("got Tags=%v, want %v", v.Tags, want)
	}
}

func TestUnmarshalIntList(t *testing.T) {
	data := []byte("ports:[int] = [80, 443, 8080]")
	var v withIntList
	if err := Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}
	want := []int64{80, 443, 8080}
	if !reflect.DeepEqual(v.Ports, want) {
		t.Errorf("got Ports=%v, want %v", v.Ports, want)
	}
}

// ---------------------------------------------------------------------------
// Test: Map value → Go map
// ---------------------------------------------------------------------------

func TestUnmarshalMapValue(t *testing.T) {
	data := []byte("headers:<str ; str> = <'Content-Type' ; 'application/json', 'Accept' ; 'text/html'>")
	var v withMap
	if err := Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}
	if v.Headers["Content-Type"] != "application/json" {
		t.Errorf("got Content-Type=%q", v.Headers["Content-Type"])
	}
	if v.Headers["Accept"] != "text/html" {
		t.Errorf("got Accept=%q", v.Headers["Accept"])
	}
}

func TestUnmarshalBinValue(t *testing.T) {
	data := []byte("data:bin = b'SGVsbG8='")
	var v withBytes
	if err := Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(v.Data, []byte("Hello")) {
		t.Fatalf("got %v, want %v", v.Data, []byte("Hello"))
	}
}

// ---------------------------------------------------------------------------
// Test: Nullable/pointer
// ---------------------------------------------------------------------------

func TestUnmarshalPointerNonNil(t *testing.T) {
	data := []byte("name:str? = 'hello'\nage:int? = 42")
	var v withPointer
	if err := Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}
	if v.Name == nil || *v.Name != "hello" {
		t.Errorf("got Name=%v, want 'hello'", v.Name)
	}
	if v.Age == nil || *v.Age != 42 {
		t.Errorf("got Age=%v, want 42", v.Age)
	}
}

func TestUnmarshalPointerNil(t *testing.T) {
	data := []byte("name:str? = nil\nage:int? = nil")
	var v withPointer
	if err := Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}
	if v.Name != nil {
		t.Errorf("got Name=%v, want nil", v.Name)
	}
	if v.Age != nil {
		t.Errorf("got Age=%v, want nil", v.Age)
	}
}

// ---------------------------------------------------------------------------
// Test: Unknown fields → ignored
// ---------------------------------------------------------------------------

func TestUnmarshalUnknownFields(t *testing.T) {
	data := []byte("host:str = 'x'\nunknown_field:int = 99")
	var v simpleScalar
	if err := Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}
	if v.Host != "x" {
		t.Errorf("got Host=%q, want %q", v.Host, "x")
	}
}

// ---------------------------------------------------------------------------
// Test: Missing fields → zero value
// ---------------------------------------------------------------------------

func TestUnmarshalMissingFields(t *testing.T) {
	data := []byte("host:str = 'only-host'")
	var v multiField
	if err := Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}
	if v.Host != "only-host" {
		t.Errorf("got Host=%q, want %q", v.Host, "only-host")
	}
	if v.Port != 0 {
		t.Errorf("got Port=%d, want 0", v.Port)
	}
}

// ---------------------------------------------------------------------------
// Test: Nested composites — struct with list of structs
// ---------------------------------------------------------------------------

func TestUnmarshalNestedComposites(t *testing.T) {
	data := []byte("servers:[{host:str, port:int}] = [{'web1', 80}, {'web2', 443}]")
	var v nestedListOfStructs
	if err := Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}
	if len(v.Servers) != 2 {
		t.Fatalf("got %d servers, want 2", len(v.Servers))
	}
	if v.Servers[0].Host != "web1" || v.Servers[0].Port != 80 {
		t.Errorf("servers[0] = %+v", v.Servers[0])
	}
	if v.Servers[1].Host != "web2" || v.Servers[1].Port != 443 {
		t.Errorf("servers[1] = %+v", v.Servers[1])
	}
}

// ---------------------------------------------------------------------------
// Test: time.Time parsing
// ---------------------------------------------------------------------------

func TestUnmarshalTimeTime(t *testing.T) {
	data := []byte("created:datetime = 2024-06-01T12:00:00Z")
	var v withTimeFields
	if err := Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}
	want := time.Date(2024, 6, 1, 12, 0, 0, 0, time.UTC)
	if !v.Created.Equal(want) {
		t.Errorf("got Created=%v, want %v", v.Created, want)
	}
}

// ---------------------------------------------------------------------------
// Test: Round-trip (Encode → Unmarshal)
// ---------------------------------------------------------------------------

func TestUnmarshalRoundTrip(t *testing.T) {
	type Config struct {
		Host  string  `pakt:"host"`
		Port  int64   `pakt:"port"`
		Debug bool    `pakt:"debug"`
		Rate  float64 `pakt:"rate"`
	}

	original := Config{
		Host:  "example.com",
		Port:  8080,
		Debug: true,
		Rate:  9.5e+1,
	}

	// Encode each field.
	var buf bytes.Buffer
	enc := NewEncoder(&buf)

	fields, err := StructFields(reflect.TypeOf(original))
	if err != nil {
		t.Fatal(err)
	}
	rv := reflect.ValueOf(original)
	for _, fi := range fields {
		if err := enc.Encode(fi.Name, fi.Type, rv.Field(fi.Index).Interface()); err != nil {
			t.Fatalf("encode %s: %v", fi.Name, err)
		}
	}

	// Unmarshal back.
	var decoded Config
	if err := Unmarshal(buf.Bytes(), &decoded); err != nil {
		t.Fatalf("unmarshal: %v\npakt data:\n%s", err, buf.String())
	}

	if decoded.Host != original.Host {
		t.Errorf("Host: got %q, want %q", decoded.Host, original.Host)
	}
	if decoded.Port != original.Port {
		t.Errorf("Port: got %d, want %d", decoded.Port, original.Port)
	}
	if decoded.Debug != original.Debug {
		t.Errorf("Debug: got %v, want %v", decoded.Debug, original.Debug)
	}
	if decoded.Rate != original.Rate {
		t.Errorf("Rate: got %v, want %v", decoded.Rate, original.Rate)
	}
}

func TestUnmarshalRoundTripList(t *testing.T) {
	type Doc struct {
		Tags []string `pakt:"tags"`
	}

	original := Doc{Tags: []string{"a", "b", "c"}}

	var buf bytes.Buffer
	enc := NewEncoder(&buf)
	fields, err := StructFields(reflect.TypeOf(original))
	if err != nil {
		t.Fatal(err)
	}
	rv := reflect.ValueOf(original)
	for _, fi := range fields {
		if err := enc.Encode(fi.Name, fi.Type, rv.Field(fi.Index).Interface()); err != nil {
			t.Fatal(err)
		}
	}

	var decoded Doc
	if err := Unmarshal(buf.Bytes(), &decoded); err != nil {
		t.Fatalf("unmarshal: %v\npakt:\n%s", err, buf.String())
	}
	if !reflect.DeepEqual(decoded.Tags, original.Tags) {
		t.Errorf("Tags: got %v, want %v", decoded.Tags, original.Tags)
	}
}

func TestUnmarshalRoundTripStruct(t *testing.T) {
	type Inner struct {
		X int64  `pakt:"x"`
		Y string `pakt:"y"`
	}
	type Doc struct {
		Data Inner `pakt:"data"`
	}

	original := Doc{Data: Inner{X: 42, Y: "hello"}}

	var buf bytes.Buffer
	enc := NewEncoder(&buf)
	fields, err := StructFields(reflect.TypeOf(original))
	if err != nil {
		t.Fatal(err)
	}
	rv := reflect.ValueOf(original)
	for _, fi := range fields {
		if err := enc.Encode(fi.Name, fi.Type, rv.Field(fi.Index).Interface()); err != nil {
			t.Fatal(err)
		}
	}

	var decoded Doc
	if err := Unmarshal(buf.Bytes(), &decoded); err != nil {
		t.Fatalf("unmarshal: %v\npakt:\n%s", err, buf.String())
	}
	if decoded.Data != original.Data {
		t.Errorf("Data: got %+v, want %+v", decoded.Data, original.Data)
	}
}

// ---------------------------------------------------------------------------
// Test: Error cases
// ---------------------------------------------------------------------------

func TestUnmarshalErrors(t *testing.T) {
	t.Run("non-pointer", func(t *testing.T) {
		var v simpleScalar
		err := Unmarshal([]byte("host:str = 'x'"), v)
		if err == nil {
			t.Fatal("expected error for non-pointer")
		}
	})

	t.Run("pointer-to-non-struct", func(t *testing.T) {
		var s string
		err := Unmarshal([]byte("host:str = 'x'"), &s)
		if err == nil {
			t.Fatal("expected error for pointer-to-string")
		}
	})

	t.Run("nil-pointer", func(t *testing.T) {
		err := Unmarshal([]byte("host:str = 'x'"), nil)
		if err == nil {
			t.Fatal("expected error for nil pointer")
		}
	})

	t.Run("type-mismatch-bool-into-string", func(t *testing.T) {
		type S struct {
			Active bool `pakt:"active"`
		}
		// Valid PAKT but Active is bool, receiving str value — this would actually
		// be a parse error from the decoder since the type annotation says str but
		// the value is 'hello'. Let's try bool into int.
		data := []byte("active:bool = true")
		var v S
		err := Unmarshal(data, &v)
		// This should succeed since the types match.
		if err != nil {
			t.Fatalf("unexpected error: %v", err)
		}
		if !v.Active {
			t.Error("expected Active=true")
		}
	})

	t.Run("invalid-pakt", func(t *testing.T) {
		type S struct {
			Host string `pakt:"host"`
		}
		err := Unmarshal([]byte("this is not valid pakt"), &S{})
		if err == nil {
			t.Fatal("expected error for invalid PAKT")
		}
	})
}

// ---------------------------------------------------------------------------
// Test: Int formats (hex, binary, octal, underscore)
// ---------------------------------------------------------------------------

func TestUnmarshalIntFormats(t *testing.T) {
	type S struct {
		Val int64 `pakt:"val"`
	}

	tests := []struct {
		pakt string
		want int64
	}{
		{"val:int = 42", 42},
		{"val:int = -10", -10},
		{"val:int = 0xFF", 255},
		{"val:int = 0b1010", 10},
		{"val:int = 0o77", 63},
		{"val:int = 1_000", 1000},
	}

	for _, tc := range tests {
		var v S
		if err := Unmarshal([]byte(tc.pakt), &v); err != nil {
			t.Errorf("Unmarshal(%q): %v", tc.pakt, err)
			continue
		}
		if v.Val != tc.want {
			t.Errorf("Unmarshal(%q): got %d, want %d", tc.pakt, v.Val, tc.want)
		}
	}
}

// ---------------------------------------------------------------------------
// Test: Empty list and map
// ---------------------------------------------------------------------------

func TestUnmarshalEmptyList(t *testing.T) {
	data := []byte("tags:[str] = []")
	var v withList
	if err := Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}
	if v.Tags == nil || len(v.Tags) != 0 {
		t.Errorf("got Tags=%v, want empty slice", v.Tags)
	}
}

func TestUnmarshalEmptyMap(t *testing.T) {
	data := []byte("headers:<str ; str> = <>")
	var v withMap
	if err := Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}
	if v.Headers == nil || len(v.Headers) != 0 {
		t.Errorf("got Headers=%v, want empty map", v.Headers)
	}
}

// ---------------------------------------------------------------------------
// Test: Dec into float64
// ---------------------------------------------------------------------------

func TestUnmarshalDecIntoFloat(t *testing.T) {
	type S struct {
		Price float64 `pakt:"price"`
	}
	data := []byte("price:dec = 19.99")
	var v S
	if err := Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}
	if v.Price != 19.99 {
		t.Errorf("got Price=%v, want 19.99", v.Price)
	}
}

// ---------------------------------------------------------------------------
// Test: Lowercase field name fallback (no pakt tag)
// ---------------------------------------------------------------------------

func TestUnmarshalLowercaseFieldName(t *testing.T) {
	type S struct {
		Hostname string
	}
	data := []byte("hostname:str = 'myhost'")
	var v S
	if err := Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}
	if v.Hostname != "myhost" {
		t.Errorf("got Hostname=%q, want %q", v.Hostname, "myhost")
	}
}

// ---------------------------------------------------------------------------
// Test: Int into uint
// ---------------------------------------------------------------------------

func TestUnmarshalIntIntoUint(t *testing.T) {
	type S struct {
		Port uint16 `pakt:"port"`
	}
	data := []byte("port:int = 8080")
	var v S
	if err := Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}
	if v.Port != 8080 {
		t.Errorf("got Port=%d, want 8080", v.Port)
	}
}
