using System.Text.Json.Nodes;

namespace XREngine.LocalAgentBroker;

/// <summary>
/// One tool exposed by the broker's stdio MCP surface.
/// </summary>
internal sealed record McpToolSpec
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required JsonObject InputSchema { get; init; }

    public bool IsReadOnly { get; init; }
}
