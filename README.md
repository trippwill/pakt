# PAKT

> **PAKT** — a typed data interchange format. Human-authorable. Stream-parseable. Spec-projected.

```
greeting:str     = 'hello world'
count:int        = 42
active:bool      = true
server:{host:str, port:int} = { 'localhost', 8080 }
```

## What is PAKT?

PAKT is a typed data interchange format where every value carries its type. No inference, no ambiguity. Documents are self-validating — type annotations are producer assertions checked at parse time.

Consumer-side `.spec.pakt` files enable **projections**: a streaming parser materializes only the fields a consumer needs, skipping everything else without allocation.

## Repository Structure

```
pakt/
├── encoding/       # Canonical Go library (github.com/trippwill/pakt/encoding)
├── main.go         # CLI entry point (go install github.com/trippwill/pakt@latest)
├── spec/           # Formal specification (PAKT v0 draft)
├── docs/           # User guide and documentation
├── site/           # Hugo website (usepakt.dev)
└── testdata/       # Sample .pakt files
```

## Install

```sh
go install github.com/trippwill/pakt@latest
```

## CLI Usage

```sh
# Parse a file and emit structured events
pakt parse data.pakt

# Parse with spec projection
pakt parse data.pakt --spec deploy.spec.pakt

# Validate only (exit 0/1)
pakt validate data.pakt
```

## Library

```go
import "github.com/trippwill/pakt/encoding"
```

## Documentation

- [PAKT Specification](spec/pakt-v0.md) — formal grammar and semantics
- [PAKT Guide](docs/guide.md) — human-friendly introduction
- [usepakt.dev](https://usepakt.dev) — website

## License

[MIT](LICENSE)
