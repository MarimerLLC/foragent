# Foragent Specification v0.2 — Proposal

> **Status:** Proposed revision to `foragent-specification.md`. Not yet merged.
> **Date:** April 2026
> **Author:** Rocky Lhotka / session notes from step 5 retrospective

This document proposes a direction change for Foragent after completing
milestone 5. It captures the revised product vision, the implied spec
edits, and a step-by-step implementation plan for milestones 6 through 9.

The v0.1 spec is still the governing document. Once this proposal is
reviewed and approved, the changes below are folded into
`foragent-specification.md` and this file is archived.

---

## Part 1 — What's changing and why

### What we learned in v0.1 (milestones 1–5)

Milestones 1–5 shipped three narrow capabilities (`fetch-page-title`,
`extract-structured-data`, `post-to-site`) and validated the RockBot
framework boundary end-to-end. They also surfaced a design question the
spec didn't anticipate: **how does Foragent scale to N websites without
N site-specific capability implementations?**

The step-4 `BlueskySitePoster` was a deliberate probe — ship one
hand-written site poster, learn what it costs to add the second. The
answer: it costs a full `ISitePoster` subclass, a CSS selector audit, a
fake-server integration test, and a re-verification every time the site
redesigns. That doesn't scale, and it isn't the product.

Step 5 also exposed a second mismatch. RockBot's `invoke_agent` tool
passes a single free-text `message` argument to the called agent. Its
LLM tried to invoke `post-to-site` with `message="Create a post on
Bluesky with the text: ..."` — not a structured `{site, credentialId,
content}` object. Narrow typed skills are hostile to natural-language
callers.

### What v0.2 is

Foragent becomes an **agentic browser agent**: given a free-form intent
and a target URL, it plans and drives the browser to fulfill the intent,
using internal LLM reasoning to resolve selectors, form structure, and
retry strategy. Site-specific code is the exception, not the rule.

This is what v0.1 §9.2 called "Stagehand-style natural-language-to-action
layers" and flagged as "may be revisited later, v1 selector-resolution
is sufficient." It wasn't. v0.2 revisits.

### What v0.2 is *not*

v0.2 is not a .NET port of Stagehand, and does not run Stagehand or
`@playwright/mcp` as a Node sidecar. Direct integration was evaluated
against the `Microsoft.Playwright` NuGet path already in use since
milestone 2, and the NuGet path won on every relevant axis:

- **Ref-annotated aria snapshots are a Playwright feature, not an MCP
  feature.** `Page.AriaSnapshotAsync()` already emits stable `[ref=e42]`
  markers, and `Page.Locator("aria-ref=e42")` resolves them. The LLM
  picks refs the same way it would through MCP, with no process hop.
- **Tool-schema wrapping is a trivial amount of C#.** Exposing
  `snapshot`/`click`/`type`/`navigate`/`wait_for` as `[AIFunction]`
  methods on an injected planner surface gives `IChatClient` the same
  auto-discovered tool-calling experience MCP would, without the
  JSON-RPC protocol overhead.
- **Session state already lives in `Foragent.Browser`.** `IBrowserSession`
  / `IBrowserPage` own the shared browser and per-task `BrowserContext`
  per spec §3.5. Moving to MCP would rebuild that management on the
  far side of a process boundary.
- **Spec §6's credential boundary stays clean.** A Node sidecar handling
  browser actions would also handle credential material (login flows,
  form values). Keeping the inner layer in-process means credentials
  never cross a process boundary — the §6.1 blast-radius guarantee
  holds as written.
- **Spec §3.4 Decision #1 survives unchanged.** The v0.1 "Playwright via
  NuGet, not via MCP server container" decision was made for the same
  reasons; v0.2's agentic model does not invalidate any of them.

This closes v0.1 §12 open question #2 ("Stagehand-equivalent for .NET")
as **build natively, not integrate or port.**

### What stays

- **A2A-native, RockBot-framework-hosted, self-hosted.** Unchanged.
- **Credentials by reference via `ICredentialBroker`.** Unchanged; still
  the design v0.1 got right.
