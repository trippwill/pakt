package encoding

// ⚠️  PERFORMANCE REGRESSION GUARD
//
// PAKT Unmarshal must remain competitive with encoding/json for streaming
// workloads. The FS1K and FS10K benchmarks are the key metrics — PAKT should
// match or beat JSON on these. Run the following before merging changes that
// touch the decode/unmarshal hot path:
//
//   go test ./encoding/ -bench='Benchmark(PAKT|JSON)(Decode|Unmarshal)' -benchmem -count=5
//
// Baseline targets (April 2025, Intel Ultra 5 228V):
//
//   UnmarshalFS1K:   PAKT ≤ JSON  (currently PAKT wins by ~5%)
//   UnmarshalFS10K:  PAKT ≤ JSON  (currently PAKT wins by ~3%)
//   DecodeFS10K:     PAKT ≤ 1.2× JSON
//   UnmarshalSmall:  PAKT ≤ 2.5× JSON  (limited by reflection vs JSON's precompiled decoders)
//
// If PAKT Unmarshal regresses to >1.2× JSON on the FS benchmarks, investigate
// before merging.

import (
	"bytes"
	"encoding/json"
	"fmt"
	"math/rand"
	"reflect"
	"strconv"
	"strings"
	"testing"
	"time"
)

func runPAKTDecodeBenchmark(b *testing.B, data []byte) {
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
		dec.Close()
	}
}

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

type benchFSEntry struct {
	Path    string `pakt:"path" json:"path"`
	Size    int64  `pakt:"size" json:"size"`
	Mode    int64  `pakt:"mode" json:"mode"`
	ModTime string `pakt:"mod_time" json:"mod_time"`
	IsDir   bool   `pakt:"is_dir" json:"is_dir"`
	Owner   string `pakt:"owner" json:"owner"`
	Group   string `pakt:"group" json:"group"`
	Hash    string `pakt:"hash" json:"hash"`
}

type benchFSDataset struct {
	Root    string         `pakt:"root" json:"root"`
	Scanned string         `pakt:"scanned" json:"scanned"`
	Entries []benchFSEntry `pakt:"entries" json:"entries"`
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

	benchFS1KPAKT  []byte
	benchFS1KJSON  []byte
	benchFS1KVal   benchFSDataset
	benchFS1KEncFs []benchEncField

	benchFS10KPAKT  []byte
	benchFS10KJSON  []byte
	benchFS10KVal   benchFSDataset
	benchFS10KEncFs []benchEncField

	benchFS1KNDJSON  []byte
	benchFS10KNDJSON []byte

	benchFSEntriesType Type
)

