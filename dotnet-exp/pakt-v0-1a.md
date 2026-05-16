# PAKT Specification

> **PAKT** — a typed data interchange format. Human-authorable. Streaming. Self-describing.

## 0. Version

This document defines **PAKT 0.1a** (draft experiment). The specification is not stable — breaking changes may occur before PAKT 1.

PAKT uses complete-version specifications. Reader libraries advertise which spec versions they support.

| Version | Status | Meaning |
|---------|--------|---------|
| PAKT 0 | Draft | Earlier draft using comma/newline separators and semicolon map binding |
| PAKT 0.1a | Draft experiment | Layout-only syntax, `=` map binding, single-quoted strings only |
| PAKT 1 | (future) | First stable release; no breaking changes within major version |

### 0.1 Definitions

- **Reader**: The component that consumes PAKT-encoded bytes and produces a token or event stream. A reader validates syntax and type annotations. Implementations may split this into layers (e.g., a tokenizer and a validating wrapper), but the spec addresses them as one logical unit.
- **Producer**: The entity that generates PAKT data — a serializer, encoder, or human author.
- **Consumer**: The entity that processes PAKT data using a reader. A consumer may be a deserializer, materializer, or application-level handler.
- **Conforming reader**: A reader that implements all normative requirements of this specification: grammar, type checking, error codes, streaming, and NUL framing.

### 0.2 Design Principles

1. **Performance is a feature.** The format, grammar, and type system are designed so that a conforming reader can operate in a single streaming pass with minimal allocation. Spec rules must not require unbounded buffering or retroactive reinterpretation.

2. **Type context flows with the data.** Every value carries or inherits its type. The reader never guesses. This enables type-directed reading without schema negotiation.

3. **The reader is lossless; interpretation is layered.** A conforming reader preserves all information present in the source — including duplicate statement names, duplicate map keys, encounter order, raw string content, and raw multi-line string content. Policy decisions such as rejecting duplicates, applying last-wins, accumulating values, stripping indentation, or normalizing presentation belong to higher-level consumers, not the core reader.

4. **Presentation is an application concern.** Human-readable formatting, event enrichment, indentation normalization, and display transformations are not reader or writer responsibilities. The core event contract is minimal and machine-oriented.

5. **The grammar is the event model.** Each grammatical construct — assign, pack, struct, tuple, list, map, scalar — maps to a distinct event kind. Consumers should not need to inspect payload strings to determine structural context.

6. **Layout separates members.** Layout separates adjacent type members, value members, atom members, and map entries. Punctuation marks structure and relationships; it is not used as a general list separator.

## 1. Data Model

A PAKT unit is a sequence of **statements** at the top level. Each statement is a named, typed value assignment: `name:type = value`.

The unit root uses self-describing statements rather than positional values. This is the system boundary where data enters without a parent type context.

```pakt
name:str = 'midwatch'
version:(int int int) = (1 0 0)
```

Streaming collections use `~[` or `~<` to open a collection that may not close. This enables append-only log scenarios where a producer writes elements incrementally:

```pakt
events:[{ts:ts level:|info warn error| msg:str}] = ~[
{ 2026-06-01T14:30:00Z |info 'server started' }
{ 2026-06-01T14:31:00Z |warn 'high latency' }
```

## 2. Encoding

A PAKT unit is a sequence of Unicode code points encoded as **UTF-8**. No other encodings are permitted.

A UTF-8 BOM (`U+FEFF`) at the start of a unit is accepted and ignored. Implementations must not reject a unit that begins with a BOM, and must not emit a BOM when writing.

## 3. Lexical Grammar

### 3.1 Characters and Layout

```ebnf
ALPHA       = 'a'-'z' | 'A'-'Z'
DIGIT       = '0'-'9'
IDENT       = (ALPHA | '_') (ALPHA | DIGIT | '_' | '-')*

WS          = ' ' | '\t' | ','
NL          = '\n' | '\r\n'
LAYOUT_CHAR = WS | NL
COMMENT     = '#' (any char except NL)*
LAYOUT      = (LAYOUT_CHAR | COMMENT)+
```

