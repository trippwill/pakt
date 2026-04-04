# PAKT Specification

> **PAKT** — a typed data interchange format. Human-authorable. Stream-parseable. Spec-projected.

## 0. Version

This document defines **PAKT 0** (draft). The specification is not yet stable — breaking changes may occur before PAKT 1.

PAKT uses single-integer versioning. Each version is a complete specification. Parser libraries advertise which spec versions they support.

| Version | Status |
|---------|--------|
| PAKT 0 | Draft — in development |
| PAKT 1 | (future) First stable release |

## 1. Document Model

A PAKT document is a sequence of **assignments** at the top level. Each assignment is a named, typed value.

The document root is a distinct concept from a struct value — it uses self-describing assignments (`name:type = value`) rather than positional values. This is the system boundary where data enters without a parent type context.

```
name:str = 'midwatch'
version:(int, int, int) = (1, 0, 0)
```

> **Future consideration**: Struct streams (a sequence of delimited struct values for data feeds) may be added in a future version.

## 2. Encoding

A PAKT document is a sequence of Unicode code points encoded as **UTF-8**. No other encodings are permitted.

A UTF-8 BOM (`U+FEFF`) at the start of a document is accepted and ignored. Implementations must not reject a document that begins with a BOM, and must not emit a BOM when writing.

## 3. Lexical Grammar

### 3.1 Characters

```
ALPHA   = 'a'-'z' | 'A'-'Z'
DIGIT   = '0'-'9'
IDENT   = (ALPHA | '_') (ALPHA | DIGIT | '_' | '-')*
WS      = ' ' | '\t'
NL      = '\n' | '\r\n'
SEP     = ',' | NL
```

### 3.2 Comments

```
COMMENT = '#' (any char except NL)* NL
```

Line comments begin with `#` and extend to end of line. May appear on their own line or after a value. No block comments.

### 3.3 Scalar Literals

```
DIGIT_SEP = DIGIT (DIGIT | '_')*
HEX_DIGIT = DIGIT | 'a'-'f' | 'A'-'F'
HEX_SEP   = HEX_DIGIT (HEX_DIGIT | '_')*
BIN_DIGIT = '0' | '1'
OCT_DIGIT = '0'-'7'

INT      = ['-'] DIGIT_SEP
         | ['-'] '0x' HEX_SEP
         | ['-'] '0b' BIN_DIGIT (BIN_DIGIT | '_')*
         | ['-'] '0o' OCT_DIGIT (OCT_DIGIT | '_')*
DEC      = ['-'] DIGIT_SEP '.' DIGIT_SEP
FLOAT    = ['-'] DIGIT_SEP ('.' DIGIT_SEP)? ('e' | 'E') [+-]? DIGIT+
BOOL     = 'true' | 'false'
NIL      = 'nil'
DATE     = DIGIT{4} '-' DIGIT{2} '-' DIGIT{2}
TZ       = 'Z' | [+-] DIGIT{2} ':' DIGIT{2}
TIME     = DIGIT{2} ':' DIGIT{2} ':' DIGIT{2} ('.' DIGIT+)? TZ
DATETIME = DATE 'T' TIME
UUID     = HEX{8} '-' HEX{4} '-' HEX{4} '-' HEX{4} '-' HEX{12}
STRING   = "'" string_char* "'"
         | '"' string_char* '"'
ML_STR   = "'''" ML_BODY "'''"
         | '"""' ML_BODY '"""'
ATOM     = IDENT
```

- Leading zeros on decimal `INT` are permitted and ignored (`01` evaluates to `1`).
- `_` in numeric literals is a visual separator, ignored by the parser.
- `ATOM` is syntactically identical to `IDENT`. It is valid as a value only when the type is an atom set.
- Strings are always quoted. There are no unquoted string values.

### 3.4 Multi-line Strings

A multi-line string is delimited by triple quotes (`'''` or `"""`).

**Opening**: The opening `'''` must be followed immediately by a newline. Content begins on the next line. The first newline after the opening delimiter is stripped.

**Closing**: The closing `'''` must appear on its own line, preceded only by whitespace. The last newline before the closing delimiter is stripped.

**Indentation stripping**: The column of the closing delimiter defines the baseline indentation. That many characters of leading whitespace are removed from each content line. A non-blank content line with fewer leading whitespace characters than the baseline is a parse error.

**Escapes**: The same escape sequences as single-line strings are recognized inside multi-line strings.

