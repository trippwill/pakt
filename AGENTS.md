# AGENTS.md — PAKT Repository

This file provides guidance for AI coding agents (e.g., GitHub Copilot) working on issues and pull requests in this repository.

## Repository Structure

```
pakt/
├── encoding/            # Core Go library — streaming decoder, encoder, marshal/unmarshal
│   ├── reader.go        # Byte-level I/O, scalar readers
│   ├── reader_type.go   # Recursive-descent type annotation parser
│   ├── reader_state.go  # State machine: parserState enum, frame stack, step() loop
│   ├── decoder.go       # Public Decoder API
│   ├── event.go         # Event types and EventKind enum
│   ├── types.go         # PAKT type system (TypeKind, Type interface, composites)
│   ├── errors.go        # ParseError with Pos and ErrorCode (spec §11)
│   ├── encoder.go       # PAKT output writer
│   ├── marshal.go       # Go struct → PAKT text
│   ├── unmarshal.go     # PAKT text → Go struct
│   ├── tags.go          # Struct tag parsing (pakt:"name")
│   └── *_test.go        # Tests for each component
├── dotnet/              # .NET library (net10.0)
│   ├── src/Pakt/        # Core library
│   │   ├── PaktReader.cs           # Tier 0: ref struct token reader (state machine)
│   │   ├── PaktMemoryReader.cs     # Tier 1: sync statement reader (memory-backed)
│   │   ├── PaktStreamReader.cs     # Tier 1: async statement reader (stream-backed)
│   │   ├── PaktFramedSource.cs     # Internal: NUL-aware async buffer for stream reader
│   │   ├── PaktSerializer.cs       # Tier 2: convenience Deserialize/DeserializeAsync/Serialize
│   │   ├── PaktUnitMaterializer.cs # Whole-unit binding (sync + async)
│   │   ├── PaktReaderExtensions.cs # Callback-based composite navigation helpers
│   │   ├── PaktWriter.cs           # Forward-only PAKT output writer
│   │   └── Serialization/          # Runtime: converters, options, type info, deserialization
│   ├── src/Pakt.Generators/        # Source generator (netstandard2.0)
│   ├── tests/                      # xUnit tests
│   └── benchmarks/                 # BenchmarkDotNet suites (FS, Fin, Small, Wide, Deep, Collections)
├── main.go              # CLI entry point (Kong)
├── cli.go               # CLI commands: parse, validate, version
├── cli_test.go          # CLI integration tests (build binary, run against testdata)
├── spec/pakt-v0.md      # Formal PAKT v0 specification
├── spec/benchmarks-v0.md # Cross-platform benchmark specification
├── docs/guide.md        # Human-friendly PAKT guide
├── design/              # Architecture documents
│   └── state-machine-rewrite.md  # Decoder state machine design
├── site/                # Hugo website (usepakt.dev)
├── testdata/            # Sample .pakt files
│   ├── valid/           # Valid units for testing
│   └── invalid/         # Invalid units for error testing
└── .github/
    ├── workflows/ci.yml # CI: build, test, lint, coverage
    ├── copilot-instructions.md
    └── agents/          # Specialized agent definitions
```

## Commands

```sh
go build ./...                         # Build all packages
go test ./... -count=1 -race           # Run tests with race detector
golangci-lint run                      # Lint (v2 config in .golangci.yml)
go run . parse testdata/valid/full.pakt        # Run CLI parse
go run . validate testdata/valid/full.pakt     # Run CLI validate
```

## Architecture: Streaming State-Machine Decoder

The decoder in `encoding/` uses an explicit-stack state machine rather than recursive descent for value parsing. This enables:

- **True streaming**: one event per `Decode()` call, zero buffering
- **Constant memory per nesting level**: explicit `frame` stack instead of Go call stack
- **Immediate event emission**: caller sees events as soon as they are parseable

### How it works

1. `Decoder.Decode()` calls `stateMachine.step()`
2. `step()` loops through `parserState` values in a `switch`
3. Each state either **yields** an event (returns) or **transitions** (continues loop)
4. Composite types push a `frame` onto the stack with their resume state
5. When a child value completes, the frame is popped and execution resumes at the parent's saved state

### Key files for decoder work

- `reader_state.go` — All state definitions, frame type, `step()` implementation
- `reader.go` — Helper actions called within state transitions (byte I/O, scalar reads, whitespace)
- `reader_type.go` — Type annotation parser (recursive descent, bounded depth — stays separate from state machine)
- `design/state-machine-rewrite.md` — Full design doc with state transition narrative, frame payloads, risky areas, and observable behavior contract

