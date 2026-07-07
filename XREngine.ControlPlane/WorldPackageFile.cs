namespace XREngine.ControlPlane;

public sealed class WorldPackageFile
{
    public string RelativePath { get; set; } = string.Empty;
    public long Length { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}
