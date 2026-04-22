# Foragent — Project Specification

> **Status:** Governing specification, v0.2.
> **Date:** April 2026
> **Author:** Rocky Lhotka, Marimer LLC
> **Repository:** https://github.com/MarimerLLC/foragent

---

## 1. Summary

**Foragent** is an A2A-native, self-hosted **agentic browser agent** for
.NET. Callers delegate free-form browser intent to Foragent — "submit
these rows to this form," "post this content on this site," "extract
this data from these pages" — and Foragent plans and drives the browser
to fulfill it, using internal LLM reasoning to resolve selectors,
interpret dynamic form structure, and recover from failure. Callers
do not reason about DOM, selectors, session state, or 2FA.

Foragent is built on the **RockBot framework** (the NuGet packages
maintained at https://github.com/MarimerLLC/rockbot) and uses the
official **Microsoft.Playwright** NuGet package for browser automation,
driven directly in-process (no MCP sidecar, no Stagehand port — see
Appendix A decision #16). It is the second consumer of the RockBot
framework, after the RockBot personal agent itself.

Foragent's product is **one generalist capability** (`browser-task`)
that handles the long tail of browser work, complemented by a small set
of narrow fast-path specialists where a structured typed interface pays
for itself. Site-specific code is the exception, not the scaling path
(see §5).

Foragent is a standalone open-source project under Marimer LLC. RockBot
is its first user, but the project is designed to be generally useful
to anyone building agentic systems on .NET that need a self-hosted
browser worker.

---

## 2. Problem statement

Agents that need to interact with the web today have several poor options:

1. **Use a browser MCP server** (e.g. Microsoft's `playwright/mcp`) — works,
   but operates at the level of individual browser primitives (click,
   navigate, extract). The calling agent ends up doing all the
   selector-resolution, retry, and session-management reasoning itself,
   which is expensive in tokens and brittle in practice.

2. **Use a SaaS browser-agent service** (Browserbase, Skyvern, Browser Use)
   — turnkey but cloud-only, opinionated, often Python or Node, and
   requires sending credentials and session data to a third party.

3. **Build browser automation directly into the primary agent** —
   couples DOM-level reasoning to the primary agent's context window,
   forces every retry loop through expensive reasoning tiers, and creates
   a much wider blast radius for prompt-injection attacks.

There is a gap in the ecosystem for a **self-hosted, .NET-native, A2A-native
browser agent** that operates at the right abstraction level (task-level
capabilities, not raw browser primitives) while handling credentials,
sessions, retries, and 2FA properly.

Foragent fills that gap.

---

## 3. Architecture

### 3.1 High-level shape

```
┌─────────────────────┐    A2A     ┌──────────────────────────────────┐
│ Calling agent       │◄─────────►│ Foragent                         │
│ (e.g. RockBot user  │            │  ┌────────────────────────────┐  │
│  agent)             │            │  │ Microsoft.Playwright NuGet │  │
└─────────────────────┘            │  └─────────────┬──────────────┘  │
                                   │                ▼                 │
                                   │  ┌────────────────────────────┐  │
                                   │  │ Chromium (subprocess)      │  │
                                   │  └────────────────────────────┘  │
                                   └──────────────────────────────────┘
                                            │
                                            ▼
                                   ┌──────────────────┐
                                   │ Credential broker│
                                   │ (pluggable: k8s  │
                                   │  secrets, Vault, │
                                   │  Azure KV, etc.) │
                                   └──────────────────┘
```

A calling agent (any A2A client; RockBot is the first one) sends an A2A
task to Foragent. Foragent's agent loop interprets the task, spins up an
isolated browser context, executes the work using Microsoft.Playwright,
and returns results. Credentials are resolved by reference through a
pluggable broker — the calling agent never sees credential values.

### 3.2 Why a dedicated agent rather than direct MCP usage

**Rejected approach:** Have RockBot's primary agent call Microsoft's
`playwright/mcp` container directly.

**Reasons rejected:**

- Operates at the wrong abstraction level. RockBot wants to say "post to
  Bluesky"; MCP exposes "click selector X." The translation work is
  itself agentic and shouldn't burn primary-agent reasoning tokens.
- Couples the primary agent's context to verbose accessibility trees.
- Forces every retry/recovery loop through the primary agent's expensive
  reasoning model.
- No clean home for credential brokering, session persistence, or
  human-in-the-loop 2FA.

**Chosen approach:** A dedicated browser agent that owns the "how" of
browser automation, exposing task-level capabilities ("post-to-social,"
"extract-structured-data") via A2A. The calling agent reasons about
*what* it wants done; Foragent reasons about *how* to do it.

### 3.3 Why A2A rather than additional MCP layers

The A2A protocol gives us several things MCP doesn't:

1. **Task lifecycle** — submitted, working, input-required, completed.
   Browser tasks routinely take 30+ seconds with retries; a transactional
   request/response model is the wrong shape.
2. **Negotiation** — Foragent can push back ("I can't find a Bluesky
   credential for you, want me to try Mastodon instead?") rather than
   simply succeeding or failing.
3. **Asynchronous progress updates** — "logged in, navigating to compose,
   waiting for editor to load."
4. **input-required state** — solves 2FA cleanly. Foragent hits a
   challenge, transitions task to input-required, calling agent (or its
   human) provides the code, Foragent resumes.
5. **Capability advertisement via agent cards** — Foragent publishes
   what it can do at the right level of abstraction.

### 3.4 Why Microsoft.Playwright (NuGet) rather than a separate Playwright MCP container

**Rejected approach:** Have Foragent invoke `mcr.microsoft.com/playwright/mcp`
as a sidecar container per task.

**Reasons rejected:**

- Adds a layer (MCP marshaling, container orchestration) between Foragent
  and the browser for no benefit, since Foragent is the only consumer.
- Higher latency per browser action (JSON over MCP transport vs.
  in-process method calls).
- More moving parts to deploy and debug.
- MCP exposes a curated subset of Playwright's API; direct library access
  gives full surface (network interception, fine-grained sync,
  storage state export).
- No native .NET ergonomics — string-bag JSON instead of typed APIs.

**Chosen approach:** Foragent's container ships with both the agent code
and the Playwright browser binaries, using `mcr.microsoft.com/playwright/dotnet`
as the base image. The agent process calls Microsoft.Playwright directly.

### 3.5 Browser instance model

- **One shared browser instance per Foragent process**, with a fresh
  `BrowserContext` per A2A task.
- Browser contexts are isolated: each gets its own cookies, storage,
  cache, and origin. Cross-task contamination is impossible.
- Contexts are disposed aggressively when tasks complete.
- For session reuse (avoiding repeated logins), `storageState` is
  exported, persisted as a credential-class secret via the broker, and
  re-imported on subsequent tasks.

### 3.6 Concurrency

- Foragent processes one A2A task at a time per pod by default.
- Horizontal scaling via KEDA based on A2A queue depth, not in-pod
  concurrency. Browser contexts are isolated, but Chromium memory grows
  fast under concurrent load, and per-pod simplicity is worth more than
  in-pod parallelism for v1.
- Concurrency-within-pod can be added later if profiling shows it's
  needed.

### 3.7 LLM tier routing

Foragent uses the RockBot framework's `TieredChatClientRegistry`
(`RockBot.Llm`, exposed via `AddRockBotTieredChatClients`). The registry
provides three `IChatClient` instances — `Low`, `Balanced`, `High` —
and registers the `Balanced` client as the default `IChatClient`
singleton for consumers that inject without a tier hint.

- **Capabilities request a tier appropriate to the work.** The generalist
  planner loop (§5.2) targets `Balanced` for planning steps and may
  request `High` for recovery from ambiguous states or complex reasoning.
  Cheap structural operations (aria-snapshot summarization, extraction
  shaping) target `Low` when a Low model is meaningfully cheaper.
- **For v0.2 Foragent ships with one configured model.** Operators wire
  the same `IChatClient` into all three tiers; future cost-optimization
  upgrades swap models per-tier without touching capability code.
- **Consumers that inject `IChatClient` directly continue to work.** They
  transparently receive the `Balanced` client — the framework guarantees
  this. No capability is required to be tier-aware; it's an opt-in
  optimization surface.

Direct injection of a single `IChatClient` (as used by v0.1's
`ExtractStructuredDataCapability`) remains supported and backwards-
compatible with the tiered registration.

---

## 4. Project structure

Standalone open-source project under Marimer LLC. Independent of RockBot
(neither agent nor framework lives inside Foragent's repo), but depends on
the RockBot framework as a NuGet package.

### 4.1 Repository layout

```
Foragent/
├── .github/
│   ├── workflows/
│   │   └── ci.yml
│   └── copilot-instructions.md
├── src/
│   ├── Foragent.Agent/              -- A2A server host (executable)
│   ├── Foragent.Browser/            -- Playwright wrapper
│   ├── Foragent.Capabilities/       -- Task-level capability definitions
│   └── Foragent.Credentials/        -- ICredentialBroker abstraction
├── tests/
│   ├── Foragent.Agent.Tests/
│   ├── Foragent.Browser.Tests/
│   └── Foragent.Integration.Tests/
├── samples/
│   └── Foragent.Sample.Console/     -- Minimal A2A client
├── docs/
│   ├── architecture.md
│   ├── capabilities.md
│   └── credentials.md
├── deploy/
│   └── (Dockerfile, Helm charts — added later)
├── .gitignore
├── Foragent.sln
├── Directory.Build.props
├── Directory.Packages.props
├── LICENSE                           -- MIT
└── README.md
```

### 4.2 Project responsibilities

- **Foragent.Agent** — entry point. Hosts the A2A server. Owns DI wiring,
  configuration, logging, and the agent loop. Depends on RockBot
  framework NuGets for agent infrastructure.
- **Foragent.Browser** — wraps Microsoft.Playwright. Owns browser and
  context lifecycle. Exposes a clean .NET API to the rest of the project
  so Playwright could theoretically be swapped later without rewriting
  the agent loop.
- **Foragent.Capabilities** — task-level operations exposed via A2A
  (post-to-site, extract-data, fill-form, etc.). Each capability is a
  small focused class. This is the *product surface* of Foragent.
- **Foragent.Credentials** — `ICredentialBroker` abstraction plus a
  small set of built-in implementations (in-memory for dev, k8s secrets
  for prod). External users bring their own implementations for other
  secret stores.

### 4.3 Tech stack

- **.NET 10** (latest stable as of project start; track current LTS/STS)
- **C# latest language version**
- **Microsoft.Playwright** — browser automation
- **Microsoft.Extensions.AI** — LLM abstraction for internal agent reasoning
- **Microsoft.Extensions.Hosting** — host model
- **xUnit** — testing
- **RockBot framework** — agent loop, A2A server primitives, tier routing,
  skill-doc support
- **Base container image:** `mcr.microsoft.com/playwright/dotnet` (ships
  Chromium and dependencies)

### 4.4 Licensing

MIT license. Matches CSLA and the broader .NET OSS ecosystem.

---

## 5. Capability surface

The capability list is the product. Foragent's value is what verbs it
exposes via A2A, not what's inside.

### 5.1 Capability model

Foragent exposes capabilities at two tiers:

1. **Generalist.** One capability — `browser-task` — that accepts
   free-form intent plus optional URL and credential hints. Runs an
   LLM-in-the-loop planner over the browser primitives, using any
   learned site knowledge from the skills and memory stores (§5.6) as
   priming. This is the default surface — the thing most callers should
   invoke.
2. **Fast-path specialists.** A small set of narrow, structured
   capabilities that do one well-defined thing cheaply and
   deterministically. `fetch-page-title` and `extract-structured-data`
   are specialists. New specialists are added only when usage shows a
   consistent, high-volume pattern that benefits from a typed interface.

Most real callers are themselves LLM agents. They default to the
generalist. Specialists exist to keep deterministic, programmatic
callers cheap — not to proliferate.

### 5.2 Initial capability set (v0.2)

| Capability | Tier | Description |
|------------|------|-------------|
| `browser-task` | Generalist | Given intent + optional URL, credential id, and allowed-hosts list, plan and drive the browser to fulfill the intent. Uses RockBot skills + memory as priming. Returns a result or a structured intermediate artifact (e.g. a learned form schema). |
| `learn-form-schema` | Specialist (phase-1) | Given a URL and optional credential, introspect a form and return its schema — fields, types, dropdown dependencies, validation rules. Persists the schema as a skill (§5.6). Returns the schema to the caller for review. |
| `execute-form-batch` | Specialist (phase-2) | Given a learned schema (by id or inline) and a batch of row data, submit the form once per row. Streams A2A progress updates. Handles partial failure. |
| `fetch-page-title` | Specialist | Return the `<title>` of a URL. Inherited from milestone 2. |
| `extract-structured-data` | Specialist | Extract structured data from a page matching a natural-language description. Inherited from milestone 3. |

The v0.1 `post-to-site` capability ships in the main codebase as a
regression test for credential handling. After step 7 it is removed
from the advertised skill list; `browser-task` subsumes its function.

The v0.1 `monitor-page` and `fill-form` capabilities fold into
`browser-task` and do not ship as separate advertised skills.

### 5.3 Capabilities explicitly out of scope (v1)

- Test automation (Playwright already does this).
- Raw browser primitive exposure (Microsoft's `@playwright/mcp` does
  this; Foragent operates one level up — task-shaped, not tool-shaped).
- Visual regression testing.
- Form-filling for sensitive financial transactions, account creation,
  or modifying security permissions (see §7.3).
- Multi-tab orchestration as a primary feature (may be used internally
  but not advertised).
- Code generation from browser traces (e.g. "generate a Playwright
  script that reproduces this"). Traces stay inside the learning
  substrate.

### 5.4 Capability design principles

- **Task-level, not action-level.** "Submit these rows to that form"
  is a capability; "click button" is not.
- **Clear contracts even for the generalist.** `browser-task`'s input
  shape is typed (intent, url?, credentialId?, allowedHosts, maxSteps?);
  only the *plan* inside is LLM-generated.
- **Return structured state, not narrative, when the caller needs to
  act on it.** A learned form schema is typed JSON, not prose. A
  submit-batch progress report is a typed status update, not a sentence.
- **Delegate to the learning substrate, don't reinvent it.** Site
  knowledge lives in RockBot skills + memory; the capability reads and
  writes, it does not own its own cache.
- **Credentials by reference.** Capabilities receive a credential id;
  the broker (§6) resolves inside the Foragent process.

### 5.5 Multi-phase flows

Many real browser tasks are multi-phase with human or caller-side
review between phases. The motivating example:

1. **Phase 1 — Learn.** Navigate to a form; introspect its fields and
   dynamic dependencies; return a schema to the caller.
2. **Review.** The caller (human via their own UI, or another agent)
   inspects the schema, decides whether to proceed, assembles input
   data, validates.
3. **Phase 2 — Execute.** Submit the form N times against the learned
   schema, streaming progress.

Foragent's role is Phase 1 and Phase 3. Phase 2 (review) is the
caller's responsibility — Foragent is not in the review loop.

To make this work:

- Phase-1 capabilities **return structured artifacts** (form schemas,
  extracted data, observed flow traces), not just status text.
- Phase-1 artifacts are **persisted in the learning substrate** (§5.6)
  and get an id the caller can reference in Phase 3.
- Phase-3 capabilities **accept a learned-artifact reference or inline
  artifact** as input, alongside per-invocation data.
- Phase-3 capabilities **stream progress and handle partial failure**
  over A2A — not batch-atomic.

This is not an A2A protocol change. A2A 1.0 already supports structured
response parts, streaming status updates, and task-id references; v0.2
makes explicit use of all three.

### 5.6 Learning substrate

Foragent uses the RockBot framework's existing persistence for learned
site knowledge, rather than building a Foragent-local store.

**What's used:**

- **`ISkillStore`** (file-backed, BM25 + optional semantic retrieval —
  `RockBot.Host.Abstractions` + `RockBot.Host.AgentMemoryExtensions.WithSkills()`).
  Stores site knowledge as markdown skills. Two origin categories:
  - **Human-authored skills** — operator-written primers for a site
    (e.g. `sites/bsky.app/overview`). Treated as priming hints for the
    generalist planner.
  - **Agent-learned skills** — written by the generalist on successful
    task completion (e.g. `sites/bsky.app/learned/login-flow`). Tagged
    with `metadata.source = "agent-learned"` and an importance score.
- **`ILongTermMemory`** (file-backed, BM25 + semantic —
  `WithLongTermMemory()`). Declarative observations that don't fit the
  procedural skill shape: failed attempts, site-version notes, ambient
  facts.

**Skill naming:** `sites/{host}/{phase-or-intent}` — e.g.
`sites/bsky.app/login`, `sites/bsky.app/compose-post`. Hierarchical `/`
nesting is supported by the store. `seeAlso` links cross-reference
skills for the same site so retrieval surfaces a small knowledge
cluster, not one skill at a time.

**Retrieval at plan time:**

1. Capability computes a search query from task intent + target URL host.
2. Queries skill store and memory store in parallel, top-K by relevance.
3. Retrieved content becomes priming context for the LLM planner.
4. New observations surface as writes after the task completes.

**Structured artifacts (the form-schema case):**

Learned form schemas are typed JSON, not markdown. Skill store holds
markdown content. Resolution deferred to step 8; current options are
(A) embed JSON in a fenced code block inside a skill, re-parse on
retrieval, or (B) add a parallel Foragent-local typed store keyed by
skill id. Framework-feedback tracks this as a candidate
`ISkillStore.AttachedArtifacts` extension if the shape recurs.

### 5.7 Human-in-the-loop

Review gates are the **caller's** responsibility, not Foragent's.

- Foragent returns structured state at phase boundaries (§5.5).
- The caller decides whether to proceed. Human callers use their own
  UI; agent callers make the decision programmatically.
- Foragent does **not** block waiting for review. Each phase is a
  separate A2A task.

A2A's `input-required` state is used only for mid-task credential
flows (2FA, §6.6). It is not used as a general "stop and let the human
review" mechanism — that coupling would force Foragent to hold browser
state across potentially-long human delays, which conflicts with the
one-context-per-task model (§3.5).

---

## 6. Credential handling

### 6.1 Threat model

Browser automation has a much wider blast radius than other forms of
agent action:

- A browser session can authenticate to accounts, post publicly, make
  purchases.
- Credentials passed in plaintext to an agent end up in: the calling
  agent's LLM context (and thus model provider logs), A2A message
  bodies, structured logs, tracing spans.
- A prompt-injection attack on the calling agent (via, say, a malicious
  page scraped earlier) could exfiltrate any credentials that agent
  has been given.

The credential design must minimize the blast radius and ensure that
**the calling agent never sees credential values**.

### 6.2 Design

`ICredentialBroker` is the contract:

```csharp
public interface ICredentialBroker
{
    Task<CredentialReference> ResolveAsync(
        string credentialId,
        CancellationToken cancellationToken = default);
}
```

The calling agent passes a credential **identifier** (e.g.
`rockbot/social/bluesky-rocky`) in the A2A task payload. Foragent's
broker resolves the identifier to actual credential values inside the
Foragent process. The values are used to authenticate the browser
session and never leave the process.

### 6.3 Built-in implementations

Ship at least two:

1. **In-memory broker** for development and testing. Loads from a local
   YAML or JSON file. Never used in production.
2. **Kubernetes secrets broker** for production homelab/cloud
   deployments. Uses a service account with read access to a specific
   namespace's secrets.

Out-of-the-box support for additional stores (Azure Key Vault, HashiCorp
Vault, AWS Secrets Manager) may be added but is not a v1 requirement.
External users implement `ICredentialBroker` for their own stores.

### 6.4 Credential bootstrapping

Foragent does not provide a UI or API for *adding* credentials. That is
out-of-band, handled through the user's normal secrets workflow
(kubectl, sealed-secrets, ArgoCD reconciliation, Vault UI, etc.).
Foragent only resolves credentials that already exist.

The agent can however report a *catalog of credential identifiers* it
has access to (without values), so a calling agent can say "I'd need
a Bluesky credential to do this and I don't see one configured — please
add one."

### 6.5 Storage state and session persistence

Browser session state (cookies, localStorage, etc.) is treated as a
credential. After successful login, `storageState` is exported and
persisted via the broker as a separate credential reference. Subsequent
tasks load the stored session rather than re-authenticating, reducing
2FA challenges and login latency.

Storage state is refreshed periodically (default: weekly) or on demand
when it is detected as expired.

### 6.6 Two-factor authentication

App passwords are preferred where available (Bluesky, Google).

When a real 2FA challenge occurs:

- Foragent transitions the A2A task to **input-required** state.
- The calling agent receives the request, surfaces it to the human
  (via whatever notification channel the calling agent uses).
- The human responds with the code through the calling agent.
- Foragent resumes the task using the provided code.

TOTP secrets are **not** stored alongside passwords by default. Doing so
defeats the purpose of 2FA (a single-store breach exposes both factors).
TOTP-via-broker is supported but discouraged; the human-in-the-loop
flow is the recommended pattern.

---

## 7. Security and safety

### 7.1 Domain allowlists

Every capability invocation — especially the generalist `browser-task`
(§5.2) — **must** carry an explicit allowed-hosts list. Empty list
**rejects** the task; there is no default-permissive mode.

Wildcards are supported to keep callers from having to enumerate every
subdomain:

- Exact host: `bsky.app`
- Subdomain wildcard: `*.example.com` (matches `foo.example.com`,
  `foo.bar.example.com`; does not match `example.com` itself — list
  both if both are desired).
- Fully unrestricted: `*` (explicit only; still callable, still logged).

Foragent refuses any navigation, fetch, or subframe load outside the
list before Playwright sees the URL. Per-tenant defaults (future, §7.5)
will let individual tasks inherit rather than list everything on every
call. Ad-hoc "navigate to whatever looks relevant" is explicitly not
supported — the generalist is powerful but bounded.

### 7.2 Network egress policies

In Kubernetes deployments, NetworkPolicy at the pod level should
restrict egress to allowlisted destinations. Foragent does not enforce
this itself but ships sample manifests demonstrating the pattern.

### 7.3 Prohibited capabilities

Some browser actions are out of scope at the project level, regardless
of capability surface design:

- **Account creation on the user's behalf.** Users create accounts
  themselves.
- **Financial transactions** (purchases, transfers, trading).
- **Modifying security permissions or access controls** on accounts.

These align with broader agent-safety norms and avoid creating tools
that can do irreversible high-stakes actions without human confirmation.

### 7.4 Audit logging

Every task records:

- Caller identity (from A2A)
- Capability invoked and parameters
- Domains navigated to
- Form submissions (with field names but not sensitive values)
- Completion status

Sensitive values (credential contents, form values for password fields)
are never logged.

### 7.5 Multi-tenant isolation

Tenant identity comes from the A2A caller identity, not from request
payloads.

- Browser contexts are per-task; cross-task contamination is impossible.
- Credential namespaces are per-tenant. A tenant's broker queries are
  scoped to its own credential namespace.
- Audit logs are per-tenant.

Multi-tenant from day one even if RockBot is the only initial tenant.
Designing for multi-tenant later is painful; designing for it and having
one tenant is easy.

---

## 8. Relationship to RockBot

### 8.1 RockBot is two things

- **RockBot the agent** — Rocky's personal AI agent (the running djinn
  instance).
- **RockBot the framework** — NuGet packages providing agent infrastructure
  (A2A server primitives, agent loop, tier routing, skill-doc support,
  Microsoft.Extensions.AI integration patterns).

Foragent depends on RockBot **the framework**, not RockBot **the agent**.
The dependency is a versioned NuGet reference.

### 8.2 Foragent as second framework consumer

Until Foragent, RockBot the agent has been the only consumer of the
RockBot framework. Foragent is the second.

This is valuable in itself:

- **Tests the framework's framework-ness.** Surfaces assumptions baked
  into the framework that aren't actually general — places where
  RockBot the agent's needs leaked into the framework abstractions.
- **Validates the value proposition.** If building Foragent on the
  framework is meaningfully easier than building it from scratch, the
  framework is doing its job.
- **Enables versioning discipline.** With one consumer, the framework
  can change anything. With two, breaking changes have real cost.
  Better to feel that pain at two consumers than at twenty.
- **Strengthens the "this is a framework" perception.** A framework with
  two diverse consumers reads more clearly as a framework than one with
  a single consumer.

### 8.3 Where Foragent will likely push on the framework

Areas where Foragent's needs will probably surface framework gaps:

- **A2A server-side primitives** — RockBot the agent has been more A2A
  client than server.
- **Capability/skill declaration patterns** for agents with narrow,
  well-defined surfaces (vs. RockBot's broad personality-driven model).
- **Stateless or near-stateless agent topology** (vs. RockBot's rich
  per-user memory model).
- **Reactive task processing** (vs. RockBot's heartbeat/proactive patrol
  pattern). Foragent should be able to opt out of heartbeat cleanly.
- **Configuration and bootstrapping cost** for new agents on the
  framework — should be `AddRockBotAgent()` with sensible defaults, not
  hundreds of lines of DI wiring.

### 8.4 Discipline

When Foragent needs a framework feature that requires a framework change,
that change happens **in the framework repo**, ships as a new NuGet
version, and Foragent updates its dependency. Framework changes do not
happen as monkey-patches inside Foragent.

This is harder than the alternative but is the only way to actually
validate (rather than circumvent) the framework boundary.

---

## 9. Sequencing

Build order optimized to surface framework feedback early and to defer
hard design questions until usage forces them.

### 9.1 Milestones

**Steps 1–5 — shipped (v0.1):**

1. **Empty agent on RockBot framework.** `fetch-page-title` with no
   Playwright.
2. **Real Playwright integration for that capability.**
3. **Second capability** — `extract-structured-data` (Playwright + LLM).
4. **Credentials and `post-to-site` for Bluesky.** `ICredentialBroker`
   + `InMemoryCredentialBroker` + `BlueskySitePoster`.
5. **RockBot wired to Foragent via A2A.** Validation loop; RockBot
   becomes Foragent's first real user.

**Steps 6–9 — v0.2 sequence:**

6. **Baseline `browser-task` generalist.** LLM-in-the-loop planner built
   directly on `Microsoft.Playwright` NuGet (no MCP sidecar, no
   Stagehand — see Appendix A #16). Exposes a small `[AIFunction]`
   tool set — `snapshot`, `click`, `type`, `navigate`, `wait_for`,
   `done`, `fail` — through `IChatClient`. Uses `Page.AriaSnapshotAsync()`
   ref-annotated snapshots and `Page.Locator("aria-ref=eN")` for ref
   resolution. No learning substrate yet. Measure unaided success rate
   on a small curated benchmark. Goal: establish the floor before
   investing in priming.

7. **Wire RockBot skills + memory as priming.** Register `ISkillStore`
   + `ILongTermMemory` in Foragent's host. Retrieve relevant skills
   into planner context; write agent-learned skills on success. Seed
   one human-authored skill for `bsky.app`. Wire `IEmbeddingGenerator`
   for semantic retrieval. Remove `post-to-site` from the advertised
   skill list once `browser-task` + the learned bsky skill cover it.
   Goal: prove the framework's persistence is the right substrate;
   file issues if it isn't.

8. **`learn-form-schema` + `execute-form-batch`.** First explicit
   multi-phase capability pair. Structured JSON schema returned from
   phase 1, batch execution with streaming per-row progress in phase 2.
   Resolve open question #6 (how to persist typed JSON alongside
   markdown skills) in the deliverable.

9. **Deprecate subsumed specialists.** Review whether `fetch-page-title`
   / `extract-structured-data` still pay their way or fold into
   `browser-task` with equivalent cost. Land on the minimum advertised
   capability set v0.2 actually needs.

Each milestone produces framework feedback. Capture it in
`docs/framework-feedback.md` — some will be small ergonomic fixes; some
may be "the framework should really have a concept of X."

### 9.2 What is explicitly out of scope for v1

- Container packaging beyond a single working Dockerfile.
- Helm charts and production k8s manifests.
- KEDA autoscaling integration.
- Multi-tenant credential broker UIs.
- Browser pool management (single shared Chromium per pod — §3.5).
- Non-browser automation (desktop, mobile, API-only flows).

(The v0.1 "no Stagehand-style natural-language-to-action layers" item
is deliberately removed. v0.2's `browser-task` *is* that layer, built
natively on Playwright NuGet — see Appendix A #16.)

---

## 10. Distribution and operating model

### 10.1 Initial mode

Personal infrastructure that happens to be public. Low maintenance
burden, no community obligations, built primarily for RockBot, others
use at their own risk.

### 10.2 Possible future modes

- **Lightweight community project** — accepting issues and PRs, semantic
  versioning, basic docs, no enterprise support promises.
- **Real product** (CSLA-shaped) — community, samples, conference talks,
  stable releases, support obligations.

Decision to advance to the next mode happens after at least six months
of personal usage and is based on actual demand, not aspiration.

### 10.3 Distribution channels

- **GitHub** (https://github.com/MarimerLLC/Foragent) — primary repo,
  issues, releases.
- **NuGet** — `Foragent.*` packages, published once the project actually
  works end-to-end. No placeholder packages.
- **Docker Hub or GHCR** — container images for the deployable agent,
  published alongside NuGet.
- **Project site** — `foragent.dev` (primary) and `foragent.net`
  (secondary). Both should be registered to prevent collision.

---

## 11. Naming rationale

The name **Foragent** is a portmanteau of **forage** and **agent**.

- Captures the verb: an agent that goes out into wild territory to find
  and gather what's needed.
- Self-explaining: developers parse "foraging agent" immediately.
- Distinctive: not a real English word, no collision with existing
  concepts or projects.
- Works as both noun ("the Foragent service") and verb ("send a
  foragent to fetch that page").
- Pairs with RockBot without confusion — different vibe (functional vs.
  personality-driven), different naming family.

Domains `foragent.dev` and `foragent.net` are both available at
reasonable prices; both should be registered. The `.net` TLD has the
useful double meaning of being both a generic TLD and the platform
identifier for .NET.

---

## 12. Open questions

These are real design questions deferred until usage forces an answer.

1. ~~Internal LLM selection and tier routing.~~ **Closed** in v0.2
   §3.7 — Foragent uses RockBot's `TieredChatClientRegistry`; ships
   with one model aliased across tiers; capabilities are tier-aware.
2. ~~Stagehand-equivalent for .NET.~~ **Closed** in v0.2 — built
   natively on `Microsoft.Playwright` NuGet; no Stagehand port, no
   `@playwright/mcp` sidecar. See Appendix A #16.
3. **Storage state encryption at rest.** Storage state is sensitive but
   not as sensitive as raw credentials. Does it need stronger protection
   than the credential broker provides, or is broker-level fine?
4. **Capability versioning.** When a capability's contract changes, how
   does the A2A capability advertisement signal that to callers?
   Defer until a capability actually needs to change shape.
5. **Tenant identity model.** A2A 1.0-preview's identity model is still
   evolving. Lock in the tenant identity story once A2A 1.0 stabilizes.
6. **Structured artifacts in `ISkillStore`.** Learned form schemas
   (§5.6) are typed JSON; skills store markdown. Stretch the skill
   shape (fenced JSON, re-parse on retrieval) or add a parallel
   Foragent-local typed store keyed by skill id? Decide at step 8.
7. **Per-task budget.** How do we cap an LLM-in-the-loop task — max
   steps, max tokens, wall-clock, cost? Proposed defaults:
   `maxSteps=30`, `maxSeconds=120`, caller can raise within bounds.
   Needed by step 6.
8. **Retry and failure semantics for batches.** In `execute-form-batch`,
   is a row failure fatal or per-row? How are partial results streamed?
   Needed by step 8.

---

## Appendix A — Decisions log

Brief record of decisions made during design discussion, in case any
need to be revisited.

| # | Decision | Rationale |
|---|----------|-----------|
| 1 | Use Playwright via NuGet, not via MCP server container | Direct library access, native .NET ergonomics, lower latency, fewer moving parts. |
| 2 | Build as a dedicated agent, not as RockBot internals | Right abstraction level, cleaner credential blast radius, reusable beyond RockBot, validates RockBot framework. |
| 3 | Use A2A protocol, not custom protocol | A2A's task lifecycle (working / input-required / completed) maps cleanly to long-running browser tasks; RockBot already supports A2A 1.0-preview. |
| 4 | One shared browser instance, fresh BrowserContext per task | Faster than browser-per-task, cleaner than persistent contexts, isolation guarantees come from BrowserContext design. |
| 5 | One A2A task per pod by default; scale horizontally | Simpler than in-pod concurrency; KEDA can scale on queue depth. |
| 6 | Standalone project, separate from RockBot repo | Forces clean API boundary; validates RockBot as a framework rather than a single-app codebase. |
| 7 | Foragent depends on RockBot framework via versioned NuGet | Forces real release discipline on the framework side. |
| 8 | Credentials by reference, never by value | Calling agent never sees credential contents; minimizes blast radius from prompt injection or log exfiltration. |
| 9 | ICredentialBroker is pluggable; ship in-memory + k8s-secrets | Most users want different secret stores; abstraction lets them bring their own. |
| 10 | Storage state persisted as credential-class secret | Avoids repeated logins and 2FA challenges; same security model as raw credentials. |
| 11 | 2FA handled via A2A input-required state, not stored TOTP | Storing TOTP defeats 2FA; human-in-loop preserves the security property. |
| 12 | Multi-tenant from day one, even with one tenant | Designing for multi-tenant later is painful; designing for it and having one tenant is easy. |
| 13 | MIT license | Matches CSLA and the broader .NET OSS ecosystem. |
| 14 | .NET 10, C# latest | Current stable .NET as of project start. |
| 15 | Name: Foragent | Distinctive, self-explaining, available domains, no dev-tools collision. |
| 16 | Build generalist `browser-task` on Microsoft.Playwright NuGet directly — no Stagehand port, no `@playwright/mcp` sidecar | Ref-annotated aria snapshots and `aria-ref=eN` locator resolution are Playwright features, not MCP-exclusive. `[AIFunction]` tool wrapping over `IChatClient` gives MCP-equivalent function-calling in-process. Keeps credential boundary (§6.1) clean and preserves v0.1 decision #1. |
| 17 | Use RockBot's `TieredChatClientRegistry` (Low/Balanced/High) with Balanced as the injected default | Future cost-optimization can route cheaper classes of work (extraction, snapshot summarization) to Low without capability rewrites. v0.2 ships with one model aliased across tiers. |
| 18 | Allowlists are mandatory per-task with wildcard support (`*.example.com`, `*`) | Generalist LLM-in-the-loop planner has much wider blast radius than fixed-flow specialists; empty list must reject. Wildcards keep callers from enumerating subdomains. |
| 19 | Learned site knowledge lives in RockBot's `ISkillStore` + `ILongTermMemory`, not a Foragent-local store | Framework-owned persistence is already packable, DI-registerable, and has BM25+semantic hybrid retrieval with importance weighting. Building parallel infrastructure would be duplicate work and would miss the framework-validation goal (§8). |
| 20 | Multi-phase flows (learn → review → execute) are expressed as separate A2A tasks, not one long-running task with `input-required` | Review gates are the caller's concern; Foragent would otherwise have to hold browser state across arbitrary human delays, breaking the one-context-per-task isolation model (§3.5). |
