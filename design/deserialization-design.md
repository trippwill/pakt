# Deserialization Design — PAKT

## Problem Statement

What should deserialization look like for PAKT — a typed, streaming, self-describing data interchange format? This document is a design exploration: principles and API sketches for what a streaming-first deserialization architecture should be, independent of specific language implementations.

**Design constraints:**
- **Streaming-first:** The entire design is streaming-first; materialization is sugar. This was a deliberate choice — PAKT's pack statements are the primary use case, not an advanced mode.
- **Custom deserializers:** Essential for real-world use; must participate in the stream (receive a reader, not pre-materialized data). Decided: per-field and per-host-type registration only — per-PAKT-type converters were rejected as too broad (can hijack unrelated target types).
- **No dynamic/untyped document model:** PAKT is typed; callers always have a target type. Deserializing into `any`/`object` is an error.
- **Cross-ecosystem consistency:** Share design principles across Go and .NET; API shape is fully idiomatic per-ecosystem.
- **Part 1** provides conceptual principles and pseudocode; **Part 2** provides concrete Go 1.25 and .NET 10 / C# 14 API designs

---

## 1. What Makes PAKT Different (and Why It Matters for Deserialization)

Five characteristics of PAKT drive the deserialization design away from the JSON/YAML model:

### 1.1 Self-Describing at the Statement Level

Every top-level statement carries its type: `server:{host:str, port:int} = {'localhost', 8080}`. The parser validates values against the type annotation during parsing. By the time the deserializer sees data, it's **guaranteed well-typed** per the annotation.

**Implication:** The deserializer's job is *mapping*, not *validating*. It doesn't ask "is this really an int?" — the parser already checked. It asks "can I fit this PAKT int into a Go int32?" (narrowing) or "does this PAKT struct have a field the target type expects?" (compatibility).

### 1.2 Keyed Struct Types, Positional Struct Values

PAKT struct *types* are keyed — they declare named, typed fields: `{host:str, port:int}`. But struct *values* are positional — they contain bare values matched left-to-right against the type's field declarations: `{'localhost', 8080}`. The parser resolves value positions to field names using the type annotation before the deserializer ever sees the data.

**Implication:** Unlike JSON (where the deserializer matches `"host"` keys to struct fields), in PAKT the parser has already done that mapping. The event stream delivers named, typed values — the names come from the type, not the value. Deserialization is a simpler mapping step.

### 1.3 Packs Are the Streaming Primitive

Pack statements (`<<`) deliver open-ended sequences of values, terminated by end-of-unit or the next statement. They're designed for streaming: log lines, rows, events.

**Implication:** The deserialization API must make packs feel natural to process one element at a time. This isn't an "advanced" mode — it's the primary use case for pack statements.

### 1.4 The Decoder Is Lossless; Interpretation Is Layered

The spec (§0.1, Principle 3) says: *"A conforming decoder preserves all information... Policy decisions such as rejecting duplicates belong to higher-level consumers."*

**Implication:** Deserialization IS the higher-level consumer. It makes policy decisions: duplicate handling, unknown-field handling, type coercion rules. These policies should be explicit and configurable, not hidden.

### 1.5 Type Context Flows With the Data

The spec (§0.1, Principle 2): *"Every value carries or inherits its type. The parser never guesses."*

**Implication:** The deserializer can always compare the data's declared type with the target type *before* reading any values. This enables early, precise errors — "field `port` is declared `str` in the data but the target expects `int`" — rather than the "strconv.Atoi failed" errors you get with JSON.

---

## 2. Design Principles for PAKT Deserialization

Derived from PAKT's spec principles and the streaming-first constraint:

### P1. The Stream Is the Primitive

The most fundamental deserialization operation is: **read one value from the stream into a typed host-language target.** Everything else — reading a full unit, reading a pack — is built on repetition of this operation.

There is no "buffer everything then map." The deserializer pulls from the stream, one value at a time.

### P2. Statement Headers Are the Navigation Layer

A PAKT unit is a sequence of statements. Each statement has a header (name, type, assign/pack). The **statement header** is how the deserializer navigates:

1. Read header → know what's coming (name, type, pack?)
2. Decide what to do (deserialize into field X, skip, stream elements)
3. Read values

This is a **pull model**: the caller decides when to advance and what to read. The deserializer never reads ahead of the caller's request.

### P3. Type Compatibility Is Checked Early

Because PAKT carries type annotations, the deserializer should compare the data type with the target type **before reading values** — at the statement header or composite entry point. This gives precise, early errors.

### P4. Custom Deserializers Participate in the Stream

A custom deserializer receives a positioned reader and the declared PAKT type. It reads from the stream — it doesn't receive a pre-materialized value. This keeps the streaming contract intact: no hidden buffering.

### P5. Policy Is Explicit

Decisions that the spec leaves to "higher-level consumers" — duplicate handling, unknown fields, type coercion — must be visible and configurable. Default policies should be documented and unsurprising.

---

## 3. The Deserialization Tiers

### Tier 0: Event Stream (the decoder)

**Already exists.** The decoder emits one event per grammatical construct. This is the building block but not a deserialization interface.

```
decoder = NewDecoder(stream)
while event = decoder.Decode():
    // EventAssignStart, EventScalarValue, EventStructStart, ...
```

**Who uses this:** Tool builders, formatters, custom stream processors. Not typical deserialization.

---

### Tier 1: Statement Reader (the primary interface)

The streaming-first deserialization primitive. Reads one statement at a time. Within a statement, reads one typed value (or iterates pack elements).

```pseudocode
reader = NewStatementReader(stream)

while reader.NextStatement():
    name = reader.Name()           // "server", "events", etc.
    type = reader.Type()           // the PAKT type annotation
    isPack = reader.IsPack()       // true if <<

    if isPack:
        while reader.HasMore():
            item = reader.ReadValue<T>()   // one pack element
            process(item)
    else:
        value = reader.ReadValue<T>()      // the single assign value
        handle(name, value)
```

**Key properties:**
- **Pull-based.** The caller decides when to advance.
- **Type-aware.** `reader.Type()` gives the declared PAKT type before any value is read.
- **Generic over the target type.** `ReadValue<T>()` maps the PAKT value to `T` using the type metadata system (reflection, source generation, or custom deserializer).
- **Skip-friendly.** If the caller doesn't recognize a statement, they call `reader.Skip()` to advance past it without allocating.
- **Pack-native.** `HasMore()` + `ReadValue<T>()` is the natural pack iteration pattern. No special API — same `ReadValue<T>()`, just called in a loop.

**Streaming contract:** At any point, only the current statement's current value is in flight. No look-ahead. Constant memory per nesting level.

#### What `ReadValue<T>()` Does

This is the core mapping operation. Given a PAKT type and value stream, produce a `T`:

1. **Check compatibility** between PAKT type and `T`. If incompatible, error early.
2. **Scalars:** Read the scalar literal, convert to `T`. Validate narrowing (int overflow, etc.).
3. **Composites:** Push into the composite, read child values, map to `T`'s fields/elements.
4. **Custom deserializers:** If `T` has a registered custom deserializer, delegate to it.
5. **Nullable:** If the value is `nil`, set `T` to its null representation (pointer, Optional, etc.).

