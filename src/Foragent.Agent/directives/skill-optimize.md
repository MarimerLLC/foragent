# Skill consolidation pass

Goal: reduce the skill store to a smaller, clearer body of site
knowledge by merging duplicates and rewriting entries for clarity.

## What to look for

You will be given the current list of skills and their Summaries,
grouped by name prefix. Focus on:

1. **Exact duplicates** — multiple skills under `sites/{host}/learned/`
   that describe the same intent (e.g. `login-to-bsky-app`,
   `sign-in-bluesky`, `authenticate-bsky`). Merge into one.

2. **Primer + learned pairs** — an operator primer at `sites/{host}/{x}`
   alongside one or more learned skills at `sites/{host}/learned/{…}`
   describing the same flow. Improve the primer with whatever the
   learned skills discovered (updated selectors, new failure modes,
   faster paths). Delete the redundant learned entries.

3. **Stale or superseded content** — a skill that claims "click the
   button labelled X" when a later learned skill shows the label is now
   Y. Prefer the newer evidence and say so.

4. **Over-long skills** — anything past ~500 words that spends most of
   its content on one-off anecdote rather than reusable procedure.
   Rewrite for density.

## When to merge

Merge two skills when **all three** are true:
- They describe the same landing URL or same sequence of intents.
- Their successful flows overlap by more than half.
- The combined skill would still fit comfortably inside ~400 words.

Do not merge skills that happen to target the same site but different
intents (e.g. `sites/bsky.app/login` vs `sites/bsky.app/compose-post`).
Those stay separate — different retrieval contexts.

## Output format

For each change you want to make, emit one of:

- `DELETE {skill-name}` — remove a redundant skill.
- `UPSERT {skill-name} | {summary-15-words-or-less}`
  followed by a markdown body on subsequent lines up to `---`.

Example:

```
DELETE sites/bsky.app/learned/sign-in-with-app-password
DELETE sites/bsky.app/learned/log-into-bluesky
UPSERT sites/bsky.app/login | Log in to bsky.app with an app password; watch for 2FA challenges.
Bluesky's public web app is at https://bsky.app…
(full markdown body)
---
```

## What not to do

- **Do not** delete a skill whose name is in the protected-prefixes
  list. Improve its content in place via UPSERT instead.
- **Do not** merge across sites. `sites/foo.example/login` and
  `sites/bar.example/login` are different knowledge.
- **Do not** write speculative content. If the trace evidence does not
  mention a specific selector, don't invent one — leave it vague.
- **Do not** drop citations — if a learned skill references a URL or
  observed selector, preserve that detail in the merged output.
