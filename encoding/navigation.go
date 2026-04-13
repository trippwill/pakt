package encoding

import (
	"io"
	"iter"
	"reflect"
)

// FieldEntry represents a named field within a struct value.
type FieldEntry struct {
	Name string
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
}

// StructFields returns an iterator over the fields of a struct value
// in the current statement. Each [FieldEntry] provides the field name.
// After each yield, the caller reads the field's value via [ReadValue]
// or skips it via [UnitReader.Skip].
//
// Errors stop iteration; call [UnitReader.Err] after the loop.
func StructFields(sr *UnitReader) iter.Seq[FieldEntry] {
	return func(yield func(FieldEntry) bool) {
		for {
			// If the previous field's value wasn't consumed by the caller,
			// the pending event is still set — drain it before reading the next field.
			if sr.pending != nil {
				ev := *sr.pending
				sr.pending = nil
				if err := skipValueEvent(sr, ev); err != nil {
					sr.setErr(err)
					return
				}
			}

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

			entry := FieldEntry{
				Name: ev.Name,
			}

			// Push the event back so the caller's ReadValue/ReadAs
			// picks it up as the field's value.
			sr.pushBack(ev)

			if !yield(entry) {
				// Caller broke — drain pending + skip rest of struct.
				if sr.pending != nil {
					pev := *sr.pending
					sr.pending = nil
					skipValueEvent(sr, pev) //nolint:errcheck
				}
				skipComposite(sr, EventStructStart) //nolint:errcheck
				return
			}
		}
	}
}

// ListElements returns an iterator over elements of a list value in the
// current statement. Each element is deserialized into type T.
//
// Errors stop iteration; call [UnitReader.Err] after the loop.
func ListElements[T any](sr *UnitReader) iter.Seq[T] {
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
// Errors stop iteration; call [UnitReader.Err] after the loop.
func MapEntries[K, V any](sr *UnitReader) iter.Seq[MapEntry[K, V]] {
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
// or skips it via [UnitReader.Skip].
//
// Errors stop iteration; call [UnitReader.Err] after the loop.
func TupleElements(sr *UnitReader) iter.Seq[TupleEntry] {
	return func(yield func(TupleEntry) bool) {
		idx := 0
		for {
			// Drain unconsumed previous element.
			if sr.pending != nil {
				ev := *sr.pending
				sr.pending = nil
				if err := skipValueEvent(sr, ev); err != nil {
					sr.setErr(err)
					return
				}
			}

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

			sr.pushBack(ev)

			if !yield(entry) {
				if sr.pending != nil {
					pev := *sr.pending
					sr.pending = nil
					skipValueEvent(sr, pev) //nolint:errcheck
				}
				skipComposite(sr, EventTupleStart) //nolint:errcheck
				return
			}

			idx++
		}
	}
}
