# Capabilities

Foragent exposes browser operations as discrete A2A capabilities. Callers
invoke capabilities by name; Foragent handles the browser mechanics.

## Advertised capabilities (v0.2)

- `browser-task` — **generalist**, spec §5.2. LLM-in-the-loop planner that
  drives a real browser to accomplish a free-form intent. Shipped in
  step 6; step 7 added skills + memory priming (spec §5.6).
- `fetch-page-title` — specialist. Inherited from step 1/2.
- `extract-structured-data` — specialist. Inherited from step 3.

The step-4 `post-to-site` capability was removed in step 7 — the
generalist `browser-task` plus the seeded `sites/bsky.app/login` skill
subsume its function, and the project is still pre-public so no consumer
needed a deprecation path.

## `browser-task` input shape

JSON in the first text part, or field-by-field metadata:

```json
{
  "intent": "free-form description of what to accomplish",
  "allowedHosts": ["bsky.app", "*.example.com", "*"],
  "url": "optional absolute http(s) starting URL",
  "credentialId": "optional broker reference",
  "maxSteps": 60,
  "maxSeconds": 120
}
```

- `intent` — required. Free-form.
- `allowedHosts` — required, non-empty (spec §7.1). An empty list rejects.
  Supports exact hosts, `*.domain` subdomain wildcards, and `*` for
  unrestricted. Off-list navigations are aborted inside the browser
  context before Playwright issues the request.
- `url` — optional. If provided, must match the allowlist.
- `credentialId` — optional. Resolved but not exposed to the planner
  yet; reserved for a typed login tool in a later step.
- `maxSteps` — default 60, ceiling 150. Enforced tool-side via
  `BrowserTaskState.BudgetExhausted`; once exceeded, tools return a
  "call done or fail" message and refuse further work.
- `maxSeconds` — default 120, ceiling 600. Enforced via a linked
  `CancellationTokenSource`.

## `browser-task` output shape

A JSON object in a single text part:

```json
{
  "status": "done" | "failed" | "incomplete",
  "summary": "one-sentence human-readable result",
  "result": "optional structured result text (e.g. extracted value)",
  "steps": 7,
  "navigations": ["https://host/path", "..."]
}
```

`incomplete` means the budget was exhausted before `done`/`fail` was
called.

## `browser-task` tool surface

Exposed to the planner via `[AIFunction]` wrappers over `IChatClient`
(spec Appendix A #16 — no MCP sidecar). Refs are Playwright aria-ref ids
and are valid only within the snapshot they came from.

- `snapshot()` — ref-annotated aria tree of the current page.
- `navigate(url)` — load a URL; host must be on the allowlist.
- `click(ref)` — click by ref.
- `type(ref, text)` — fill by ref.
- `wait_for(ref, timeoutSeconds?)` — wait for visibility.
- `done(summary, result?)` — mark complete.
- `fail(reason)` — mark failed.

## Design principles

- Capabilities operate at the task level, not at the DOM-operation level.
- Each capability invocation gets an isolated `BrowserContext` (spec §3.5).
- Per-task host allowlists are mandatory (spec §7.1).
- Credential references are passed by ID; values are resolved inside
  Foragent and never cross A2A boundaries (spec §6.1).
- Prohibited capabilities — account creation, financial transactions,
  modifying security permissions — are out of scope regardless of
  implementation ease (spec §7.3).
