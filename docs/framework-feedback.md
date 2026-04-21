# Framework feedback

Observations collected while building Foragent on the RockBot framework. The Foragent project
specification (§9.1) calls this out as a deliverable: "Each milestone produces framework
feedback. Capture it."

## Step 1 — Empty agent on RockBot framework

### Resolved

- **Gateway was not on NuGet.** `RockBot.A2A.Gateway` was marked `IsPackable=false` and its
  load-bearing types were `internal`, which meant a new agent had no supported path to an
  in-process HTTP A2A surface without running the Gateway as a separate container. Filed as
  [rockbot#279](https://github.com/MarimerLLC/rockbot/issues/279). **Resolved** in RockBot 0.8.4
  via commit `476f0bb` — Gateway is now a packable library exposing
  `AddA2AHttpGateway`, `AddA2AApiKeyAuthentication`, and `MapA2AHttpGateway` extension methods,
  with a thin `RockBot.A2A.Gateway.Host` executable for container-only deployments. Foragent
  consumes the NuGet directly.

### Open

- **`IChatClient` is mandatory even for non-LLM agents.** `AddRockBotChatClient` must be called
  or the host fails to resolve services. Foragent v1 does zero LLM work; it ships an
  `EchoChatClient` stub solely to satisfy the registration contract. The framework should let an
  agent opt out of the chat-client dependency (e.g. an `AddRockBotHost(..., skipChat: true)` or
  a null-object registration the framework installs when no real client is provided).
- **One `IAgentTaskHandler` per agent forces skill dispatch into user code.** Foragent has one
  capability today (`fetch-page-title`), but the design calls for five in v1. The handler now
  does `switch (request.Skill)` to route, which duplicates what `AgentSkill` registration
  already expresses. A framework-level "register a handler per skill" API would remove that
  duplication (and make per-skill DI scoping / cancellation / metrics cleaner).
- **`AgentCard` and `AgentSkill` are declared twice.** Once in `AddA2A(opts => opts.Card = …)`
  for bus-side discovery, and again in `Gateway:Skills` in `appsettings.json` for the HTTP
  agent-card endpoint. They must stay in sync by hand. The two surfaces should share one
  source of truth — either the gateway reads the `A2AOptions.Card` out of DI, or both paths
  bind from the same config section.
- **`agentcard.json` discovery wiring lives in Gateway, not `AddA2A`.** A pure bus-worker agent
  (no HTTP surface) can't advertise itself to an HTTP-native A2A client. Today the only way
  to get the `/.well-known/agent-card.json` endpoint is to also wire the gateway. This is fine
  for Foragent (which wants HTTP anyway), but worth flagging — separating "advertise a card"
  from "host a JSON-RPC bridge" would help bus-only deployments.

## Step 2 — Playwright integration

- **`PlaywrightBrowserHost` as `IHostedService` composes cleanly with `AddRockBotHost`.** No
  lifecycle conflict; `StartAsync` runs alongside RockBot's hosted services and `StopAsync`
  disposes Chromium before the message bus is closed. Nothing to change.
- **Playwright's runtime base image choice is worth calling out.** The spec (§3.4) directs
  agents to use `mcr.microsoft.com/playwright/dotnet`. We pinned `v1.50.0-noble` to match the
  `Microsoft.Playwright` NuGet version. Keeping those two version numbers in sync is manual
  today — would be a nice framework-level helper (e.g. a `RockBot.Browser` package that brings
  both the NuGet and a dockerfile snippet / base-image recommendation). Not blocking for v1.
- **Still no resolution on the single-handler-per-agent shape** (flagged in step 1). Growing
  beyond step 2 will make this more painful; the `switch (request.Skill)` in
  `ForagentTaskHandler` is already starting to accumulate per-skill setup.

## Step 3 — Second capability (extract-structured-data)

- **A2A metadata pass-through.** Filed as [rockbot#281](https://github.com/MarimerLLC/rockbot/issues/281),
  **resolved** in RockBot 0.8.5 (commit `08e86b9`). `AgentMessage.Metadata` and
  `AgentTaskRequest.Metadata` are now `IReadOnlyDictionary<string, string>?`, and the bridge
  maps request- and message-level metadata in both directions (plus non-text parts).
  Foragent's `CapabilityInput.Parse` now reads metadata first and falls back to the JSON /
  bare-URL / embedded-URL paths for back-compat with older callers.
- **Single-handler-per-agent resolved in-repo, not upstream.** We introduced `ICapability`
  inside Foragent and rewrote `ForagentTaskHandler` as a pure dispatcher that resolves
  `IEnumerable<ICapability>` from DI and routes on `SkillId`. It works but it's *Foragent's*
  dispatcher — every RockBot-based agent will reinvent this. Candidate for a
  `RockBot.A2A.Capability` (or similar) helper in the framework: a base class / extension
  method that registers per-skill handlers and auto-wires dispatch. Would also kill the
  duplicate agent-card bookkeeping (we now build `opts.Card.Skills` and `opts.Skills`
  from one static list).
- **`AgentCard` lives in two places on the wire.** `A2AOptions.Card.Skills` (bus-side
  discovery) and `GatewayOptions.Skills` (HTTP agent-card endpoint) are independent. Our
  Program.cs populates both from a single `ForagentCapabilities.Skills` array — a workaround,
  not a fix. The framework should treat one as authoritative and derive the other.
