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

Start the editor, enable the server, then check the client tool picker for XRENGINE tools such as `list_worlds`, `list_scene_nodes`, and `capture_viewport_screenshot`.

## In-Editor Assistant

The ImGui editor includes **Tools > MCP Assistant**. It can use provider keys from editor preferences or environment variables such as `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `GEMINI_API_KEY`, and `GITHUB_TOKEN`.

For scene or material edits, prefer prompts that ask the assistant to inspect the current world, make a bounded change, and verify with read-back or a viewport screenshot.

## Safety Notes

- Use read-only mode for inspection-only sessions.
- Require auth when exposing the server beyond trusted local processes.
- Keep mutating and destructive tools behind prompts unless you are running controlled automation.
- Use allowed/denied tool lists for constrained workflows.

## Deeper Docs

- [MCP Server Implementation](../../developer-guides/ai/mcp-server.md)
- [MCP Assistant Developer Guide](../../developer-guides/ai/mcp-assistant.md)
