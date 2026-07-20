# MCP Server And Assistant

[Back to user guide](../README.md)

The editor MCP server lets AI assistants and external tools inspect and modify the active XRENGINE editor world through HTTP JSON-RPC. Use this page to enable and operate it. For protocol and implementation details, see [MCP Server Implementation](../../developer-guides/ai/mcp-server.md).

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

## Command Line

You can also launch the editor with MCP enabled:

```powershell
XREngine.Editor.exe --mcp
XREngine.Editor.exe --mcp --mcp-port 8080
XREngine.Editor.exe --mcp --mcp-allow-all
```

Use `--mcp-allow-all` only for trusted local automation because it bypasses permission prompts.

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

## Safety Notes

- Use read-only mode for inspection-only sessions.
- Require auth when exposing the server beyond trusted local processes.
- Keep mutating and destructive tools behind prompts unless you are running controlled automation.
- Use allowed/denied tool lists for constrained workflows.

## Deeper Docs

- [MCP Server Implementation](../../developer-guides/ai/mcp-server.md)
- [MCP Assistant Developer Guide](../../developer-guides/ai/mcp-assistant.md)
