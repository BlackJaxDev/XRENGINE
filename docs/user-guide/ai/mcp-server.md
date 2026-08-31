# MCP Server And Assistant

[Back to user guide](../README.md)

The editor MCP server lets AI assistants and external tools inspect and modify the active XRENGINE editor world through HTTP JSON-RPC. Use this page to enable and operate it. For protocol and implementation details, see [MCP Server Implementation](../../developer-guides/ai/mcp-server.md).

This is distinct from the optional [Local Agent Broker](local-agent-broker.md):
the editor is an HTTP MCP server exposing scene tools, while the broker is a
stdio MCP server that starts explicitly selected OpenAI API workers and acts as
an internal MCP client of one named editor session. Neither should expose the
editor endpoint publicly.

## Enable The Server

Open **Global Editor Preferences** and find the **MCP Server** category.

Important settings:

- `McpServerEnabled`: starts or stops the server.
- `McpServerPort`: default `5467`.
- `McpServerRequireAuth` and `McpServerAuthToken`: require bearer auth.
- `McpServerReadOnly`: blocks mutating tools.
- `McpServerAllowedTools` and `McpServerDeniedTools`: constrain the visible tool set.
- `McpPermissionPolicy`: controls whether tools prompt before execution.

The default endpoint is:

```text
http://localhost:5467/mcp/
```

## Isolated Editor Sessions

Use an isolated editor session for agent-driven MCP work. Each named session gets its own managed build output and intermediate files, MCP port, process identity, editor preferences, asset cache/metadata, and logs. A normal solution build can then overwrite `Build/Editor` without touching a running session.

```powershell
pwsh Tools/Manage-McpEditorSession.ps1 Start -Name agent-rendering
pwsh Tools/Manage-McpEditorSession.ps1 Start -Name agent-physics
pwsh Tools/Manage-McpEditorSession.ps1 List
```

`Start` selects an available port beginning at `5467`, builds with a session-specific .NET artifacts root, launches the Unit Testing World, and waits for that session's MCP status endpoint. Pass `-Port 5501` to require a particular port, `-NoWait` to return immediately after launch, or `-NoBuild` to reuse that stopped session's existing artifacts.

Call a named session without copying its port:

```powershell
pwsh Tools/Invoke-Mcp.ps1 -Session agent-rendering -Method ping
pwsh Tools/Invoke-Mcp.ps1 -Session agent-rendering -Method tools/list
```

Stop only the process owned by that session, then remove its disposable artifacts when they are no longer needed:

```powershell
pwsh Tools/Manage-McpEditorSession.ps1 Stop -Name agent-rendering
pwsh Tools/Manage-McpEditorSession.ps1 Remove -Name agent-rendering
```

The manager verifies the executable path, PID, and process start time before stopping anything. It first requests a graceful window close, then terminates only that verified session PID if the editor apphost does not expose a closable main-window handle. It never searches for and kills all editor processes. Pass `Stop -Force` to skip the graceful close attempt.

Session data lives under `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/<timestamp>-<name>/`; commands address the logical `<name>`. The session manager retains at most five sessions and removes the oldest stopped session before creating another. Repository source assets and `Assets/UnitTestingWorldSettings.jsonc` remain shared intentionally, so source edits are still visible across sessions. The default session permission policy is `AllowAll` for unattended local automation; use `-PermissionPolicy AllowReadOnly` when mutation is not required.

## Command Line

You can also launch the editor with MCP enabled:

```powershell
XREngine.Editor.exe --mcp
XREngine.Editor.exe --mcp --mcp-port 8080
XREngine.Editor.exe --mcp --mcp-allow-all
XREngine.Editor.exe --no-mcp
```

Use `--mcp-allow-all` only for trusted local automation because it bypasses permission prompts.
Use `--no-mcp` to force MCP off for an isolated benchmark or unattended run,
even when the saved editor preference enables it.
Command-line MCP values are process-local session overrides. They do not modify
or appear as unsaved changes in the persisted editor preference assets.
MCP clients can apply any editor preference property for only the active process
by calling `set_editor_preference` with `session_only: true`; nested dotted paths
such as `Debug.RenderMesh3DBounds` are supported. Restarting the editor discards
the value and reveals the saved global/project preference again.
`set_game_setting` supports the same `session_only: true` behavior for any writable
game setting or nested dotted path.

## VS Code

Add this workspace MCP config when you want Copilot or another MCP-aware client to connect:

```json
{
  "servers": {
    "xrengine": {
      "type": "http",
      "url": "http://localhost:5467/mcp/"
    }
  }
}
```

