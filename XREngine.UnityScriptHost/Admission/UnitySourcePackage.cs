namespace XREngine.UnityScriptHost.Admission;

/// <summary>Records a package manifest and its declared dependency constraints without resolving them.</summary>
public sealed record UnitySourcePackage(
    string Path,
    string Name,
    string Version,
    bool IsEmbeddedPackage,
    SortedDictionary<string, string> Dependencies,
    SortedDictionary<string, string> VpmDependencies);