```
# Closing delimiter at column 4 → strip 4 spaces from each line
query:str = '''
    SELECT id, name
    FROM users
    WHERE active = true
    '''
# Result: "SELECT id, name\nFROM users\nWHERE active = true"
```

```
# Closing delimiter at column 0 → no stripping
raw:str = '''
line one
line two
'''
# Result: "line one\nline two"
```

### 3.5 String Escapes

Within a quoted string, the following escape sequences are recognized:

| Escape | Meaning |
|--------|---------|
| `\\` | Backslash |
| `\'` | Single quote |
| `\"` | Double quote |
| `\n` | Newline (U+000A) |
| `\r` | Carriage return (U+000D) |
| `\t` | Tab (U+0009) |
| `\uXXXX` | Unicode BMP code point (4 hex digits) |
| `\UXXXXXXXX` | Unicode code point (8 hex digits) |

Any other `\` followed by a character is a parse error. Null bytes (`U+0000`) are not permitted in strings, whether literal or escaped.

### 3.6 Tokens

```
HASH    = '#'
ASSIGN  = '='
COLON   = ':'
AT      = '@'                       ; reserved for future constraints
COMMA   = ','
PIPE    = '|'
LBRACE  = '{'    RBRACE = '}'
LPAREN  = '('    RPAREN = ')'
LBRACK  = '['    RBRACK = ']'
LANGLE  = '<'    RANGLE = '>'
```

## 4. Type System

Every value must have a type. There is no default type and no type inference.

### 4.1 Scalar Types

| Type | Literal | Example |
|------|---------|---------|
| `str` | Quoted string | `'hello'` |
| `int` | Signed 64-bit integer | `42`, `-7`, `0xFF`, `0b1010`, `0o77`, `1_000` |
| `dec` | Exact decimal | `3.14`, `1_000.50` |
| `float` | IEEE 754 binary64 | `6.022e23`, `1.5E-10` |
| `bool` | Boolean keyword | `true`, `false` |
| `uuid` | UUID | `550e8400-e29b-41d4-a716-446655440000` |
| `date` | ISO date | `2026-06-01` |
| `time` | ISO time (tz required) | `14:30:00Z`, `14:30:00-04:00` |
| `datetime` | ISO datetime (tz required) | `2026-06-01T14:30:00Z` |

**Numeric precision:**

- **`int`**: Signed 64-bit integer. Range: −9,223,372,036,854,775,808 to 9,223,372,036,854,775,807. Values outside this range are a parse error.
- **`float`**: IEEE 754 binary64 (double precision). Implementations must parse and round per IEEE 754.
- **`dec`**: Arbitrary-precision decimal in the text representation. Implementations must support at least 34 significant digits (equivalent to IEEE 754 decimal128). An implementation may reject values exceeding its supported precision with a clear error.

`true`, `false`, and `nil` are reserved keywords, not atoms.

### 4.2 Nullable Types

Any type may be made nullable by appending `?`:

```
nullable_type = type '?'
```

A nullable type accepts all values of the base type plus `nil`. A non-nullable type receiving `nil` is a parse error.

### 4.3 Atom Sets

An atom set constrains a value to one of a fixed set of bareword identifiers:

```
atom_set = PIPE IDENT (COMMA IDENT)* PIPE
```

Example: `|dev, staging, prod|`

`true`, `false`, and `nil` are reserved keywords and cannot be used as atoms.

### 4.4 Composite Types

| Kind | Type syntax | Delimiter | Keys | Values |
|------|-------------|-----------|------|--------|
| Struct | `{field:type, ...}` | `{ }` | Atom (static) | Heterogeneous |
| Tuple | `(type, ...)` | `( )` | None (positional) | Heterogeneous |
| List | `[type]` | `[ ]` | None (ordered) | Homogeneous |
| Map | `<keytype = valtype>` | `< >` | Any typed value | Homogeneous |

Five unique delimiter pairs — no overloads:

| First token after `=` | Kind |
|-----------------------|------|
| `{` | Struct |
| `(` | Tuple |
| `[` | List |
| `<` | Map |
| anything else | Scalar or atom |

Empty lists (`[]`) and empty maps (`<>`) are valid. Empty structs, tuples, and atom sets are parse errors — these types require at least one member.

### 4.5 Reserved

The `@` token is reserved for future constraint syntax (e.g., `@len`, `@range`, `@pattern`).

## 5. Syntactic Grammar

### 5.1 Document

```
document = assignment*
```

### 5.2 Assignment

```
assignment = IDENT type_annot ASSIGN value
```

### 5.3 Type Annotation

```
type_annot = COLON type '?'?
```

### 5.4 Type

```
type = scalar_type | atom_set | struct_type | tuple_type | list_type | map_type

scalar_type = 'str' | 'int' | 'dec' | 'float' | 'bool' | 'uuid' | 'date' | 'time' | 'datetime'

atom_set    = PIPE IDENT (COMMA IDENT)* PIPE

struct_type = LBRACE struct_field_decl (COMMA struct_field_decl)+ RBRACE

struct_field_decl = IDENT COLON type '?'?

tuple_type  = LPAREN type (COMMA type)+ RPAREN

list_type   = LBRACK type '?'? RBRACK

map_type    = LANGLE type ASSIGN type RANGLE
```

### 5.5 Values

```
value = scalar | NIL | atom_val | struct_val | tuple_val | list_val | map_val

scalar    = STRING | INT | DEC | FLOAT | BOOL | UUID | DATE | TIME | DATETIME
atom_val  = ATOM
```

`nil` is valid only when the type is nullable (`type?`).

### 5.6 Struct Value

Struct values contain positional values matched left-to-right against the fields declared in the type annotation. The type annotation is required.

```
struct_val      = LBRACE struct_members RBRACE
struct_members  = (value (SEP value)* SEP?)?
```

> **Future consideration**: Self-describing struct members (`name:type = value`) may be added in a future version.

### 5.7 Tuple Value

Tuple values contain positional values matched left-to-right against the types declared in the type annotation. The type annotation is required.

```
tuple_val      = LPAREN tuple_members RPAREN
tuple_members  = (value (SEP value)* SEP?)?
```

> **Future consideration**: Self-describing tuple members (`:type value`) may be added in a future version.

### 5.8 List Value

```
list_val     = LBRACK list_members RBRACK
list_members = (value (SEP value)* SEP?)?
```

All elements must conform to the declared element type. An empty list (`[]`) is valid.

### 5.9 Map Value

```
map_val     = LANGLE map_entries RANGLE
map_entries = (map_entry (SEP map_entry)* SEP?)?
map_entry   = value ASSIGN value
```

Keys conform to the declared key type. Values conform to the declared value type. An empty map (`<>`) is valid. Duplicate keys are a parse error.

## 6. Uniqueness

Duplicate names at the document root are a parse error. Duplicate keys within a single map value are a parse error.

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

## 9. Spec Model

PAKT has a three-layer spec model: producer assertions, external specs, and consumer projections.

### 9.1 Producer Assertions

Type annotations in a document are assertions by the producer. They are validated during parsing — a document that violates its own assertions is malformed.

```
release:int = 26
status:|active, inactive| = active
```

### 9.2 External Spec Files

A spec file (`.spec.pakt`) uses PAKT type syntax without values:

```
spec        = spec_member*
spec_member = IDENT type_annot
```

Example:

```
# deploy.spec.pakt
deploy:{level:|dev, staging, prod|, release:int, date:date}
version:(int, int, int)
```

A spec file defines the consumer's requirements.

### 9.3 Spec Compatibility

| Check | Rule |
|-------|------|
| **Structural** | Spec fields missing from the document are errors. Document fields not in the spec are ignored (projection). |
| **Type compatibility** | Document type must match or be a subtype of the spec type. |
| **Atom set compatibility** | Document atom set must be a subset of or equal to the spec atom set. |

### 9.4 Consumer Projections

A consumer may supply a spec at parse time as a projection — a filter over the document stream.

- Fields matching the spec are parsed, validated, and emitted.
- Fields not in the spec are skipped without allocation.
- Type mismatches on matched fields are immediate errors.

Projections may be defined statically (in `.spec.pakt` files) or constructed dynamically at runtime.

```
# Full document
{
level:|dev, staging, prod| = prod
release:int               = 26
date:date                 = 2026-06-01
}

# Projection A (deployment service):
# deploy:{level:|dev, staging, prod|, date:date}
#   → sees level and date, skips release

# Projection B (audit service):
# deploy:{release:int}
#   → sees release only
```

## 10. File Conventions

| Extension | Purpose | MIME Type |
|-----------|---------|----------|
| `.pakt` | PAKT data document | `application/vnd.pakt` |
| `.spec.pakt` | PAKT spec file | `application/vnd.pakt.spec` |

Website: [usepakt.dev](https://usepakt.dev)
