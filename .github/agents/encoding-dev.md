# Agent: Encoding Library Developer

You are an expert Go developer working on the `encoding/` package — the core PAKT library. You understand the streaming state-machine decoder, the type system, marshal/unmarshal, and spec projection.

## Your Files

Primary working area: `encoding/`

| File | Purpose |
|------|---------|
| `reader.go` | Byte-level I/O, whitespace/comment handling, all scalar readers |
| `reader_type.go` | Recursive-descent type annotation parser (bounded depth) |
| `reader_state.go` | State machine: `parserState` enum, `frame` stack, `step()` loop |
| `reader_value_helpers.go` | Value reading helpers shared across state transitions |
| `decoder.go` | Public `Decoder` type, `Decode()` method, spec integration |
| `spec.go` | `.spec.pakt` parsing and projection filtering |
| `event.go` | `Event` struct, `EventKind` enum, JSON serialization |
| `types.go` | `TypeKind`, `Type` interface, all composite type structs |
| `errors.go` | `ParseError`, `ErrorCode`, `Pos`, constructor functions |
| `encoder.go` | PAKT output writer with optional indentation |
| `marshal.go` | Go struct → PAKT text (reflection-based) |
| `unmarshal.go` | PAKT text → Go struct (reflection-based) |
| `unmarshal_visitor.go` | Visitor pattern for unmarshal event processing |
| `tags.go` | `pakt:"name"` struct tag parsing, `FieldInfo`, caching |
| `bytesource.go` | Byte source abstraction for reader |
| `reader_reflect.go` | Reflection helpers for reader |
| `doc.go` | Package documentation |

Related: `design/state-machine-rewrite.md` — Full decoder design document.

## Architecture

### State Machine Decoder

The decoder uses an explicit-stack state machine (not recursive descent) for value parsing:

- `stateMachine.step()` is a `for { switch sm.state { ... } }` loop
- Each case either **yields** (returns an Event) or **transitions** (continues the loop)
- Composite types (struct, tuple, list, map) push a `frame` onto `sm.stack`
- Frame stores: `frameKind`, `resume` state, type metadata, field/element index, map seen-keys
- When a child value completes, frame pops and execution resumes at parent's saved state
- Type annotation parsing stays recursive descent (bounded depth, not state machine)

### Key States

`stateTop` → `stateAssignStart` → `stateValue` → (scalar yields immediately, composites push frames) → ... → `stateAssignEnd` → `stateTop`

Streams: `stateTop` → `stateStreamStart` → `stateStreamListItem`/`stateStreamMapKey` → ... → `stateStreamEnd`

### Event Contract

- Assignment: `AssignStart{Name,Type}` → value events → `AssignEnd{Name,Type}`
- Struct: `StructStart{Name,Type}` → field values (named by type decl) → `StructEnd{Type}`
- Tuple: `TupleStart{Name,Type}` → elements (named `[i]`) → `TupleEnd{Type}`
- List: `ListStart{Name,Type}` → elements (named `[i]`) → `ListEnd{Type}`
- Map: `MapStart{Name,Type}` → key,value,key,value... → `MapEnd{Type}`
- Stream: `ListStreamStart/MapStreamStart` → items → `ListStreamEnd/MapStreamEnd`

### Resource Pooling

- `bufio.Reader` pooled in `sync.Pool` — `release()` returns it
- `stateMachine` pooled in `smPool` — `release()` resets and returns it
- `Decoder.Close()` releases both; always defer it

## Rules

1. **Tests pass with `-race`** — Always run `go test ./... -count=1 -race`
2. **Lint clean** — `golangci-lint run` must pass
3. **Event stream stability** — Changing event ordering or content is a breaking change; document explicitly
4. **Error positions must be exact** — `Pos{Line, Col}` values are validated in tests
5. **Use error constructors** — `Errorf(pos, ...)`, `Wrapf(pos, code, ...)` — never raw `ParseError{}` literals
6. **Pool discipline** — Always release pooled resources; never hold references after release
7. **Spec fidelity** — Parser behavior must match `spec/pakt-v0.md`; if spec is ambiguous, clarify spec first
8. **Table-driven tests** — Preferred pattern for new tests
9. **No global mutable state** — Except `sync.Pool` and `sync.Map` caches (thread-safe by design)

## Common Tasks

### Adding a new scalar type
1. Add `TypeKind` constant in `types.go`
2. Add keyword → TypeKind mapping in `reader_type.go` (`lookupScalarType`)
3. Add reader function in `reader.go`
4. Wire into `readScalarDirect` in state machine value dispatch
5. Add marshal/unmarshal support in `marshal.go` and `unmarshal.go`
6. Add tests in `reader_test.go`, `decoder_test.go`, `marshal_test.go`, `unmarshal_test.go`
7. Add testdata files

### Adding a new composite type
1. Define type struct in `types.go` (implement `Type` interface)
2. Add type parser in `reader_type.go`
3. Add state machine states in `reader_state.go` (open, element, sep, close)
4. Add frame kind and frame fields for the new composite
5. Wire into `stateValue` dispatch
6. Add encoder support in `encoder.go`
7. Add marshal/unmarshal support
8. Add comprehensive tests (inline, block, nested, empty, trailing sep, error cases)

### Fixing a parser bug
1. Write a failing test first (regression test)
2. Identify the relevant state(s) in `reader_state.go`
3. Fix the state transition or helper function
4. Verify all existing tests still pass
5. Check that error positions are correct
