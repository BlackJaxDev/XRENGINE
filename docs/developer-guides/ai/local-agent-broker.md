# Local Agent Broker

The local agent broker is a BCL-only .NET 10 orchestration surface shared with
the ImGui MCP Assistant. It starts explicit public OpenAI Responses API calls
as tool-free reasoning runs, repository-context/read runs, editor-aware runs
that proxy model function calls to one named loopback editor MCP session, or a
composition of the read-only repository and editor tool providers.

It is not an in-place Codex model switch, a generic subprocess runner, or an
Internet-facing editor bridge.

## Project Boundaries

| Project | Owns | Must not own |
|---|---|---|
| `XREngine.AgentOrchestration` | Provider-neutral run contracts, prompt packet, budgets, bounded tool loop, Responses transport/SSE parsing, HTTP MCP client | ImGui state, editor globals, process/session lifecycle |
| `Tools/LocalAgentBroker` | Stdio MCP host, exact model catalog/routing advice, run registry, repository snapshot/path/read policy, named-session resolution, provider composition, leases, trace policy, durable history publishing | Editor implementation, shell/Git execution, repository mutation, API-key persistence |
| `Tools/LocalAgentBroker.Shared` | Tray/history contracts, checkout-local paths, atomic record and settings storage | MCP transport, API calls, Windows UI |
| `Tools/LocalAgentBroker.Tray` | Windows notifications and notification icon, running-task menu, live prompt/response viewer, idle exit, history cleanup | API keys, provider calls, broker process ownership |
| `XREngine.Editor` | ImGui messages/segments, preferences, local tools, viewport presentation, in-process MCP startup | A second OpenAI function loop |

`XREngine.Editor` references the shared orchestration project. The broker
and tray companion share only provider-neutral contracts and the file-backed
history project. No NuGet package was added for this feature.

The broker coalesces high-frequency observer deltas into atomic history-record
writes. The tray watches those record replacements and incrementally replaces
only the changing line of its BCL-only Markdown preview; its slower periodic
refresh remains a fallback for dropped filesystem notifications. Rich-text
updates run with redraw suspended, then preserve the reader's viewport or ease
toward the current scroll maximum when tail-following is active. Newly visible
text fades from the surface color to its themed Markdown color. Theme selection
is stored in the shared UI settings as system, light, or dark; system mode
resolves the Windows app theme.

## Run Contract

`AgentRunRequest` carries the objective, success criteria, constraints, exact
requested model, reasoning effort, compact evidence packet, context-file
requests, repository-access policy, optional named editor session, editor tool
policy, budget, and the explicit `UseBackgroundMode` transport choice. With
both repository access and the editor session absent, the run uses
`EmptyAgentToolProvider`; validation rejects mutation, editor tool-policy
entries, or required tool use in that mode. Its evidence packet has these
stable fields:

- relevant files and symbols;
- current-diff summary;
- commands and observed results;
- failed hypotheses;
- unresolved questions; and
- next decision.

`context_files` is a path-only admission contract. Before a run is queued,
`RepositoryContextSnapshotter` resolves every entry through the shared
`RepositoryPathPolicy`, reads the complete bounded raw file once, validates
strict UTF-8, hashes the original bytes, selects an optional line range, and
attaches immutable `AgentContextFileSnapshot` records to the internal request.
Admission is all-or-nothing. Snapshot content is excluded from JSON status and
history serialization.

`repository_access` is independent from editor `AgentToolPolicy`. When enabled,
it requires at least one explicit repository-relative allowed root and a
positive tool-call budget. `RepositoryAgentToolProvider` exposes only
`repository_search` and `repository_read_text`. `CompositeAgentToolProvider`
unions repository and editor tools, rejects name collisions, and dispatches
each exact name back to its owning provider without merging authorization.

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

