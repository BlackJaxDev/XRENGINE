using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine.Editor.Mcp
{
    /// <summary>
    /// Defines an MCP tool's metadata and invocation handler.
    /// </summary>
    /// <remarks>
    /// Creates a new tool definition.
    /// </remarks>
    /// <param name="name">The tool name as exposed to MCP clients.</param>
    /// <param name="description">A description of what the tool does.</param>
    /// <param name="inputSchema">JSON Schema describing the tool's input parameters.</param>
    /// <param name="handler">The async function that handles tool invocation.</param>
    public sealed class McpToolDefinition(
        string name,
        string description,
        object inputSchema,
        Func<McpToolContext, JsonElement, CancellationToken, Task<McpToolResponse>> handler)
    {
        /// <summary>
        /// The tool name as exposed to MCP clients (e.g., "list_scene_nodes").
        /// </summary>
        public string Name { get; } = name;

        /// <summary>
        /// A description of what the tool does, shown to MCP clients.
        /// </summary>
        public string Description { get; } = description;

        /// <summary>
        /// JSON Schema describing the tool's input parameters.
        /// </summary>
        public object InputSchema { get; } = inputSchema;

        /// <summary>
        /// The async function that handles tool invocation.
        /// </summary>
        public Func<McpToolContext, JsonElement, CancellationToken, Task<McpToolResponse>> Handler { get; } = handler;
    }
}
