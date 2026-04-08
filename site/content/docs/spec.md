---
title: "PAKT Specification"
description: "The formal PAKT v0 specification — grammar, types, and semantics."
weight: 2
---

> **PAKT** — a typed data interchange format. Human-authorable. Streaming. Self-describing.

This site page is a synchronized overview of the current **PAKT v0** surface. The normative source remains [`spec/pakt-v0.md`](https://github.com/trippwill/pakt/blob/main/spec/pakt-v0.md).

> **Implementation note:** The current Go library and CLI implement top-level `<<` pack statements as first-class root events (`PackStart` / `PackEnd`). Event names in the Go code currently use `ListStreamStart/End` and `MapStreamStart/End` — a renaming alignment is planned.

## Version

PAKT uses single-integer versioning. Each version is a complete specification.

| Version | Status | Meaning |
|---------|--------|---------|
| PAKT 0 | Draft | In active development; breaking changes expected |
| PAKT 0 | Accepted | Feature-complete; only clarifications and bug fixes |
| PAKT 1 | (future) | First stable release; no breaking changes within major version |

## Data Model

The canonical unit grammar is:

```text
unit      = statement*
statement = assign | pack
```

Current Go implementation support:

- `assign = IDENT type_annot ASSIGN value`
- `pack = IDENT type_annot PACK pack_body`

Assigns look like:

```pakt
name:str = 'midwatch'
version:(int, int, int) = (1, 0, 0)
payload:bin = x'48656C6C6F'
```

## Scalar Types

| Type | Literal | Example |
|------|---------|---------|
| `str` | Quoted or raw string | `'hello'`, `r'C:\tmp'` |
| `int` | Signed 64-bit integer | `42`, `-7`, `0xFF`, `0b1010`, `0o77`, `1_000` |
| `dec` | Exact decimal | `3.14`, `1_000.50` |
| `float` | IEEE 754 binary64 | `6.022e23`, `1.5E-10` |
| `bool` | Boolean keyword | `true`, `false` |
| `uuid` | UUID | `550e8400-e29b-41d4-a716-446655440000` |
| `date` | ISO date | `2026-06-01` |
| `ts` | ISO timestamp (tz required) | `2026-06-01T14:30:00Z` |
| `bin` | Binary data | `x'48656C6C6F'`, `b'SGVsbG8='` |

`bin` literals accept hexadecimal (`x'...'`) and RFC 4648 base64 with padding (`b'...'`). Both forms produce the same bytes.

## Strings

PAKT supports:

- quoted strings: `'hello'`, `"hello"`
- raw strings: `r'C:\Users\alice'`, `r"^\d{3}-\d{4}$"`
- triple-quoted strings: `''' ... '''`, `""" ... """`
- raw triple-quoted strings: `r''' ... '''`, `r""" ... """`

### Multi-line indentation

The opening triple quote must be followed immediately by a newline. The closing triple quote must appear on its own line.

The **first non-blank content line** defines the indentation baseline. That much leading whitespace is stripped from each non-blank content line.

```pakt
query:str = '''
    SELECT id, name
    FROM users
    WHERE active = true
    '''
```

### Raw strings

Raw strings disable escape processing:

```pakt
path:str     = r'C:\Users\alice\Documents'
template:str = r'''
    Hello \n World
    '''
```

## Composite Types

| Kind | Type syntax | Value syntax |
|------|-------------|--------------|
| Struct | `{field:type, ...}` | `{ val, ... }` |
| Tuple | `(type, ...)` | `(val, val, ...)` |
| List | `[type]` | `[val, ...]` |
| Map | `<K ; V>` | `<key ; value, ...>` |

Maps use `;` between the key and value in both the type annotation and each map entry.

```pakt
users:<int ; str> = <
    1 ; 'Alice'
    2 ; 'Bob'
>
```

Duplicate map keys are preserved in decode order. Interpreting them is an application/domain concern.

## Grammar Excerpts

```text
scalar_type = 'str' | 'int' | 'dec' | 'float' | 'bool' | 'uuid' | 'date' | 'ts' | 'bin'
map_type    = LANGLE type SEMI type RANGLE
value       = scalar | NIL | atom_val | struct_val | tuple_val | list_val | map_val
scalar      = STRING | RAW_STR | ML_STR | ML_RAW | INT | DEC | FLOAT | BOOL | UUID | DATE | TS | BIN
map_entry   = value SEMI value
pack        = IDENT type_annot PACK pack_body
```

## Canonical Specification

For the full normative grammar and semantics, including pack statements and duplicate-key handling, read the canonical spec:

- [`spec/pakt-v0.md`](https://github.com/trippwill/pakt/blob/main/spec/pakt-v0.md)

## 6. Duplicates

Duplicate names at the unit root are preserved in encounter order — this matches the handling of duplicate map keys. Interpreting duplicate statement names is an application/domain concern.

Struct field names are declared in the type, not in the value, so duplicates are caught at the type level.

## 7. Whitespace Rules

- Whitespace around `=` is optional: `name:str = 'x'` and `name:str='x'` are equivalent.
- Whitespace around `:` in type annotations is **not** permitted: `name:int`, not `name : int`.
- Members are separated by commas, newlines, or both (`SEP`). At least one separator is required between members.
- Consecutive newlines (blank lines) are ignored.
- Indentation is insignificant — cosmetic only.

## 8. Structural Equivalence

Block and inline forms are semantically identical. A conforming formatter may freely convert between them:

```
# Block struct
deploy:{level:str, release:int} = {
'platform'
26
}

# Inline struct
deploy:{level:str, release:int} = { 'platform', 26 }
```

```
# Block tuple
version:(int, int, int) = (
3
45
5678
)

# Inline tuple
version:(int, int, int) = (3, 45, 5678)
```

## 9. Type Assertions

Type annotations in a PAKT unit are assertions by the producer. They are validated during parsing — a unit that violates its own assertions is malformed.

```
release:int = 26
status:|active, inactive| = |active
```

> **Future consideration:** External spec files (`.spec.pakt`) and consumer projections may be added in a future version. These features are deferred until real-world usage patterns are better understood.

## 10. File Conventions

| Extension | Purpose | MIME Type |
|-----------|---------|----------|
| `.pakt` | PAKT data unit | `application/vnd.pakt` |

Website: [usepakt.dev](https://usepakt.dev)
