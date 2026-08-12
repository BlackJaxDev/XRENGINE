# Local Agent Broker

The local agent broker is an optional, checkout-local stdio MCP app. It lets
Codex or another MCP client start a bounded OpenAI Responses API worker on an
explicit GPT-5.6 tier. A reasoning-only worker has no local tools; an
editor-aware worker receives controlled access to one named, loopback XRENGINE
editor MCP session.

The current Codex task remains the coordinator. A broker worker is a separate,
independently billed API request; it is not an in-place model switch. The
broker has no generic shell, Git, repository-write, process-discovery, or
process-lifecycle tool.

Codex evaluates it automatically for substantive tasks and uses it when a
bounded second worker can contribute editor evidence or a focused reasoning
slice. Exact one-step shell operations remain local because the broker cannot
perform them and delegation would add latency without useful judgment.

## Native Agents And Broker Workers

Project Codex configuration uses Terra/Medium for the coordinator and Luna/Low
for default native subagents. The custom `luna_explorer`, `terra_worker`, and
`sol_architect` agents own repository exploration, implementation, and
consequential reasoning respectively. Native agents have the appropriate Codex
filesystem and shell surface; broker workers do not.

Use native agents for repository searches, file operations, code changes, and
validation. Use broker workers for bounded reasoning over a compact evidence
packet or controlled live-editor evidence. Independent read-heavy work may run
in parallel; overlapping writes, moves, removals, and editor mutation remain
serialized.

## Requirements

Publishing and protocol-smoke-testing the app do not require an API key and do
not make an OpenAI API request. Starting a worker requires every item below.

| Requirement | How to satisfy or verify it |
|---|---|
| Supported checkout | Use Windows 10/11 with this XRENGINE checkout and the .NET 10 SDK. |
| Published broker | Run `Tools/Setup-LocalAgentBroker.ps1`; verify `Build/_AgentValidation/00000000-000000-shared/agent-tools/LocalAgentBroker.current` names a versioned deployment containing `XREngine.LocalAgentBroker.dll`. |
| Trusted project configuration | Trust the repository in Codex. Project-scoped `.codex/config.toml` MCP configuration is loaded only for trusted projects. Restart Codex after setup or configuration changes. |
| API project | Use an OpenAI API project with billing/quota and access to the exact selected model. API service is managed and billed separately from ChatGPT subscriptions. |
| API key | Put the project key in the process environment or, on Windows, the user environment, normally as `OPENAI_API_KEY`. Never put the value in the repository, MCP arguments, a prompt, logs, or command-line arguments. |
| Broker tools | Confirm the `local-agent-broker` MCP server exposes `recommend_agent_route`, `start_agent_run`, `get_agent_run`, `cancel_agent_run`, and `list_agent_runs`. |
| Editor session when needed | Omit `editor_session` for reasoning-only work. For editor evidence or mutation, start one exact session with `Tools/Manage-McpEditorSession.ps1`; its manifest and loopback MCP endpoint must be live for the whole run. |
| Standing authority | `AGENTS.md` pre-authorizes bounded broker/API spend for XRENGINE tasks. The coordinator selects the lowest-cost suitable model automatically without asking per run. Mutation and destructive operations still require authority from the task itself. |
| Bounded request | Set narrow turn, tool-call, output-token, elapsed-time, retry, and tool-result limits appropriate to the task. |

The supported exact model IDs are:

- `gpt-5.6-luna`
- `gpt-5.6-terra`
- `gpt-5.6-sol`

The broker rejects aliases and provider-reported dated snapshot suffixes: both
the requested and actual model must be the same exact approved model ID.

