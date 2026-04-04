---
title: "Installation"
description: "How to install the PAKT CLI and Go library."
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

## Requirements

- Go 1.25 or later
