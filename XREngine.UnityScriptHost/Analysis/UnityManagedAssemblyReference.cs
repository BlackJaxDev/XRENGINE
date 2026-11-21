namespace XREngine.UnityScriptHost.Analysis;

/// <summary>Records one assembly identity named by managed PE metadata.</summary>
public sealed record UnityManagedAssemblyReference
{
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Culture { get; init; } = string.Empty;
    public string PublicKeyToken { get; init; } = string.Empty;
}
