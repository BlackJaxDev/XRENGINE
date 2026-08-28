namespace XREngine.Animation.Importers;

/// <summary>
/// Maps explicitly supported source callback names to native animation event identifiers.
/// </summary>
/// <remarks>
/// The initial allowlist is deliberately empty. Additions require a native, typed receiver
/// contract and must not infer component methods from the source callback name.
/// </remarks>
public static class ImportedAnimationEventAllowlist
{
    public static bool TryMap(string sourceFunctionName, out string eventId)
    {
        _ = sourceFunctionName;
        eventId = string.Empty;
        return false;
    }
}
