# Sequence-skill detection pass

Goal: find repeated tool-call patterns across successful `browser-task`
runs and propose a named, reusable skill so future planners can
retrieve the pattern directly instead of rediscovering it.

## Inputs

The tool-call log for recent `browser-task` runs, grouped by primary
host. Each entry includes:
- The intent text.
- Ordered tool-call names (arguments omitted for privacy — you see
  `type(ref, …)` without the value).
- Final outcome (`done` / `fail` / `incomplete`).
- Step count and duration.

## What to look for

1. **Recurrent prefixes.** Multiple successful runs that start with the
   same 3+ tool calls (often "navigate to login URL, snapshot, click
   Sign-in"). That's a candidate login primer.

2. **Recurrent mid-sequences.** A 4–6 step pattern that appears inside
   runs with different overall intents — e.g. "click menu → click
   Settings → navigate Settings URL" appears in three different
   settings-related tasks. That's a candidate navigation primer.

3. **Recurrent error-recovery.** A pattern where the planner hits a
   specific state, recovers, and succeeds (e.g. "dismiss cookie banner
   → retry click"). Worth a primer so future runs skip the recovery
   phase.

## Threshold

Require **at least 3 distinct successful runs** exhibiting the pattern
before proposing a skill. Two matches is coincidence; three is a
pattern worth remembering.

## Output format

For each sequence-skill candidate, emit:

```
UPSERT sites/{host}/{slug} | {summary-15-words-or-less}
# {Human-readable title}

**When to use:** {one-sentence trigger — what a future planner's intent
text or current URL should look like}

**Steps:**
1. {tool-call + reason, e.g. "navigate to https://{host}/login"}
2. …

**Known pitfalls:** {brief; only if the evidence shows a recovery
pattern}

**See also:** {list of existing related skills}
---
```

Slug should be a short kebab-case name. Prefer verbs ("open-compose",
"dismiss-cookie-banner") over nouns.

## What not to do

- **Do not** propose a sequence skill where the only common element is
  "navigate to site root, then snapshot." That's not a pattern, that's
  the default shape of every task.
- **Do not** emit argument values — the log omits them for a reason.
- **Do not** emit sequence skills longer than ~8 steps. Long sequences
  are fragile against site changes and benefit the next planner less
  than a well-written shorter primer.
- **Do not** duplicate an existing skill. If `sites/{host}/login`
  already exists and your candidate sequence matches it, either emit
  nothing or emit an UPSERT that *improves* the existing one.