func init() {
	benchInitSmall()
	benchInitWide()
	benchInitDeep()
	benchInitLargeList()
	benchInitLargeMap()
	benchInitFS()
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

	fields, err := ReflectStructFields(reflect.TypeOf(benchSmallVal))
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
	pb.WriteString("data:<str ; int> = <")
	for i := 1; i <= n; i++ {
		if i > 1 {
			pb.WriteString(", ")
		}
		fmt.Fprintf(&pb, "'key_%04d' ; %d", i, i)
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

func benchInitFS() {
	benchFS1KVal, benchFS1KPAKT, benchFS1KJSON = benchGenerateFS(1000)
	benchFS10KVal, benchFS10KPAKT, benchFS10KJSON = benchGenerateFS(10000)

	benchFS1KNDJSON = benchGenerateNDJSON(benchFS1KVal.Entries)
	benchFS10KNDJSON = benchGenerateNDJSON(benchFS10KVal.Entries)

	benchFSEntriesType = benchFSBuildEntriesType()
	benchFS1KEncFs = benchFSBuildEncFields(benchFS1KVal)
	benchFS10KEncFs = benchFSBuildEncFields(benchFS10KVal)
}

func benchGenerateNDJSON(entries []benchFSEntry) []byte {
	var buf bytes.Buffer
	enc := json.NewEncoder(&buf)
	for i := range entries {
		enc.Encode(entries[i]) //nolint:errcheck
	}
	return buf.Bytes()
}

func benchFSBuildEntriesType() Type {
	scalar := func(k TypeKind) Type { return Type{Scalar: &k} }
	return Type{List: &ListType{Element: Type{Struct: &StructType{Fields: []Field{
		{Name: "path", Type: scalar(TypeStr)},
		{Name: "size", Type: scalar(TypeInt)},
		{Name: "mode", Type: scalar(TypeInt)},
		{Name: "mod_time", Type: scalar(TypeTs)},
		{Name: "is_dir", Type: scalar(TypeBool)},
		{Name: "owner", Type: scalar(TypeStr)},
		{Name: "group", Type: scalar(TypeStr)},
		{Name: "hash", Type: scalar(TypeStr)},
	}}}}}
}

func benchFSBuildEncFields(ds benchFSDataset) []benchEncField {
	sk := TypeStr
	dk := TypeTs

	encEntries := make([]any, len(ds.Entries))
	for i, e := range ds.Entries {
		encEntries[i] = map[string]any{
			"path":     e.Path,
			"size":     e.Size,
			"mode":     e.Mode,
			"mod_time": e.ModTime,
			"is_dir":   e.IsDir,
			"owner":    e.Owner,
			"group":    e.Group,
			"hash":     e.Hash,
		}
	}

	return []benchEncField{
		{"root", Type{Scalar: &sk}, ds.Root},
		{"scanned", Type{Scalar: &dk}, ds.Scanned},
		{"entries", benchFSEntriesType, encEntries},
	}
}

func benchGenerateFS(n int) (benchFSDataset, []byte, []byte) {
	rng := rand.New(rand.NewSource(42)) //nolint:gosec // deterministic seed for reproducible benchmarks

	extensions := []string{".csv", ".parquet", ".json", ".log", ".tmp", ".idx"}
	subdirs := []string{"incoming", "archive", "staging", "reports", "temp", "indexes"}
	fileModes := []int64{0o644, 0o600, 0o444}
	owners := []string{"etl", "root", "app", "backup", "deploy"}
	groups := []string{"data", "root", "apps", "ops"}

	baseTime := time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)
	const dayRange = 151 // Jan 1 to Jun 1

	entries := make([]benchFSEntry, n)
	for i := 0; i < n; i++ {
		isDir := rng.Float64() < 0.15

		depth := rng.Intn(4) + 1
		parts := make([]string, 0, depth+1)
		parts = append(parts, "/data/warehouse")
		for d := 0; d < depth-1; d++ {
			parts = append(parts, subdirs[rng.Intn(len(subdirs))])
		}

		var path string
		var size int64
		var mode int64
		var hash string

		if isDir {
			parts = append(parts, subdirs[rng.Intn(len(subdirs))])
			path = strings.Join(parts, "/") + "/"
			mode = 0o755
		} else {
			name := fmt.Sprintf("file_%05d%s", i, extensions[rng.Intn(len(extensions))])
			parts = append(parts, name)
			path = strings.Join(parts, "/")
			size = int64(rng.Intn(100*1024*1024-1024) + 1024)
			mode = fileModes[rng.Intn(len(fileModes))]
			hash = fmt.Sprintf("%08x", i)
		}

		offset := time.Duration(rng.Intn(dayRange)*24+rng.Intn(24)) * time.Hour
		modTime := baseTime.Add(offset)

		entries[i] = benchFSEntry{
			Path:    path,
			Size:    size,
			Mode:    mode,
			ModTime: modTime.Format(time.RFC3339),
			IsDir:   isDir,
			Owner:   owners[i%len(owners)],
			Group:   groups[i%len(groups)],
			Hash:    hash,
		}
	}

	val := benchFSDataset{
		Root:    "/data/warehouse",
		Scanned: "2026-06-01T14:30:00Z",
		Entries: entries,
	}

	// Build PAKT bytes using pack syntax (<<).
	var pb strings.Builder
	pb.WriteString("root:str = '/data/warehouse'\n")
	pb.WriteString("scanned:ts = 2026-06-01T14:30:00Z\n")
	pb.WriteString("entries:[{path:str, size:int, mode:int, mod_time:ts, is_dir:bool, owner:str, group:str, hash:str}] <<\n")
	for i, e := range entries {
		if i > 0 {
			pb.WriteByte('\n')
		}
		boolStr := "false"
		if e.IsDir {
			boolStr = "true"
		}
		fmt.Fprintf(&pb, "    { '%s', %d, %d, %s, %s, '%s', '%s', '%s' }",
			e.Path, e.Size, e.Mode, e.ModTime, boolStr, e.Owner, e.Group, e.Hash)
	}
	pb.WriteByte('\n')

	jsonBytes, _ := json.Marshal(val)
	return val, []byte(pb.String()), jsonBytes
}

// ---------------------------------------------------------------------------
// Small Document Benchmarks (Decode / Encode / Marshal / Unmarshal)
// ---------------------------------------------------------------------------

func BenchmarkPAKTDecodeSmall(b *testing.B) {
	runPAKTDecodeBenchmark(b, benchSmallPAKT)
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
		UnmarshalNewInto(data, &v) //nolint:errcheck
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
	runPAKTDecodeBenchmark(b, benchWidePAKT)
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
	runPAKTDecodeBenchmark(b, benchDeepPAKT)
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
	runPAKTDecodeBenchmark(b, benchListPAKT)
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
	runPAKTDecodeBenchmark(b, benchMapPAKT)
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

// ---------------------------------------------------------------------------
// Filesystem Metadata Benchmarks — 1K entries (Decode / Unmarshal / Marshal / Encode)
// ---------------------------------------------------------------------------

func BenchmarkPAKTDecodeFS1K(b *testing.B) {
	runPAKTDecodeBenchmark(b, benchFS1KPAKT)
}

func BenchmarkJSONDecodeFS1K(b *testing.B) {
	data := benchFS1KJSON
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var v map[string]any
		json.Unmarshal(data, &v) //nolint:errcheck
	}
}

func BenchmarkPAKTUnmarshalFS1K(b *testing.B) {
	data := benchFS1KPAKT
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var v benchFSDataset
		UnmarshalNewInto(data, &v) //nolint:errcheck
	}
}

