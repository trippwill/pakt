package encoding

import (
	"bytes"
	"strings"
	"testing"
)

// FuzzDecode exercises the full decoder with arbitrary input.
// Catches panics, infinite loops, and OOM on malformed PAKT.
func FuzzDecode(f *testing.F) {
	// Seed corpus from valid PAKT patterns
	f.Add([]byte("name:str = 'hello'\n"))
	f.Add([]byte("count:int = 42\n"))
	f.Add([]byte("rate:float = 3.14e0\n"))
	f.Add([]byte("ok:bool = true\n"))
	f.Add([]byte("id:uuid = 550e8400-e29b-41d4-a716-446655440000\n"))
	f.Add([]byte("d:date = 2026-06-01\n"))
	f.Add([]byte("t:ts = 2026-06-01T14:30:00Z\n"))
	f.Add([]byte("b:bin = x'48656C6C6F'\n"))
	f.Add([]byte("s:{x:str, y:int} = {'a', 1}\n"))
	f.Add([]byte("t:(int, str) = (1, 'x')\n"))
	f.Add([]byte("l:[int] = [1, 2, 3]\n"))
	f.Add([]byte("m:<str ; int> = <'a' ; 1>\n"))
	f.Add([]byte("p:[int] <<\n1\n2\n3\n"))
	f.Add([]byte("n:str? = nil\n"))
	f.Add([]byte("a:|x, y, z| = |x\n"))
	f.Add([]byte("# comment\nname:str = 'hello'\n"))
	f.Add([]byte(""))
	f.Add([]byte("\x00"))
	f.Add([]byte("name:str = 'hello'\x00"))

	f.Fuzz(func(t *testing.T, data []byte) {
		dec := NewDecoder(bytes.NewReader(data))
		defer dec.Close()
		for i := 0; i < 10000; i++ {
			_, err := dec.Decode()
			if err != nil {
				return
			}
		}
	})
}

// FuzzUnmarshalNew exercises the full deserialization pipeline.
// Catches reflection panics, type confusion, and event stream corruption.
func FuzzUnmarshalNew(f *testing.F) {
	type Target struct {
		Name   string  `pakt:"name"`
		Count  int64   `pakt:"count"`
		Rate   float64 `pakt:"rate"`
		Active bool    `pakt:"active"`
		Label  *string `pakt:"label"`
	}

	f.Add([]byte("name:str = 'test'\ncount:int = 1\nrate:float = 1e0\nactive:bool = true\n"))
	f.Add([]byte("name:str = 'x'\n"))
	f.Add([]byte("label:str? = nil\n"))
	f.Add([]byte(""))
	f.Add([]byte("unknown:int = 42\n"))

	f.Fuzz(func(t *testing.T, data []byte) {
		var target Target
		_ = UnmarshalNewInto(data, &target)
	})
}

// FuzzReadString exercises string parsing with escape processing.
// Catches panics on malformed escapes, unterminated strings, null bytes.
func FuzzReadString(f *testing.F) {
	f.Add("'hello'")
	f.Add("'hello\\nworld'")
	f.Add("'\\u0041'")
	f.Add("'''\\nmulti\\nline\\n'''")
	f.Add("r'raw string'")
	f.Add("r'''\\nraw multi\\n'''")
	f.Add("'escape \\' inside'")
	f.Add("'")
	f.Add("''")
	f.Add("'\\")
	f.Add("'\\u'")
	f.Add("'\\u00'")

	f.Fuzz(func(t *testing.T, input string) {
		r := newReader(strings.NewReader(input))
		defer r.release()
		_, _ = r.readString()
	})
}

// FuzzParseIntLiteral exercises integer literal parsing.
// Catches overflow, invalid prefix combinations, underscore edge cases.
func FuzzParseIntLiteral(f *testing.F) {
	f.Add("0")
	f.Add("42")
	f.Add("-7")
	f.Add("+3")
	f.Add("1_000_000")
	f.Add("0xFF")
	f.Add("0b1010")
	f.Add("0o777")
	f.Add("9223372036854775807")  // MaxInt64
	f.Add("-9223372036854775808") // MinInt64
	f.Add("9223372036854775808")  // overflow
	f.Add("0x")
	f.Add("0b")
	f.Add("")
	f.Add("_")
	f.Add("0xGG")

	f.Fuzz(func(t *testing.T, input string) {
		_, _ = parseIntLiteral(input)
	})
}

// FuzzParseType exercises the recursive descent type annotation parser.
// Catches stack overflow on deeply nested types, malformed syntax.
func FuzzParseType(f *testing.F) {
	f.Add("str")
	f.Add("int")
	f.Add("str?")
	f.Add("{x:str, y:int}")
	f.Add("(int, str)")
	f.Add("[int]")
	f.Add("<str ; int>")
	f.Add("|a, b, c|")
	f.Add("{a:{b:{c:str}}}")
	f.Add("[[[[int]]]]")
	f.Add("")
	f.Add("???")
	f.Add("{")
	f.Add("{{{{{{{{{{{{{{{{{{{{")

	f.Fuzz(func(t *testing.T, input string) {
		r := newReader(strings.NewReader(input))
		defer r.release()
		_, _ = r.readType()
	})
}
