namespace XREngine.ControlPlane;

public sealed class LeaveMultiplayerInstanceRequest
{
    public string InstanceId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
}