func BenchmarkJSONUnmarshalFS1K(b *testing.B) {
	data := benchFS1KJSON
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var v benchFSDataset
		json.Unmarshal(data, &v) //nolint:errcheck
	}
}

func BenchmarkPAKTMarshalFS1K(b *testing.B) {
	val := benchFS1KVal
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		Marshal("doc", val) //nolint:errcheck
	}
}

func BenchmarkJSONMarshalFS1K(b *testing.B) {
	val := benchFS1KVal
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		json.Marshal(val) //nolint:errcheck
	}
}

func BenchmarkPAKTEncodeFS1K(b *testing.B) {
	fs := benchFS1KEncFs
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

func BenchmarkJSONEncodeFS1K(b *testing.B) {
	val := benchFS1KVal
	var buf bytes.Buffer
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		buf.Reset()
		json.NewEncoder(&buf).Encode(val) //nolint:errcheck
	}
}

// ---------------------------------------------------------------------------
// Filesystem Metadata Benchmarks — 10K entries (Decode / Unmarshal / Marshal / Encode)
// ---------------------------------------------------------------------------

func BenchmarkPAKTDecodeFS10K(b *testing.B) {
	runPAKTDecodeBenchmark(b, benchFS10KPAKT)
}

func BenchmarkJSONDecodeFS10K(b *testing.B) {
	data := benchFS10KJSON
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var v map[string]any
		json.Unmarshal(data, &v) //nolint:errcheck
	}
}

func BenchmarkPAKTUnmarshalFS10K(b *testing.B) {
	data := benchFS10KPAKT
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var v benchFSDataset
		UnmarshalNewInto(data, &v) //nolint:errcheck
	}
}

func BenchmarkJSONUnmarshalFS10K(b *testing.B) {
	data := benchFS10KJSON
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var v benchFSDataset
		json.Unmarshal(data, &v) //nolint:errcheck
	}
}

func BenchmarkPAKTMarshalFS10K(b *testing.B) {
	val := benchFS10KVal
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		Marshal("doc", val) //nolint:errcheck
	}
}

func BenchmarkJSONMarshalFS10K(b *testing.B) {
	val := benchFS10KVal
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		json.Marshal(val) //nolint:errcheck
	}
}

func BenchmarkPAKTEncodeFS10K(b *testing.B) {
	fs := benchFS10KEncFs
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

func BenchmarkJSONEncodeFS10K(b *testing.B) {
	val := benchFS10KVal
	var buf bytes.Buffer
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		buf.Reset()
		json.NewEncoder(&buf).Encode(val) //nolint:errcheck
	}
}

// ---------------------------------------------------------------------------
// Streaming Benchmarks — element-by-element pack via More()+UnmarshalNext
// vs NDJSON streaming via json.Decoder
//
// Uses the FS dataset entries as the pack elements. PAKT reads from a pack
// statement (<<); JSON reads from newline-delimited JSON (one object per line).
// Both decode one element per iteration, measuring the true streaming path.
// ---------------------------------------------------------------------------

