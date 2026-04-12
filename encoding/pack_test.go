package encoding

import (
	"reflect"
	"testing"
)

type withList struct {
	Tags []string `pakt:"tags"`
}

type innerStruct struct {
	Host string `pakt:"host"`
	Port int64  `pakt:"port"`
}

type nestedListOfStructs struct {
	Servers []innerStruct `pakt:"servers"`
}

type withMap struct {
	Headers map[string]string `pakt:"headers"`
}

func TestDecodeListPack(t *testing.T) {
	events := decodeAll(t, "ports:[int] << 80, 443, 8080")
	if len(events) != 5 {
		t.Fatalf("expected 5 events, got %d: %v", len(events), events)
	}
	if events[0].Kind != EventListPackStart || events[0].Name != "ports" {
		t.Fatalf("event[0] = %v", events[0])
	}
	if events[1].Name != "[0]" || events[1].Value != "80" {
		t.Fatalf("event[1] = %v", events[1])
	}
	if events[2].Name != "[1]" || events[2].Value != "443" {
		t.Fatalf("event[2] = %v", events[2])
	}
	if events[3].Name != "[2]" || events[3].Value != "8080" {
		t.Fatalf("event[3] = %v", events[3])
	}
	if events[4].Kind != EventListPackEnd || events[4].Name != "ports" {
		t.Fatalf("event[4] = %v", events[4])
	}
}

func TestDecodeListPackStopsAtNextStatement(t *testing.T) {
	input := "states:[|dev, prod|] << |dev\nnext:int = 1"
	events := decodeAll(t, input)
	if len(events) != 6 {
		t.Fatalf("expected 6 events, got %d: %v", len(events), events)
	}
	if events[0].Kind != EventListPackStart || events[0].Name != "states" {
		t.Fatalf("event[0] = %v", events[0])
	}
	if events[1].Kind != EventScalarValue || events[1].Value != "dev" {
		t.Fatalf("event[1] = %v", events[1])
	}
	if events[2].Kind != EventListPackEnd || events[2].Name != "states" {
		t.Fatalf("event[2] = %v", events[2])
	}
	if events[3].Kind != EventAssignStart || events[3].Name != "next" {
		t.Fatalf("event[3] = %v", events[3])
	}
}

func TestDecodeMapPack(t *testing.T) {
	input := "headers:<str ; int> << 'a' ; 1, 'b' ; 2"
	events := decodeAll(t, input)
	if len(events) != 6 {
		t.Fatalf("expected 6 events, got %d: %v", len(events), events)
	}
	if events[0].Kind != EventMapPackStart || events[0].Name != "headers" {
		t.Fatalf("event[0] = %v", events[0])
	}
	if events[1].Kind != EventScalarValue || events[1].Value != "a" {
		t.Fatalf("event[1] = %v", events[1])
	}
	if events[2].Kind != EventScalarValue || events[2].Value != "1" {
		t.Fatalf("event[2] = %v", events[2])
	}
	if events[5].Kind != EventMapPackEnd || events[5].Name != "headers" {
		t.Fatalf("event[5] = %v", events[5])
	}
}

func TestDecodeMapPackDuplicateKeysPreserved(t *testing.T) {
	input := "headers:<str ; int> << 'a' ; 1, 'a' ; 2"
	events := decodeAll(t, input)
	if len(events) != 6 {
		t.Fatalf("expected 6 events, got %d: %v", len(events), events)
	}
	if events[1].Value != "a" || events[2].Value != "1" || events[3].Value != "a" || events[4].Value != "2" {
		t.Fatalf("unexpected duplicate-key event sequence: %v", events)
	}
}

func TestUnmarshalListPack(t *testing.T) {
	data := []byte("tags:[str] << 'alpha', 'beta', 'gamma'")
	v, err := UnmarshalNew[withList](data)
	if err != nil {
		t.Fatal(err)
	}
	want := []string{"alpha", "beta", "gamma"}
	if !reflect.DeepEqual(v.Tags, want) {
		t.Fatalf("got %v, want %v", v.Tags, want)
	}
}

func TestUnmarshalStructListPack(t *testing.T) {
	data := []byte("servers:[{host:str, port:int}] << { 'a', 80 }, { 'b', 443 }")
	v, err := UnmarshalNew[nestedListOfStructs](data)
	if err != nil {
		t.Fatal(err)
	}
	want := []innerStruct{
		{Host: "a", Port: 80},
		{Host: "b", Port: 443},
	}
	if !reflect.DeepEqual(v.Servers, want) {
		t.Fatalf("got %#v, want %#v", v.Servers, want)
	}
}

func TestUnmarshalMapPackLastWins(t *testing.T) {
	data := []byte("headers:<str ; str> << 'Accept' ; 'json', 'Accept' ; 'text/html'")
	v, err := UnmarshalNew[withMap](data)
	if err != nil {
		t.Fatal(err)
	}
	if got := v.Headers["Accept"]; got != "text/html" {
		t.Fatalf("got %q, want %q", got, "text/html")
	}
}

func TestUnmarshalDelimitedMapDuplicateKeysLastWins(t *testing.T) {
	data := []byte("headers:<str ; str> = <'Accept' ; 'json', 'Accept' ; 'text/html'>")
	v, err := UnmarshalNew[withMap](data)
	if err != nil {
		t.Fatal(err)
	}
	if got := v.Headers["Accept"]; got != "text/html" {
		t.Fatalf("got %q, want %q", got, "text/html")
	}
}
