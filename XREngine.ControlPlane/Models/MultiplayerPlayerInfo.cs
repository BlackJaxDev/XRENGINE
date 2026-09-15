namespace XREngine.ControlPlane;

/// <summary>
/// Represents information about a player in a multiplayer instance.
/// </summary>
public sealed class MultiplayerPlayerInfo
{
    /// <summary>
    /// Gets or sets the unique identifier of the client associated with the player.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the display name of the player.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp when the player joined the multiplayer instance.
    /// </summary>
    public DateTimeOffset JoinedUtc { get; set; }

    /// <summary>
    /// Gets or sets the metadata associated with the player.
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
}
