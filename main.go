// pakt is a CLI tool for parsing and validating PAKT documents.
//
// Install:
//
//	go install github.com/trippwill/pakt@latest
package main

import (
	"fmt"
	"os"

	"github.com/alecthomas/kong"
)

// version is set at build time via -ldflags.
var version = "dev"

func main() {
	cli := &CLI{}
	ctx := kong.Parse(cli,
		kong.Name("pakt"),
		kong.Description("Parse and validate PAKT documents."),
		kong.UsageOnError(),
		kong.Vars{"version": version},
	)
	err := ctx.Run(cli)
	if err != nil {
		fmt.Fprintf(os.Stderr, "error: %v\n", err)
		os.Exit(1)
	}
}