Layout separates adjacent syntactic items. A comment does not consume the following newline. Comments are trivia that may appear where layout is permitted.

Commas are layout in PAKT 0.1a — they are treated as whitespace and have no semantic meaning. This means PAKT data authored with commas (e.g., from v0 conventions or JSON habits) parses identically to data without them.

### 3.2 Comments

Line comments begin with `#` and extend until, but do not include, the next newline.

```ebnf
COMMENT = '#' (any char except NL)*
```

Comments do not consume newlines. The newline remains visible to the reader and participates in layout.

```pakt
ports:[int] = [
  8080 # public
  8081 # admin
]
```

### 3.3 Scalar Literals

```ebnf
DIGIT_SEP = DIGIT (DIGIT | '_')*
HEX_DIGIT = DIGIT | 'a'-'f' | 'A'-'F'
BIN_DIGIT = '0' | '1'
OCT_DIGIT = '0'-'7'

INT      = ['-'] DIGIT_SEP
         | ['-'] '0x' HEX_DIGIT (HEX_DIGIT | '_')*
         | ['-'] '0b' BIN_DIGIT (BIN_DIGIT | '_')*
         | ['-'] '0o' OCT_DIGIT (OCT_DIGIT | '_')*
DEC      = ['-'] DIGIT_SEP? '.' DIGIT_SEP
FLOAT    = ['-'] DIGIT_SEP? ('.' DIGIT_SEP)? ('e' | 'E') [+-]? DIGIT+
BOOL     = 'true' | 'false'
NIL      = 'nil'
DATE     = DIGIT{4} '-' DIGIT{2} '-' DIGIT{2}
TZ       = 'Z' | [+-] DIGIT{2} ':' DIGIT{2}
TS       = DATE 'T' DIGIT{2} ':' DIGIT{2} ':' DIGIT{2} ('.' DIGIT+)? TZ
UUID     = HEX_DIGIT{8} '-' HEX_DIGIT{4} '-' HEX_DIGIT{4} '-' HEX_DIGIT{4} '-' HEX_DIGIT{12}
BIN      = 'x' "'" HEX_DIGIT* "'"
         | 'b' "'" BASE64_CHAR* "'"
BASE64_CHAR = ALPHA | DIGIT | '+' | '/' | '='

ESCAPE      = '\\' ('\\' | "'" | 'n' | 'r' | 't')
            | '\\u' HEX_DIGIT{4}
string_char = ESCAPE | any code point except "'", '\\', NL, U+0000
raw_char    = any code point except "'", U+0000
ml_char     = ESCAPE | any code point except U+0000, not forming the closing triple-quote
raw_ml_char = any code point except U+0000, not forming the closing triple-quote

STRING   = "'" string_char* "'"
RAW_STR  = 'r' "'" raw_char* "'"
ML_STR   = "'''" ml_char* "'''"
ML_RAW   = 'r' "'''" raw_ml_char* "'''"
ATOM     = '|' IDENT
```

- Leading zeros on decimal `INT` are permitted and ignored (`01` evaluates to `1`).
- `_` in numeric literals is a visual separator, ignored by the reader.
- `ATOM` values use a `|` prefix to distinguish them from identifiers. Atom set type declarations use bare names inside pipe delimiters (`|dev staging prod|`), while atom values use the prefix (`|dev`).
- `bin` literals use a prefix to indicate encoding: `x'...'` for hexadecimal, `b'...'` for base64.
- Hex literals must contain an even number of hex digits; an odd count is a parse error.
- Base64 literals follow RFC 4648 standard encoding. Padding (`=`) is required.
- Strings are always quoted. There are no unquoted string values.
- Double-quoted strings are not part of PAKT 0.1a.

Scalar literal validation is performed against the expected type. A reader need not classify every numeric-looking token before type context is known.

### 3.4 Raw Strings

A raw string is prefixed with `r` and performs no escape processing. Backslashes are literal.