- **One shared browser, fresh `BrowserContext` per task.** Unchanged.
- **Prohibited-capability list in §7.3** (no account creation, no
  financial transactions, no security-permission changes). Unchanged;
  arguably more load-bearing under the broader model.

### What changes

- **§5 Capability surface.** The initial five-verb list becomes a
  two-tier model: one generalist capability plus a small set of narrow
  fast-path specialists. `BlueskySitePoster` becomes a regression test,
  not a template.
- **New §5.5 Multi-phase flows.** First-class support for "learn then
  execute" patterns with typed intermediate artifacts.
- **New §5.6 Learning substrate.** Foragent uses RockBot framework's
  `ISkillStore` + `ILongTermMemory` to persist learned site knowledge
  and retrieve it on subsequent tasks.
- **New §5.7 Human-in-the-loop.** Explicit statement that review gates
  are the caller's responsibility; Foragent returns structured state.
- **§7 Security.** Tighter: a generalist capability needs per-task
  domain allowlist + intent policy enforcement, not just "refuse to
  navigate off-allowlist."
- **§9 Sequencing.** Steps 6–9 added.
- **§9.2 Out of scope.** Stagehand-style exclusion removed.

---

## Part 2 — Proposed revised sections

### §5 Capability surface (replacement for current §5.1–§5.4)

#### §5.1 Capability model

Foragent exposes capabilities at two tiers:

1. **Generalist.** One capability (`browser-task`) that accepts
   free-form intent plus optional URL and credential hints. Runs an
   LLM-in-the-loop planner over the browser primitives, using any
   learned site knowledge from the skills / memory store as priming.
   This is the default surface — the thing most callers should invoke.

