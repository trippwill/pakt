package encoding

import (
	"io"
	"iter"
	"reflect"
)

// PackItems returns an iterator over the elements of a pack statement.
// Each element is deserialized into type T.
//
// On error, iteration stops. Call [UnitReader.Err] after the loop.
//
// If the caller breaks out of the loop early, the iterator drains the
// remaining pack elements (without deserializing them) so the reader is
// positioned at the next statement.
func PackItems[T any](sr *UnitReader) iter.Seq[T] {
	return func(yield func(T) bool) {
		if sr.current == nil || !sr.inPack {
			sr.setErr(&DeserializeError{Message: "PackItems called outside a pack statement"})
			return
		}

		for {
			ev, err := sr.nextEvent()
			if err != nil {
				if err != io.EOF {
					sr.setErr(err)
				}
				// EOF or pack-end: nextEvent cleared sr.current
				return
			}

			// Deserialize the element.
			var val T
			target := reflect.ValueOf(&val).Elem()
			target = allocPtr(target)
			if err := handleValueEvent(sr, ev, target); err != nil {
				sr.setErr(err)
				// Drain remaining pack events.
				sr.drainCurrent()
				return
			}

			if !yield(val) {
				// Caller broke out of loop — drain remaining pack events.
				sr.drainCurrent()
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
func PackItemsInto[T any](sr *UnitReader, buf *T) iter.Seq[*T] {
	return func(yield func(*T) bool) {
		if buf == nil {
			sr.setErr(&DeserializeError{Message: "PackItemsInto requires a non-nil buffer"})
			return
		}
		if sr.current == nil || !sr.inPack {
			sr.setErr(&DeserializeError{Message: "PackItemsInto called outside a pack statement"})
			return
		}

		for {
			ev, err := sr.nextEvent()
			if err != nil {
				if err != io.EOF {
					sr.setErr(err)
				}
				// EOF or pack-end: nextEvent cleared sr.current
				return
			}

			// Zero the buffer and populate.
			*buf = *new(T)
			target := reflect.ValueOf(buf).Elem()
			target = allocPtr(target)
			if err := handleValueEvent(sr, ev, target); err != nil {
				sr.setErr(err)
				sr.drainCurrent()
				return
			}

			if !yield(buf) {
				sr.drainCurrent()
				return
			}
		}
	}
}

// drainCurrent reads and discards events until the current statement ends.
// It uses nextEvent to properly track nesting depth.
func (sr *UnitReader) drainCurrent() {
	for {
		_, err := sr.nextEvent()
		if err != nil {
			// io.EOF means statement ended; other errors are also terminal.
			return
		}
	}
}
