namespace XREngine.UnityScriptHost.Admission;

/// <summary>Preserves assembly admission inputs without claiming compilation or platform resolution.</summary>
public sealed record UnityAssemblyDefinition
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public string[] References { get; init; } = [];
    public string[] PrecompiledReferences { get; init; } = [];
    public string[] IncludePlatforms { get; init; } = [];
    public string[] ExcludePlatforms { get; init; } = [];
    public string[] DefineConstraints { get; init; } = [];
    public string[] OptionalUnityReferences { get; init; } = [];
    public UnityAssemblyVersionDefine[] VersionDefines { get; init; } = [];
    public bool OverrideReferences { get; init; }
    public bool AllowUnsafeCode { get; init; }
    public bool AutoReferenced { get; init; } = true;
    public bool NoEngineReferences { get; init; }
}
