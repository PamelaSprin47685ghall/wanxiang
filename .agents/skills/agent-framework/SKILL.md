---
name: agent-framework
description: Agent layer for the F#/Avalonia kelivo rewrite.
version: 0.1.0
author: Hermes
metadata:
  hermes:
    tags: [FSharp, Avalonia, AgentHosting, A2A, MultiAgent]
---

# Microsoft Agent Framework in the kelivo Rewrite

This skill orients work on the vendored `agent-framework/` repo (a clone of Microsoft Agent Framework, MAF) as the Agent layer for the kelivo rewrite mandated by the wanxiang SSOT (`skill://wanxiang`): F# + Avalonia, UI unchanged, multi-platform, and a peer C/S architecture where every instance is both client and server. It covers the .NET side only (F# consumes it directly); the Python packages in `python/` are out of scope, and Avalonia UI work is out of scope. The local repo under `agent-framework/` is the source of truth — code verbatim from its `dotnet/samples/` is preferred over docs.

## When to Use

- "Wire agent-framework into the F# project" / "which MAF package do we need"
- Creating or running an `AIAgent` (single-turn or multi-turn) from F#/C#
- Hosting an agent as a server so other instances can call it (C/S peer architecture)
- Making a client that calls another instance's server (or its own, loopback)
- Agent-to-agent delegation (A2A), tools, sessions, streaming
- "Which sample should I copy?" — anything MAF-related in this repo

## Prerequisites