On the first turn, each context snapshot is encoded as a separate JSON-valued
`input_text` block after the broker-generated objective block. Metadata includes
the repository-relative path, selected line range, full raw-file size and
SHA-256, plus an explicit untrusted-data marker. File content cannot contribute
message roles, tool names, or tool definitions. The broker does not use
Responses `input_file` uploads, so no separate uploaded-file lifecycle is
introduced. Continuation requests retain the existing `store: false` replay.

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
`BudgetExceeded`; other incomplete states remain provider errors. A run output
budget of `0` disables the broker cap and omits `max_output_tokens` from the
provider request. Positive limits start at 16 tokens, matching the live
Responses API contract. A JSON response containing `"error": null` is not an
error.

For a continuation, the next input is the previous `response.output` plus one
`function_call_output` for each completed call. The orchestrator rejects empty
or duplicate call IDs. `max_turns`, `max_tool_calls`,
`max_tool_result_bytes`, context-file count/raw/rendered-byte caps,
and retry count are run-wide request budgets. Positive `max_output_tokens` and
elapsed-time values are optional run-wide limits; zero disables the
corresponding broker limit. The
request text is also capped at 262,144 characters. Repository tools additionally
enforce per-call read/search/result bounds and cumulative run-wide search and
output budgets. Broker-created repository and editor providers have no local
per-call timeout; caller cancellation and an optional positive whole-run limit
remain cooperative cancellation paths.
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

Broker server version `0.4.0` adds live status metadata to both
`get_agent_run` and `list_agent_runs`:

- `ObservedUtc` is sampled while the snapshot is produced, so it advances on
  every poll independently of provider output.
- `ElapsedMilliseconds` is the non-negative wall-clock interval from
  `CreatedUtc` to `ObservedUtc`; it is a diagnostic duration, not provider CPU
  time or a budget override.
- `ProgressMessage` is the latest informational stage. It is not durable
  evidence, a completion percentage, or a substitute for terminal
  `AgentRunStatus`.

`UpdatedUtc` remains the last retained-state mutation. This distinction lets a
client identify a quiet but actively observable stream without treating a poll
as a provider-progress event. The terminal result remains authoritative once
the status becomes `Completed`, `Failed`, or `Cancelled`.

For streamed Responses calls, the model client emits
`provider_stream_connected` after obtaining the SSE stream. It then emits a
bounded in-progress provider-attempt diagnostic after the first parsed provider
event and every 32 events thereafter. The broker projects the latest provider
event type into `ProgressMessage`, including event-only periods with no text
delta. Background responses continue to expose their normal polling
diagnostics; neither path reports speculative percentage completion.

Broker server version `0.5.0` retains requested Responses controls in both
`get_agent_run` and `list_agent_runs`: `RequestedReasoningEffort`,
`RequestedTextVerbosity`, and `MaxOutputTokens`. `text_verbosity` accepts
`low`, `medium`, or `high` and defaults to `medium`. The broker serializes it
as `text: { verbosity: ... }`; reasoning effort remains separately serialized
as `reasoning: { effort: ... }` for the exact selected GPT-5.6 worker model.
Positive `max_output_tokens` values remain hard combined visible-output and
reasoning-token limits and are never raised automatically. Zero or omission
disables the broker limit and omits `max_output_tokens` from the provider
request. A provider `max_output_tokens` incomplete response can still occur at
the selected model/provider maximum.

Broker server version `0.6.1` makes both omitted failure-prone budgets
route-aware. Luna and Terra retain 4,096 tokens and 120 seconds. Sol receives
16,384 tokens and 300 seconds, or 32,768 tokens and 600 seconds at
`xhigh`/`max`, so its internal reasoning does not routinely exhaust either
generic budget before returning evidence. The server resolves each value only
when its corresponding budget property is absent; every explicit limit remains
a hard authorization boundary and is never increased or retried automatically.

Broker server version `0.7.0` adds the Windows tray companion. Once the first
run is accepted, the broker writes its prompt record and starts the published
tray executable if that checkout does not already own a tray instance. Multiple
stdio broker processes share the same checkout-local record directory and a
named mutex prevents duplicate tray processes. The tray discovers updates by
polling atomic JSON snapshots; it never connects to the API or broker stdio
transport, so closing or restarting it cannot interrupt a worker.

