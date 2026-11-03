# Local Agent Broker Integration

The implementation and tests live in [BlackJaxDev/local-agent-broker](https://github.com/BlackJaxDev/local-agent-broker). This repository owns the engine integration. See the [user guide](../../user-guide/ai/local-agent-broker.md) for installation and credentials, and the [broker policy](agent-broker-policy.md) for delegation rules.

## Ownership

| Owner | Responsibility |
|---|---|
| External `LocalAgentBroker.Orchestration` | Provider-neutral contracts, tool loop, provider transports, streaming, and HTTP MCP client. |
| External `LocalAgentBroker` | Stdio MCP host, run registry, repository policy, swarms, and model routing. |
| External shared and tray projects | History contracts, persistence, and Windows viewer. |
| `Tools/Dependencies/LocalAgentBroker.config.json` | Engine history and session paths, exact session identity validation, and tray startup. |
| `XREngine.Editor` | ImGui assistant, editor preferences, MCP tools, and scene presentation. |
| `Build/Shared/LocalAgentBroker.props` | Pinned library reference and acquisition before assembly resolution. |
| `Tools/Setup-LocalAgentBroker.ps1` | Verified release acquisition, versioned deployment, and pointer publication. |

The editor references the DLL from the pinned orchestration nupkg. This library has only framework dependencies. The build does not use an unpinned sibling source checkout or a floating package version. The broker and tray are not engine solution projects.

## Project Configuration

The broker has no engine adapter or engine-specific defaults. The launcher passes the tracked JSON config and pins `--repo-root` to this checkout. Relative state and session paths resolve against that root. A custom config can be selected with `-ConfigPath` or `LOCAL_AGENT_BROKER_CONFIG`. Explicit launcher arguments take priority over environment settings; environment settings take priority over JSON.

The launcher translates supported `XRE_LOCAL_AGENT_BROKER_*` settings only when the corresponding `LOCAL_AGENT_BROKER_*` setting is absent. Provider key values remain in environment variables. The config stores variable names only.

`sessionIdentityPath` is `["status", "editorSession", "name"]`. The HTTP client requires this ping result to match the requested session name. The editor supplies explicit read-only and destructive tool annotations; name prefixes do not grant tool access. The default history and session paths remain unchanged.

## Release Pin

`Tools/Dependencies/LocalAgentBroker.lock.json` records the version, package name and SHA-256, library SHA-256, and Windows archive name and SHA-256. `Build/Shared/LocalAgentBroker.props` selects the same library version. Update both in one reviewed change.

The installer serializes writes per checkout. It downloads into a temporary file, verifies the archive hash, checks each extraction path, and verifies the library bytes. A corrupt existing library directory fails visibly instead of being overwritten while an editor might use it.

Full setup creates a fresh broker deployment. It runs initialize/tools-list against that exact DLL before replacing `LocalAgentBroker.current`. Existing connections remain attached to their previous process. No provider API request is required for installation or this smoke check.

## Validation

Run the standalone solution and regression suite in the broker repository. In this checkout, run the protocol smoke check, build the editor through `Tools/Invoke-AgentBuild.ps1`, and exercise the ImGui assistant and named-session path when changing their contracts. Keep work records in the private work-docs repository.

The standalone project uses the MIT License. Third-party components retain their own licenses and notices. The Windows viewer's WebView2 and JavaScript assets belong to the standalone distribution. The engine dependency report records the external orchestration package separately.
