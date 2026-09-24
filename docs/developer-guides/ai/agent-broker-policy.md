# Agent Broker Policy

Read this policy before using the local agent broker or a hierarchical swarm. It supplements the model routing rules in [AGENTS.md](../../../AGENTS.md).

## Local Agent Broker

The optional `local-agent-broker` MCP server is a supported, checkout-local
evidence-worker surface. Each worker is a separate, independently billed public
OpenAI Responses API request. It does not change the model running the current
Codex task and must never be described as an in-place switch or native handoff.

### Standing Authorization And Preconditions

Broker/API spend is pre-authorized for XRENGINE tasks within the model-routing,
scope, data, and budget limits below. For every substantive repository task,
assess whether an independent reasoning or editor-evidence slice will improve
quality or wall-clock time and invoke the broker automatically when it will.
Do not ask for per-run permission. Skip a paid worker when there is no meaningful
delegated judgment, such as a single exact file read or deterministic shell
operation that the coordinator can complete faster itself.

Call `start_agent_run` only when all of these conditions are true:

- The coordinator automatically selects the exact supported model ID for each
  bounded slice unless the user pins a model or tier ceiling. Use
  `recommend_agent_route` plus the routing policy in `AGENTS.md`, and pass its exact
  `gpt-5.6-luna`, `gpt-5.6-terra`, `gpt-5.6-sol`, `gpt-6-luna`,
  `gpt-6-sol`, or `gpt-6-astra` result to
  `start_agent_run`. Do not stop to ask the user which tier to use.
  `recommend_agent_route` defaults to the GPT-6 family. Use the deprecated
  `model_family: "gpt-5.6"` only for an explicit legacy request. GPT-6 maps bounded
  work to Luna, ordinary work to Sol, and difficult/high-risk work to Astra.
- The five broker tools are callable in the current session. If they are
  missing, follow `docs/user-guide/ai/local-agent-broker.md` for setup,
  project trust, and restart requirements. Do not simulate a broker run.
- The operator has configured the API key in the environment inherited by
  Codex or, on Windows, the configured user-scoped environment variable, and
  the API project has billing/quota plus access to the selected exact model.
  Never ask the user to paste the key into chat, print or echo it, inspect its
  value, put it in MCP arguments, or persist it.
- An evidence-only run omits both `repository_access` and `editor_session`,
  exposes no local tools, and cannot mutate repository, process, or editor
  state. Use `context_files` for immutable selected repository text, or enable
  read-only repository tools only with explicit narrow `allowed_roots`; both
  mechanisms send selected content to the OpenAI API. A run that needs editor
  evidence targets one exact named session created with
  `Tools/Manage-McpEditorSession.ps1`; the broker accepts only that session's
  manifest and loopback endpoint and never discovers or manages processes.
- The objective is one bounded reasoning/editor slice with explicit success
  criteria, constraints, tool policy, and narrow turn, tool-call,
  tool-result-byte, retry, and concurrency budgets. Output-token and
  elapsed-time limits are optional controls rather than required defaults.

Automatic runs default to at most 3 turns, 8 local tool calls, 1 retry, and
per-run concurrency 1. Context snapshots default to at most 16 UTF-8 text
files, 256 KiB per raw file, 1 MiB aggregate raw content, and 2 MiB rendered
provider input. The broker defaults to no run-wide output-token cap and no
elapsed-time timeout for every model. It omits `max_output_tokens` from the
Responses request and relies on the selected model/provider maximum; the
caller can still cancel a run. Explicit positive token and elapsed-time limits
remain hard caps. Raise any other individual limit only when the objective
requires it and the expected validation benefit justifies the additional cost.
Global broker concurrency remains bounded by
`XRE_LOCAL_AGENT_BROKER_MAX_CONCURRENCY`.

### Opt-In Hierarchical Code Swarms

