# Foragent

> ⚠️ **Early development — APIs will change without notice.**

**Foragent** is an A2A-native browser agent for .NET. It exposes browser
automation capabilities (navigate, extract, fill forms, post to sites,
monitor pages) over the [Agent2Agent (A2A) protocol](https://google.github.io/A2A/). Other agents
delegate browser work to Foragent rather than reasoning about DOM selectors
or session management themselves.

Foragent is built on the [RockBot framework](https://github.com/MarimerLLC/rockbot)
and uses [Microsoft.Playwright](https://playwright.dev/dotnet/) for browser
automation.

## Build status

![CI](https://github.com/MarimerLLC/foragent/actions/workflows/ci.yml/badge.svg)

## What Foragent does

- Accepts browser tasks from other agents via A2A
- Executes tasks using a real browser (Playwright) with LLM-assisted selector
  resolution (Microsoft.Extensions.AI)
- Returns structured results over A2A
- Manages browser contexts per task; keeps credentials inside the process

## What Foragent is not

- Not a Playwright MCP server (Microsoft already ships one)
- Not a test automation framework
- Not a general browser orchestration platform

## Getting started

> ⚠️ The agent is not yet functional. See `docs/architecture.md` for the
> planned design and `docs/capabilities.md` for planned capabilities.

## License

MIT — see [LICENSE](LICENSE).