#### Heterogeneous Units

Real PAKT units often have different types for different statements:

```pakt
name:str = 'myservice'
version:(int, int, int) = (2, 1, 0)
config:{host:str, port:int} = {'localhost', 8080}
events:[{ts:ts, level:str, msg:str}] <<
    {2026-06-01T14:30:00Z, 'info', 'started'}
```

The statement reader handles this naturally:

```pseudocode
reader = NewStatementReader(stream)

while reader.NextStatement():
    switch reader.Name():
        case "name":
            name = reader.ReadValue<string>()
        case "version":
            version = reader.ReadValue<Version>()
        case "config":
            config = reader.ReadValue<Config>()
        case "events":
            while reader.HasMore():
                event = reader.ReadValue<Event>()
                process(event)
        default:
            reader.Skip()
```

---

### Tier 2: Whole-Unit Materialization (sugar)

Built on Tier 1. Reads all statements in a unit and maps them to fields of a target struct.

```pseudocode
func Unmarshal<T>(data, target: &T):
    reader = NewStatementReader(data)
    fields = TypeMetadata<T>.Fields()        // cached field info

    while reader.NextStatement():
        field = fields.FindByPaktName(reader.Name())
        if field is None:
            reader.Skip()                     // unknown field policy
            continue

        if reader.IsPack:
            collection = field.AsCollection()
            while reader.HasMore():
                elem = reader.ReadValue<field.ElementType>()
                collection.Add(elem)
        else:
            value = reader.ReadValue<field.Type>()
            field.Set(target, value)
```

**This is sugar.** It loops `NextStatement()` and dispatches `ReadValue<T>()` for each field. The implementation can be generated (source gen), reflected (runtime reflection), or hand-written — the pattern is the same.

**Materialization is a convenience wrapper over the streaming reader, not a parallel implementation.** Both code paths should use the same underlying `ReadValue` logic.

---

### Tier 3: Custom Deserializers

A custom deserializer is a user-defined function that takes over the deserialization of a specific type. It participates in the stream — it receives a reader positioned at the value, not a pre-materialized result.

#### The Interface

```pseudocode
interface ValueDeserializer<T>:
    // Called when a PAKT value of a compatible type needs to be deserialized into T.
    // `reader` is positioned at the start of the value.
    // `paktType` is the declared PAKT type annotation.
    // The deserializer MUST consume exactly one complete value from the reader.
    Deserialize(reader: ValueReader, paktType: PaktType) → T
```

#### What the ValueReader Provides

For scalars:
```pseudocode
reader.ScalarType()    → str | int | dec | float | bool | uuid | date | ts | bin
reader.StringValue()   → string    // the raw text
reader.IntValue()      → int64     // parsed int
reader.DecValue()      → decimal   // parsed decimal
reader.BoolValue()     → bool      // parsed bool
// etc.
```

For composites:
```pseudocode
reader.IsStruct()      → bool
reader.StructFields()  → iterator of (name: string, type: PaktType)
reader.ReadField<T>()  → T         // read next struct field value

reader.IsList()        → bool
reader.ListElement()   → PaktType  // the element type
reader.ReadElement<T>()→ T         // read next list element
reader.HasMore()       → bool      // more elements?

reader.IsMap()         → bool
reader.MapKeyType()    → PaktType
reader.MapValueType()  → PaktType
reader.ReadKey<K>()    → K
reader.ReadMapValue<V>()→ V

reader.IsTuple()       → bool
reader.TupleElements() → []PaktType
reader.ReadElement<T>()→ T         // read next tuple element
```

#### Registration and Precedence

Custom deserializers attach at two levels, with this precedence (highest first):

1. **Per field:** "For this specific struct field, use this deserializer."
2. **Per host type:** "Whenever deserializing into type `T`, use this deserializer."

Lower-precedence deserializers are only consulted if no higher-precedence one matches.

#### Example: Custom Timestamp Deserializer

```pseudocode
// A custom deserializer that parses PAKT timestamps into a domain-specific Instant type
struct InstantDeserializer implements ValueDeserializer<Instant>:
    Deserialize(reader, paktType):
        raw = reader.StringValue()
        return Instant.Parse(raw, myCustomFormat)

// Registration (per host type)
options.RegisterDeserializer<Instant>(InstantDeserializer{})
```

#### Example: Custom Struct Deserializer (Validation)

```pseudocode
// A custom deserializer that adds validation to a Config struct
struct ConfigDeserializer implements ValueDeserializer<Config>:
    Deserialize(reader, paktType):
        config = Config{}
        for name, type in reader.StructFields():
            switch name:
                case "host":
                    config.Host = reader.ReadField<string>()
                case "port":
                    port = reader.ReadField<int>()
                    if port < 1 or port > 65535:
                        error("port out of range: {port}")
                    config.Port = port
                default:
                    reader.SkipField()
        return config
```

---

## 4. Key Design Decisions

### 4.1 Type Compatibility Model

Because PAKT annotations are validated at parse time, the deserializer deals with **mapping**, not **validation**. The compatibility rules:

| Category | Rule | Example |
|----------|------|---------|
| **Exact match** | PAKT type matches host type directly | `int` → int64, `str` → string |
| **Narrowing** | PAKT type fits into a smaller host type | `int` → int32 (overflow check) |
| **Nullable** | PAKT `type?` maps to host nullable | `str?` → *string, Optional\<string\> |
| **Structural** | PAKT composite maps to host composite | `{host:str}` → Config{Host string} |
| **Extra fields** | Data has fields target doesn't | Skip silently (configurable) |
| **Missing fields** | Target has fields data doesn't | Zero value (configurable) |
| **Atom → enum** | PAKT atom set maps to host enum | `\|a,b,c\|` → enum{A,B,C} |
| **Custom** | Custom deserializer handles mapping | any → any (user-defined) |

**Not supported (error):**
- PAKT `str` → host `int` (fundamental type mismatch)
- PAKT non-nullable `nil` (caught at parse time, never reaches deserializer)

### 4.2 Unknown Statement/Field Handling

**Default policy:** Skip silently. This enables forward compatibility — new fields can be added to data without breaking old consumers.

**Configurable policies:**
- `Skip` (default) — unknown fields ignored
- `Error` — unknown fields are an error (strict mode)

### 4.2b Missing Field Handling

**Default policy:** Zero value. If the target type expects a field that the PAKT data doesn't contain, the field retains its zero/default value.

**Configurable policies:**
- `ZeroValue` (default) — missing fields get the type's zero value
- `Error` — missing required fields are an error (strict mode)

### 4.3 Duplicate Statement Handling

The decoder preserves duplicates. The deserializer must choose a policy:

**Default policy:** Last-wins for struct targets (consistent with most config systems).

**Configurable policies:**
- `LastWins` (default) — last value overwrites previous
- `FirstWins` — first value kept, subsequent ignored
- `Error` — duplicate is an error
- `Accumulate` — append to a collection (if target is a collection type)

