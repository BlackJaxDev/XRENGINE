# Local Agent Broker

The broker is maintained in [BlackJaxDev/local-agent-broker](https://github.com/BlackJaxDev/local-agent-broker). It provides bounded model runs, repository tools, code swarms, and an optional Windows history viewer. The engine keeps its ImGui assistant and editor MCP tools.

## Install

Run from the engine checkout:

```powershell
pwsh Tools/Setup-LocalAgentBroker.ps1
pwsh Tools/Test-LocalAgentBrokerMcp.ps1
```

Setup downloads the exact release pinned in `Tools/Dependencies/LocalAgentBroker.lock.json`. It verifies SHA-256 before extraction. The orchestration package is stored under `Build/Dependencies/LocalAgentBroker/<version>/`. The broker and tray use a new versioned deployment under `Build/_AgentValidation/00000000-000000-shared/agent-tools/`.

The deployment pointer changes only after the protocol smoke check passes. Existing MCP connections continue to use their running process. Restart the host chat or application to load the new deployment. Setup does not stop existing broker or tray processes.

The editor build installs only the pinned orchestration library when required. For an explicit library-only install, run `pwsh Tools/Setup-LocalAgentBroker.ps1 -LibraryOnly`. This does not install or start the tray.

Setup needs network access for uncached assets. For offline setup, pass `-PackagePath <package.nupkg>` and, for a full install, `-ArchivePath <release.zip>`. Local files must match the same pinned hashes. No second source checkout or private package-feed login is required.

## Credentials And Launch

Set `OPENAI_API_KEY` for OpenAI runs or `ANTHROPIC_API_KEY` for Claude runs. Set `ANTHROPIC_WORKSPACE_ID` only when required by that key. On Windows, the broker can read the configured user environment. Do not put key values in source, MCP arguments, or logs.

The existing MCP configuration continues to call `Tools/Invoke-LocalAgentBroker.ps1`. The launcher selects `--integration xrengine`, which preserves the checkout's history and named editor-session locations. Existing `XRE_LOCAL_AGENT_BROKER_*` variables remain accepted; the standalone server also supports `LOCAL_AGENT_BROKER_*` names.

The launcher can still open a legacy deployment while a host is being updated. New deployments use `LocalAgentBroker.dll` and namespace `LocalAgentBroker.*`.

## Tools And Permissions

The tools remain `recommend_agent_route`, `start_agent_run`, `get_agent_run`, `cancel_agent_run`, and `list_agent_runs`. The standalone repository owns the current model catalog, transport code, test suite, and protocol documentation.

The [broker policy](../../developer-guides/ai/agent-broker-policy.md) remains the engine's delegation policy. Runs require authorization, exact model selection, and bounded access. Context snapshots and tool results are sent to the selected provider. The broker does not change the model of the host application.

Repository access is opt-in and requires allowed roots. Editor access uses one explicitly named isolated MCP session. Mutation requires the matching session permission and tool allowlist. Code swarms return reviewed proposals unless automatic application is explicitly authorized.

## History And Editor Sessions

The XRENGINE adapter preserves history under `Build/_AgentValidation/00000000-000000-shared/local-agent-broker-ui/` and reads manifests under the shared `mcp-sessions/` directory. Opt-in metadata traces now use the history root's `traces/` directory.

Start and stop editor sessions through `Tools/Manage-McpEditorSession.ps1`. The broker only resolves a named manifest and contacts its loopback endpoint; it does not manage editor processes.

In generic workspaces, the standalone broker defaults to `.local-agent-broker/` state and disables editor sessions unless a manifest root is explicitly configured. An `AGENTS.md` file is not required by generic mode.
