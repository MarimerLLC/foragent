# Framework feedback

Observations collected while building Foragent on the RockBot framework. The Foragent project
specification (§9.1) calls this out as a deliverable: "Each milestone produces framework
feedback. Capture it."

## Step 1 — Empty agent on RockBot framework

### Resolved

- **Gateway was not on NuGet.** `RockBot.A2A.Gateway` was marked `IsPackable=false` and its
  load-bearing types were `internal`, which meant a new agent had no supported path to an
  in-process HTTP A2A surface without running the Gateway as a separate container. Filed as
  [rockbot#279](https://github.com/MarimerLLC/rockbot/issues/279). **Resolved** in RockBot 0.8.4
  via commit `476f0bb` — Gateway is now a packable library exposing
  `AddA2AHttpGateway`, `AddA2AApiKeyAuthentication`, and `MapA2AHttpGateway` extension methods,
  with a thin `RockBot.A2A.Gateway.Host` executable for container-only deployments. Foragent
  consumes the NuGet directly.

### Open

- **`IChatClient` is mandatory even for non-LLM agents.** `AddRockBotChatClient` must be called
  or the host fails to resolve services. Foragent v1 does zero LLM work; it ships an
  `EchoChatClient` stub solely to satisfy the registration contract. The framework should let an
  agent opt out of the chat-client dependency (e.g. an `AddRockBotHost(..., skipChat: true)` or
  a null-object registration the framework installs when no real client is provided).
- **One `IAgentTaskHandler` per agent forces skill dispatch into user code.** Foragent has one
  capability today (`fetch-page-title`), but the design calls for five in v1. The handler now
  does `switch (request.Skill)` to route, which duplicates what `AgentSkill` registration
  already expresses. A framework-level "register a handler per skill" API would remove that
  duplication (and make per-skill DI scoping / cancellation / metrics cleaner).
- **`AgentCard` and `AgentSkill` are declared twice.** Once in `AddA2A(opts => opts.Card = …)`
  for bus-side discovery, and again in `Gateway:Skills` in `appsettings.json` for the HTTP
  agent-card endpoint. They must stay in sync by hand. The two surfaces should share one
  source of truth — either the gateway reads the `A2AOptions.Card` out of DI, or both paths
  bind from the same config section.
- **`agentcard.json` discovery wiring lives in Gateway, not `AddA2A`.** A pure bus-worker agent
  (no HTTP surface) can't advertise itself to an HTTP-native A2A client. Today the only way
  to get the `/.well-known/agent-card.json` endpoint is to also wire the gateway. This is fine
  for Foragent (which wants HTTP anyway), but worth flagging — separating "advertise a card"
  from "host a JSON-RPC bridge" would help bus-only deployments.

## Step 2 — Playwright integration

- **`PlaywrightBrowserHost` as `IHostedService` composes cleanly with `AddRockBotHost`.** No
  lifecycle conflict; `StartAsync` runs alongside RockBot's hosted services and `StopAsync`
  disposes Chromium before the message bus is closed. Nothing to change.
- **Playwright's runtime base image choice is worth calling out.** The spec (§3.4) directs
  agents to use `mcr.microsoft.com/playwright/dotnet`. We pinned `v1.50.0-noble` to match the
  `Microsoft.Playwright` NuGet version. Keeping those two version numbers in sync is manual
  today — would be a nice framework-level helper (e.g. a `RockBot.Browser` package that brings
  both the NuGet and a dockerfile snippet / base-image recommendation). Not blocking for v1.
- **Still no resolution on the single-handler-per-agent shape** (flagged in step 1). Growing
  beyond step 2 will make this more painful; the `switch (request.Skill)` in
  `ForagentTaskHandler` is already starting to accumulate per-skill setup.

## Step 4 — Credentials + first credentialed capability (post-to-site / Bluesky)

### Framework observations

- **`ICredentialBroker` is Foragent-local, not framework.** We deliberately did
  not propose this for RockBot yet — spec §6.2 treats the broker as a Foragent
  concept, and no second consumer exists. If future agents (RockBot or
  third-party) grow similar needs, consider lifting a broker abstraction
  upstream with the same value-dictionary shape (see below).
- **`ISitePoster` dispatch is a repeat of the step-3 pattern.** We added a
  small in-capability dispatcher (`PostToSiteCapability` → keyed
  `IReadOnlyDictionary<string, ISitePoster>`) to route a single A2A skill to
  a family of site-specific implementations. Together with the step-3
  `ICapability` dispatcher, this is now the second hand-rolled skill-to-impl
  dispatch inside Foragent. A framework helper (e.g. `AddRockBotCapability<T>`,
  `AddRockBotCapabilityVariant<T>`) would fold both patterns down.
- **No framework hook for per-tenant broker scoping.** Spec §7.5 calls for
  tenant identity from A2A caller, not request payload. The RockBot framework
  exposes the caller identity on `AgentTaskContext.MessageContext.Agent`, but
  there's no established pattern for a broker to receive it. Today Foragent's
  broker ignores tenancy; see "Deferred" below.
- **Playwright string-selector dialect is regex-free.** The first cut of
  `BlueskySitePoster` used `role=button[name=/sign in/i]`-style selectors;
  Playwright's string parser does not accept regex. `getByRole(…, new() { NameRegex = … })`
  works on `IPage` but not in `WaitForSelectorAsync` string form. Switched to
  exact attribute matches. Worth a note in a future `RockBot.Browser` helper if
  one materialises, so consumers don't repeat the mistake.
- **`contenteditable` + Playwright `FillAsync` works for text but not rich
  content.** Bluesky's real composer uses a ProseMirror editor that rejects
  naive `FillAsync`. Our selector targets the contenteditable host, which the
  test fake also uses. Real-world posting may require typing or scripting the
  editor — when we exercise against real bsky.app we'll learn whether this
  path holds. Flagged here so the next session doesn't chase it as a new bug.

### Deferred (tracked so we don't lose them)

All of these are on the step-4 line in spec §9.1 but intentionally punted to
later iterations to keep the PR reviewable. Each is wired into the current
design in a way that allows adding it without breaking changes:

- **Storage state as a credential (spec §6.5).** `BlueskySitePoster` re-auths
  every post. The fix is to call `IBrowserContext.StorageStateAsync()` after
  successful login, persist it back through the broker under a new `Kind`
  (`storage-state`), and re-apply via `Browser.NewContextAsync(new { StorageState = … })`
  on subsequent runs. Requires either an `IBrowserSessionFactory.CreateSessionAsync(storageState)`
  overload or a session-level "import" method. Keeping the broker
  value-shape as `IReadOnlyDictionary<string,string>` means storage state
  (a JSON blob) just becomes `Values["json"]`.
- **2FA via A2A `input-required` (spec §6.6).** RockBot's framework exposes
  the `input-required` state on `AgentTaskContext`, but we haven't wired
  BlueskySitePoster to detect a 2FA prompt and suspend. App passwords bypass
  2FA for now, which is why spec §6.6 recommends them — but the input-required
  path is what unlocks non-app-password sites.
- **Kubernetes secrets broker (spec §6.3).** Only `InMemoryCredentialBroker`
  is implemented; prod deploy will need a `KubernetesCredentialBroker` reading
  from a scoped service account. No deployment target exists yet (spec §9.2).
- **Per-tenant credential namespaces (spec §7.5).** `ICredentialBroker.ResolveAsync`
  takes only the credential id. A production broker should also take a tenant
  id derived from `AgentTaskContext.MessageContext.Agent`, and scope its
  lookup. Foragent is currently single-tenant by omission.
- **Audit logging (spec §7.4).** We log capability invocation + credential id
  via `ILogger`, but there's no dedicated audit sink separate from diagnostic
  logging. Spec §7.4 calls for a per-tenant audit log with structured fields;
  current logs are prose.
- **Domain allowlists (spec §7.1).** `post-to-site` hard-codes the Bluesky
  login URL; no request-level or tenant-level allowlist. When we add a second
  poster, promote the URL to config and add an allowlist check around
  `IBrowserSession.OpenPageAsync`.

## Step 3 — Second capability (extract-structured-data)

- **A2A metadata pass-through.** Filed as [rockbot#281](https://github.com/MarimerLLC/rockbot/issues/281),
  **resolved** in RockBot 0.8.5 (commit `08e86b9`). `AgentMessage.Metadata` and
  `AgentTaskRequest.Metadata` are now `IReadOnlyDictionary<string, string>?`, and the bridge
  maps request- and message-level metadata in both directions (plus non-text parts).
  Foragent's `CapabilityInput.Parse` now reads metadata first and falls back to the JSON /
  bare-URL / embedded-URL paths for back-compat with older callers.
- **Single-handler-per-agent resolved in-repo, not upstream.** We introduced `ICapability`
  inside Foragent and rewrote `ForagentTaskHandler` as a pure dispatcher that resolves
  `IEnumerable<ICapability>` from DI and routes on `SkillId`. It works but it's *Foragent's*
  dispatcher — every RockBot-based agent will reinvent this. Candidate for a
  `RockBot.A2A.Capability` (or similar) helper in the framework: a base class / extension
  method that registers per-skill handlers and auto-wires dispatch. Would also kill the
  duplicate agent-card bookkeeping (we now build `opts.Card.Skills` and `opts.Skills`
  from one static list).
- **`AgentCard` lives in two places on the wire.** `A2AOptions.Card.Skills` (bus-side
  discovery) and `GatewayOptions.Skills` (HTTP agent-card endpoint) are independent. Our
  Program.cs populates both from a single `ForagentCapabilities.Skills` array — a workaround,
  not a fix. The framework should treat one as authoritative and derive the other.
