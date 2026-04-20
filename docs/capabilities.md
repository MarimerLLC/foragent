# Capabilities

Foragent exposes browser operations as discrete A2A capabilities. Callers
invoke capabilities by name; Foragent handles the browser mechanics.

## Planned initial capability set

- [ ] `fetch-page-content` — Navigate to a URL and return the page content
- [ ] `extract-structured-data` — Extract structured data from a page using
  an LLM-assisted schema
- [ ] `fill-form` — Fill and optionally submit an HTML form
- [ ] `post-to-site` — Perform a multi-step posting action on a target site
- [ ] `monitor-page` — Poll a page for a condition and notify when met

## Design principles

- Capabilities operate at the task level, not at the DOM-operation level
- Each capability invocation gets an isolated browser context
- Credential references are passed by ID; values are resolved inside
  Foragent and never cross A2A boundaries
