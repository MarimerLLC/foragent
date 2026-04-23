---
layout: default
title: Foragent
description: An A2A-native browser agent for .NET
---

# Foragent

**An A2A-native browser agent for .NET.** Other agents delegate browser
work to Foragent rather than reasoning about DOM selectors, login flows,
or session management themselves.

Foragent is built on the [RockBot framework](https://github.com/MarimerLLC/rockbot)
and uses [Microsoft.Playwright](https://playwright.dev/dotnet/) for browser
automation. It speaks the
[Agent2Agent (A2A) protocol](https://google.github.io/A2A/) over RabbitMQ
and HTTP.

> ⚠️ **Early development.** APIs change without notice. External
> contributions are not yet open — see [Contributing](#contributing) below.

---

## Why Foragent exists

Agents that need to "just use a website" — read a page, fill a form,
post an update — usually end up re-inventing a brittle mix of HTTP
scraping, selector heuristics, and credential handling. Foragent
centralizes that work behind a small A2A surface so the rest of your
agent fleet can stay focused on their own jobs.

The design principles are laid out in the
[specification]({{ '/foragent-specification' | relative_url }}):

- **Task-level capabilities, not action-level.** Callers ask for
  *"post this update to bsky"*, not *"click the button with aria-label X"*.
- **One shared browser, one fresh context per task.** Isolation comes
  from the context boundary, not from spinning up new browsers.
- **Credentials stay inside the process.** Callers reference them by
  id; Foragent resolves them locally and never returns their values.
- **Multi-tenant from day one.** Tenant identity comes from the A2A
  caller, not from request payloads.
- **Learning substrate.** Successful tasks write reusable skills that
  prime future planning; a daily dream loop consolidates them.

---

## Usage scenarios

Foragent is aimed at other agents, not end users. Typical callers:

- **Social posting.** A personal assistant agent asks Foragent to
  publish an update to a site it has credentials for.
- **Site monitoring.** A research or ops agent asks Foragent to check
  a page and report back structured results.
- **Form submission at scale.** A workflow agent hands Foragent a
  learned form schema plus a batch of rows; Foragent submits each one
  and streams per-row progress.
- **One-off browser tasks.** A generalist agent describes an intent
  in natural language; Foragent's planner drives the browser
  step-by-step under a strict host allowlist.

The current advertised capabilities are:

| Capability | What it does |
|---|---|
| `browser-task` | LLM-planned, multi-step browser work against an allowlist of hosts |
| `learn-form-schema` | Scan a form and persist a typed schema for later reuse |
| `execute-form-batch` | Submit a batch of rows against a learned (or inline) form schema |

See [capabilities]({{ '/capabilities' | relative_url }}) for the full
contract, inputs, and examples.

---

## Getting started

The fastest path is the end-to-end dev harness at the repo root:

```bash
git clone https://github.com/MarimerLLC/foragent.git
cd foragent
docker compose up --build
```

That brings up RabbitMQ, Foragent (HTTP A2A on `http://localhost:5210`),
and a RockBot agent seeded to know about Foragent as an A2A peer. The
agent-card is published at
`http://localhost:5210/.well-known/agent-card.json`; the authenticated
JSON-RPC endpoint is `POST http://localhost:5210/` with header
`X-Api-Key: rockbot-calls-foragent`. A curl-based smoke test is at the
top of `docker-compose.yml`.

For local builds without Docker:

```bash
dotnet restore
dotnet build --configuration Release
dotnet test  --configuration Release
```

Target framework is **.NET 10**. Foragent requires an LLM — configure
`ForagentLlm:Endpoint`, `ModelId`, and `ApiKey` (see
[architecture]({{ '/architecture' | relative_url }}) for the full
configuration surface).

---

## Resources

**In this repo:**

- [Specification]({{ '/foragent-specification' | relative_url }}) — the
  governing design document (v0.2)
- [Architecture]({{ '/architecture' | relative_url }}) — how the pieces
  fit together
- [Capabilities]({{ '/capabilities' | relative_url }}) — what Foragent
  exposes over A2A
- [Credentials]({{ '/credentials' | relative_url }}) — how credentials
  are modeled and resolved
- [Framework feedback]({{ '/framework-feedback' | relative_url }}) —
  observations pushed back to the RockBot framework

**On GitHub:**

- [Source on GitHub](https://github.com/MarimerLLC/foragent)
- [Issues](https://github.com/MarimerLLC/foragent/issues) — bug reports
  and feature suggestions are welcome
- [RockBot framework](https://github.com/MarimerLLC/rockbot) — the
  agent framework Foragent is built on

---

## Contributing

Foragent is in early development and is **not yet accepting external
code contributions** — see
[CONTRIBUTING.md](https://github.com/MarimerLLC/foragent/blob/main/CONTRIBUTING.md).

In the meantime, the most useful things outside contributors can do are:

- Open an [issue](https://github.com/MarimerLLC/foragent/issues) to
  report a bug or suggest a capability
- Read the [specification]({{ '/foragent-specification' | relative_url }})
  and push back on anything that looks wrong

---

## License

MIT — see [LICENSE](https://github.com/MarimerLLC/foragent/blob/main/LICENSE).
