// Package encoding implements the canonical Go library for the PAKT data
// interchange format. It provides streaming decode, typed marshal/unmarshal,
// and encoding.
//
// # Decoder
//
// [Decoder] reads PAKT input from an [io.Reader] and emits [Event] values one
// at a time. Each grammatical construct — assignment, pack, struct, tuple,
// list, map, scalar — maps to a distinct [EventKind].
//
// # Events
//
// The event model is minimal and machine-oriented:
//   - Root statements emit AssignStart/End or ListPackStart/End / MapPackStart/End
//   - Composite values emit StructStart/End, TupleStart/End, ListStart/End, MapStart/End
//   - Scalar values emit ScalarValue with a [TypeKind] (integer, not string)
//
// # StatementReader
//
// [StatementReader] is the primary deserialization interface. It wraps a
// [Decoder] and provides statement-level navigation with iterator-based
// pack streaming:
//
//	sr := encoding.NewStatementReader(r)
//	defer sr.Close()
//	for stmt := range sr.Statements() {
//	    switch stmt.Name {
//	    case "config":
//	        cfg, err := encoding.ReadValue[Config](sr)
//	    case "events":
//	        for event := range encoding.PackItems[LogEvent](sr) {
//	            process(event)
//	        }
//	    }
//	}
//
// # Marshal / Unmarshal
//
// [Marshal] and [UnmarshalNew] convert between Go structs and PAKT text, using
// struct tags (`pakt:"name"`) for field mapping. [Encoder] provides low-level
// control over output formatting.
//
// # Errors
//
// Parse errors are reported as [*ParseError] with source position and a
// numeric [ErrorCode] matching spec §11 categories. Use [errors.Is] to check
// sentinel categories like [ErrUnexpectedEOF] or [ErrTypeMismatch].
// Deserialization errors are reported as [*DeserializeError] with additional
// statement and field context.
package encoding
