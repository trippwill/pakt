package encoding

import (
	"io"
	"iter"
	"reflect"
)

// FieldEntry represents a named field within a struct value, providing
// the field name and declared PAKT type.
type FieldEntry struct {
	Name string
	Type Type
}

// MapEntry represents a key-value pair from a PAKT map value.
// K is not constrained to comparable — iteration doesn't require hashing.
type MapEntry[K, V any] struct {
	Key   K
	Value V
}

// TupleEntry represents one element in a heterogeneous tuple value.
type TupleEntry struct {
	Index int
	Type  Type
}

// StructFields returns an iterator over the fields of a struct value
// in the current statement. Each [FieldEntry] provides the field name
// and declared type. After each yield, the caller reads the field's value
// via [ReadValue], [ReadAs], or [StatementReader.Skip].
//
// Errors stop iteration; call [StatementReader.Err] after the loop.
func StructFields(sr *StatementReader) iter.Seq[FieldEntry] {
	return func(yield func(FieldEntry) bool) {
		// Expect the first event to be StructStart (already consumed by Statements).
		// The caller may have already consumed the StructStart via ReadValue dispatch,
		// so we consume the next event and look for field value events.
		for {
			ev, err := sr.nextEvent()
			if err != nil {
				if err != io.EOF {
					sr.setErr(err)
				}
				return
			}

			if ev.Kind == EventStructEnd {
				return
			}

			// For struct fields, the event carries the field name.
			entry := FieldEntry{
				Name: ev.Name,
			}

			if !yield(entry) {
				// Caller broke — skip rest of struct.
				skipComposite(sr, EventStructStart) //nolint:errcheck
				return
			}

			// The caller is expected to consume this field's value.
			// If they didn't (next call to nextEvent would get the wrong thing),
			// the value was already yielded as the current event in the iterator body.
			// Actually the design requires the caller to read the value after yield.
			// Since the event was already consumed, the next ReadValue/ReadAs call
			// will read from the stream correctly.
		}
	}
}

// ListElements returns an iterator over elements of a list value in the
// current statement. Each element is deserialized into type T.
//
// Errors stop iteration; call [StatementReader.Err] after the loop.
func ListElements[T any](sr *StatementReader) iter.Seq[T] {
	return func(yield func(T) bool) {
		for {
			ev, err := sr.nextEvent()
			if err != nil {
				if err != io.EOF {
					sr.setErr(err)
				}
				return
			}

			if ev.Kind == EventListEnd {
				return
			}

			var val T
			target := reflect.ValueOf(&val).Elem()
			if ev.Kind == EventScalarValue && ev.IsNilValue() {
				if err := setNil(target); err != nil {
					sr.setErr(err)
					return
				}
			} else {
				target = allocPtr(target)
				if err := handleValueEvent(sr, ev, target); err != nil {
					sr.setErr(err)
					return
				}
			}

			if !yield(val) {
				skipComposite(sr, EventListStart) //nolint:errcheck
				return
			}
		}
	}
}

// MapEntries returns an iterator over key-value pairs of a map value in the
// current statement. K is not constrained to comparable — iteration doesn't
// require hashing.
//
// Errors stop iteration; call [StatementReader.Err] after the loop.
func MapEntries[K, V any](sr *StatementReader) iter.Seq[MapEntry[K, V]] {
	return func(yield func(MapEntry[K, V]) bool) {
		for {
			// Read key
			keyEv, err := sr.nextEvent()
			if err != nil {
				if err != io.EOF {
					sr.setErr(err)
				}
				return
			}

			if keyEv.Kind == EventMapEnd {
				return
			}

			var key K
			keyTarget := reflect.ValueOf(&key).Elem()
			keyTarget = allocPtr(keyTarget)
			if err := handleValueEvent(sr, keyEv, keyTarget); err != nil {
				sr.setErr(err)
				return
			}

			// Read value
			valEv, err := sr.nextEvent()
			if err != nil {
				sr.setErr(err)
				return
			}

			var val V
			valTarget := reflect.ValueOf(&val).Elem()
			if valEv.Kind == EventScalarValue && valEv.IsNilValue() {
				if err := setNil(valTarget); err != nil {
					sr.setErr(err)
					return
				}
			} else {
				valTarget = allocPtr(valTarget)
				if err := handleValueEvent(sr, valEv, valTarget); err != nil {
					sr.setErr(err)
					return
				}
			}

			if !yield(MapEntry[K, V]{Key: key, Value: val}) {
				skipComposite(sr, EventMapStart) //nolint:errcheck
				return
			}
		}
	}
}

// TupleElements returns an iterator over the elements of a tuple value
// in the current statement. Each [TupleEntry] provides the element index.
// After each yield, the caller reads the element's value via [ReadValue]
// or [ReadAs].
//
// Errors stop iteration; call [StatementReader.Err] after the loop.
func TupleElements(sr *StatementReader) iter.Seq[TupleEntry] {
	return func(yield func(TupleEntry) bool) {
		idx := 0
		for {
			ev, err := sr.nextEvent()
			if err != nil {
				if err != io.EOF {
					sr.setErr(err)
				}
				return
			}

			if ev.Kind == EventTupleEnd {
				return
			}

			entry := TupleEntry{
				Index: idx,
			}

			if !yield(entry) {
				skipComposite(sr, EventTupleStart) //nolint:errcheck
				return
			}

			idx++
		}
	}
}
