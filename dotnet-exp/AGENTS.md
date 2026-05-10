# AGENTS.md — `dotnet-exp`

This file provides guidance for coding agents working in `dotnet-exp/`.

## Purpose

`dotnet-exp/` is the experimental .NET 10 project skeleton for PAKT. It is intentionally small and currently focuses on:

- project/bootstrap correctness
- Native AOT-friendly defaults
- shared local workflow via `mise`
- minimal VS Code workspace consistency

It is **not** yet the mature `dotnet/` implementation.

## Project layout

```text
dotnet-exp/
├── Pakt.Core/         # Core library skeleton
├── Pakt.Core.Test/    # xUnit v3 + Microsoft.Testing.Platform tests
├── Pakt.Cli/          # CLI skeleton, PublishAot enabled
├── Pakt.Benchmarks/   # BenchmarkDotNet runner project
├── .mise.toml         # Shared task entry points
├── .editorconfig      # Formatting/style source of truth
├── .vscode/           # Minimal checked-in VS Code workspace config
└── artifacts/         # Build output
```

## Build / test / workflow

Prefer the checked-in `mise` tasks over ad hoc shell commands when they fit the task.

### Primary tasks

```sh
mise run build
mise run test
mise run test:coverage
mise run format
mise run format:check
mise run ci
mise run pre-commit
mise run bench
```

### What they do

- `build` → `dotnet build`
- `test` → runs after `build`, uses `dotnet test --no-build`
- `test:coverage` → runs tests with Microsoft Testing Platform coverage and writes Cobertura output to `artifacts/TestResults/coverage.cobertura.xml`
- `format` → `dotnet format`
- `format:check` → `dotnet format --verify-no-changes`
- `ci` → composed task chain for build/test/format verification/AOT smoke
- `pre-commit` → formats, builds, runs tests, then stages tracked updates with `git add -u`
- `bench` → runs the benchmark project via BenchmarkDotNet

If you need the raw commands, they are defined in `.mise.toml`.

## Native AOT / trimming expectations

Shared defaults are defined in `Directory.Build.props`:

- `TargetFramework=net10.0`
- `TreatWarningsAsErrors=true`
- `IsAotCompatible=true`
- `IsTrimmable=true`
- `EnableTrimAnalyzer=true`
- `EnableAotAnalyzer=true`

Implications:

- Prefer AOT-safe, trim-safe code in `Pakt.Core` and `Pakt.Cli`
- Avoid dynamic code generation and reflection-heavy patterns in production code unless explicitly justified
- Fix analyzer warnings rather than suppressing them casually

### Project-specific exceptions

- `Pakt.Cli` has `PublishAot=true`
- `Pakt.Core.Test` opts out of AOT/trimming requirements:
  - `IsAotCompatible=false`
  - `IsTrimmable=false`
- `Pakt.Benchmarks` also opts out of AOT/trimming requirements

## Publish tasks and host constraints

Native AOT publish tasks are defined in `.mise.toml`.

Supported from this Linux host:

```sh
mise run publish:linux-x64
mise run publish:linux-arm64
```

Host-guarded:

- `publish:osx-arm64` → macOS host only
- `publish:win-x64` → Windows host only

Do not assume cross-OS Native AOT publish works from Linux.

## Testing details

- Test framework: `xunit.v3.mtp-v2`
- Runner: `Microsoft.Testing.Platform`
- Coverage: `Microsoft.Testing.Extensions.CodeCoverage`
- Test data is copied from `../../testdata` into the test output as `TestData/...`

When adding tests, prefer using the copied runtime path rather than assuming direct filesystem access to the repo root.

## Formatting / editor expectations

`.editorconfig` is the formatting source of truth.

Key repo-wide expectations already configured:

- LF line endings
- UTF-8 without BOM
- no final newline by default
- C# formatting via `dotnet format`

**Parameter wrapping rule** (not enforced by `dotnet format` — enforced in review):
- Parameter lists must be either **all on one line** or **each on its own indented line**.
- No partial wrapping (e.g., two params on the first line, one on the next).

VS Code workspace settings are intentionally minimal and checked in under `.vscode/`.

## Benchmark project status

`Pakt.Benchmarks` currently contains the BenchmarkDotNet runner only. It may report that no `[Benchmark]` types exist until actual benchmark classes are added. That is expected in the current project state.

## Agent guidance

1. Prefer changing the shared task/config layer instead of introducing duplicate local commands.
2. Keep VS Code settings minimal and repo-specific; avoid personal editor preferences.
3. When adding new workflow commands, expose them through `.mise.toml`.
4. When adding production code, preserve AOT/trim compatibility by default.
5. When changing formatting or build behavior, verify via the existing tasks instead of inventing new scripts.
