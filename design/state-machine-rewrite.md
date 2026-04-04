# State Machine Decoder — Design Document

## Goals

1. **True streaming**: emit one event per `Decode()` call with zero buffering. The caller sees each event the instant it is parseable, not after the entire assignment is materialized.
2. **Constant memory per nesting level**: replace the Go call stack with an explicit stack. Memory scales with nesting depth, not document size.
3. **Preserve all observable behavior**: every test in the existing suite must pass with identical event sequences, error positions, and sentinel error types.
4. **Support new spec constructs**: the state machine must accommodate `<<` streams, `;` map separator, `bin` type, and raw strings — but those can be wired in incrementally. The architecture must not preclude them.
5. **Keep type parsing recursive**: type annotations are bounded-depth and parsed once per assignment. They do not benefit from a state machine and can remain recursive descent.

---

## Function Classification

Every function in the current recursive-descent reader falls into one of three categories for the state machine rewrite.

### A. Parser States

These are the points in the grammar where the state machine can **yield an event** to the caller and resume later. Each becomes a value in the `parserState` enum.

| State | Current RD function(s) | Yields event | Next state(s) |
|-------|----------------------|--------------|----------------|
| `stateTop` | `readAssignment` entry | — | `stateAssignStart` or EOF |
| `stateAssignStart` | `readAssignment` after ident+type+`=` | `EventAssignStart` | `stateValue` |
| `stateValue` | `readValue` entry dispatch | — (dispatch) | scalar/composite open |
| `stateScalar` | `readScalarValue`, `readAtomValue` | `EventScalarValue` | pop stack → parent |
| `stateNil` | `readValue` nil path | `EventScalarValue(nil)` | pop stack → parent |
| `stateStructOpen` | `readStructValue` after `{` | `EventCompositeStart` | `stateStructField` |
| `stateStructField` | `readStructValue` loop body | — (push child value) | `stateValue` |
| `stateStructSep` | `readStructValue` between fields | — | `stateStructField` or `stateStructClose` |
| `stateStructClose` | `readStructValue` at `}` | `EventCompositeEnd` | pop stack → parent |
| `stateTupleOpen` | `readTupleValue` after `(` | `EventCompositeStart` | `stateTupleElem` |
| `stateTupleElem` | `readTupleValue` loop body | — (push child value) | `stateValue` |
| `stateTupleSep` | `readTupleValue` between elems | — | `stateTupleElem` or `stateTupleClose` |
| `stateTupleClose` | `readTupleValue` at `)` | `EventCompositeEnd` | pop stack → parent |
| `stateListOpen` | `readListValue` after `[` | `EventCompositeStart` | `stateListElem` |
| `stateListElem` | `readListValue` loop body | — | `stateValue` or `stateListClose` |
| `stateListSep` | `readListValue` between elems | — | `stateListElem` or `stateListClose` |
| `stateListClose` | `readListValue` at `]` | `EventCompositeEnd` | pop stack → parent |
| `stateMapOpen` | `readMapValue` after `<` | `EventCompositeStart` | `stateMapKey` |
| `stateMapKey` | `readMapValue` key reading | `EventScalarValue` (key) | `stateMapSemi` |
| `stateMapSemi` | `readMapValue` expecting `;` | — | `stateMapVal` |
| `stateMapVal` | `readMapValue` value reading | — (push child value) | `stateValue` |
| `stateMapEntry` | `readMapValue` between entries | — | `stateMapKey` or `stateMapClose` |
| `stateMapClose` | `readMapValue` at `>` | `EventCompositeEnd` | pop stack → parent |
| `stateAssignEnd` | `readAssignment` epilogue | `EventAssignEnd` | `stateTop` |

### B. Helper Actions

These are called synchronously **within** a state transition. They never yield events and never need to be states. They remain as methods on the reader.

**Byte-level I/O** (unchanged):
- `peekByte`, `readByte`, `unreadByte`
- `skipBOM`, `ensureBOM`
- `expectByte`

**Whitespace & comments** (unchanged):
- `skipWS`, `skipWSAndNewlines`
- `skipComment`, `skipInsignificant`

**Character classification** (unchanged):
- `isAlpha`, `isDigit`, `isHex`, `isBin`, `isOct`, `hexVal`