### 4.4 Atom Set Mapping

PAKT atom sets (`|dev, staging, prod|`) are constrained string enumerations. Mapping options:

- **String:** The simplest. Atom values are strings. No compile-time safety.
- **Enum:** Host language enum type. The deserializer validates that the atom value matches a known enum member.
- **Custom deserializer:** Full control.

The default should be string (lowest friction). Enum mapping should be opt-in via type metadata (struct tags, attributes, etc.).

### 4.5 Tuple Mapping

PAKT tuples (`(int, str, bool)`) are heterogeneous and positional. Host language mapping depends on ecosystem:

- **Go:** Struct with fields matched positionally. The first field gets the first tuple element, second gets second, etc. Field names are irrelevant — only count and types matter. A fixed-size array works when all elements share a type.
- **.NET:** `ValueTuple<T1, T2, T3>` or positional record. The tuple element types must match positionally.
- **Other:** Language-specific tuple/product types

The key requirements:
1. The target type must declare exactly as many positional slots as the tuple has elements.
2. Each slot's type must be compatible with the corresponding tuple element's type.
3. Arity mismatch (too few or too many) is always an error — unlike structs, there's no concept of "unknown" or "missing" tuple elements.

### 4.6 Error Propagation

Deserialization errors should include:
- **Source position** (line, column) from the PAKT data
- **Statement context** (which statement name)
- **Field context** (which field within a composite)
- **The nature of the failure** (type mismatch, overflow, missing field, custom deserializer error)

Errors are returned immediately (fail-fast), not accumulated. This is consistent with streaming — you can't "continue past" a deserialization error in a stream.

---

## 5. The Streaming Architecture Visualized

```
┌─────────────────────────────────────────────────────────────────┐
│                     PAKT Byte Stream                            │
└────────────────────────────┬────────────────────────────────────┘
                             │
                     ┌───────▼───────┐
                     │    Decoder    │  Tier 0: Events
                     │  (parser +   │  EventAssignStart, EventScalarValue, ...
                     │   state      │  [validates type annotations]
                     │   machine)   │
                     └───────┬───────┘
                             │
                    ┌────────▼────────┐
                    │   Statement    │  Tier 1: Statements
                    │    Reader     │  NextStatement() → Name, Type, IsPack
                    │               │  ReadValue<T>() → one typed value
                    │               │  HasMore() → pack iteration
                    └───┬───────┬───┘
                        │       │
              ┌─────────▼──┐  ┌─▼───────────────┐
              │  Unmarshal │  │ Custom           │  Tier 2 & 3
              │  (sugar)   │  │ Deserializers    │
              │            │  │ (user-defined)   │
              │ Loops      │  │ Participate in   │
              │ statements │  │ the stream       │
              │ maps to    │  │                  │
              │ struct     │  │                  │
              │ fields     │  │                  │
              └────────────┘  └──────────────────┘
```

### The critical invariant

**Every tier reads from the same stream, in order, without buffering.** Materialization doesn't buffer-then-map; it loops the streaming primitives. Custom deserializers don't receive pre-read data; they read from the stream themselves.

This means:
- Memory is O(nesting depth), not O(data size)
- Pack elements can be processed and discarded one at a time
- A custom deserializer in the middle of a struct doesn't break the streaming contract

---

## 6. Pseudocode Sketches for Common Patterns

### Pattern A: Config File (whole-unit materialization)

```pakt
name:str = 'myservice'
host:str = 'localhost'
port:int = 8080
debug:bool = false
```

```pseudocode
type Config struct {
    Name  string  @pakt("name")
    Host  string  @pakt("host")
    Port  int     @pakt("port")
    Debug bool    @pakt("debug")
}

config = Unmarshal<Config>(data)
// Uses Tier 2 (materialization) internally
```

### Pattern B: Streaming Log Processing (pack iteration)

```pakt
events:[{ts:ts, level:|info,warn,error|, msg:str}] <<
    {2026-06-01T14:30:00Z, |info, 'server started'}
    {2026-06-01T14:31:00Z, |warn, 'high latency'}
    {2026-06-01T14:32:00Z, |error, 'connection lost'}
```

```pseudocode
reader = NewStatementReader(stream)

while reader.NextStatement():
    if reader.Name() == "events" and reader.IsPack():
        while reader.HasMore():
            event = reader.ReadValue<LogEvent>()
            process(event)  // constant memory per event
```

### Pattern C: Heterogeneous Unit (mixed statement types)

```pakt
name:str = 'deployment-2026-06-01'
targets:[str] = ['us-east-1', 'eu-west-1']
config:{replicas:int, image:str} = {3, 'myapp:latest'}
metrics:<str ; float> = <'cpu' ; 0.85, 'mem' ; 0.62>
```

```pseudocode
reader = NewStatementReader(stream)

while reader.NextStatement():
    switch reader.Name():
        case "name":    name = reader.ReadValue<string>()
        case "targets": targets = reader.ReadValue<[]string>()
        case "config":  config = reader.ReadValue<DeployConfig>()
        case "metrics": metrics = reader.ReadValue<map[string]float>()
        default:        reader.Skip()
```

### Pattern D: Custom Deserializer (semantic validation)

```pakt
endpoint:{url:str, timeout:int, retries:int} = {'https://api.example.com', 30, 5}
```

```pseudocode
struct EndpointDeserializer implements ValueDeserializer<Endpoint>:
    Deserialize(reader, paktType):
        ep = Endpoint{}
        for name, type in reader.StructFields():
            switch name:
                case "url":
                    raw = reader.ReadField<string>()
                    ep.URL = ParseURL(raw)  // domain-specific parsing
                    if ep.URL.Scheme != "https":
                        error("endpoint must use HTTPS")
                case "timeout":
                    ep.Timeout = Duration(reader.ReadField<int>(), Seconds)
                case "retries":
                    n = reader.ReadField<int>()
                    if n < 0 or n > 10:
                        error("retries must be 0-10")
                    ep.Retries = n
        return ep
```

### Pattern E: Pack with Custom Deserializer (streaming + custom)

```pakt
rows:[{id:int, data:bin, checksum:str}] <<
    {1, b'SGVsbG8=', 'sha256:abc123'}
    {2, b'V29ybGQ=', 'sha256:def456'}
```

```pseudocode
struct VerifiedRowDeserializer implements ValueDeserializer<VerifiedRow>:
    Deserialize(reader, paktType):
        row = VerifiedRow{}
        for name, type in reader.StructFields():
            switch name:
                case "id":       row.ID = reader.ReadField<int>()
                case "data":     row.Data = reader.ReadField<bytes>()
                case "checksum": row.Checksum = reader.ReadField<string>()
        // Verify integrity before returning
        if not VerifyChecksum(row.Data, row.Checksum):
            error("checksum mismatch for row {row.ID}")
        return row

// Usage: streaming with per-element verification
reader = NewStatementReader(stream)
while reader.NextStatement():
    if reader.Name() == "rows":
        while reader.HasMore():
            row = reader.ReadValue<VerifiedRow>()  // custom deserializer runs
            store(row)
```

