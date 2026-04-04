package encoding

import (
	"encoding/json"
	"fmt"
)

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

// MarshalJSON serializes EventKind as its string name.
func (k EventKind) MarshalJSON() ([]byte, error) {
	return json.Marshal(k.String())
}

// UnmarshalJSON deserializes an EventKind from its string name.
func (k *EventKind) UnmarshalJSON(data []byte) error {
	var s string
	if err := json.Unmarshal(data, &s); err != nil {
		return err
	}
	for i, name := range eventKindNames {
		if name == s {
			*k = EventKind(i)
			return nil
		}
	}
	return fmt.Errorf("unknown EventKind %q", s)
}

// Pos represents a position (line and column) in the source input.
type Pos struct {
	Line int `json:"line"` // 1-based line number
	Col  int `json:"col"`  // 1-based column number
}

// Event is a single element in the decoded PAKT stream.
type Event struct {
	Kind  EventKind `json:"kind"`            // category of event
	Pos   Pos       `json:"pos"`             // source position
	Name  string    `json:"name,omitempty"`  // assignment or field name (empty for positional values)
	Type  string    `json:"type,omitempty"`  // type annotation as written in the source
	Value string    `json:"value,omitempty"` // literal value text (empty for composite/structural events)
	Err   error     `json:"-"`               // non-nil only when Kind == EventError; handled by custom MarshalJSON
}

// String returns a tab-separated representation of the event:
//
//	EVENT\tLINE:COL\tNAME\tTYPE\tVALUE
func (e Event) String() string {
	return fmt.Sprintf("%s\t%d:%d\t%s\t%s\t%s",
		e.Kind, e.Pos.Line, e.Pos.Col, e.Name, e.Type, e.Value)
}

// MarshalJSON produces a JSON object for the Event.
// When Err is non-nil, an "error" field is included with the error message.
func (e Event) MarshalJSON() ([]byte, error) {
	type eventAlias Event // prevent infinite recursion
	a := struct {
		eventAlias
		Error string `json:"error,omitempty"`
	}{
		eventAlias: eventAlias(e),
	}
	if e.Err != nil {
		a.Error = e.Err.Error()
	}
	return json.Marshal(a)
}