Start the editor, enable the server, then check the client tool picker for XRENGINE tools such as `list_worlds`, `list_scene_nodes`, `capture_viewport_screenshot`, and `start_viewport_sequence_capture`.

## In-Editor Assistant

The ImGui editor includes **Tools > MCP Assistant**. It can use provider keys from editor preferences or environment variables such as `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `GEMINI_API_KEY`, and `GITHUB_TOKEN`.

For scene or material edits, prefer prompts that ask the assistant to inspect the current world, make a bounded change, and verify with read-back or a viewport screenshot.

## Capture Subsequent Viewport Frames

For animation, physics, flicker, streaming, and temporal-rendering issues, ask the assistant to start a viewport sequence capture. The assistant can capture an exact number of subsequent frames or sample for a bounded number of seconds, poll `get_viewport_sequence_capture`, and inspect the resulting individual PNGs, `contact-sheet.png`, and `manifest.json`.

Example prompts:

- *"Capture the next 12 consecutive viewport frames and inspect the contact sheet for flicker."*
- *"Sample the editor viewport at 10 FPS for five seconds and identify which frames differ most."*
- *"List active viewport sequence captures and cancel the one still running."*

The default overflow policy fails rather than silently omitting a requested consecutive frame. Captures are bounded by frame, duration, pixel, memory, and contact-sheet limits. Both OpenGL and Vulkan are supported. Vulkan capture uses bounded GPU staging slots and nonblocking fence polling; the manifest reports GPU completion time, CPU conversion time, source format, queue slot, and whether an MSAA resolve was needed. If the renderer queue is full, `overflow_policy: "fail"` stops the sequence while `"drop"` records the skipped frame. There is no silent CPU or OS-window fallback.

On Vulkan, an unsignaled capture fence produces a warning after two seconds and fails the requesting capture after ten seconds without blocking the render thread. The slot stays quarantined until the GPU finishes or the renderer is recreated. This protects the editor-side workflow from hanging, while the operating system's GPU watchdog remains responsible for recovering a GPU submission that is genuinely stuck.

## Occlusion Validation

`set_editor_camera_depth_mode(reversed_depth)` changes the active editor camera's
depth convention and invalidates its viewport. It does not change saved project
defaults. Verify both normal and reversed depth against an occlusion-disabled
capture at the same pose before accepting a culling change.

For Vulkan GPU cost measurements, use
`get_render_profiler_stats.vulkan.frame_lifecycle.gpu_command_buffer_timing`.
Its coherent snapshot separates current query availability from `last_completed`,
which identifies the submitted render frame, sample sequence, image slot, age
and elapsed nanoseconds. Count each completed sequence once and reject samples
whose source frame precedes the workload change. The legacy
`gpu_command_buffer_ms` scalar alone does not identify the measured frame.

`evaluate_gpu_hiz_crossover(samples_json, requirements_json)` evaluates supplied
matched Disabled/Full/Coarse GPU timings without changing engine settings. Each
sample names the GPU, backend, extent, workload, depth convention, parity proof,
cohort and timestamp scope. Requirements specify minimum observations and cost
margins. The tool checks internal consistency; it does not independently verify
the caller's parity proof or manufacture a profitable threshold. Missing,
insufficient, ambiguous or non-winning evidence never promotes an occlusion mode.

## Texture Streaming Diagnostics

`get_render_state` includes the active window's owner-published event/surface
snapshots and effective clip-depth range. These read-only snapshots help
distinguish suspended output from renderer failure without controlling the
desktop; a sampled event-pump stack alone does not establish minimized state.
`get_time_state.terminalFault` retains the first exception that stopped the
current timer run, including the loop phase and visibility-publication sequence
numbers. It remains available in Release builds even when category logs are
compiled out, and resets only when a new timer run starts.

`get_texture_streaming_summary` includes `backend_upload_diagnostics`, an
on-demand text snapshot of the active backend's upload counters. Vulkan reports
worker preparation and retained ownership separately from queued transfers and
descriptor publication. These are backend-wide counters, not an atomic frame
sample. Use repeated observations and completed uploads to establish progress.

## Safety Notes

- Use read-only mode for inspection-only sessions.
- Require auth when exposing the server beyond trusted local processes.
- Keep mutating and destructive tools behind prompts unless you are running controlled automation.
- Use allowed/denied tool lists for constrained workflows.

## Deeper Docs

- [MCP Server Implementation](../../developer-guides/ai/mcp-server.md)
- [MCP Assistant Developer Guide](../../developer-guides/ai/mcp-assistant.md)
