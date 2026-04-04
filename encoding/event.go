package encoding

import "fmt"

// EventKind classifies the streaming events emitted by a [Decoder].
type EventKind int

const (
	EventAssignStart    EventKind = iota // beginning of a top-level assignment
	EventAssignEnd                       // end of a top-level assignment
	EventScalarValue                     // a scalar (or nil/atom) value
	EventCompositeStart                  // opening delimiter of a composite value
	EventCompositeEnd                    // closing delimiter of a composite value
	EventError                           // parse or validation error
)

var eventKindNames = [...]string{
	EventAssignStart:    "AssignStart",
	EventAssignEnd:      "AssignEnd",
	EventScalarValue:    "ScalarValue",
	EventCompositeStart: "CompositeStart",
	EventCompositeEnd:   "CompositeEnd",
	EventError:          "Error",
}

// String returns the human-readable name for the event kind.
func (k EventKind) String() string {
	if int(k) >= 0 && int(k) < len(eventKindNames) {
		return eventKindNames[k]
	}
	return fmt.Sprintf("EventKind(%d)", int(k))
}

// Pos represents a position (line and column) in the source input.
type Pos struct {
	Line int // 1-based line number
	Col  int // 1-based column number
}

// Event is a single element in the decoded PAKT stream.
type Event struct {
	Kind  EventKind // category of event
	Pos   Pos       // source position
	Name  string    // assignment or field name (empty for positional values)
	Type  string    // type annotation as written in the source
	Value string    // literal value text (empty for composite/structural events)
	Err   error     // non-nil only when Kind == EventError
}

// String returns a tab-separated representation of the event:
//
//	EVENT\tLINE:COL\tNAME\tTYPE\tVALUE
func (e Event) String() string {
	return fmt.Sprintf("%s\t%d:%d\t%s\t%s\t%s",
		e.Kind, e.Pos.Line, e.Pos.Col, e.Name, e.Type, e.Value)
}
