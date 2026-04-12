package encoding

import (
	"fmt"
)

// ErrorCode identifies a parse failure category per spec §11.2.
// Codes 1–99 are spec-defined; implementations use 100+ for extensions.
type ErrorCode int

const (
	ErrUnexpectedEOF  ErrorCode = 1 // unexpected end of input
	ErrTypeMismatch   ErrorCode = 2 // type mismatch
	ErrNilNonNullable ErrorCode = 3 // nil on non-nullable type
	ErrSyntax         ErrorCode = 4 // syntax error (catch-all)
)

var errorCodeNames = [...]string{
	ErrUnexpectedEOF:  "unexpected_eof",
	ErrTypeMismatch:   "type_mismatch",
	ErrNilNonNullable: "nil_non_nullable",
	ErrSyntax:         "syntax",
}

// Error returns the spec identifier for this error category.
func (e ErrorCode) Error() string {
	if int(e) >= 0 && int(e) < len(errorCodeNames) && errorCodeNames[e] != "" {
		return errorCodeNames[e]
	}
	return fmt.Sprintf("error_%d", int(e))
}

// ParseError reports a problem at a specific position in the PAKT source.
type ParseError struct {
	Pos     Pos       // source position where the error was detected
	Message string    // human-readable description
	Wrapped ErrorCode // error category (zero if uncategorized)
}

// Code returns the spec §11 numeric error code, or 0 if uncategorized.
func (e *ParseError) Code() int { return int(e.Wrapped) }

// NewParseError returns a new [ParseError] at the given position.
func NewParseError(pos Pos, msg string) *ParseError {
	return &ParseError{Pos: pos, Message: msg}
}

// Errorf returns a new [ParseError] at the given position with a formatted message.
func Errorf(pos Pos, format string, args ...any) *ParseError {
	return &ParseError{Pos: pos, Message: fmt.Sprintf(format, args...)}
}

// Wrap returns a new [ParseError] that wraps an underlying error.
func Wrap(pos Pos, msg string, code ErrorCode) *ParseError {
	return &ParseError{Pos: pos, Message: msg, Wrapped: code}
}

// Wrapf returns a new [ParseError] that wraps an underlying error with a formatted message.
func Wrapf(pos Pos, sentinel ErrorCode, format string, args ...any) *ParseError {
	return &ParseError{Pos: pos, Message: fmt.Sprintf(format, args...), Wrapped: sentinel}
}

// Error implements the [error] interface.
func (e *ParseError) Error() string {
	return fmt.Sprintf("%d:%d: %s", e.Pos.Line, e.Pos.Col, e.Message)
}

// Unwrap returns the underlying error category, or nil if uncategorized.
func (e *ParseError) Unwrap() error {
	if e.Wrapped == 0 {
		return nil
	}
	return e.Wrapped
}

// DeserializeError wraps a parse or conversion error with deserialization context.
type DeserializeError struct {
	Pos      Pos    // source position in the PAKT data
	Property string // which unit property (e.g., "config")
	Field    string // which field within a composite (e.g., "port"), or empty
	Message  string // human-readable description
	Err      error  // wrapped underlying error
}

// Error implements the [error] interface.
// Format: "statement.field (line:col): message" or "statement (line:col): message".
func (e *DeserializeError) Error() string {
	loc := fmt.Sprintf("%d:%d", e.Pos.Line, e.Pos.Col)
	if e.Field != "" {
		return fmt.Sprintf("%s.%s (%s): %s", e.Property, e.Field, loc, e.Message)
	}
	if e.Property != "" {
		return fmt.Sprintf("%s (%s): %s", e.Property, loc, e.Message)
	}
	return fmt.Sprintf("(%s): %s", loc, e.Message)
}

// Unwrap returns the underlying error.
func (e *DeserializeError) Unwrap() error { return e.Err }
