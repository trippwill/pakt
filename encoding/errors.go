package encoding

import "fmt"

// ParseError reports a problem at a specific position in the PAKT source.
type ParseError struct {
	Pos     Pos    // source position where the error was detected
	Message string // human-readable description
	Wrapped error  // optional underlying cause
}

// NewParseError returns a new [ParseError] at the given position.
func NewParseError(pos Pos, msg string) *ParseError {
	return &ParseError{Pos: pos, Message: msg}
}

// Error implements the [error] interface.
func (e *ParseError) Error() string {
	return fmt.Sprintf("%d:%d: %s", e.Pos.Line, e.Pos.Col, e.Message)
}

// Unwrap returns the underlying error, if any.
func (e *ParseError) Unwrap() error {
	return e.Wrapped
}
