# PAKT

> **PAKT** — a typed data interchange format. Human-authorable. Streaming. Self-describing.

```
greeting:str     = 'hello world'
count:int        = 42
payload:bin      = x'48656C6C6F'
active:bool      = true
server:{host:str, port:int} = { 'localhost', 8080 }
```

## What is PAKT?

PAKT is a typed data interchange format where every value carries its type. No inference, no ambiguity. Units are self-validating — type annotations are producer assertions checked at parse time.

The current Go library and CLI implement the PAKT v0 surface: `;` map syntax, `bin`, raw strings, the first-content-line multi-line string rule, and top-level `<<` pack statements. Duplicate statement names and map keys are preserved in decode order; higher-level consumers decide how to interpret them.

## Repository Structure

```
pakt/
├── encoding/       # Canonical Go library (github.com/trippwill/pakt/encoding)
├── dotnet/         # .NET library, source generator, benchmarks
├── main.go         # CLI entry point (go install github.com/trippwill/pakt@latest)
├── spec/           # Formal specification (PAKT v0 draft)
├── docs/           # User guide and documentation
├── site/           # Hugo website (usepakt.dev)
└── testdata/       # Sample .pakt files
```

## Install

```sh
go install github.com/trippwill/pakt@latest
```

## CLI Usage

```sh
# Parse a file and emit structured events
pakt parse data.pakt

# Validate only (exit 0/1)
pakt validate data.pakt
```

## Library

```go
import "github.com/trippwill/pakt/encoding"
```

### Unmarshal

```go
type Config struct {
    Host string `pakt:"host"`
    Port int    `pakt:"port"`
}

cfg, err := encoding.UnmarshalNew[Config](data)
```

### Streaming (UnitReader)

```go
ur := encoding.NewUnitReader(reader)
defer ur.Close()
for prop := range ur.Properties() {
    switch prop.Name {
    case "config":
        cfg, err := encoding.ReadValue[Config](ur)
    case "events":
        for event := range encoding.PackItems[LogEvent](ur) {
            process(event)
        }
    }
}
if err := ur.Err(); err != nil { ... }
```

### Event-Level Decode

```go
dec := encoding.NewDecoder(reader)
defer dec.Close()
for {
    ev, err := dec.Decode()
    if err == io.EOF { break }
    fmt.Println(ev.Kind, ev.Name, string(ev.Value))
}
```

## .NET Library

The `dotnet/` directory contains a high-performance .NET implementation with source-generated serialization. See [dotnet/README.md](dotnet/README.md) for full details.

```csharp
using Pakt;
using Pakt.Serialization;

[PaktSerializable(typeof(Server))]
public partial class AppPaktContext : PaktSerializerContext { }

// Deserialize a unit with top-level statements matching CLR properties
var server = PaktSerializer.Deserialize<Server>(paktBytes, AppPaktContext.Default);

// Iterate pack statements
using var reader = PaktMemoryReader.Create(paktBytes, AppPaktContext.Default);
while (reader.ReadStatement())
{
    if (reader.IsPack && reader.StatementType.IsList)
    {
        foreach (var item in reader.ReadPack<Server>())
            Process(item);
    }
    else if (reader.IsPack && reader.StatementType.IsMap)
    {
        foreach (var item in reader.ReadMapPack<string, Server>())
            Console.WriteLine($"{item.Key}: {item.Value.Host}:{item.Value.Port}");
    }
    else
    {
        var value = reader.ReadValue<Server>();
        Console.WriteLine($"{value.Host}:{value.Port}");
    }
}
```

## Documentation

- [PAKT Specification](spec/pakt-v0.md) — formal grammar and semantics
- [PAKT Guide](docs/guide.md) — human-friendly introduction
- [usepakt.dev](https://usepakt.dev) — website

## License

[MIT](LICENSE)