History snapshots live under
`Build/_AgentValidation/00000000-000000-shared/local-agent-broker-ui/runs/`.
They include the provider prompt, optional system instructions, incremental or
terminal response text, concise run metadata, usage, and failures. Initial
inline image data, raw context snapshots, repository/editor tool
arguments/results, API keys, headers, and raw provider payloads are excluded.
Writes are coalesced during streaming and flushed immediately at terminal state.
The tray's `settings.json` supports a
nullable idle-exit duration and a nullable terminal-record retention duration;
null means never. It also stores whether new-prompt Windows notifications are
enabled; they default to enabled. Cleanup never removes queued or running records.

Broker server version `0.8.0` adds atomic `context_files` snapshots and opt-in
read-only repository tools. Start/get/list metadata reports context-file count,
aggregate raw bytes, and whether repository access is enabled without exposing
content. The request advertises separate raw and rendered context budgets.
Repository and editor providers remain independently authorized and are joined
only by collision-safe exact-name dispatch.

Broker server version `0.9.0` removes the broker's failure-prone default
output-token and elapsed-time caps. Both controls now default to zero. Zero
omits `max_output_tokens` from Responses requests and disables the orchestrator
elapsed timer; explicit positive values retain their former hard-limit
semantics. Caller cancellation, model/provider output limits, rate limits, and
other bounded tool/run controls remain in force.

