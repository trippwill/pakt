# PAKT Guide

> **PAKT** — a typed data interchange format. Human-authorable. Streaming. Self-describing.

---

## The Basics

PAKT units are UTF-8. A BOM at the start is accepted but ignored.

At the top level, a PAKT unit is a sequence of statements. A statement is either an assignment or a collect.

```
greeting:str     = 'hello world'
count:int        = 42
flags:int        = 0xFF
mask:int         = 0b1010_0011
big:int          = 1_000_000
price:dec        = 19.99
avogadro:float   = 6.022e23
active:bool      = true
id:uuid          = 550e8400-e29b-41d4-a716-446655440000
started:date     = 2026-06-01
updated:ts       = 2026-06-01T14:30:00Z
payload:bin      = x'48656C6C6F'
```

```
events:[int] << 1, 2, 3
metrics:<str ; int> << 'ok' ; 1, 'warn' ; 2
```

Every value must have a type. No exceptions.

**Numeric precision:**

- `int` is a signed 64-bit integer (−9.2 × 10¹⁸ to 9.2 × 10¹⁸). Out-of-range values are a parse error.
- `float` is IEEE 754 binary64 (double precision).
- `dec` is arbitrary-precision in the text. Implementations must support at least 28 significant digits.
- `bin` is raw byte data. The decoder accepts both hex (`x'...'`) and base64 (`b'...'`) literals.

`<<` collects are parsed by the current Go library and CLI. In the event model they surface as explicit `FeedStart` / `FeedEnd` root events rather than pretending to be delimited collections.

---

## Strings

Strings are always quoted — single or double quotes. Standard escape sequences are supported:

```
message:str = 'hello\nworld'
path:str    = 'C:\\Users\\alice'
tab:str     = "col1\tcol2"
emoji:str   = '\u2603'           # snowman
flag:str    = '😀'               # grinning face (literal UTF-8)
```

| Escape | Meaning |
|--------|--------|
| `\\` | Backslash |
| `\'` | Single quote |
| `\"` | Double quote |
| `\n` | Newline |
| `\r` | Carriage return |
| `\t` | Tab |
| `\uXXXX` | Unicode BMP code point |

Null bytes are not allowed in strings. Surrogate code points (`U+D800`–`U+DFFF`) are not valid in `\u` escapes — use literal UTF-8 for supplementary-plane characters.

### Raw Strings

Prefix a string with `r` to disable escape processing:

```
path:str  = r'C:\Users\alice\Documents'
regex:str = r"^\d{3}-\d{4}$"
```

Raw strings may also be triple-quoted. Indentation stripping works the same way as regular multi-line strings, but backslashes stay literal:

```
template:str = r'''
    Hello \n World
    '''
# Result: "Hello \\n World"
```

### Multi-line Strings

Triple quotes (`'''` or `"""`) for multi-line content. The first non-blank content line sets the indentation baseline — that much leading whitespace is stripped from every non-blank content line:

```
query:str = '''
    SELECT id, name
    FROM users
    WHERE active = true
    '''
# Result: "SELECT id, name\nFROM users\nWHERE active = true"
```

The first newline after the opening `'''` and the last newline before the closing `'''` are stripped. The closing delimiter must be on its own line.

```
raw:str = '''
no indent here
'''
# Result: "no indent here"
```

Same escape sequences work inside triple-quoted strings.

---

## Comments

```
# Full-line comment
greeting:str = 'hello'   # Inline comment
```

---

## Atoms

Atoms are bareword identifiers — unquoted, constrained to a declared set:

```
level:|dev, staging, prod| = |prod
status:|active, inactive|  = |active
```

Atoms are distinct from booleans — `true`, `false`, and `nil` are reserved keywords, not atoms.

---

## Structs

A struct is a collection of named fields, wrapped in `{ }`. The shape is declared in the type — values are positional:

```
server:{host:str, port:int, debug:bool} = {
'localhost'
8080
false
}
```

Inline:

```
server:{host:str, port:int, debug:bool} = { 'localhost', 8080, false }
```

Values match fields left-to-right. The type annotation carries the names and types.

---

## Tuples

An ordered sequence of typed values, wrapped in `( )`. The shape is declared in the type:

```
version:(int, int, int) = (3, 45, 5678)
```

Block form:

```
version:(int, int, int) = (
3
45
5678
)
```

---

## Lists

A homogeneous sequence, wrapped in `[ ]`:

```
ids:[int] = [12, 14, 26, 78]
```

Block form:

```
ids:[int] = [
12
14
26
78
]
```

Empty lists are valid: `ids:[int] = []`

---

## Maps

A homogeneous collection of key-value pairs, wrapped in `< >`. Keys and values are separated by `;`:

```
users:<int ; str> = <
1 ; 'Alice'
2 ; 'Bob'
>
```

Values can be composites:

```
users:<int ; {gn:str, fn:str, admin:bool, dob:(int, int, int)}> = <
01 ; { 'Johnson', 'Amy', true, (1982, 06, 22) }
02 ; { 'Smith', 'Bob', false, (2001, 03, 12) }
>
```

Empty maps are valid: `cache:<str ; int> = <>`

Duplicate keys in a map are an error.

---

## Nullable Types

Any type becomes nullable by appending `?`. A nullable value may be `nil`:

```
nickname:str?       = nil
score:int?          = 42
role:|admin, user|? = nil
```

`nil` is only valid when the type is nullable — using `nil` with a non-nullable type is a parse error.

---

## Block vs. Inline

Every composite can be block (newline-separated) or inline (comma-separated). They're identical semantically:

```
# Block
deploy:{level:str, release:int} = {
'platform'
26
}

# Inline
deploy:{level:str, release:int} = { 'platform', 26 }
```

Whitespace around `=` in assignments and `;` in maps is optional. Indentation is cosmetic.

---

## Duplicates

Duplicate names at the unit root are preserved in encounter order:

```
# Both statements are preserved — the consumer decides how to handle them
name:str = 'Alice'
name:str = 'Bob'
```

Duplicate map keys are also preserved in encounter order. Interpreting them is an application/domain concern above the raw decode.

---

## Type Assertions

Type annotations in a PAKT unit are promises by the producer. The parser validates them at parse time:

```
release:int = 26
status:|active, inactive| = |active
```

A value that doesn't conform to its declared type is an immediate parse error.

---

## Quick Reference

| Kind | Type syntax | Value syntax |
|------|-------------|--------------|
| String | `:str` | `'quoted text'` |
| Integer | `:int` | `42`, `0xFF`, `0b1010`, `1_000` |
| Decimal | `:dec` | `3.14`, `1_000.50` |
| Float | `:float` | `6.022e23`, `1.5E-10` |
| Boolean | `:bool` | `true`, `false` |
| Binary | `:bin` | `x'48656C6C6F'`, `b'SGVsbG8='` |
| UUID | `:uuid` | `550e8400-e29b-...` |
| Date | `:date` | `2026-06-01` |
| Timestamp | `:ts` | `2026-06-01T14:30:00Z` |
| Atom | `:\|a, b, c\|` | `\|b` |
| Struct | `:{field:type, ...}` | `{ val, ... }` (positional) |
| Tuple | `:(type, ...)` | `(val, val, ...)` |
| List | `:[type]` | `[val, ...]` |
| Map | `:<K ; V>` | `<key ; val, ...>` |
| Nullable | `:type?` | value or `nil` |

**Five rules:**

1. `=` is only the assignment operator — maps use `;` between key and value
2. Every value must have a type — no defaults, no inference
3. Append `?` for nullable — only nullable types accept `nil`
4. Block and inline are the same semantics, different whitespace
5. Indentation is never significant