func BenchmarkPAKTStreamFS1K(b *testing.B) {
	benchStreamPAKT(b, benchFS1KPAKT)
}

func BenchmarkJSONStreamFS1K(b *testing.B) {
	benchStreamJSON(b, benchFS1KNDJSON)
}

func BenchmarkPAKTStreamFS10K(b *testing.B) {
	benchStreamPAKT(b, benchFS10KPAKT)
}

func BenchmarkJSONStreamFS10K(b *testing.B) {
	benchStreamJSON(b, benchFS10KNDJSON)
}

func benchStreamPAKT(b *testing.B, data []byte) {
	b.Helper()
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		type header struct {
			Root    string         `pakt:"root"`
			Scanned string         `pakt:"scanned"`
			Entries []benchFSEntry `pakt:"entries"`
		}
		h, err := UnmarshalNew[header](data)
		if err != nil {
			b.Fatal(err)
		}
		_ = h
	}
}

func benchStreamJSON(b *testing.B, data []byte) {
	b.Helper()
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		dec := json.NewDecoder(bytes.NewReader(data))
		for dec.More() {
			var entry benchFSEntry
			if err := dec.Decode(&entry); err != nil {
				b.Fatal(err)
			}
		}
	}
}

// ---------------------------------------------------------------------------
// Financial Benchmark: Trade + Position data
//
// Domain: trade execution log with a map-pack of portfolio positions.
// Designed to stress non-string scalars (int, dec, bool, ts, uuid),
// atom sets, and embedded composites (list inside struct).
// ---------------------------------------------------------------------------

type benchTrade struct {
	Timestamp string   `pakt:"timestamp" json:"timestamp"`
	Ticker    string   `pakt:"ticker" json:"ticker"`
	Side      string   `pakt:"side" json:"side"`
	Quantity  int64    `pakt:"quantity" json:"quantity"`
	Price     string   `pakt:"price" json:"price"` // dec → string
	Fees      string   `pakt:"fees" json:"fees"`   // dec → string
	Filled    bool     `pakt:"filled" json:"filled"`
	Venue     string   `pakt:"venue" json:"venue"`
	OrderID   string   `pakt:"order_id" json:"order_id"`
	Tags      []string `pakt:"tags" json:"tags"`
}

type benchPosition struct {
	Qty           int64  `pakt:"qty" json:"qty"`
	AvgCost       string `pakt:"avg_cost" json:"avg_cost"`
	UnrealizedPnl string `pakt:"unrealized_pnl" json:"unrealized_pnl"`
	LastPrice     string `pakt:"last_price" json:"last_price"`
	Updated       string `pakt:"updated" json:"updated"`
}

type benchFinDataset struct {
	Account   string                   `pakt:"account" json:"account"`
	AsOf      string                   `pakt:"as_of" json:"as_of"`
	Trades    []benchTrade             `pakt:"trades" json:"trades"`
	Positions map[string]benchPosition `pakt:"positions" json:"positions"`
}

var (
	benchFin1KPAKT   []byte
	benchFin1KJSON   []byte
	benchFin1KVal    benchFinDataset
	benchFin1KNDJSON []byte

	benchFin10KPAKT   []byte
	benchFin10KJSON   []byte
	benchFin10KVal    benchFinDataset
	benchFin10KNDJSON []byte
)

func init() {
	benchInitFin()
}

func benchInitFin() {
	benchFin1KVal, benchFin1KPAKT, benchFin1KJSON = benchGenerateFin(1000)
	benchFin10KVal, benchFin10KPAKT, benchFin10KJSON = benchGenerateFin(10000)
	benchFin1KNDJSON = benchGenerateNDJSON2(benchFin1KVal.Trades)
	benchFin10KNDJSON = benchGenerateNDJSON2(benchFin10KVal.Trades)
}

func benchGenerateNDJSON2[T any](items []T) []byte {
	var buf bytes.Buffer
	enc := json.NewEncoder(&buf)
	for i := range items {
		enc.Encode(items[i]) //nolint:errcheck
	}
	return buf.Bytes()
}

