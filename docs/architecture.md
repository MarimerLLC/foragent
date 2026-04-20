# Architecture

> TODO: This document is a placeholder. The architecture will be documented
> once the initial implementation is complete.

## Overview

Foragent is structured in three layers:

```
A2A Callers
    │
    ▼
┌─────────────────────┐
│   Foragent.Agent    │  A2A server host — receives tasks, returns results
└──────────┬──────────┘
           │
    ┌──────┴───────┐
    ▼              ▼
┌──────────────┐  ┌──────────────────┐
│  Foragent.   │  │  Foragent.       │
│  Capabilities│  │  Credentials     │
└──────┬───────┘  └──────────────────┘
       │
       ▼
┌──────────────┐
│  Foragent.   │  Playwright wrapper — isolated browser context per task
│  Browser     │
└──────────────┘
```

## TODO sections

- [ ] Agent loop design
- [ ] Session lifecycle (browser context per task)
- [ ] Credential flow (ICredentialBroker pattern)
- [ ] A2A capability advertisement
- [ ] LLM integration via Microsoft.Extensions.AI
- [ ] Multi-tenant isolation
