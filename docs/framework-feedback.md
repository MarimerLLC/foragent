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

## Step 7 — skills + memory priming

### Framework observations

- **`Skill` record has no tags, metadata, or importance field.** The
  0.8.5 shape is `(Name, Summary, Content, CreatedAt, UpdatedAt?,
  LastUsedAt?, SeeAlso)`. The "agent-learned vs human-authored"
  distinction Foragent needs (and spec §5.6 calls out) has no first-class
  slot — today it's encoded in the name prefix (`sites/{host}/learned/…`
  vs `sites/{host}/…`). `ILongTermMemory`'s `MemoryEntry` by contrast
  carries `Category`, `Tags`, `Metadata`, and `ImportanceScore`. Skills
  would benefit from at least `Metadata` parity: agent-learned skills
  want a `confidence` score, a `last-verified` timestamp, and a
  `source` tag so the planner can weight them below operator primers.
  Framework candidate: add `IReadOnlyDictionary<string, string>?
  Metadata` on `Skill` without changing the file-backed format's
  tolerance of older shapes.

- **`ISkillStore.SearchAsync` takes an explicit `float[]?
  queryEmbedding`.** Callers compute the embedding (via
  `IEmbeddingGenerator<string, Embedding<float>>` from DI). This is
  the right shape — it lets consumers cache embeddings across stores
  and pick when to spend the embedding call — but it means the store
  can't do any "cheap query → skip embedding" optimisation on its own.
  Fine for Foragent's usage; worth noting for anyone expecting the
  RockBot agent's auto-recall pattern (where the framework does the
  embedding behind the scenes) to carry over.

- **No tests-side mock / in-memory `ISkillStore`.** Foragent's tests
  ship a 12-line `FakeSkillStore` that implements the interface by
  hand. Framework candidate: a `RockBot.Host.Testing` package with
  in-memory implementations of the persistence stores would let
  downstream agents write tests without re-implementing the bag of
  interfaces. Low-priority but trivial to produce.

- **Extension methods hang off `AgentHostBuilder`, not
  `IServiceCollection`.** `WithSkills` / `WithLongTermMemory` must be
  called inside the `AddRockBotHost(agent => …)` callback; calling
  them outside on `builder.Services` isn't possible. This is fine — it
  enforces the "owned by the agent host" model — but the naming
  convention (`With…` on a builder vs `Add…` on services) took a moment
  to discover. No ask; noting for consistency.

- **Embedding generator integration is implicit.** Register an
  `IEmbeddingGenerator<string, Embedding<float>>` in DI; `FileSkillStore`
  and `FileMemoryStore` presumably pick it up for vector persistence on
  `SaveAsync`. There's no explicit handshake in the extension method
  signatures (`WithSkills(opts)` doesn't take an
  `IEmbeddingGenerator` or a `UseEmbeddings(bool)`). Works, but an
  explicit `opts.UseEmbeddings = true/auto/off` would make the behavior
  discoverable from `SkillOptions` alone.

### Config and operator-facing shape

- **Split embedding config from chat config.** Foragent ships two
  separate config sections — `ForagentLlm` and `ForagentEmbeddings` —
  because in practice embedding deployments live on different Azure AI
  Foundry subscriptions than the chat deployment. If RockBot's own
  `EmbeddingOptions` (under `Embedding:*`) later wants to grow this
  flexibility, the Foragent layout is a reasonable reference shape.

### Unaided floor regression check (2026-04-22)

Re-ran the step-6 benchmark with priming wired in but stores empty
(NoopSkillStore / NoopLongTermMemory in the integration harness). All 3
scenarios still pass on first attempt — the priming wiring itself adds
no overhead when the stores return nothing, confirming the fail-soft
contract. A separate benchmark with a populated store is step-8-or-later
work (need a curated skill set worth priming against).

## Step 7.5 — dream loop

### Framework observations

