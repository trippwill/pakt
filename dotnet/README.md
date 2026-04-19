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

### 3. Read a Unit with PaktMemoryReader

`PaktMemoryReader` is the statement-level API — it iterates top-level statements one at a time:

```csharp
using Pakt;

using var reader = PaktMemoryReader.Create(paktBytes, AppPaktContext.Default);

while (reader.ReadStatement())
{
    Console.WriteLine($"{reader.StatementName}: {reader.StatementType}");

    if (!reader.IsPack && reader.StatementName == "server")
    {
        var server = reader.ReadValue<Server>();
        Console.WriteLine($"  {server.Host}:{server.Port}");
    }
    else if (reader.IsPack && reader.StatementType.IsList)
    {
        foreach (var server in reader.ReadPack<Server>())
            Console.WriteLine($"  {server.Host}:{server.Port}");
    }
    else if (reader.IsPack && reader.StatementType.IsMap)
    {
        foreach (var entry in reader.ReadMapPack<string, Server>())
            Console.WriteLine($"  {entry.Key}: {entry.Value.Host}:{entry.Value.Port}");
    }
    else
    {
        reader.Skip();
    }
}
```

### 4. Convenience: Whole-Unit Serialize/Deserialize

For whole-unit materialization and whole-unit serialization, use the `PaktSerializer` static API:

```csharp
// Deserialize a unit with top-level statements that match CLR properties
var server = PaktSerializer.Deserialize<Server>(paktBytes, AppPaktContext.Default);

// Serialize the CLR object back to a PAKT unit
byte[] bytes = PaktSerializer.Serialize(server, AppPaktContext.Default);
```

## API Overview

### Layered Architecture

```
  PaktSerializer           High-level convenience (Deserialize / DeserializeAsync / Serialize)
        ↓
  PaktMemoryReader         Sync statement-level iteration (memory-backed)
  PaktStreamReader         Async statement-level iteration (stream-backed)
        ↓
  PaktReader / PaktWriter  Low-level token-by-token I/O
        ↓
  Source Generator         Compile-time (de)serialization code via [PaktSerializable]
```

### Core Types

| Type | Description |
|---|---|
| `PaktReader` | Forward-only, zero-copy tokenizer over `ReadOnlySpan<byte>`. Ref struct — stack-only, pooled allocations. |
| `PaktWriter` | Forward-only PAKT output writer to `IBufferWriter<byte>`. |
| `PaktMemoryReader` | Sync statement-level reader for `ReadOnlyMemory<byte>` / `IMemoryOwner<byte>`. `ReadValue<T>()`, `ReadPack<T>()`, `ReadMapPack<TKey, TValue>()`. |
| `PaktStreamReader` | Async statement-level reader for `Stream`. Real `Stream.ReadAsync` at I/O boundaries. `ReadValueAsync<T>()`, `ReadPackAsync<T>()` (`IAsyncEnumerable`). |
| `PaktSerializer` | Static convenience API. `Deserialize<T>` (sync memory), `DeserializeAsync<T>` (async stream), `Serialize<T>`. |
| `PaktSerializerContext` | Base class for source-generated serialization contexts. Provides `GetTypeInfo<T>()` for type resolution. |
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
| `[PaktConverter(typeof(C))]` | Property | Use a custom converter for that property. |

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
| `ts` | `DateTimeOffset` |
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

# Run tests
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
│   ├── Pakt/                  # Core library (net10.0)
│   └── Pakt.Generators/       # Source generator (netstandard2.0)
├── tests/
│   ├── Pakt.Tests/             # Reader, writer, unit reader, serializer tests
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
- ✅ Statement-level reader (`PaktMemoryReader`) — sync, memory-backed
- ✅ Async stream reader (`PaktStreamReader`) — real `Stream.ReadAsync` at I/O boundaries
- ✅ Source-generated (de)serialization — structs, lists, maps, nullable types, nested types
- ✅ Convenience API (`PaktSerializer`) — `Deserialize<T>` (sync) + `DeserializeAsync<T>` (stream)

**Deferred:**

- Tuple deserialization (tuples are tokenized but not mapped to C# types)
- Source-generated tuple serialization/deserialization coverage
- Spec projection (`.spec.pakt` filtering)
- NuGet packaging
