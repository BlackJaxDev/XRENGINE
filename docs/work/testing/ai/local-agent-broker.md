# Local Agent Broker Validation

Guide: [Local Agent Broker](../../../user-guide/ai/local-agent-broker.md)
Implementation: [Provider contracts](../../../developer-guides/ai/local-agent-broker.md)

## Setup

Use .NET 10 and `Tools/Setup-LocalAgentBroker.ps1`. The script publishes the
broker and tray, then checks the five-tool MCP surface. Restart an existing
Codex chat or app to load the new deployment and tool schema.

Claude runs need `ANTHROPIC_API_KEY`. Multi-workspace keys also need
`ANTHROPIC_WORKSPACE_ID`. Both values can come from the Windows user environment.
Do not include key values in evidence or commands.

## Checks

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Build | Build `Tools/LocalAgentBroker/LocalAgentBroker.csproj` with `-m:1`. | No compiler warnings or errors. | Passed | 2026-10-09 |
| Published MCP surface | Run `Tools/Setup-LocalAgentBroker.ps1`. | Five tools; server version 0.11.1; four exact Claude IDs and Haiku swarm support in the start schema. | Passed | 2026-10-09 |
| Claude text | Run each Claude ID at low effort with one turn, no tools, no retries, 1,024 output tokens, and 90 seconds. Ask for a fixed short reply. | Completed status, expected reply, and exact requested/actual model match. | Passed for all four IDs | 2026-10-09 |
| Workspace selection | Use a multi-workspace key with `ANTHROPIC_WORKSPACE_ID`. | Anthropic accepts the workspace header. | Passed | 2026-10-09 |
| Missing workspace | Use a multi-workspace key without a workspace ID. | Explicit provider error; no provider fallback. | Passed; API returned HTTP 400 with setup instructions | 2026-10-09 |
| Tool continuation | In an isolated workspace, provide the approved `AgentModelCatalog.cs` file. Run Haiku with required tool use, a context excerpt, one repository read, and two turns. | Correct tool result association, accepted thinking continuation, and final model list. | Passed; one tool call and two provider turns | 2026-10-09 |
| Haiku swarm | Use synthetic `Sample.cs`, Haiku at max effort, one leaf, and proposal-only mode. Allow 8,192 tokens per step and 24,576 reserved tokens. | Planning, leaf proposal, and parent review complete; every node and the aggregate report Haiku; source stays unchanged. | Passed; one approved replacement and three provider calls | 2026-10-09 |
| Swarm token exhaustion | Run the synthetic Haiku swarm with 4,096 tokens per step. | A truncated leaf fails the swarm without returning or applying a proposal. | Passed; explicit `BudgetExceeded` | 2026-10-09 |
| Unsupported controls | Submit Claude `none` effort, background mode, Sonnet required tool use, and an unapproved model alias. | Rejection before provider execution. | Passed | 2026-10-09 |
| Editor image input | Use a named editor session and a capture tool result. | Claude receives the image and reports visible evidence. | Not run | None |
| Provider limits | Run high-effort, long-output requests at each model limit. | Correct usage and explicit budget failures. | Not run | None |

The live tool check sent only the source file that the user explicitly approved.
It used an isolated workspace to limit the available repository content.

## Regression Checks

Run the existing `AgentOrchestration` test group. A temporary .NET 10 harness
can link those same tests and broker sources to avoid the editor dependency
graph. Windows history and workspace checks need ordinary filesystem access;
sandbox access errors do not establish a provider failure.

On 2026-10-09, the narrow harness passed 111 tests after the user approved test
changes. The checks cover both provider clients, the Claude catalog, header
and workspace selection, signed tool replay, parser integrity, cancellation,
model mismatch, stop-reason handling, and both supported swarm models. NuGet reported advisory-feed access
warnings; the tests and compiler reported no failures.

## Related Investigation

[History file replacement during live runs](../../investigations/ai/broker-history-file-replacement.md)

[Haiku swarm validation](../../investigations/ai/haiku-swarm-validation.md)
