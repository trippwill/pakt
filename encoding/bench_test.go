package encoding

import (
	"bytes"
	"encoding/json"
	"fmt"
	"reflect"
	"strconv"
	"strings"
	"testing"
)

// ---------------------------------------------------------------------------
// Benchmark struct type (~10 fields, mixed scalars)
// ---------------------------------------------------------------------------

type benchSmallDoc struct {
	Name     string  `pakt:"name" json:"name"`
	Version  int     `pakt:"version" json:"version"`
	Debug    bool    `pakt:"debug" json:"debug"`
	Rate     float64 `pakt:"rate" json:"rate"`
	Host     string  `pakt:"host" json:"host"`
	Port     int     `pakt:"port" json:"port"`
	MaxRetry int     `pakt:"max_retry" json:"max_retry"`
	Timeout  int     `pakt:"timeout" json:"timeout"`
	Verbose  bool    `pakt:"verbose" json:"verbose"`
	Label    string  `pakt:"label" json:"label"`
}

// ---------------------------------------------------------------------------
// Pre-generated benchmark data (populated by init)
// ---------------------------------------------------------------------------

type benchEncField struct {
	name string
	typ  Type
	val  any
}

var (
	benchSmallVal   benchSmallDoc
	benchSmallPAKT  []byte
	benchSmallJSON  []byte
	benchSmallEncFs []benchEncField

	benchWidePAKT    []byte
	benchWideJSON    []byte
	benchWideEncFs   []benchEncField
	benchWideJSONVal map[string]any

	benchDeepPAKT    []byte
	benchDeepJSON    []byte
	benchDeepEncType Type
	benchDeepEncVal  any
	benchDeepJSONVal map[string]any

	benchListPAKT    []byte
	benchListJSON    []byte
	benchListEncType Type
	benchListEncVal  any
	benchListJSONVal map[string]any

	benchMapPAKT    []byte
	benchMapJSON    []byte
	benchMapEncType Type
	benchMapEncVal  any
	benchMapJSONVal map[string]any
)

func init() {
	benchInitSmall()
	benchInitWide()
	benchInitDeep()
	benchInitLargeList()
	benchInitLargeMap()
}

// ---------------------------------------------------------------------------
// Data generators
// ---------------------------------------------------------------------------

func benchInitSmall() {
	benchSmallVal = benchSmallDoc{
		Name: "my-app", Version: 42, Debug: true, Rate: 3.14,
		Host: "localhost", Port: 8080, MaxRetry: 3, Timeout: 30,
		Verbose: false, Label: "production",
	}

	fields, err := StructFields(reflect.TypeOf(benchSmallVal))
	if err != nil {
		panic(err)
	}

	var buf bytes.Buffer
	enc := NewEncoder(&buf)
	rv := reflect.ValueOf(benchSmallVal)
	for _, fi := range fields {
		v := rv.Field(fi.Index).Interface()
		if err := enc.Encode(fi.Name, fi.Type, v); err != nil {
			panic(err)
		}
		benchSmallEncFs = append(benchSmallEncFs, benchEncField{fi.Name, fi.Type, v})
	}
	benchSmallPAKT = buf.Bytes()

	benchSmallJSON, _ = json.Marshal(benchSmallVal)
}

func benchInitWide() {
	const n = 100
	var pb strings.Builder
	jm := make(map[string]any, n)

	for i := 1; i <= n; i++ {
		name := fmt.Sprintf("field_%03d", i)
		if i%2 == 0 {
			fmt.Fprintf(&pb, "%s:int = %d\n", name, i)
			jm[name] = i
			k := TypeInt
			benchWideEncFs = append(benchWideEncFs, benchEncField{name, Type{Scalar: &k}, int64(i)})
		} else {
			val := fmt.Sprintf("value_%03d", i)
			fmt.Fprintf(&pb, "%s:str = '%s'\n", name, val)
			jm[name] = val
			k := TypeStr
			benchWideEncFs = append(benchWideEncFs, benchEncField{name, Type{Scalar: &k}, val})
		}
	}

	benchWidePAKT = []byte(pb.String())
	benchWideJSONVal = jm
	benchWideJSON, _ = json.Marshal(jm)
}

func benchInitDeep() {
	const depth = 10

	// Build PAKT type and value strings from innermost level outward.
	typeStr := "{name:str}"
	valueStr := fmt.Sprintf("{'level_%d'}", depth-1)
	for i := depth - 2; i >= 0; i-- {
		typeStr = fmt.Sprintf("{name:str, child:%s}", typeStr)
		valueStr = fmt.Sprintf("{'level_%d', %s}", i, valueStr)
	}
	benchDeepPAKT = []byte(fmt.Sprintf("root:%s = %s\n", typeStr, valueStr))

	dv := benchBuildDeepValue(0, depth)
	benchDeepJSONVal = dv
	benchDeepEncVal = dv
	benchDeepJSON, _ = json.Marshal(dv)
	benchDeepEncType = benchBuildDeepType(depth)
}

