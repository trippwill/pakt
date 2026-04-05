// Package encoding implements the canonical Go library for the PAKT data
// interchange format. It provides streaming decode, typed marshal/unmarshal,
// encoding, and spec-based projection.
//
// # Decoder
//
// [Decoder] reads PAKT input from an [io.Reader] and emits [Event] values one
// at a time. Each grammatical construct — assignment, stream, struct, tuple,
// list, map, scalar — maps to a distinct [EventKind]. An optional [Spec]
// projection filters the stream to matched fields, skipping everything else
// without allocation.
//
// # Events
//
// The event model is minimal and machine-oriented:
//   - Root statements emit AssignStart/End or ListStreamStart/End / MapStreamStart/End
//   - Composite values emit StructStart/End, TupleStart/End, ListStart/End, MapStart/End
//   - Scalar values emit ScalarValue with a [TypeKind] (integer, not string)
//
// # Marshal / Unmarshal
//
// [Marshal] and [Unmarshal] convert between Go structs and PAKT text, using
// struct tags (`pakt:"name"`) for field mapping. [Encoder] provides low-level
// control over output formatting.
//
// # Errors
//
// Parse errors are reported as [*ParseError] with source position and a
// numeric [ErrorCode] matching spec §11 categories. Use [errors.Is] to check
// sentinel categories like [ErrUnexpectedEOF] or [ErrTypeMismatch].
package encoding
