---
title: "Examples"
description: "Annotated PAKT examples — common patterns and real-world usage."
weight: 4
---

These examples are drawn from the PAKT test suite. Each demonstrates a different aspect of the format with commentary.

---

## Scalar Types

Every value in PAKT carries an explicit type. Here are all the scalar types:

```
greeting:str     = 'hello world'
count:int        = 42
hex:int          = 0xFF
binary:int       = 0b1010_0011
octal:int        = 0o77
big:int          = 1_000_000
negative:int     = -273
price:dec        = 19.99
avogadro:float   = 6.022e23
active:bool      = true
inactive:bool    = false
id:uuid          = 550e8400-e29b-41d4-a716-446655440000
started:date     = 2026-06-01
opened:time      = 09:30:00-04:00
updated:datetime = 2026-06-01T14:30:00Z
payload:bin      = x'48656C6C6F'
```

**Key points:**

- Integers support decimal, hex (`0x`), binary (`0b`), and octal (`0o`) literals
- Underscores (`_`) are visual separators — `1_000_000` is the same as `1000000`
- `dec` is arbitrary-precision decimal; `float` is IEEE 754 binary64
- `bin` accepts both hex (`x'...'`) and base64 (`b'...'`) forms
- Time and datetime values require a timezone (`Z` or offset like `-04:00`)

---

## String Varieties

Strings are always quoted. PAKT supports single quotes, double quotes, raw strings, escape sequences, and multi-line triple-quoted strings:

```
# Single and double quotes are interchangeable
single:str = 'hello world'
double:str = "hello world"

# Standard escape sequences
newline:str   = 'line one\nline two'
tabbed:str    = "col1\tcol2"
backslash:str = 'C:\\Users\\alice'

# Unicode escapes
bmp-escape:str  = '\u2603'        # snowman ☃
full-escape:str = '\U0001F600'    # grinning face 😀

# Raw strings
windows-path:str = r'C:\Users\alice\Documents'
pattern:str      = r"^\d{3}-\d{4}$"
```

### Multi-line strings

Triple quotes (`'''` or `"""`) open a multi-line string. The first non-blank content line determines how much leading whitespace is stripped:

```
# Indentation stripping: the first content line has 4 leading spaces, so 4 are stripped from each non-blank content line
query:str = '''
    SELECT id, name
    FROM users
    WHERE active = true
    '''
# Result: "SELECT id, name\nFROM users\nWHERE active = true"

# No stripping when the first non-blank content line starts at column 0
raw:str = '''
no indent here
second line
'''
# Result: "no indent here\nsecond line"

# Double-quoted multi-line works the same way
poem:str = """
    Roses are red,
    Violets are blue,
    PAKT is typed,
    And so are you.
    """

# Raw multi-line strings keep backslashes literal
template:str = r'''
    Hello \n World
    '''
```

---

## Atoms

Atoms are bareword identifiers constrained to a declared set. They're similar to enums — the type declares the allowed values:

```
level:|dev, staging, prod| = prod
status:|active, inactive, suspended| = active
color:|red, green, blue| = blue
priority:|low, medium, high, critical| = high
```

**Note:** `true`, `false`, and `nil` are reserved keywords and cannot be used as atom values.

---

## Structs

Structs are collections of named, typed fields. The type annotation declares the shape; values are positional:

```
# Block form — one value per line
server:{host:str, port:int, debug:bool} = {
    'localhost'
    8080
    false
}

# Inline form — comma-separated on one line
origin:{x:int, y:int} = { 0, 0 }
```

Values are matched left-to-right against the fields in the type annotation. The names and types live in the annotation — the value block contains only data.

---

## Tuples

Tuples are ordered sequences of typed values. Like structs, but without field names:

```
# Inline tuple
version:(int, int, int) = (3, 45, 5678)

# Block form
version:(int, int, int) = (
    3
    45
    5678
)

# Decimal point tuple
point:(dec, dec) = (1.5, 2.5)
```

---

## Lists

Lists are homogeneous sequences — every element has the same type:

```
# Inline list
tags:[str] = ['alpha', 'beta', 'release']

# Block list
ids:[int] = [
    12
    14
    26
    78
]

# Empty lists are valid
empty-list:[str] = []
```

---

## Maps

Maps are key-value collections. The `;` in the type declares key and value types; the `;` in entries separates individual keys from values:

```
# Block map
users:<int ; str> = <
    1 ; 'Alice'
    2 ; 'Bob'
    3 ; 'Charlie'
>

# Inline map
codes:<str ; int> = <'us' ; 1, 'gb' ; 44, 'jp' ; 81>

# Empty maps are valid
cache:<str ; int> = <>
```

### Maps with composite values

Map values can be structs, tuples, or any other composite:

```
roster:<int ; {gn:str, fn:str, admin:bool}> = <
    01 ; { 'Johnson', 'Amy', true }
    02 ; { 'Smith', 'Bob', false }
>
```

**Note:** Duplicate keys within a map are preserved in encounter order. Higher-level consumers decide what that means for their domain.

---

## Nullable Types

Append `?` to any type to make it nullable. A nullable field can hold `nil`:

```
# Nullable scalars
nickname:str? = nil
score:int?    = 42

# Nullable atoms
role:|admin, user|?       = nil
status:|active, inactive|? = active

# Nullable composites
config:{host:str, port:int}? = nil

# Lists of nullable elements
sparse:[int?] = [1, nil, 3, nil, 5]

# Various nullable types
maybe-flag:bool?      = nil
maybe-price:dec?      = 9.99
maybe-stamp:datetime? = nil
```

Using `nil` with a non-nullable type is a parse error — you must opt in with `?`.

---

## A Realistic Document

This example shows a production deployment configuration using structs, lists, maps, tuples, nullable types, and atoms together:

```
# Deployment configuration
# Generated for environment: production

app-name:str = 'midwatch'
version:(int, int, int) = (2, 14, 0)

# Deployment target
deploy:{level:|dev, staging, prod|, release:int, date:date} = {
    prod
    26
    2026-06-01
}

# Feature flags
features:[str] = [
    'dark-mode'
    'notifications'
    'audit-log'
]

# Primary database connection
db:{host:str, port:int, name:str, pool-size:int} = {
    'db.prod.internal'
    5432
    'midwatch_prod'
    20
}

# Replica endpoints — list of structs
replicas:[{host:str, port:int}] = [
    { 'replica-1.prod.internal', 5432 }
    { 'replica-2.prod.internal', 5432 }
]

# Nullable fields
tls-fingerprint:str? = 'sha256:a1b2c3d4e5f6'
rollback-version:(int, int, int)? = nil    # not set

# Service metadata map
meta:<str ; str> = <
    'owner'  = 'platform-team'
    'region' ; 'us-east-1'
    'tier'   = 'critical'
>

# Health check configuration
health:{endpoint:str, interval-sec:int, timeout-sec:int, healthy-threshold:int} = {
    '/healthz'
    30
    5
    3
}

active:bool        = true
instance-count:int = 4
started:datetime   = 2026-06-01T14:30:00Z
```

This document uses nearly every PAKT feature: scalars, strings, atoms, structs, tuples, lists, maps, nullable types, and comments. Every field is self-describing — the type annotation tells you exactly what to expect.

---

## Spec File

A `.spec.pakt` file declares what a consumer expects — types without values:

```
# deploy.spec.pakt
deploy:{level:|dev, staging, prod|, release:int, date:date}
version:(int, int, int)
```

When used as a projection, only `deploy` and `version` are materialized from the data stream. All other fields are skipped without allocation or processing.

Different consumers can define different specs against the same document:

```
# audit.spec.pakt — only cares about the release number
deploy:{release:int}
```

```
# ops.spec.pakt — cares about deploy details and health
deploy:{level:|dev, staging, prod|, date:date}
health:{endpoint:str, interval-sec:int}
active:bool
```

One document, multiple projections — each consumer sees exactly what it needs.
