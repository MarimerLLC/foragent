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

### Credential abstraction — backend generality check

Before finalizing step 4 we sanity-checked whether
`ICredentialBroker.ResolveAsync(id) → CredentialReference(Id, Kind, Values)`
is general enough to back alternative secret stores beyond in-memory and
k8s. The shape bends:

- **k8s Secrets** — Secret name → `Id`. `data` map (base64-decoded) → `Values`. Clean fit.
- **Azure Key Vault** — One vault secret per credential holding a JSON blob, deserialized into `Values`. Or naming convention (`bluesky-rocky-identifier`, `bluesky-rocky-password`); broker collates. Both work.
- **AWS Secrets Manager** — Native JSON `SecretString` maps directly to `Values`.
- **HashiCorp Vault (KV v2)** — `secret/data/<id>` → string map → `Values`. Direct fit.
- **File-based dev broker** — Gitignored JSON file, one-to-one with `Values`.

`Values` was switched from `IReadOnlyDictionary<string, string>` to
`IReadOnlyDictionary<string, ReadOnlyMemory<byte>>` pre-emptively. Most real
backends (k8s Secrets, cert stores, storage-state blobs) are byte-native;
text is the common case but not the *only* case. `CredentialReference.FromText`
+ `RequireText` cover the UTF-8 path at the edges without forcing every
broker / consumer to care.

### Known gaps in the credential interface (not yet fixed)

These are not blocking step 4 but will force changes as the spec is filled
in. Captured here so they aren't rediscovered:

1. **No catalog / list.** Spec §6.4 calls for advertising which credential
   ids exist (without values) so a caller can say "I'd need a Bluesky
   credential, none is configured." Today's interface is `Resolve` only.
   Every non-toy backend supports listing. Will need
   `IAsyncEnumerable<string> ListAsync(CancellationToken)` or equivalent.
2. **No write path for storage state (§6.5).** Storage-state-as-credential
   requires the broker to *persist* post-login session bytes. Will need a
   `Task WriteAsync(CredentialReference)` — and some backends are read-only
   (Key Vault read role), so the interface should signal write capability
   (either a feature flag or a separate `IWritableCredentialBroker`).
3. **Tenancy isn't on the interface.** `ResolveAsync(string id)` has no
   tenant parameter. Production backends need to scope lookups to the A2A
   caller's tenant id. Either `ResolveAsync(TenantId, string id)` or
   per-tenant broker scoping. Blocked on the spec-level tenant-identity
   decision (spec §12 open question 5).

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

## Step 5 — RockBot as Foragent's first real user

Validation loop per spec §9.1 step 5. The goal is not new capabilities — it's
to watch the end-to-end A2A path (RockBot → Foragent → response) actually
run, with RockBot the agent standing in for a real caller.

### What was exercised

Ran `docker compose up --build` against pinned `rockylhotka/rockbot-agent:0.8.5`
(latest framework release shipping the metadata pass-through and gateway
packaging fixes from steps 1/3). Directly curled Foragent through the HTTP
A2A gateway for all three skills:

- `fetch-page-title` against `https://example.com` → `"Example Domain"`.
- `extract-structured-data` with JSON body → LLM returned `{"heading":"Example Domain"}`.
- `post-to-site` with unknown site → safe dispatcher error listing known sites.
- `post-to-site` with `bluesky-rocky` id not present in the broker → clean
  `"Credential 'bluesky-rocky' is not configured."` No credential id in the
  response (only the requested id, which the caller already knows), no
  exception surface, no leaked site internals. Warning log in Foragent
  carries the id for operator debugging.

Bluesky posting against the real site is intentionally deferred — this
milestone was scoped to "poster dispatches" (capability → broker lookup →
ISitePoster dispatch). The real-post path will be exercised once the user
populates `FORAGENT_BLUESKY_IDENTIFIER` / `FORAGENT_BLUESKY_APP_PASSWORD`
in `.env`.

### Framework observations

- **RockBot's peer registry shadows the peer's agent-card instead of
  discovering from it.** Filed as
  [rockbot#287](https://github.com/MarimerLLC/rockbot/issues/287). The
  A2A v1 spec defines `/.well-known/agent-card.json` as the authoritative
  skill list, but RockBot's `well-known-agents.json` carries a *full*
  duplicate copy of each peer's skills (id, name, description). This is
  a framework-side flaw, not a Foragent one — Foragent already collapses
  its own internal duplication to a single `ForagentCapabilities.Skills`
  source. Consequence: adding a capability to Foragent requires editing
  the peer's seed file, not just Foragent's code. The registry should
  carry only the peer coordinates (`url`, auth config) and let the A2A
  client fetch + cache the card at first contact.
- **`agent-trust.json` reseed is an all-or-nothing hammer.** The init
  script uses `[ ! -s /data/agent/agent-trust.json ]` to guard against
  overwriting user-curated trust. Reasonable default, but it means
  adding a new Foragent skill requires wiping the `rockbot-data` volume
  — which also wipes memory, conversations, and feedback. Distinct from
  #287: `approvedSkills` is legitimately RockBot-owned local policy and
  shouldn't be auto-discovered, but the reconciliation flow (peer adds
  a skill → operator decides whether to approve) should not require a
  destructive volume wipe.
