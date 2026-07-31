# Local Agent Broker

The local agent broker lets Codex or another MCP client start a bounded OpenAI
Responses API worker on an explicitly selected GPT-5.6 tier while keeping
XRENGINE editor tools on the local machine. It is optional. A broker worker is
a separate API request; it does not change the model running the current Codex
task.

The broker exposes only five orchestration tools:

- `recommend_agent_route` classifies work without starting a paid request.
- `start_agent_run` starts an explicitly selected worker and returns promptly.
- `get_agent_run` polls incremental and terminal results.
- `cancel_agent_run` requests cooperative cancellation.
- `list_agent_runs` lists bounded metadata for active and recent runs.

There is deliberately no shell or arbitrary-command broker tool.

## One-Time Setup

You need the .NET 10 SDK and an OpenAI API key belonging to the API project
that should be billed. A ChatGPT subscription or Codex sign-in is not an API
credential.

1. Create an API key on the
   [OpenAI API key page](https://platform.openai.com/api-keys).
2. Store it in the Windows user environment as `OPENAI_API_KEY`. The Windows
   **Edit environment variables for your account** dialog avoids putting the
   key in a repository file or a checked-in command. Do not put it in
   `.codex/config.toml`, JSON, logs, or a command-line argument.
3. Open a new terminal so it inherits the variable, then publish and smoke-test
   the broker:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Setup-LocalAgentBroker.ps1
   ```

4. Trust this repository in Codex, then restart Codex. The checked-in
   `.codex/config.toml` starts the published broker through
   `Tools/Invoke-LocalAgentBroker.ps1`.

`ExecTool --bootstrap --with-agent-tools` includes this setup. In VS Code,
`Setup-LocalAgentBroker` and `Test-LocalAgentBrokerMcp` provide the same setup
and protocol-only smoke-test steps.

To use another environment-variable name, launch the broker manually with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Invoke-LocalAgentBroker.ps1 `
  -ApiKeyEnvironmentVariable MY_OPENAI_API_KEY
```

For Codex startup, set `XRE_LOCAL_AGENT_BROKER_API_KEY_ENV` before starting
Codex or edit the launcher arguments locally. The secret itself must remain in
the referenced environment variable.

## Start A Named Editor Session

Every worker run must target one exact editor session. For read-only work:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File Tools/Manage-McpEditorSession.ps1 `
  Start -Name broker-read -PermissionPolicy AllowReadOnly
```

For a task that genuinely needs scene mutation, start a separate session with
only the required editor permission threshold:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File Tools/Manage-McpEditorSession.ps1 `
  Start -Name broker-edit -PermissionPolicy AllowMutate
```

The broker resolves only
`Build/_AgentValidation/mcp-sessions/<name>/session.json`, accepts a loopback
HTTP endpoint, calls `ping`, and verifies the exact reported session name. It
never discovers or terminates an editor by process name.

When finished, stop only the session you started:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File Tools/Manage-McpEditorSession.ps1 `
  Stop -Name broker-read
```

## Asking Codex To Use It

Give explicit authority and a narrow objective. For example:

> Use the local agent broker with `gpt-5.6-sol` against editor session
> `broker-read` for a read-only analysis of the shadow artifact. Keep the run
> under 3 turns, 6 tool calls, 2,000 output tokens, and 120 seconds. Poll it to
> completion and integrate the evidence, but do not modify files or the scene.

For mutation, name the exact editor tools that may be used:

> Start a `gpt-5.6-terra` broker worker against `broker-edit`. Allow mutation
> only through `set_transform` and verification through `get_transform` and
> `capture_viewport_screenshot`. Require read-back/capture evidence.

The caller must provide `requested_model` exactly as one of:

- `gpt-5.6-luna`
- `gpt-5.6-terra`
- `gpt-5.6-sol`

The broker never substitutes another tier. Results contain both
`requested_model` and `actual_model`; a mismatch is a terminal visible failure.
The route recommendation tool is advisory and cannot launch a worker.

After `start_agent_run`, Codex should retain the returned run ID and poll
`get_agent_run` until the status is `completed`, `failed`, or `cancelled`.
Codex should cancel a run it no longer needs. Read-only runs may overlap;
mutating runs against the same named editor session are serialized and exclude
readers for the mutation lease.

## Cost, Storage, And Limits

Each worker is billed through the API project associated with
`OPENAI_API_KEY`, independently of ChatGPT or Codex product billing. Configure
API project budgets and usage alerts using OpenAI's
[production guidance](https://developers.openai.com/api/docs/guides/production-best-practices).
The broker reports token counts but intentionally does not embed price
estimates.

Responses use `store: false`. Continuations replay the returned response output,
including encrypted reasoning items, together with correlated
`function_call_output` items. Requests still leave the machine for OpenAI
processing; local editor MCP remains loopback-only.

Default process limits are four concurrent runs, 32 retained runs, and
120-minute retention. Override them before launching Codex with:

| Environment variable | Default | Range |
|---|---:|---:|
| `XRE_LOCAL_AGENT_BROKER_MAX_CONCURRENCY` | 4 | 1–8 |
| `XRE_LOCAL_AGENT_BROKER_MAX_RUNS` | 32 | 4–256 |
| `XRE_LOCAL_AGENT_BROKER_RETENTION_MINUTES` | 120 | 1–1440 |
| `XRE_LOCAL_AGENT_BROKER_TRACE` | `off` | `off` or `metadata` |

`metadata` traces contain run/model IDs, budgets, counts, timing, usage, and
redacted failure data. They do not contain prompts, tool arguments/results, API
keys, or authorization headers. Trace creation stops rather than exceeding the
repository limit of ten immediate `Build/_AgentValidation/` run folders.

## Troubleshooting

- **Broker output is missing:** run `Tools/Setup-LocalAgentBroker.ps1`, then
  restart Codex.
- **API key is not set:** set `OPENAI_API_KEY` in the environment inherited by
  Codex and restart it.
- **Session manifest is missing or stale:** start/status the exact name with
  `Tools/Manage-McpEditorSession.ps1`; do not edit `session.json` manually.
- **Model unavailable:** confirm that the configured API project has access to
  the exact model. Submit a new request only if you explicitly authorize a
  different tier.
- **Mutation tool denied:** use `AllowMutate` on the named editor session and
  include the exact tool in the broker run's `allowed_tools`. Both controls must
  permit it.
- **Protocol check:** run
  `powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Test-LocalAgentBrokerMcp.ps1`.

For implementation and security details, see the
[Local Agent Broker developer guide](../../developer-guides/ai/local-agent-broker.md).
