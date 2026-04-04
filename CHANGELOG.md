# Changelog

All notable changes to this project will be documented in this file.

## [0.0.1] — 2026-04-04

Initial release of PAKT — a typed data interchange format.

### Format

- PAKT v0 draft specification (`spec/pakt-v0.md`)
- 9 scalar types: str, int, dec, float, bool, uuid, date, time, datetime
- 4 composite types: struct (named fields), tuple (positional), list (homogeneous), map (key-value)
- Atom sets for constrained enumeration values
- Nullable types via `?` suffix
- Block and inline forms with identical semantics
- Multi-line strings with indentation stripping
- Spec files (`.spec.pakt`) for consumer-side projection

### Go Library (`encoding/`)

- **Decoder**: LL(1) streaming parser with hybrid lexer/parser architecture
  - Type-directed value reading — zero ambiguity, no intermediate token allocations
  - Spec projection with fast skip (balances delimiters, no validation of skipped content)
  - UTF-8 BOM handling, `\r\n` normalization
- **Encoder**: Write PAKT to `io.Writer` with compact and pretty-print modes
- **Marshal/Unmarshal**: Struct-tag-based serialization (`pakt:"name,omitempty"`)
  - Go type → PAKT type auto-detection
  - Supports: structs, slices, maps, pointers (nullable), `time.Time`, `TextMarshaler`
- **Spec parsing**: `ParseSpec()` for `.spec.pakt` files
- Error sentinels: `ErrDuplicateName`, `ErrDuplicateKey`, `ErrUnexpectedEOF`, `ErrNilNonNullable`, `ErrTypeMismatch`
- JSON marshaling for Event types (JSON Lines CLI output)

### CLI

- `pakt parse <file>` — parse and emit streaming events
  - `--format text|json` — tab-separated text (default) or JSON Lines
  - `--spec <file.spec.pakt>` — apply consumer projection
  - Reads from stdin with `-`
  - Env vars: `PAKT_SPEC`, `PAKT_FORMAT`
- `pakt validate <file>` — validate only, exit 0 (valid) or 1 (invalid)
- `pakt version` — print version

### Website

- Hugo site for usepakt.dev
- Custom minimal theme with dark nav, amber accent
- Full documentation: guide, specification, installation, annotated examples

### CI/CD

- GitHub Actions: test, build, lint (golangci-lint), coverage — Linux + macOS matrix

### Stats

- 11,491 lines of Go implementation
- 7,289 lines of tests (438 tests)
- 24 benchmarks comparing PAKT vs `encoding/json`
