using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine.Editor.Mcp
{
    public sealed class McpToolDefinition
    {
        public McpToolDefinition(
            string name,
            string description,
            object inputSchema,
            Func<McpToolContext, JsonElement, CancellationToken, Task<McpToolResponse>> handler)
        {
            Name = name;
            Description = description;
            InputSchema = inputSchema;
            Handler = handler;
        }

        public string Name { get; }
        public string Description { get; }
        public object InputSchema { get; }
        public Func<McpToolContext, JsonElement, CancellationToken, Task<McpToolResponse>> Handler { get; }
    }
}
