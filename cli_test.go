package main_test

import (
	"encoding/json"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
)

var binaryPath string

func TestMain(m *testing.M) {
	dir, err := os.MkdirTemp("", "pakt-cli-test-*")
	if err != nil {
		panic(err)
	}
	defer func() { _ = os.RemoveAll(dir) }()

	binaryPath = filepath.Join(dir, "pakt")
	cmd := exec.Command("go", "build", "-o", binaryPath, ".")
	cmd.Stderr = os.Stderr
	if err := cmd.Run(); err != nil {
		panic("failed to build binary: " + err.Error())
	}

	os.Exit(m.Run())
}

func TestParseTextOutput(t *testing.T) {
	out, err := exec.Command(binaryPath, "parse", "testdata/valid/scalars.pakt").Output()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	lines := strings.Split(strings.TrimSpace(string(out)), "\n")
	if len(lines) == 0 {
		t.Fatal("expected output lines, got none")
	}
	for i, line := range lines {
		if !strings.Contains(line, "\t") {
			t.Errorf("line %d: expected tab-separated fields, got %q", i, line)
		}
	}
}

func TestParseJSONOutput(t *testing.T) {
	out, err := exec.Command(binaryPath, "parse", "testdata/valid/scalars.pakt", "--format", "json").Output()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	lines := strings.Split(strings.TrimSpace(string(out)), "\n")
	if len(lines) == 0 {
		t.Fatal("expected output lines, got none")
	}
	for i, line := range lines {
		var obj map[string]interface{}
		if err := json.Unmarshal([]byte(line), &obj); err != nil {
			t.Errorf("line %d: invalid JSON: %v\n  line: %s", i, err, line)
			continue
		}
		if _, ok := obj["kind"]; !ok {
			t.Errorf("line %d: missing 'kind' field", i)
		}
		if _, ok := obj["pos"]; !ok {
			t.Errorf("line %d: missing 'pos' field", i)
		}
	}
}

func TestParseExplicitText(t *testing.T) {
	out, err := exec.Command(binaryPath, "parse", "testdata/valid/scalars.pakt", "--format", "text").Output()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	lines := strings.Split(strings.TrimSpace(string(out)), "\n")
	if len(lines) == 0 {
		t.Fatal("expected output lines, got none")
	}
	// Should be tab-separated text, not JSON
	for i, line := range lines {
		if strings.HasPrefix(line, "{") {
			t.Errorf("line %d: expected text format, got JSON-like output: %q", i, line)
		}
		if !strings.Contains(line, "\t") {
			t.Errorf("line %d: expected tab-separated fields, got %q", i, line)
		}
	}
}

func TestValidateSuccess(t *testing.T) {
	cmd := exec.Command(binaryPath, "validate", "testdata/valid/scalars.pakt")
	if err := cmd.Run(); err != nil {
		t.Fatalf("expected exit code 0, got error: %v", err)
	}
}

func TestValidateFailure(t *testing.T) {
	cmd := exec.Command(binaryPath, "validate", "testdata/invalid/nil-non-nullable.pakt")
	out, err := cmd.CombinedOutput()
	if err == nil {
		t.Fatal("expected non-zero exit code, got 0")
	}
	if exitErr, ok := err.(*exec.ExitError); ok {
		if exitErr.ExitCode() == 0 {
			t.Fatal("expected non-zero exit code")
		}
	}
	if len(out) == 0 {
		t.Fatal("expected error output on stderr, got none")
	}
}

func TestParseStdin(t *testing.T) {
	f, err := os.Open("testdata/valid/scalars.pakt")
	if err != nil {
		t.Fatalf("opening test file: %v", err)
	}
	defer func() { _ = f.Close() }()

	cmd := exec.Command(binaryPath, "parse", "-")
	cmd.Stdin = f
	out, err := cmd.Output()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	lines := strings.Split(strings.TrimSpace(string(out)), "\n")
	if len(lines) == 0 {
		t.Fatal("expected output from stdin, got none")
	}
}

func TestParseWithSpec(t *testing.T) {
	cmd := exec.Command(binaryPath, "parse", "testdata/valid/full.pakt",
		"--spec", "testdata/valid/spec-example.spec.pakt")
	out, err := cmd.Output()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	lines := strings.Split(strings.TrimSpace(string(out)), "\n")
	if len(lines) == 0 {
		t.Fatal("expected output with spec projection, got none")
	}
}

func TestFormatEnvVar(t *testing.T) {
	cmd := exec.Command(binaryPath, "parse", "testdata/valid/scalars.pakt")
	cmd.Env = append(os.Environ(), "PAKT_FORMAT=json")
	out, err := cmd.Output()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	lines := strings.Split(strings.TrimSpace(string(out)), "\n")
	if len(lines) == 0 {
		t.Fatal("expected output lines, got none")
	}
	// Every line should be valid JSON
	for i, line := range lines {
		var obj map[string]interface{}
		if err := json.Unmarshal([]byte(line), &obj); err != nil {
			t.Errorf("line %d: expected JSON from env var override, got invalid JSON: %v", i, err)
		}
	}
}

func TestVersion(t *testing.T) {
	out, err := exec.Command(binaryPath, "version").Output()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	s := strings.TrimSpace(string(out))
	if !strings.HasPrefix(s, "pakt ") {
		t.Errorf("expected version string starting with 'pakt ', got %q", s)
	}
}

func TestNoArgs(t *testing.T) {
	cmd := exec.Command(binaryPath)
	out, err := cmd.CombinedOutput()
	if err == nil {
		t.Fatal("expected non-zero exit code with no args, got 0")
	}
	if len(out) == 0 {
		t.Fatal("expected usage output, got none")
	}
}
