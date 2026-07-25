using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

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
        if (string.IsNullOrWhiteSpace(location) || !File.Exists(location))
            return string.Empty;

        using FileStream stream = File.OpenRead(location);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string GetTargetFramework(Assembly assembly)
        => assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? string.Empty;

    public static Architecture ProcessArchitecture => RuntimeInformation.ProcessArchitecture;
}

