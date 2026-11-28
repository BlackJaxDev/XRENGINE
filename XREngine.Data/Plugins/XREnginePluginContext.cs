namespace XREngine.Data.Plugins;

/// <summary>Describes where a plugin was loaded from.</summary>
/// <param name="Name">The plugin's folder name, which is also the simple name of its entry assembly.</param>
/// <param name="PluginDirectory">The folder that holds the entry assembly and its private dependencies.</param>
/// <param name="EntryAssemblyPath">The full path of the entry assembly.</param>
public sealed record XREnginePluginContext(string Name, string PluginDirectory, string EntryAssemblyPath);
