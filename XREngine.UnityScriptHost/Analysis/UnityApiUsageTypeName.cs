namespace XREngine.UnityScriptHost.Analysis;

/// <summary>Retains a type signature and the assembly that owns its generic base type.</summary>
internal readonly record struct UnityApiUsageTypeName(string Assembly, string Name,
    bool Resolved = true, bool IsLocal = false)
{
    internal string QualifiedName => "[" + Assembly + "]" + Name;
}