```pakt
path:str = r'C:\Users\alice\Documents'
regex:str = r'^\d{3}-\d{4}$'
```

The only character that cannot appear in a raw single-line string is the unescaped closing single quote. Null bytes (`U+0000`) are not permitted in raw strings.

### 3.5 Multi-line Strings

A multi-line string is delimited by triple single quotes (`'''`). A raw multi-line string is prefixed with `r'''`.

```pakt
query:str = '''
    SELECT id, name
    FROM users
    WHERE active = true
'''
```

The content of a multi-line string is the exact sequence of Unicode code points between the opening and closing delimiters, excluding the delimiters themselves. No indentation stripping is performed by the core reader.

For non-raw multi-line strings, escape sequences are recognized using the same rules as single-line strings. Raw multi-line strings (`r'''...'''`) perform no escape processing.

Applications and higher-level deserializers may apply indentation normalization, common-indent stripping, or leading/trailing newline trimming as presentation or binding policy. Such normalization is not part of the core PAKT data model.

Null bytes (`U+0000`) are not permitted in multi-line strings.

### 3.6 String Escapes

Within a quoted string (not raw), the following escape sequences are recognized:

| Escape | Meaning |
|--------|---------|
| `\\` | Backslash |
| `\'` | Single quote |
| `\n` | Newline (U+000A) |
| `\r` | Carriage return (U+000D) |
| `\t` | Tab (U+0009) |
| `\uXXXX` | Unicode BMP code point (4 hex digits) |

