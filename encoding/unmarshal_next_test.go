package encoding

import (
	"bytes"
	"io"
	"strings"
	"testing"
)

func TestUnmarshalNextBasicAssignment(t *testing.T) {
	doc := "name:str = 'hello'\ncount:int = 42\n"
	dec := NewDecoder(strings.NewReader(doc))
	defer dec.Close()

	type Doc struct {
		Name  string `pakt:"name"`
		Count int    `pakt:"count"`
	}

	var d Doc
	for dec.More() {
		if err := dec.UnmarshalNext(&d); err != nil {
			t.Fatal(err)
		}
	}

	if d.Name != "hello" {
		t.Errorf("Name = %q, want %q", d.Name, "hello")
	}
	if d.Count != 42 {
		t.Errorf("Count = %d, want %d", d.Count, 42)
	}
}

func TestUnmarshalNextPackList(t *testing.T) {
	doc := "items:[int] <<\n1\n2\n3\n"
	dec := NewDecoder(strings.NewReader(doc))
	defer dec.Close()

	type Doc struct {
		Items []int `pakt:"items"`
	}

	var d Doc
	for dec.More() {
		if err := dec.UnmarshalNext(&d); err != nil {
			t.Fatal(err)
		}
	}

	if len(d.Items) != 3 {
		t.Fatalf("Items length = %d, want 3", len(d.Items))
	}
	if d.Items[0] != 1 || d.Items[1] != 2 || d.Items[2] != 3 {
		t.Errorf("Items = %v, want [1, 2, 3]", d.Items)
	}
}

func TestUnmarshalNextPackStruct(t *testing.T) {
	doc := `root:str = '/data'
entries:[{name:str, size:int}] <<
    {'file1.txt', 100}
    {'file2.txt', 200}
`
	dec := NewDecoder(strings.NewReader(doc))
	defer dec.Close()

	type Entry struct {
		Name string `pakt:"name"`
		Size int    `pakt:"size"`
	}
	type Doc struct {
		Root    string  `pakt:"root"`
		Entries []Entry `pakt:"entries"`
	}

	var d Doc
	for dec.More() {
		if err := dec.UnmarshalNext(&d); err != nil {
			t.Fatal(err)
		}
	}

	if d.Root != "/data" {
		t.Errorf("Root = %q, want %q", d.Root, "/data")
	}
	if len(d.Entries) != 2 {
		t.Fatalf("Entries length = %d, want 2", len(d.Entries))
	}
	if d.Entries[0].Name != "file1.txt" || d.Entries[0].Size != 100 {
		t.Errorf("Entries[0] = %+v", d.Entries[0])
	}
}

func TestUnmarshalNextPackElementByElement(t *testing.T) {
	doc := "items:[int] <<\n10\n20\n30\n"
	dec := NewDecoder(strings.NewReader(doc))
	defer dec.Close()

	var items []int
	for dec.More() {
		var item int
		if err := dec.UnmarshalNext(&item); err != nil {
			t.Fatal(err)
		}
		items = append(items, int(item))
	}

	if len(items) != 3 {
		t.Fatalf("items length = %d, want 3", len(items))
	}
	if items[0] != 10 || items[1] != 20 || items[2] != 30 {
		t.Errorf("items = %v, want [10, 20, 30]", items)
	}
}

func TestUnmarshalNextPackElementThenAssign(t *testing.T) {
	doc := "nums:[int] <<\n1\n2\nname:str = 'after'\n"
	dec := NewDecoder(strings.NewReader(doc))
	defer dec.Close()

	var nums []int
	for dec.More() {
		var n int
		if err := dec.UnmarshalNext(&n); err != nil {
			if err == io.EOF {
				break
			}
			t.Fatal(err)
		}
		nums = append(nums, int(n))
		// After reading pack elements, check if more pack elements
		// or if the next statement has started.
		if !dec.More() {
			break
		}
	}

	if len(nums) != 2 {
		t.Fatalf("nums = %v, want [1, 2]", nums)
	}

	// Now read the assignment after the pack.
	type Doc struct {
		Name string `pakt:"name"`
	}
	var d Doc
	for dec.More() {
		if err := dec.UnmarshalNext(&d); err != nil {
			t.Fatal(err)
		}
	}
	if d.Name != "after" {
		t.Errorf("Name = %q, want %q", d.Name, "after")
	}
}

func TestUnmarshalNextSkipsUnknownFields(t *testing.T) {
	doc := "extra:str = 'skip me'\nname:str = 'keep'\n"
	dec := NewDecoder(strings.NewReader(doc))
	defer dec.Close()

	type Doc struct {
		Name string `pakt:"name"`
	}

	var d Doc
	for dec.More() {
		if err := dec.UnmarshalNext(&d); err != nil {
			t.Fatal(err)
		}
	}

	if d.Name != "keep" {
		t.Errorf("Name = %q, want %q", d.Name, "keep")
	}
}

func TestUnmarshalNextEOF(t *testing.T) {
	dec := NewDecoder(strings.NewReader(""))
	defer dec.Close()

	if dec.More() {
		t.Error("More() = true on empty input")
	}

	type Doc struct{}
	var d Doc
	err := dec.UnmarshalNext(&d)
	if err != io.EOF {
		t.Errorf("expected io.EOF, got %v", err)
	}
}

func TestUnmarshalNextNilPointerError(t *testing.T) {
	dec := NewDecoder(strings.NewReader("x:int = 1\n"))
	defer dec.Close()

	err := dec.UnmarshalNext(nil)
	if err == nil {
		t.Error("expected error for nil argument")
	}
}