func benchGenerateFin(n int) (benchFinDataset, []byte, []byte) {
	rng := rand.New(rand.NewSource(77)) //nolint:gosec // deterministic seed for reproducible benchmarks

	tickers := []string{"AAPL", "GOOG", "MSFT", "AMZN", "NVDA", "META", "TSLA", "JPM", "V", "UNH",
		"XOM", "JNJ", "PG", "MA", "HD", "CVX", "MRK", "ABBV", "PEP", "KO"}
	venues := []string{"NYSE", "NASDAQ", "BATS", "IEX", "EDGX", "MEMX"}
	tagPool := []string{"algo", "manual", "dark-pool", "pre-market", "post-market", "block", "sweep", "iceberg"}

	baseTime := time.Date(2026, 3, 1, 9, 30, 0, 0, time.FixedZone("EST", -5*3600))

	trades := make([]benchTrade, n)
	for i := 0; i < n; i++ {
		ticker := tickers[rng.Intn(len(tickers))]

		side := "buy"
		if rng.Float64() < 0.45 {
			side = "sell"
		}

		qty := int64(rng.Intn(9900) + 100)
		priceDollars := rng.Intn(400) + 10
		priceCents := rng.Intn(100)
		price := fmt.Sprintf("%d.%02d", priceDollars, priceCents)

		feesCents := rng.Intn(500) + 1
		fees := fmt.Sprintf("%d.%02d", feesCents/100, feesCents%100)

		filled := rng.Float64() < 0.92
		venue := venues[rng.Intn(len(venues))]

		orderID := fmt.Sprintf("%08x-%04x-%04x-%04x-%012x",
			rng.Uint32(), rng.Uint32()&0xFFFF, 0x4000|rng.Uint32()&0x0FFF,
			0x8000|rng.Uint32()&0x3FFF, rng.Int63()&0xFFFFFFFFFFFF)

		// 1-3 tags per trade
		numTags := rng.Intn(3) + 1
		tags := make([]string, numTags)
		for j := range numTags {
			tags[j] = tagPool[rng.Intn(len(tagPool))]
		}

		offset := time.Duration(i*3+rng.Intn(3)) * time.Second
		ts := baseTime.Add(offset)

		trades[i] = benchTrade{
			Timestamp: ts.Format(time.RFC3339),
			Ticker:    ticker,
			Side:      side,
			Quantity:  qty,
			Price:     price,
			Fees:      fees,
			Filled:    filled,
			Venue:     venue,
			OrderID:   orderID,
			Tags:      tags,
		}
	}

	// Build positions from unique tickers seen
	positions := make(map[string]benchPosition)
	for _, t := range tickers {
		priceDollars := rng.Intn(400) + 10
		priceCents := rng.Intn(100)
		costDollars := rng.Intn(400) + 10
		costCents := rng.Intn(100)
		pnl := (priceDollars - costDollars) * (rng.Intn(5000) + 100)

		positions[t] = benchPosition{
			Qty:           int64(rng.Intn(50000) + 100),
			AvgCost:       fmt.Sprintf("%d.%02d", costDollars, costCents),
			UnrealizedPnl: fmt.Sprintf("%d.%02d", pnl, rng.Intn(100)),
			LastPrice:     fmt.Sprintf("%d.%02d", priceDollars, priceCents),
			Updated:       baseTime.Add(time.Duration(n*3) * time.Second).Format(time.RFC3339),
		}
	}

	val := benchFinDataset{
		Account:   "ACCT-7734-PRIME",
		AsOf:      baseTime.Add(time.Duration(n*3) * time.Second).Format(time.RFC3339),
		Trades:    trades,
		Positions: positions,
	}

	// Build PAKT
	var pb strings.Builder
	pb.WriteString("account:str = 'ACCT-7734-PRIME'\n")
	pb.WriteString(fmt.Sprintf("as_of:ts = %s\n", val.AsOf))

	// Trades as list pack
	pb.WriteString("trades:[{timestamp:ts, ticker:str, side:|buy, sell|, quantity:int, price:dec, fees:dec, filled:bool, venue:str, order_id:uuid, tags:[str]}] <<\n")
	for i, tr := range trades {
		if i > 0 {
			pb.WriteByte('\n')
		}
		boolStr := "false"
		if tr.Filled {
			boolStr = "true"
		}
		// Build tags list
		var tagBuf strings.Builder
		tagBuf.WriteByte('[')
		for j, tag := range tr.Tags {
			if j > 0 {
				tagBuf.WriteString(", ")
			}
			fmt.Fprintf(&tagBuf, "'%s'", tag)
		}
		tagBuf.WriteByte(']')

		fmt.Fprintf(&pb, "    { %s, '%s', |%s, %d, %s, %s, %s, '%s', %s, %s }",
			tr.Timestamp, tr.Ticker, tr.Side, tr.Quantity, tr.Price, tr.Fees,
			boolStr, tr.Venue, tr.OrderID, tagBuf.String())
	}
	pb.WriteString("\n")

	// Positions as map pack
	pb.WriteString("positions:<str ; {qty:int, avg_cost:dec, unrealized_pnl:dec, last_price:dec, updated:ts}> <<\n")
	first := true
	for ticker, pos := range positions {
		if !first {
			pb.WriteByte('\n')
		}
		first = false
		fmt.Fprintf(&pb, "    '%s' ; { %d, %s, %s, %s, %s }",
			ticker, pos.Qty, pos.AvgCost, pos.UnrealizedPnl, pos.LastPrice, pos.Updated)
	}
	pb.WriteString("\n")

	jsonBytes, _ := json.Marshal(val)
	return val, []byte(pb.String()), jsonBytes
}

