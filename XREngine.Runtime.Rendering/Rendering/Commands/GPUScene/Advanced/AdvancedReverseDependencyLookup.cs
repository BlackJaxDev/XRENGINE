namespace XREngine.Rendering.Commands;

/// <summary>Result of an exact reverse-dependency query against a sealed publication.</summary>
public readonly record struct AdvancedReverseDependencyLookup(
    int Count,
    EAdvancedReverseDependencyFallback Fallback)
{
    public bool IsExact => Fallback == EAdvancedReverseDependencyFallback.None;
}
