# XREngine MCP (Model Context Protocol) Server

This document describes the MCP server implementation in XREngine Editor, which enables AI assistants and external tools to interact with the engine through a standardized JSON-RPC 2.0 protocol.

## Overview

The XREngine MCP Server exposes the editor's functionality via HTTP, allowing external tools (such as AI coding assistants) to:

- Query and manipulate the scene hierarchy
- Create, modify, and delete scene nodes
- Add and configure components
- Modify transforms (position, rotation, scale)
- Capture viewport screenshots
- List worlds and scenes

The server implements the [Model Context Protocol](https://modelcontextprotocol.io/) specification (version `2024-11-05`).

---

## Starting the Server

The MCP server can be enabled through Editor Preferences or via command-line arguments.

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

Changes take effect immediately - the server will start or stop based on the `McpServerEnabled` setting, and will restart on a new port if `McpServerPort` is changed while running.

### Command Line Arguments (Override)

Command-line arguments can be used to override preferences at startup:

```bash
XREngine.Editor.exe --mcp                    # Enable MCP server (sets preference to true)
XREngine.Editor.exe --mcp --mcp-port 8080    # Enable on custom port
```

> **Note:** Command-line arguments set the corresponding preferences, so the server state persists after startup.

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

---

## Protocol

The server accepts JSON-RPC 2.0 requests via HTTP POST.

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

Requirements:

- `jsonrpc` should be `"2.0"`
- `method` must be a non-empty string
- for `tools/call`, `params` must be an object and `arguments` (if provided) must be an object

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
| `set_component_property`     | Set a property value on a component            |
| `capture_viewport_screenshot`| Capture a screenshot from the viewport         |
| `undo` / `redo`              | Apply editor undo or redo                      |
| `clear_selection`            | Clear current node selection                   |
| `delete_selected_nodes`      | Delete all selected nodes                      |
| `select_node_by_name`        | Select nodes by display name                   |
| `enter_play_mode` / `exit_play_mode` | Toggle play-mode transitions         |
| `create_primitive_shape`     | Create primitive nodes (cube/box/sphere/cone) |
| `save_world` / `load_world`  | Save or load world assets                      |
| `list_tools`                 | List MCP tools from inside a tool call         |

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
      }
    ],
    "isError": false,
    "data": { /* Optional structured data */ }
  }
}
```

| Field     | Type      | Description                                         |
|-----------|-----------|-----------------------------------------------------|
| `content` | `array`   | Array of content blocks (currently text only)       |
| `isError` | `boolean` | `true` if the operation failed                      |
| `data`    | `object`  | Optional structured data returned by the tool       |

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

### Adding New Tools

To add a new MCP tool:

1. Add a method to `EditorMcpActions` (in appropriate partial class file)
2. Decorate with `[XRMcp]` attribute
3. Use `[McpName("tool_name")]` to set the tool name
4. Use `[Description("...")]` for tool description
5. Use `[McpName("param_name")]` and `[Description("...")]` on parameters
6. Add XML documentation summary for docfx
7. Return `Task<McpToolResponse>`

```csharp
/// <summary>
/// Description of what the tool does for docfx documentation.
/// </summary>
/// <param name="context">The MCP tool execution context.</param>
/// <param name="param1">Description of first parameter.</param>
/// <param name="param2">Description of optional parameter.</param>
/// <returns>Description of return value.</returns>
[XRMcp]
[McpName("my_new_tool")]
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

See the main project [LICENSE](../../LICENSE) file for licensing information.