func TestUnmarshalNextWithSpec(t *testing.T) {
	doc := "name:str = 'hello'\nextra:int = 99\ncount:int = 42\n"
	dec := NewDecoder(strings.NewReader(doc))
	defer dec.Close()

	spec := "name:str\ncount:int"
	if err := dec.SetSpec(strings.NewReader(spec)); err != nil {
		t.Fatal(err)
	}

	type Doc struct {
		Name  string `pakt:"name"`
		Count int    `pakt:"count"`
	}

	var d Doc
	for dec.More() {
		if err := dec.UnmarshalNext(&d); err != nil {
			t.Fatal(err)
		}
	}

	if d.Name != "hello" {
		t.Errorf("Name = %q, want %q", d.Name, "hello")
	}
	if d.Count != 42 {
		t.Errorf("Count = %d, want %d", d.Count, 42)
	}
}

func TestUnmarshalNextList(t *testing.T) {
	doc := "tags:[str] = ['alpha', 'beta', 'gamma']\n"
	dec := NewDecoder(strings.NewReader(doc))
	defer dec.Close()

	type Doc struct {
		Tags []string `pakt:"tags"`
	}

	var d Doc
	for dec.More() {
		if err := dec.UnmarshalNext(&d); err != nil {
			t.Fatal(err)
		}
	}

	if len(d.Tags) != 3 || d.Tags[0] != "alpha" {
		t.Errorf("Tags = %v", d.Tags)
	}
}

func TestUnmarshalNextMap(t *testing.T) {
	doc := "data:<str ; int> = <'a' ; 1, 'b' ; 2>\n"
	dec := NewDecoder(strings.NewReader(doc))
	defer dec.Close()

	type Doc struct {
		Data map[string]int `pakt:"data"`
	}

	var d Doc
	for dec.More() {
		if err := dec.UnmarshalNext(&d); err != nil {
			t.Fatal(err)
		}
	}

	if len(d.Data) != 2 || d.Data["a"] != 1 || d.Data["b"] != 2 {
		t.Errorf("Data = %v", d.Data)
	}
}

func TestUnmarshalNextBoolAndFloat(t *testing.T) {
	doc := "active:bool = true\nrate:float = 3.14e0\n"
	dec := NewDecoder(strings.NewReader(doc))
	defer dec.Close()

	type Doc struct {
		Active bool    `pakt:"active"`
		Rate   float64 `pakt:"rate"`
	}

	var d Doc
	for dec.More() {
		if err := dec.UnmarshalNext(&d); err != nil {
			t.Fatal(err)
		}
	}

	if !d.Active {
		t.Error("Active = false, want true")
	}
	if d.Rate < 3.13 || d.Rate > 3.15 {
		t.Errorf("Rate = %f, want ~3.14", d.Rate)
	}
}

func TestUnmarshalNextNullable(t *testing.T) {
	doc := "name:str? = nil\n"
	dec := NewDecoder(strings.NewReader(doc))
	defer dec.Close()

	type Doc struct {
		Name *string `pakt:"name"`
	}

	var d Doc
	for dec.More() {
		if err := dec.UnmarshalNext(&d); err != nil {
			t.Fatal(err)
		}
	}

	if d.Name != nil {
		t.Errorf("Name = %v, want nil", d.Name)
	}
}

func TestDecoderCloseIdempotent(t *testing.T) {
	dec := NewDecoder(bytes.NewReader(nil))
	dec.Close()
	dec.Close() // second close should not panic
}

func TestUnmarshalNextNestedStruct(t *testing.T) {
	doc := "config:{host:str, port:int} = {'localhost', 8080}\n"
	dec := NewDecoder(strings.NewReader(doc))
	defer dec.Close()

	type Config struct {
		Host string `pakt:"host"`
		Port int    `pakt:"port"`
	}
	type Doc struct {
		Config Config `pakt:"config"`
	}

	var d Doc
	for dec.More() {
		if err := dec.UnmarshalNext(&d); err != nil {
			t.Fatal(err)
		}
	}

	if d.Config.Host != "localhost" || d.Config.Port != 8080 {
		t.Errorf("Config = %+v", d.Config)
	}
}

func TestUnmarshalNextStructIntoMap(t *testing.T) {
	doc := "meta:{author:str, version:int} = {'alice', 3}\n"

	type Doc struct {
		Meta map[string]string `pakt:"meta"`
	}

	var d Doc
	if err := Unmarshal([]byte(doc), &d); err != nil {
		t.Fatal(err)
	}

	if d.Meta["author"] != "alice" {
		t.Errorf("Meta[author] = %q, want %q", d.Meta["author"], "alice")
	}
}

func TestUnmarshalNextTuple(t *testing.T) {
	doc := "pair:(str, int) = ('hello', 42)\n"

	type Doc struct {
		Pair []string `pakt:"pair"`
	}

	var d Doc
	if err := Unmarshal([]byte(doc), &d); err != nil {
		t.Fatal(err)
	}

	if len(d.Pair) != 2 || d.Pair[0] != "hello" {
		t.Errorf("Pair = %v", d.Pair)
	}
}

func TestUnmarshalNextTs(t *testing.T) {
	doc := "ts:ts = 2026-01-15T10:30:00Z\n"
	dec := NewDecoder(strings.NewReader(doc))
	defer dec.Close()

	type Doc struct {
		Ts string `pakt:"ts"`
	}

	var d Doc
	for dec.More() {
		if err := dec.UnmarshalNext(&d); err != nil {
			t.Fatal(err)
		}
	}

	if d.Ts != "2026-01-15T10:30:00Z" {
		t.Errorf("Ts = %q", d.Ts)
	}
}
