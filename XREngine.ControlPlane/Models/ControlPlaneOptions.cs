namespace XREngine.ControlPlane;

public sealed class ControlPlaneOptions
{
    public string DefaultProtocolVersion { get; set; } = "dev";
    public int DefaultMaxPlayers { get; set; } = 8;
    public int TokenByteLength { get; set; } = 32;
    public string DefaultMulticastGroup { get; set; } = "239.0.0.222";
    public int DefaultMulticastPort { get; set; } = 5000;
}
