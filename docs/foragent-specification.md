# Foragent — Project Specification

> **Status:** Design specification, pre-implementation.
> **Date:** April 2026
> **Author:** Rocky Lhotka, Marimer LLC
> **Repository (planned):** https://github.com/MarimerLLC/Foragent

---

## 1. Summary

**Foragent** is an A2A-native browser agent for .NET. It exposes browser
automation capabilities — navigate, extract, fill forms, post to sites,
monitor pages — over the Agent2Agent (A2A) protocol. Other agents delegate
browser work to Foragent rather than reasoning about DOM selectors, session
state, or 2FA flows themselves.

Foragent is built on the **RockBot framework** (the NuGet packages
maintained at https://github.com/MarimerLLC/rockbot) and uses the official
**Microsoft.Playwright** NuGet package for browser automation. It is the
second consumer of the RockBot framework, after the RockBot personal agent
itself.

Foragent is a standalone open-source project under Marimer LLC. RockBot
is its first user, but the project is designed to be generally useful to
anyone building agentic systems on .NET that need a self-hosted browser
worker.

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

### 5.1 Initial capability set (v0.x)

Start narrow. Add only when usage demands it.

| Capability | Description |
|------------|-------------|
| `fetch-page-content` | Navigate to a URL, return rendered text and structured page metadata. |
| `extract-structured-data` | Navigate to a URL, extract data matching a description (e.g. "the product price and availability"). |
| `fill-form` | Navigate to a URL, fill out a form given a description of the values, submit. |
| `post-to-site` | Authenticate against a configured site (using credential broker) and post content. First targets: Bluesky, Mastodon. |
| `monitor-page` | Periodically check a page for changes matching a description; emit A2A progress updates when changes occur. |

### 5.2 Capabilities explicitly out of scope (v1)

- Test automation (Playwright already does this)
- Raw browser primitive exposure (Microsoft's playwright/mcp does this)
- Visual regression testing
- Form-filling for sensitive financial transactions or account creation
  (see Section 7.3)
- Multi-tab orchestration as a primary feature (may be supported
  internally but not advertised as a capability)

### 5.3 Capability design principles

- Each capability has a **clear, named contract** — inputs, outputs,
  error modes documented.
- Capabilities are **task-level, not action-level**. "Post to site" is
  a capability; "click button" is not.
- Capabilities **may delegate to internal LLM reasoning** for
  selector resolution, intent translation, and retry logic. This is
  what makes Foragent an *agent* rather than a wrapper.
- Capabilities **respect the credential broker contract**. They
  reference credentials by ID; they never receive raw values from
  callers.

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

Per-task allowlists for navigable domains. The calling agent can
constrain a task to specific origins; Foragent refuses navigation
outside the allowlist. Default is restrictive, not permissive.

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

1. **Empty agent on RockBot framework.** Stand up Foragent.Agent that
   registers itself as an A2A server with one trivial capability
   (`fetch-page-title`). No Playwright yet. Goal: feel the bootstrap
   cost of building a new agent on RockBot.

2. **Real Playwright integration for that capability.** Add
   Microsoft.Playwright NuGet, implement `fetch-page-title` for real
   against actual web pages. Goal: feel the integration story between
   RockBot's agent loop and the Playwright library.

3. **Add a second capability** (`extract-structured-data`). Goal: feel
   how the framework supports growing the capability surface.

4. **Add credentials and a third capability that needs them**
   (`post-to-site` for Bluesky). Goal: end-to-end credential broker
   story including ICredentialBroker abstraction and at least one
   real implementation.

5. **Wire RockBot the agent up to call Foragent via A2A.** Goal:
   validate the full loop. RockBot becomes Foragent's first real user.

Each milestone produces framework feedback. Capture it. Some will be
small ergonomic fixes; some may be "the framework should really have a
concept of X."

### 9.2 What is explicitly out of scope for v1

- Container packaging beyond a single working Dockerfile
- Helm charts and production k8s manifests
- KEDA autoscaling integration
- Multi-tenant credential broker UIs
- Agent self-improvement / learning
- Browser pool management
- Stagehand-style natural-language-to-action layers (may be revisited
  later; the internal LLM-based selector resolution is sufficient for
  v1)

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

1. **Internal LLM selection and tier routing.** Foragent will use
   Microsoft.Extensions.AI for internal reasoning. Which tier routing
   patterns from RockBot apply directly, and which are RockBot-specific?
2. **Stagehand-equivalent for .NET.** Stagehand is Node-only. Should
   Foragent build an equivalent natural-language `page.act()` layer in
   C# using its internal LLM? Defer to v2 unless v1 selector-resolution
   proves insufficient.
3. **Storage state encryption at rest.** Storage state is sensitive but
   not as sensitive as raw credentials. Does it need stronger protection
   than the credential broker provides, or is broker-level fine?
4. **Capability versioning.** When a capability's contract changes, how
   does the A2A capability advertisement signal that to callers?
   Defer until a capability actually needs to change shape.
5. **Tenant identity model.** A2A 1.0-preview's identity model is still
   evolving. Lock in the tenant identity story once A2A 1.0 stabilizes.

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
