using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using XREngine.Data;

namespace XREngine.Rendering;

/// <summary>
/// Computes cold-path identity fields for a renderer module assembly.
/// </summary>
public static class RendererBackendModuleIdentity
{
    public static string GetBuildHash(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string location = assembly.Location;
        if (string.IsNullOrWhiteSpace(location))
            return string.Empty;

        RuntimeAssetReadServices.EnsureHostFileAccess("Renderer module assembly hash");
        IRuntimeHostFileReadBackend files = RuntimeFileDiscoveryServices.CaptureHostFileReadBackend();
        if (!files.FileExists(location))
            return string.Empty;

        using Stream stream = files.OpenRead(location, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string GetTargetFramework(Assembly assembly)
        => assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? string.Empty;

    public static Architecture ProcessArchitecture => RuntimeInformation.ProcessArchitecture;
}