- .NET SDK 10 (`net10.0` is the samples' target framework). Check with `dotnet --version`.
- The vendored repo at `/home/kunweiz/Desktop/vibe/wanxiang/agent-framework/`.
- Env vars used by samples (never hardcode secrets):
  - `FOUNDRY_PROJECT_ENDPOINT` + `FOUNDRY_MODEL` (default `gpt-5.4-mini`) for Foundry-backed agents; run `az login` first.
  - `OPENAI_API_KEY` / `OPENAI_CHAT_MODEL_NAME` for OpenAI-backed agents.
  - `ASPNETCORE_URLS` to override the hosting bind URL; `RESPONSES_SERVER_URL` (default `http://localhost:5000`) for the client side.
- The kelivo rewrite consumes MAF via NuGet (`dotnet add package Microsoft.Agents.AI ...`); samples use `<ProjectReference>` only because they live inside the MAF repo.

## How to Run

Invoke through the `terminal` tool. Run a sample project straight from the repo:

```bash
cd agent-framework/dotnet/samples/01-get-started/01_hello_agent && dotnet run
```

For the hosting pair (server then client, two terminals):

```bash
cd agent-framework/dotnet/samples/04-hosting/af-hosting/local_responses/Server && dotnet run
cd agent-framework/dotnet/samples/04-hosting/af-hosting/local_responses/Client && dotnet run
```

For the kelivo F# project, add the core package and build:

```bash
dotnet add package Microsoft.Agents.AI
dotnet build
```

## Quick Reference

Packages (names match `dotnet/src/` directories): `Microsoft.Agents.AI` (core), `.OpenAI`, `.Foundry`, `.Anthropic`, `.Hosting`, `.Hosting.OpenAI`, `.Hosting.AspNetCore`, `.Hosting.A2A.AspNetCore`, `.A2A`, `.Workflows`, `.AGUI`, `.Mcp`, `.Declarative`.

Core types (`Microsoft.Agents.AI` + `Microsoft.Extensions.AI`): `AIAgent` (abstract base), `AgentSession` (multi-turn state), `ChatClientAgent`, `IChatClient`, `AITool` / `AIFunction`, `AIFunctionFactory`, `AgentResponse` (has `.Text`), `AgentSessionStore` / `InMemoryAgentSessionStore`.

Key methods: `agent.RunAsync(msg)` / `RunAsync(msg, session)` -> `AgentResponse`; `RunStreamingAsync(...)` -> async enum of updates; `agent.CreateSessionAsync()` -> `AgentSession`; `chatClient.AsAIAgent(model:, instructions:, name:, tools:)`; `AIFunctionFactory.Create(method, name:)`.

Hosting helpers (`Microsoft.Agents.AI.Hosting.OpenAI`): `OpenAIResponses.ToAgentRunRequest`, `.GetSessionStoreId`, `.CreateResponseId`, `.WriteResponse`, `.WriteResponseStreamAsync`; batteries-included `MapOpenAIResponses` also exists.

A2A (`Microsoft.Agents.AI.A2A`): `A2ACardResolver(url, httpClient)`, `cardResolver.GetAIAgentAsync()`, `remoteAgent.AsAIFunction()` (expose a remote agent as a tool).

Env vars: `FOUNDRY_PROJECT_ENDPOINT`, `FOUNDRY_MODEL`, `OPENAI_API_KEY`, `OPENAI_CHAT_MODEL_NAME`, `ASPNETCORE_URLS`, `RESPONSES_SERVER_URL`.

Sample map (`agent-framework/dotnet/samples/`): `01-get-started/01_hello_agent` (minimal agent, `Program.cs`), `04-hosting/af-hosting/local_responses/{Server,Client}` (own-route hosting + consuming client — the C/S template), `05-end-to-end/A2AClientServer/{A2AServer,A2AClient}` (A2A peer-to-peer), `02-agents/AgentProviders/{openai,ollama,anthropic,...}` (providers), `02-agents/Agents/` (tools, middleware, structured output), `03-workflows/` (graph orchestration).

## Procedure

1. Locate the framework: everything is under `agent-framework/` — README.md (overview), `dotnet/src/` (packages), `dotnet/samples/` (copy-paste code), `docs/specs/` (design specs, e.g. 003-dotnet-hosting-protocol-helpers.md). Use `search_files`/`read_file` on samples before writing new code.
2. Pick packages for the F# layer. Minimum: `Microsoft.Agents.AI` plus one provider (`Microsoft.Agents.AI.OpenAI` or `.Foundry`); add `.Hosting` + `.Hosting.OpenAI` (or `.Hosting.A2A.AspNetCore`) when an instance exposes its agent as a server.
3. Create an agent. Verbatim from `01_hello_agent/Program.cs`:
   ```csharp
   AIAgent agent = new AIProjectClient(new Uri(endpoint), new DefaultAzureCredential())
       .AsAIAgent(model: model, instructions: "You are good at telling jokes.", name: "Joker");
   Console.WriteLine(await agent.RunAsync("Tell me a joke about a pirate."));
   ```
   OpenAI equivalent: `new OpenAIClient(new ApiKeyCredential(apiKey)).GetChatClient(modelId).AsAIAgent(instructions: ..., name: ..., tools: ...)` (see `A2AClient/HostClientAgent.cs`).
4. Multi-turn: create one `AgentSession` via `agent.CreateSessionAsync()` and pass it to every `RunAsync(msg, session)` call — the session threads conversation state (see `A2AClient/Program.cs` and the MAF path in `af-hosting/local_responses/Client/Program.cs`).
5. Add tools: `tools: [AIFunctionFactory.Create(LookupWeather, name: "lookup_weather")]` with a `[Description]`-annotated static method (verbatim from `af-hosting/local_responses/Server/Program.cs`).
6. Host the instance as a server (the "S" in C/S). Copy the pattern from `04-hosting/af-hosting/local_responses/Server/Program.cs`: `WebApplication.CreateBuilder`, an `AgentSessionStore` (e.g. `InMemoryAgentSessionStore`), then `app.MapPost("/responses", ...)` converting the body with `OpenAIResponses.ToAgentRunRequest(body)`, running the agent, persisting the session, and writing `OpenAIResponses.WriteResponse(...)` (or SSE via `WriteResponseStreamAsync` for `stream: true`). Run with `app.Run("http://localhost:5000")`.
7. Consume another instance's server (the "C"): either plain `IChatClient` (`responseClient.AsIChatClient(model)` from a `ResponsesClient` pointed at the server URL, threading `ChatOptions.ConversationId`), or higher-level `responseClient.AsAIAgent(...)` + `AgentSession` (see the two paths in `af-hosting/local_responses/Client/Program.cs`). For loopback self-command, point the client at your own instance's URL.
8. Peer-to-peer agent delegation (A2A): resolve remote agents with `new A2ACardResolver(url, httpClient).GetAIAgentAsync()`, cast each to `AITool` via `AsAIFunction()`, and hand them as `tools:` to your own orchestrator agent (verbatim `HostClientAgent.InitializeAgentAsync` in `05-end-to-end/A2AClientServer/A2AClient/HostClientAgent.cs`).
9. From F#: `open Microsoft.Agents.AI` and call the Task-returning APIs with `Async.AwaitTask` (or the `task {}` CE), e.g. `let! resp = agent.RunAsync(msg, session) |> Async.AwaitTask`. Keep the agent code in an F# library project referencing the MAF packages; the Avalonia UI project references that library, leaving the UI unchanged per the wanxiang SSOT (`skill://wanxiang`).

## Pitfalls

- `DefaultAzureCredential` is dev-only: samples warn it causes latency and credential probing; in production use a specific credential (e.g. `ManagedIdentityCredential`).
- The Foundry samples throw if `FOUNDRY_PROJECT_ENDPOINT` is unset — set env vars or use the OpenAI provider before running.
- Samples target `net10.0`; older SDKs will fail to build.
- The `af-hosting` server deliberately owns routing/auth/session storage; the sample's `Authorize` is a stub — a real deployment must authenticate callers and bind session ids to principals. Concurrent runs against one conversation id are NOT serialized; provide per-conversation single-writer coordination.
- Local A2A/Responses sample servers ignore the API key, but the OpenAI SDK still requires a credential — pass `new ApiKeyCredential("not-needed")`.
- Do not re-derive APIs from memory or MS Learn: read the vendored samples first; the local repo is the source of truth.
- Session id from the request is untrusted input — validate before using as a storage key (see `OpenAIResponses.GetSessionStoreId` handling in the Server sample).
- This skill covers the .NET/F# path only; `agent-framework/python/` is a parallel implementation, not a reference for this rewrite.

## Verification

Build the hosting server sample (no credentials needed):

```bash
cd agent-framework/dotnet/samples/04-hosting/af-hosting/local_responses/Server && dotnet build
```

With credentials set, `dotnet run` in `01-get-started/01_hello_agent` prints an agent reply; the full C/S round-trip is verified by starting Server then Client and getting answers across a 3-turn conversation.
