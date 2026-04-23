# Memory-mining pass

Goal: promote durable observations from recent `browser-task` runs
into `ILongTermMemory` so they prime future planning without growing
the skill store.

## What belongs in memory (not in skills)

Skills are **procedural** — "how to do X on site Y." Memory is
**declarative** — facts and observations that don't fit the
how-to-do-X shape. Examples of good memory entries:

- "bsky.app enforces an email-code challenge roughly 1-in-10 logins
  from fresh contexts" — site behaviour, not a procedure.
- "example.com served a 503 maintenance page on 2026-04-21; retries
  after 2026-04-22 succeeded" — time-bounded incident.
- "the bsky compose editor rejects `FillAsync` on its ProseMirror
  root; only keystroke-based typing works" — tooling quirk worth
  remembering across capabilities.
- "Cloudflare challenge pages show a checkbox labelled 'Verify you are
  human'; no successful automated bypass has been observed" — a
  concrete negative finding.

## What does NOT belong in memory

- Credential values, tokens, typed field contents.
- Specific user data (post text, usernames, message bodies).
- One-off successful runs that are already captured by a learned
  skill.
- Generic observations ("sites take time to load") — too vague to
  retrieve usefully.

## Inputs

Recent tool-call logs and browser-task results, plus the existing
memory entries (so you don't duplicate).

## What to look for

1. **Site-level behaviours** that appear across multiple runs (captcha
   prompts, rate-limit responses, maintenance windows, DOM changes).
2. **Tooling quirks** — situations where a capability's tool call
   behaved unexpectedly in a specific way that would save future runs
   time to know about.
3. **Negative findings** — things that were tried and *didn't* work,
   saving a future planner from repeating the attempt.

## Output format

For each memory worth recording:

```
MEMORY {category} | [tags]
{One-paragraph observation. Lead with the specific, observable fact.
Keep under ~80 words. If it has a date boundary, include it
explicitly.}
```

Category should be `sites/{host}` when the observation is site-specific,
or a general category like `browser-tooling`, `captcha-patterns`,
`rate-limit-patterns` otherwise.

Tags are a free-form subset of: `site-behaviour`, `tooling-quirk`,
`negative-finding`, `incident`, `rate-limit`, `captcha`, `dom-change`.
Keep to 1-3 tags per entry.

## What not to do

- **Do not** emit memory entries for already-captured facts. Skim the
  existing memory first.
- **Do not** write essays. A memory entry should be a single tight
  paragraph a future planner can retrieve and integrate quickly.
- **Do not** include credential values, typed content, or user data.
- **Do not** create memory entries for facts that would be better as a
  skill — if it answers "how do I do X," it's a skill, not a memory.