func benchBuildDeepType(depth int) Type {
	k := TypeStr
	nf := Field{Name: "name", Type: Type{Scalar: &k}}
	if depth <= 1 {
		return Type{Struct: &StructType{Fields: []Field{nf}}}
	}
	ct := benchBuildDeepType(depth - 1)
	return Type{Struct: &StructType{Fields: []Field{nf, {Name: "child", Type: ct}}}}
}

func benchBuildDeepValue(level, maxDepth int) map[string]any {
	m := map[string]any{"name": fmt.Sprintf("level_%d", level)}
	if level < maxDepth-1 {
		m["child"] = benchBuildDeepValue(level+1, maxDepth)
	}
	return m
}

func benchInitLargeList() {
	const n = 10000

	var pb strings.Builder
	pb.WriteString("numbers:[int] = [")
	for i := 1; i <= n; i++ {
		if i > 1 {
			pb.WriteString(", ")
		}
		pb.WriteString(strconv.Itoa(i))
	}
	pb.WriteString("]\n")
	benchListPAKT = []byte(pb.String())

	nums := make([]int, n)
	for i := range nums {
		nums[i] = i + 1
	}
	benchListJSONVal = map[string]any{"numbers": nums}
	benchListJSON, _ = json.Marshal(benchListJSONVal)

	ik := TypeInt
	benchListEncType = Type{List: &ListType{Element: Type{Scalar: &ik}}}
	vals := make([]any, n)
	for i := range vals {
		vals[i] = int64(i + 1)
	}
	benchListEncVal = vals
}

func benchInitLargeMap() {
	const n = 1000

	var pb strings.Builder
	pb.WriteString("data:<str = int> = <")
	for i := 1; i <= n; i++ {
		if i > 1 {
			pb.WriteString(", ")
		}
		fmt.Fprintf(&pb, "'key_%04d' = %d", i, i)
	}
	pb.WriteString(">\n")
	benchMapPAKT = []byte(pb.String())

	m := make(map[string]int, n)
	for i := 1; i <= n; i++ {
		m[fmt.Sprintf("key_%04d", i)] = i
	}
	benchMapJSONVal = map[string]any{"data": m}
	benchMapJSON, _ = json.Marshal(benchMapJSONVal)

	sk := TypeStr
	ik := TypeInt
	benchMapEncType = Type{Map: &MapType{Key: Type{Scalar: &sk}, Value: Type{Scalar: &ik}}}
	mv := make(map[string]int64, n)
	for i := 1; i <= n; i++ {
		mv[fmt.Sprintf("key_%04d", i)] = int64(i)
	}
	benchMapEncVal = mv
}

// ---------------------------------------------------------------------------
// Small Document Benchmarks (Decode / Encode / Marshal / Unmarshal)
// ---------------------------------------------------------------------------

func BenchmarkPAKTDecodeSmall(b *testing.B) {
	data := benchSmallPAKT
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		dec := NewDecoder(bytes.NewReader(data))
		for {
			_, err := dec.Decode()
			if err != nil {
				break
			}
		}
	}
}

func BenchmarkJSONDecodeSmall(b *testing.B) {
	data := benchSmallJSON
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var v map[string]any
		json.Unmarshal(data, &v) //nolint:errcheck
	}
}

func BenchmarkPAKTEncodeSmall(b *testing.B) {
	fs := benchSmallEncFs
	var buf bytes.Buffer
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		buf.Reset()
		enc := NewEncoder(&buf)
		for _, f := range fs {
			enc.Encode(f.name, f.typ, f.val) //nolint:errcheck
		}
	}
}

func BenchmarkJSONEncodeSmall(b *testing.B) {
	val := benchSmallVal
	var buf bytes.Buffer
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		buf.Reset()
		json.NewEncoder(&buf).Encode(val) //nolint:errcheck
	}
}

func BenchmarkPAKTMarshalSmall(b *testing.B) {
	val := benchSmallVal
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		Marshal("doc", val) //nolint:errcheck
	}
}

func BenchmarkJSONMarshalSmall(b *testing.B) {
	val := benchSmallVal
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		json.Marshal(val) //nolint:errcheck
	}
}

func BenchmarkPAKTUnmarshalSmall(b *testing.B) {
	data := benchSmallPAKT
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var v benchSmallDoc
		Unmarshal(data, &v) //nolint:errcheck
	}
}

func BenchmarkJSONUnmarshalSmall(b *testing.B) {
	data := benchSmallJSON
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var v benchSmallDoc
		json.Unmarshal(data, &v) //nolint:errcheck
	}
}

