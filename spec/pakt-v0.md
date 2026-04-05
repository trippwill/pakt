# PAKT Specification

> **PAKT** — a typed data interchange format. Human-authorable. Stream-parseable. Spec-projected.

## 0. Version

This document defines **PAKT 0** (draft). The specification is not yet stable — breaking changes may occur before PAKT 1.

PAKT uses single-integer versioning. Each version is a complete specification. Parser libraries advertise which spec versions they support.

| Version | Status | Meaning |
|---------|--------|---------|
| PAKT 0 | Draft | In active development; breaking changes expected |
| PAKT 0 | Accepted | Feature-complete; only clarifications and bug fixes |
| PAKT 1 | (future) | First stable release; no breaking changes within major version |

### 0.1 Design Principles

1. **Performance is a feature.** The format, grammar, and type system are designed so that a conforming parser can operate in a single streaming pass with minimal allocation. Spec rules must not require unbounded buffering or retroactive reinterpretation.

2. **Type context flows with the data.** Every value carries or inherits its type. The parser never guesses. This enables type-directed scanning and projection without schema negotiation.

3. **The decoder is lossless; interpretation is layered.** A conforming decoder preserves all information present in the source — including duplicate map keys and encounter order. Policy decisions such as rejecting duplicates, applying last-wins, or accumulating values belong to higher-level consumers, not the core decoder.

4. **Presentation is an application concern.** Human-readable formatting, event enrichment, and display transformations (such as CLI output or JSON projection) are not encoder or decoder responsibilities. The core event contract is minimal and machine-oriented.

5. **The grammar is the event model.** Each grammatical construct — assignment, stream, struct, tuple, list, map, scalar — maps to a distinct event kind. Consumers should not need to inspect payload strings to determine structural context.

## 1. Document Model

A PAKT document is a sequence of **statements** at the top level. A statement is either an **assignment** (a named, typed, single value) or a **stream** (a named, typed, open-ended sequence of values).

The document root uses self-describing statements (`name:type = value` or `name:type << values...`) rather than positional values. This is the system boundary where data enters without a parent type context.

```
name:str = 'midwatch'
version:(int, int, int) = (1, 0, 0)
```

Streams deliver zero or more values of a collection type, terminated by EOF or the start of the next statement:

```
events:[{ts:datetime, level:str, msg:str}] <<
{ 2026-06-01T14:30:00Z, 'info', 'server started' }
{ 2026-06-01T14:31:00Z, 'warn', 'high latency' }
```

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
DEC      = ['-'] DIGIT_SEP? '.' DIGIT_SEP
FLOAT    = ['-'] DIGIT_SEP? ('.' DIGIT_SEP)? ('e' | 'E') [+-]? DIGIT+
BOOL     = 'true' | 'false'
NIL      = 'nil'
DATE     = DIGIT{4} '-' DIGIT{2} '-' DIGIT{2}
TZ       = 'Z' | [+-] DIGIT{2} ':' DIGIT{2}
TIME     = DIGIT{2} ':' DIGIT{2} ':' DIGIT{2} ('.' DIGIT+)? TZ
DATETIME = DATE 'T' TIME
UUID     = HEX{8} '-' HEX{4} '-' HEX{4} '-' HEX{4} '-' HEX{12}
BIN      = 'x' "'" HEX_DIGIT* "'"
         | 'b' "'" BASE64_CHAR* "'"
BASE64_CHAR = ALPHA | DIGIT | '+' | '/' | '='

ESCAPE      = '\' ('\' | "'" | '"' | 'n' | 'r' | 't')
            | '\u' HEX_DIGIT{4}
            | '\U' HEX_DIGIT{8}
string_char = ESCAPE
            | any code point except matching_quote, '\', NL, U+0000
raw_char    = any code point except matching_quote, U+0000
ML_CHAR     = ESCAPE
            | any code point except U+0000, not forming the closing triple-quote
ML_BODY     = ML_CHAR*
raw_ml_char = any code point except U+0000, not forming the closing triple-quote

STRING   = "'" string_char* "'"
         | '"' string_char* '"'
RAW_STR  = 'r' "'" raw_char* "'"
         | 'r' '"' raw_char* '"'
