package encoding

import (
	"strings"
	"testing"
	"time"
)

func TestReadValueString(t *testing.T) {
	sr := NewUnitReader(strings.NewReader("name:str = 'hello'\n"))
	defer sr.Close()

	for stmt := range sr.Properties() {
		if stmt.Name != "name" {
			t.Fatalf("expected 'name', got %q", stmt.Name)
		}
		val, err := ReadValue[string](sr)
		if err != nil {
			t.Fatal(err)
		}
		if val != "hello" {
			t.Errorf("expected 'hello', got %q", val)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

func TestReadValueInt(t *testing.T) {
	sr := NewUnitReader(strings.NewReader("port:int = 8080\n"))
	defer sr.Close()

	for stmt := range sr.Properties() {
		if stmt.Name != "port" {
			t.Fatalf("expected 'port', got %q", stmt.Name)
		}
		val, err := ReadValue[int64](sr)
		if err != nil {
			t.Fatal(err)
		}
		if val != 8080 {
			t.Errorf("expected 8080, got %d", val)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

func TestReadValueBool(t *testing.T) {
	sr := NewUnitReader(strings.NewReader("debug:bool = true\n"))
	defer sr.Close()

	for stmt := range sr.Properties() {
		_ = stmt
		val, err := ReadValue[bool](sr)
		if err != nil {
			t.Fatal(err)
		}
		if val != true {
			t.Errorf("expected true, got %v", val)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

func TestReadValueFloat(t *testing.T) {
	sr := NewUnitReader(strings.NewReader("rate:float = 3.14e0\n"))
	defer sr.Close()

	for stmt := range sr.Properties() {
		_ = stmt
		val, err := ReadValue[float64](sr)
		if err != nil {
			t.Fatal(err)
		}
		if val != 3.14 {
			t.Errorf("expected 3.14, got %f", val)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

func TestReadValueStruct(t *testing.T) {
	type Server struct {
		Host string `pakt:"host"`
		Port int64  `pakt:"port"`
	}

	sr := NewUnitReader(strings.NewReader(
		"server:{host:str, port:int} = {'localhost', 8080}\n"))
	defer sr.Close()

	for stmt := range sr.Properties() {
		if stmt.Name != "server" {
			t.Fatalf("expected 'server', got %q", stmt.Name)
		}
		val, err := ReadValue[Server](sr)
		if err != nil {
			t.Fatal(err)
		}
		if val.Host != "localhost" || val.Port != 8080 {
			t.Errorf("expected {localhost, 8080}, got %+v", val)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

func TestReadValueList(t *testing.T) {
	sr := NewUnitReader(strings.NewReader(
		"tags:[str] = ['alpha', 'beta', 'gamma']\n"))
	defer sr.Close()

	for stmt := range sr.Properties() {
		_ = stmt
		val, err := ReadValue[[]string](sr)
		if err != nil {
			t.Fatal(err)
		}
		if len(val) != 3 || val[0] != "alpha" || val[1] != "beta" || val[2] != "gamma" {
			t.Errorf("expected [alpha, beta, gamma], got %v", val)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

func TestReadValueMap(t *testing.T) {
	sr := NewUnitReader(strings.NewReader(
		"headers:<str ; str> = <'Content-Type' ; 'text/html', 'Accept' ; '*/*'>\n"))
	defer sr.Close()

	for stmt := range sr.Properties() {
		_ = stmt
		val, err := ReadValue[map[string]string](sr)
		if err != nil {
			t.Fatal(err)
		}
		if len(val) != 2 {
			t.Errorf("expected 2 entries, got %d", len(val))
		}
		if val["Content-Type"] != "text/html" {
			t.Errorf("expected 'text/html', got %q", val["Content-Type"])
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

func TestReadValueMultipleStatements(t *testing.T) {
	input := "name:str = 'svc'\nport:int = 9090\ndebug:bool = false\n"
	sr := NewUnitReader(strings.NewReader(input))
	defer sr.Close()

	var name string
	var port int64
	var debug bool

	for stmt := range sr.Properties() {
		var err error
		switch stmt.Name {
		case "name":
			name, err = ReadValue[string](sr)
		case "port":
			port, err = ReadValue[int64](sr)
		case "debug":
			debug, err = ReadValue[bool](sr)
		}
		if err != nil {
			t.Fatal(err)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}

	if name != "svc" || port != 9090 || debug != false {
		t.Errorf("got name=%q port=%d debug=%v", name, port, debug)
	}
}

func TestReadValueTimestamp(t *testing.T) {
	sr := NewUnitReader(strings.NewReader(
		"created:ts = 2026-06-01T14:30:00Z\n"))
	defer sr.Close()

	for stmt := range sr.Properties() {
		_ = stmt
		val, err := ReadValue[time.Time](sr)
		if err != nil {
			t.Fatal(err)
		}
		if val.Year() != 2026 || val.Month() != 6 || val.Day() != 1 {
			t.Errorf("unexpected time: %v", val)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

func TestReadValueNullable(t *testing.T) {
	sr := NewUnitReader(strings.NewReader(
		"label:str? = nil\n"))
	defer sr.Close()

	for stmt := range sr.Properties() {
		_ = stmt
		val, err := ReadValue[*string](sr)
		if err != nil {
			t.Fatal(err)
		}
		if val != nil {
			t.Errorf("expected nil, got %q", *val)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

func TestReadValueInto(t *testing.T) {
	sr := NewUnitReader(strings.NewReader("name:str = 'hello'\n"))
	defer sr.Close()

	for range sr.Properties() {
		var val string
		err := ReadValueInto(sr, &val)
		if err != nil {
			t.Fatal(err)
		}
		if val != "hello" {
			t.Errorf("expected 'hello', got %q", val)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

func TestReadValueIntoReuse(t *testing.T) {
	input := "a:int = 1\nb:int = 2\nc:int = 3\n"
	sr := NewUnitReader(strings.NewReader(input))
	defer sr.Close()

	var val int64
	var sum int64
	for range sr.Properties() {
		err := ReadValueInto(sr, &val)
		if err != nil {
			t.Fatal(err)
		}
		sum += val
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
	if sum != 6 {
		t.Errorf("expected sum=6, got %d", sum)
	}
}

func TestReadValueTuple(t *testing.T) {
	input := "point:(int, int, int) = (10, 20, 30)\n"
	sr := NewUnitReader(strings.NewReader(input))
	defer sr.Close()

	for range sr.Properties() {
		val, err := ReadValue[[]int64](sr)
		if err != nil {
			t.Fatal(err)
		}
		if len(val) != 3 {
			t.Fatalf("expected 3 elements, got %d", len(val))
		}
		if val[0] != 10 || val[1] != 20 || val[2] != 30 {
			t.Errorf("expected [10,20,30], got %v", val)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

func TestReadValueStructIntoMap(t *testing.T) {
	input := "cfg:{host:str, mode:str} = {'localhost', 'debug'}\n"
	sr := NewUnitReader(strings.NewReader(input))
	defer sr.Close()

	for range sr.Properties() {
		val, err := ReadValue[map[string]string](sr)
		if err != nil {
			t.Fatal(err)
		}
		if val["host"] != "localhost" {
			t.Errorf("expected host=localhost, got %q", val["host"])
		}
		if val["mode"] != "debug" {
			t.Errorf("expected mode=debug, got %q", val["mode"])
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

func TestReadValueBin(t *testing.T) {
	input := "data:bin = x'48454c4c4f'\n"
	sr := NewUnitReader(strings.NewReader(input))
	defer sr.Close()

	for range sr.Properties() {
		val, err := ReadValue[[]byte](sr)
		if err != nil {
			t.Fatal(err)
		}
		if string(val) != "HELLO" {
			t.Errorf("expected 'HELLO', got %q", val)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

func TestReadValueDec(t *testing.T) {
	input := "price:dec = 19.99\n"
	sr := NewUnitReader(strings.NewReader(input))
	defer sr.Close()

	for range sr.Properties() {
		val, err := ReadValue[float64](sr)
		if err != nil {
			t.Fatal(err)
		}
		if val != 19.99 {
			t.Errorf("expected 19.99, got %f", val)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

func TestReadValueDecIntoString(t *testing.T) {
	input := "price:dec = 99.999\n"
	sr := NewUnitReader(strings.NewReader(input))
	defer sr.Close()

	for range sr.Properties() {
		val, err := ReadValue[string](sr)
		if err != nil {
			t.Fatal(err)
		}
		if val != "99.999" {
			t.Errorf("expected '99.999', got %q", val)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

func TestReadValueSkipUnknownField(t *testing.T) {
	type Small struct {
		Name string `pakt:"name"`
	}
	input := "data:{name:str, extra:int, bonus:{a:str}} = {'hello', 42, {'nested'}}\n"
	sr := NewUnitReader(strings.NewReader(input))
	defer sr.Close()

	for range sr.Properties() {
		val, err := ReadValue[Small](sr)
		if err != nil {
			t.Fatal(err)
		}
		if val.Name != "hello" {
			t.Errorf("expected 'hello', got %q", val.Name)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

func TestReadValueNestedStruct(t *testing.T) {
	type Inner struct {
		X int64 `pakt:"x"`
		Y int64 `pakt:"y"`
	}
	type Outer struct {
		Name  string `pakt:"name"`
		Point Inner  `pakt:"point"`
	}

	sr := NewUnitReader(strings.NewReader(
		"data:{name:str, point:{x:int, y:int}} = {'origin', {0, 0}}\n"))
	defer sr.Close()

	for stmt := range sr.Properties() {
		_ = stmt
		val, err := ReadValue[Outer](sr)
		if err != nil {
			t.Fatal(err)
		}
		if val.Name != "origin" || val.Point.X != 0 || val.Point.Y != 0 {
			t.Errorf("unexpected: %+v", val)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}
