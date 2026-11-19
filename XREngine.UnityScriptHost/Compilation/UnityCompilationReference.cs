namespace XREngine.UnityScriptHost.Compilation;

/// <summary>Names one pinned metadata reference and its graph selection role.</summary>
public sealed record UnityCompilationReference
{
    public string Path { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public string AssemblyName { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;

    /// <summary>Checks that Unity API references use their own role and other identities use a permitted role.</summary>
    public bool HasValidRole()
    {
        if (string.IsNullOrWhiteSpace(AssemblyName))
            return false;
        if (AssemblyName.Equals("UnityEngine", StringComparison.OrdinalIgnoreCase) || AssemblyName.StartsWith("UnityEngine.", StringComparison.OrdinalIgnoreCase))
            return Role == "UnityEngine";
        if (AssemblyName.Equals("UnityEditor", StringComparison.OrdinalIgnoreCase) || AssemblyName.StartsWith("UnityEditor.", StringComparison.OrdinalIgnoreCase))
            return Role == "UnityEditor";
        if (Role == "Imported" &&
            (AssemblyName.Equals(typeof(UnityCompilationReference).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase) ||
             AssemblyName.Equals(typeof(Contracts.UnityBuildRequest).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase) ||
             AssemblyName.Equals(typeof(Execution.UnityCompatibilityRuntime).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase)))
            return false;
        return Role is "Compatibility" or "Platform" or "Imported";
    }
}