ML_STR   = "'''" ML_BODY "'''"
         | '"""' ML_BODY '"""'
ML_RAW   = 'r' "'''" raw_ml_char* "'''"
         | 'r' '"""' raw_ml_char* '"""'
ATOM     = '|' IDENT
```

- Leading zeros on decimal `INT` are permitted and ignored (`01` evaluates to `1`).
- `_` in numeric literals is a visual separator, ignored by the parser.
- `ATOM` values use a `|` prefix to distinguish them from identifiers. Atom set type declarations use bare names inside pipe delimiters (`|dev, staging, prod|`), while atom values use the prefix (`|dev`).
- `bin` literals use a prefix to indicate encoding: `x'...'` for hexadecimal, `b'...'` for base64.
- Hex literals must contain an even number of hex digits (each pair is one byte); an odd count is a parse error. Whitespace within hex literals is not permitted.
- Base64 literals follow RFC 4648 standard encoding. Padding (`=`) is required. Invalid base64 characters or incorrect padding are parse errors.
- Strings are always quoted. There are no unquoted string values.

### 3.4 Raw Strings

A raw string is prefixed with `r` and performs no escape processing. Backslashes are literal.

```
path:str = r'C:\Users\alice\Documents'
regex:str = r"^\d{3}-\d{4}$"
```

Raw strings may also be triple-quoted for multi-line content. Indentation stripping follows the same rules as regular multi-line strings (based on the first non-blank content line), but no escape sequences are processed:

```
template:str = r'''
    Hello \n World
    '''
