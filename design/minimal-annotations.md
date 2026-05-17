# Design Note: Minimal Annotations — Structural Only

**Status**: Future exploration  
**Date**: 2026-05-17

## Observation

PAKT value tokens are syntactically unambiguous — every scalar type has a
distinct literal form. This means scalar type names in annotations (`:str`,
`:int`, `:date`, `:ts`, `:uuid`, etc.) are redundant with the value syntax.

## What annotations carry non-redundant information

- **Field names** — `{path size mode is_dir}` — essential for self-description
- **Nullable marker** — `?` — signals that `nil` is valid
- **Atom sets** — `|dev staging prod|` — constrains valid atom values
- **Composite structure** — `[...]`, `(...)`, `{...}`, `<...>` — shape

## What's redundant

Scalar type names: `:str`, `:int`, `:dec`, `:float`, `:bool`, `:date`, `:ts`,
`:uuid`, `:bin`. The value syntax already encodes the type:

```
'hello'                          → str (quote-delimited)
42                               → int (digits)
3.14                             → dec (dot, no exponent)
6.022e23                         → float (exponent)
true / false                     → bool (keyword)
2026-06-01                       → date (YYYY-MM-DD)
2026-06-01T14:30:00Z             → ts (ISO with T)
550e8400-e29b-41d4-a716-...      → uuid (8-4-4-4-12 hex)
x'48656C6C6F'                    → bin (prefix + quote)
```

## What minimal annotations look like

```pakt
# Today
entries:[{path:str size:int mode:int is_dir:bool}] = ~[

# Minimal — scalar types omitted, field names kept
entries:[{path size mode is_dir}] = ~[
```

## Open concern

Unannotated collections of composites lose field names entirely:
```pakt
entries = ~[
    { '/data/file.csv' 36914834 420 false }   # what are these fields?
```
Composite annotations must remain for self-description.

## Relationship to type-directed scanning

If annotations are structural-only (no scalar type names), the reader
classifies scalar types from value syntax alone — which is exactly what
the classifying tokenizer already does. The type annotation becomes
purely about composite shape and field naming, not scalar classification.