**Scalar readers** (unchanged — leaf operations that complete in one call):
- `readString`, `readEscape`, `readUnicodeEscape`
- `readMultiLineString`, `readRawLine`
- `readInt`, `readDec`, `readFloat`
- `readBool`, `readNil`
- `readDate`, `readTime`, `readDateTime`
- `readUUID`, `readAtom`
- `readScalarDirect`, `peekNil`

**Separator handling** (unchanged):
- `readSep`

**Error construction** (unchanged):
- `errorf`, `wrapf`

**String helpers** (unchanged):
- `countLeadingWS`, `processEscapes`, `parseHexDigits`

**Type parsing** (stays recursive descent — bounded depth):
- `readTypeAnnot`, `readType`, `readScalarType`
- `readAtomSetType`, `readStructType`, `readFieldDecl`
- `readTupleType`, `readListType`, `readMapType`
- `lookupScalarType`

**Skip functions** (unchanged — already iterative):
- `skipValue`, `skipString`, `skipTripleQuotedString`
- `skipComposite`, `skipCompositeInner`
- `skipToNewline`, `skipKeywordOrAtom`, `skipNumberLike`

### C. Explicit Stack Frame Payloads

Each nesting level in the value tree needs a stack frame that captures the state the RD code currently stores on the Go call stack.

```go
type frameKind int

const (
    frameAssign frameKind = iota
    frameStruct
    frameTuple
    frameList
    frameMap
)

type frame struct {
    kind      frameKind
    resume    parserState  // state to resume after child value completes

    // Assignment-level (kind == frameAssign)
    name      string       // root name (for AssignEnd event)
    typeStr   string       // type annotation string (for AssignEnd event)

    // Composite shared
    pos       Pos          // position of opening delimiter (for CompositeStart)

    // Struct (kind == frameStruct)
    st        *StructType
    fieldIdx  int          // next field index to parse

    // Tuple (kind == frameTuple)
    tt        *TupleType
    elemIdx   int

    // List (kind == frameList)
    lt        *ListType
    elemIdx   int

    // Map (kind == frameMap)
    mt        *MapType
    seen      map[string]struct{} // duplicate key detection
    keyStr    string              // current key string (names the value event)
}
```

> **Note**: `fieldIdx` / `elemIdx` is used by both tuple and list. Since only one of struct/tuple/list/map is active per frame, they could overlap, but separate fields are clearer and safer.

---

## State Transition Narrative

### Document top level

```
stateTop:
    skipInsignificant(true)
    if EOF → return io.EOF
    read IDENT → check root uniqueness in `seen` map
    read type annotation (recursive descent — bounded)
    skip WS, expect '='
    push frame{kind: frameAssign, name: ident, typeStr: typ.String()}
    set pending value type/name from assignment
    → stateAssignStart
```

### Assignment lifecycle

```
stateAssignStart:
    yield Event{Kind: EventAssignStart, Pos: identPos, Name: name, Type: typeStr}
    → stateValue (with valueType/valueName set from assignment)

stateAssignEnd:
    yield Event{Kind: EventAssignEnd, Pos: r.pos, Name: name, Type: typeStr}
    pop assignment frame
    → stateTop
```

### Value dispatch

```
stateValue:
    skip WS
    type = pending valueType, name = pending valueName

    if nullable and peekNil():
        read "nil"
        yield EventScalarValue{Name: name, Type: type.String(), Value: "nil"}
        → parent resume state

    if !nullable and peekNil():
        return ErrNilNonNullable

    switch type:
        Scalar  → read scalar direct, yield EventScalarValue → parent resume
        AtomSet → read atom, yield EventScalarValue → parent resume
        Struct  → push frameStruct, → stateStructOpen
        Tuple   → push frameTuple, → stateTupleOpen
        List    → push frameList, → stateListOpen
        Map     → push frameMap, → stateMapOpen
```

### Struct

```
stateStructOpen:
    expect '{', capture pos before consuming
    yield EventCompositeStart{Pos: pos, Name: name, Type: st.String()}
    skipInsignificant(true)
    check for premature '}' → arity error if fields expected
    frame.fieldIdx = 0
    → stateStructField

stateStructField:
    field = frame.st.Fields[frame.fieldIdx]
    set pending: valueType=field.Type, valueName=field.Name
    frame.resume = stateStructSep
    → stateValue

stateStructSep:
    frame.fieldIdx++
    if frame.fieldIdx < len(frame.st.Fields):
        sep, err := readSep()
        if !sep → check for premature '}' (arity error) or missing separator error
        → stateStructField
    else:
        readSep() // optional trailing
        skipInsignificant(true)
        → stateStructClose

stateStructClose:
    expect '}'
    yield EventCompositeEnd{Pos: r.pos, Type: st.String()}
    pop frame
    → parent resume state
```

