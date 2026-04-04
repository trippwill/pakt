package main

import (
	"encoding/json"
	"fmt"
	"io"
	"os"

	"github.com/trippwill/pakt/encoding"
)

// CLI is the top-level Kong command structure.
type CLI struct {
	Parse    ParseCmd    `cmd:"" help:"Parse a PAKT file and emit structured events."`
	Validate ValidateCmd `cmd:"" help:"Validate a PAKT file (errors only, exit 0/1)."`
	Version  VersionCmd  `cmd:"" help:"Print version information."`
}

// ParseCmd reads a PAKT file and emits streaming events to stdout.
type ParseCmd struct {
	File   string `arg:"" help:"Path to .pakt file (use - for stdin)." type:"existingfile"`
	Spec   string `short:"s" optional:"" help:"Path to .spec.pakt for projection." type:"existingfile" env:"PAKT_SPEC"`
	Format string `short:"f" enum:"text,json" default:"text" help:"Output format (text or json)." env:"PAKT_FORMAT"`
}

// ValidateCmd checks a PAKT file for errors without emitting events.
type ValidateCmd struct {
	File string `arg:"" help:"Path to .pakt file (use - for stdin)." type:"existingfile"`
	Spec string `short:"s" optional:"" help:"Path to .spec.pakt for projection." type:"existingfile" env:"PAKT_SPEC"`
}

// VersionCmd prints version information.
type VersionCmd struct{}

// Run executes the parse command.
func (c *ParseCmd) Run(cli *CLI) error {
	r, err := openInput(c.File)
	if err != nil {
		return err
	}
	defer func() { _ = r.Close() }()

	dec := encoding.NewDecoder(r)

	if c.Spec != "" {
		specFile, err := os.Open(c.Spec)
		if err != nil {
			return fmt.Errorf("opening spec: %w", err)
		}
		defer func() { _ = specFile.Close() }()
		if err := dec.SetSpec(specFile); err != nil {
			return fmt.Errorf("loading spec: %w", err)
		}
	}

	jsonEnc := json.NewEncoder(os.Stdout)

	for {
		evt, err := dec.Decode()
		if err == io.EOF {
			break
		}
		if err != nil {
			return err
		}
		switch c.Format {
		case "json":
			if err := jsonEnc.Encode(evt); err != nil {
				return fmt.Errorf("encoding JSON: %w", err)
			}
		default:
			fmt.Println(evt.String())
		}
	}

	return nil
}

// Run executes the validate command.
func (c *ValidateCmd) Run(cli *CLI) error {
	r, err := openInput(c.File)
	if err != nil {
		return err
	}
	defer func() { _ = r.Close() }()

	dec := encoding.NewDecoder(r)

	if c.Spec != "" {
		specFile, err := os.Open(c.Spec)
		if err != nil {
			return fmt.Errorf("opening spec: %w", err)
		}
		defer func() { _ = specFile.Close() }()
		if err := dec.SetSpec(specFile); err != nil {
			return fmt.Errorf("loading spec: %w", err)
		}
	}

	hasErrors := false
	for {
		evt, err := dec.Decode()
		if err == io.EOF {
			break
		}
		if err != nil {
			return err
		}
		if evt.Kind == encoding.EventError {
			fmt.Fprintln(os.Stderr, evt.String())
			hasErrors = true
		}
	}

	if hasErrors {
		os.Exit(1)
	}
	return nil
}

// Run executes the version command.
func (c *VersionCmd) Run(cli *CLI) error {
	fmt.Printf("pakt %s\n", version)
	return nil
}

// openInput opens the given file path, or returns stdin if path is "-".
func openInput(path string) (io.ReadCloser, error) {
	if path == "-" {
		return os.Stdin, nil
	}
	f, err := os.Open(path)
	if err != nil {
		return nil, fmt.Errorf("opening input: %w", err)
	}
	return f, nil
}
