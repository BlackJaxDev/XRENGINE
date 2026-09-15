namespace XREngine.ControlPlane;

/// <summary>Progress reported while copying a verified world package into a local cache or worker staging directory.</summary>
public sealed class WorldPackageStagingProgress
{
    public string RelativePath { get; init; } = string.Empty;
    public long BytesTransferred { get; init; }
    public long TotalBytes { get; init; }
}