// ---------------------------------------------------------------------------
// Wide Document Benchmarks (100 fields, Decode / Encode)
// ---------------------------------------------------------------------------

func BenchmarkPAKTDecodeWide(b *testing.B) {
	data := benchWidePAKT
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		dec := NewDecoder(bytes.NewReader(data))
		for {
			_, err := dec.Decode()
			if err != nil {
				break
			}
		}
	}
}

func BenchmarkJSONDecodeWide(b *testing.B) {
	data := benchWideJSON
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var v map[string]any
		json.Unmarshal(data, &v) //nolint:errcheck
	}
}

func BenchmarkPAKTEncodeWide(b *testing.B) {
	fs := benchWideEncFs
	var buf bytes.Buffer
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		buf.Reset()
		enc := NewEncoder(&buf)
		for _, f := range fs {
			enc.Encode(f.name, f.typ, f.val) //nolint:errcheck
		}
	}
}

func BenchmarkJSONEncodeWide(b *testing.B) {
	val := benchWideJSONVal
	var buf bytes.Buffer
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		buf.Reset()
		json.NewEncoder(&buf).Encode(val) //nolint:errcheck
	}
}

// ---------------------------------------------------------------------------
// Deep Document Benchmarks (10 levels nested, Decode / Encode)
// ---------------------------------------------------------------------------

func BenchmarkPAKTDecodeDeep(b *testing.B) {
	data := benchDeepPAKT
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		dec := NewDecoder(bytes.NewReader(data))
		for {
			_, err := dec.Decode()
			if err != nil {
				break
			}
		}
	}
}

func BenchmarkJSONDecodeDeep(b *testing.B) {
	data := benchDeepJSON
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var v map[string]any
		json.Unmarshal(data, &v) //nolint:errcheck
	}
}

func BenchmarkPAKTEncodeDeep(b *testing.B) {
	typ := benchDeepEncType
	val := benchDeepEncVal
	var buf bytes.Buffer
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		buf.Reset()
		enc := NewEncoder(&buf)
		enc.Encode("root", typ, val) //nolint:errcheck
	}
}

func BenchmarkJSONEncodeDeep(b *testing.B) {
	val := benchDeepJSONVal
	var buf bytes.Buffer
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		buf.Reset()
		json.NewEncoder(&buf).Encode(val) //nolint:errcheck
	}
}

// ---------------------------------------------------------------------------
// Large List Benchmarks (10,000 ints, Decode / Encode)
// ---------------------------------------------------------------------------

func BenchmarkPAKTDecodeLargeList(b *testing.B) {
	data := benchListPAKT
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		dec := NewDecoder(bytes.NewReader(data))
		for {
			_, err := dec.Decode()
			if err != nil {
				break
			}
		}
	}
}

func BenchmarkJSONDecodeLargeList(b *testing.B) {
	data := benchListJSON
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var v map[string]any
		json.Unmarshal(data, &v) //nolint:errcheck
	}
}

func BenchmarkPAKTEncodeLargeList(b *testing.B) {
	typ := benchListEncType
	val := benchListEncVal
	var buf bytes.Buffer
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		buf.Reset()
		enc := NewEncoder(&buf)
		enc.Encode("numbers", typ, val) //nolint:errcheck
	}
}

func BenchmarkJSONEncodeLargeList(b *testing.B) {
	val := benchListJSONVal
	var buf bytes.Buffer
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		buf.Reset()
		json.NewEncoder(&buf).Encode(val) //nolint:errcheck
	}
}

// ---------------------------------------------------------------------------
// Large Map Benchmarks (1,000 entries, Decode / Encode)
// ---------------------------------------------------------------------------

func BenchmarkPAKTDecodeLargeMap(b *testing.B) {
	data := benchMapPAKT
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		dec := NewDecoder(bytes.NewReader(data))
		for {
			_, err := dec.Decode()
			if err != nil {
				break
			}
		}
	}
}

func BenchmarkJSONDecodeLargeMap(b *testing.B) {
	data := benchMapJSON
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var v map[string]any
		json.Unmarshal(data, &v) //nolint:errcheck
	}
}

func BenchmarkPAKTEncodeLargeMap(b *testing.B) {
	typ := benchMapEncType
	val := benchMapEncVal
	var buf bytes.Buffer
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		buf.Reset()
		enc := NewEncoder(&buf)
		enc.Encode("data", typ, val) //nolint:errcheck
	}
}

func BenchmarkJSONEncodeLargeMap(b *testing.B) {
	val := benchMapJSONVal
	var buf bytes.Buffer
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		buf.Reset()
		json.NewEncoder(&buf).Encode(val) //nolint:errcheck
	}
}