---

## 7. Open Questions

### Q1. Should ReadValue support reading into pre-existing values?

Two modes:
- **Create:** `value = reader.ReadValue<T>()` — allocates and returns a new T
- **Populate:** `reader.ReadValueInto(&existingT)` — populates an existing value

Populate mode enables buffer reuse in hot loops (reuse the same struct for each pack element). This matters for performance in streaming scenarios.

**Recommendation:** Support both. Create is the default for ergonomics. Populate is opt-in for performance-sensitive pack processing.

### Q2. Should the Statement Reader expose the raw event stream?

Some advanced callers may want to drop down to Tier 0 within a statement (e.g., to implement a custom event-driven processor). Should the statement reader expose its underlying decoder?

**Recommendation:** Yes, but as an explicit "escape hatch" that clearly documents the contract: once you take the raw decoder, you own advancing it correctly.

### Q3. How should atom sets interact with custom deserializers?

Atom sets are validated at parse time — the value is guaranteed to be one of the declared members. Should a custom deserializer for an atom-set-typed field receive the raw atom string, or a pre-validated enum value?

**Recommendation:** The custom deserializer receives the raw atom string. It can trust the string is a valid member (the parser checked), but it does its own mapping to the host type. This keeps the custom deserializer interface uniform.

### Q4. Pack element count — should it be available?

For list packs, the producer doesn't declare an element count. The consumer reads until the pack ends. Should the reader expose a count hint (if known)?

**Recommendation:** No. The streaming contract means you don't know the count until you've read everything. Callers who need a count should collect into a list. Providing a count hint would violate the streaming-first principle and couldn't be trusted anyway.

### Q5. Statement-level type checking — when and how?

When `reader.ReadValue<Config>()` is called, when does the type check happen?

- **Eager:** Compare PAKT type annotation with Config's type metadata before reading any values. Fail immediately if incompatible.
- **Lazy:** Read values and let individual field mismatches surface naturally.

**Recommendation:** Eager for composites (check structural compatibility upfront), lazy for scalars (check at conversion time). This gives the best error messages without unnecessary overhead.

---

---

# Part 2: Language-Specific API Design

> **Constraint:** This API design gives no weight to existing implementations. It asks: given Go 1.25 and .NET 10 / C# 14, what's the ideal API for each ecosystem?

---

## 8. Relevant Language & Runtime Features

### 8.1 Go 1.25

| Feature | Relevance to PAKT |
|---------|-------------------|
| **`iter.Seq[V]` / `iter.Seq2[K,V]`** | Pack iteration and composite traversal return iterators. `for event := range reader.Statements()` is idiomatic. |
| **Range-over-func (stable)** | Custom iterators compose with `for...range`. Statement readers and pack readers become rangeable. |
| **Generics (no core types)** | `ReadValue[T]()` is now practical. Generic deserialization functions with proper type constraints. |
| **Bounded `sync.Pool`** | Pooled readers, state machines, and buffers with memory pressure control. |
| **PGO (stable)** | Hot paths (scalar conversion, field lookup) optimizable from production profiles. |

**Not available in Go:** Source generation, compile-time metaprogramming, ref structs, `Span<T>`. Go relies on runtime reflection or code generation tools (go generate).

### 8.2 .NET 10 / C# 14

| Feature | Relevance to PAKT |
|---------|-------------------|
| **Partial constructors** | Source generator can emit constructor logic for deserialization targets. Generated partial ctors initialize type metadata without user boilerplate. |
| **Extension members** | `ReadOnlySpan<byte>.DeserializePakt<T>()` as an extension method/property block. Cleaner API surface without polluting the type. |
| **Implicit `Span<T>` conversions** | `byte[]`, `Memory<byte>`, and `ReadOnlySpan<byte>` all flow into deserializer APIs seamlessly. |
| **`ref struct`** | Reader type lives on the stack. Zero heap allocation for the reader itself. |
| **`IAsyncEnumerable<T>`** | Async pack iteration: `await foreach (var item in reader.ReadPack<T>())`. |
| **Source generators (incremental)** | Compile-time codegen for per-type deserialization delegates. No reflection at runtime. |
| **`field` keyword** | Simplifies generated property accessors in deserialized types. |

---

## 9. Go API Design

### 9.1 Package Structure

```
encoding/              # existing package: github.com/trippwill/pakt/encoding
    decoder.go         # Tier 0: event-level decoder (exists)
    reader.go          # Tier 1: StatementReader
    unmarshal.go       # Tier 2: Unmarshal / UnmarshalFrom
    converter.go       # Tier 3: ValueConverter interface + registry
    options.go         # DeserializeOptions (policies)
    types.go           # PaktType, TypeKind (exists)
    errors.go          # ParseError (exists)
```

### 9.2 Tier 0: Decoder (unchanged)

The event-level decoder exists and is the foundation. No changes needed to its API.

```go
type Decoder struct { /* ... */ }

func NewDecoder(r io.Reader) *Decoder
func (d *Decoder) Decode() (Event, error)
func (d *Decoder) Close()
```

### 9.3 Tier 1: StatementReader — The Primary API

The `StatementReader` wraps a decoder and provides a pull-based, statement-at-a-time interface. It's the primary way callers consume PAKT data.

```go
// StatementReader reads PAKT statements one at a time from a stream.
// It is the primary deserialization interface.
type StatementReader struct { /* unexported fields */ }

// NewStatementReader creates a reader from any io.Reader.
func NewStatementReader(r io.Reader, opts ...Option) *StatementReader

// NewStatementReaderFromBytes creates a reader from a byte slice (zero-copy path).
func NewStatementReaderFromBytes(data []byte, opts ...Option) *StatementReader

// Close releases all pooled resources. Must be called when done.
func (sr *StatementReader) Close()
```

#### Statement Navigation

```go
// Statement represents a top-level statement header.
// It is valid only until the next call to NextStatement or Close.
type Statement struct {
    Name   string   // statement name (e.g., "server", "events")
    Type   Type     // declared PAKT type annotation
    IsPack bool     // true if << (pack statement)
}

// Statements returns an iterator over all statements in the unit.
// Each Statement is valid only for the current iteration step.
// On error, iteration stops; call sr.Err() to retrieve the error.
//
// Usage:
//   for stmt := range reader.Statements() {
//       ...
//   }
//   if err := reader.Err(); err != nil { ... }
func (sr *StatementReader) Statements() iter.Seq[Statement]

// Err returns the first error encountered during iteration,
// or nil if iteration completed successfully.
func (sr *StatementReader) Err() error
```

#### Reading Values

```go
// ReadValue reads the current statement's value (or current pack element)
// and deserializes it into a new value of type T.
//
// For assign statements: reads the single value.
// For pack statements: reads the next element. Call within PackItems loop.
func ReadValue[T any](sr *StatementReader) (T, error)

// ReadValueInto reads the current value into an existing target.
// This enables buffer reuse in hot pack-processing loops.
func ReadValueInto[T any](sr *StatementReader, target *T) error

// Skip advances past the current statement or pack element without
// allocating or deserializing. Use for unknown/unwanted statements.
func (sr *StatementReader) Skip() error
```

