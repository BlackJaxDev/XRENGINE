namespace XREngine.ControlPlane;

/// <summary>Keeps a downloaded immutable package out of cache eviction while a native client uses its assets.</summary>
public sealed class RemoteWorldPackageLease(string path, FileStream lease) : IDisposable
{
    public string Path { get; } = path;
    public void Dispose() => lease.Dispose();
}
