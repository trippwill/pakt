// Package encoding provides a streaming decoder for the PAKT data interchange
// format. It follows the conventions of [encoding/json]: callers construct a
// [Decoder] around an [io.Reader] and pull events one at a time.
//
// The package also exposes the low-level [Scanner] (lexer) and the typed
// event model used by the decoder.
package encoding