## Architecture: .NET Two-Reader Model

The .NET library uses a layered architecture with two distinct Tier 1 readers:

### Tier 0: `PaktReader` (ref struct)

Stack-only, zero-copy tokenizer over `ReadOnlySpan<byte>`. Same state-machine design as the Go decoder but adapted for .NET's ref struct constraints. Source-generated deserializers operate at this level for maximum performance.

### Tier 1: Two readers

- **`PaktMemoryReader`** — Sync-only, `IDisposable`. For `ReadOnlyMemory<byte>` or `IMemoryOwner<byte>` input. No artificial async. The memory-backed fast path.
- **`PaktStreamReader`** — Async-only, `IAsyncDisposable`. Real `Stream.ReadAsync` at I/O refill boundaries. Uses `PaktFramedSource` internally for NUL-delimited unit framing with correct leftover handling. No sync wrappers.

The two readers share no interface or base class. This is intentional — async exists only where the underlying code path is genuinely async.

### Tier 2: `PaktSerializer`

Static convenience API: `Deserialize<T>(ReadOnlyMemory<byte>)` (sync, uses `PaktMemoryReader`), `DeserializeAsync<T>(Stream)` (async, uses `PaktStreamReader`), `Serialize<T>`. Sugar over Tier 1.

### Key design rules

- No fake async adapters. Async only on `PaktStreamReader` where `Stream.ReadAsync` is real.
- `IMemoryOwner<byte>` is the canonical ownership transfer mechanism.
- Source-generated code targets `PaktReader` directly for zero-alloc scalar reads.
- `PaktConvertContext` is a `readonly ref struct` — no heap allocation for converter context.

## Spec Compliance

The specification (`spec/pakt-v0.md`) is the authoritative source for PAKT semantics. When working on the parser or event model:

- Error codes must match spec §11 categories (`ErrorCode` in `errors.go`)
- Event ordering must follow the contract in the spec and `design/state-machine-rewrite.md`
- Duplicate statement names and map keys are preserved in decode order (spec design principle §0.1.3)
- Type annotations are producer assertions checked at parse time
- Nil is only valid for nullable types (`?` suffix)

## Design Priorities

PAKT is pre-release. Backward compatibility is not a concern. The priorities are:

1. **Correctness** — the implementation matches the spec
2. **Performance** — minimal allocation, streaming-first
3. **Ergonomics** — clean, discoverable APIs
4. **Consistency** — uniform design within each library; each library should be idiomatic for its ecosystem (Go idioms in Go, .NET idioms in .NET) except where that conflicts with a higher priority

When these conflict, higher-numbered priorities yield to lower-numbered ones.

## Testing Requirements

- **All tests must pass with `-race`** before submitting changes
- **Table-driven tests** are preferred in `encoding/`
- **Regression tests required** for bug fixes
- **New features need tests** — both positive and negative cases
- **CLI tests** (`cli_test.go`) build the binary and run against `testdata/` files
- **`testdata/valid/`** — Add new `.pakt` files for valid syntax coverage
- **`testdata/invalid/`** — Add new `.pakt` files for error case coverage

## Error Handling

- Use `ParseError` constructors: `Errorf(pos, format, args...)` or `Wrapf(pos, code, format, args...)`
- Always include `Pos{Line, Col}` — positions must match what the user would see in their editor
- Use `ErrorCode` sentinels for spec-defined error categories
- Callers check errors with `errors.Is(err, encoding.ErrTypeMismatch)` etc.

## CI Pipeline

The CI workflow (`.github/workflows/ci.yml`) runs on every push to `main` and on PRs:

1. **Build** — `go build ./...` (matrix: ubuntu-latest, macos-latest)
2. **Test** — `go test ./... -count=1 -race -coverprofile=coverage.out`
3. **Lint** — `golangci-lint-action@v7`
4. **Coverage summary** — Extracts total coverage percentage

## Resource Management

- `bufio.Reader` instances are pooled via `sync.Pool` — always call `release()` or `Close()`
- `stateMachine` instances are pooled — always call `release()`
- `Decoder.Close()` handles both; callers should `defer dec.Close()`

## Style Notes

- Go 1.25; follow standard Go idioms
- Exported names have doc comments
- Errors wrapped with context: `fmt.Errorf("opening spec: %w", err)`
- No magic numbers — use named constants
- golangci-lint v2 config enforces: govet, errcheck, staticcheck, unused, ineffassign, misspell
