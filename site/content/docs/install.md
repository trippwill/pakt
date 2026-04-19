---
title: "Installation"
description: "How to install the PAKT CLI, Go library, and .NET library."
weight: 3
---

## CLI Tool

Install the PAKT command-line tool:

```sh
go install github.com/trippwill/pakt@latest
```

## Go Library

Add the encoding package to your Go project:

```sh
go get github.com/trippwill/pakt/encoding
```

### Streaming (recommended)

Process PAKT data one property at a time with constant memory:

```go
package main

import (
    "fmt"
    "os"

    "github.com/trippwill/pakt/encoding"
)

type Config struct {
    Host string `pakt:"host"`
    Port int64  `pakt:"port"`
}

type LogEvent struct {
    Timestamp string `pakt:"ts"`
    Level     string `pakt:"level"`
    Message   string `pakt:"msg"`
}

func main() {
    f, err := os.Open("data.pakt")
    if err != nil {
        fmt.Fprintln(os.Stderr, err)
        os.Exit(1)
    }
    defer f.Close()

    ur := encoding.NewUnitReader(f)
    defer ur.Close()

    for prop := range ur.Properties() {
        switch prop.Name {
        case "config":
            cfg, err := encoding.ReadValue[Config](ur)
            if err != nil {
                fmt.Fprintln(os.Stderr, err)
                return
            }
            fmt.Printf("Server: %s:%d\n", cfg.Host, cfg.Port)

        case "events":
            // Stream pack elements one at a time
            for event := range encoding.PackItems[LogEvent](ur) {
                fmt.Printf("[%s] %s: %s\n", event.Timestamp, event.Level, event.Message)
            }
        }
    }
    if err := ur.Err(); err != nil {
        fmt.Fprintln(os.Stderr, err)
    }
}
```

### Quick unmarshal

Deserialize an entire PAKT unit into a struct:

```go
type AppConfig struct {
    Name   string   `pakt:"name"`
    Port   int64    `pakt:"port"`
    Debug  bool     `pakt:"debug"`
    Tags   []string `pakt:"tags"`
}

cfg, err := encoding.UnmarshalNew[AppConfig](data)
```

### Event-level decode

For custom processing, use the low-level event decoder:

```go
import (
    "fmt"
    "io"
    "os"

    "github.com/trippwill/pakt/encoding"
)

// ...

dec := encoding.NewDecoder(f)
defer dec.Close()

for {
    evt, err := dec.Decode()
    if err == io.EOF {
        break
    }
    if err != nil {
        fmt.Fprintln(os.Stderr, err)
        return
    }
    fmt.Println(evt)
}
```

### Requirements

- Go 1.25 or later

## .NET Library

The .NET library uses source-generated serialization modeled after `System.Text.Json`.

```sh
dotnet add package Pakt
```

### Define types and create a context

```csharp
using Pakt.Serialization;

public class Server
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
}

[PaktSerializable(typeof(Server))]
public partial class AppPaktContext : PaktSerializerContext { }
```

### Read a unit

```csharp
using Pakt;

using var reader = PaktMemoryReader.Create(paktBytes, AppPaktContext.Default);

while (reader.ReadStatement())
{
    if (reader.IsPack && reader.StatementType.IsList)
    {
        foreach (var s in reader.ReadPack<Server>())
            Console.WriteLine($"{s.Host}:{s.Port}");
    }
    else if (reader.IsPack && reader.StatementType.IsMap)
    {
        foreach (var entry in reader.ReadMapPack<string, Server>())
            Console.WriteLine($"{entry.Key}: {entry.Value.Host}:{entry.Value.Port}");
    }
    else
    {
        var s = reader.ReadValue<Server>();
        Console.WriteLine($"{s.Host}:{s.Port}");
    }
}
```

### Whole-unit convenience

```csharp
var server = PaktSerializer.Deserialize<Server>(paktBytes, AppPaktContext.Default);
byte[] bytes = PaktSerializer.Serialize(server, AppPaktContext.Default);
```

### Read from a stream (async)

```csharp
await using var reader = PaktStreamReader.Create(networkStream, AppPaktContext.Default);

while (await reader.ReadStatementAsync(ct))
{
    if (reader.IsPack && reader.StatementType.IsList)
    {
        await foreach (var s in reader.ReadPackAsync<Server>(ct))
            Console.WriteLine($"{s.Host}:{s.Port}");
    }
    else
    {
        var s = await reader.ReadValueAsync<Server>(ct);
        Console.WriteLine($"{s.Host}:{s.Port}");
    }
}
```

### Requirements

- .NET 10
