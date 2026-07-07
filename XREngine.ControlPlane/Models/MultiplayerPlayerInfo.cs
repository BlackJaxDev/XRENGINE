namespace XREngine.ControlPlane;

public sealed class MultiplayerPlayerInfo
{
    public string ClientId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public DateTimeOffset JoinedUtc { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = [];
}
