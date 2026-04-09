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

### Usage

```go
package main

import (
    "os"
    "fmt"
    "io"
    "github.com/trippwill/pakt/encoding"
)

func main() {
    f, _ := os.Open("data.pakt")
    defer f.Close()

    dec := encoding.NewDecoder(f)
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

await using var reader = PaktStreamReader.Create(paktBytes, AppPaktContext.Default);

while (await reader.ReadStatementAsync())
{
    if (reader.IsPack)
        await foreach (var s in reader.ReadPackElements<Server>())
            Console.WriteLine($"{s.Host}:{s.Port}");
    else
        var s = reader.Deserialize<Server>();
}
```

### Single-statement convenience

```csharp
var server = PaktSerializer.Deserialize<Server>(paktBytes, AppPaktContext.Default);
byte[] bytes = PaktSerializer.Serialize(server, AppPaktContext.Default, "server");
```

### Requirements

- .NET 8.0 or later (net8.0, net10.0)
