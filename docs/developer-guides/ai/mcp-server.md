# XREngine MCP (Model Context Protocol) Server

This document describes the MCP server implementation in XREngine Editor, which enables AI assistants and external tools to interact with the engine through a standardized JSON-RPC 2.0 protocol.

For enabling the server, configuring editor preferences, and connecting VS Code or the in-editor assistant, start with the [MCP Server And Assistant user guide](../../user-guide/ai/mcp-server.md). This page is the protocol, tool surface, and contributor implementation reference.

## Overview

The XREngine MCP Server exposes the editor's functionality via HTTP, allowing external tools (such as AI coding assistants) to:

- Query and manipulate the scene hierarchy
- Create, modify, and delete scene nodes
- Add and configure components
- Modify transforms (position, rotation, scale)
- Capture single viewport screenshots or bounded temporal frame sequences
- List worlds and scenes

The server implements the [Model Context Protocol](https://modelcontextprotocol.io/) specification (version `2024-11-05`).

---

## Starting the Server

The MCP server can be enabled through Editor Preferences or via command-line arguments.

### Isolated Agent Sessions (Recommended For Automation)

Do not launch automated MCP editors from the canonical `Build/Editor/...` output. A running process locks assemblies there and prevents another contributor from building the solution. Use the session manager instead:

```powershell
pwsh Tools/Manage-McpEditorSession.ps1 Start -Name agent-rendering
pwsh Tools/Manage-McpEditorSession.ps1 Status -Name agent-rendering
pwsh Tools/Manage-McpEditorSession.ps1 Stop -Name agent-rendering
```

The manager uses `dotnet build --artifacts-path` so every referenced project's `bin` and `obj` output is below the named session. The editor and engine projects honor SDK artifacts output instead of their canonical `BaseOutputPath` in this mode. Repository-managed native bridge DLLs are reused rather than rebuilt, avoiding shared native output races. After changing a native bridge, build that bridge explicitly before starting a new session.

The child process receives these isolation variables:

| Variable | Purpose |
|----------|---------|
| `XRE_EDITOR_SESSION_NAME` | Stable identity reported by MCP `ping` and `/mcp/status` |
| `XRE_EDITOR_SESSION_ROOT` | Root for isolated editor user data and engine logs |
| `XRE_GAME_CACHE_PATH` | Per-session imported-asset cache |
| `XRE_GAME_METADATA_PATH` | Per-session asset metadata, initially seeded from the repository metadata |

The manifest at `Build/_AgentValidation/mcp-sessions/<name>/session.json` records the endpoint, artifact path, PID, and process start time. Port selection and same-name startup are serialized through a file lock. Stop operations validate the recorded executable, PID, and start time to prevent PID reuse or cross-session termination.

MCP status distinguishes the editor process session from protocol client sessions:

```json
{
  "editorSession": {
    "name": "agent-rendering",
    "isolated": true,
    "processId": 12345
  }
}
```

### Editor Preferences (Recommended)

The MCP server settings are located in the **Global Editor Preferences** panel under the **MCP Server** category:

| Setting             | Description                                         | Default  |
|---------------------|-----------------------------------------------------|----------|
| `McpServerEnabled`  | Enable/disable the MCP server at runtime            | `false`  |
| `McpServerPort`     | Port number for the MCP server                      | `5467`   |
| `McpServerRequireAuth` | Require bearer auth (`Authorization: Bearer ...`) | `false`  |
| `McpServerAuthToken` | Bearer token expected when auth is enabled         | `""`     |
| `McpServerCorsAllowlist` | Allowed browser origins (`*` or comma-separated). Empty = allow all | `""` |
| `McpServerMaxRequestBytes` | Maximum HTTP request payload size            | `1048576` |
| `McpServerRequestTimeoutMs` | Request timeout in milliseconds             | `30000`  |
| `McpServerReadOnly` | Block mutating tools and allow read-only operations only | `false` |
| `McpServerAllowedTools` | Optional allow-list of tool names (comma/semicolon/newline separated) | `""` |
| `McpServerDeniedTools` | Optional deny-list of tool names (comma/semicolon/newline separated) | `""` |
| `McpServerRateLimitEnabled` | Enable per-client request rate limiting | `false` |
| `McpServerRateLimitRequests` | Max requests allowed per client in each window | `120` |
| `McpServerRateLimitWindowSeconds` | Rate-limit window duration in seconds | `60` |
| `McpServerIncludeStatusInPing` | Include expanded health/status payload in `ping` response | `true` |
| `McpPermissionPolicy` | Auto-approve threshold for MCP tool permission prompts | `AllowReadOnly` |
| `McpDispatchMode` | Default thread dispatch mode for MCP tools without explicit affinity | `Direct` |

Changes take effect immediately - the server will start or stop based on the `McpServerEnabled` setting, and will restart on a new port if `McpServerPort` is changed while running.

### Command Line Arguments (Override)

Command-line arguments can be used to override preferences at startup:

```bash
XREngine.Editor.exe --mcp                                      # Enable MCP server (sets preference to true)
XREngine.Editor.exe --mcp --mcp-port 8080                      # Enable on custom port
XREngine.Editor.exe --mcp --mcp-allow-all                      # Enable unattended local tool calls
XREngine.Editor.exe --mcp --mcp-permission-policy AllowMutate  # Set a specific auto-approve threshold
```

> **Note:** Command-line arguments set the corresponding preferences, so the server state persists after startup.
> Use `--mcp-allow-all` only for trusted local automation, because it bypasses every MCP permission prompt.

### Default Endpoint

```
http://localhost:5467/mcp/
```

---

## Using with VS Code

### GitHub Copilot Integration

To use the XREngine MCP server with GitHub Copilot in VS Code, add the server configuration to your MCP settings:

1. Open VS Code Settings (`Ctrl+,`)
2. Search for "mcp" 
3. Click "Edit in settings.json" under **Github > Copilot > Chat: Mcp Servers**
4. Add the XREngine server configuration:

```json
{
  "github.copilot.chat.mcpServers": {
    "xrengine": {
      "type": "http",
      "url": "http://localhost:5467/mcp/"
    }
  }
}
```

Alternatively, create a `.vscode/mcp.json` file in your workspace:

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

### Verifying the Connection

1. Start XREngine Editor with the MCP server enabled
2. Open the Copilot Chat panel in VS Code (`Ctrl+Alt+I`)
3. Click the **Tools** icon (wrench) in the chat input
4. You should see the XREngine tools listed (e.g., `list_worlds`, `list_scene_nodes`)

### Example Usage

Once connected, you can ask Copilot to interact with the engine:

- *"List all scene nodes in the current world"*
- *"Create a new scene node called 'Player' and add a MeshComponent to it"*
- *"Move the node with ID xyz to position (10, 5, 0)"*
- *"Take a screenshot of the current viewport"*
- *"Capture the next 12 viewport frames and return a contact sheet"*

### In-Editor MCP Assistant (ImGui)

The editor includes an **ImGui tool window** named **MCP Assistant** that provides a
chat-style interface for interacting with AI providers while the editor is running.

**Opening:** **Tools → MCP Assistant** (also available from the unit-testing toolbar).

#### Features

| Feature | Details |
|---------|---------|
| **Chat history** | Scrollable conversation log with color-coded user/assistant messages, timestamps, and animated streaming indicators. |
| **Streaming responses** | Both OpenAI and Anthropic HTTP paths use SSE streaming — tokens appear in the chat log as they arrive. |
| **OpenAI Realtime WebSocket** | Toggle in provider settings to send prompts over the Realtime WebSocket API instead of standard HTTP. Also streams token-by-token. |
| **Provider selection** | **Codex (OpenAI)**, **Claude Code (Anthropic)**, **Gemini (Google)**, or **GitHub Models**; each provider exposes its own key/token and model fields. |
| **MCP server attachment** | Automatically syncs the local MCP server URL and auth token from Editor Preferences. The endpoint is sent as a tool/server reference in provider requests so the AI can call MCP tools. |
| **Hosted tool support (OpenAI Responses)** | OpenAI provider can expose hosted tools like `web_search_preview` and `image_generation` through the Responses API. Generated images are persisted under `McpCaptures/GeneratedImages/` when the API returns base64 image payloads. |
| **Workspace tool support** | Assistant function tools include `file_search` (workspace text search) and `apply_patch` (git-style unified diff apply via `git apply`) in addition to MCP editor tools. |
| **Collapsible settings** | Provider and MCP settings collapse into a header so the chat log gets maximum space. |
| **Max tokens** | Configurable per-request token limit (default 4 096). |
| **Completion marker protocol** | Prompts instruct the model to end only fully completed responses with `[[XRENGINE_ASSISTANT_DONE]]`, enabling deterministic continuation control. |
| **Auto re-prompt loop** | If no completion marker is emitted, the assistant automatically issues continuation prompts until completion or the configured max re-prompt limit is reached. |
| **Max auto re-prompts** | Configurable ceiling per user prompt to prevent unbounded continuation loops (default: 3). |
| **OpenAI context window awareness** | The assistant queries OpenAI model metadata and displays the selected model context window when available. |
| **Auto summarize near limit** | Optional context-pressure behavior that asks the model to emit a compact self-summary block before continuing, improving long-session continuity. |
| **Auto Camera View prompt guidance** | When enabled in MCP Assistant settings, system instructions nudge the model to keep camera framing on its active work area using camera-view MCP tools as scene-edit context shifts. |
| **Auto Camera focus pitch controls** | Editor Preferences expose **Focus Preferred Down Pitch** and **Focus Max Down Pitch** to tune auto-focus end pitch. Defaults are 20° preferred and 45° max downward tilt (never upward, never near straight-down). |
| **Schema-first mutation guidance** | System instructions bias the model to discover writable members with `get_component_schema`/`get_component_snapshot` before mutation, instead of guessing property names. |
| **Mutation read-back verification** | System instructions require read-back verification (`get_component_property`/`get_component_snapshot`) and screenshot verification for visual edits before reporting success. |
| **Edit menu** | Copy last response, copy full history, clear history. |
| **Auto-scroll** | Toggleable via **Settings** menu; keeps the chat log scrolled to the latest content during streaming. |

#### Environment variables

The **Load Keys from ENV** button reads:

- `OPENAI_API_KEY`
- `ANTHROPIC_API_KEY`
- `GEMINI_API_KEY`
- `GITHUB_TOKEN`

### Realtime Visual Context

When the MCP Assistant uses the OpenAI Realtime WebSocket path, the session exposes a built-in screenshot function tool named `request_view_screenshot` during active response generation.

- The model can request the current editor view.
- The request can target a specific camera via `camera_node_id` or `camera_name`.
- Results are returned immediately as both structured tool output and inline image input, so the model can continue reasoning against the fresh visual state in the same exchange.

---

## Protocol

The server accepts JSON-RPC 2.0 requests via HTTP POST at the MCP endpoint (`/mcp/`).

### Transport Endpoints

- `POST /mcp/` — JSON-RPC request/notification entrypoint.
- `GET /mcp/` — SSE stream endpoint (`Accept: text/event-stream`).
- `DELETE /mcp/` — Session termination when `Mcp-Session-Id` is supplied.

### Security and Limits

- If `McpServerRequireAuth` is enabled, requests must include:

```http
Authorization: Bearer <McpServerAuthToken>
```

- Browser-origin requests are checked against `McpServerCorsAllowlist`.
- Payloads larger than `McpServerMaxRequestBytes` are rejected.
- Requests that exceed `McpServerRequestTimeoutMs` are canceled.
- If `McpServerReadOnly` is enabled, mutating tools are blocked.
- `McpServerAllowedTools` and `McpServerDeniedTools` can be used to enforce per-tool policy.
- If `McpServerRateLimitEnabled` is enabled, per-client requests above quota return `429` with `Retry-After`.

### Permission Model

Each tool is classified by risk and checked against the configured auto-approve policy before invocation.

#### Permission levels

| Level | Value | Meaning |
|-------|-------|---------|
| `ReadOnly` | `0` | Queries, inspection, screenshots, and other no-side-effect operations |
| `Mutate` | `1` | In-memory state changes such as node creation or property edits |
| `Destructive` | `2` | Disk writes, deletes, world replacement, or other destructive operations |
| `Arbitrary` | `3` | Arbitrary method or expression execution |

#### Auto-approve policies

These policies are exposed in **Global Editor Preferences → MCP Server**.

| Policy | Effect |
|--------|--------|
| `AlwaysAsk` | Prompt for every tool call |
| `AllowReadOnly` | Auto-approve `ReadOnly`; prompt for `Mutate` and above. This is the default. |
| `AllowMutate` | Auto-approve `ReadOnly` and `Mutate`; prompt for `Destructive` and above |
| `AllowDestructive` | Prompt only for `Arbitrary` |
| `AllowAll` | Never prompt |

#### Resolution rules

- `McpToolRegistry.ResolvePermissionLevel()` uses explicit `XRMcp.Permission` metadata when present.
- If a tool omits explicit metadata, the registry falls back to a name-based heuristic, for example `get_` and `list_` default to `ReadOnly`, while `delete_` defaults to `Destructive`.
- `McpServerHost` checks the effective level before dispatch and enqueues an approval request when the current policy requires one.
- The ImGui permission prompt shows the tool name, description, arguments, and risk badge, and supports session-scoped remembered decisions per tool.

### Filesystem Sandboxing

- Script and game-asset tools are sandboxed to `Engine.Assets.GameAssetsPath`.
- Config file tools are sandboxed to the active project's `Config/` directory.
- Path traversal attempts and absolute paths outside the allowed roots are rejected.
- Read-only mode blocks write, delete, import, compile, and other mutating file-backed operations.

### Health Status Endpoint

The server exposes an optional status endpoint:

```
GET http://localhost:5467/mcp/status
```

The response includes protocol metadata, enabled methods, uptime, and active security/rate-limit configuration.

### Request Format

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "tool_name",
    "arguments": { ... },
    "idempotency_key": "optional-client-generated-key"
  }
}
```

The request body may be either:

- A single JSON-RPC request object
- A JSON-RPC batch array containing one or more request/notification objects

Requirements:

- `jsonrpc` should be `"2.0"`
- `method` must be a non-empty string
- for `tools/call`, `params` must be an object and `arguments` (if provided) must be an object

### Notification / No-Response Semantics

- Requests without an `id` are treated as JSON-RPC notifications and do not produce a JSON body response.
- If a request payload contains only notifications (or client responses), the server returns HTTP `202 Accepted` with no body.

### Session Header Support

- On an `initialize` request without `Mcp-Session-Id`, the server issues a new session ID in the `Mcp-Session-Id` response header.
- Requests that include `Mcp-Session-Id` are validated against active sessions.
- Unknown session IDs return HTTP `404 Not Found`.
- `DELETE /mcp/` with `Mcp-Session-Id` terminates that session and returns `204 No Content`.

### Server Notifications (SSE)

- Connected `GET /mcp/` SSE clients receive MCP JSON-RPC notifications when server tool visibility changes.
- Current notification method: `notifications/tools/list_changed`.
- Additional notification methods:
  - `notifications/resources/list_changed`
  - `notifications/prompts/list_changed`
- Notification payload includes a `reason` field identifying the triggering policy change.

### Tool Visibility and `tools/list`

- `tools/list` returns the effective tool set after server policy filters are applied.
- The following preferences can change visible tools at runtime:
  - `McpServerReadOnly`
  - `McpServerAllowedTools`
  - `McpServerDeniedTools`
- MCP capabilities advertise `tools.listChanged = true`, `resources.listChanged = true`, and `prompts.listChanged = true`.

### Built-in Methods

| Method         | Description                              |
|----------------|------------------------------------------|
| `initialize`   | Initialize the MCP connection            |
| `tools/list`   | List all available tools                 |
| `tools/call`   | Execute a specific tool                  |
| `resources/list` | List server resources                  |
| `resources/read` | Read a specific server resource        |
| `prompts/list` | List server prompts                      |
| `prompts/get`  | Get a specific server prompt             |
| `ping`         | Health check                             |

---

## Available Commands

For detailed documentation of each command including parameters and return values, see the API reference for <xref:XREngine.Editor.Mcp.EditorMcpActions>.

### Command Summary

| Command                      | Description                                    |
|------------------------------|------------------------------------------------|
| `list_worlds`                | List active world instances and scenes         |
| `list_scene_nodes`           | List scene nodes in the hierarchy              |
| `get_scene_node_info`        | Get detailed info about a scene node           |
| `create_scene_node`          | Create a new scene node                        |
| `delete_scene_node`          | Delete a scene node and its children           |
| `set_node_active`            | Enable/disable a scene node                    |
| `reparent_node`              | Move a node to a new parent                    |
| `set_transform`              | Set position, rotation, and scale              |
| `rotate_transform`           | Apply incremental rotation                     |
| `list_components`            | List components on a node                      |
| `add_component_to_node`      | Add a new component to a node                  |
| `get_component_snapshot`     | Read full component member snapshot            |
| `set_component_property`     | Set a property value on a component            |
| `set_component_properties`   | Set multiple properties on a component         |
| `assign_component_asset_property` | Assign asset refs to component members   |
| `find_asset`                 | Find assets by ID, path, or name              |
| `create_material_asset`      | Create and save XRMaterial assets              |
| `capture_viewport_screenshot`| Capture a screenshot from the viewport         |
| `start_viewport_sequence_capture` | Start a bounded asynchronous viewport frame sequence |
| `get_viewport_sequence_capture` | Poll sequence progress and artifact paths    |
| `list_viewport_sequence_captures` | List active and recent sequence sessions     |
| `cancel_viewport_sequence_capture` | Cancel and finalize a partial sequence       |
| `undo` / `redo`              | Apply editor undo or redo                      |
| `clear_selection`            | Clear current node selection                   |
| `delete_selected_nodes`      | Delete all selected nodes                      |
| `select_node_by_name`        | Select nodes by display name                   |
| `enter_play_mode` / `exit_play_mode` | Toggle play-mode transitions         |
| `create_primitive_shape`     | Create primitive nodes (cube/box/sphere/cone) |
| `save_world` / `load_world`  | Save or load world assets                      |
| `list_tools`                 | List MCP tools from inside a tool call         |

### Viewport Sequence Capture Sessions

Temporal captures use an asynchronous session so an MCP request does not remain open for the duration of capture and exceed the server's request timeout. Start a session, poll it by `capture_id`, and optionally cancel it. Only one sequence capture may be active for a given viewport at a time.

`start_viewport_sequence_capture` requires exactly one stop condition:

- `frame_count`: number of images to produce. With `frame_stride: 1` and the default `overflow_policy: "fail"`, the tool either captures subsequent render frames or reports that it could not preserve the sequence.
- `duration_seconds`: wall-clock duration, up to 60 seconds. `capture_fps` optionally limits sampling cadence; `max_frames` remains a hard output cap.

Both modes support camera/window/viewport targeting, `frame_stride`, post-readback `output_scale`, alpha preservation, bounded in-flight readbacks, automatic or fixed contact-sheet columns, and optional temporal image analysis. The start result returns immediately with a `capture_id` and unique output directory.

```json
{
  "name": "start_viewport_sequence_capture",
  "arguments": {
    "frame_count": 12,
    "frame_stride": 1,
    "max_in_flight_readbacks": 3,
    "overflow_policy": "fail",
    "output_scale": 0.5,
    "create_contact_sheet": true,
    "compute_frame_differences": true,
    "output_dir": "Build/_AgentValidation/20260720-temporal-artifact/mcp-captures"
  }
}
```

Each unique capture directory contains:

- `frame_000000.png`, `frame_000001.png`, and subsequent individual frames.
- `contact-sheet.png` when contact-sheet generation is enabled and at least one frame succeeds. Tiles are row-major; zero-based row/column coordinates are recorded per frame.
- `manifest.json`, atomically written after all in-flight readbacks drain. It includes render frame IDs, UTC and elapsed timestamps, render delta, camera pose, source/output dimensions, GPU readback format/byte count/queue slot, GPU-fence and CPU-conversion timings, MSAA-resolve use, SHA-256 hashes, mean luminance, black-pixel ratio, normalized differences from the previous frame, dropped-frame records, renderer queue diagnostics, warnings, and terminal state.

Configuration caps are 300 frames, 60 seconds for duration-based capture, 250 million output pixels, eight in-flight readbacks, 256 MiB of estimated queued readback storage, and a 64-megapixel contact sheet. `overflow_policy: "drop"` is opt-in and records every skipped eligible render frame; the default `fail` policy never silently breaks a consecutive sequence. Output filenames are capture-ID scoped and never overwrite an earlier session.

OpenGL uses its asynchronous PBO/fence path. Vulkan uses a renderer-owned ring of eight readback slots: the render thread records an image-to-buffer transfer on the graphics queue and returns immediately, the frame loop polls `vkGetFenceStatus`, and RGBA conversion plus all image resizing, PNG encoding, analysis, contact-sheet assembly, and manifest I/O run outside the render callback. The Vulkan path supports RGBA/BGRA 8-bit, 16-bit normalized/float, 32-bit float, and packed B10G11R11 color sources, restores the tracked source layout, resolves multisampled color targets into a bounded single-sample image, and reports the source format and timings per frame.

The Vulkan queue has both an eight-request limit and a 256 MiB raw in-flight budget. It uses the engine staging-buffer pool, preserves command-buffer resource lifetime pins until fence completion, and applies the same `overflow_policy` semantics as the session queue if another capture consumer occupies renderer slots. A fence still unsignaled after two seconds emits a diagnostic without waiting; after ten seconds its MCP consumer fails and the submitted slot remains quarantined until the fence signals or the renderer is destroyed. Device loss completes every pending consumer exactly once. There is no synchronous `vkWaitForFences`, `vkQueueWaitIdle`, CPU renderer, or OS-window fallback in the capture path.

For RenderDoc validation, `XRE_VULKAN_DIAGNOSTIC_PRESET=RenderDocFriendly` skips optional Streamline proxy provisioning when DLSS and DLSS-G are not requested because both systems interpose Vulkan entry points. An explicit DLSS or DLSS-G request remains strict and reports the incompatibility instead of silently changing the requested rendering path.

A GPU watchdog is the operating-system/driver recovery mechanism that detects GPU work which appears stuck for too long. On Windows this is commonly called Timeout Detection and Recovery (TDR). A blocking host wait does not itself make a copy command expensive, but an unbounded wait in the render path can freeze the editor while a genuinely stalled submission approaches TDR. The asynchronous queue keeps the CPU responsive and makes a slow or lost fence observable; it cannot rescue an invalid shader or GPU command that actually hangs the device.

### Tool Aliases (Backward Compatibility)

These aliases are accepted by `tools/call` and resolved to current tool names:

| Alias | Canonical Tool |
|-------|----------------|
| `get_scene_hierarchy` | `list_scene_nodes` |
| `select_scene_node` | `select_node_by_name` |
| `delete_selected` | `delete_selected_nodes` |

### Runtime Tool Registry (Generated)

Generated from `McpToolRegistry.Tools` via:

```powershell
pwsh Tools/Reports/generate_mcp_docs.ps1
```

<!-- MCP_TOOL_TABLE:START -->

| Tool | Description |
|------|-------------|
| `add_component_to_node` | Add a component to a scene node by type name. |
| `assign_component_asset_property` | Assign an asset reference to a component property or field (e.g., Material). |
| `bake_shape_components_to_model` | Bake ShapeMeshComponent nodes into one ModelComponent using boolean ops (union/intersect/difference/xor). |
| `batch_create_nodes` | Create multiple scene nodes in a single call. Each entry: {name, parent_id?, components?: string[], transform?: {x?,y?,z?,pitch?,yaw?,roll?,sx?,sy?,sz?}}. |
| `batch_set_properties` | Set properties on multiple components/nodes in one call. Each operation: {node_id, component_type?, property_name, value}. |
| `bulk_reparent_nodes` | Reparent multiple scene nodes to a new parent (or root) in one call. |
| `cancel_viewport_sequence_capture` | Cancel an active viewport sequence capture, drain in-flight readbacks, and finalize its partial manifest/contact sheet. |
| `capture_openxr_desktop_mirror_texture` | Capture the latest OpenXR desktop mirror texture and report pixel statistics. |
| `capture_openxr_eye_preview_texture` | Capture the latest OpenXR preview copy for the left or right eye and report pixel statistics. |
| `capture_render_pipeline_texture` | Capture a named live render-pipeline texture to PNG, EXR, or Radiance HDR and report pixel statistics. |
| `capture_viewport_screenshot` | Capture a screenshot from a viewport or camera for LLM context. |
| `clear_selection` | Clear the current scene-node selection. |
| `clone_scene` | Deep-clone a scene for experimentation. The clone is added to the world (hidden by default). |
| `compile_game_scripts` | Regenerate game project files, compile, and hot-reload the game DLL. Returns compilation result. |
| `cook_asset` | Cook/package an asset for optimized runtime loading. Creates a cooked binary file at the specified output location. |
| `copy_game_asset` | Copy a file within the game project's assets directory. |
| `create_asset` | Create a new typed engine asset (e.g., material, texture, animation) and save it to the game project's assets directory. |
| `create_material_asset` | Create and save a new XRMaterial asset. |
| `create_prefab_from_node` | Save a scene node hierarchy as a new prefab asset file. |
| `create_primitive_shape` | Create a visible primitive shape node with a default material in the active scene. |
| `create_scene` | Create a new scene in the active world. |
| `create_scene_node` | Create a scene node in the active world/scene. |
| `delete_game_asset` | Delete a file or directory from the game project's assets directory. |
| `delete_game_script` | Delete a .cs script file from the game project's assets directory. |
| `delete_scene` | Delete a scene from the active world. |
| `delete_scene_node` | Delete a scene node and its hierarchy. |
| `delete_selected_nodes` | Delete all currently selected scene nodes. |
| `diff_scene_nodes` | Diff two scene node hierarchies and return structural/property differences. |
| `dump_cpu_frame_profile` | Dump the latest CPU profiler frame snapshot to an LLM-readable log file in the current Build/Logs run directory. |
| `dump_gpu_render_pipeline_profile` | Dump retained GPU timing history for one render pipeline, or all captured pipelines when pipeline_name is omitted. |
| `duplicate_scene_node` | Duplicate a scene node (optionally with children). |
| `enter_play_mode` | Enter play mode. |
| `evaluate_expression` | Evaluate a dot-separated property chain expression on an XRBase object (e.g. 'Transform.WorldMatrix.Translation.X'). |
| `exit_play_mode` | Exit play mode. |
| `export_scene` | Export a scene asset to a directory. |
| `find_asset` | Find a project asset by ID, path, or name. |
| `find_nodes_by_name` | Find scene nodes by name (exact or contains). |
| `find_nodes_by_type` | Find scene nodes that have a component type. |
| `focus_node_in_view` | Focus the editor camera on a scene node. |
| `get_assembly_types` | List all types in a specific loaded assembly. |
| `get_asset_dependencies` | List all assets referenced/embedded by a given asset. Specify by asset GUID or file path. |
| `get_asset_info` | Get detailed info about a loaded asset by ID or path. |
| `get_asset_references` | Find all loaded assets and scene nodes that reference a given asset. Specify by asset GUID or file path. |
| `get_compile_errors` | Compile the game scripts and return any errors and warnings as structured data. |
| `get_compile_status` | Get the current compilation state of the game scripts: whether scripts are dirty, last binary path, compile-on-change status. |
| `get_component_events` | List events on a component with subscriber counts. |
| `get_component_property` | Get a component property or field value by name. |
| `get_component_schema` | Get detailed component type schema including properties and fields. |
| `get_component_snapshot` | Get a component snapshot including readable properties and fields. |
| `get_derived_types` | Find all types that derive from a given type across all loaded assemblies. |
| `get_editor_preferences` | Read all editor preferences (effective view: global base + project overrides merged). |
| `get_engine_settings` | Read engine configuration overview (user settings, timing, project info, runtime metrics). |
| `get_engine_state` | Get engine/editor play mode and high-level state flags. |
| `get_enum_values` | Get all named values for an enum type. |
| `get_game_asset_tree` | Get the full directory tree of the game project's assets directory as nested JSON. |
| `get_game_project_info` | Get game project metadata: project name, solution/binary paths, target framework, and loaded assembly state. |
| `get_game_settings` | Read the current game startup settings (networking, windows, timing, build, etc.). |
| `get_job_manager_state` | Get job manager queues, workers, and queue capacity. |
| `get_loaded_game_types` | List all types loaded from the game DLL plugin: components, menu items, and all exported types grouped by assembly. |
| `get_material_uniforms` | List all shader uniforms (Parameters) on a material, including names, types, and current values. Target the material by asset ID or via a component's Material property. |
| `get_method_info` | Get detailed method signature including parameters, return type, generic constraints, and attributes. |
| `get_node_world_transform` | Get a scene node's world transform (translation, rotation, scale). |
| `get_object_properties` | Read all property values from any XRBase-derived instance by GUID. |
| `get_parent_types` | Walk the inheritance chain upward from a type, including interfaces. |
| `get_prefab_structure` | Get the node hierarchy for a prefab source or variant. |
| `get_render_capabilities` | Get renderer capability flags (GPU, extensions, ray tracing). |
| `get_render_profiler_stats` | Return the latest render-profiler counters, including Vulkan frame lifecycle timings and command-buffer cache state. |
| `get_render_state` | Get current rendering pipeline and camera state. |
| `get_scene_node_info` | Get detailed info about a scene node, including transform and components. |
| `get_scene_statistics` | Get scene statistics including node and component counts. |
| `get_selection` | Get the currently selected scene nodes. |
| `get_texture_streaming_summary` | Return imported texture streaming summary telemetry, including Vulkan freeze and generation state. |
| `get_time_state` | Get timing, delta, and target frequency information. |
| `get_transform_decomposed` | Get local/world/render translation, rotation, and scale for a scene node. |
| `get_transform_matrices` | Get local/world/render matrices for a scene node. |
| `get_type_hierarchy_tree` | Get a full inheritance tree rooted at a type as nested JSON. Supports up/down/both direction. |
| `get_type_info` | Get full type metadata (name, namespace, base type, interfaces, flags) for any loaded type. |
| `get_type_members` | Get properties, fields, methods, events, and constructors from any loaded type. |
| `get_undo_history` | Get undo/redo history entries. |
| `get_viewport_sequence_capture` | Get viewport sequence capture progress, terminal state, artifact paths, and optionally per-frame metadata. |
| `get_vulkan_frame_op_trace` | Return the latest Vulkan frame-op trace snapshot. Requires launching with XRE_VULKAN_FRAMEOP_TRACE=1. |
| `import_scene` | Import a scene asset from disk and add it to the active world. |
| `import_third_party_asset` | Import a third-party file (GLTF, FBX, OBJ, PNG, WAV, etc.) into game assets using the engine's import pipeline. |
| `instantiate_prefab` | Instantiate a prefab into the active scene by asset ID or path. |
| `invoke_method` | Invoke a method on an XRBase instance (by GUID) or a static method on any type. |
| `list_active_jobs` | List jobs currently executing. |
| `list_assemblies` | List all loaded assemblies in the current AppDomain. |
| `list_asset_import_options` | List third-party import options for a source asset path. |
| `list_components` | List components on a scene node. |
| `list_component_types` | List available component types and metadata. |
| `list_enums` | List enum types from loaded assemblies, optionally filtered by namespace or assembly. |
| `list_game_assets` | List files and folders in the game project's assets directory with optional filtering. |
| `list_game_configs` | List config files in the current project's Config/ directory. |
| `list_game_scripts` | List all .cs files in the game project's assets directory. |
| `list_input_devices` | List available input devices and connection state. |
| `list_layers` | List known layers and layers used in the active world. |
| `list_loaded_assets` | List assets currently loaded by the asset manager. |
| `list_local_players` | List local player controllers, viewports, and input presence. |
| `list_prefabs` | List loaded prefab assets. |
| `list_render_pipeline_resources` | List live render-pipeline textures and framebuffers for the selected viewport. |
| `list_scenes` | List scenes in the active world. |
| `list_scene_nodes` | List scene nodes in the active world/scene. |
| `list_tags` | List tags on a node or across the active world. |
| `list_texture_streaming_textures` | List imported texture streaming texture telemetry rows for live residency validation. |
| `list_tools` | List all MCP tools currently registered by the editor. |
| `list_transform_children` | List immediate child transforms for a scene node. |
| `list_transform_types` | List available transform types. |
| `list_ui_viewport_diagnostics` | List hidden/editor UI viewport components and their offscreen render command state. |
| `list_viewport_sequence_captures` | List active and recently completed viewport sequence captures without per-frame payloads. |
| `list_vulkan_image_allocation_diagnostics` | List live Vulkan image allocation sizes by debug name for VRAM pressure diagnostics. |
| `list_worlds` | List active world instances and their scenes. |
| `load_world` | Load a world asset and set it as active on the current world instance. |
| `move_node_sibling` | Reorder a scene node among siblings. |
| `prefab_apply_overrides` | Apply an instance's recorded prefab overrides back to its source prefab asset. |
| `prefab_revert_overrides` | Revert recorded prefab overrides on an instance by restoring source prefab values. |
| `probe_editor_depth_hit` | Probe the editor camera pawn depth-hit state at a normalized viewport coordinate. |
| `probe_render_pipeline_depth` | Read depth from a named render-pipeline FBO at a normalized viewport coordinate. |
| `query_references` | Query references for assets, scene nodes, and components by GUID. |
| `read_game_asset` | Read the raw text contents of a file from the game project's assets directory (.asset, .json, .xml, .yaml, .cs, etc.). |
| `read_game_config` | Read the contents of a config file from the project's Config/ directory. |
| `read_game_script` | Read the contents of a .cs script file from the game project's assets directory. |
| `redo` | Redo the most recently undone editor change. |
| `reload_asset` | Force-reload an asset from disk after external edits. Specify by asset GUID or file path. |
| `remove_component` | Remove a component from a scene node. |
| `rename_game_asset` | Rename or move a file or directory within the game project's assets directory. |
| `rename_game_script` | Rename or move a .cs script file within the game project's assets directory. |
| `rename_scene_node` | Rename a scene node by ID. |
| `reparent_node` | Reparent a scene node to a new parent. |
| `restore_world_state` | Restore the active world from a previously captured snapshot. |
| `rotate_transform` | Apply a local rotation to a scene node's transform (degrees). |
| `run_editor_command` | Execute an allowlisted editor command via MCP (undo/redo/selection/play-mode/save/load/focus/select). |
| `save_world` | Save the active world asset to disk. |
| `scaffold_component` | Generate a new XRComponent subclass from a template. Creates a .cs file with backing fields, SetField pattern, lifecycle hooks, and Description attribute. |
| `scaffold_game_mode` | Generate a new game mode class from template. Creates a .cs file extending GameMode<T> with standard lifecycle methods. |
| `search_types` | Search across all loaded types by name pattern (contains, regex, or exact match). |
| `select_node` | Select one or more scene nodes in the editor. |
| `select_node_by_name` | Select scene nodes by display name. |
| `set_active_scene` | Set a scene as active (first in scene list). |
| `set_asset_import_options` | Set a third-party import option property and save it. |
| `set_compile_on_change` | Toggle CodeManager.CompileOnChange: when enabled, game scripts auto-compile when .cs files change and editor regains focus. |
| `set_component_properties` | Set multiple component properties/fields in one call. |
| `set_component_property` | Set a component property or field value by name. |
| `set_editor_camera_render_on_demand` | Set render-on-demand for the active editor camera pawn and optionally invalidate the viewport. |
| `set_editor_camera_view` | Set the editor camera view with interpolation using position plus look-at or Euler rotation. |
| `set_editor_preference` | Set an editor preference by property name or dotted path (writes to the global default). |
| `set_game_setting` | Set a game startup setting by property name. |
| `set_layer` | Set the layer for a scene node. |
| `set_material_uniform` | Set a shader uniform value on a material by uniform name. Supports float, int, uint, vec2 ({X,Y}), vec3 ({X,Y,Z}), vec4 ({X,Y,Z,W}). Target by material asset ID or via a component's Material property. |
| `set_material_uniforms` | Set multiple shader uniforms on a material in one call. Pass a map of uniform_name -> value. |
| `set_node_active` | Set whether a scene node is active in the hierarchy. |
| `set_node_active_recursive` | Set active state on a node and its children. |
| `set_node_transform` | Set a scene node transform (translation, rotation, scale). |
| `set_node_world_transform` | Set a scene node world transform (translation, rotation, scale). |
| `set_object_property` | Set a property on any XRBase instance by GUID (uses SetField pipeline). |
| `set_tag` | Assign or remove a tag on a scene node. |
| `set_transform` | Set a scene node transform (translation, rotation, scale). |
| `snapshot_world_state` | Capture an in-memory snapshot of the active world state for later restore. |
| `start_viewport_sequence_capture` | Start a bounded asynchronous viewport frame-sequence capture with manifest, contact sheet, timing, camera, and image-difference metadata. |
| `sweep_render_pipeline_depth` | Read depth from every live render-pipeline FBO that has a depth/stencil attachment at a normalized viewport coordinate. |
| `toggle_scene_visibility` | Toggle scene visibility. |
| `transaction_begin` | Begin an MCP transaction by capturing a rollback snapshot of the current world state. |
| `transaction_commit` | Commit an MCP transaction and discard its rollback snapshot. |
| `transaction_rollback` | Rollback an MCP transaction by restoring the snapshot captured at begin. |
| `undo` | Undo the most recent editor change. |
| `validate_scene` | Validate a scene for common hierarchy issues. |
| `validate_scene_integrity` | Perform deep integrity validation on a scene hierarchy (null roots, parent mismatches, cycles, duplicate IDs, world binding issues). |
| `watch_property` | Watch a property for changes on an XRBase object. Returns a watch_id for polling, or retrieves accumulated changes when watch_id is provided. |
| `write_game_asset` | Write or overwrite a text asset file in the game project's assets directory. |
| `write_game_config` | Write (create or overwrite) a config file in the project's Config/ directory. |
| `write_game_script` | Write or create a .cs script file in the game project's assets directory. Optionally triggers immediate compilation. |
| `zoom_editor_camera_at_depth_hit` | Apply editor camera scroll zoom at a normalized viewport coordinate using the pawn depth-hit mechanic. |
<!-- MCP_TOOL_TABLE:END -->

---

## Response Format

All tool responses follow this structure:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "content": [
      {
        "type": "text",
        "text": "Human-readable message describing the result"
      },
      {
        "type": "text",
        "text": "{ /* JSON-serialized structured data, present only when the tool returns data */ }"
      }
    ],
    "isError": false,
    "structuredContent": { /* Optional structured data */ },
    "data": { /* Optional structured data (legacy mirror of structuredContent) */ }
  }
}
```

