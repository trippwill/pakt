package encoding

import (
	"strings"
	"testing"
	"time"
)

func TestUnmarshalNewBasic(t *testing.T) {
	type Config struct {
		Host  string `pakt:"host"`
		Port  int64  `pakt:"port"`
		Debug bool   `pakt:"debug"`
	}

	data := []byte("host:str = 'localhost'\nport:int = 8080\ndebug:bool = true\n")
	cfg, err := UnmarshalNew[Config](data)
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Host != "localhost" || cfg.Port != 8080 || cfg.Debug != true {
		t.Errorf("unexpected: %+v", cfg)
	}
}

func TestUnmarshalNewNested(t *testing.T) {
	type Server struct {
		Host string `pakt:"host"`
		Port int64  `pakt:"port"`
	}
	type Config struct {
		Name   string `pakt:"name"`
		Server Server `pakt:"server"`
	}

	data := []byte("name:str = 'myapp'\nserver:{host:str, port:int} = {'example.com', 443}\n")
	cfg, err := UnmarshalNew[Config](data)
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Name != "myapp" || cfg.Server.Host != "example.com" || cfg.Server.Port != 443 {
		t.Errorf("unexpected: %+v", cfg)
	}
}

func TestUnmarshalNewList(t *testing.T) {
	type Config struct {
		Tags []string `pakt:"tags"`
	}

	data := []byte("tags:[str] = ['alpha', 'beta']\n")
	cfg, err := UnmarshalNew[Config](data)
	if err != nil {
		t.Fatal(err)
	}
	if len(cfg.Tags) != 2 || cfg.Tags[0] != "alpha" || cfg.Tags[1] != "beta" {
		t.Errorf("unexpected: %+v", cfg)
	}
}

func TestUnmarshalNewMap(t *testing.T) {
	type Config struct {
		Headers map[string]string `pakt:"headers"`
	}

	data := []byte("headers:<str ; str> = <'X-Foo' ; 'bar'>\n")
	cfg, err := UnmarshalNew[Config](data)
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Headers["X-Foo"] != "bar" {
		t.Errorf("unexpected: %+v", cfg)
	}
}

func TestUnmarshalNewNullable(t *testing.T) {
	type Config struct {
		Label *string `pakt:"label"`
		Count *int64  `pakt:"count"`
	}

	data := []byte("label:str? = nil\ncount:int? = 42\n")
	cfg, err := UnmarshalNew[Config](data)
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Label != nil {
		t.Errorf("expected nil label, got %q", *cfg.Label)
	}
	if cfg.Count == nil || *cfg.Count != 42 {
		t.Errorf("expected count=42, got %v", cfg.Count)
	}
}

func TestUnmarshalNewTimestamp(t *testing.T) {
	type Config struct {
		Created time.Time `pakt:"created"`
	}

	data := []byte("created:ts = 2026-06-01T14:30:00Z\n")
	cfg, err := UnmarshalNew[Config](data)
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Created.Year() != 2026 || cfg.Created.Month() != 6 {
		t.Errorf("unexpected: %v", cfg.Created)
	}
}

func TestUnmarshalNewUnknownFields(t *testing.T) {
	type Config struct {
		Name string `pakt:"name"`
	}

	data := []byte("name:str = 'svc'\nextra:int = 42\n")

	// Default: skip unknown
	cfg, err := UnmarshalNew[Config](data)
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Name != "svc" {
		t.Errorf("unexpected: %+v", cfg)
	}

	// Strict: error on unknown
	_, err = UnmarshalNew[Config](data, UnknownFields(ErrorUnknown))
	if err == nil {
		t.Error("expected error for unknown field 'extra'")
	}
}

func TestUnmarshalNewPack(t *testing.T) {
	type Entry struct {
		Name string `pakt:"name"`
		Size int64  `pakt:"size"`
	}
	type Doc struct {
		Files []Entry `pakt:"files"`
	}

	data := []byte("files:[{name:str, size:int}] <<\n{'readme.md', 100}\n{'main.go', 500}\n")
	doc, err := UnmarshalNew[Doc](data)
	if err != nil {
		t.Fatal(err)
	}
	if len(doc.Files) != 2 {
		t.Fatalf("expected 2 files, got %d", len(doc.Files))
	}
	if doc.Files[0].Name != "readme.md" || doc.Files[0].Size != 100 {
		t.Errorf("file 0: %+v", doc.Files[0])
	}
}

func TestUnmarshalNewDuplicateError(t *testing.T) {
	type Config struct {
		Name string `pakt:"name"`
	}

	data := []byte("name:str = 'first'\nname:str = 'second'\n")
	_, err := UnmarshalNew[Config](data, Duplicates(ErrorDupes))
	if err == nil {
		t.Error("expected error for duplicate 'name'")
	}
}

func TestUnmarshalNewFrom(t *testing.T) {
	type Config struct {
		Host string `pakt:"host"`
		Port int64  `pakt:"port"`
	}

	r := strings.NewReader("host:str = 'example.com'\nport:int = 443\n")
	cfg, err := UnmarshalNewFrom[Config](r)
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Host != "example.com" || cfg.Port != 443 {
		t.Errorf("unexpected: %+v", cfg)
	}
}

func TestUnmarshalNewMissingFieldsError(t *testing.T) {
	type Config struct {
		Host string `pakt:"host"`
		Port int64  `pakt:"port"`
	}

	data := []byte("host:str = 'localhost'\n") // missing 'port'
	_, err := UnmarshalNew[Config](data, MissingFields(ErrorMissing))
	if err == nil {
		t.Error("expected error for missing field 'port'")
	}
}

func TestUnmarshalNewMissingFieldsZero(t *testing.T) {
	type Config struct {
		Host string `pakt:"host"`
		Port int64  `pakt:"port"`
	}

	data := []byte("host:str = 'localhost'\n") // missing 'port'
	cfg, err := UnmarshalNew[Config](data, MissingFields(ZeroMissing))
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Host != "localhost" {
		t.Errorf("unexpected host: %q", cfg.Host)
	}
	if cfg.Port != 0 {
		t.Errorf("expected port=0, got %d", cfg.Port)
	}
}

func TestUnmarshalNewDuplicateFirstWins(t *testing.T) {
	type Config struct {
		Name string `pakt:"name"`
	}

	data := []byte("name:str = 'first'\nname:str = 'second'\n")
	cfg, err := UnmarshalNew[Config](data, Duplicates(FirstWins))
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Name != "first" {
		t.Errorf("expected 'first' (FirstWins), got %q", cfg.Name)
	}
}

func TestUnmarshalNewDuplicateLastWins(t *testing.T) {
	type Config struct {
		Name string `pakt:"name"`
	}

	data := []byte("name:str = 'first'\nname:str = 'second'\n")
	cfg, err := UnmarshalNew[Config](data, Duplicates(LastWins))
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Name != "second" {
		t.Errorf("expected 'second' (LastWins), got %q", cfg.Name)
	}
}

func TestUnmarshalNewFromNonStruct(t *testing.T) {
	r := strings.NewReader("x:int = 1\n")
	_, err := UnmarshalNewFrom[int](r)
	if err == nil {
		t.Error("expected error for non-struct type")
	}
}