#### Pack Iteration

```go
// PackItems returns an iterator over the elements of a pack statement.
// Each element is deserialized into type T.
// On error, iteration stops; call sr.Err() to retrieve the error.
//
// Early break: if the caller breaks out of the loop, the iterator
// drains the remaining pack elements (without deserializing them)
// so the reader is positioned at the next statement.
//
// Usage:
//   for stmt := range reader.Statements() {
//       if stmt.IsPack {
//           for item := range PackItems[LogEvent](reader) {
//               process(item)
//           }
//           if err := reader.Err(); err != nil { ... }
//       }
//   }
func PackItems[T any](sr *StatementReader) iter.Seq[T]

// PackItemsInto returns an iterator that reuses a caller-provided buffer.
// On each iteration, the buffer is populated with the next element.
// The yielded pointer aliases the buffer — do not retain across iterations.
// Early break drains remaining pack elements.
func PackItemsInto[T any](sr *StatementReader, buf *T) iter.Seq[*T]
```

#### Complete Tier 1 Example

```go
func processUnit(r io.Reader) error {
    sr := encoding.NewStatementReader(r)
    defer sr.Close()

    for stmt := range sr.Statements() {
        switch stmt.Name {
        case "name":
            name, err := encoding.ReadValue[string](sr)
            if err != nil { return err }
            fmt.Println("Name:", name)

        case "config":
            cfg, err := encoding.ReadValue[Config](sr)
            if err != nil { return err }
            startServer(cfg)

        case "events":
            for event := range encoding.PackItems[LogEvent](sr) {
                ingest(event)
            }
            if err := sr.Err(); err != nil { return err }

        default:
            sr.Skip()
        }
    }
    return sr.Err()
}
```

### 9.4 Tier 2: Whole-Unit Materialization

Sugar over Tier 1. Reads all statements and maps to struct fields.

```go
// Unmarshal deserializes a complete PAKT unit from bytes into a struct.
// This is convenience sugar over StatementReader.
func Unmarshal[T any](data []byte, opts ...Option) (T, error)

// UnmarshalFrom deserializes a complete PAKT unit from a reader.
func UnmarshalFrom[T any](r io.Reader, opts ...Option) (T, error)
```

**Key difference from current API:** Returns the value instead of requiring a pre-allocated pointer. Uses generics to infer the return type. The pointer-based `UnmarshalInto` variant exists for buffer reuse:

```go
// UnmarshalInto deserializes into an existing value.
// Useful when reusing buffers or populating embedded structs.
func UnmarshalInto[T any](data []byte, target *T, opts ...Option) error
```

#### Struct Tags

```go
type Config struct {
    Host    string        `pakt:"host"`
    Port    int           `pakt:"port"`
    Debug   bool          `pakt:"debug,omitempty"`
    Labels  []string      `pakt:"labels"`
    Meta    map[string]string `pakt:"meta"`
    Secret  string        `pakt:"-"`           // skip
}
```

Tag syntax: `pakt:"name[,option]..."` where options are:
- `omitempty` — omit during marshal when zero
- `-` — skip field entirely

#### Whole-Unit Example

```go
type Deployment struct {
    Name    string            `pakt:"name"`
    Version [3]int            `pakt:"version"`  // tuple → fixed array
    Config  DeployConfig      `pakt:"config"`
    Metrics map[string]float64 `pakt:"metrics"`
}

dep, err := encoding.Unmarshal[Deployment](data)
```

### 9.5 Tier 3: Custom Value Converters

Custom converters receive a scoped `ValueReader` — not the full `StatementReader`. This gives them exactly enough API to read one value (scalar or composite) without access to statement-level navigation.

```go
// ValueReader is a scoped view of the stream, positioned at a single value.
// It provides read access for scalars and navigation for composites.
// A ValueReader is only valid for the duration of the converter call.
type ValueReader struct { /* unexported: wraps *StatementReader */ }

// --- Scalar access (only valid when positioned at a scalar) ---
func (vr *ValueReader) StringValue() (string, error)
func (vr *ValueReader) IntValue() (int64, error)
func (vr *ValueReader) DecValue() (string, error)   // string to preserve precision
func (vr *ValueReader) FloatValue() (float64, error)
func (vr *ValueReader) BoolValue() (bool, error)
func (vr *ValueReader) BytesValue() ([]byte, error)
func (vr *ValueReader) IsNil() bool

// --- Composite navigation ---
func (vr *ValueReader) StructFields() iter.Seq[FieldEntry]
func (vr *ValueReader) ListElements() iter.Seq[ValueReader]
func (vr *ValueReader) MapEntries() iter.Seq[MapValueEntry]
func (vr *ValueReader) TupleElements() iter.Seq[TupleValueEntry]

// --- Delegated deserialization (for child values) ---
// ReadAs deserializes the current child value using the framework's
// type mapping, converters, and options. This is how converters compose.
func ReadAs[T any](vr *ValueReader) (T, error)

// --- Skip ---
func (vr *ValueReader) Skip() error

// --- Error ---
func (vr *ValueReader) Err() error

type MapValueEntry struct {
    Key   ValueReader
    Value ValueReader
}

type TupleValueEntry struct {
    Index int
    Type  Type
}
```

```go
// ValueConverter converts PAKT values to/from a specific Go type.
// Implementations receive a scoped ValueReader positioned at the value,
// not the full StatementReader.
type ValueConverter[T any] interface {
    // FromPakt reads a PAKT value and returns T.
    // The ValueReader is positioned at the start of the value.
    // The converter MUST consume exactly one complete value.
    FromPakt(vr *ValueReader, paktType Type) (T, error)

    // ToPakt writes a value of type T to the encoder.
    ToPakt(enc *Encoder, value T) error
}
```

#### Registration

```go
// RegisterConverter registers a ValueConverter for type T.
// When deserializing into T, the converter is used instead of
// the default reflection-based mapping.
func RegisterConverter[T any](c ValueConverter[T]) Option

// Usage:
sr := encoding.NewStatementReader(r,
    encoding.RegisterConverter[Instant](InstantConverter{}),
    encoding.RegisterConverter[IPAddr](IPAddrConverter{}),
)
```

#### Field-Level Override

For per-field converters, use a struct tag + registration:

```go
type Config struct {
    // Use a custom converter for this specific field
    Endpoint URL `pakt:"endpoint,converter=url"`
}

// Register with a name that matches the tag
sr := encoding.NewStatementReader(r,
    encoding.RegisterNamedConverter("url", URLConverter{}),
)
```

#### Converter Example: Validated Endpoint