### Tuple (mirrors struct with positional indexing)

```
stateTupleOpen:
    expect '(', yield EventCompositeStart
    frame.elemIdx = 0
    → stateTupleElem

stateTupleElem:
    elem = frame.tt.Elements[frame.elemIdx]
    set pending: valueType=elem, valueName=indexName(frame.elemIdx)
    frame.resume = stateTupleSep
    → stateValue

stateTupleSep:
    frame.elemIdx++
    if more elements → consume SEP → stateTupleElem
    else → consume trailing SEP → stateTupleClose

stateTupleClose:
    expect ')', yield EventCompositeEnd
    pop frame → parent resume
```

### List (dynamic length)

```
stateListOpen:
    expect '[', yield EventCompositeStart
    skipInsignificant(true)
    if ']' → stateListClose (empty list)
    frame.elemIdx = 0
    → stateListElem

stateListElem:
    set pending: valueType=frame.lt.Element, valueName=indexName(frame.elemIdx)
    frame.resume = stateListSep
    → stateValue

stateListSep:
    frame.elemIdx++
    consume optional SEP
    skipInsignificant(true)
    if ']' → stateListClose
    → stateListElem

stateListClose:
    consume ']', yield EventCompositeEnd
    pop frame → parent resume
```

### Map

```
stateMapOpen:
    expect '<', yield EventCompositeStart
    frame.seen = make(map[string]struct{})
    skipInsignificant(true)
    if '>' → stateMapClose (empty map)
    → stateMapKey

stateMapKey:
    read key (scalar/atom — always a leaf read, no recursion)
    yield EventScalarValue for key
    check duplicate in frame.seen → ErrDuplicateKey if found
    frame.keyStr = key string
    → stateMapSemi

stateMapSemi:
    skip WS, expect ';'
    skip WS
    set pending: valueType=frame.mt.Value, valueName=frame.keyStr
    frame.resume = stateMapEntry
    → stateValue

stateMapEntry:
    consume optional SEP
    skipInsignificant(true)
    if '>' → stateMapClose
    → stateMapKey

stateMapClose:
    consume '>', yield EventCompositeEnd
    pop frame → parent resume
```

---

## Observable Behaviors That Must Not Change

### Event stream contract
- `EventAssignStart` has Name + Type; `EventAssignEnd` has same Name + Type
- `EventCompositeStart` has Name (from parent) + Type; `EventCompositeEnd` has Type only (Name="")
- `EventScalarValue` has Name + Type + Value
- Struct fields named by type declaration; tuple/list elements named `[i]`; map values named by key string
- Nil: `EventScalarValue` with Value=`"nil"` and Type including `?`

### Event ordering
- Struct: `CompositeStart → field₀ → field₁ → ... → CompositeEnd` (positional, left-to-right)
- Tuple: `CompositeStart → elem₀ → elem₁ → ... → CompositeEnd`
- List: `CompositeStart → elem₀ → elem₁ → ... → CompositeEnd`
- Map: `CompositeStart → key₀ → val₀ → key₁ → val₁ → ... → CompositeEnd`
- Assignment: `AssignStart → value events → AssignEnd`

### Error semantics (sentinel errors)
- `ErrDuplicateName` — duplicate root names
- `ErrDuplicateKey` — duplicate map keys
- `ErrNilNonNullable` — nil for non-nullable type
- `ErrUnexpectedEOF` — unterminated strings/composites/types
- `ParseError` includes `Pos{Line, Col}` — positions must match exactly

### Whitespace / separator rules
- `skipInsignificant(true)` between top-level statements
- `skipInsignificant(true)` after opening delimiters and after separators
- Trailing separators valid in all composites
- Block and inline forms produce identical event streams

### Projection / skip
- `skipValue()` unchanged (already iterative with depth counter)
- Spec-based projection skips non-matching fields completely
- Missing spec fields produce error at EOF