- **Dream directives don't ship with the framework.** `DreamOptions`
  defaults to bare filenames (`dream.md`, `skill-optimize.md`,
  `sequence-skill.md`, etc.) that `DreamService` reads at runtime. The
  `RockBot.Host`/`RockBot.Host.Abstractions` assemblies carry **zero
  embedded resources** — no `.md` defaults, no stub directives. The
  RockBot agent ships its directive set inside its docker image
  (`/app/agent/*.md`), and `docker-compose.yml`'s `rockbot-init` step
  copies them to `/data/agent/`. This is intentional (per operator
  guidance: the framework can't know what any given consumer needs),
  but it means every new framework consumer carries a ~300-line
  directive-authoring cost as a prerequisite to turning on dreams.
  Candidate framework offering (not an ask, since the intentionality
  is real): optional companion packages like
  `RockBot.Host.Directives.Personality` and
  `RockBot.Host.Directives.Task` that ship starter directive sets,
  selectable by `WithDreaming(opts => opts.UsePersonalityDefaults())`
  or similar. Reduces onboarding cost without compromising the
  no-hardcoded-content principle.

- **Directive paths resolve via `AgentProfileOptions.BasePath`.** IL
  inspection of `DreamService`'s `ResolvePath` helper confirms: for
  each directive (e.g. `opts.SkillOptimizeDirectivePath =
  "skill-optimize.md"`), the final path is:
  `Path.Combine(basePath, directive)` where `basePath` comes from
  `IOptions<AgentProfileOptions>.Value.BasePath`. If `basePath` is
  relative, it combines against `AppContext.BaseDirectory` (binary
  output dir). Foragent configures `AgentProfileOptions.BasePath =
  "directives"` and ships markdown files alongside the binary via
  `CopyToOutputDirectory=PreserveNewest` — no `WithProfile()` call
  needed. Worth documenting in RockBot's dream-loop guide: consumers
  that don't load a personality profile still need to Configure the
  options type because that's the single source of truth for directive
  base paths.

- **`DreamService`'s constructor pulls 17 dependencies.** Everything
  the dream subtypes might need (`IConversationLog`, `IDlqSampler`,
  `IWispExecutionLog`, `IKnowledgeGraph`, `TierRoutingLogger`, …) is a
  hard ctor parameter, so the framework registers stub / no-op
  implementations for the ones a given agent doesn't use. Works, but
  consumers who turn off a subtype shouldn't need its stores in DI at
  all. Candidate framework refactor: make the subtype dependencies
  optional (`IEnumerable<IDreamSubtype>` or similar) so
  `DreamService.StartAsync` enumerates whatever's registered and skips
  what isn't. Lower priority than the directives ask.

- **`ProtectedSkillPrefixes` literal-only.** The list is
  `List<string>` and (from the IL) matched via `StartsWith` — no
  wildcard expansion. Foragent ships it empty; operators can add
  specific literals if they need to freeze a skill. Noting because
  wildcard-style patterns (`sites/*/login`) would be a natural
  extension and aren't there today.

### Manual verification plan

Automated tests for the dream loop would require faking the scheduler
and running an end-to-end pass — out of scope. Verified manually via
docker-compose:

- Container starts with `ForagentDreams__Enabled=true` → startup log
  shows `ForagentDreams enabled; daily dream pass on schedule '0 3 * *
  *'`.
- Container starts with dreams disabled → log shows the opposite and
  `DreamService` is not registered.
- Directive files present at `/app/directives/*.md` inside the
  container (verified via `docker compose exec foragent ls
  /app/directives/`).

First live dream pass against a non-empty skill store will be observed
after enough `browser-task` runs accumulate — probably step 8 or when
the operator turns the harness on for a sustained session.

## Step 8 — `learn-form-schema` + `execute-form-batch`

### Framework observations

- **Multi-file skill API (RockBot 0.9) closes spec open-question #6
  cleanly.** Step 8 needed typed JSON schemas alongside the existing
  markdown-shaped skills; we'd sketched three options (fenced JSON in
  the skill body, a parallel Foragent-local typed store, or an upstream
  framework extension). The upstream extension had already landed in
  `rockbot` main — `Skill.Manifest: IReadOnlyList<SkillResource>?` plus
  `ISkillStore.SaveAsync(skill, resources)` and
  `GetResourceAsync(skillName, filename)`. `SkillResourceType.JsonSchema`
  is literally the enum value this use case needed. Foragent consumed it
  directly: `LearnFormSchemaCapability` writes the skill bundle,
  `ExecuteFormBatchCapability` reads `schema.json` back, no parallel
  store. The "framework is the substrate" discipline from spec §8
  actually paid off here — we'd have thrown away a Foragent-local store
  one step later when the framework landed this.

- **`SaveAsync(skill)` preserving the manifest is the important bit.**
  Per commit 2db3775 fix #1, a plain
  `ISkillStore.SaveAsync(skill)` call preserves the existing
  `Manifest` when the incoming skill doesn't carry one. That means the
  daily dream loop's `skill-optimize` subtype (which rewrites
  markdown content) can't accidentally orphan resource files, and
  Foragent's `learn-form-schema` can update a skill's prose primer
  without re-writing the schema resource. Without this property, the
  dream loop would silently delete Foragent's typed schemas over time.
  Worth documenting prominently in RockBot's multi-file-skill guide —
  future framework consumers will trip on "my resources disappeared"
  otherwise.

