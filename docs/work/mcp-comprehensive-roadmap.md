# XREngine MCP Comprehensive Roadmap

## Goal
Make XREngine MCP production-grade for AI agent workflows by closing protocol, security, reliability, and workflow-surface gaps.

## Current Baseline (Feb 2026)
- Server transport: HTTP JSON-RPC via `McpServerHost`.
- Protocol methods: `initialize`, `tools/list`, `tools/call`, `resources/list`, `resources/read`, `prompts/list`, `prompts/get`, `ping`.
- Tool surface: ~59 editor tools spanning scene/world/components/introspection/viewport.
- Auto-registration and schema generation from `[XRMcp]` + `[McpName]`.
- Existing docs under `docs/features/mcp-server.md` and `docs/features/voice-mcp-bridge.md` are partially out of sync with actual tools.

## Status Snapshot
- ✅ Epic 1 largely implemented: resources/prompts protocol handlers and capabilities are now exposed.
- ✅ Epic 2 implemented: auth/CORS/size/timeout + read-only + allow/deny tool policy controls.
- ✅ Epic 3 partially implemented: idempotency support added for `tools/call` via `idempotency_key`.
- ✅ Epic 4 implemented: workflow parity tools (`undo`, `redo`, `clear_selection`, `create_primitive_shape`, `enter_play_mode`, `exit_play_mode`, `save_world`, `load_world`) plus alias handling.
- ⏳ Epic 5/6/7 still pending: docs parity automation, dedicated MCP test suite, operational telemetry/rate-limits.

---

## Epic 1 — Protocol Completeness (P0)

### Outcome
Support the expected MCP object model beyond tools so clients can consume resources/prompts and react to capability updates.

### Scope
- Add protocol handlers:
  - `resources/list`
  - `resources/read`
  - `prompts/list`
  - `prompts/get`
- Add `notifications/tools/list_changed` emission when tool surface changes (or explicit declaration of static surface with no notifications).
- Clarify protocol versioning strategy and compatibility policy.

### Acceptance Criteria
- `initialize` returns protocol and capability metadata for tools/resources/prompts.
- `resources/*` and `prompts/*` methods return valid JSON-RPC results and typed payloads.
- Unknown methods/invalid params produce spec-compliant JSON-RPC errors.
- Protocol behavior is covered by automated tests.

### File Touch Points
- `XREngine.Editor/Mcp/McpServerHost.cs`
- `XREngine.Editor/Mcp/McpToolRegistry.cs`
- `XREngine.Editor/Mcp/McpToolDefinition.cs`
- New: `XREngine.Editor/Mcp/McpResourceRegistry.cs` (suggested)
- New: `XREngine.Editor/Mcp/McpPromptRegistry.cs` (suggested)

---

## Epic 2 — Security and Safety Controls (P0)

### Outcome
Secure-by-config server defaults suitable for local dev and stricter team/shared setups.

### Scope
- Bearer auth (implemented in initial pass).
- CORS allowlist (implemented in initial pass).
- Request payload size limit (implemented in initial pass).
- Request timeout and cancellation propagation (implemented in initial pass).
- Add optional write-tool policy gate:
  - allowlist/denylist by tool name
  - read-only mode

### Acceptance Criteria
- Unauthorized calls return `401` with JSON-RPC error body.
- Non-allowlisted browser origins return `403`.
- Oversized payloads return `413`.
- Long-running requests are canceled at timeout and return timeout status.
- Write policy can block mutating tools deterministically with clear errors.

### File Touch Points
- `XREngine.Editor/Mcp/McpServerHost.cs`
- `XRENGINE/Settings/EditorPreferences.cs`
- `XRENGINE/Settings/EditorPreferencesOverrides.cs`
- New: `XREngine.Editor/Mcp/McpToolPolicy.cs` (suggested)

---

## Epic 3 — Mutation Reliability (P1)

### Outcome
Safer and more deterministic editing from AI agents.

### Scope
- Add idempotency support (`request_id` or `operation_id`) for mutating calls.
- Add transaction/session workflow:
  - `begin_transaction`
  - `commit_transaction`
  - `rollback_transaction`
- Add dry-run/preview variants for destructive operations.

### Acceptance Criteria
- Replayed operation IDs do not duplicate side effects.
- Transaction rollback restores pre-transaction state for supported operations.
- Preview responses include explicit proposed changes and impact summary.

