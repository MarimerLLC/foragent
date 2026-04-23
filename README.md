# Foragent

> ⚠️ **Alpha quality — APIs will change without notice.** v0.2 has shipped
> end-to-end but the project is still pre-1.0 and is not yet publishing
> NuGet or Docker artifacts.

**Foragent** is an A2A-native browser agent for .NET. It exposes browser
automation capabilities over the
[Agent2Agent (A2A) protocol](https://google.github.io/A2A/) so other
agents can delegate browser work rather than reasoning about DOM
selectors, login flows, or session management themselves.

Foragent is built on the [RockBot framework](https://github.com/MarimerLLC/rockbot)
and uses [Microsoft.Playwright](https://playwright.dev/dotnet/) for browser
automation.

Project site: [foragent.dev](https://foragent.dev)

## Build status

![CI](https://github.com/MarimerLLC/foragent/actions/workflows/ci.yml/badge.svg)

## What Foragent does

- Accepts browser tasks from other agents via A2A (RabbitMQ or HTTP)
- Executes tasks in a real browser (Chromium via Playwright) with
  LLM-in-the-loop planning over ref-annotated aria snapshots
- Manages one shared browser per process with a fresh `BrowserContext`
  per task for isolation
- Keeps credentials inside the Foragent process — callers reference
  them by id, never by value
- Learns from successful tasks: persists reusable site skills and
  form schemas, consolidated by a daily dream pass

Advertised capabilities today:

- `browser-task` — LLM-planned multi-step browser work under a host
  allowlist
- `learn-form-schema` — scan a form and persist a typed schema
- `execute-form-batch` — submit a batch of rows against a learned
  schema

See [docs/capabilities.md](docs/capabilities.md) for the full contract.

## What Foragent is not

- Not a Playwright MCP server (Microsoft already ships one)
- Not a test automation framework
- Not a general browser orchestration platform

## Getting started

The fastest path is the end-to-end dev harness at the repo root:

```bash
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
[docs/architecture.md](docs/architecture.md) for the full configuration
surface).

## Documentation

- [Specification](docs/foragent-specification.md) — the governing
  design document (v0.2)
- [Architecture](docs/architecture.md) — how the pieces fit together
- [Capabilities](docs/capabilities.md) — what Foragent exposes over A2A
- [Credentials](docs/credentials.md) — how credentials are modeled and
  resolved

## Contributing

External code contributions are not yet open — see
[CONTRIBUTING.md](CONTRIBUTING.md). Bug reports and capability
suggestions via [issues](https://github.com/MarimerLLC/foragent/issues)
are welcome.

## License

MIT — see [LICENSE](LICENSE).
