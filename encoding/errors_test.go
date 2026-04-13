package encoding

import (
	"errors"
	"testing"
)

func TestDeserializeErrorFormatting(t *testing.T) {
	tests := []struct {
		name string
		err  DeserializeError
		want string
	}{
		{
			name: "with property and field",
			err: DeserializeError{
				Pos:      Pos{Line: 5, Col: 10},
				Property: "config",
				Field:    "port",
				Message:  "invalid value",
			},
			want: "config.port (5:10): invalid value",
		},
		{
			name: "with property no field",
			err: DeserializeError{
				Pos:      Pos{Line: 3, Col: 1},
				Property: "server",
				Message:  "type mismatch",
			},
			want: "server (3:1): type mismatch",
		},
		{
			name: "no property no field",
			err: DeserializeError{
				Pos:     Pos{Line: 1, Col: 1},
				Message: "unexpected event",
			},
			want: "(1:1): unexpected event",
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := tt.err.Error()
			if got != tt.want {
				t.Errorf("got %q, want %q", got, tt.want)
			}
		})
	}
}

func TestDeserializeErrorUnwrap(t *testing.T) {
	inner := errors.New("root cause")
	err := &DeserializeError{
		Pos:     Pos{Line: 1, Col: 1},
		Message: "wrap",
		Err:     inner,
	}
	if !errors.Is(err, inner) {
		t.Error("expected Unwrap to return inner error")
	}

	// nil Err
	err2 := &DeserializeError{Message: "no inner"}
	if err2.Unwrap() != nil {
		t.Error("expected nil Unwrap when Err is nil")
	}
}

func TestErrorCodeError(t *testing.T) {
	tests := []struct {
		code ErrorCode
		want string
	}{
		{ErrUnexpectedEOF, "unexpected_eof"},
		{ErrTypeMismatch, "type_mismatch"},
		{ErrNilNonNullable, "nil_non_nullable"},
		{ErrSyntax, "syntax"},
		{ErrorCode(99), "error_99"}, // unknown code
	}
	for _, tt := range tests {
		t.Run(tt.want, func(t *testing.T) {
			got := tt.code.Error()
			if got != tt.want {
				t.Errorf("got %q, want %q", got, tt.want)
			}
		})
	}
}

func TestNewParseError(t *testing.T) {
	pe := NewParseError(Pos{Line: 2, Col: 5}, "something broke")
	if pe.Pos.Line != 2 || pe.Pos.Col != 5 {
		t.Errorf("wrong position: %+v", pe.Pos)
	}
	if pe.Message != "something broke" {
		t.Errorf("wrong message: %q", pe.Message)
	}
	want := "2:5: something broke"
	if pe.Error() != want {
		t.Errorf("got %q, want %q", pe.Error(), want)
	}
	if pe.Unwrap() != nil {
		t.Error("expected nil Unwrap for uncategorized error")
	}
}

func TestParseErrorWrap(t *testing.T) {
	pe := Wrap(Pos{Line: 10, Col: 3}, "nil not allowed", ErrNilNonNullable)
	if pe.Wrapped != ErrNilNonNullable {
		t.Errorf("wrong wrapped code: %v", pe.Wrapped)
	}
	if !errors.Is(pe, ErrNilNonNullable) {
		t.Error("expected errors.Is to match ErrNilNonNullable")
	}
	if pe.Code() != int(ErrNilNonNullable) {
		t.Errorf("wrong Code(): %d", pe.Code())
	}
}

func TestParseErrorWrapf(t *testing.T) {
	pe := Wrapf(Pos{Line: 1, Col: 1}, ErrSyntax, "bad token %q", "@@")
	if pe.Message != `bad token "@@"` {
		t.Errorf("wrong message: %q", pe.Message)
	}
	if !errors.Is(pe, ErrSyntax) {
		t.Error("expected errors.Is to match ErrSyntax")
	}
}

func TestParseErrorErrorf(t *testing.T) {
	pe := Errorf(Pos{Line: 7, Col: 12}, "unexpected %s", "token")
	if pe.Message != "unexpected token" {
		t.Errorf("wrong message: %q", pe.Message)
	}
	want := "7:12: unexpected token"
	if pe.Error() != want {
		t.Errorf("got %q, want %q", pe.Error(), want)
	}
}
