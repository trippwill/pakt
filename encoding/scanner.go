package encoding

import "io"

// Scanner tokenizes PAKT input from an [io.Reader] into a stream of [Token]
// values. Callers advance through the token stream by calling [Scanner.Scan]
// repeatedly until [TokenEOF] is returned.
type Scanner struct {
	r io.Reader
}

// NewScanner returns a Scanner that reads PAKT source from r.
func NewScanner(r io.Reader) *Scanner {
	return &Scanner{r: r}
}

// Scan reads the next token from the input.
//
// TODO: implement — currently returns EOF immediately.
func (s *Scanner) Scan() (Token, error) {
	return Token{Kind: TokenEOF}, nil
}