```go
type EndpointConverter struct{}

func (EndpointConverter) FromPakt(vr *ValueReader, pt Type) (Endpoint, error) {
    var ep Endpoint

    for field := range vr.StructFields() {
        switch field.Name {
        case "url":
            raw, err := ReadAs[string](vr)
            if err != nil { return ep, err }
            u, err := url.Parse(raw)
            if err != nil { return ep, fmt.Errorf("invalid URL: %w", err) }
            if u.Scheme != "https" {
                return ep, fmt.Errorf("endpoint must use HTTPS, got %s", u.Scheme)
            }
            ep.URL = u

        case "timeout":
            secs, err := ReadAs[int64](vr)
            if err != nil { return ep, err }
            ep.Timeout = time.Duration(secs) * time.Second

        case "retries":
            n, err := ReadAs[int](vr)
            if err != nil { return ep, err }
            if n < 0 || n > 10 {
                return ep, fmt.Errorf("retries must be 0-10, got %d", n)
            }
            ep.Retries = n

        default:
            vr.Skip()
        }
    }
    if err := vr.Err(); err != nil { return ep, err }
    return ep, nil
}
```

#### Composite Navigation Helpers

These are methods on `ValueReader` (shown above) and also available as free functions for the `StatementReader` level:

```go
// StructFields returns an iterator over the fields of a struct value.
// Each FieldEntry provides the field name and declared type.
// The caller reads each field's value via ReadAs or Skip.
// Errors stop iteration; call sr.Err() after.
func StructFields(sr *StatementReader) iter.Seq[FieldEntry]

type FieldEntry struct {
    Name string
    Type Type
}

// ListElements returns an iterator over elements of a list value.
// Errors stop iteration; call sr.Err() after.
func ListElements[T any](sr *StatementReader) iter.Seq[T]

// MapEntries returns an iterator over key-value pairs of a map value.
// K is not constrained to comparable — iteration doesn't require hashing.
// Errors stop iteration; call sr.Err() after.
func MapEntries[K, V any](sr *StatementReader) iter.Seq[MapEntry[K, V]]

type MapEntry[K, V any] struct {
    Key   K
    Value V
}

// TupleElements returns an iterator for heterogeneous tuples.
// Each entry provides the index and type; the caller reads each
// element with ReadAs of the appropriate type.
func TupleElements(sr *StatementReader) iter.Seq[TupleEntry]

type TupleEntry struct {
    Index int
    Type  Type
}
```

### 9.6 Options & Policies

```go
type Option func(*options)

// UnknownFieldPolicy controls behavior when PAKT data contains
// fields not present in the target struct.
func UnknownFields(policy FieldPolicy) Option

type FieldPolicy int
const (
    SkipUnknown  FieldPolicy = iota  // default: silently skip
    ErrorUnknown                      // return error on unknown field
)

// MissingFieldPolicy controls behavior when the target struct has
// fields not present in the PAKT data.
func MissingFields(policy MissingPolicy) Option

type MissingPolicy int
const (
    ZeroMissing  MissingPolicy = iota  // default: use zero value
    ErrorMissing                        // return error on missing field
)

// DuplicatePolicy controls behavior when PAKT data contains
// duplicate statement names or map keys.
func Duplicates(policy DuplicatePolicy) Option

type DuplicatePolicy int
const (
    LastWins    DuplicatePolicy = iota  // default: last value wins
    FirstWins                            // first value kept
    ErrorDupes                           // return error on duplicate
    Accumulate                           // append to collection (target must be slice/map)
)
```

### 9.7 Error Design

```go
// DeserializeError wraps a parse error with deserialization context.
type DeserializeError struct {
    Pos       Pos        // source position in the PAKT data
    Statement string     // which statement (e.g., "config")
    Field     string     // which field within a composite (e.g., "port")
    Message   string     // human-readable description
    Err       error      // wrapped underlying error (ParseError, type mismatch, etc.)
}

func (e *DeserializeError) Error() string {
    // "config.port (3:12): int64 overflow: value 999999999999999999999"
}
func (e *DeserializeError) Unwrap() error { return e.Err }
```

---

## 10. .NET API Design

### 10.1 Namespace & Assembly Structure

```
Pakt/
    PaktReader.cs              # Tier 0: token-level reader (exists, ref struct)
    PaktStatementReader.cs     # Tier 1: statement-level streaming
    PaktSerializer.cs          # Tier 2: whole-unit materialization
    Serialization/
        PaktSerializerContext.cs    # source-gen context base
        PaktTypeInfo.cs            # per-type metadata + delegates
        PaktConverter.cs           # Tier 3: custom converter base
        PaktConverterAttribute.cs  # field-level converter binding
        PaktPropertyAttribute.cs   # field name/order/ignore
        DeserializeOptions.cs      # policies
```

### 10.2 Tier 0: PaktReader (unchanged concept)

The low-level token reader. A `ref struct` backed by `ReadOnlySpan<byte>`. Exists today.

```csharp
public ref struct PaktReader
{
    public bool Read();
    public PaktTokenType TokenType { get; }
    public PaktScalarType ScalarType { get; }
    public string? StatementName { get; }
    public PaktType? StatementType { get; }
    // ... scalar accessors: GetString(), GetInt64(), etc.
    public void Dispose();
}
```

### 10.3 Tier 1: PaktStatementReader — The Primary API

A higher-level reader that operates at the statement level. Unlike the raw `PaktReader`, this type is not a `ref struct` — it can be stored, passed, and used with `IAsyncEnumerable`.

```csharp
/// <summary>
/// Reads PAKT statements one at a time from a stream.
/// This is the primary deserialization interface.
/// </summary>
public sealed class PaktStatementReader : IDisposable, IAsyncDisposable
{
    // --- Construction ---

    public static PaktStatementReader Create(
        ReadOnlySpan<byte> data,
        PaktSerializerContext context,
        DeserializeOptions? options = null);

    public static PaktStatementReader Create(
        Stream stream,
        PaktSerializerContext context,
        DeserializeOptions? options = null);

    // --- Statement Navigation ---

    /// <summary>
    /// Advances to the next statement. Returns false when the unit is exhausted.
    /// </summary>
    public bool ReadStatement();

    /// <summary>
    /// Async variant for stream-backed readers.
    /// </summary>
    public ValueTask<bool> ReadStatementAsync(CancellationToken ct = default);

    /// <summary>Current statement name (e.g., "server", "events").</summary>
    public string StatementName { get; }

    /// <summary>Current statement's declared PAKT type.</summary>
    public PaktType StatementType { get; }

    /// <summary>True if the current statement uses pack syntax (<<).</summary>
    public bool IsPack { get; }

    // --- Value Reading ---

    /// <summary>
    /// Deserialize the current statement's value (or current pack element) as T.
    /// </summary>
    public T ReadValue<T>();

    /// <summary>
    /// Skip the current statement or pack element without allocating.
    /// </summary>
    public void Skip();

    // --- Pack Iteration ---

    /// <summary>
    /// Returns an enumerable of pack elements, deserialized as T.
    /// </summary>
    public IEnumerable<T> ReadPack<T>();

    /// <summary>
    /// Returns an async enumerable of pack elements for stream-backed readers.
    /// </summary>
    public IAsyncEnumerable<T> ReadPackAsync<T>(CancellationToken ct = default);

    // --- Resource Management ---
    public void Dispose();
    public ValueTask DisposeAsync();
}
```

