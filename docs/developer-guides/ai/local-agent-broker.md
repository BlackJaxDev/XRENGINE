# Local Agent Broker

The local agent broker is a BCL-only .NET 10 orchestration surface shared with
the ImGui MCP Assistant. It starts explicit public OpenAI Responses API calls
and proxies model function calls to one named, loopback editor MCP session.

It is not an in-place Codex model switch, a generic subprocess runner, or an
Internet-facing editor bridge.

## Project Boundaries

| Project | Owns | Must not own |
|---|---|---|
| `XREngine.AgentOrchestration` | Provider-neutral run contracts, prompt packet, budgets, bounded tool loop, Responses transport/SSE parsing, HTTP MCP client | ImGui state, editor globals, process/session lifecycle |
| `Tools/LocalAgentBroker` | Stdio MCP host, exact model catalog/routing advice, run registry, named-session resolution, leases, trace policy | Editor implementation, shell/Git execution, API-key persistence |
| `XREngine.Editor` | ImGui messages/segments, preferences, local tools, viewport presentation, in-process MCP startup | A second OpenAI function loop |

`XREngine.Editor` references the shared orchestration project. The broker
references only that project. No NuGet package was added for this feature.

## Run Contract

`AgentRunRequest` carries the objective, success criteria, constraints, exact
requested model, reasoning effort, compact evidence packet, named editor
session, tool policy, budget, and the explicit `UseBackgroundMode` transport
choice. Its evidence packet has these stable fields:

- relevant files and symbols;
- current-diff summary;
- commands and observed results;
- failed hypotheses;
- unresolved questions; and
- next decision.

`AgentRunResult` reports the run ID/status, requested and actual models, final
text and multimodal output items, tool evidence, token usage, turn/tool counts,
elapsed time, retry count, bounded provider-attempt diagnostics, and a
structured `AgentFailure`.

The observer stream reports status, text delta, tool-started/tool-completed,
usage, retry, and diagnostic events. No observer references UI types.

## Responses Protocol

`OpenAiResponsesModelClient` calls only
`https://api.openai.com/v1/responses`, reads its bearer key through an
in-memory delegate, and sends `store: false`. The default path streams SSE.
The explicit background path sends `background: true`, creates without
streaming, polls `GET /v1/responses/{id}` while queued/in-progress, and calls
`POST /v1/responses/{id}/cancel` on cooperative cancellation. Payloads preserve
the caller's exact model and output-token limit.

`OpenAiResponsesStreamParser` handles:

- text deltas and completed text;
- interleaved/multiple function calls and argument deltas;
- stable `call_id` correlation;
- usage, response ID, and actual model;
- generated-image output;
- provider failure/error events;
- `response.incomplete`, including `incomplete_details.reason`; and
- continuation output items, including encrypted reasoning content.

An incomplete response caused by `max_output_tokens` is classified as
`BudgetExceeded`; other incomplete states remain provider errors. The minimum
accepted run output budget is 16 tokens, matching the live Responses API
contract. A JSON response containing `"error": null` is not an error.

For a continuation, the next input is the previous `response.output` plus one
`function_call_output` for each completed call. The orchestrator rejects empty
or duplicate call IDs. `max_turns`, `max_tool_calls`,
`max_tool_result_bytes`, `max_output_tokens`, elapsed time, and retry count are
run-wide request budgets; request text is also capped at 262,144 characters.
Only retryable provider transport/rate-limit failures use exponential backoff
with jitter; local mutations are never retried. Each attempt records only safe
metadata: turn/attempt, background flag, outcome, response ID, actual model,
event/poll and malformed-event counts, last event/sequence, terminal status,
incomplete reason, elapsed time, failure/status, retry disposition, and
provider-cancellation acceptance. Prompts, response bodies, headers, and
credentials are excluded. Background poll transport failures resume polling
the same response instead of creating a duplicate response.

## Stdio MCP Surface

