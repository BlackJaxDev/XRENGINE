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
| Published MCP surface | Run `Tools/Setup-LocalAgentBroker.ps1`. | Five tools; server version 0.11.2; four exact Claude IDs and configurable Haiku swarm effort in the start schema. | Passed | 2026-10-11 |
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

## Master Sync And Saved Work — 2026-10-11

The checkout was fast-forwarded to `b89e427c48be7b9002765d3b0566e2292ea429d4`
before broker work resumed. The original saved work remains in stash commit
`f7ea33a3f8f8a6fdd51637c74194b17727ca22bf`. It was not applied as a whole or dropped.

- Restored the original staged history store/publisher changes, persistence
  regression tests, accepted recovery investigation, and documentation index
  entries. Merged the history guide edits into the updated guides. The index
  conflict kept both the upstream validation link and the saved investigation link.
- Restored the saved partial-result exception, cancellation, orchestrator, and
  provider stop-reason contracts. Integrated them into the upstream Messages
  transport. Native completion, budget errors, cancellation, usage, and visible
  failed-turn text now retain the expected MCP metadata.
- Kept the upstream four-Claude catalog, `BrokerModelClient`, provider/tool
  continuation, configuration, and catalog tests. These supersede the old
  Sonnet-only client/router, eight-model assertion, and restrictive prototype
  guide sections. Prototype custom workspace-variable aliases remain in the
  backup; current lookup uses `ANTHROPIC_WORKSPACE_ID`.
- Kept the upstream Luna/Haiku swarm model scope. Enabled Haiku's five approved
  effort levels and applied the full admission checks in the public runner.
  Luna/max behavior, immutable snapshots, parent review, node limits, and
  reservation budgets remain covered by regression checks.

Release build and publish had no compiler warnings or errors. The checkout's
workload-set resolver is unavailable; the BCL build used
`MSBuildEnableWorkloadResolver=false` without changing the SDK configuration.
Deployment `LocalAgentBroker-20261011172844571` exposes MCP version `0.11.2`.
Existing transports and the tray were not restarted.

The focused suite passed 103 tests. New checks cover complete terminal metadata,
partial failed-turn text/usage, accepted-stream failure without reissue, and a
full Haiku/medium hierarchy. The initial test compilation rejected an expression
tree pattern; the test predicate was corrected to ordinary null equality.
All five MCP tools were called in an owned smoke process. Invalid Haiku effort
was rejected before admission; the registry stayed empty. The coordinator then
admitted 20 live Haiku/medium writers. Admission is not completion evidence.

Disposable logs and the reconciliation manifest are under
`Build/_AgentValidation/20261011-172444-broker-master-reconcile/`.

## Commit Validation — 2026-10-20

The broker Release build passed with no compiler warnings or errors. The
normal build failed because the pinned workload version `10.0.401.1` is not
installed. The narrow build and tests passed with the process setting
`MSBuildEnableWorkloadResolver=false`. The SDK configuration was not changed.

A temporary .NET 10 harness compiled the current broker projects and linked
the existing `AgentOrchestration` fixtures. All 117 tests passed, with no
failures or skipped tests. The harness excluded `TypeSafeClientTests` and the
paid `LiveOpenAiAgentSmokeTests`; it did not build the editor dependency graph.
No test source was changed.

An isolated broker process passed the MCP initialize and tool-list checks.
It reported version `0.11.2`, all five tools, optional `system_instructions`,
and the configurable Haiku effort description. No provider request was made.
The existing broker deployment and running processes were not changed.

Disposable build logs, the TRX report, the harness, and the MCP schema are under
`Build/_AgentValidation/20261020-120000-broker-commits/`.

## Related Investigation

[History file replacement during live runs](../../investigations/ai/broker-history-file-replacement.md)

[Haiku swarm validation](../../investigations/ai/haiku-swarm-validation.md)
