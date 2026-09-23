using System.Text.Json.Serialization;

namespace XREngine.LocalAgentBroker;

internal sealed record AgentSwarmProposedChange
{
    [JsonRequired]
    public string Path { get; init; } = string.Empty;
    [JsonRequired, JsonPropertyName("base_sha256")]
    public string BaseSha256 { get; init; } = string.Empty;
    [JsonRequired, JsonPropertyName("old_text")]
    public string OldText { get; init; } = string.Empty;
    [JsonRequired, JsonPropertyName("new_text")]
    public string NewText { get; init; } = string.Empty;
}