When the user requests a hierarchical swarm, `start_agent_run` also accepts
`swarm` with exact `gpt-6-luna` and `reasoning_effort: "max"`. Keep the node,
depth, fanout, concurrency, output-reservation, and elapsed-time bounds narrow.
Supply exact `allowed_paths` and any read-only `context_files`. Every node
either decomposes/reviews or proposes one small code replacement. Agents have
no shell, Git, editor, or filesystem tools in this mode.

Reviewed proposals are the default. Set `swarm.auto_apply: true` only when the
user selected automatic application for that run and the underlying task
authorizes those source edits. Parent approval alone does not confer extra
authority. The host checks base hashes, merges disjoint replacements, applies
changes, and reads them back. Inspect `code_changes`, the node review chain,
failures, and `applied_paths`, then perform the relevant local build/runtime
validation. A swarm review does not establish that the code works. These
opt-in source changes are separate from the editor mutation policy below.

### Required Coordinator Workflow

1. Keep the current Codex agent as coordinator. Use native Codex subagents for
   broad repository searches, filesystem operations, implementation, and
   validation; use the broker for bounded reasoning, explicitly rooted
   read-only repository evidence, or editor-tool evidence. Prefer a supported
   native Codex handoff for routing an entire coding task.
2. Partition broker work into coherent bounded slices and route
   each slice to the lowest-cost tier likely to validate successfully: Luna for
   deterministic inventory, evidence extraction, and classification; GPT-6 Sol for
   ordinary scoped analysis and integration; GPT-6 Astra for unresolved architecture,
   GPU/concurrency root cause, or consequential final review. After an
   expensive reasoning slice resolves the ambiguity, route later mechanical
   slices back down instead of retaining the expensive tier.
3. Unless the user pinned a model, call `recommend_agent_route` for the current
   slice and use its exact result as `requested_model` under the standing
   repository authorization.
4. Build a compact evidence packet containing only the objective, success
   criteria, constraints, relevant files/symbols, current diff, commands and
   observed results, failed hypotheses, unresolved questions, and next
   decision. Attach exact known files through `context_files`; enable
   `repository_access` only when the worker must discover additional context,
   and authorize the narrowest practical roots. Do not send unrelated
   repository data or secrets.
5. Keep `use_background_mode` disabled by default. Enable it only for a
   long-running slice after the user or an applicable project policy accepts
   the provider's temporary response storage and non-ZDR behavior. When
   enabled, retain provider attempt diagnostics and cancellation acceptance.
6. Repository tools are disabled by default, always read-only, and separately
   authorized from editor policy. Use read-only editor access by default. A
   non-empty editor allowlist should name only the tools needed for the
   objective.
7. Mutation still requires all of: explicit task authority, an `AllowMutate` named
   session, `allow_mutation: true`, an exact non-empty mutating-tool allowlist,
   and a later read-back, query, inspection, validation, or capture. Destructive
   access additionally requires explicit destructive authority.
8. Call `start_agent_run` once per slice, retain its run ID, and poll
   `get_agent_run` until `completed`, `failed`, or `cancelled`. Cancel
   queued/running work that is abandoned or no longer useful.
9. Verify both `requested_model` and `actual_model`. Treat any mismatch as a
   terminal failure for that run. Do not accept silent substitution. A later
   run may use another automatically recommended tier only for a newly bounded
   or materially reclassified slice; do not
   change tiers merely to bypass provider/model-access failure.
10. Integrate the returned evidence into the local investigation and perform the
   relevant local read-back, capture, build, or test. The worker may read only
   explicitly attached or rooted eligible repository text. It has no generic
   shell, repository-write, Git, or process tool, so its answer is evidence,
   not repository validation.
11. Report API, policy, editor, timeout, budget, or model-access failures
    directly. Do not improvise credentials/endpoints, discover processes, or
    silently fall back.
12. Stop only a named editor session that this workflow started, and only after
    all runs using it are terminal.

Setup, cost, requirements, prompts, tool fields, and troubleshooting are in
`docs/user-guide/ai/local-agent-broker.md`. Implementation and security
details are in `docs/developer-guides/ai/local-agent-broker.md`.