- **`AgentTaskContext.PublishStatus` works unchanged for per-row
  streaming.** Step 8's `execute-form-batch` publishes
  `AgentTaskStatusUpdate { State = Working, Message = …per-row text… }`
  between row submissions. The surface from 0.8.x is still right for
  this, and matches how `RockBot.ResearchAgent` uses it for its
  iterative research loop. Nothing to change; noting so the next
  step-N capability that wants streaming knows the shape is stable.

- **Credential broker still doesn't know about storage-state or
  per-tenant scoping.** `learn-form-schema` and `execute-form-batch`
  both accept `credentialId` but only resolve-and-discard it for
  audit / fail-fast; the actual authenticated-form flow (storage-state
  reuse from a prior `browser-task` login) is still the step-4 deferred
  item. Not new — noting because step 8's capabilities would naturally
  use this if it existed. When storage-state lands, both form
  capabilities grow `storageStateCredentialId` support in one pass.

- **Foragent-local `FakeSkillStore` still a 40-line hand-rolled
  double.** Step 8 adds more surface area (`SaveAsync(skill, resources)`
  + `GetResourceAsync`) and the fake needs to match `FileSkillStore`'s
  manifest-preservation behavior to be a faithful substitute. Still
  noting from step 7: a `RockBot.Host.Testing` package with in-memory
  implementations would let Foragent delete both `FakeSkillStore` in
  `Foragent.Agent.Tests` and `InMemorySkillStore` in
  `Foragent.Browser.Tests`, and would surface any future-proof gaps
  in the fakes once a framework change lands.

### Spec resolutions

- **Open-question #6 (structured artifacts in `ISkillStore`):
  resolved upstream.** No Foragent-local typed store. `schema.json`
  resources under `SkillResourceType.JsonSchema`, consumed via
  `GetResourceAsync`. Step 8 ships the reference pattern.
- **Open-question #8 (batch retry/failure semantics): resolved as
  abort-on-first default, caller-opt-in continue.** Rationale: forms
  mutate, so a row failure is likely a schema or session issue where
  continuing would generate more bad submissions, not recover. Per-row
  status stays in the final result regardless. Deciding abort-by-default
  aligns with how human operators would handle a failed row during a
  paper-form batch.

### Verification

- Unit tests in `Foragent.Agent.Tests/Forms/` — 14 tests covering input
  validation, schema round-trip through `SkillResource`, abort-on-first
  vs continue semantics, `successIndicator` path, and required-field
  validation. Run time ~3s.
- Integration test `Foragent.Browser.Tests/FormCapabilitiesIntegrationTests`
  — spins up Kestrel with a real HTML form, drives
  `learn-form-schema` + `execute-form-batch` end-to-end against real
  Chromium, verifies two rows actually land in the server's POST
  handler. Not LLM-gated — the enricher short-circuits on forms
  without select/radio, so this runs in CI without
  `FORAGENT_LLM_*`.
- Existing step-6 benchmark still 3/3 — framework bump didn't regress
  anything else.

## Step 9 — Deprecate subsumed specialists

Step 9 is a decision milestone, not a feature ship, so the framework
observations are few. Advertised surface lands at three skills:
`browser-task`, `learn-form-schema`, `execute-form-batch`.

### What was deleted and why

- **`fetch-page-title`** — milestone-1 smoke-test relic. Pure
  specialist wrapping `<title>` reads. `browser-task` with intent
  `"fetch the page title of <url>"` and `done.result` produces the
  same value for ~2× the tokens, which is fine given no deterministic
  high-volume caller actually exists.
- **`extract-structured-data`** — single-turn typed extraction with
  `ResponseFormat = Json`. Two deciding factors on top of the "no
  caller" argument: (1) it was out of spec on §7.1 — it called the
  no-argument `CreateSessionAsync` overload and accepted any host,
  which the generalist refuses by design; (2) bringing it into spec
  would have added mandatory `allowedHosts` to its input shape and
  erased its simplicity advantage.
- **`IBrowserSession.FetchPageTitleAsync` /
  `CapturePageSnapshotAsync` / `PageSnapshot` / `PageSnapshotSource`**
  — orphaned once the two capabilities went. Deleted from the
  `Foragent.Browser` surface rather than left as dead code. The
  `snapshot` tool inside `browser-task` uses `IBrowserAgentPage` and
  never touched this API path.
