package encoding

import (
	"bufio"
	"io"
)

// byteSource abstracts the byte-level input operations used by the reader.
// Two implementations exist: bufioSource (wrapping bufio.Reader for streaming)
// and bytesSource (operating directly on []byte for Unmarshal).
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

// bytesSource operates directly on a []byte slice with zero buffering overhead.
type bytesSource struct {
	data []byte
	off  int
}

func newBytesSource(data []byte) *bytesSource {
	return &bytesSource{data: data}
}

func (s *bytesSource) PeekByte() (byte, error) {
	if s.off >= len(s.data) {
		return 0, io.EOF
	}
	return s.data[s.off], nil
}

func (s *bytesSource) ReadByte() (byte, error) {
	if s.off >= len(s.data) {
		return 0, io.EOF
	}
	b := s.data[s.off]
	s.off++
	return b, nil
}

func (s *bytesSource) UnreadByte() error {
	if s.off > 0 {
		s.off--
	}
	return nil
}

func (s *bytesSource) Peek(n int) ([]byte, error) {
	remaining := len(s.data) - s.off
	if remaining <= 0 {
		return nil, io.EOF
	}
	if n > remaining {
		return s.data[s.off:], io.EOF
	}
	return s.data[s.off : s.off+n], nil
}

func (s *bytesSource) Discard(n int) {
	s.off += n
	if s.off > len(s.data) {
		s.off = len(s.data)
	}
}
