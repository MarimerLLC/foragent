# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Status

Foragent is at **milestone 9 shipped — v0.2 complete**. Three capabilities are advertised (`browser-task`, `learn-form-schema`, `execute-form-batch`); the A2A loop is wired end-to-end against RockBot via the `docker-compose.yml` harness (image pinned to `rockylhotka/rockbot-agent:0.9.11`, which brings the structured-data `invoke_agent` surface from PR #291 so RockBot can consume Foragent's FormSchema JSON natively rather than as prose). Step 9 removed the v0.1 `fetch-page-title` and `extract-structured-data` specialists: both are reachable via `browser-task` (the planner's `done(result=...)` channel carries JSON when the intent asks for it), at roughly 2–3× the tokens per call — acceptable given zero deterministic high-volume callers today. `extract-structured-data` was also out of spec on §7.1 mandatory allowlists, which the generalist enforces by design. Session-level `FetchPageTitleAsync` / `CapturePageSnapshotAsync` were removed from `IBrowserSession` alongside the capabilities that used them. RockBot framework refs are on `0.9.*` (bumped from `0.8.*` for step 8's multi-file skill API). Step 6 shipped the generalist `browser-task` planner (LLM-in-the-loop over ref-annotated aria snapshots + `aria-ref=eN` locator resolution, built on `Microsoft.Playwright` 1.59 — bumped from 1.50 for the Ai aria-snapshot mode; see Appendix A #16). Tiered chat clients are wired via `AddRockBotTieredChatClients` with one model aliased across Low/Balanced/High per spec §3.7. Step 7 wired the learning substrate: `ISkillStore` + `ILongTermMemory` via `WithSkills()` + `WithLongTermMemory()`, `BrowserTaskPriming` injects retrieved skill + memory content into the planner prompt, successful tasks write a learned skill at `sites/{host}/learned/{slug}`, and `BskySeedSkillService` seeds `sites/bsky.app/login` on first start (idempotent — only writes when absent). Step 8 added `learn-form-schema` + `execute-form-batch` (spec §5.5 multi-phase flow): learn returns a typed `FormSchema` and persists it as a `Skill` plus a `SkillResourceType.JsonSchema` resource at `sites/{host}/forms/{slug}`; execute resolves schemas via `ISkillStore.GetResourceAsync("schema.json")` (or accepts them inline), submits each row, and streams per-row progress via `AgentTaskContext.PublishStatus`. Default mode is **abort-on-first** (spec open-question #8 resolution); callers opt into `"continue"` for known-messy batches. Spec open-question #6 (typed JSON alongside markdown skills) is resolved by the RockBot 0.9 multi-file skill API — no parallel Foragent-local store was needed. Embeddings are optional and configured separately under `ForagentEmbeddings` so they can live on a different Azure Foundry subscription than the chat model; missing embeddings downgrade retrieval to BM25-only with a single startup warning. `post-to-site` has been removed from both the advertised skill list and the codebase (greenfield deletion — `browser-task` + the learned bsky skill cover the use case). The governing spec is `docs/foragent-specification.md` **v0.2**. Storage-state persistence, 2FA input-required flow, k8s-secrets broker, and per-tenant credential namespaces remain deferred — tracked in `docs/framework-feedback.md`. Framework-level observations from each milestone are captured in `docs/framework-feedback.md`.

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
  ├─ Foragent.Capabilities   (task-level verbs: browser-task, fetch-page-title, …)
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
- `RockBot.Host.AgentMemoryExtensions.WithSkills` / `WithLongTermMemory` — file-backed `ISkillStore` + `ILongTermMemory` (step 7). `ISkillStore.SearchAsync` takes an explicit `float[]? queryEmbedding`; callers compute the embedding. `Skill` record is lean (`Name, Summary, Content, CreatedAt, UpdatedAt?, LastUsedAt?, SeeAlso, Manifest?`). Multi-file skills (RockBot 0.9.x, step 8): `ISkillStore.SaveAsync(skill, resources)` writes a bundle atomically, `GetResourceAsync(skillName, filename)` reads a resource's text. `Skill.Manifest` is an `IReadOnlyList<SkillResource>?` describing attached files (`SkillResourceType.JsonSchema`, `Python`, `Wisp`, `Markdown`, `Text`, `Other`). **A plain `SaveAsync(skill)` preserves the existing manifest** (framework commit 2db3775 fix #1), so dream-loop skill-optimize rewriting markdown content won't drop Foragent's attached schemas.

Foragent requires an LLM. Config lives under `ForagentLlm` — separate from any rockbot-side `LLM` config so the two agents can point at different models. Program.cs fails fast at startup if `ForagentLlm:Endpoint`/`ModelId`/`ApiKey` are missing. Starting step 6 the single configured model is wired via `AddRockBotTieredChatClients(low, balanced, high)` aliased to the same inner `IChatClient`; that one call registers both `IChatClient` (wrapped with `RockBotFunctionInvokingChatClient` for automatic tool invocation) and `TieredChatClientRegistry` (per spec §3.7). Don't also call `AddRockBotChatClient` — it would swap out the wrapped registration. Capabilities that want to escalate/de-escalate per request can resolve `TieredChatClientRegistry` and call `GetClient(ModelTier.Low|Balanced|High)`; none do today.

## Browser

`Foragent.Browser` wraps Playwright. `AddForagentBrowser()` in `Foragent.Agent/Program.cs` registers `PlaywrightBrowserHost` (`IHostedService` owning one shared Chromium per process) and `IBrowserSessionFactory` (hands out a fresh `IBrowserContext` per A2A task — isolation guarantee from spec §3.5). `IBrowserSession` exposes `OpenPageAsync` → `IBrowserPage` (navigate / fill / click / wait / read / `SelectOptionAsync` / `SetCheckedAsync` / `ScanFormAsync`) for multi-step flows like login + post and form submission, and `OpenAgentPageAsync` → `IBrowserAgentPage` for LLM-in-the-loop planners (ref-annotated aria snapshots + `aria-ref=eN` locator resolution). The one-shot `FetchPageTitleAsync` / `CapturePageSnapshotAsync` + `PageSnapshot` record were removed in step 9 along with the specialists that used them — `browser-task`'s `snapshot` tool covers the same ground. `ScanFormAsync` (added in step 8) runs a single JS pass in the page to collect inputs/selects/textareas with labels, validation attributes, and select/radio options — returns a typed `FormScan` that the form capabilities lift into `FormSchema`. The snapshot uses Chromium's aria-snapshot (via `Locator.AriaSnapshotAsync`; `Mode = AriaSnapshotMode.Ai` gets the ref-annotated form) and falls back to `<body>` inner text when the tree is empty. Selectors passed to `IBrowserPage` use Playwright's string-selector dialect (CSS + `role=role[name="..."]`); **regex is not accepted in string form**, use exact attribute matches. `Foragent.Browser` has `InternalsVisibleTo("Foragent.Browser.Tests")` so tests drive the real `PlaywrightBrowserSessionFactory` without promoting its implementation types to public.

`CreateSessionAsync(Func<Uri,bool> allowedHost, ...)` is the step-6 entry point for allowlist-scoped sessions. The factory installs a context-wide `RouteAsync("**/*", ...)` that aborts off-list document/subframe navigations before Playwright issues the request (spec §7.1). The no-argument overload accepts any host and stays available for specialists that enforce narrower rules elsewhere.

## Capabilities

`Foragent.Capabilities` is the product surface. The pattern:

- Each capability implements `ICapability` — owns its own `AgentSkill` metadata (exposed as a static `SkillDefinition`) and its own `ExecuteAsync` logic.
- `ForagentTaskHandler` is a pure dispatcher that resolves `IEnumerable<ICapability>` from DI and routes on `SkillId`. **Do not add skill-specific logic to the handler.** New capabilities go in new `ICapability` classes.
- `ForagentCapabilities.Skills` (static array) is the single source of truth for advertised skills — both the bus-side `AgentCard.Skills` and the HTTP gateway's `opts.Skills` read from it.
- Capabilities parse their own input near the capability (`BrowserTaskInput` in `BrowserTask/`, the `*Input` classes in `Forms/`). The `CapabilityInput.Parse` shared URL+description shim was removed in step 9 with its only consumers.
- `browser-task` (in `BrowserTask/`) is the generalist planner (spec §5.2). `BrowserTaskInput` parses intent + mandatory `allowedHosts` + optional `url` / `credentialId` / `maxSteps` (default 60, ceiling 150) / `maxSeconds` (default 120, ceiling 600). `BrowserTaskTools` wraps `snapshot` / `navigate` / `click` / `type` / `wait_for` / `done` / `fail` as `AIFunction`s via `AIFunctionFactory.Create` and passes them in `ChatOptions.Tools`; the RockBot-wrapped function-invoking `IChatClient` runs the full model ↔ tool loop inside one `GetResponseAsync` call. Budget is enforced tool-side (each tool checks `BrowserTaskState.BudgetExhausted`) because Microsoft.Extensions.AI does not surface per-request iteration caps through `ChatOptions`; wall-clock is a linked `CancellationTokenSource`. **Never log tool arguments verbatim** — `type` carries user-supplied values that may be sensitive (log length only). Refs from a snapshot are valid only until the next mutating call; the system prompt and tool descriptions both state this, but don't code anything that assumes cross-snapshot ref stability.
- `learn-form-schema` and `execute-form-batch` (both in `Forms/`) are the step-8 phase-1 / phase-3 pair (spec §5.5). `FormSchema` / `FormField` are the wire contract — stable JSON shape, stored via `FormSchema.SerializerOptions`. `LearnFormSchemaCapability` navigates, calls `IBrowserPage.ScanFormAsync`, maps the raw scan to `FormSchema` via the deterministic `FormSchemaMapper` (pure — no LLM), then runs `FormSchemaEnricher` for one LLM turn to infer `dependsOn` and a note (skipped when there are no select/radio fields). The schema is persisted as a `Skill` bundle at `sites/{host}/forms/{slug}` with a `SkillResourceType.JsonSchema` resource named `schema.json`. Only the enricher can add `dependsOn` / `notes` — structural fields (type, selector, required, options) come only from the DOM scan, so the LLM cannot fabricate fields or rewrite selectors. `ExecuteFormBatchCapability` accepts `schemaRef` (resolves via `ISkillStore.GetResourceAsync(name, "schema.json")`) or an inline `schema`, and submits each row with `FillAsync` / `SelectOptionAsync` / `SetCheckedAsync` per field type. Per-row progress is published via `AgentTaskContext.PublishStatus(new AgentTaskStatusUpdate { State = Working, Message = … })`. Default `mode` is `"abort-on-first"` (spec open-question #8 resolution: a failed submit on a mutating form usually indicates a real problem, so continuing would just generate more bad submissions); callers opt into `"continue"` for known-messy batches. Success detection: an optional `successIndicator` CSS selector is the preferred signal; the fallback is URL change after submit, which fails (correctly) for forms that submit in place. File uploads and multi-step wizards are **out of scope** for v0.2 — `ScanFormAsync` explicitly skips `type=file`, and there's no flow control for wizards.

## Learning substrate (step 7)

Two RockBot framework stores are wired into the host via `AgentHostBuilder`:

- `ISkillStore` (`agent.WithSkills(opts => opts.BasePath = …)`) — file-backed skill store for markdown site primers. Content root defaults to `ForagentMemory:SkillsPath` or `data/skills`.
- `ILongTermMemory` (`agent.WithLongTermMemory(opts => opts.BasePath = …)`) — file-backed memory for declarative observations. `ForagentMemory:MemoryPath` or `data/memory`.

`BrowserTaskPriming` (DI-scoped) runs before each `browser-task` planner call: it derives a query from intent + primary allowlist host, optionally computes an embedding via `IEmbeddingGenerator<string, Embedding<float>>`, and calls `ISkillStore.SearchAsync` + `ILongTermMemory.SearchAsync` in parallel. Retrieved content is injected as a "Known site knowledge" section in the user prompt. Fail-soft: either store throwing is logged at debug and skipped, so a broken priming path never fails a task.

Embeddings are optional. `ForagentEmbeddings:Endpoint` / `ModelId` / `ApiKey` are all-or-nothing; missing any one drops back to BM25-only with a startup warning. The embeddings config is a separate section from `ForagentLlm` because the user's subscription for embeddings lives elsewhere from the chat deployment — keep them split.

On successful completion (`state.IsDone`), `BrowserTaskCapability.TryWriteLearnedSkillAsync` runs one extra synthesizer LLM turn (same `IChatClient`, no tools) to author a reusable skill at `sites/{primaryHost}/learned/{intent-slug}`. The synthesizer prompt forbids including credential values or typed field contents. Writes are skipped when the task was trivial (≤1 navigation) or the primary host can't be determined. Errors are logged but never fail the completed task.

`BskySeedSkillService` (IHostedService) seeds `sites/bsky.app/login` on first start by calling `ISkillStore.GetAsync` and only writing if absent — docker volume recreation reseeds cleanly; operator edits to the skill through other channels are preserved.

Skill naming follows spec §5.6: `sites/{host}/{intent}` for human-authored primers, `sites/{host}/learned/{slug}` for agent-generated, `sites/{host}/forms/{slug}` for learned form schemas (step 8). `Skill.SeeAlso` cross-references related skills to surface clusters rather than single entries. **Note:** `Skill` (from `RockBot.Host 0.9.x`) carries `Manifest: IReadOnlyList<SkillResource>?` for multi-file bundles but still doesn't carry tags, metadata, or importance — the `agent-learned` vs `human-authored` vs `form-schema` distinction is encoded in the name prefix only. The dream loop (below) keeps the distinction from mattering at retrieval time: skills get improved, merged, and deduped across origins on a daily cadence; `SaveAsync(skill)` with null resources preserves the existing manifest so dream-loop markdown edits don't orphan attached resource files.

## Dream loop (step 7.5)

Foragent runs a daily RockBot dream pass to consolidate accumulated skills and memory. Wired via `agent.AddScheduling()` + `agent.WithDreaming(opts)` inside `AddRockBotHost`. Five subtypes are enabled, eight are off:

- **Enabled:** main orchestrator (`dream.md`), skill-optimize (merge/dedup), skill-gap (detect missing coverage), sequence-skill (detect repeated tool patterns), memory-mining (promote durable observations to `ILongTermMemory`).
- **Disabled:** preference inference, episode extraction, tier-routing review, entity extraction, graph consolidation, identity reflection, DLQ review, Wisp failure analysis. All personality-agent territory.

`ProtectedSkillPrefixes = []` — empty on purpose. Operator primers like `sites/bsky.app/login` are *improved in place* by the dream, not frozen; the seed service only writes on a cold boot, so later dream-authored improvements survive restarts. Operators who need to reset a primer can delete the stored skill file and bounce the host.

Directive files live at `src/Foragent.Agent/directives/*.md` and ship with the binary via `<Content Include="directives/*.md" CopyToOutputDirectory="PreserveNewest" />`. `DreamService` resolves each `DreamOptions.*DirectivePath` relative to `AgentProfileOptions.BasePath` (confirmed by IL inspection — relative base paths combine against `AppContext.BaseDirectory`, which is the binary output dir). Program.cs configures `AgentProfileOptions.BasePath = "directives"`; no `WithProfile()` call, Foragent doesn't need the personality-profile doc set.

Dreams are **opt-in** via `ForagentDreams:Enabled`. `appsettings.json` defaults false so `dotnet run` smoke tests don't trigger scheduled LLM calls; `docker-compose.yml` sets `ForagentDreams__Enabled=true` because that's the "full operating mode" shape. `CronSchedule` defaults to `0 3 * * *` (03:00 UTC daily) — the framework default of every 12 hours is too frequent for a browser worker. `InitialDelay` is the framework default (5 minutes from start), which is fine in prod but worth noting if someone spins up the compose harness for a 10-minute smoke session.

**Don't add directive content to the RockBot agent's `deploy/rockbot-seed/` set.** Foragent's directives are task-shaped (browser outcomes, site knowledge); RockBot's are personality-shaped (identity, preferences). Mixing them defeats the reason Foragent authored its own.

## Credentials

`Foragent.Credentials` ships `ICredentialBroker` + `CredentialReference(Id, Kind, Values)`. `AddForagentCredentials(configuration, "Credentials")` wires an `InMemoryCredentialBroker` bound to the config section — dev/test only per spec §6.3. Populate via user-secrets (`dotnet user-secrets set "Credentials:bluesky-rocky:Kind" username-password`, etc.), never appsettings.json. **Never log `CredentialReference.Values`**, never include them in A2A responses, never embed them in exception messages. `CredentialReference.ToString()` deliberately does not expose values. Missing credentials throw `CredentialNotFoundException` carrying only the id.

`CredentialReference.Values` is `IReadOnlyDictionary<string, ReadOnlyMemory<byte>>` — byte-shaped so backends like k8s Secrets (byte-native), cert stores, and storage-state blobs pass through without lossy text conversion. Text-origin credentials go in via `CredentialReference.FromText(id, kind, stringDict)` (UTF-8 encodes at the boundary); text-shaped fields come out via `cred.RequireText(key)` (UTF-8 decodes). Use `cred.Require(key)` for raw bytes. `InMemoryCredentialBroker`'s config binding stays text (user-secrets / env vars are string-native); UTF-8 encoding happens at the broker boundary, not at config time.

Credential ids are free-form via user-secrets/appsettings (slashes are fine — `rockbot/social/bluesky-rocky` matches spec §6.2's example). Via env vars / docker-compose, ids must be single-segment: `__` separates config-path segments, so `Credentials__rockbot__social__bluesky-rocky__Kind` becomes the config path `Credentials:rockbot:social:bluesky-rocky:Kind` and fails to bind as an id. Stick with flat ids (`bluesky-rocky`) in the compose harness.

## Conventions

- Contributions are not yet open externally (`CONTRIBUTING.md`). Internal changes are fine.
- APIs are explicitly unstable — the README warns "APIs will change without notice." Backwards-compat shims and deprecation paths are not expected at this stage; prefer clean replacements.
- **Version scheme.** `Directory.Build.props` sets `<Version>` for every assembly/package in the solution. Format: `{spec-major}.{spec-minor}.0-alpha.{step}` while the spec is pre-1.0 — e.g. `0.2.0-alpha.8` = implementing spec v0.2, step 8 has shipped. Bump the `alpha.N` segment when a step's PR merges to `main`; bump the spec major/minor when a new spec revision is adopted. Drop `-alpha.N` only when the spec version is declared stable and Foragent actually ships NuGet/Docker artifacts (spec §10.3 — "published once the project actually works end-to-end. No placeholder packages."). No git tags yet; add them when the first artifact is published.