// ---------------------------------------------------------------------------
// Financial Benchmarks — 1K trades
// ---------------------------------------------------------------------------

func BenchmarkPAKTDecodeFin1K(b *testing.B) {
	runPAKTDecodeBenchmark(b, benchFin1KPAKT)
}

func BenchmarkJSONDecodeFin1K(b *testing.B) {
	data := benchFin1KJSON
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var v map[string]any
		json.Unmarshal(data, &v) //nolint:errcheck
	}
}

func BenchmarkPAKTUnmarshalFin1K(b *testing.B) {
	data := benchFin1KPAKT
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var v benchFinDataset
		UnmarshalNewInto(data, &v) //nolint:errcheck
	}
}

func BenchmarkJSONUnmarshalFin1K(b *testing.B) {
	data := benchFin1KJSON
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var v benchFinDataset
		json.Unmarshal(data, &v) //nolint:errcheck
	}
}

// ---------------------------------------------------------------------------
// Financial Benchmarks — 10K trades
// ---------------------------------------------------------------------------

func BenchmarkPAKTDecodeFin10K(b *testing.B) {
	runPAKTDecodeBenchmark(b, benchFin10KPAKT)
}

func BenchmarkJSONDecodeFin10K(b *testing.B) {
	data := benchFin10KJSON
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var v map[string]any
		json.Unmarshal(data, &v) //nolint:errcheck
	}
}

func BenchmarkPAKTUnmarshalFin10K(b *testing.B) {
	data := benchFin10KPAKT
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var v benchFinDataset
		UnmarshalNewInto(data, &v) //nolint:errcheck
	}
}

func BenchmarkJSONUnmarshalFin10K(b *testing.B) {
	data := benchFin10KJSON
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var v benchFinDataset
		json.Unmarshal(data, &v) //nolint:errcheck
	}
}

// ---------------------------------------------------------------------------
// Financial Benchmarks — Streaming (one trade at a time)
// ---------------------------------------------------------------------------

func BenchmarkPAKTStreamFin1K(b *testing.B) {
	data := benchFin1KPAKT
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		sr := NewUnitReaderFromBytes(data)
		for stmt := range sr.Properties() {
			if stmt.Name == "trades" && stmt.IsPack {
				for trade := range PackItems[benchTrade](sr) {
					_ = trade
				}
			}
		}
		sr.Close()
	}
}

func BenchmarkPAKTStreamFin10K(b *testing.B) {
	data := benchFin10KPAKT
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		sr := NewUnitReaderFromBytes(data)
		for stmt := range sr.Properties() {
			if stmt.Name == "trades" && stmt.IsPack {
				for trade := range PackItems[benchTrade](sr) {
					_ = trade
				}
			}
		}
		sr.Close()
	}
}

func BenchmarkJSONStreamFin1K(b *testing.B) {
	data := benchFin1KNDJSON
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		dec := json.NewDecoder(bytes.NewReader(data))
		for dec.More() {
			var trade benchTrade
			if err := dec.Decode(&trade); err != nil {
				b.Fatal(err)
			}
			_ = trade
		}
	}
}

func BenchmarkJSONStreamFin10K(b *testing.B) {
	data := benchFin10KNDJSON
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		dec := json.NewDecoder(bytes.NewReader(data))
		for dec.More() {
			var trade benchTrade
			if err := dec.Decode(&trade); err != nil {
				b.Fatal(err)
			}
			_ = trade
		}
	}
}
