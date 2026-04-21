# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Status

Foragent is at **milestone 3** (spec §9.1): two capabilities now — `fetch-page-title` (step 2, Playwright) and `extract-structured-data` (step 3, Playwright + LLM). Credentials are milestone 4. The authoritative design document is `docs/foragent-specification.md` — read it before making non-trivial changes. Framework-level observations from each milestone are captured in `docs/framework-feedback.md`.

## Build / test

Solution file is `Foragent.slnx` (the newer XML format — `dotnet` CLI handles it, but older tooling may not).

```
dotnet restore
dotnet build --configuration Release
dotnet test  --configuration Release
```

Single test project / single test:

```
dotnet test tests/Foragent.Agent.Tests/Foragent.Agent.Tests.csproj
dotnet test --filter "FullyQualifiedName~FetchPageTitleHandlerTests"
```

`Foragent.Browser.Tests` spins up real Chromium against an in-process Kestrel host — **unit tests mock `IBrowserSessionFactory`, integration tests use it for real.** First run downloads Chromium (~130 MB) into `~/.cache/ms-playwright`; subsequent runs reuse the cache and take ~1 s.

`Directory.Build.props` sets `TreatWarningsAsErrors=true` and `Nullable=enable` for every project — a warning will break the build. `Directory.Packages.props` enables central package management with **floating versions** (`CentralPackageFloatingVersionsEnabled=true`); RockBot packages float on `0.8.*`. Add new deps as `<PackageVersion>` there, then reference them from the csproj without a version.

Target framework is **.NET 10** across the whole solution.

## Running locally

End-to-end dev harness is `docker-compose.yml` at the repo root:

```
docker compose up --build
```

That brings up RabbitMQ, Foragent (HTTP A2A on `http://localhost:5210`), and a RockBot agent from `rockylhotka/rockbot-agent:latest` seeded to know about Foragent as an A2A peer. The agent-card is published at `http://localhost:5210/.well-known/agent-card.json`; the authenticated JSON-RPC endpoint is `POST http://localhost:5210/` with header `X-Api-Key: rockbot-calls-foragent`. See the comment block at the top of `docker-compose.yml` for a curl-based smoke test.

The RockBot seed files live in `deploy/rockbot-seed/` — edit `well-known-agents.json` and `agent-trust.json` there, not in the running container.

## Architecture

Four library/host projects with a strict layering:

```
Foragent.Agent          (executable, A2A server host, DI composition root)
  ├─ Foragent.Capabilities   (task-level verbs: fetch-page-content, post-to-site, …)
  │    └─ Foragent.Browser   (Playwright wrapper; owns browser + per-task BrowserContext)
  └─ Foragent.Credentials    (ICredentialBroker abstraction + built-in brokers)
```

Key invariants that should not be violated when adding code:

- **One shared browser instance per process; a fresh `BrowserContext` per A2A task.** Isolation guarantees come from the context, not from spawning browsers. See spec §3.5.
- **Capabilities are task-level, not action-level.** "Post to site" is a capability; "click button" is not. Action-level primitives belong inside `Foragent.Browser`, never on the A2A surface.
- **Credentials are referenced by ID, never passed by value across the A2A boundary.** `ICredentialBroker.ResolveAsync` runs inside the Foragent process. Do not add APIs that accept raw credential values from callers, and do not log credential contents or password-field form values.
- **Multi-tenant from day one.** Tenant identity comes from the A2A caller identity, not from request payloads. Per-tenant credential namespaces and audit logs.
- **Prohibited capabilities (project-level, regardless of how easy they'd be):** account creation, financial transactions, modifying security permissions. See spec §7.3.

## Relationship to RockBot

Foragent depends on the **RockBot framework** (NuGet packages from `MarimerLLC/rockbot`) — not on RockBot the agent. When Foragent needs a framework change, that change ships as a new RockBot NuGet version and Foragent bumps its reference. **Do not monkey-patch framework types inside this repo** — it defeats the point of validating the framework boundary (spec §8.4).

Key framework pieces Foragent uses today:
- `RockBot.Host.AddRockBotHost` + `AgentHostBuilder.AddA2A` — bus-side agent registration. Subscribes to `agent.task.{agentName}` on RabbitMQ.
- `RockBot.A2A.IAgentTaskHandler` — the single per-agent extension point. `ForagentTaskHandler` (in `Foragent.Capabilities`) implements this and dispatches on `request.Skill`.
- `RockBot.A2A.Gateway.AddA2AHttpGateway` + `MapA2AHttpGateway` — the in-process HTTP surface. Published as NuGet in RockBot 0.8.4 (see `docs/framework-feedback.md`).

Foragent requires an LLM (for `extract-structured-data` and future capabilities). The same `IChatClient` is registered both as a singleton (capabilities inject it directly) and via `AddRockBotChatClient` (satisfies the framework's mandatory registration). Config lives under `ForagentLlm` — separate from any rockbot-side `LLM` config so the two agents can point at different models. Program.cs fails fast at startup if `ForagentLlm:Endpoint`/`ModelId`/`ApiKey` are missing.

## Browser

`Foragent.Browser` wraps Playwright. `AddForagentBrowser()` in `Foragent.Agent/Program.cs` registers `PlaywrightBrowserHost` (`IHostedService` owning one shared Chromium per process) and `IBrowserSessionFactory` (hands out a fresh `IBrowserContext` per A2A task — isolation guarantee from spec §3.5). `IBrowserSession` exposes `FetchPageTitleAsync` and `CapturePageSnapshotAsync`; the snapshot uses Chromium's aria-snapshot (via `Locator.AriaSnapshotAsync`) and falls back to `<body>` inner text when the tree is empty. `Foragent.Browser` has `InternalsVisibleTo("Foragent.Browser.Tests")` so tests drive the real `PlaywrightBrowserSessionFactory` without promoting its implementation types to public.

## Capabilities

`Foragent.Capabilities` is the product surface. The pattern:

- Each capability implements `ICapability` — owns its own `AgentSkill` metadata (exposed as a static `SkillDefinition`) and its own `ExecuteAsync` logic.
- `ForagentTaskHandler` is a pure dispatcher that resolves `IEnumerable<ICapability>` from DI and routes on `SkillId`. **Do not add skill-specific logic to the handler.** New capabilities go in new `ICapability` classes.
- `ForagentCapabilities.Skills` (static array) is the single source of truth for advertised skills — both the bus-side `AgentCard.Skills` and the HTTP gateway's `opts.Skills` read from it.
- `CapabilityInput.Parse` is the input-parsing shim until rockbot#281 ships real metadata pass-through. Capabilities that need a URL + description accept a `{"url":"...","description":"..."}` JSON blob in the single text part today; capabilities that only need a URL also accept a bare URL string. When the framework change lands, swap this helper — capability contracts don't need to change.

## Conventions

- Contributions are not yet open externally (`CONTRIBUTING.md`). Internal changes are fine.
- APIs are explicitly unstable — the README warns "APIs will change without notice." Backwards-compat shims and deprecation paths are not expected at this stage; prefer clean replacements.