### Resource management
- `bufio.Reader` pooled and returned on Close/EOF/error
- Safe to call Close multiple times

---

## Risky Areas — Implicit State in RD Code

### 1. `readMapValue` local `seen` map — HIGH

```go
seen := make(map[string]struct{})  // local to call frame
```

Must move into the map stack frame. Forgetting it silently disables duplicate key detection.
**Covered by**: `TestReadMapDuplicateKeyError`, `TestSentinelErrDuplicateKey`

### 2. Struct/tuple field index — HIGH

```go
for i, field := range st.Fields { ... }
```

Loop variable `i` is call-stack state. Must be in the stack frame. Arity checking ("too few values") depends on `i` vs `len(st.Fields)`.
**Covered by**: `TestReadStructTooFewFields`, `TestReadTupleTooFewElements`

### 3. Nullable/nil dispatch ordering — MEDIUM

Nil check happens *before* dispatching to composite reader. In the state machine, `stateValue` must handle this before pushing a composite frame.
**Covered by**: `TestReadNilValue`, `TestReadNilNonNullableError`

### 4. Map key string capture — MEDIUM

Key is read and emitted within the map reader, then its string is used to name the subsequent value event. The key string must be stored in the frame between key-read and value-read states.
**Covered by**: `TestReadMapInline`, `TestDecodeMapAssignment`

### 5. Position capture timing — MEDIUM

Position is captured *before* consuming delimiters. In the state machine, capture pos in the frame before the consuming transition.
**Covered by**: `TestDecodeEventStream` (validates exact positions)

### 6. Sep-or-close logic interleaved with arity checking — MEDIUM

After each struct/tuple field, the RD code checks: separator present? If not, is it the closing delimiter? This combined check must be replicated exactly.
**Covered by**: `TestReadStructTrailingSep`, `TestReadTupleTrailingSep`

### 7. `peekNil` speculative 256-byte lookahead — LOW

Peeks 256 bytes to check for "nil". Works because bufio has 4KB. Not a rewrite risk but fragile for edge cases.
**Covered by**: `TestPeekNilTrue`, `TestPeekNilWithSpaces`

### 8. Multi-line string batched indentation — LOW (addressed by spec change)

Current impl buffers all lines. Spec change (first-content-line baseline) enables line-by-line stripping, but the helper still collects all lines. Not a state machine concern since it's a leaf operation.

---

## Proposed File Structure

```
encoding/
  reader.go          — byte-level ops, scalar readers (UNCHANGED)
  reader_type.go     — type parsing, recursive descent (UNCHANGED)
  reader_state.go    — NEW: parserState enum, frame type, state machine step()
  decoder.go         — MODIFIED: Decode() calls sm.step()
  spec.go            — MODIFIED: readAssignmentWithSpec uses state machine
  reader_value.go    — DELETED after migration
  event.go           — UNCHANGED
  types.go           — UNCHANGED
  errors.go          — UNCHANGED
  encoder.go         — UNCHANGED
  marshal.go         — UNCHANGED
  unmarshal.go       — UNCHANGED (uses Decoder public API)
  tags.go            — UNCHANGED
```

### Core state machine type

```go
type stateMachine struct {
    r       *reader
    stack   []frame
    state   parserState
    // Per-value (set before stateValue dispatch)
    valType Type
    valName string
}

func (sm *stateMachine) step() (Event, error) {
    for {
        switch sm.state {
        case stateTop:
            // read ident, type, '=', push assignment frame
        case stateAssignStart:
            // yield EventAssignStart, transition to stateValue
            return ev, nil
        case stateValue:
            // nil check, then dispatch by type
        case stateScalar:
            // yield EventScalarValue, pop to parent resume
            return ev, nil
        // ... all composite states ...
        case stateAssignEnd:
            // yield EventAssignEnd, pop to stateTop
            return ev, nil
        }
    }
}
```

Each case either **yields** (returns an event) or **transitions** (continues the loop).

---

## Migration Strategy

1. **Build `reader_state.go` alongside existing code** — new file, no modifications yet.
2. **Create parallel constructor** `NewStreamingDecoder` that uses the state machine.
3. **Run the full test suite against both** — compare event streams for identity.
4. **Benchmark both** to validate streaming and allocation improvements.
5. **Switch default** once verified.
6. **Delete `reader_value.go`** and old Decode path.