Check the [current OpenAI model catalog](https://developers.openai.com/api/docs/models)
and the API project's model access before a live run. The broker never silently
substitutes another tier.

## What The App Exposes

The fixed MCP surface contains five orchestration tools:

- `recommend_agent_route` classifies an objective under the repository routing
  policy. It is local and advisory; it never launches or switches a model. The
  coordinator uses its exact result automatically unless the user pinned a
  model.
- `start_agent_run` validates a request, starts the paid worker asynchronously,
  and returns a run ID promptly.
- `get_agent_run` returns incremental text/evidence/usage, retry count, bounded
  provider-attempt diagnostics, current observation/progress metadata, and the
  terminal result for one run.
- `cancel_agent_run` cooperatively cancels a queued or running worker and its
  pending editor tool call.
- `list_agent_runs` returns bounded metadata for active and recently retained
  runs.

The broker itself does not start or stop the editor. The named session manager
owns editor process lifecycle and validates PID ownership.

### Run Status And Progress Contract

`get_agent_run` is the authoritative snapshot for one run; `list_agent_runs`
returns the same status fields in compact form. Both responses include:

- `updatedUtc`: the most recent time the broker changed retained run state;
- `observedUtc`: the time this response was produced. It advances on every poll,
  including when the provider has produced no text or tool result;
- `elapsedMilliseconds`: non-negative wall-clock time from run creation through
  `observedUtc`; and
- `progressMessage`: the latest informational broker/provider stage. It is not a
  percent-complete estimate and does not replace the authoritative `status`.

New runs begin with `queued`; orchestration changes this to
`orchestration_started`. A streaming Responses call publishes
`provider_stream_connected` once the SSE connection is established. While the
stream advances, the broker periodically records the provider's latest event
type (after the first provider event and then at bounded intervals), even if no
text delta is available. A terminal snapshot replaces `progressMessage` with
the terminal status. Continue polling until `status` is `completed`, `failed`,
or `cancelled`; use elapsed/observed time to distinguish a quiet healthy stream
from a stale caller or transport.

### Response Controls And Output Budget

`start_agent_run` accepts `text_verbosity` as `low`, `medium`, or `high`; it
defaults to `medium`. The broker sends this as the Responses API `text.verbosity`
control and preserves `reasoning_effort` separately. Start, get, and list
responses retain both requested controls and the requested `max_output_tokens`
hard budget. That budget covers visible output and reasoning tokens, so the
broker never automatically raises it or retries an incomplete response with a
larger budget. When a response ends incomplete because `max_output_tokens` was
reached, start a new explicitly authorized bounded run with a higher budget or
with lower reasoning effort/text verbosity.

## One-Time Installation

From the repository root, publish and smoke-test the broker:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Setup-LocalAgentBroker.ps1
```

This publishes the BCL-only app to an immutable directory under
`Build/_AgentValidation/00000000-000000-shared/agent-tools/LocalAgentBroker-<timestamp>`, atomically updates
`Build/_AgentValidation/00000000-000000-shared/agent-tools/LocalAgentBroker.current`, and performs an MCP
initialize/list-tools test. Versioned deployment avoids overwriting DLLs held
by a broker process that is already running. It does not contact the OpenAI API.
An already-running Codex task remains attached to its existing stdio process;
restart that task or Codex after setup before expecting it to use the new
deployment. Killing the child process alone closes the old transport and does
not hot-rebind it.

Alternatively, the opt-in agent-tool bootstrap includes the same setup:

```powershell
ExecTool --bootstrap --with-agent-tools
```

Create an API key on the
[OpenAI API key page](https://platform.openai.com/api-keys), then store it in
the Windows user environment as `OPENAI_API_KEY`. OpenAI recommends keeping
keys out of source and using an environment variable or secret manager; see
[API key safety](https://help.openai.com/en/articles/5112595).

The launcher checks its inherited process environment first. On Windows, when
that value is absent, it reads the same variable name from the user environment
and injects it into only the broker child process. This means a newly configured
user-scoped key does not need to be copied into chat, arguments, or repository
state. Other operating systems require the MCP process to inherit the variable.
A ChatGPT or Codex sign-in is not a replacement for API credentials, and
[API billing is separate](https://help.openai.com/en/articles/8156019).

Trust this repository and restart Codex. The checked-in
`.codex/config.toml` launches the published broker through
`Tools/Invoke-LocalAgentBroker.ps1` and allowlists only the five broker tools.
Use the MCP server UI or `/mcp`, where available, to confirm the server and
tools are present. See the [Codex MCP guide](https://learn.chatgpt.com/docs/extend/mcp)
for project-scoped MCP behavior.

To use a different key variable, set only the variable name before starting
Codex:

```powershell
$env:XRE_LOCAL_AGENT_BROKER_API_KEY_ENV = 'MY_OPENAI_API_KEY'
```

The named variable must contain the secret. Do not put the secret itself in
`XRE_LOCAL_AGENT_BROKER_API_KEY_ENV`.

For a manual broker launch outside Codex:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Invoke-LocalAgentBroker.ps1 -ApiKeyEnvironmentVariable MY_OPENAI_API_KEY
```

## Per-Run Workflow

### 1. Choose Reasoning-Only Or Editor-Aware Execution

For focused reasoning over a compact evidence packet, omit `editor_session`.
The worker receives no local tools, no editor lease or endpoint, and no ability
to mutate repository, process, or editor state.

Start a named session only when the worker needs live editor evidence. Read-only
is the default and preferred permission:


```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Manage-McpEditorSession.ps1 Start -Name broker-read -PermissionPolicy AllowReadOnly
```

Confirm its status if needed:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Manage-McpEditorSession.ps1 Status -Name broker-read
```

For a task that explicitly authorizes scene mutation, use a separate session:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Manage-McpEditorSession.ps1 Start -Name broker-edit -PermissionPolicy AllowMutate
```

The broker resolves only
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/<timestamp>-<name>/session.json`, accepts only a
loopback HTTP(S) endpoint, calls `ping`, and verifies the exact reported
session name. Do not edit `session.json` by hand.

### 2. Let Codex Route Automatically

XRENGINE's standing policy lets Codex invoke bounded broker workers without a
per-run permission prompt. A user may still pin a model, tier ceiling, budget,
or forbid broker use for a task. For editor-aware work, provide the session,
objective, authority, evidence, and limits:

> Use the local agent broker against editor session `broker-read` for a
> read-only analysis of the shadow artifact. Select the lowest-cost suitable
> tier automatically. Keep the run under 3 turns, 6 tool calls, 2,000 output
> tokens, and 120 seconds. Poll it to completion, verify the selected and actual
> model, and integrate the evidence locally. Do not modify files or the scene.

For mutation, name every permitted editor tool and the required verification:

> Start a broker worker against `broker-edit` and select the tier
> automatically. Allow mutation only through `set_transform`; allow
> verification through `get_transform` and `capture_viewport_screenshot`.
> Require read-back or capture evidence. Do not use any other mutating or
> destructive tool.

The standing authorization covers API spend only within the documented bounds.
It does not grant scene mutation, destructive access, external writes, or a
broader task scope.

### 3. Let The Coordinator Drive The Run To A Terminal State

Codex should:

1. Choose reasoning-only execution or confirm the exact named editor session,
   scope, and mutation boundary.
2. Partition the task into a bounded slice. Unless the user pinned a model, call
   `recommend_agent_route` and use its exact result as `requested_model`.
3. Build a compact evidence packet with relevant files/symbols, current diff,
   commands/results, failed hypotheses, unresolved questions, and the next
   decision.
4. Call `start_agent_run` with the selected exact model and narrow budgets.
5. Keep `use_background_mode` false unless the user or an applicable project
   policy accepts temporary provider storage for a long-running slice. When
   enabled, poll normally; the broker polls the provider response separately.
6. Retain the returned run ID and poll `get_agent_run` until status is
   `completed`, `failed`, or `cancelled`.
7. Cancel a run that is abandoned or no longer useful.
8. Verify both `requested_model` and `actual_model`. Treat a mismatch as a
   terminal failure for that run and never accept silent substitution.
9. Integrate the returned evidence and validate conclusions or mutations
   locally. The worker cannot run repository shell/Git commands.
10. Route later mechanical slices back down to Terra or Luna when appropriate.
   Report provider, editor, budget, or policy failures plainly; do not change
   tiers merely to bypass a model-access failure.

Reasoning-only runs share only the global concurrency bound. Editor read-only
runs may overlap; a mutating run takes an exclusive lease on its named editor
session and excludes readers until the mutation lease ends.

### 4. Stop Only A Session You Started

When the run is terminal and no further inspection is needed:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Manage-McpEditorSession.ps1 Stop -Name broker-read
```

Never stop editor processes by name or terminate a session another workflow
owns.

## Start Request Contract

A caller using the MCP tools directly must provide `objective`,
and `requested_model`. `editor_session` is optional and must be omitted for a
reasoning-only run. When the user did not pin a tier, the coordinator fills
`requested_model` from `recommend_agent_route`. The other fields make the run
safer and more reproducible.

A reasoning-only request has no editor session or tool policy entries:

```json
{
  "objective": "Review the supplied architecture decision for concurrency risks.",
  "success_criteria": [
    "Return ranked risks and a concrete recommendation."
  ],
  "constraints": [
    "Reason only from the supplied evidence packet."
  ],
  "requested_model": "gpt-5.6-sol",
  "reasoning_effort": "max",
  "text_verbosity": "medium",
  "use_background_mode": false,
  "evidence_packet": {
    "relevant_files_and_symbols": [],
    "current_diff": "Compact coordinator-supplied summary.",
    "commands_and_results": [],
    "failed_hypotheses": [],
    "unresolved_questions": [],
    "next_decision": "Approve or revise the design."
  },
  "budget": {
    "max_turns": 3,
    "max_tool_calls": 0,
    "max_output_tokens": 4096,
    "max_tool_result_bytes": 262144,
    "max_elapsed_seconds": 120,
    "max_retries": 1,
    "max_concurrency": 1
  }
}
```

An editor-aware request names the session and exact tool policy:

```json
{
  "objective": "Identify the render pass responsible for the shadow artifact.",
  "success_criteria": [
    "Name the responsible pass or return a bounded unresolved result.",
    "Include viewport or editor-state evidence."
  ],
  "constraints": [
    "Read-only.",
    "Do not modify files or scene state."
  ],
  "requested_model": "gpt-5.6-sol",
  "reasoning_effort": "high",
  "use_background_mode": false,
  "editor_session": "broker-read",
  "evidence_packet": {
    "relevant_files_and_symbols": [
      "XREngine/Rendering/Pipelines/Commands/..."
    ],
    "current_diff": "No rendering files changed.",
    "commands_and_results": [
      "MCP screenshot shows the artifact in two camera positions."
    ],
    "failed_hypotheses": [],
    "unresolved_questions": [
      "Which pass first writes the incorrect shadow value?"
    ],
    "next_decision": "Choose the next read-only editor inspection."
  },
  "tool_policy": {
    "allow_mutation": false,
    "allow_destructive": false,
    "require_mutation_evidence": true,
    "allowed_tools": [
      "capture_viewport_screenshot",
      "find_nodes_by_name"
    ],
    "denied_tools": []
  },
  "budget": {
    "max_turns": 3,
    "max_tool_calls": 6,
    "max_output_tokens": 2000,
    "max_tool_result_bytes": 262144,
    "max_elapsed_seconds": 120,
    "max_retries": 1,
    "max_concurrency": 1
  }
}
```

Tool permission is intersection-based. A tool call must be permitted by the
editor session and absent from the broker deny list. When `allowed_tools` is
non-empty, the call must also be listed there. Mutation additionally requires:

- explicit user authorization in the task;
- an `AllowMutate` editor session;
- `allow_mutation: true`;
- a non-empty exact `allowed_tools` list; and
- a later successful read-back, inspection, validation, query, or capture.

Destructive authorization also requires mutation authorization. Unknown or
unannotated editor tools are classified conservatively.

When `editor_session` is absent, `allow_mutation`, `allow_destructive`,
`allowed_tools`, `denied_tools`, and required tool use are rejected if they
would imply local tool access.

`use_background_mode` defaults to `false`. When set to `true`, each provider
turn is created asynchronously and polled until `completed`, `failed`,
`incomplete`, or `cancelled`. Use it for long reasoning runs where one
uninterrupted stream is unreliable. Cancellation calls the provider cancellation
endpoint after a response ID exists. The terminal snapshot reports each safe
attempt's turn/attempt number, response ID, actual model, event/poll count, last
event type, terminal status, incomplete reason, elapsed time, retry disposition,
and whether provider cancellation was accepted. It never includes prompts,
response text, headers, credentials, or raw request bodies in those diagnostics.
`budget.max_output_tokens` must be between 16 and 128,000; values below the live
Responses API minimum are rejected before any paid request starts.

## Cost, Data, And Security

Each worker is billed to the API project associated with the configured key,
independently of ChatGPT/Codex product billing. Configure API project budgets,
usage alerts, and model access before relying on standing automatic runs. The
broker reports token usage but intentionally does not embed volatile price
estimates. Omitted run budgets default to 3 turns, 8 tool calls, 4,096 output
tokens, 120 seconds, 1 retry, and per-run concurrency 1; the process default is
at most 4 concurrent runs.

Requests and editor evidence selected for the run leave the machine for OpenAI
processing. The editor MCP endpoint remains loopback-only. Responses use
`store: false`; continuations replay prior output items, correlated tool
results, and encrypted reasoning items required by the Responses API flow.
Background mode still sends `store: false`, but OpenAI temporarily stores the
response for roughly ten minutes so it can be polled; it is therefore not Zero
Data Retention compatible. See
[OpenAI background mode](https://developers.openai.com/api/docs/guides/background).

The API key value is read first from the broker process environment and, only on
Windows when absent there, from the configured user-scoped environment
variable. It is not accepted in tool arguments, persisted, echoed, or included
in metadata traces.
If a key may have leaked, rotate it before another run.

Tracing is off by default. `metadata` traces contain run/model IDs, budgets,
counts, timing, usage, and redacted failures. They exclude prompts, editor tool
arguments/results, API keys, and authorization headers.

## Process Configuration

Set these variables before starting Codex. Windows user-scoped API-key values
are also resolved on demand by the launcher and broker:

| Environment variable | Default | Allowed values |
|---|---:|---|
| `XRE_LOCAL_AGENT_BROKER_API_KEY_ENV` | `OPENAI_API_KEY` | Valid environment-variable name |
| `XRE_LOCAL_AGENT_BROKER_EDITOR_AUTH_ENV` | unset | Name of an optional editor bearer-token variable |
| `XRE_LOCAL_AGENT_BROKER_MAX_CONCURRENCY` | 4 | 1-8 |
| `XRE_LOCAL_AGENT_BROKER_MAX_RUNS` | 32 | 4-256 |
| `XRE_LOCAL_AGENT_BROKER_RETENTION_MINUTES` | 120 | 1-1440 |
| `XRE_LOCAL_AGENT_BROKER_TRACE` | `off` | `off` or `metadata` |

The normal named-session workflow uses loopback without bearer authentication.
When editor auth is enabled, put the bearer token in the environment variable
named by `XRE_LOCAL_AGENT_BROKER_EDITOR_AUTH_ENV`.

## Troubleshooting

- **Broker server or tools are missing:** run
  `Tools/Setup-LocalAgentBroker.ps1`, trust the repository, restart Codex, and
  inspect the MCP server list. The project server is optional
  (`required = false`), so Codex can start even when the broker cannot.
- **Broker transport closed after republishing or stopping the old process:**
  restart the Codex task/app. Stdio MCP transports are process-bound; an
  already-running task cannot attach the replacement deployment in place.
- **Published DLL is missing:** verify
-  `Build/_AgentValidation/00000000-000000-shared/agent-tools/LocalAgentBroker.current` contains a valid versioned
  deployment name and that deployment contains `XREngine.LocalAgentBroker.dll`,
  then rerun setup.
- **Protocol smoke test fails:** run
  `powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Test-LocalAgentBrokerMcp.ps1`.
  This test does not make a paid API request.
- **API key is not set:** on Windows, set the configured variable in the user or
  process environment; on other systems ensure Codex inherits it. Restart Codex
  only when changing broker setup/configuration or when an older broker process
  is already loaded. Do not paste the key into chat.
- **Quota, billing, or model access fails:** check the API project and exact
  model. Do not change tiers merely to bypass access failure; route a newly
  bounded or materially reclassified slice under the standing policy.
- **Session manifest is missing or stale:** use
  `Tools/Manage-McpEditorSession.ps1 Status -Name <exact-name>`, or stop and
  recreate only that named session. Do not repair the JSON manually.
- **Session identity or endpoint is rejected:** verify the manifest is under
  this checkout, its endpoint is loopback, the editor is alive, and `ping`
  reports the exact requested session name.
- **Mutation tool is denied:** all permission layers must agree. Confirm the
  user's mutation authority, `AllowMutate`, `allow_mutation: true`, and the
  exact tool allowlist.
- **Mutation result is rejected:** the worker must perform a later read-back or
  capture. A successful mutating call alone is not a successful run.
- **Run remains queued/running:** poll the retained run ID within its elapsed
  budget. `observedUtc` must advance on each poll; use `progressMessage` and
  `updatedUtc` to see the latest broker/provider stage, then cancel the run if
  the result is no longer needed.
- **A long stream ends before completion:** after accepting the temporary
  storage/ZDR tradeoff, rerun the newly bounded slice with
  `use_background_mode: true`; do not silently enable it for unrelated runs.
- **Requested and actual model differ:** treat the run as failed and report it.
  Never accept silent substitution.

## Related Documentation

- [Local Agent Broker developer guide](../../developer-guides/ai/local-agent-broker.md)
- [Bootstrap and first-time setup](../setup/bootstrap.md)
- [Editor MCP server and assistant](mcp-server.md)
