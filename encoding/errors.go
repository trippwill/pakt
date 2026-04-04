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

// Errorf returns a new [ParseError] at the given position with a formatted message.
func Errorf(pos Pos, format string, args ...any) *ParseError {
	return &ParseError{Pos: pos, Message: fmt.Sprintf(format, args...)}
}

// Wrap returns a new [ParseError] that wraps an underlying error.
func Wrap(pos Pos, msg string, err error) *ParseError {
	return &ParseError{Pos: pos, Message: msg, Wrapped: err}
}

// Error implements the [error] interface.
func (e *ParseError) Error() string {
	return fmt.Sprintf("%d:%d: %s", e.Pos.Line, e.Pos.Col, e.Message)
}

// Unwrap returns the underlying error, if any.
func (e *ParseError) Unwrap() error {
	return e.Wrapped
}