`McpStdioServer` uses one JSON-RPC object per line and writes only protocol
responses to stdout. Diagnostics go to stderr. It supports `initialize`,
`ping`, `tools/list`, and `tools/call`; notifications do not receive responses.
The fixed broker tool catalog is:

- `recommend_agent_route`
- `start_agent_run`
- `get_agent_run`
- `cancel_agent_run`
- `list_agent_runs`

`start_agent_run` validates the request synchronously and returns a run ID
before the API work begins. The bounded registry owns the background task,
cooperative cancellation, global concurrency, time retention, and terminal
snapshot. `get_agent_run` is the authoritative incremental/terminal view.
The snapshot and nested terminal result both retain provider attempts, retry
count, and the earliest observed actual model, including cancellation paths.

The supported exact model IDs are `gpt-5.6-luna`, `gpt-5.6-terra`, and
`gpt-5.6-sol`. Route advice implements the repository policy but has no launch
side effect. A provider-reported model may include a dated snapshot suffix of
the exact requested model; any other model is classified as substitution and
fails the run. Re-check the
[current GPT-5.6 guidance](https://developers.openai.com/api/docs/guides/latest-model)
before distribution.

## Editor Tool Security

`EditorSessionResolver` validates the session-name grammar, combines it only
under `Build/_AgentValidation/mcp-sessions`, checks full-path containment,
reads `session.json`, requires a loopback HTTP(S) URI, and verifies the
manifest name. `HttpMcpToolProvider.PreflightAsync` then calls MCP `ping` and
requires the exact editor session name.

Tool permission is intersection-based:

1. the editor session permission policy must allow the operation;
2. the broker deny list must not contain it;
3. a non-empty broker allowlist must contain it;
4. read-only is the broker default; and
5. mutation requires `allow_mutation: true` plus a non-empty allowlist.

The provider consumes MCP tool annotations. Missing annotations are classified
conservatively using the tool name; unknown tools are treated as mutating.
Destructive authorization additionally requires mutation authorization.
MCP JSON-RPC and `isError` results remain errors.

The per-session fair reader/writer lease permits overlapping read-only runs.
A mutating run is exclusive and serializes behind earlier requests. After a
successful mutation, the orchestrator requires a later successful read-back,
inspection, query, validation, or capture tool. Image data may be carried as a
data URI or a local evidence path; text content is byte-bounded and marked
when truncated.

## Threat Model

| Threat | Control |
|---|---|
| API-key exposure | Key name may be configured. The value is read from process scope first and, on Windows only when absent, from user scope. It is never accepted in MCP arguments, persisted, echoed, or traced. Errors exclude request headers. |
| Prompt/tool injection | Workers receive an explicit system safety contract. Broker-side tool policy remains authoritative regardless of model instructions or hostile tool descriptions/results. |
| Path traversal | Session names use a strict grammar and the resolved manifest must remain under the repository's session root. |
| Arbitrary endpoint | Only the named session manifest is accepted; endpoints must be loopback and exact identity is preflighted. |
| Duplicate/ambiguous mutation | Duplicate call IDs fail. Mutating tool calls are never transport-retried. Same-session mutations hold an exclusive lease. |
| Unverified mutation | A later successful read-back or capture is mandatory by default. |
| Cost exhaustion | Exact model authorization, per-run turn/tool/token/time/retry budgets, bounded concurrency/retention, cancellation, usage reporting, and external API project limits. |
| Orphaned run | The registry links every run to explicit cancellation and broker shutdown. The caller polls to terminal state and cancels abandoned work. |
| Secret/content leakage through traces | Tracing is off by default and metadata-only when enabled; prompts and tool payloads are excluded. |
| Background-response retention | Background mode is opt-in and disclosed in the MCP schema/user guide. It uses temporary provider storage for polling and is not ZDR compatible even with `store: false`. |
| Editor process damage | The broker never starts/stops/finds processes. The named session manager owns lifecycle and PID validation. |

## Configuration

The checked-in `.codex/config.toml` resolves
`Tools/Invoke-LocalAgentBroker.ps1` by walking upward from the current
directory. The launcher resolves the repository root from its own location,
reads `Build/AgentTools/LocalAgentBroker.current`, and executes that immutable
versioned deployment without writing a build banner to the stdio protocol.
`Setup-LocalAgentBroker.ps1` publishes a fresh version before atomically moving
the pointer, so a loaded MCP process never blocks the update. A legacy fixed
directory remains a fallback only when no pointer exists.
The pointer affects only future launches. Existing Codex tasks retain their
current stdio process and pipes; stopping that process closes the transport and
requires a task/app restart rather than hot-rebinding the new deployment.

`BrokerConfiguration` accepts only repository root and environment-variable
names on the command line. API-key lookup uses process scope first and a
Windows user-scope fallback without copying the value into arguments or durable
state. Process-level bounds come from:

- `XRE_LOCAL_AGENT_BROKER_API_KEY_ENV`
- `XRE_LOCAL_AGENT_BROKER_EDITOR_AUTH_ENV`
- `XRE_LOCAL_AGENT_BROKER_MAX_RUNS`
- `XRE_LOCAL_AGENT_BROKER_MAX_CONCURRENCY`
- `XRE_LOCAL_AGENT_BROKER_RETENTION_MINUTES`
- `XRE_LOCAL_AGENT_BROKER_TRACE`

Optional editor bearer authentication is read from the environment variable
named by `XRE_LOCAL_AGENT_BROKER_EDITOR_AUTH_ENV`. The normal named-session
workflow uses loopback and no bearer token.

## Tests And Validation

Tests under `XREngine.UnitTests/AgentOrchestration` use scripted model clients
and fake HTTP handlers; ordinary tests never contact OpenAI or a live editor.
They cover streaming reconstruction, malformed/provider events, `store: false`
continuation replay, exact model selection and substitution, mutation
read-back, duplicate call IDs, cancellation, MCP error preservation, session
identity, route advice, and reader/writer leases.

Run the deterministic suite:

```powershell
dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj `
  --filter "FullyQualifiedName~AgentOrchestration" `
  -p:XREngineUseExistingNativeBridges=true
```

Publish and validate that stdio has no banners, resolves the current versioned
deployment, and advertises all five tools:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Setup-LocalAgentBroker.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Test-LocalAgentBrokerMcp.ps1
```

The opt-in live API test requires both `OPENAI_API_KEY` and
`XRE_RUN_LIVE_AGENT_BROKER_TESTS=1`, has no editor tools, and uses strict
turn/token/time limits. It is explicit and excluded from ordinary CI. Before a
release, re-check OpenAI model availability, service terms, and usage policies;
do not encode volatile pricing in this repository.

For release validation, also exercise a user-scoped key with the child process
variable removed, one successful `use_background_mode` tool turn, a deliberately
bounded `response.incomplete`, and cancellation after the provider response ID
appears. Verify exact requested/actual model identity and matching attempt
metadata in both the broker snapshot and nested terminal result.

### Direct-versus-broker comparison

The deterministic comparison deliberately uses the same scripted provider and
tool results so transport variability does not masquerade as quality:

| Property | Direct in-editor | Local broker |
|---|---|---|
| Result parsing/quality | Shared parser and tool loop | Same |
| Provider token use | Same prompt/tools/budget produces the same reported usage | Same |
| Tool-call count/evidence | Shared call-ID and evidence contracts | Same, plus broker policy filtering |
| Latency | Provider plus tool latency | Adds enqueue, named-session preflight, lease, and caller polling |
| Failure clarity | Structured provider/tool failure shown in ImGui | Same category plus queued/running/terminal status and requested/actual model |

No fixed latency claim is recorded because editor state, model service load, and
hardware dominate it. A release comparison should use the opt-in live lane and
record its dated evidence outside ordinary CI.

See the [Local Agent Broker user guide](../../user-guide/ai/local-agent-broker.md)
for workstation setup and operational use.
