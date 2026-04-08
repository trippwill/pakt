# Copilot Instructions — PAKT

## Project Overview

PAKT is a typed data interchange format: human-authorable, streaming, self-describing. This repository contains:

- **`encoding/`** — The canonical Go library (`github.com/trippwill/pakt/encoding`). Streaming decoder, encoder, marshal/unmarshal.
- **`main.go` / `cli.go`** — CLI tool (`go install github.com/trippwill/pakt@latest`). Uses Kong for command parsing.
- **`spec/pakt-v0.md`** — Formal PAKT v0 specification (grammar, semantics, error codes).
- **`docs/guide.md`** — Human-friendly introduction to PAKT.
- **`design/`** — Architecture documents (e.g., `state-machine-rewrite.md`).
- **`site/`** — Hugo website for usepakt.dev, deployed via Cloudflare Pages.
- **`testdata/`** — Sample `.pakt` files (`valid/` and `invalid/` subdirectories).

## Architecture — encoding/ Package

The encoding package uses a **streaming state-machine decoder**:

1. **`reader.go`** — Byte-level I/O, whitespace/comment handling, scalar readers (strings, numbers, dates, etc.). Uses `bufio.Reader` with pooling.
2. **`reader_type.go`** — Recursive-descent type annotation parser. Bounded depth, not part of the state machine.
3. **`reader_state.go`** — State machine: `parserState` enum, `frame` stack type, `stateMachine.step()` loop. Each `step()` call either yields an event or transitions internally.
4. **`decoder.go`** — Public `Decoder` type wrapping the state machine. `Decode()` calls `step()` and returns one `Event` per call.
5. **`spec.go`** — Spec projection (legacy experimental; being moved to future consideration).
6. **`event.go`** — `Event` struct and `EventKind` enum. Events: `AssignStart/End`, `ListStreamStart/End`, `MapStreamStart/End`, `ScalarValue`, `StructStart/End`, `TupleStart/End`, `ListStart/End`, `MapStart/End`, `Error`.
7. **`types.go`** — PAKT type system: `TypeKind` (scalars), `Type` interface, composite types (`StructType`, `TupleType`, `ListType`, `MapType`), `AtomSetType`.
8. **`errors.go`** — `ParseError` with `Pos{Line, Col}` and `ErrorCode` matching spec §11.
9. **`encoder.go`** — Output writer with optional indentation.
10. **`marshal.go` / `unmarshal.go`** — Reflection-based Go struct ↔ PAKT conversion using `pakt:"name"` struct tags.
11. **`tags.go`** — Struct tag parsing and field metadata caching.

### Key Design Principle

The state machine uses an **explicit stack** (not Go call stack) for nesting. Memory scales with nesting depth, not unit size. Each composite type (struct, tuple, list, map) pushes a `frame` with its state. The `step()` function loops through states, yielding events via `return`.

Refer to `design/state-machine-rewrite.md` for the full state transition narrative and frame layout.

## PAKT Type System

Scalar types: `str`, `int`, `dec`, `float`, `bool`, `uuid`, `date`, `ts`, `bin`

Composite types: `{field:type, ...}` (struct), `(type, ...)` (tuple), `[type]` (list), `<keytype ; valuetype>` (map)

Nullable: any type with `?` suffix (e.g., `str?`)

Atom sets: `|a, b, c|` — enumerated string constants

Packs: `name:type << value, value, ...` — open-ended top-level sequences

## Build, Test, Lint

```sh
# Build
go build ./...

# Test (with race detector)
go test ./... -count=1 -race

# Lint (golangci-lint v2)
golangci-lint run

# Run the CLI
go run . parse testdata/valid/scalars.pakt
go run . validate testdata/valid/full.pakt
```

## Go Conventions

- **Go 1.25** (set in `go.mod` and `.mise.toml`)
- **golangci-lint v2** config in `.golangci.yml`: govet, errcheck, staticcheck, unused, ineffassign, misspell, gofmt, goimports
- Follow standard Go idioms: exported names documented, errors wrapped with `fmt.Errorf("context: %w", err)`, table-driven tests
- Parse errors use `ParseError` with `Pos` and `ErrorCode` — use `Wrapf()` or `Errorf()` constructors, not raw struct literals
- Buffer resources (readers, state machines) are pooled via `sync.Pool` — always `release()` / `Close()` them

## Testing Patterns

- **Table-driven tests** are the norm in `encoding/`
- **`decodeAll` helper** — decodes full input into `[]Event` slice for assertion
- **Golden files** in `testdata/valid/` and `testdata/invalid/` — used by CLI tests (`cli_test.go`)
- **CLI tests** build a binary via `TestMain`, then run it against testdata files
- **`encoding.test`** — Large (~5.6 MB) precompiled test binary; do not read it directly
- Always run tests with `-race` flag

## Event Stream Contract

For any assign `name:type = value`:
- `AssignStart{Name, Type}` → value events → `AssignEnd{Name, Type}`

For composites:
- `StructStart/TupleStart/ListStart/MapStart{Name, Type}` → children → corresponding `End{Type}`

For maps, children alternate: key (`ScalarValue`) → value → key → value → ...

For packs: `ListPackStart/MapPackStart` → items → `ListPackEnd/MapPackEnd`

> **Note**: The Go implementation currently uses `ListStreamStart/End` and `MapStreamStart/End` event names. These will be renamed to `ListPackStart/End` and `MapPackStart/End` in a future alignment pass.

## PR Expectations

- All tests pass with `-race`
- `golangci-lint run` clean
- Changes to the spec (`spec/pakt-v0.md`) should be reflected in `docs/guide.md` and site content
- Changes to the event model must preserve backward compatibility or be clearly documented as breaking
- New features need tests; bug fixes need regression tests