#### Complete Tier 1 Example

```csharp
await using var reader = PaktStatementReader.Create(stream, AppContext.Default);

while (await reader.ReadStatementAsync())
{
    switch (reader.StatementName)
    {
        case "name":
            var name = reader.ReadValue<string>();
            Console.WriteLine($"Name: {name}");
            break;

        case "config":
            var cfg = reader.ReadValue<ServerConfig>();
            StartServer(cfg);
            break;

        case "events":
            await foreach (var evt in reader.ReadPackAsync<LogEvent>())
            {
                Ingest(evt);
            }
            break;

        default:
            reader.Skip();
            break;
    }
}
```

### 10.4 Tier 2: Whole-Unit Materialization

Static convenience methods. Sugar over Tier 1.

```csharp
public static class PaktSerializer
{
    /// <summary>
    /// Deserialize a complete PAKT unit into T.
    /// </summary>
    public static T Deserialize<T>(
        ReadOnlySpan<byte> data,
        PaktSerializerContext context,
        DeserializeOptions? options = null);

    /// <summary>
    /// Deserialize from a stream.
    /// </summary>
    public static ValueTask<T> DeserializeAsync<T>(
        Stream stream,
        PaktSerializerContext context,
        DeserializeOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Serialize T into a PAKT unit.
    /// </summary>
    public static byte[] Serialize<T>(
        T value,
        PaktSerializerContext context,
        string statementName);
}
```

#### Source-Generated Context

```csharp
[PaktSerializable(typeof(ServerConfig))]
[PaktSerializable(typeof(LogEvent))]
[PaktSerializable(typeof(Deployment))]
public partial class AppContext : PaktSerializerContext { }

// Generated by source generator:
// - PaktTypeInfo<ServerConfig> with Deserialize/Serialize delegates
// - PaktTypeInfo<LogEvent> with Deserialize/Serialize delegates
// - etc.
// - GetTypeInfo<T>() override dispatching to the correct info
// - Default static singleton
```

