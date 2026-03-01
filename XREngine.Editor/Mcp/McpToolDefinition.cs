using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Data.Core;

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
    /// <param name="permissionLevel">The permission level required to execute this tool.</param>
    /// <param name="permissionReason">Optional human-readable reason shown in the permission prompt.</param>
    /// <param name="threadAffinity">Optional explicit thread affinity for dispatching this tool.</param>
    public sealed class McpToolDefinition(
        string name,
        string description,
        object inputSchema,
        Func<McpToolContext, JsonElement, CancellationToken, Task<McpToolResponse>> handler,
        McpPermissionLevel permissionLevel = McpPermissionLevel.ReadOnly,
        string? permissionReason = null,
        McpThreadAffinity? threadAffinity = null)
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

        /// <summary>
        /// The permission level required to execute this tool.
        /// Controls whether the user is prompted before execution.
        /// </summary>
        public McpPermissionLevel PermissionLevel { get; } = permissionLevel;

        /// <summary>
        /// Optional human-readable reason shown in the permission prompt
        /// explaining why this tool requires elevated permission.
        /// </summary>
        public string? PermissionReason { get; } = permissionReason;

        /// <summary>
        /// Optional explicit thread affinity for this tool.
        /// When null, the global <c>McpDispatchMode</c> setting determines dispatch behavior.
        /// When set, overrides the global default.
        /// </summary>
        public McpThreadAffinity? ThreadAffinity { get; } = threadAffinity;
    }
}