- **Caller identity flows cleanly on the bus side.** The
  `ApiKeys__rockbot-calls-foragent__AgentId: RockBot` env-var maps the
  HTTP API key to a caller id, and `AgentTaskContext.MessageContext.Agent`
  carries `"RockBot"` through to the capability. When per-tenant credential
  scoping lands (spec §7.5), this is where tenant resolution belongs —
  no changes needed to the A2A wire format.
- **RockBot 0.8.5 started clean on first boot of the step-4 harness** —
  previous milestones had a `mcp.json` deserialization warning on cold
  start that self-resolves; still present but not blocking. Worth
  mentioning so the next session doesn't chase it.

### Not yet exercised in this step

- **RockBot LLM-driven invocation of Foragent via the blazor UI.** The
  direct-curl path validates the A2A server surface end-to-end, but the
  "RockBot reasons its way to `invoke_agent` based on user chat" path was
  not driven in this session. The harness is running (blazor on :8080)
  for ad-hoc validation outside the milestone PR.

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

## Step 6 — baseline `browser-task` generalist

### Framework observations

- **`AddRockBotTieredChatClients` obviates `AddRockBotChatClient` but this
  is undocumented.** Calling `AddRockBotTieredChatClients(low, balanced,
  high)` registers an `IChatClient` singleton whose factory already wraps
  the inner client with `RockBotFunctionInvokingChatClient`, plus a
  `TieredChatClientRegistry` singleton. Callers who previously used
  `AddRockBotChatClient(client)` don't need to call both — but that's
  not spelled out anywhere. If both are called, the second registration
  silently wins (standard MEDI behavior), which can swap the wrapped
  client for an unwrapped one depending on order. Docs gap; candidate
  framework fix is either a guard throw or collapsing both methods into
  one overload shape.

- **No per-request iteration cap surface on the function-invoking chat
  client.** `FunctionInvokingChatClient.MaximumIterationsPerRequest` is
  an *instance* property, and the wrapped client is built inside
  `AddRockBotTieredChatClients` — the caller has no hook to set it per
  `GetResponseAsync` invocation. `ChatOptions.AdditionalProperties`
  lookup keys are not honored. `ModelBehavior.MaxToolIterationsOverride`
  exists on the RockBot side but routes through YAML behavior config,
  not per-call. Foragent enforces its step budget tool-side (each tool
  checks `BrowserTaskState.BudgetExhausted`); wall-clock cancellation
  is the real safety net. Framework candidate: either honor a standard
  `ChatOptions.AdditionalProperties["MaximumIterationsPerRequest"]`
  convention or expose the FICC instance via DI so consumers can
  configure it.

- **`Microsoft.Playwright` 1.50 (pinned since step 2) does not expose
  the Ai aria-snapshot mode.** Step 6 requires ref-annotated snapshots
  (`[ref=eN]` + `aria-ref=eN` locator resolution). That gating moved
  from a boolean `Ref` option to `Mode = AriaSnapshotMode.Ai` sometime
  between 1.52 and the current 1.59 C# bindings. Foragent bumped the
  pin to 1.59.0; container base image
  (`mcr.microsoft.com/playwright/dotnet:v1.50.0-noble`) will need the
  matching bump in the first release that ships browser-task. Not a
  framework-issue per se, but relevant to RockBot's "v1 Foragent" story
  and to anyone using the framework + Playwright together.

- **Aria-ref lifetime is a contract the planner must respect.** Refs are
  valid only within the snapshot they came from. The tool surface
  documents this in the `snapshot` description; if the framework ever
  ships a "browser task runner" helper of its own (candidate
  `RockBot.Browser.Planner`?), it should bake the "re-snapshot after
  mutation" rule into a first-class contract rather than leaving it to
  prompt text.

- **`AIFunctionFactory.Create(Delegate, name:, description:, …)`
  descriptions only surface the method-level `[Description]`.** Parameter
  descriptions must be on parameters via `[Description]` — easy to miss
  without the reminder. Worked as expected; noting for anyone building
  similar tool surfaces.

- **RockBot's `RockBotFunctionInvokingChatClient` auto-invokes tools end
  to end in a single `GetResponseAsync` call.** This is exactly what the
  planner wants; no custom loop needed. One quirk: the FICC keeps
  iterating as long as the model emits tool calls, with no public
  step cap (see above). Combined with aria-ref lifetimes, a model that
  thrashes on stale refs can burn budget fast. Step 7's learning
  substrate is the intended mitigation.

### Unaided floor measurement (2026-04-22)

First end-to-end benchmark against the operator's Azure AI Foundry
Balanced model (no learned skills, no priming — the "unaided" floor the
spec §9.1 step 6 calls for):

| Scenario | Result | Wall-clock |
|---|---|---|
| Click-through (home → link → read destination value) | ✅ done | 5 s |
| Form submit (fill name + textarea → submit → read confirmation) | ✅ done | 8 s |
| Multi-page nav (index → intro → chapter-2 → read bolded answer) | ✅ done | 7 s |

3 / 3 passed on first attempt. Establishes the baseline Foragent must
not regress against once step 7 adds priming. Re-run this set whenever
the planner prompt, tool surface, or model pin changes.

### Not yet exercised

- **`TieredChatClientRegistry.GetClient(ModelTier.Low/High)` is wired
  but no capability resolves it yet.** All three tiers currently alias
  to the same model. Tier-aware capability code lands as models diverge.
