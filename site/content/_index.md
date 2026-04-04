---
title: "PAKT"
layout: "index"
description: "A typed data interchange format. Human-authorable. Stream-parseable. Spec-projected."
hero_code: |
  app-name:str = 'midwatch'
  version:(int, int, int) = (2, 14, 0)
  deploy:{level:|dev, staging, prod|, release:int} = { prod, 26 }
  features:[str] = ['dark-mode', 'notifications', 'audit-log']
  db:{host:str, port:int} = { 'db.prod.internal', 5432 }
  active:bool = true
---
