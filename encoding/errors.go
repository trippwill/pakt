package encoding

import (
	"fmt"
)

// ErrorCode identifies a parse failure category per spec §11.2.
// Codes 1–99 are spec-defined; implementations use 100+ for extensions.
type ErrorCode int

const (
	ErrUnexpectedEOF  ErrorCode = 1 // unexpected end of input
	_                 ErrorCode = 2 // reserved (formerly duplicate_name; removed per spec §6.1)
	ErrTypeMismatch   ErrorCode = 3 // type mismatch
	ErrNilNonNullable ErrorCode = 4 // nil on non-nullable type
	ErrSyntax         ErrorCode = 5 // syntax error (catch-all)
)

var errorCodeNames = [...]string{
	ErrUnexpectedEOF:  "unexpected_eof",
	2:                 "",
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
