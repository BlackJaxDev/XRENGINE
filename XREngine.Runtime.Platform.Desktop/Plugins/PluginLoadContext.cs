using System.Reflection;
using System.Runtime.Loader;

namespace XREngine.Runtime.Platform.Desktop.Plugins;

/// <summary>
/// Loads one plugin and its private dependencies. Assemblies the host ships are shared from the default context,
/// so engine types keep one identity across the host and every plugin.
/// </summary>
internal sealed class PluginLoadContext(string name, string entryAssemblyPath) : AssemblyLoadContext($"Plugin:{name}")
{
    private readonly AssemblyDependencyResolver _resolver = new(entryAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsHostAssembly(assemblyName))
            return null;

        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    private static bool IsHostAssembly(AssemblyName assemblyName)
    {
        string? simpleName = assemblyName.Name;
        if (string.IsNullOrEmpty(simpleName))
            return false;

        foreach (Assembly loaded in Default.Assemblies)
            if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                return true;

        return File.Exists(Path.Combine(AppContext.BaseDirectory, simpleName + ".dll"));
    }
}
