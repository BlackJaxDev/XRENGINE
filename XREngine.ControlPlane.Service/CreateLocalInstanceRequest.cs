namespace XREngine.ControlPlane.Service;

/// <summary>Public create request; executable, filesystem paths and worker credentials are operator-owned.</summary>
public sealed class CreateLocalInstanceRequest
{
    public string OperationId { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int MaxPlayers { get; set; } = 4;
    public bool IsPublic { get; set; } = true;
}
