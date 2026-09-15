namespace XREngine.Runtime.Bootstrap;

/// <summary>Service-owned package selection and capacity request for a new local managed instance.</summary>
public sealed class ManagedServiceCreateInstanceRequest
{
    public string OperationId { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int MaxPlayers { get; set; } = 4;
    public bool IsPublic { get; set; } = true;
}
