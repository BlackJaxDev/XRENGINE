namespace XREngine.UnityScriptHost.Compilation;

/// <summary>Gives the selected source assemblies in dependency order.</summary>
public sealed record UnityAssemblyGraph
{
    public string SourceContentSha256 { get; init; } = string.Empty;
    public string ProfileName { get; init; } = string.Empty;
    public string ProfileSha256 { get; init; } = string.Empty;
    public string[] CompilationOrder { get; init; } = [];
    public UnityAssemblyNode[] Assemblies { get; init; } = [];
    public UnityAssemblyGraphDiagnostic[] Diagnostics { get; init; } = [];
    public bool Succeeded => !Diagnostics.Any(static diagnostic => diagnostic.IsError);
}
