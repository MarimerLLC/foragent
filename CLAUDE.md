# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Status

Foragent is at **milestone 5** (spec §9.1): the A2A surface is wired end-to-end against RockBot as the first real user via the `docker-compose.yml` harness, pinned to `rockylhotka/rockbot-agent:0.8.5`. Three capabilities are exercised — `fetch-page-title` (step 2, Playwright), `extract-structured-data` (step 3, Playwright + LLM), and `post-to-site` (step 4, Playwright + credential broker). Validation was scoped to "poster dispatches" — real Bluesky posting requires populating `FORAGENT_BLUESKY_*` in `.env` and is not yet covered by the milestone. Storage-state persistence, 2FA input-required flow, k8s-secrets broker, and per-tenant credential namespaces are still deferred — tracked in `docs/framework-feedback.md` step 4. The authoritative design document is `docs/foragent-specification.md` — read it before making non-trivial changes. Framework-level observations from each milestone are captured in `docs/framework-feedback.md`.

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

`Foragent.Browser` wraps Playwright. `AddForagentBrowser()` in `Foragent.Agent/Program.cs` registers `PlaywrightBrowserHost` (`IHostedService` owning one shared Chromium per process) and `IBrowserSessionFactory` (hands out a fresh `IBrowserContext` per A2A task — isolation guarantee from spec §3.5). `IBrowserSession` exposes `FetchPageTitleAsync` / `CapturePageSnapshotAsync` for one-shot reads, plus `OpenPageAsync` → `IBrowserPage` (navigate / fill / click / wait / read) for multi-step flows like login + post. The snapshot uses Chromium's aria-snapshot (via `Locator.AriaSnapshotAsync`) and falls back to `<body>` inner text when the tree is empty. Selectors passed to `IBrowserPage` use Playwright's string-selector dialect (CSS + `role=role[name="..."]`); **regex is not accepted in string form**, use exact attribute matches. `Foragent.Browser` has `InternalsVisibleTo("Foragent.Browser.Tests")` so tests drive the real `PlaywrightBrowserSessionFactory` without promoting its implementation types to public.

## Capabilities

`Foragent.Capabilities` is the product surface. The pattern:

- Each capability implements `ICapability` — owns its own `AgentSkill` metadata (exposed as a static `SkillDefinition`) and its own `ExecuteAsync` logic.
- `ForagentTaskHandler` is a pure dispatcher that resolves `IEnumerable<ICapability>` from DI and routes on `SkillId`. **Do not add skill-specific logic to the handler.** New capabilities go in new `ICapability` classes.
- `ForagentCapabilities.Skills` (static array) is the single source of truth for advertised skills — both the bus-side `AgentCard.Skills` and the HTTP gateway's `opts.Skills` read from it.
- `CapabilityInput.Parse` is the shared URL + description shim used by `fetch-page-title` and `extract-structured-data`. Capabilities with different input shapes (e.g. `post-to-site` needing `site` / `credentialId` / `content`) parse their own input near the capability — see `PostToSiteInput` in `PostToSiteCapability.cs`. Don't overload `CapabilityInput` for unrelated shapes.
- `post-to-site` dispatches to an `ISitePoster` keyed on `Site` (in `SitePosting/`). `BlueskySitePoster` is the only implementation today; add new sites by registering another `ISitePoster` in `AddForagentCapabilities()`. The capability never echoes exception messages from posters back to callers — they may contain credential material; operators read the full exception in logs.

## Credentials

`Foragent.Credentials` ships `ICredentialBroker` + `CredentialReference(Id, Kind, Values)`. `AddForagentCredentials(configuration, "Credentials")` wires an `InMemoryCredentialBroker` bound to the config section — dev/test only per spec §6.3. Populate via user-secrets (`dotnet user-secrets set "Credentials:bluesky-rocky:Kind" username-password`, etc.), never appsettings.json. **Never log `CredentialReference.Values`**, never include them in A2A responses, never embed them in exception messages. `CredentialReference.ToString()` deliberately does not expose values. Missing credentials throw `CredentialNotFoundException` carrying only the id.

`CredentialReference.Values` is `IReadOnlyDictionary<string, ReadOnlyMemory<byte>>` — byte-shaped so backends like k8s Secrets (byte-native), cert stores, and storage-state blobs pass through without lossy text conversion. Text-origin credentials go in via `CredentialReference.FromText(id, kind, stringDict)` (UTF-8 encodes at the boundary); text-shaped fields come out via `cred.RequireText(key)` (UTF-8 decodes). Use `cred.Require(key)` for raw bytes. `InMemoryCredentialBroker`'s config binding stays text (user-secrets / env vars are string-native); UTF-8 encoding happens at the broker boundary, not at config time.

Credential ids are free-form via user-secrets/appsettings (slashes are fine — `rockbot/social/bluesky-rocky` matches spec §6.2's example). Via env vars / docker-compose, ids must be single-segment: `__` separates config-path segments, so `Credentials__rockbot__social__bluesky-rocky__Kind` becomes the config path `Credentials:rockbot:social:bluesky-rocky:Kind` and fails to bind as an id. Stick with flat ids (`bluesky-rocky`) in the compose harness.

## Conventions

- Contributions are not yet open externally (`CONTRIBUTING.md`). Internal changes are fine.
- APIs are explicitly unstable — the README warns "APIs will change without notice." Backwards-compat shims and deprecation paths are not expected at this stage; prefer clean replacements.
