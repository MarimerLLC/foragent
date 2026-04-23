# Skill-gap detection pass

Goal: identify tasks that failed or struggled because Foragent was
missing a relevant site skill, and record the gap so future operator
priming or future dream passes can close it.

## Inputs

You will be given the recent `browser-task` traces where the outcome
was either:

- `failed` (planner called `fail()`), or
- `incomplete` (budget exhausted before `done()` or `fail()`), or
- `done` but with an unusually high step count relative to peers for
  the same host.

Alongside each trace, you will see the skills (if any) that were
injected into the planner prompt as priming.

## What to look for

1. **Failures where no primer existed for the host.** If the task
   targeted `sites/foo.example/*` and the skill store contains nothing
   under `sites/foo.example/`, that's the clearest kind of gap.

2. **Failures where the primer content did not cover the intent.** If
   the task intent was "compose a post" but the only retrieved skill
   was `sites/{host}/login`, the gap is a missing compose primer.

3. **Recurring pain points within a single host.** Three failures on
   bsky.app's 2FA email-code prompt in a week is worth a specific
   entry, even if one successful run exists.

4. **Tool thrash.** A trace with 30+ `snapshot` calls and no `click`
   that succeeded usually means the planner didn't know which element
   to target — a gap in selector-level guidance.

## Output format

For each gap identified, emit a memory entry:

```
MEMORY sites/{primary-host} | [tags]
Missing primer / selector coverage for {intent summary}. Evidence: {N
failed traces over {period}, most recent {date}}. Suggested content:
{one-paragraph hint on what a future primer should cover}.
```

Tags should be a subset of: `gap`, `failure-cluster`,
`missing-primer`, `selector-ambiguous`, `2fa-blocked`,
`budget-exhausted`.

Multiple gaps per host are fine — keep them separate entries so
retrieval surfaces the specific flavour of gap that matches a future
query.

## What not to do

- **Do not** write a gap entry for a single failed task. Require at
  least two failures or one failure plus one struggle (high step count)
  before flagging.
- **Do not** invent selectors or URLs. The gap entry describes the
  *shape* of the missing knowledge, not the content of it.
- **Do not** include the failing intent verbatim if it contains
  personal content, usernames, or data — describe the *pattern*.
- **Do not** blame the planner for site changes. If the evidence shows
  the site itself changed (new DOM, new domain), record that as a site
  event, not a skill gap.
