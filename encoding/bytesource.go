package encoding

import (
	"bufio"
	"io"
)

// byteSource abstracts the byte-level input operations used by the reader.
type byteSource interface {
	// PeekByte returns the next byte without consuming it.
	PeekByte() (byte, error)
	// ReadByte reads and returns a single byte.
	ReadByte() (byte, error)
	// UnreadByte pushes back the last byte read.
	UnreadByte() error
	// Peek returns up to n bytes without consuming them.
	// Returns whatever is available (may be less than n).
	Peek(n int) ([]byte, error)
	// Discard skips n bytes.
	Discard(n int)
}

// bufioSource wraps a *bufio.Reader as a byteSource.
type bufioSource struct {
	br *bufio.Reader
}

func (s *bufioSource) PeekByte() (byte, error) {
	p, err := s.br.Peek(1)
	if err != nil {
		return 0, err
	}
	return p[0], nil
}

func (s *bufioSource) ReadByte() (byte, error) {
	return s.br.ReadByte()
}

func (s *bufioSource) UnreadByte() error {
	return s.br.UnreadByte()
}

func (s *bufioSource) Peek(n int) ([]byte, error) {
	return s.br.Peek(n)
}

func (s *bufioSource) Discard(n int) {
	s.br.Discard(n) //nolint:errcheck
}

func (s *bufioSource) Reset(r io.Reader) {
	s.br.Reset(r)
}