Any other `\` followed by a character is a parse error. Null bytes (`U+0000`) are not permitted in strings, whether literal or escaped. Surrogate code points (`U+D800`–`U+DFFF`) are not valid in `\u` escapes — they are an encoding detail, not code points. To include supplementary-plane characters, use literal UTF-8.

### 3.7 Tokens

```ebnf
HASH    = '#'
ASSIGN  = '='
COLON   = ':'                       ; type annotation only
BIND    = '='                       ; map entry binding (= in map context)
STREAM  = '~'                       ; streaming collection prefix (~[ or ~<)
PIPE    = '|'
QMARK   = '?'
LBRACE  = '{'    RBRACE = '}'
LPAREN  = '('    RPAREN = ')'
LBRACK  = '['    RBRACK = ']'
LANGLE  = '<'    RANGLE = '>'

; Reserved tokens (see §4.5)
DQUOTE  = '"'
AT      = '@'
BANG    = '!'
STAR    = '*'
DOLLAR  = '$'
AMP     = '&'
SEMI    = ';'
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
| `ts` | ISO timestamp (tz recommended) | `2026-06-01T14:30:00Z` |
| `bin` | Binary data | `x'48656C6C6F'`, `b'SGVsbG8='` |

**Numeric precision:**

- **`int`**: Signed 64-bit integer. Range: −9,223,372,036,854,775,808 to 9,223,372,036,854,775,807. Values outside this range are a parse error.
- **`float`**: IEEE 754 binary64 (double precision). Implementations must parse and round per IEEE 754.
- **`dec`**: Arbitrary-precision decimal in the text representation. Implementations must support at least 28 significant digits. An implementation may reject values exceeding its supported precision with a clear error.
- **`bin`**: Raw byte sequence. Hex form (`x'...'`) and base64 form (`b'...'`) are semantically equivalent — both produce the same bytes.

`true`, `false`, and `nil` are reserved keywords, not atoms.

### 4.2 Nullable Types

Any type may be made nullable by appending `?`:

```ebnf
nullable_type = type '?'
```

A nullable type accepts all values of the base type plus `nil`. A non-nullable type receiving `nil` is a parse error.

### 4.3 Atom Sets

An atom set constrains a value to one of a fixed set of bareword identifiers:

```ebnf
atom_set = PIPE IDENT (LAYOUT IDENT)* PIPE
```

Example: `|dev staging prod|`

`true`, `false`, and `nil` are reserved keywords and cannot be used as atoms. Empty atom sets are parse errors.

Atom values in data position use the `|` prefix: `|dev`, `|staging`, `|prod`.

### 4.4 Composite Types

| Kind | Type syntax | Delimiter | Keys | Values |
|------|-------------|-----------|------|--------|
| Struct | `{field:type ...}` | `{ }` | Field names (static) | Heterogeneous |
| Tuple | `(type ...)` | `( )` | None (positional) | Heterogeneous |
| List | `[type]` | `[ ]` | None (ordered) | Homogeneous |
| Map | `<keytype = valtype>` | `< >` | Any typed value | Homogeneous |

Five unique delimiter pairs — no overloads:

| First token after `=` | Kind |
|-----------------------|------|
| `{` | Struct |
| `(` | Tuple |
| `[` | List |
| `<` | Map |
| anything else | Scalar, atom, or nil |

Empty lists (`[]`), empty maps (`<>`), empty structs (`{}`), and empty tuples (`()`) are valid. Empty atom sets are parse errors.

### 4.5 Reserved

The following tokens are reserved for future use or deliberately excluded from PAKT 0.1a. They must not appear in units outside of string literals. A conforming reader encountering a reserved token in an unexpected position should report a syntax error.

| Token | Status / possible future use |
|-------|-------------------------------|
| `"` | Reserved; double-quoted strings are not part of PAKT 0.1a |
| `@` | Constraints (`@len`, `@range`, `@pattern`) |
| `!` | Assertions or negation |
| `*` | Wildcards or glob patterns |
| `$` | Variable references or interpolation |
| `&` | Type aliases (see §4.6) |
| `;` | Reserved |
| `` ` `` | Alternate string delimiters or template literals |

### 4.6 Future Consideration: Type Aliases

> **Status**: Design sketch only. The `&` token is reserved for this purpose.

Type aliases would allow naming a type once and referencing it by name in type positions:

```pakt
&entry = {path:str size:int is_dir:bool hash:bin?}
&level = |info warn error|

entries:[&entry] = ~[...
events:[{ts:ts level:&level msg:str}] = ~[...
```

Planned semantics:

- **`&name = type`** defines a type alias. It is not a statement — it emits no data events and does not participate in root duplicate handling.
- **`&name`** in type position is a structural substitution — the alias expands to the underlying type. There is no nominal typing.
- **Must appear before use.** This is the streaming-compatible ordering rule. Forward references are a parse error.
- **`&` sigil required in both definition and reference.** This keeps alias names and statement names in separate namespaces.
- **Aliases can reference earlier aliases.**

## 5. Syntactic Grammar

### 5.1 Unit

```ebnf
unit      = layout_opt statement*
statement = assign
```

### 5.2 Statement Headers

A statement header consists of:

```ebnf
IDENT type_annot ASSIGN
```

Layout (including newlines) is permitted between any tokens in a statement header. This allows complex type annotations to wrap across lines for readability:

```pakt
# Single-line (conventional for simple types)
name:str = 'midwatch'

# Multi-line type annotation (useful for complex structs)
config:{
    server:{host:str port:int}
    db:{host:str port:int name:str}
} = {
    { 'api.example.com' 443 }
    { 'db.internal' 5432 'myapp' }
}
```

Layout around the statement operator is optional:

```pakt
name:str = 'midwatch'
name:str='midwatch'
```

### 5.3 Assign

```ebnf
assign = IDENT type_annot layout_opt ASSIGN layout_opt value
```

### 5.4 Streaming Collections

A collection value may be opened with a streaming prefix `~` before `[` or `<`. This signals that the closing delimiter is optional — the collection may be terminated by end-of-unit (EOF or NUL) instead.

```ebnf
streaming_list = STREAM LBRACK layout_opt list_members? layout_opt RBRACK?
streaming_map  = STREAM LANGLE layout_opt map_entries? layout_opt RANGLE?
```

Streaming collections enable append-only scenarios (e.g., log files) where a producer writes elements incrementally without knowing the total count. The `~` prefix tells the reader to tolerate a missing close delimiter.

When a streaming collection appears mid-unit (before another statement), the closing delimiter is required. When it appears at the tail of a unit, the closing delimiter may be omitted and end-of-unit terminates the collection.

If the closing delimiter is present, the collection is structurally identical to a non-streaming collection. The `~` is a signal to consumers about streaming intent, not a structural difference.

```pakt
# Mid-unit streaming (] required before next statement)
events:[{ts:ts msg:str}] = ~[
    { 2026-06-01T14:30:00Z 'started' }
    { 2026-06-01T14:30:05Z 'lag detected' }
]
name:str = 'midwatch'

# Tail streaming (] optional, EOF terminates)
log:[{ts:ts msg:str}] = ~[
    { 2026-06-01T14:30:00Z 'started' }
    { 2026-06-01T14:30:05Z 'lag detected' }
```

### 5.5 Type Annotation

```ebnf
type_annot = COLON type
```

Layout around `:` in type annotations is permitted but conventionally omitted. Both forms are valid:

```pakt
name:str
name : str
{level:str release:int}
{level : str release : int}
```

### 5.6 Type

```ebnf
type = (scalar_type | atom_set | struct_type | tuple_type | list_type | map_type) '?'?

scalar_type = 'str' | 'int' | 'dec' | 'float' | 'bool' | 'uuid' | 'date' | 'ts' | 'bin'

atom_set    = PIPE layout_opt IDENT (LAYOUT IDENT)* layout_opt PIPE

struct_type = LBRACE layout_opt (struct_field_decl (LAYOUT struct_field_decl)*)? layout_opt RBRACE

struct_field_decl = IDENT COLON type

tuple_type  = LPAREN layout_opt (type (LAYOUT type)*)? layout_opt RPAREN

list_type   = LBRACK layout_opt type layout_opt RBRACK

map_type    = LANGLE layout_opt type layout_opt BIND layout_opt type layout_opt RANGLE
```

### 5.7 Values

```ebnf
value = scalar | NIL | atom_val | struct_val | tuple_val | list_val | map_val

scalar    = STRING | RAW_STR | ML_STR | ML_RAW | INT | DEC | FLOAT | BOOL | UUID | DATE | TS | BIN
atom_val  = ATOM
```

`nil` is valid only when the type is nullable (`type?`).

### 5.8 Struct Value

Struct values contain positional values matched left-to-right against the fields declared in the type annotation.

```ebnf
struct_val      = LBRACE layout_opt struct_members? layout_opt RBRACE
struct_members  = value (LAYOUT value)*
```

A struct value must contain exactly as many values as the type declares fields. Fewer or more values than declared is a parse error. Arity is validated during parsing.

> **Future consideration**: Self-describing struct members (`name:type = value`) may be added in a future version.

### 5.9 Tuple Value

Tuple values contain positional values matched left-to-right against the types declared in the type annotation.

```ebnf
tuple_val      = LPAREN layout_opt tuple_members? layout_opt RPAREN
tuple_members  = value (LAYOUT value)*
```

A tuple value must contain exactly as many values as the type declares elements. Fewer or more values than declared is a parse error. Arity is validated during parsing.

> **Future consideration**: Self-describing tuple members (`:type value`) may be added in a future version.

### 5.10 List Value

```ebnf
list_val     = LBRACK layout_opt list_members? layout_opt RBRACK
list_members = value (LAYOUT value)*
```

All elements must conform to the declared element type. An empty list (`[]`) is valid.

### 5.11 Map Value

```ebnf
map_val     = LANGLE layout_opt map_entries? layout_opt RANGLE
map_entries = map_entry (LAYOUT map_entry)*
map_entry   = value layout_opt BIND layout_opt value
```

Keys conform to the declared key type. Values conform to the declared value type. Key-value pairs are associated using `=`. An empty map (`<>`) is valid. Duplicate keys are preserved in encounter order; interpreting them is an application/domain concern (see §6).

## 6. Duplicates

### 6.1 Root Statement Duplicates

Duplicate names at the unit root are not a format-level parse error and do not carry built-in replacement semantics.

A conforming reader preserves repeated statements in encounter order. How duplicates are interpreted is an application/domain concern. Higher-level consumers or bindings may choose to reject duplicates, apply first-wins or last-wins semantics, accumulate all values, or preserve raw order for later processing, but they must document that behavior.

The reserved keywords `true`, `false`, and `nil` cannot be used as statement names.

### 6.2 Map Duplicate Keys

Duplicate keys in a map value (`= <...>` or `= ~<...>`) are not a format-level parse error and do not carry built-in replacement semantics in the format itself.

A conforming reader preserves repeated map entries in encounter order. How duplicates are interpreted is an application/domain concern. Higher-level consumers or bindings may choose to reject duplicates, apply first-wins or last-wins semantics, accumulate all values, or preserve raw order for later processing, but they must document that behavior.

Struct field names are declared in the type, not in the value, so duplicates are caught at the type level.

## 7. Layout Rules

- Layout separates adjacent type members, value members, atom members, and map entries.
- Commas are not separators.
- Consecutive layout is equivalent to a single layout separator where layout is permitted.
- Layout before the first member, after the last member, and around delimiters is ignored where the grammar permits `layout_opt`.
- Layout is optional around `=`.

- Layout around `:` in type annotations is permitted but conventionally omitted.
- Indentation is insignificant to the core reader.
- A comment does not consume a newline. The newline remains part of layout.
- A comment participates in layout only where layout is permitted.

Examples:

```pakt
ports:[int] = [8080 8081 8082]

deploy:{level:str release:int active:bool} = {
  'platform'
  26
  true
}

headers:<str = str> = <
  'content-type' = 'application/json'
  'accept' = 'application/json'
>
```

## 8. Structural Equivalence

Block and inline forms are semantically identical. A conforming formatter may freely convert between them as long as layout separation is preserved.

```pakt
# Block struct
deploy:{level:str release:int} = {
  'platform'
  26
}

# Inline struct
deploy:{level:str release:int} = { 'platform' 26 }
```

```pakt
# Block tuple
version:(int int int) = (
  3
  45
  5678
)

# Inline tuple
version:(int int int) = (3 45 5678)
```

## 9. Type Assertions

Type annotations in a PAKT unit are assertions by the producer. They are validated during parsing — a unit that violates its own assertions is malformed.

```pakt
release:int = 26
status:|active inactive| = |active
```

The reader checks each value against its declared type at parse time. A type mismatch is an immediate error.

### 9.1 Future Consideration: Spec Files and Projections

> **Status**: Design sketch only.

External spec files (`.spec.pakt`) and consumer projections may be added in a future version. A spec file would use PAKT type syntax without values to define a consumer's requirements. A projection would let a consumer supply a spec at parse time as a filter — parsing only matching fields and skipping the rest without allocation. These features are deferred until real-world usage patterns are better understood.

## 10. File Conventions

| Extension | Purpose | MIME Type |
|-----------|---------|----------|
| `.pakt` | PAKT data unit | `application/vnd.pakt` |

### 10.1 Transport Framing

When transmitting PAKT units over a raw byte stream (e.g., pipes, sockets, serial links), implementations MAY use a NUL byte (`U+0000`, `0x00`) as a unit delimiter. The NUL byte is not part of the PAKT text — it is a framing sentinel.

Since NUL is forbidden in all PAKT text, it is unambiguous as a boundary marker.

A reader encountering a NUL byte at the top level MUST treat it as end-of-unit. Behavior on encountering NUL inside a syntactic construct is a parse error.

When reading from a file, end-of-file serves as end-of-unit. NUL framing is optional for file-based usage.

## 11. Error Model

A conforming implementation must report parse errors with sufficient detail for programmatic handling and human diagnosis.

PAKT readers are expected to fail fast. A malformed value, malformed type, missing layout separator, invalid reserved token, unterminated string, or type mismatch is an immediate parse error. Readers should not reinterpret malformed input to continue.

### 11.1 Error Structure

Each error MUST include:

- **Code** — a numeric identifier from the table below, or an implementation-defined code ≥ 100
- **Identifier** — a short string name corresponding to the code
- **Position** — source line and column (1-based)
- **Message** — a human-readable description of the problem

### 11.2 Normative Error Categories

Codes 1–99 are reserved for the spec. Implementations MUST support at least the categories below and MUST allow callers to distinguish them programmatically.

| Code | Identifier | Condition |
|------|-----------|-----------|
| 1 | `unexpected_eof` | Input ends before a syntactic construct is complete |
| 2 | `type_mismatch` | A value does not conform to its declared type |
| 3 | `nil_non_nullable` | `nil` appears where the type is not nullable |
| 4 | `syntax` | Any lexical or grammatical error not covered by a more specific category |
| 8 | `arity_mismatch` | A struct or tuple value has too few or too many values |

### 11.3 Extensibility

Implementations MAY define additional error categories with codes ≥ 100. Implementation-defined categories must be documented and must not reuse codes 1–99 for different meanings.

### 11.4 Non-Errors

The following conditions are explicitly not parse errors:

- **Duplicate root statement names** — preserved in encounter order per §6.1 and principle 3.
- **Duplicate map keys** — preserved in encounter order per §6.2 and principle 3.
- **Multi-line string indentation shape** — preserved by the core reader. Indentation stripping or normalization is a consumer policy.

## Appendix A. Collected Formal Grammar

This appendix assembles every grammar rule from the spec body into a single reference. No new rules are introduced — this is a collected view for implementors.

### A.1 Lexical Grammar

```ebnf
; --- Characters and layout ---

ALPHA       = 'a'-'z' | 'A'-'Z'
DIGIT       = '0'-'9'
HEX_DIGIT   = DIGIT | 'a'-'f' | 'A'-'F'
BIN_DIGIT   = '0' | '1'
OCT_DIGIT   = '0'-'7'
DIGIT_SEP   = DIGIT (DIGIT | '_')*
BASE64_CHAR = ALPHA | DIGIT | '+' | '/' | '='

IDENT       = (ALPHA | '_') (ALPHA | DIGIT | '_' | '-')*

WS          = ' ' | '\t' | ','
NL          = '\n' | '\r\n'
LAYOUT_CHAR = WS | NL
COMMENT     = '#' (any char except NL)*
LAYOUT      = (LAYOUT_CHAR | COMMENT)+
layout_opt  = LAYOUT?

; --- Tokens ---

ASSIGN  = '='
COLON   = ':'
BIND    = '='                       ; map entry binding (= in map context)
STREAM  = '~'                       ; streaming collection prefix (~[ or ~<)
PIPE    = '|'
QMARK   = '?'
LBRACE  = '{'    RBRACE  = '}'
LPAREN  = '('    RPAREN  = ')'
LBRACK  = '['    RBRACK  = ']'
LANGLE  = '<'    RANGLE  = '>'

; Reserved tokens
DQUOTE  = '"'
AT      = '@'
BANG    = '!'
STAR    = '*'
DOLLAR  = '$'
AMP     = '&'
SEMI    = ';'
BTICK   = '`'

; --- Scalar literals ---

INT         = ['-'] DIGIT_SEP
            | ['-'] '0x' HEX_DIGIT (HEX_DIGIT | '_')*
            | ['-'] '0b' BIN_DIGIT (BIN_DIGIT | '_')*
            | ['-'] '0o' OCT_DIGIT (OCT_DIGIT | '_')*
DEC         = ['-'] DIGIT_SEP? '.' DIGIT_SEP
FLOAT       = ['-'] DIGIT_SEP? ('.' DIGIT_SEP)? ('e' | 'E') [+-]? DIGIT+
BOOL        = 'true' | 'false'
NIL         = 'nil'

DATE        = DIGIT{4} '-' DIGIT{2} '-' DIGIT{2}
TZ          = 'Z' | [+-] DIGIT{2} ':' DIGIT{2}
TS          = DATE 'T' DIGIT{2} ':' DIGIT{2} ':' DIGIT{2} ('.' DIGIT+)? TZ
UUID        = HEX_DIGIT{8} '-' HEX_DIGIT{4} '-' HEX_DIGIT{4} '-' HEX_DIGIT{4} '-' HEX_DIGIT{12}

BIN         = 'x' "'" HEX_DIGIT* "'"
            | 'b' "'" BASE64_CHAR* "'"

; --- String literals ---

ESCAPE      = '\\' ('\\' | "'" | 'n' | 'r' | 't')
            | '\\u' HEX_DIGIT{4}

string_char = ESCAPE | any code point except "'", '\\', NL, U+0000
raw_char    = any code point except "'", U+0000
ml_char     = ESCAPE | any code point except U+0000, not forming closing triple-quote
raw_ml_char = any code point except U+0000, not forming closing triple-quote

STRING      = "'" string_char* "'"
RAW_STR     = 'r' "'" raw_char* "'"
ML_STR      = "'''" ml_char* "'''"
ML_RAW      = "r'''" raw_ml_char* "'''"

ATOM        = PIPE IDENT
```

### A.2 Syntactic Grammar

```ebnf
; --- Unit structure ---

unit        = layout_opt statement*
statement   = assign

; --- Statement headers ---

assign      = IDENT type_annot layout_opt ASSIGN layout_opt value

; layout (including newlines) is permitted between header tokens

; --- Streaming collections ---
;     ~[ or ~< opens a collection that tolerates missing close delimiter.
;     EOF or NUL terminates. Close delimiter required if more statements follow.

streaming_list = STREAM LBRACK layout_opt list_members? layout_opt RBRACK?
streaming_map  = STREAM LANGLE layout_opt map_entries? layout_opt RANGLE?

; --- Type annotations ---

type_annot  = COLON type

type        = (scalar_type
            | atom_set
            | struct_type
            | tuple_type
            | list_type
            | map_type) QMARK?

scalar_type = 'str' | 'int' | 'dec' | 'float' | 'bool'
            | 'uuid' | 'date' | 'ts' | 'bin'

atom_set    = PIPE layout_opt IDENT (LAYOUT IDENT)* layout_opt PIPE

struct_type = LBRACE layout_opt (field_decl (LAYOUT field_decl)*)? layout_opt RBRACE
field_decl  = IDENT COLON type

tuple_type  = LPAREN layout_opt (type (LAYOUT type)*)? layout_opt RPAREN
list_type   = LBRACK layout_opt type layout_opt RBRACK
map_type    = LANGLE layout_opt type layout_opt BIND layout_opt type layout_opt RANGLE

; --- Values ---

value       = scalar | NIL | atom_val
            | struct_val | tuple_val | list_val | map_val

scalar      = STRING | RAW_STR | ML_STR | ML_RAW
            | INT | DEC | FLOAT | BOOL
            | UUID | DATE | TS | BIN

atom_val    = ATOM

struct_val  = LBRACE layout_opt (value (LAYOUT value)*)? layout_opt RBRACE
tuple_val   = LPAREN layout_opt (value (LAYOUT value)*)? layout_opt RPAREN
list_val    = LBRACK layout_opt (value (LAYOUT value)*)? layout_opt RBRACK
map_val     = LANGLE layout_opt (map_entry (LAYOUT map_entry)*)? layout_opt RANGLE
map_entry   = value layout_opt BIND layout_opt value
```

### A.3 Reserved Keywords

The following identifiers are reserved and cannot be used as statement names or atom set members:

```text
true   false   nil
```

### A.4 Example

```pakt
name:str = 'midwatch'
version:(int int int) = (1 0 0)

env:|dev staging prod| = |dev

headers:<str = str> = <
  'content-type' = 'application/json'
  'accept' = 'application/json'
>

events:[{ts:ts level:|info warn error| msg:str}] = ~[
{ 2026-06-01T14:30:00Z |info 'server started' }
{ 2026-06-01T14:31:00Z |warn 'high latency' }
```
