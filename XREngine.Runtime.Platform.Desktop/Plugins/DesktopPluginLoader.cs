using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using XREngine.Data;
using XREngine.Data.Plugins;

namespace XREngine.Runtime.Platform.Desktop.Plugins;

/// <summary>
/// Finds and installs managed plugins for desktop CoreCLR hosts. A plugin is a folder that holds an entry assembly
/// with the folder's name, such as <c>Plugins/MyImporter/MyImporter.dll</c>, plus its private dependencies.
/// </summary>
/// <remarks>
/// Search roots are the <c>Plugins</c> folder next to the host executable and each path in
/// <see cref="PluginPathsVariable"/>. A root is either a plugin folder or a folder of plugin folders. A plugin that
/// fails to load or install is logged and skipped. Published AOT hosts do not load plugins.
/// </remarks>
public static class DesktopPluginLoader
{
    /// <summary>Environment variable with extra search roots, separated by <see cref="Path.PathSeparator"/>.</summary>
    public const string PluginPathsVariable = "XRE_PLUGIN_PATHS";

    /// <summary>Folder next to the host executable that holds installed plugins.</summary>
    public const string PluginsFolderName = "Plugins";

    /// <summary>Installs every plugin under the default search roots. Disposing the result uninstalls them.</summary>
    public static IDisposable Install()
        => Install(DefaultSearchRoots());

    /// <summary>Installs every plugin under <paramref name="searchRoots"/>, in path order within each root.</summary>
    public static IDisposable Install(IEnumerable<string> searchRoots)
    {
        ArgumentNullException.ThrowIfNull(searchRoots);

        // Published players refuse runtime assembly loading; see GameCSProjLoader for the content policy.
        if (XRRuntimeEnvironment.IsPublishedBuild)
            return RegistrationLeaseGroup.Create(static _ => { });

        return RegistrationLeaseGroup.Create(leases =>
        {
            HashSet<string> installedNames = new(StringComparer.OrdinalIgnoreCase);
            foreach (string pluginDirectory in searchRoots.SelectMany(FindPluginDirectories))
            {
                string name = Path.GetFileName(pluginDirectory);
                if (!installedNames.Add(name))
                {
                    Debug.LogWarning(ELogCategory.General,
                        $"[Plugins] Skipped '{pluginDirectory}': a plugin named '{name}' is already installed.");
                    continue;
                }

                IDisposable? lease = TryInstall(name, pluginDirectory);
                if (lease is not null)
                    leases.Add(lease);
            }
        });
    }

    /// <summary>The <c>Plugins</c> folder next to the host executable, then each root named by <see cref="PluginPathsVariable"/>.</summary>
    public static IEnumerable<string> DefaultSearchRoots()
    {
        yield return Path.Combine(AppContext.BaseDirectory, PluginsFolderName);

        string? extra = Environment.GetEnvironmentVariable(PluginPathsVariable);
        if (string.IsNullOrWhiteSpace(extra))
            yield break;

        foreach (string root in extra.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return root;
    }

    private static IEnumerable<string> FindPluginDirectories(string root)
    {
        if (!Directory.Exists(root))
            return [];

        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (File.Exists(EntryAssemblyPath(fullRoot)))
            return [fullRoot];

        return Directory.EnumerateDirectories(fullRoot)
            .Where(static directory => File.Exists(EntryAssemblyPath(directory)))
            .OrderBy(static directory => directory, StringComparer.OrdinalIgnoreCase);
    }

    private static string EntryAssemblyPath(string pluginDirectory)
        => Path.Combine(pluginDirectory, Path.GetFileName(pluginDirectory) + ".dll");

    private static IDisposable? TryInstall(string name, string pluginDirectory)
    {
        string entryAssemblyPath = EntryAssemblyPath(pluginDirectory);
        try
        {
            PluginLoadContext context = new(name, entryAssemblyPath);
            Assembly assembly = context.LoadFromAssemblyPath(entryAssemblyPath);
            XREnginePluginAttribute[] entries = [.. assembly.GetCustomAttributes<XREnginePluginAttribute>()];
            if (entries.Length == 0)
            {
                Debug.LogWarning(ELogCategory.General,
                    $"[Plugins] Skipped '{entryAssemblyPath}': it has no [assembly: {nameof(XREnginePluginAttribute)}].");
                return null;
            }

            XREnginePluginContext pluginContext = new(name, pluginDirectory, entryAssemblyPath);
            IDisposable lease = RegistrationLeaseGroup.Create(leases =>
            {
                foreach (XREnginePluginAttribute entry in entries)
                    leases.Add(CreatePlugin(entry.PluginType).Install(pluginContext));
            });
            Debug.Log(ELogCategory.General, $"[Plugins] Installed '{name}' from '{pluginDirectory}'.");
            return lease;
        }
        catch (Exception ex)
        {
            Debug.LogException(ELogCategory.General, ex, $"[Plugins] Failed to install '{name}' from '{pluginDirectory}'.");
            return null;
        }
    }

    private static IXREnginePlugin CreatePlugin(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type pluginType)
    {
        if (!typeof(IXREnginePlugin).IsAssignableFrom(pluginType))
            throw new InvalidOperationException($"'{pluginType.FullName}' does not implement {nameof(IXREnginePlugin)}.");

        return (IXREnginePlugin)(Activator.CreateInstance(pluginType)
            ?? throw new InvalidOperationException($"Could not create '{pluginType.FullName}'."));
    }
}