| Field               | Type      | Description                                                                                     |
|---------------------|-----------|-------------------------------------------------------------------------------------------------|
| `content`           | `array`   | Content blocks. The first text block is the human-readable message; when a tool returns structured data, a second text block carries that data as JSON so spec-compliant clients (which only read `content`) can see it. |
| `isError`           | `boolean` | `true` if the operation failed                                                                  |
| `structuredContent` | `object`  | Optional structured data, populated for MCP clients that consume `structuredContent`            |
| `data`              | `object`  | Optional structured data; legacy mirror of `structuredContent` kept for existing integrations   |


---

## Error Handling

### JSON-RPC Error Codes

| Code   | Meaning                          |
|--------|----------------------------------|
| -32600 | Invalid request (missing method) |
| -32601 | Method not found                 |
| -32602 | Invalid params                   |
| -32000 | Server error (no active world)   |

### Error Response Example

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "error": {
    "code": -32601,
    "message": "Tool 'unknown_tool' not found."
  }
}
```

### Tool-Level Errors

When a tool executes but encounters an error, `isError` is set to `true`:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "content": [
      {
        "type": "text",
        "text": "Scene node 'invalid-guid' not found."
      }
    ],
    "isError": true,
    "structuredContent": null,
    "data": null
  }
}
```

