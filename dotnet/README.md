# PAKT for .NET

A high-performance, streaming-first serialization library for the [PAKT](../spec/pakt-v0.md) typed data interchange format. Designed around zero-copy tokenization, forward-only reading/writing, and source-generated (de)serialization — modeled after `System.Text.Json`.

## Quick Start

### 1. Define Your Types

```csharp
public class Server
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
}
```

### 2. Create a Serializer Context

Decorate a partial class with `[PaktSerializable]` attributes for each type you want to (de)serialize. The source generator emits the implementation at compile time:

```csharp
using Pakt.Serialization;

[PaktSerializable(typeof(Server))]
public partial class AppPaktContext : PaktSerializerContext { }
```

### 3. Read a Document with PaktStreamReader

`PaktStreamReader` is the primary consumption API — it iterates top-level statements one at a time:

```csharp
using Pakt;

await using var reader = PaktStreamReader.Create(paktBytes);

while (await reader.ReadStatementAsync())
{
    Console.WriteLine($"{reader.StatementName}: {reader.StatementType}");

    if (reader.StatementName == "server")
    {
        var server = reader.Deserialize(AppPaktContext.Default.Server);
        Console.WriteLine($"  {server.Host}:{server.Port}");
    }
    else
    {
        await reader.SkipAsync();
    }
}
```

### 4. Convenience: Single-Statement Serialize/Deserialize

For documents with a single assignment, use the `PaktSerializer` static API:

```csharp
// Deserialize
var server = PaktSerializer.Deserialize(paktBytes, AppPaktContext.Default.Server);

// Serialize
byte[] bytes = PaktSerializer.Serialize(server, AppPaktContext.Default.Server, "server");
```

## API Overview

### Layered Architecture

```
  PaktSerializer          High-level convenience (single assignment)
       ↓
  PaktStreamReader        Statement-level iteration (multi-statement docs, streams)
       ↓
  PaktReader / PaktWriter Low-level token-by-token I/O
       ↓
  Source Generator         Compile-time (de)serialization code via [PaktSerializable]
```

### Core Types

| Type | Description |
|---|---|
| `PaktReader` | Forward-only, zero-copy tokenizer over `ReadOnlySpan<byte>`. Ref struct — stack-only, pooled allocations. |
| `PaktWriter` | Forward-only PAKT output writer to `IBufferWriter<byte>`. |
| `PaktStreamReader` | Async statement-level reader. Iterates top-level assignments and streams. Supports `Deserialize<T>()` and `ReadStreamElements<T>()`. |
| `PaktSerializer` | Static convenience API for single-statement documents. |
| `PaktSerializerContext` | Base class for source-generated serialization contexts. |
| `PaktTypeInfo<T>` | Generated metadata + delegate pair for a serializable type. |
| `PaktType` | Immutable PAKT type descriptor (scalars, structs, tuples, lists, maps, atom sets). |
| `PaktException` | Parse/validation error with `PaktPosition` and `PaktErrorCode`. |

### Source Generator Attributes

| Attribute | Target | Purpose |
|---|---|---|
| `[PaktSerializable(typeof(T))]` | Class | Register a type for code generation on a `PaktSerializerContext` subclass. |
| `[PaktProperty("name")]` | Property | Override the PAKT field name (default: camelCase of property name). |
| `[PaktPropertyOrder(n)]` | Property | Control serialization field order. |
| `[PaktIgnore]` | Property | Exclude a property from serialization. |
| `[PaktAtom("a", "b", "c")]` | Property | Declare valid atom set members. |
| `[PaktScalar(PaktScalarType.X)]` | Property | Override the inferred scalar type mapping. |
| `[PaktConverter(typeof(C))]` | Property | Use a custom converter (planned). |

### Supported Type Mappings

| PAKT Type | C# Type |
|---|---|
| `str` | `string` |
| `int` | `int`, `long` |
| `dec` | `decimal` |
| `float` | `double` |
| `bool` | `bool` |
| `uuid` | `Guid` |
| `date` | `DateOnly` |
| `time` | `TimeOnly`, `DateTimeOffset` |
| `datetime` | `DateTimeOffset` |
| `bin` | `byte[]` |
| `@(a\|b)` | `string` (with `[PaktAtom]`) |
| `T?` | Nullable reference/value types |
| `{...}` | Class with properties |
| `[T]` | `List<T>` |
| `<K;V>` | `Dictionary<K,V>` |

## Building

```sh
# Build all projects
dotnet build

# Run tests (218 tests)
dotnet test

# Build benchmarks (does not run them)
dotnet build benchmarks/Pakt.Benchmarks

# Run benchmarks
dotnet run --project benchmarks/Pakt.Benchmarks -- --filter '*'
```

### Project Structure

```
dotnet/
├── src/
│   ├── Pakt/                  # Core library (net8.0 + net10.0)
│   └── Pakt.Generators/       # Source generator (netstandard2.0)
├── tests/
│   ├── Pakt.Tests/             # Reader, writer, stream reader, serializer tests
│   └── Pakt.Generators.Tests/  # Generator snapshot tests
├── benchmarks/
│   └── Pakt.Benchmarks/       # BenchmarkDotNet throughput & allocation benchmarks
├── Directory.Build.props       # Shared build settings
├── Directory.Packages.props    # Central package version management
└── Pakt.slnx                  # Solution file
```

### Referencing the Source Generator

Consumer projects must reference the generator as an analyzer — not a regular library:

```xml
<ProjectReference Include="path/to/Pakt.Generators.csproj"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
```

## Status

**MVP complete.** Core features implemented:

- ✅ Streaming tokenizer (`PaktReader`) — all PAKT scalar and composite types
- ✅ Forward-only writer (`PaktWriter`)
- ✅ Statement-level reader (`PaktStreamReader`) — assignments and streams
- ✅ Source-generated (de)serialization — structs, lists, maps, nullable types, nested types
- ✅ Convenience API (`PaktSerializer`) — single-statement round-trip
- ✅ 218 tests passing

**Deferred:**

- Tuple deserialization (tuples are tokenized but not mapped to C# types)
- Custom converters (`PaktConverter<T>` — attribute wired, base class stubbed)
- Spec projection (`.spec.pakt` filtering)
- NuGet packaging
- True chunked streaming (currently buffers full document for `PaktStreamReader`)
