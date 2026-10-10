using System.Runtime.CompilerServices;
using XREngine.Core.Files;

namespace XREngine.Rendering;

/// <summary>Identifies one shader file and asset discovery installation.</summary>
internal readonly struct ShaderSourceProviderOwner : IEquatable<ShaderSourceProviderOwner>
{
    public ShaderSourceProviderOwner(
        IShaderSourceFileBackend? fileBackend,
        int fileBackendGeneration,
        IAssetFileSystem? fileSystem,
        long fileSystemGeneration,
        bool hostFileAccess)
    {
        FileBackend = fileBackend;
        FileBackendGeneration = fileBackendGeneration;
        FileSystem = fileSystem;
        FileSystemGeneration = fileSystemGeneration;
        HostFileAccess = hostFileAccess;
    }

    public IShaderSourceFileBackend? FileBackend { get; }
    public int FileBackendGeneration { get; }
    public IAssetFileSystem? FileSystem { get; }
    public long FileSystemGeneration { get; }
    public bool HostFileAccess { get; }

    public bool Equals(ShaderSourceProviderOwner other)
        => ReferenceEquals(FileBackend, other.FileBackend)
        && FileBackendGeneration == other.FileBackendGeneration
        && ReferenceEquals(FileSystem, other.FileSystem)
        && FileSystemGeneration == other.FileSystemGeneration
        && HostFileAccess == other.HostFileAccess;

    public override bool Equals(object? obj) => obj is ShaderSourceProviderOwner other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(
            FileBackend is null ? 0 : RuntimeHelpers.GetHashCode(FileBackend),
            FileBackendGeneration,
            FileSystem is null ? 0 : RuntimeHelpers.GetHashCode(FileSystem),
            FileSystemGeneration,
            HostFileAccess);
}