# Result: "Hello \\n World" — the \n is two literal characters
```

The only character that cannot appear in a raw string is the unescaped closing quote (or triple-quote for multi-line). To include the closing delimiter, use the alternate quote style:

```
has_single:str = r"it's raw"
has_double:str = r'she said "hello"'
```

Null bytes (`U+0000`) are not permitted in raw strings.

### 3.5 Multi-line Strings

A multi-line string is delimited by triple quotes (`'''` or `"""`).

**Opening**: The opening `'''` must be followed immediately by a newline. Content begins on the next line. The first newline after the opening delimiter is stripped.

**Closing**: The closing `'''` must appear on its own line, preceded only by whitespace. The last newline before the closing delimiter is stripped.

**Indentation stripping**: The leading whitespace of the **first non-blank content line** defines the baseline indentation. That many characters of leading whitespace are removed from each content line (including the first). A non-blank content line with fewer leading whitespace characters than the baseline is a parse error. Blank content lines (containing only whitespace) are preserved as empty lines. This rule allows streaming parsers to determine the strip level after reading a single line.

**Escapes**: The same escape sequences as single-line strings are recognized inside multi-line strings.

```
# First content line has 4 leading spaces → strip 4 from each line
query:str = '''
    SELECT id, name
    FROM users
    WHERE active = true
    '''
# Result: "SELECT id, name\nFROM users\nWHERE active = true"
```

```
# First content line has 0 leading spaces → no stripping
raw:str = '''
line one
line two
'''
# Result: "line one\nline two"
```

### 3.6 String Escapes

Within a quoted string (not raw), the following escape sequences are recognized:

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

### 3.7 Tokens

```
HASH    = '#'
ASSIGN  = '='
COLON   = ':'                       ; type annotation only
SEMI    = ';'                       ; map key-value association
STREAM  = '<<'
COMMA   = ','
PIPE    = '|'
LBRACE  = '{'    RBRACE = '}'
LPAREN  = '('    RPAREN = ')'
LBRACK  = '['    RBRACK = ']'
LANGLE  = '<'    RANGLE = '>'

; Reserved tokens (see §4.5)
AT      = '@'
BANG    = '!'
STAR    = '*'
DOLLAR  = '$'
AMP     = '&'
TILDE   = '~'
BTICK   = '`'
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
| `bin` | Binary data | `x'48656C6C6F'`, `b'SGVsbG8='` |

**Numeric precision:**

- **`int`**: Signed 64-bit integer. Range: −9,223,372,036,854,775,808 to 9,223,372,036,854,775,807. Values outside this range are a parse error.
- **`float`**: IEEE 754 binary64 (double precision). Implementations must parse and round per IEEE 754.
- **`dec`**: Arbitrary-precision decimal in the text representation. Implementations must support at least 34 significant digits (equivalent to IEEE 754 decimal128). An implementation may reject values exceeding its supported precision with a clear error.
- **`bin`**: Raw byte sequence. Hex form (`x'...'`) and base64 form (`b'...'`) are semantically equivalent — both produce the same bytes. Implementations represent this as a byte array/slice.

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
Atom values in data position use the `|` prefix: `|dev`, `|staging`, `|prod`.

### 4.4 Composite Types

| Kind | Type syntax | Delimiter | Keys | Values |
|------|-------------|-----------|------|--------|
| Struct | `{field:type, ...}` | `{ }` | Atom (static) | Heterogeneous |
| Tuple | `(type, ...)` | `( )` | None (positional) | Heterogeneous |
| List | `[type]` | `[ ]` | None (ordered) | Homogeneous |
| Map | `<keytype ; valtype>` | `< >` | Any typed value | Homogeneous |

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

The following tokens are reserved for future use. They must not appear in documents outside of string literals. A conforming parser encountering a reserved token in an unexpected position should report a syntax error.

| Token | Possible future use |
|-------|-------------------|
| `@` | Constraints (`@len`, `@range`, `@pattern`) |
| `!` | Assertions or negation |
| `*` | Wildcards or glob patterns |
| `$` | Variable references or interpolation |
| `&` | Type aliases (see §4.6) |
| `~` | Approximate matching or home paths |
| `` ` `` | Alternate string delimiters or template literals |

### 4.6 Future Consideration: Type Aliases

> **Status**: Design sketch only. Not part of PAKT 0. The `&` token is reserved for this purpose.

Type aliases would allow naming a type once and referencing it by name in type positions:

```
&entry = {path:str, size:int, is_dir:bool, hash:bin?}
&level = |info, warn, error|

entries:[&entry] << ...
events:[{ts:datetime, level:&level, msg:str}] << ...
```

Planned semantics:

- **`&name = type`** defines a type alias. It is not a statement — it emits no events and does not participate in root uniqueness.
- **`&name`** in type position is a structural substitution — the alias expands to the underlying type. There is no nominal typing.
- **Must appear before use.** This is the streaming-compatible ordering rule. Forward references are a parse error. In practice, aliases go at the top of the document.
- **`&` sigil required in both definition and reference.** This keeps alias names and statement names in separate namespaces — a document can have both `&entry` (type alias) and `entry` (statement name) without conflict.
- **Aliases can reference earlier aliases.** `&log = {ts:datetime, level:&level}` is valid if `&level` is already defined.
- **Spec files define their own aliases.** Projection is structural, not nominal — a spec's `&entry` must structurally match the document's `&entry`, but they are independent definitions.

## 5. Syntactic Grammar

### 5.1 Document

```
document  = statement*
statement = assignment | stream
```

### 5.2 Assignment

```
assignment = IDENT type_annot ASSIGN value
```

### 5.3 Stream

```
stream = IDENT type_annot STREAM stream_body
```

A stream delivers zero or more bare values of the annotated collection type. The type must be a list type, map type, or a list-of-structs type.

Stream body for list types (`[type]`):

```
stream_body = (value (SEP value)* SEP?)?
```

Each value conforms to the list's element type.

Stream body for map types (`<K ; V>`):

```
stream_body = (map_entry (SEP map_entry)* SEP?)?
```

Each entry is `key ; value` conforming to the map's key and value types.

**Termination**: A stream ends at EOF or when the parser encounters the start of the next top-level statement (`IDENT COLON`). This is LL(0)-decidable: atom values begin with `|`, booleans and `nil` are reserved keywords that cannot be statement names, and all other value forms begin with non-identifier characters (digits, quotes, delimiters). Therefore a bare identifier at stream level always begins a new statement.

**Duplicate keys**: Repeated map entries are preserved in encounter order. Interpreting duplicate keys is an application/domain concern (see §6).

**Root uniqueness**: Stream names participate in root uniqueness — a document may not have two statements (assignments or streams) with the same name.

### 5.4 Type Annotation

```
type_annot = COLON type '?'?
```

### 5.5 Type

```
type = scalar_type | atom_set | struct_type | tuple_type | list_type | map_type

scalar_type = 'str' | 'int' | 'dec' | 'float' | 'bool' | 'uuid' | 'date' | 'time' | 'datetime' | 'bin'

atom_set    = PIPE IDENT (COMMA IDENT)* PIPE

struct_type = LBRACE struct_field_decl (COMMA struct_field_decl)* RBRACE

struct_field_decl = IDENT COLON type '?'?

tuple_type  = LPAREN type (COMMA type)* RPAREN

list_type   = LBRACK type '?'? RBRACK

map_type    = LANGLE type SEMI type RANGLE
```

### 5.6 Values

```
value = scalar | NIL | atom_val | struct_val | tuple_val | list_val | map_val

scalar    = STRING | RAW_STR | ML_STR | ML_RAW | INT | DEC | FLOAT | BOOL | UUID | DATE | TIME | DATETIME | BIN
atom_val  = ATOM
```

`nil` is valid only when the type is nullable (`type?`).

### 5.7 Struct Value

Struct values contain positional values matched left-to-right against the fields declared in the type annotation. The type annotation is required.

```
struct_val      = LBRACE struct_members RBRACE
struct_members  = (value (SEP value)* SEP?)?
```

A struct value must contain exactly as many values as the type declares fields. Fewer or more values than declared is a **parse error**. Arity is validated during parsing.

> **Future consideration**: Self-describing struct members (`name:type = value`) may be added in a future version.

### 5.8 Tuple Value

Tuple values contain positional values matched left-to-right against the types declared in the type annotation. The type annotation is required.

```
tuple_val      = LPAREN tuple_members RPAREN
tuple_members  = (value (SEP value)* SEP?)?
```

A tuple value must contain exactly as many values as the type declares elements. Fewer or more values than declared is a **parse error**. Arity is validated during parsing.

> **Future consideration**: Self-describing tuple members (`:type value`) may be added in a future version.

### 5.9 List Value

```
list_val     = LBRACK list_members RBRACK
list_members = (value (SEP value)* SEP?)?
```

All elements must conform to the declared element type. An empty list (`[]`) is valid.

### 5.10 Map Value

```
map_val     = LANGLE map_entries RANGLE
map_entries = (map_entry (SEP map_entry)* SEP?)?
map_entry   = value SEMI value
```

Keys conform to the declared key type. Values conform to the declared value type. Key-value pairs are separated by `;`. An empty map (`<>`) is valid. Duplicate keys are preserved in encounter order; interpreting them is an application/domain concern (see §6).

## 6. Uniqueness

Duplicate names at the document root are a parse error — this applies to both assignments and streams.
The reserved keywords `true`, `false`, and `nil` cannot be used as statement names.

### 6.1 Map Duplicate Keys

Duplicate keys in either a **map value** (`= <...>`) or a **map stream** (`<<`) are not a format-level parse error and do not carry built-in replacement semantics in the format itself.

A conforming decoder should preserve repeated map entries in encounter order. How duplicates are interpreted is an application/domain concern. Higher-level consumers or bindings may choose to reject duplicates, apply first-wins or last-wins semantics, accumulate all values, or preserve raw order for later processing, but they must document that behavior.

Struct field names are declared in the type, not in the value, so duplicates are caught at the type level.

## 7. Whitespace Rules

- Whitespace around `=` is optional: `name:str = 'x'` and `name:str='x'` are equivalent.
- Whitespace around `<<` is optional: `name:[int] << 1, 2` and `name:[int]<<1, 2` are equivalent.
- Whitespace around `:` in type annotations is **not** permitted: `name:int`, not `name : int`.
- Whitespace around `;` in map entries is optional: `'key' ; value` and `'key';value` are equivalent.
- Members are separated by commas, newlines, or both (`SEP`). At least one separator is required between stream items or between members inside delimited composites.
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
status:|active, inactive| = |active
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
| **Type** | The document field type must match the spec field type exactly. |

### 9.4 Consumer Projections

A consumer may supply a spec at parse time as a projection — a filter over the document stream.

- Fields matching the spec are parsed, validated, and emitted.
- Fields not in the spec are skipped without allocation.
- Type mismatches on matched fields are immediate errors.

Projections may be defined statically (in `.spec.pakt` files) or constructed dynamically at runtime.

```
# Full document
{
level:|dev, staging, prod| = |prod
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

Projections apply equally to assignments and streams. A spec field that matches a `<<` stream emits the stream's events; a spec field that does not match a stream skips the entire stream body without allocation.

```
# Document with mixed statements
name:str = 'midwatch'
events:[{ts:datetime, level:str}] <<
    { 2026-06-01T14:30:00Z, 'info' }
    { 2026-06-01T14:31:00Z, 'warn' }
metrics:<str ; int> << 'ok' ; 1, 'err' ; 2

# Projection: events:[{ts:datetime, level:str}]
#   → emits the events stream, skips name and metrics
```

## 10. File Conventions

| Extension | Purpose | MIME Type |
|-----------|---------|----------|
| `.pakt` | PAKT data document | `application/vnd.pakt` |
| `.spec.pakt` | PAKT spec file | `application/vnd.pakt.spec` |

## 11. Error Model

A conforming implementation must report parse errors with sufficient detail for programmatic handling and human diagnosis.

### 11.1 Error Structure

Each error MUST include:

- **Code** — a numeric identifier from the table below (or an implementation-defined code ≥ 100)
- **Identifier** — a short string name corresponding to the code
- **Position** — source line and column (1-based)
- **Message** — a human-readable description of the problem

### 11.2 Normative Error Categories

Codes 1–99 are reserved for the spec. Implementations MUST support at least these categories and MUST allow callers to distinguish them programmatically (via sentinel errors, error codes, typed exceptions, or equivalent).

| Code | Identifier | Condition |
|------|-----------|-----------|
| 1 | `unexpected_eof` | Input ends before a syntactic construct is complete |
| 2 | `duplicate_name` | Two root-level statements share the same identifier |
| 3 | `type_mismatch` | A value does not conform to its declared type, or a spec projection type does not match the document type |
| 4 | `nil_non_nullable` | `nil` appears where the type is not nullable |
| 5 | `syntax` | Any lexical or grammatical error not covered by a more specific category |

### 11.3 Extensibility

Implementations MAY define additional error categories with codes ≥ 100. Implementation-defined categories must be documented and must not reuse codes 1–99 for different meanings.

### 11.4 Non-Errors

The following conditions are explicitly **not** parse errors:

- **Duplicate map keys** — preserved in encounter order per §6.1 and principle 3 (lossless decoder). Higher-level consumers decide whether duplicates are meaningful.
- **Unknown fields during projection** — silently skipped per §9.4.

Website: [usepakt.dev](https://usepakt.dev)

## Appendix A. Collected Formal Grammar

This appendix assembles every grammar rule from the spec body into a single reference. No new rules are introduced — this is a collected view for implementors.

### A.1 Lexical Grammar

```
; --- Characters and whitespace ---

ALPHA       = 'a'-'z' | 'A'-'Z'
DIGIT       = '0'-'9'
HEX_DIGIT   = DIGIT | 'a'-'f' | 'A'-'F'
BIN_DIGIT   = '0' | '1'
OCT_DIGIT   = '0'-'7'
DIGIT_SEP   = DIGIT (DIGIT | '_')*
HEX_SEP     = HEX_DIGIT (HEX_DIGIT | '_')*
BASE64_CHAR = ALPHA | DIGIT | '+' | '/' | '='

IDENT       = (ALPHA | '_') (ALPHA | DIGIT | '_' | '-')*
WS          = ' ' | '\t'
NL          = '\n' | '\r\n'
SEP         = ',' | NL
COMMENT     = '#' (any char except NL)* NL

; --- Tokens ---

ASSIGN  = '='
COLON   = ':'
SEMI    = ';'
STREAM  = '<<'
COMMA   = ','
PIPE    = '|'
LBRACE  = '{'    RBRACE  = '}'
LPAREN  = '('    RPAREN  = ')'
LBRACK  = '['    RBRACK  = ']'
LANGLE  = '<'    RANGLE  = '>'

; Reserved tokens (§4.5)
AT      = '@'
BANG    = '!'
STAR    = '*'
DOLLAR  = '$'
AMP     = '&'
TILDE   = '~'
BTICK   = '`'

; --- Scalar literals ---

INT         = ['-'] DIGIT_SEP
            | ['-'] '0x' HEX_SEP
            | ['-'] '0b' BIN_DIGIT (BIN_DIGIT | '_')*
            | ['-'] '0o' OCT_DIGIT (OCT_DIGIT | '_')*
DEC         = ['-'] DIGIT_SEP? '.' DIGIT_SEP
FLOAT       = ['-'] DIGIT_SEP? ('.' DIGIT_SEP)? ('e' | 'E') [+-]? DIGIT+
BOOL        = 'true' | 'false'
NIL         = 'nil'

DATE        = DIGIT{4} '-' DIGIT{2} '-' DIGIT{2}
TZ          = 'Z' | [+-] DIGIT{2} ':' DIGIT{2}
TIME        = DIGIT{2} ':' DIGIT{2} ':' DIGIT{2} ('.' DIGIT+)? TZ
DATETIME    = DATE 'T' TIME
UUID        = HEX{8} '-' HEX{4} '-' HEX{4} '-' HEX{4} '-' HEX{12}

BIN         = 'x' "'" HEX_DIGIT* "'"
            | 'b' "'" BASE64_CHAR* "'"

; --- String literals ---

ESCAPE      = '\' ('\' | "'" | '"' | 'n' | 'r' | 't')
            | '\u' HEX_DIGIT{4}
            | '\U' HEX_DIGIT{8}

string_char = ESCAPE | any code point except matching_quote, '\', NL, U+0000
raw_char    = any code point except matching_quote, U+0000
ml_char     = ESCAPE | any code point except U+0000, not forming closing triple-quote
raw_ml_char = any code point except U+0000, not forming closing triple-quote

STRING      = "'" string_char* "'"  | '"' string_char* '"'
RAW_STR     = 'r' "'" raw_char* "'" | 'r' '"' raw_char* '"'
ML_STR      = "'''" ml_char* "'''"  | '"""' ml_char* '"""'
ML_RAW      = "r'''" raw_ml_char* "'''" | 'r"""' raw_ml_char* '"""'

ATOM        = PIPE IDENT
```

### A.2 Syntactic Grammar

```
; --- Document structure ---

document    = statement*
statement   = assignment | stream

assignment  = IDENT type_annot ASSIGN value
stream      = IDENT type_annot STREAM stream_body

; --- Stream body ---
;     Terminates at EOF or the start of the next statement (IDENT COLON).

stream_body = list_stream_body | map_stream_body
list_stream_body = (value (SEP value)* SEP?)?
map_stream_body  = (map_entry (SEP map_entry)* SEP?)?

; --- Type annotations ---

type_annot  = COLON type '?'?

type        = scalar_type
            | atom_set
            | struct_type
            | tuple_type
            | list_type
            | map_type

scalar_type = 'str' | 'int' | 'dec' | 'float' | 'bool'
            | 'uuid' | 'date' | 'time' | 'datetime' | 'bin'

atom_set    = PIPE IDENT (COMMA IDENT)* PIPE

struct_type = LBRACE field_decl (COMMA field_decl)* RBRACE
field_decl  = IDENT COLON type '?'?

tuple_type  = LPAREN type (COMMA type)* RPAREN
list_type   = LBRACK type '?'? RBRACK
map_type    = LANGLE type SEMI type RANGLE

; --- Values ---

value       = scalar | NIL | atom_val
            | struct_val | tuple_val | list_val | map_val

scalar      = STRING | RAW_STR | ML_STR | ML_RAW
            | INT | DEC | FLOAT | BOOL
            | UUID | DATE | TIME | DATETIME | BIN

atom_val    = ATOM

struct_val  = LBRACE (value (SEP value)* SEP?)? RBRACE
tuple_val   = LPAREN (value (SEP value)* SEP?)? RPAREN
list_val    = LBRACK (value (SEP value)* SEP?)? RBRACK
map_val     = LANGLE (map_entry (SEP map_entry)* SEP?)? RANGLE
map_entry   = value SEMI value

; --- Spec files ---

spec        = spec_member*
spec_member = IDENT type_annot
```

### A.3 Reserved Keywords

The following identifiers are reserved and cannot be used as statement names or atom set members:

```
true   false   nil
```