---

## Architecture

The MCP implementation consists of the following classes:

| Class                  | Description                                    |
|------------------------|------------------------------------------------|
| <xref:XREngine.Editor.Mcp.McpServerHost>        | HTTP server handling JSON-RPC requests         |
| <xref:XREngine.Editor.Mcp.McpToolRegistry>      | Tool discovery and invocation                  |
| <xref:XREngine.Editor.Mcp.McpToolDefinition>    | Tool metadata and handler definition           |
| <xref:XREngine.Editor.Mcp.McpToolContext>       | Execution context passed to tool handlers      |
| <xref:XREngine.Editor.Mcp.McpToolResponse>      | Standard response structure                    |
| <xref:XREngine.Editor.Mcp.McpWorldResolver>     | Resolves active world instance                 |
| <xref:XREngine.Editor.Mcp.EditorMcpActions>     | Tool implementations (partial class)           |
| `ViewportSequenceCaptureManager`                | Active/recent capture registry and per-viewport exclusivity |
| `ViewportSequenceCaptureSession`                | Bounded frame scheduling, readback draining, and artifact finalization |

### Tool Organization

The tool surface is split across focused `EditorMcpActions.*.cs` partials under `XREngine.Editor/Mcp/Actions/`.

| File | Primary responsibility |
|------|------------------------|
| `EditorMcpActions.TypeSystem.cs` | Runtime type inspection and discovery |
| `EditorMcpActions.Scripting.cs` | Game script CRUD, compilation, and scaffolding |
| `EditorMcpActions.GameAssets.cs` | Game asset file-system and engine-aware asset operations |
| `EditorMcpActions.LiveInspection.cs` | XRBase instance reflection, mutation, and method invocation |
| `EditorMcpActions.Settings.cs` | Game, editor, and engine settings/configuration |
| `EditorMcpActions.SceneAuthoring.cs` | Prefab workflows, batch scene edits, and scene cloning |
| `EditorMcpActions.ExtendedWorkflow.cs` | Integrity checks, snapshots, transactions, reference queries, and editor commands |
| `EditorMcpActions.ViewportSequence.cs` | Start/status/list/cancel lifecycle for temporal viewport captures |

