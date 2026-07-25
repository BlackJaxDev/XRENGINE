using System.Reflection;
using System.Runtime.Loader;

namespace XREngine.Editor.HotReload;

internal sealed class RendererBackendLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> SharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "XREngine.Runtime.Rendering",
        "XREngine.Runtime.Core",
        "XREngine.Data",
        "XREngine.Extensions",
    };

    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _generationDirectory;

    public RendererBackendLoadContext(string entryAssemblyPath, long generation)
        : base($"renderer-backend-{generation}", isCollectible: true)
    {
        _resolver = new(entryAssemblyPath);
        _generationDirectory = Path.GetDirectoryName(entryAssemblyPath)
            ?? throw new ArgumentException("The backend entry assembly must have a directory.", nameof(entryAssemblyPath));
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string name = assemblyName.Name ?? string.Empty;
        if (SharedAssemblyNames.Contains(name))
        {
            Assembly? shared = Default.Assemblies.FirstOrDefault(
                assembly => string.Equals(
                    assembly.GetName().Name,
                    name,
                    StringComparison.OrdinalIgnoreCase));
            return shared ?? throw new FileLoadException(
                $"Shared renderer contract assembly '{name}' is not loaded in the default context.");
        }

        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is null)
            return null;

        EnsureUnderGenerationDirectory(path);
        return LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (path is null)
            return nint.Zero;

        EnsureUnderGenerationDirectory(path);
        RendererNativeDependencyRegistry.ValidateAndRecord(path);
        return LoadUnmanagedDllFromPath(path);
    }

    private void EnsureUnderGenerationDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_generationDirectory)) +
            Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new FileLoadException(
                $"Renderer module dependency '{fullPath}' resolved outside the approved generation directory '{root}'.");
        }
    }

    public static bool IsSharedAssemblyName(string fileName)
        => SharedAssemblyNames.Contains(Path.GetFileNameWithoutExtension(fileName));
}