### File Touch Points
- `XREngine.Editor/Mcp/EditorMcpActions.Scene.cs`
- `XREngine.Editor/Mcp/EditorMcpActions.Components.cs`
- `XREngine.Editor/Mcp/McpToolContext.cs`
- New: `XREngine.Editor/Mcp/McpTransactionManager.cs` (suggested)

---

## Epic 4 — Workflow Tooling Parity (P1)

### Outcome
Fill high-value missing tools expected by docs/voice and common assistant workflows.

### Scope
- Add or standardize these tools:
  - `undo`, `redo`
  - `clear_selection`
  - `create_primitive_shape`
  - `enter_play_mode`, `exit_play_mode`
  - `save_world`, `load_world`
- Add `tool_aliases` map for backwards compatibility where names drifted.

### Acceptance Criteria
- Voice bridge built-in command mappings resolve to real MCP tools.
- Documentation examples run without name translation hacks.
- New tools expose explicit schema and return structured data where relevant.

### File Touch Points
- `XREngine.Editor/Mcp/EditorMcpActions.Scene.cs`
- `XREngine.Editor/Mcp/EditorMcpActions.World.cs`
- `XREngine.Editor/Mcp/EditorMcpActions.Introspection.cs`
- `XRENGINE/Scene/Components/Audio/VoiceMcpBridgeComponent.cs`

---

## Epic 5 — Discoverability and Docs Sync (P1)

### Outcome
No drift between source and docs.

### Scope
- Auto-generate MCP tool table from `McpToolRegistry.Tools` for docs.
- Include parameter schema snippets and examples.
- Add migration notes for renamed/aliased tools.

### Acceptance Criteria
- `docs/features/mcp-server.md` command table matches runtime `tools/list` output.
- CI check fails when docs snapshot diverges from runtime tool registry.

### File Touch Points
- `docs/features/mcp-server.md`
- New: `Tools/Reports/generate_mcp_docs.ps1` (suggested)
- New: `XREngine.UnitTests/Mcp/McpDocsParityTests.cs` (suggested)

---

## Epic 6 — Test Coverage (P1)

### Outcome
Protocol and tool behavior stays stable as engine evolves.

### Scope
- Add MCP host tests:
  - envelope validation
  - auth/cors/size/timeout behavior
  - error code behavior
- Add registry/schema tests:
  - required/optional param mapping
  - enum/schema generation
- Add mutation smoke tests for representative scene/component tools.

### Acceptance Criteria
- Failing tests catch protocol regressions before merge.
- Representative mutation and introspection tools covered.

### File Touch Points
- New folder: `XREngine.UnitTests/Mcp/`
- `XREngine.Editor/Mcp/McpServerHost.cs`
- `XREngine.Editor/Mcp/McpToolRegistry.cs`

---

## Epic 7 — Operational Readiness (P2)

### Outcome
Easier rollout and debugging in multi-client workflows.

### Scope
- Structured request logging with redaction for auth headers.
- Optional per-client rate limiting.
- Health/status endpoint details (`/mcp/status` or `ping` payload expansion).
- Startup diagnostics in editor log (enabled methods/capabilities, security mode).

### Acceptance Criteria
- Logs are useful for diagnosing failed tool calls without exposing secrets.
- Rate limits are configurable and test-covered.

### File Touch Points
- `XREngine.Editor/Mcp/McpServerHost.cs`
- `XRENGINE/Settings/EditorPreferences.cs`

---

## Delivery Plan

### Milestone A (1–2 days)
- Land P0 hardening (auth, CORS, size, timeout, strict envelope validation).
- Update docs for new MCP settings and request requirements.

### Milestone B (3–5 days)
- Add protocol completeness (`resources/*`, `prompts/*`).
- Add MCP unit tests for host + registry.

### Milestone C (3–5 days)
- Fill workflow parity tools and voice mapping compatibility.
- Add docs parity generation/check.

### Milestone D (1 week)
- Add transaction/idempotency support and write-policy controls.

---

## Immediate Next PRs
1. **PR-1 (P0 hardening + docs):** host + preferences + docs updates.
2. **PR-2 (tests):** host/registry test harness and baseline protocol tests.
3. **PR-3 (protocol expansion):** resources/prompts registries and methods.
4. **PR-4 (workflow parity):** missing core workflow tools + voice bridge sync.
