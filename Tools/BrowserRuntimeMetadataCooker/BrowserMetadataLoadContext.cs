using System.Reflection;
using System.Runtime.Loader;

namespace XREngine.Publishing;

/// <summary>Loads shipped browser-managed binaries without unifying them with the Editor's assemblies.</summary>
public sealed class BrowserMetadataLoadContext(string binaryDirectory, HashSet<string> shippedNames)
    : AssemblyLoadContext(isCollectible: true), IDisposable
{
    public Assembly LoadPublishedAssembly(string name)
    {
        string path = Path.Combine(binaryDirectory, name + ".dll");
        if (!File.Exists(path))
            throw new FileNotFoundException($"BrowserPublish.AssemblyClosureIncomplete: compiled '{name}' is missing.", path);
        return LoadFromAssemblyPath(path);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string? name = assemblyName.Name;
        if (name is null || name.StartsWith("System.", StringComparison.Ordinal)
            || name is "netstandard" or "mscorlib")
            return null;
        if (!shippedNames.Contains(name))
            throw new FileNotFoundException($"BrowserPublish.AssemblyClosureIncomplete: referenced '{name}' is absent from the shipped browser closure.");
        return LoadPublishedAssembly(name);
    }

    public void Dispose() => Unload();
}