2. **Fast-path specialists.** A small set of narrow, structured
   capabilities that do one well-defined thing cheaply and
   deterministically. `fetch-page-title` and `extract-structured-data`
   are specialists. New specialists are added only when usage shows a
   consistent, high-volume pattern that benefits from a typed interface
   (e.g. "get the product price from an e-commerce page" if that
   genuinely becomes the 10%-of-all-calls shape — which it probably
   won't).

Most real callers are themselves LLM agents. They'll default to the
generalist. Specialists exist to keep deterministic, programmatic
callers cheap — not to proliferate.

#### §5.2 Initial capability set (v0.2)

| Capability | Tier | Description |
|------------|------|-------------|
| `browser-task` | Generalist | Given intent + optional URL/credential, plan and drive the browser to fulfill the intent. Uses RockBot skills + memory as priming knowledge. Returns structured result or intermediate artifact. |
| `learn-form-schema` | Specialist (phase-1) | Given a URL (and optional credential to log in first), introspect a form and return its schema — fields, types, dropdown dependencies, validation rules. Persists the schema as a skill for later reuse. Returns the schema to the caller. |
| `execute-form-batch` | Specialist (phase-2) | Given a previously-learned schema (by id or inline) and a batch of row data, submit the form once per row. Streams progress. Handles partial failure. |
| `fetch-page-title` | Specialist | (Existing, milestone 2) Return the `<title>` of a URL. |
| `extract-structured-data` | Specialist | (Existing, milestone 3) Extract structured data from a page matching a natural-language description. |

The v0.1 `post-to-site` capability ships in the main codebase as a
regression test for step-4 credential handling. It is not advertised in
new agent-card skill lists after step 7; `browser-task` subsumes its
function.

`monitor-page` and `fill-form` from v0.1 §5.1 fold into `browser-task`.

#### §5.3 Capabilities explicitly out of scope (v1)

- Test automation (Playwright already does this).
- Raw browser primitive exposure (Microsoft's `playwright/mcp` does this).
- Visual regression testing.
- Form-filling for sensitive financial transactions, account creation,
  or modifying security permissions (see §7.3).
- Multi-tab orchestration as a primary feature (may be used internally
  but not advertised).
- Code generation from browser traces (e.g. "generate a Playwright
  script that reproduces this"). Traces stay inside the learning
  substrate.

> ~~Stagehand-style natural-language-to-action layers~~ — removed.
> `browser-task` is that layer.

#### §5.4 Capability design principles

- **Task-level, not action-level.** Unchanged from v0.1.
- **Clear contracts even for the generalist.** `browser-task`'s input
  shape is typed (intent, url?, credentialId?, allowlist?, budget?);
  only the *plan* inside is LLM-generated.
- **Return structured state, not narrative, when the caller needs to
  act on it.** A learned form schema is JSON, not prose. A submit-batch
  progress report is a typed status update, not a sentence.
- **Delegate to the learning substrate, don't reinvent it.** Site
  knowledge lives in RockBot skills + memory; the capability reads and
  writes, it doesn't own its own cache.
- **Credentials by reference.** Unchanged.

### §5.5 Multi-phase flows (new)

Many real browser tasks are multi-phase with human review between
phases. The canonical example (motivating this revision):

1. **Phase 1 — Learn.** Navigate to a form; introspect its fields and
   dynamic dependencies; return a schema to the caller.
2. **Review.** The caller (human via Claude Code, or another agent)
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
  so Phase 3 doesn't re-learn. They get an id the caller can reference.
- Phase-3 capabilities **accept a learned-artifact reference or inline
  artifact** as input, alongside the per-invocation data.
- Phase-3 capabilities **stream progress and handle partial failure**
  over A2A — not batch-atomic.

This is not an A2A protocol change. A2A 1.0 already supports structured
response parts, streaming status updates, and task-id references. v0.2
makes explicit use of all three.

### §5.6 Learning substrate (new)

Foragent uses the RockBot framework's existing persistence for learned
site knowledge, rather than building a Foragent-local store.

**What's used:**

- **`ISkillStore` (file-backed, BM25 + optional semantic retrieval).**
  Stores site knowledge as markdown skills. Two origin categories:
  - **Human-authored skills** — operator-written primers for a site
    (e.g. `sites/bsky.app/overview`). Treated as priming hints for the
    generalist planner.
  - **Agent-learned skills** — written by the generalist on successful
    task completion (e.g. `sites/bsky.app/learned/login-flow`). Tagged
    with `metadata.source = "agent-learned"` and an importance score.
- **`ILongTermMemory` (file-backed, BM25 + semantic).** Declarative
  observations that don't fit the procedural skill shape: failed
  attempts, site-version notes, ambient facts ("bsky.app's home feed
  heading is the login success signal").

**What's stored (skill shape):**

- **Content:** markdown body describing the site, the flow, selectors,
  success signals, known pitfalls.
- **Name convention:** `sites/{host}/{phase-or-intent}` — e.g.
  `sites/bsky.app/login`, `sites/bsky.app/compose-post`. Hierarchical
  `/` nesting supported by the store.
- **`seeAlso` links** across skills for the same site, so retrieval
  surfaces a small knowledge cluster rather than one skill at a time.

**What's stored (memory shape for non-procedural facts):**

- **Category:** `sites/{host}` — so all site observations are
  retrievable together.
- **Tags:** freeform (`selector`, `flow`, `failure`, `version`, etc.).
- **Importance:** ranked 0–1. Confirmed-working patterns get high
  importance; one-off observations start low and drift with reuse.

**Retrieval pattern at plan time:**

1. Generalist capability computes a search query from the task intent
   + target URL host.
2. Queries skill store and memory store in parallel, top-K by relevance.
3. Retrieved content becomes priming context for the LLM planner.
4. Planner proceeds; any new observation surfaces as a write after the
   task completes.

**Structured artifacts (the form-schema case):**

Learned form schemas are typed JSON, not markdown. RockBot's skill store
holds markdown content. Two options, decision deferred to step 8:

- **(A)** Embed the JSON in a fenced code block inside the skill
  content. Loose — re-parse on retrieval.
- **(B)** Store the schema in an adjacent Foragent-local typed store,
  reference it by id from a skill. Tighter but duplicates infrastructure.

The framework-feedback log records (A) vs (B) as a candidate
`ISkillStore.AttachedArtifacts` extension for RockBot, if we hit the
shape often enough.

### §5.7 Human-in-the-loop (new)

Review gates are the **caller's** responsibility, not Foragent's.

- Foragent returns structured state at phase boundaries (§5.5).
- The caller decides whether to proceed. Human callers use their own UI
  (Claude Code, Blazor proxy, bespoke dashboards). Agent callers make
  the decision programmatically.
- Foragent does **not** block waiting for review. Each phase is a
  separate A2A task.

A2A's `input-required` state is still used for credential 2FA prompts
(§6.6). It is **not** used as a general "stop and let the human review"
mechanism — that coupling would force Foragent to hold browser state
across potentially-long human delays, which conflicts with the
one-context-per-task model (§3.5).

### §7.1 Domain allowlists (augmented)

Under v0.2, allowlists become more load-bearing because the generalist
can navigate anywhere the LLM plans to navigate. Every `browser-task`
invocation:

- **MUST** accept an explicit `allowedHosts` list (empty = reject).
- **MUST** refuse any navigation, fetch, or subframe load outside the
  list.
- **SHOULD** have per-tenant defaults (future: §7.5) so individual tasks
  can inherit rather than list everything.

Ad-hoc "navigate to whatever looks relevant" is explicitly not
supported. The generalist is powerful but bounded.

### §9.1 Milestones (extended)

Existing milestones 1–5: unchanged (shipped).

6. **Baseline `browser-task` generalist.** LLM-in-the-loop planner over
   existing browser primitives. No learning substrate yet. Measure
   unaided success rate on a small curated benchmark (e.g. 10 varied
   sites). Goal: establish the floor before investing in priming.

7. **Wire RockBot skills + memory as priming.** Register
   `ISkillStore` + `ILongTermMemory` in Foragent's host. Retrieve
   relevant skills into planner context. Write agent-learned skills on
   success. Goal: prove the framework's persistence surface is the
   right substrate; file issues if it isn't.

8. **`learn-form-schema` + `execute-form-batch`.** First explicit
   multi-phase capability pair. Structured JSON schema returned from
   phase 1, batch execution streaming progress in phase 2.

9. **Deprecate narrow specialists that `browser-task` covers.** Remove
   `post-to-site` from the advertised skill list (keep as regression
   test). Review whether `fetch-page-title` / `extract-structured-data`
   still pay their way or fold into `browser-task` with equivalent
   cost. Goal: land on the minimum capability set that v0.2 actually
   needs.

### §9.2 Out of scope (v1, revised)

Unchanged except:

- ~~Stagehand-style natural-language-to-action layers~~ — **removed.**
  `browser-task` is that layer.

### §12 Open questions (revised)

1. **Internal LLM selection and tier routing.** (Unchanged from v0.1.)
2. ~~Stagehand-equivalent for .NET.~~ — **closed.** v0.2 builds it
   natively on `Microsoft.Playwright` NuGet, not via Stagehand port or
   `@playwright/mcp` sidecar. See Part 1 "What v0.2 is not."
3. **Storage state encryption at rest.** (Unchanged.)
4. **Capability versioning.** (Unchanged.)
5. **Tenant identity model.** (Unchanged.)
6. **(New) Structured artifacts in `ISkillStore`.** Do we stretch the
   skill-as-markdown shape to carry typed JSON (fenced code blocks,
   parse on retrieval), or add a parallel Foragent-local typed store?
   Decide at step 8 based on how ugly (A) feels in practice.
7. **(New) Per-task budget.** How do we cap an LLM-in-the-loop task —
   max steps, max tokens, wall-clock, cost? Caller-specified, agent-
   enforced, or both? Needed by step 6.
8. **(New) Retry and failure semantics for batches.** In
   `execute-form-batch`, is a row failure fatal or per-row? Does the
   caller get per-row errors streamed, or a final report? Needed by
   step 8.

---

## Part 3 — Implementation plan

### Step 6 — Baseline `browser-task` generalist

**Goal:** prove the LLM-in-the-loop-over-browser-primitives baseline
works on real sites without learned priming. Establish the floor.

**Deliverables:**
- New `BrowserTaskCapability : ICapability` with skill id `browser-task`.
- Typed input: `{intent: string, url: string?, credentialId: string?,
  allowedHosts: string[], maxSteps: int?}`.
- Pure .NET planner, no Node sidecar, no MCP transport. Built on
  `Microsoft.Playwright` NuGet directly via a new `Foragent.Planner`
  project that consumes `IBrowserPage` from `Foragent.Browser`.
- Snapshot/action bridge: extend `IBrowserPage` (or add a sibling
  `IBrowserPlannerPage`) with `AriaSnapshotAsync()` returning
  ref-annotated aria text, plus `ResolveRefAsync("e42")` returning an
  `ILocator`. Playwright already emits `[ref=eN]` markers in aria
  snapshots and accepts `aria-ref=eN` in the selector engine — we're
  exposing that, not reimplementing it.
- Planner loop: snapshot → LLM selects next action → dispatch via ref →
  repeat until the planner emits a terminal action or max-steps is hit.
- LLM contract: a small `[AIFunction]` tool set — `snapshot`, `click`,
  `type`, `navigate`, `wait_for`, `done`, `fail` — surfaced through
  `IChatClient`'s native function-calling. No MCP JSON-RPC layer.
- Per-task allowlist enforced on every `navigate` before Playwright
  sees the URL.
- Integration test: real Kestrel host with a fixed form; `browser-task`
  fills it via free-text intent.
- Framework-feedback update: what the planner loop wanted from the
  framework that wasn't there.

**Out of scope for step 6:**
- Any learning / persistence.
- Multi-phase / returned artifacts.
- Credentials beyond what `IBrowserSession` already supports.

**Exit criteria:**
- `browser-task` completes the step-4 Bluesky poster flow end-to-end
  against the step-4 fake Kestrel server, *without* any
  Bluesky-specific code in Foragent's codebase (only the shared browser
  primitives and the LLM planner).
- Runs on 3+ more varied form shapes in tests.
- `BlueskySitePoster` still passes its existing regression tests —
  v0.2 does not break v0.1.

### Step 7 — Learning substrate wired

**Goal:** prove the framework's skills + memory is the right substrate
for site knowledge, and that retrieval-primed generalist runs beat
unaided runs.

**Deliverables:**
- `builder.WithSkills()` + `builder.WithLongTermMemory()` added to
  `Foragent.Agent/Program.cs`.
- `/data/foragent` volume in `docker-compose.yml` with
  `AgentProfile__BasePath=/data/foragent`.
- `BrowserTaskCapability` queries both stores pre-plan; retrieved
  content primes the planner. Query shape: intent + target host +
  top-K.
- On task success, planner writes one skill per distinguishable flow
  (login / action / success signal) keyed by host, tagged
  `metadata.source=agent-learned`.
- `IEmbeddingGenerator` wired (Azure OpenAI text-embedding-3-small or
  similar) for semantic retrieval. Falls back to BM25-only if not
  configured.
- Seed one human-authored skill for `bsky.app` as a priming example;
  check in as `deploy/skills-seed/sites/bsky.app/overview.md`.
- Integration test: cold run vs. primed run; assert primed run uses
  fewer LLM steps.

**Framework observations to capture:**
- Does `ISkillStore`'s markdown-content shape fit procedural site
  knowledge, or does it strain?
- Does memory's category/tag/importance model fit site observations?
- Any gaps in retrieval (e.g. no host-prefix query shape) → file
  rockbot issues.

**Exit criteria:**
- Primed `browser-task` runs on the same task consistently use ≥30%
  fewer planner LLM calls than the unprimed baseline from step 6.
- Agent-learned skills are readable and actionable when inspected by a
  human.

### Step 8 — Multi-phase: form learn + batch execute

**Goal:** first-class support for the motivating scenario — introspect
a form, return a reviewable schema, later submit a batch against it.

**Deliverables:**
- New `LearnFormSchemaCapability` with skill id `learn-form-schema`.
  - Input: `{url, credentialId?, allowedHosts}`.
  - Output: typed JSON schema — fields (name, type, visibility
    rules), dropdown options, dependency graph, submit button locator.
  - Persists the schema alongside a skill (open question #6 — decide
    at step start).
- New `ExecuteFormBatchCapability` with skill id `execute-form-batch`.
  - Input: `{url, credentialId?, schemaId | schema, rows[],
    allowedHosts, onError: "abort"|"continue"}`.
  - Streams A2A status updates per row.
  - Returns a per-row result array on completion.
- Integration test: Kestrel-hosted form with a dynamic dropdown
  (e.g. `category=alpha` reveals fields A/B, `category=beta` reveals
  fields C/D). Schema round-trips; batch of 20 mixed rows submits.
- Open question #8 decided (in the deliverable, not as prose):
  per-row continue-vs-abort, progress shape.

**Exit criteria:**
- Schema learned in one task, batch submitted in a separate task
  (different process invocations) against the persisted schema.
- Schema is human-reviewable: a developer can read it and understand
  what Foragent will submit before consenting.

### Step 9 — Deprecate subsumed specialists

**Goal:** land on v0.2's actual advertised capability set.

**Deliverables:**
- `post-to-site` removed from advertised `ForagentCapabilities.Skills`
  and from `deploy/rockbot-seed/well-known-agents.json` and
  `agent-trust.json` `approvedSkills`. Implementation stays in the
  codebase as a regression test for credential handling; integration
  tests remain green.
- `monitor-page` and `fill-form` from v0.1 §5.1 never shipped; remove
  from spec.
- Review: do `fetch-page-title` and `extract-structured-data` still
  pay their way? Measure runtime cost vs. equivalent `browser-task`
  calls. Remove if `browser-task` is competitive; keep if they're 10×+
  cheaper on the hot path.
- Spec v0.2 merged into `foragent-specification.md`; this proposal
  file archived to `docs/archive/`.

**Exit criteria:**
- Advertised capability list matches §5.2 of spec v0.2.
- No codepath is exercised only by deprecated specialists — every
  line has a live caller or a live test.

---

## Part 4 — Open questions for you before step 6 starts

1. **Generalist action set.** Start with `{snapshot, navigate, click,
   type, wait_for, done, fail}` (aligning with what `@playwright/mcp`
   exposes and what Playwright's ref-resolver supports natively), or
   broader (`hover, select, keyboard_shortcut, file_upload`)? I'd start
   narrow and grow on demand. **Your call?**

2. **Planner LLM.** Use the same `IChatClient` as
   `extract-structured-data` (Azure AI Foundry `gpt-5.3-chat`), or
   wire a separate one? Separate would let us route planner ≠
   extraction cost-optimally. I'd start same, split if cost forces it.

3. **Per-task budget default.** Propose: `maxSteps=30`,
   `maxSeconds=120`, caller can raise within bounds. **OK, or do you
   want these higher/lower?**

4. **Allowlist default.** Refuse navigation if `allowedHosts` is empty,
   or treat empty as "same-origin as `url`"? I lean refuse — forces
   callers to be explicit, cheap to construct.

5. **RockBot side.** Foragent's `invoke_agent` experience at step 5
   showed RockBot's tool only passes free-text. Does step 6's
   `browser-task` fit that shape naturally (intent *is* free text), so
   the problem dissolves? I think yes — worth confirming before
   building.

6. **Spec merge timing.** Merge this proposal into the main spec now
   (it becomes the v0.2 spec and the project operates under it), or
   keep it as a proposal until step 6 validates the core approach?
   I lean: merge §5 + §9 now, leave §5.6 / §5.7 as "proposed" until
   step 7 actually exercises them.
