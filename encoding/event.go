package encoding

import (
	"encoding/json"
	"fmt"
)

// EventKind classifies the streaming events emitted by a [Decoder].
type EventKind int

const (
	EventAssignStart   EventKind = iota // beginning of a top-level assignment
	EventAssignEnd                      // end of a top-level assignment
	EventListPackStart                  // beginning of a top-level list pack
	EventListPackEnd                    // end of a top-level list pack
	EventMapPackStart                   // beginning of a top-level map pack
	EventMapPackEnd                     // end of a top-level map pack
	EventScalarValue                    // a scalar (or nil/atom) value
	EventStructStart                    // opening delimiter of a struct value
	EventStructEnd                      // closing delimiter of a struct value
	EventTupleStart                     // opening delimiter of a tuple value
	EventTupleEnd                       // closing delimiter of a tuple value
	EventListStart                      // opening delimiter of a list value
	EventListEnd                        // closing delimiter of a list value
	EventMapStart                       // opening delimiter of a map value
	EventMapEnd                         // closing delimiter of a map value
	EventError                          // parse or validation error
)

var eventKindNames = [...]string{
	EventAssignStart:   "AssignStart",
	EventAssignEnd:     "AssignEnd",
	EventListPackStart: "ListPackStart",
	EventListPackEnd:   "ListPackEnd",
	EventMapPackStart:  "MapPackStart",
	EventMapPackEnd:    "MapPackEnd",
	EventScalarValue:   "ScalarValue",
	EventStructStart:   "StructStart",
	EventStructEnd:     "StructEnd",
	EventTupleStart:    "TupleStart",
	EventTupleEnd:      "TupleEnd",
	EventListStart:     "ListStart",
	EventListEnd:       "ListEnd",
	EventMapStart:      "MapStart",
	EventMapEnd:        "MapEnd",
	EventError:         "Error",
}

// IsCompositeStart reports whether k is a composite opening event.
func (k EventKind) IsCompositeStart() bool {
	return k == EventStructStart || k == EventTupleStart || k == EventListStart || k == EventMapStart
}

// IsCompositeEnd reports whether k is a composite closing event.
func (k EventKind) IsCompositeEnd() bool {
	return k == EventStructEnd || k == EventTupleEnd || k == EventListEnd || k == EventMapEnd
}

// IsPackStart reports whether k is a pack opening event.
func (k EventKind) IsPackStart() bool {
	return k == EventListPackStart || k == EventMapPackStart
}

// IsPackEnd reports whether k is a pack closing event.
func (k EventKind) IsPackEnd() bool {
	return k == EventListPackEnd || k == EventMapPackEnd
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
	Kind       EventKind `json:"kind"`                 // category of event
	Pos        Pos       `json:"pos"`                  // source position
	Name       string    `json:"name,omitempty"`       // assignment or field name (empty for positional values)
	Type       *Type     `json:"type,omitempty"`       // declared PAKT type (populated on statement start events)
	ScalarType TypeKind  `json:"scalarType,omitempty"` // scalar type kind (zero for structural events)
	Value      []byte    `json:"value,omitempty"`      // literal value bytes (borrow: valid until next Decode call)
	Err        error     `json:"-"`                    // non-nil only when Kind == EventError; handled by custom MarshalJSON
}

// String returns a tab-separated representation of the event:
//
//	EVENT\tLINE:COL\tNAME\tSCALAR_TYPE\tVALUE
func (e Event) String() string {
	return fmt.Sprintf("%s\t%d:%d\t%s\t%s\t%s",
		e.Kind, e.Pos.Line, e.Pos.Col, e.Name, e.ScalarType, string(e.Value))
}

// ValueString returns the Value as a string. This allocates.
// Prefer working with Value as []byte when possible.
func (e Event) ValueString() string { return string(e.Value) }

// IsNilValue reports whether this scalar event represents a nil value.
func (e Event) IsNilValue() bool {
	return e.Kind == EventScalarValue && len(e.Value) == 3 &&
		e.Value[0] == 'n' && e.Value[1] == 'i' && e.Value[2] == 'l'
}

// MarshalJSON produces a JSON object for the Event.
// Value is encoded as a string (not base64). When Err is non-nil,
// an "error" field is included with the error message.
func (e Event) MarshalJSON() ([]byte, error) {
	a := struct {
		Kind       EventKind `json:"kind"`
		Pos        Pos       `json:"pos"`
		Name       string    `json:"name,omitempty"`
		Type       *Type     `json:"type,omitempty"`
		ScalarType TypeKind  `json:"scalarType,omitempty"`
		Value      string    `json:"value,omitempty"`
		Error      string    `json:"error,omitempty"`
	}{
		Kind:       e.Kind,
		Pos:        e.Pos,
		Name:       e.Name,
		Type:       e.Type,
		ScalarType: e.ScalarType,
		Value:      string(e.Value),
	}
	if e.Err != nil {
		a.Error = e.Err.Error()
	}
	return json.Marshal(a)
}

// UnmarshalJSON reads an Event from JSON, decoding Value from a string
// (not base64) to match [Event.MarshalJSON].
func (e *Event) UnmarshalJSON(data []byte) error {
	var raw struct {
		Kind       EventKind `json:"kind"`
		Pos        Pos       `json:"pos"`
		Name       string    `json:"name"`
		Type       *Type     `json:"type"`
		ScalarType TypeKind  `json:"scalarType"`
		Value      string    `json:"value"`
		Error      string    `json:"error"`
	}
	if err := json.Unmarshal(data, &raw); err != nil {
		return err
	}
	e.Kind = raw.Kind
	e.Pos = raw.Pos
	e.Name = raw.Name
	e.Type = raw.Type
	e.ScalarType = raw.ScalarType
	if raw.Value != "" {
		e.Value = []byte(raw.Value)
	} else {
		e.Value = nil
	}
	if raw.Error != "" {
		e.Err = fmt.Errorf("%s", raw.Error)
	}
	return nil
}