### Adding New Tools

To add a new MCP tool:

1. Add a method to `EditorMcpActions` (in appropriate partial class file)
2. Decorate with `[XRMcp(...)]` and set method metadata there:
  - `Name = "tool_name"`
  - optional `Permission = McpPermissionLevel.ReadOnly|Mutate|Destructive|Arbitrary`
  - optional `PermissionReason = "..."`
3. Use `[Description("...")]` for tool description
4. Use `[McpName("param_name")]` and `[Description("...")]` on parameters
5. Add XML documentation summary for docfx
6. Return `Task<McpToolResponse>`

Additional contributor conventions:

- Prefer the most specific existing partial file for the tool category rather than expanding unrelated files.
- Use `CancellationToken` for async or potentially long-running I/O.
- File-backed tools must preserve the path sandbox guarantees described above.
- Tools that execute arbitrary methods or expressions should be tagged with `McpPermissionLevel.Arbitrary` and a clear `PermissionReason`.

```csharp
/// <summary>
/// Description of what the tool does for docfx documentation.
/// </summary>
/// <param name="context">The MCP tool execution context.</param>
/// <param name="param1">Description of first parameter.</param>
/// <param name="param2">Description of optional parameter.</param>
/// <returns>Description of return value.</returns>
[XRMcp(Name = "my_new_tool", Permission = McpPermissionLevel.ReadOnly)]
[Description("Description of what the tool does.")]
public static Task<McpToolResponse> MyNewToolAsync(
    McpToolContext context,
    [McpName("param1"), Description("First parameter.")] string param1,
    [McpName("param2"), Description("Optional parameter.")] int? param2 = null)
{
    // Implementation
    return Task.FromResult(new McpToolResponse("Success message.", new { result = "data" }));
}
```

---

## License

See the main project [LICENSE](../../../LICENSE) file for licensing information.