- **`CapabilityInput.Parse`** — the shared URL+description shim had
  only the deleted specialists as consumers. `BrowserTaskInput`
  handles its own shape; `Forms/*Input.cs` handles theirs. Deleted.
- **Integration test `ExtractStructuredDataIntegrationTests`** — the
  only `[SkippableFact]` in the browser-tests project that needed
  `FORAGENT_LLM_*`. Removed; `BrowserTaskIntegrationTests` remains the
  real-LLM benchmark.

### Framework observations

- **Capability-surface evolution is painless.** Removing
  `ICapability` implementations is a three-line edit to
  `ForagentCapabilitiesServiceCollectionExtensions` + a one-line edit
  to the static `Skills` array. No framework API made the deletion
  harder than it had to be — `IAgentTaskHandler.HandleTaskAsync` +
  DI-resolved capabilities remain the right shape for a fast-moving
  pre-1.0 product surface. Confirms that foragent#5 /
  [rockbot#283](https://github.com/MarimerLLC/rockbot/issues/283) (per-skill
  handler registration) is a quality-of-life improvement, not a
  blocker.
- **`AgentCard` and `Gateway:Skills` share one source of truth now.**
  Post-step-1 refactor landed the `ForagentCapabilities.Skills` static
  that both the A2A card and the HTTP gateway read from — step 9
  touched exactly that one array and both sides updated. That worked
  exactly as intended; the step-1 feedback entry about duplicate card
  declarations is effectively closed on the Foragent side via the
  local static. An upstream generalization — framework reads
  `A2AOptions.Card.Skills` directly in the gateway — would remove the
  small local static but isn't urgent.
- **No spec open-questions closed or opened.** Open items #3, #4, #5,
  #7 remain as written; #6 and #8 closed in step 8; #1 and #2 closed
  in v0.2 spec adoption. v0.2 is the shipped minimum surface.

### Follow-up fixes surfaced while validating step 9

Running RockBot → Foragent end-to-end (MacBook-price search across
apple.com + bestbuy.com) surfaced three pre-existing issues. All fixed
on the step-9 branch since step 9's test plan claims end-to-end
validation that didn't actually work without them.

- **`BrowserTaskPriming` required `IEmbeddingGenerator`** — the
  primary-constructor parameter was already annotated nullable
  (`IEmbeddingGenerator<string, Embedding<float>>?`), but MSDI ignores
  C# nullable annotations; it only honors default parameter values.
  Reordered to put `embeddingGenerator` last with `= null` so MSDI
  treats it as optional. Spec §5.6 says missing embeddings should
  downgrade to BM25-only — this made that claim actually true.
  Framework observation: MSDI's "nullable means optional" footgun is
  well-documented but still catches people; worth a sentence in the
  RockBot host-wiring docs if they exist.
- **Skill names with dotted hosts fail silently** — RockBot 0.9's
  `FileSkillStore.ValidateName` rejects `.` (only alphanumeric,
  hyphens, underscores, `/`). `sites/bsky.app/login`,
  `sites/apple.com/learned/…`, `sites/example.com/forms/…` — all
  common real hosts — threw `ArgumentException` on save, which
  `BskySeedSkillService` swallowed as a warning and
  `TryWriteLearnedSkillAsync` swallowed on the error path. Added
  `SkillNaming.SanitizeHost(host)` that replaces `.` → `-`
  (`bsky.app` → `bsky-app`). Three call sites updated:
  `BskySeedSkillService`, `BrowserTaskCapability.TryWriteLearnedSkillAsync`,
  `LearnFormSchemaCapability.DeriveSkillName`. Framework observation:
  the validator's error is informative but the fact that *every real
  host fails validation* suggests either `.` should be allowed in
  skill names (it's fine on filesystems) or the framework should
  offer a canonical sanitizer so every consumer doesn't reinvent one.
  Allowlist matching and memory-search categories keep the original
  dotted host.
- **Named-volume permissions on fresh compose boot** — the Foragent
  Dockerfile chowns `/data` to the non-root `foragent` user (uid 1655)
  at image-build time, but Docker mounts a fresh named volume
  root-owned and masks the build-time chown. Added a `foragent-init`
  busybox one-shot (mirroring the `rockbot-init` pattern) that
  `chmod -R 777 /data/foragent` on volume creation. Harness issue,
  not framework — noting here because it's the kind of thing that
  could bite any RockBot consumer that mounts persistent state as a
  named volume.
