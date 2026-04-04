---
title: "PAKT"
---

**PAKT** is a typed data interchange format where every value carries its type.
No inference, no ambiguity.

```
greeting:str     = 'hello world'
count:int        = 42
active:bool      = true
server:{host:str, port:int} = { 'localhost', 8080 }
```

Documents are self-validating — type annotations are producer assertions checked at parse time.
Consumer-side `.spec.pakt` files enable **projections**: a streaming parser materializes only the fields a consumer needs, skipping everything else without allocation.
