# Design Note: Maps as Tagged Lists of Tuples

**Status**: Exploratory — candidate for v0.2 or later  
**Date**: 2026-05-16

## Observation

PAKT maps provide no uniqueness guarantee for keys (§6.2 — duplicates preserved in encounter order). Semantically, a PAKT map is a list of key-value tuples with an *advisory* associative intent. The current syntax (`<K => V>` type, `<>` value delimiters, `=>` bind operator) introduces a fifth composite kind with dedicated delimiter pair, token type (`MapEntryBind`), and parser state machine (`MapPhase`).

## Proposal

Eliminate maps as a first-class composite. Maps become tagged lists of tuples:

```pakt
# Current v0.1a
meta:<str => str> = <
    'owner'  => 'platform-team'
    'region' => 'us-east-1'
>

# Proposed
meta:@assoc[(str str)] = [
    ('owner' 'platform-team')
    ('region' 'us-east-1')
]
```

### What changes

- **Type syntax**: `<K => V>` → `@assoc[(K V)]` (or `@map[...]`)
- **Value syntax**: `<k => v k => v>` → `[(k v) (k v)]`
- **Delimiters**: `<>` freed, `=>` eliminated from values
- **Composite kinds**: 5 → 4 (struct, tuple, list, map → struct, tuple, list)
- **Token types**: `MapStart`, `MapEnd`, `MapEntryBind` eliminated

### `@tag` mechanism

The `@` prefix (already reserved in v0.1a §4.5) becomes a type tag — advisory metadata that doesn't change the structural type:

```pakt
meta:@assoc[(str str)] = [ ... ]    # associative intent
ids:@set[int] = [ ... ]              # uniqueness intent
scores:@sorted[int] = [ ... ]        # ordering intent
```

Tags are hints for consumers. A parser that ignores tags produces structurally correct results. A deserializer can use tags to select materialization strategy (e.g., `@assoc` → `Dictionary<K,V>`, plain list → `List<(K,V)>`).

### Benefits

1. **Fewer composite kinds** — parser, validator, and source generator all simplify
2. **No delimiter overloading** — `[]` is always list, `()` is always tuple, `{}` is always struct
3. **Wire efficiency** — `('k' 'v')` is 2 bytes overhead vs `'k' => 'v'` at 4 bytes
4. **Reuses existing machinery** — list and tuple parsing already work; no map-specific state machine
5. **Extensible** — `@tag` is a general mechanism, not a one-off for maps

### Costs

1. **More nesting** — every entry gains `()` delimiters (visual noise)
2. **Self-description weaker** — without `@assoc`, a `[(str str)]` doesn't signal associative intent
3. **Breaking change** — all existing map syntax becomes invalid
4. **Tag design needed** — `@tag` syntax, semantics, and which tags are normative vs advisory

### Open questions

- Is `@assoc` normative (parser enforces pair arity) or advisory (consumer interprets)?
- Should tags be in the grammar or the type system?
- What other tags are useful? `@set`, `@sorted`, `@unique`, `@ordered`?
- Does `#` work instead of `@`? (`#` collides with comments visually but not grammatically in type position)