The supported exact model IDs are `gpt-5.6-luna`, `gpt-5.6-terra`, and
`gpt-5.6-sol`. Route advice implements the repository policy but has no launch
side effect. Both the requested and provider-reported model must match the same
exact ID; aliases and dated snapshot suffixes are terminal substitution failures
rather than silently accepted replacements. Re-check the
[current GPT-5.6 guidance](https://developers.openai.com/api/docs/guides/latest-model)
before distribution.

## Repository And Editor Tool Security

`RepositoryPathPolicy` is shared by context admission and live repository
tools. It accepts only repository-relative source-focused text paths; validates
canonical containment with a trailing root separator; rejects traversal,
absolute/device/alternate-stream paths, invalid/reserved names, reparse points
on every existing component, generated/cache/dependency roots, common secret
files, and non-text extensions. `RepositoryTextFileReader` bounds raw bytes
before allocation, rechecks length and modification time, hashes the complete
raw file, decodes strict UTF-8, and rejects NUL/control characters and private
key markers. Recursive search never descends into reparse-point directories.

`repository_search` supports literal matching, optional validated globs, stable
path order, at most 50 results, 5,000 files and 64 MiB per call, and a 256 MiB
run-wide scan budget. `repository_read_text` reads
at most a 1 MiB source file and 400 selected lines, returns provenance and
pagination metadata, and may require `expected_sha256`. Per-call tool-result
limits and a 2 MiB run-wide repository-output budget apply to both tools.

Repository policy is a defense-in-depth content eligibility boundary, not a
guaranteed secret detector. Callers should authorize the narrowest practical
`allowed_roots`; a trusted checkout and local user remain part of the threat
model.

When `editor_session` is absent, the registry skips session resolution, leases,
HTTP MCP construction, and preflight. The orchestrator receives either the
repository provider when explicitly enabled or an empty catalog for a bounded
reasoning-only call. When both providers exist, editor mutation verification
cannot be satisfied by repository tools because their names do not match the
orchestrator's editor read-back classification.

`EditorSessionResolver` validates the session-name grammar, combines it only
under `Build/_AgentValidation/00000000-000000-shared/mcp-sessions`, checks full-path containment,
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
| Reasoning-only local access | Omitting both repository access and `editor_session` selects an empty tool provider; validation rejects mutation, editor tool lists, and required tool use. |
| Repository data overreach | Context files are explicit atomic snapshots. Live tools are disabled by default, require explicit roots, remain read-only, use source/secret exclusions, and return no absolute paths. Selected content still leaves the machine and must be treated as provider-bound data. |
| Path traversal | Session names use a strict grammar. Repository paths reject absolute/device/alternate-stream/traversal forms, validate canonical containment, and reject reparse points; search does not descend into junctions or symlinks. |
| Arbitrary endpoint | Only the named session manifest is accepted; endpoints must be loopback and exact identity is preflighted. |
| Duplicate/ambiguous mutation | Duplicate call IDs fail. Mutating tool calls are never transport-retried. Same-session mutations hold an exclusive lease. |
| Unverified mutation | A later successful read-back or capture is mandatory by default. |
| Cost exhaustion | Exact model authorization, per-run turn/tool/token/time/retry budgets, bounded concurrency/retention, cancellation, usage reporting, and external API project limits. |
| Orphaned run | The registry links every run to explicit cancellation and broker shutdown. The caller polls to terminal state and cancels abandoned work. |
| Secret/content leakage through traces | Tracing is off by default and metadata-only when enabled; prompts, raw context snapshots, and tool payloads are excluded. Source-focused exclusions reduce accidental secret selection but are not a substitute for narrow roots or secret scanning. |
| Background-response retention | Background mode is opt-in and disclosed in the MCP schema/user guide. It uses temporary provider storage for polling and is not ZDR compatible even with `store: false`. |
| Editor process damage | The broker never starts/stops/finds processes. The named session manager owns lifecycle and PID validation. |

## Configuration

The checked-in `.codex/config.toml` resolves
`Tools/Invoke-LocalAgentBroker.ps1` by walking upward from the current
directory. The launcher resolves the repository root from its own location,
reads `Build/_AgentValidation/00000000-000000-shared/agent-tools/LocalAgentBroker.current`, and executes that immutable
versioned deployment without writing a build banner to the stdio protocol.
`Setup-LocalAgentBroker.ps1` publishes a fresh version before atomically moving
the pointer, so a loaded MCP process never blocks the update. A legacy fixed
directory remains a fallback only when no pointer exists.
The pointer affects only future launches. Existing Codex tasks retain their
current stdio process and pipes; stopping that process closes the transport and
requires a task/app restart rather than hot-rebinding the new deployment.

The same project configuration selects Terra at medium effort for the primary
Codex coordinator, Luna at low effort for default subagents, and four native
subagent threads. Project custom agents define the read-only Luna explorer,
workspace-writing Terra implementer, and read-only max-effort Sol architect.
`AGENTS.md` provides standing bounded broker-spend authorization while retaining
task-specific mutation and destructive-operation boundaries.

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

The broker's non-paid protocol validation must also confirm that
`editor_session` is optional and that `context_files`, `repository_access`, and
their budgets are advertised in the start schema. Reasoning-only live runs must
omit repository/editor access and advertise no function tools.

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
metadata in both the broker snapshot and nested terminal result. Exercise one
context snapshot plus `repository_search` and a hash-pinned
`repository_read_text`, then confirm traversal, excluded roots, hash mismatch,
and malformed UTF-8 are rejected before provider execution.

### Direct-versus-broker comparison

The deterministic comparison deliberately uses the same scripted provider and
tool results so transport variability does not masquerade as quality:

| Property | Direct in-editor | Local broker |
|---|---|---|
| Result parsing/quality | Shared parser and tool loop | Same |
| Provider token use | Same prompt/tools/budget produces the same reported usage | Same |
| Tool-call count/evidence | Shared call-ID and evidence contracts | Same, plus broker policy filtering |
| Latency | Provider plus tool latency | Adds enqueue and caller polling; editor-aware runs also add named-session preflight and a lease |
| Failure clarity | Structured provider/tool failure shown in ImGui | Same category plus queued/running/terminal status and requested/actual model |

No fixed latency claim is recorded because editor state, model service load, and
hardware dominate it. A release comparison should use the opt-in live lane and
record its dated evidence outside ordinary CI.

See the [Local Agent Broker user guide](../../user-guide/ai/local-agent-broker.md)
for workstation setup and operational use.
