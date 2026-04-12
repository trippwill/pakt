package encoding

import (
	"io"
	"iter"
	"reflect"
)

// PackItems returns an iterator over the elements of a pack statement.
// Each element is deserialized into type T.
//
// On error, iteration stops. Call [StatementReader.Err] after the loop.
//
// If the caller breaks out of the loop early, the iterator drains the
// remaining pack elements (without deserializing them) so the reader is
// positioned at the next statement.
func PackItems[T any](sr *StatementReader) iter.Seq[T] {
	return func(yield func(T) bool) {
		if sr.current == nil || !sr.inPack {
			sr.setErr(&DeserializeError{Message: "PackItems called outside a pack statement"})
			return
		}

		endKind := sr.endKindForCurrent()

		for {
			ev, err := sr.dec.Decode()
			if err != nil {
				if err != io.EOF {
					sr.setErr(err)
				}
				sr.current = nil
				return
			}

			// Check for pack end.
			if ev.Kind == endKind {
				sr.current = nil
				return
			}

			// Deserialize the element.
			var val T
			target := reflect.ValueOf(&val).Elem()
			target = allocPtr(target)
			if err := handleValueEvent(sr, ev, target); err != nil {
				sr.setErr(err)
				// Drain remaining pack events.
				drainUntil(sr, endKind)
				return
			}

			if !yield(val) {
				// Caller broke out of loop — drain remaining pack events.
				drainUntil(sr, endKind)
				return
			}
		}
	}
}

// PackItemsInto returns an iterator that reuses a caller-provided buffer.
// On each iteration, the buffer is populated with the next element.
// The yielded pointer aliases the buffer — do not retain across iterations.
//
// Early break drains remaining pack elements.
func PackItemsInto[T any](sr *StatementReader, buf *T) iter.Seq[*T] {
	return func(yield func(*T) bool) {
		if sr.current == nil || !sr.inPack {
			sr.setErr(&DeserializeError{Message: "PackItemsInto called outside a pack statement"})
			return
		}

		endKind := sr.endKindForCurrent()

		for {
			ev, err := sr.dec.Decode()
			if err != nil {
				if err != io.EOF {
					sr.setErr(err)
				}
				sr.current = nil
				return
			}

			if ev.Kind == endKind {
				sr.current = nil
				return
			}

			// Zero the buffer and populate.
			*buf = *new(T)
			target := reflect.ValueOf(buf).Elem()
			target = allocPtr(target)
			if err := handleValueEvent(sr, ev, target); err != nil {
				sr.setErr(err)
				drainUntil(sr, endKind)
				return
			}

			if !yield(buf) {
				drainUntil(sr, endKind)
				return
			}
		}
	}
}

// drainUntil reads and discards events until the matching end event.
func drainUntil(sr *StatementReader, endKind EventKind) {
	depth := 0
	for {
		ev, err := sr.dec.Decode()
		if err != nil {
			sr.current = nil
			return
		}
		if ev.Kind.IsCompositeStart() || ev.Kind.IsPackStart() {
			depth++
		} else if ev.Kind.IsCompositeEnd() || ev.Kind.IsPackEnd() {
			if depth == 0 && ev.Kind == endKind {
				sr.current = nil
				return
			}
			depth--
		}
	}
}
