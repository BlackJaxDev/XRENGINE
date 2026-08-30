namespace XREngine.Rendering.Commands;

/// <summary>Countable reasons a consumer must invalidate a whole dependency domain.</summary>
public enum EAdvancedReverseDependencyFallback : byte
{
    None,
    ManifestUnavailable,
    ManifestInconsistent,
    DependencyNotRetained,
    DestinationCapacityExceeded,
}
