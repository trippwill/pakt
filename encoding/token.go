package encoding

import "fmt"

// TokenKind identifies the lexical category of a [Token].
type TokenKind int

const (
	// Identifiers and keywords.

	TokenIdent    TokenKind = iota // bare identifier
	TokenAssign                    // '='
	TokenColon                     // ':'
	TokenComma                     // ','
	TokenPipe                      // '|'
	TokenHash                      // '#'
	TokenLBrace                    // '{'
	TokenRBrace                    // '}'
	TokenLParen                    // '('
	TokenRParen                    // ')'
	TokenLBrack                    // '['
	TokenRBrack                    // ']'
	TokenLAngle                    // '<'
	TokenRAngle                    // '>'
	TokenString                    // single- or double-quoted string
	TokenMLString                  // triple-quoted multi-line string
	TokenInt                       // integer literal (dec, hex, bin, oct)
	TokenDec                       // exact decimal literal
	TokenFloat                     // IEEE 754 float literal
	TokenBool                      // 'true' or 'false'
	TokenNil                       // 'nil'
	TokenDate                      // ISO date
	TokenTime                      // ISO time with timezone
	TokenDateTime                  // ISO datetime with timezone
	TokenUUID                      // UUID literal
	TokenAtom                      // atom value (bare ident in atom-set context)
	TokenNewline                   // significant newline (separator)
	TokenEOF                       // end of input
	TokenIllegal                   // unrecognized or malformed token
)

var tokenKindNames = [...]string{
	TokenIdent:    "Ident",
	TokenAssign:   "Assign",
	TokenColon:    "Colon",
	TokenComma:    "Comma",
	TokenPipe:     "Pipe",
	TokenHash:     "Hash",
	TokenLBrace:   "LBrace",
	TokenRBrace:   "RBrace",
	TokenLParen:   "LParen",
	TokenRParen:   "RParen",
	TokenLBrack:   "LBrack",
	TokenRBrack:   "RBrack",
	TokenLAngle:   "LAngle",
	TokenRAngle:   "RAngle",
	TokenString:   "String",
	TokenMLString: "MLString",
	TokenInt:      "Int",
	TokenDec:      "Dec",
	TokenFloat:    "Float",
	TokenBool:     "Bool",
	TokenNil:      "Nil",
	TokenDate:     "Date",
	TokenTime:     "Time",
	TokenDateTime: "DateTime",
	TokenUUID:     "UUID",
	TokenAtom:     "Atom",
	TokenNewline:  "Newline",
	TokenEOF:      "EOF",
	TokenIllegal:  "Illegal",
}

// String returns the human-readable name for the token kind.
func (k TokenKind) String() string {
	if int(k) >= 0 && int(k) < len(tokenKindNames) {
		return tokenKindNames[k]
	}
	return fmt.Sprintf("TokenKind(%d)", int(k))
}

// Token is a single lexical unit produced by the [Scanner].
type Token struct {
	Kind    TokenKind // lexical category
	Literal string    // raw text of the token
	Line    int       // 1-based line number
	Col     int       // 1-based column number
}