The source generator uses **partial constructors** (C# 14) to inject initialization:

```csharp
// Generated code
public partial class AppContext
{
    // C# 14 partial constructor — generator provides the body
    public partial AppContext()
    {
        // Initialize type info cache
        _serverConfig = CreateServerConfigTypeInfo();
        _logEvent = CreateLogEventTypeInfo();
        // ...
    }
}
```

#### Type Configuration Attributes

```csharp
public class ServerConfig
{
    [PaktProperty("host")]               // explicit PAKT name
    public string HostName { get; set; }

    public int Port { get; set; }         // default: "port" (lowercase first char)

    [PaktIgnore]                          // excluded from serialization
    public string InternalId { get; set; }

    [PaktPropertyOrder(0)]               // explicit serialization order
    public string Region { get; set; }

    [PaktConverter(typeof(InstantConverter))]  // per-field custom converter
    public Instant CreatedAt { get; set; }
}
```

### 10.5 Tier 3: Custom Converters

Custom converters receive the raw `PaktReader` (for zero-alloc reads) plus a `PaktConvertContext` that provides access to nested deserialization (for composing with the framework).

```csharp
/// <summary>
/// Base class for custom PAKT value converters.
/// Converters participate in the stream — they read from the reader directly.
/// </summary>
public abstract class PaktConverter<T>
{
    /// <summary>
    /// Read a PAKT value from the reader and return T.
    /// The reader is positioned at the start of the value.
    /// The converter MUST consume exactly one complete value.
    /// Use context.ReadAs&lt;U&gt;() to delegate child value deserialization
    /// back to the framework (enables converter composition).
    /// </summary>
    public abstract T Read(ref PaktReader reader, PaktType declaredType, PaktConvertContext context);

    /// <summary>
    /// Write a value of type T to the writer.
    /// </summary>
    public abstract void Write(PaktWriter writer, T value);
}

/// <summary>
/// Provides deserialization context to custom converters.
/// Enables converters to delegate child value deserialization
/// back to the framework (including other registered converters).
/// </summary>
public readonly ref struct PaktConvertContext
{
    /// <summary>
    /// Deserialize a child value as U using the framework's type mapping,
    /// registered converters, and options.
    /// </summary>
    public U ReadAs<U>(ref PaktReader reader);

    /// <summary>Skip the current value without deserializing.</summary>
    public void Skip(ref PaktReader reader);

    /// <summary>Access to the serializer context for type info lookup.</summary>
    public PaktSerializerContext SerializerContext { get; }
}
```

#### Registration

Two levels of precedence (highest first):

```csharp
// 1. Per-field: via attribute
public class Config
{
    [PaktConverter(typeof(UrlConverter))]
    public Uri Endpoint { get; set; }
}

// 2. Per-type: via context options
var options = new DeserializeOptions
{
    Converters = { new InstantConverter(), new IPAddressConverter() }
};
```

#### Converter Example: Validated Endpoint

```csharp
public class EndpointConverter : PaktConverter<Endpoint>
{
    public override Endpoint Read(ref PaktReader reader, PaktType declaredType, PaktConvertContext context)
    {
        var ep = new Endpoint();

        // Expect struct start
        reader.Read(); // StructStart

        while (reader.Read())
        {
            if (reader.TokenType == PaktTokenType.StructEnd) break;

            switch (reader.CurrentName)
            {
                case "url":
                    reader.Read();
                    var raw = reader.GetString();
                    ep.Url = new Uri(raw);
                    if (ep.Url.Scheme != "https")
                        throw new PaktException("endpoint must use HTTPS");
                    break;

                case "timeout":
                    reader.Read();
                    ep.Timeout = TimeSpan.FromSeconds(reader.GetInt64());
                    break;

                case "retries":
                    reader.Read();
                    var n = (int)reader.GetInt64();
                    if (n is < 0 or > 10)
                        throw new PaktException($"retries must be 0-10, got {n}");
                    ep.Retries = n;
                    break;

                default:
                    context.Skip(ref reader);  // use context for skip
                    break;
            }
        }

        return ep;
    }
}
```

#### Composite Navigation Helpers

Extension methods (using C# 14 extension members) for use in custom converters:

```csharp
public static class PaktReaderExtensions
{
    extension(ref PaktReader reader)
    {
        /// <summary>
        /// Enumerate struct fields. Yields (name, type) pairs.
        /// Caller reads each field's value via reader methods or Skip.
        /// </summary>
        public IEnumerable<PaktFieldEntry> StructFields()
        {
            while (reader.Read() && reader.TokenType != PaktTokenType.StructEnd)
                yield return new(reader.CurrentName!, reader.CurrentType!);
        }

        /// <summary>
        /// Enumerate list elements as T.
        /// </summary>
        public IEnumerable<T> ListElements<T>(PaktSerializerContext ctx)
        {
            while (reader.Read() && reader.TokenType != PaktTokenType.ListEnd)
                yield return ctx.GetTypeInfo<T>()!.Deserialize!(ref reader);
        }

        /// <summary>
        /// Skip the current value (scalar or composite) entirely.
        /// </summary>
        public void SkipValue() { /* depth-aware skip */ }
    }
}
```

### 10.6 Options & Policies

```csharp
public sealed class DeserializeOptions
{
    /// <summary>
    /// How to handle unknown fields in PAKT data.
    /// Default: Skip.
    /// </summary>
    public UnknownFieldPolicy UnknownFields { get; init; } = UnknownFieldPolicy.Skip;

    /// <summary>
    /// How to handle missing fields (target has fields data doesn't).
    /// Default: ZeroValue.
    /// </summary>
    public MissingFieldPolicy MissingFields { get; init; } = MissingFieldPolicy.ZeroValue;

    /// <summary>
    /// How to handle duplicate statement names.
    /// Default: LastWins.
    /// </summary>
    public DuplicatePolicy Duplicates { get; init; } = DuplicatePolicy.LastWins;

    /// <summary>
    /// Custom converters registered by target CLR type.
    /// </summary>
    public IList<object> Converters { get; } = new List<object>();
}

public enum UnknownFieldPolicy { Skip, Error }
public enum MissingFieldPolicy { ZeroValue, Error }
public enum DuplicatePolicy { LastWins, FirstWins, Error, Accumulate }
```

### 10.7 Error Design

```csharp
public class PaktDeserializeException : PaktException
{
    public string? StatementName { get; }
    public string? FieldName { get; }
    public PaktPosition Position { get; }

    // "config.port (3:12): Int64 overflow: value 999999999999999999999"
    public override string Message { get; }
}
```

---

## 11. Cross-Cutting Design Patterns

### 11.1 Streaming Architecture Invariant

Both APIs enforce the same invariant:

> **Every tier reads from the same stream, in order, without buffering.** Materialization loops the streaming primitives. Custom converters read from the stream themselves.

In Go, this is achieved by having `Unmarshal` internally create a `StatementReader` and iterate it. In .NET, `PaktSerializer.Deserialize` internally creates a `PaktStatementReader`.

### 11.2 Type Metadata Caching

| Concern | Go | .NET |
|---------|-----|------|
| Field mapping | `sync.Map` keyed by `reflect.Type` | Source-generated `PaktTypeInfo<T>` |
| Field lookup | `map[string]*fieldInfo` (per-type) | Generated `switch` on field name |
| Type inference | `typeOfReflect(reflect.Type) Type` at runtime | `TypeModelBuilder` at compile-time |
| Converter lookup | Options chain checked at call site | Options chain checked at call site |

### 11.3 Pack Processing Comparison

| Pattern | Go | .NET |
|---------|-----|------|
| Iterate | `for item, err := range PackItems[T](sr)` | `foreach (var item in reader.ReadPack<T>())` |
| Async iterate | N/A (use goroutine + channel if needed) | `await foreach (var item in reader.ReadPackAsync<T>())` |
| Buffer reuse | `PackItemsInto[T](sr, &buf)` | Not needed (struct value types are stack-allocated) |
| Early exit | `break` in range loop (yield returns false) | `break` in foreach (IEnumerable disposes) |

### 11.4 Custom Converter Comparison

| Concern | Go | .NET |
|---------|-----|------|
| Interface | `ValueConverter[T]` (generic interface) | `PaktConverter<T>` (abstract class) |
| Receives | `*ValueReader` (scoped) + `Type` | `ref PaktReader` + `PaktType` + `PaktConvertContext` |
| Child dispatch | `ReadAs[U](vr)` free function | `context.ReadAs<U>(ref reader)` method |
| Per-field | `pakt:"field,converter=name"` tag | `[PaktConverter(typeof(...))]` attribute |
| Per-type | `RegisterConverter[T](c)` option | `options.Converters.Add(c)` |

---

## 12. Open Questions (Updated)

### Q1. Go: Should StatementReader be an interface?

An interface would allow mock implementations for testing. But concrete types are idiomatic Go and enable inlining. **Recommendation:** Concrete type. Provide a test helper that creates a `StatementReader` from a string.

### Q2. .NET: Streaming invariant for async paths

The `PaktReader` is a `ref struct` (stack-only, zero-alloc). The `PaktStatementReader` needs to support `IAsyncEnumerable` for pack iteration, which requires heap state. The current design has `PaktStatementReader` as a class that internally manages the reader lifecycle.

**Concern:** Async state machines can't hold `ref struct` fields. The `PaktStatementReader` must buffer at least one token's worth of state to bridge between its internal `PaktReader` and the async enumeration pattern.

**Recommendation:** Accept this single-token bridge buffer as an implementation detail. The streaming invariant holds at the semantic level: callers still see one value at a time, and memory is O(nesting depth). The `ref struct PaktReader` remains available as the Tier 0 escape hatch for true zero-alloc synchronous scenarios.

### Q3. Go: Scanner pattern — RESOLVED

Use `iter.Seq[T]` with `sr.Err() error` checked after the loop. This is the scanner pattern, consistent with `bufio.Scanner` and idiomatic Go.

### Q4. Go: Early break in pack iterators — RESOLVED

When a caller breaks out of a `PackItems` loop, the iterator drains the remaining pack elements (skipping without deserializing) so the reader is positioned at the next statement. This is necessary to maintain the streaming invariant.

### Q5. Both: Converter composition — RESOLVED

Custom converters compose by delegating child values back to the framework:
- **Go:** `ReadAs[U](vr)` free function on `ValueReader`
- **.NET:** `context.ReadAs<U>(ref reader)` on `PaktConvertContext`

This enables a converter for `Config` to delegate its `Server` field to the framework (which may invoke another converter), without the parent converter needing to know about child converters.

### Q6. .NET: Should ReadPack return IEnumerable or a custom type?

`IEnumerable<T>` is universal but boxes value types. A custom `PaktPackEnumerable<T>` struct could avoid allocation.

**Recommendation:** Return `IEnumerable<T>` for simplicity. The per-element deserialization cost dwarfs enumerator allocation. For the async path, `IAsyncEnumerable<T>` is required.

### Q7. Map Pack Streaming

Top-level map packs (`data:<str;int> << 'a';1\n'b';2`) should be consumable through the same Tier 1 API. The pack iterator yields `MapEntry[K,V]` for map packs and `T` for list packs. The `Statement.Type` tells the caller which kind of pack it is.

**Go:**
```go
for stmt := range sr.Statements() {
    if stmt.IsPack && stmt.Type.Kind() == TypeMap {
        for entry := range PackItems[MapEntry[string, int]](sr) {
            fmt.Printf("%s = %d\n", entry.Key, entry.Value)
        }
    }
}
```

**.NET:**
```csharp
if (reader.IsPack && reader.StatementType.IsMap)
{
    foreach (var entry in reader.ReadPack<KeyValuePair<string, int>>())
        Console.WriteLine($"{entry.Key} = {entry.Value}");
}
```

### Q8. Behavior for `any`/`object`/interface targets

Since there is no dynamic document model, attempting to deserialize into `any` (Go) or `object` (.NET) should be an error. The caller must always provide a concrete target type.

**Recommendation:** Return a clear error: "cannot deserialize into interface type; provide a concrete target type."



