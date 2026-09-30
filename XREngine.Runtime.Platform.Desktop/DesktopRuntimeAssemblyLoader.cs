using System.Reflection;
using System.Runtime.Loader;
using XREngine.Data;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Discovers optional engine assemblies from the desktop application's output directory.</summary>
internal sealed class DesktopRuntimeAssemblyLoader : IRuntimeAssemblyLoader
{
    private readonly object _loadLock = new();
    private volatile bool _attempted;

    public void EnsureRuntimeAssembliesLoaded()
    {
        if (_attempted)
            return;

        lock (_loadLock)
        {
            if (_attempted)
                return;

            _attempted = true;
            string baseDirectory = AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(baseDirectory) || !Directory.Exists(baseDirectory))
                return;

            HashSet<string> loadedNames = [.. AppDomain.CurrentDomain.GetAssemblies()
                .Where(static assembly => !assembly.IsDynamic)
                .Select(static assembly => assembly.GetName().Name)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()];

            foreach (string assemblyPath in Directory
                .EnumerateFiles(baseDirectory, "XREngine.Runtime*.dll", SearchOption.TopDirectoryOnly)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                AssemblyName assemblyName;
                try
                {
                    assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
                }
                catch
                {
                    continue;
                }

                string? simpleName = assemblyName.Name;
                if (string.IsNullOrWhiteSpace(simpleName) || !loadedNames.Add(simpleName))
                    continue;

                try
                {
                    AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
                }
                catch
                {
                    // Missing or incompatible optional assemblies remain unresolved by the caller.
                }
            }
        }
    }
}
