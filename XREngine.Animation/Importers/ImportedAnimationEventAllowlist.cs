namespace XREngine.Animation.Importers;

/// <summary>
/// Maps explicitly supported source callback names to native animation event identifiers.
/// </summary>
/// <remarks>
/// Source callbacks beginning with <see cref="SourceFunctionPrefix"/> are an explicit
/// transport contract for native events. The suffix is carried as an opaque identifier
/// below <see cref="EventIdPrefix"/> and is delivered only to
/// <see cref="IImportedAnimationEventReceiver"/> implementations. It is never used for
/// reflection or component-method dispatch. All other source callbacks are rejected.
/// </remarks>
public static class ImportedAnimationEventAllowlist
{
    /// <summary>
    /// Source callback prefix reserved for an imported native animation event. The suffix
    /// must be a non-empty ASCII identifier containing letters, digits, <c>_</c>, <c>-</c>,
    /// or <c>.</c>.
    /// </summary>
    public const string SourceFunctionPrefix = "XREvent_";

    /// <summary>Native event identifier namespace produced by <see cref="TryMap"/>.</summary>
    public const string EventIdPrefix = "xre.event.";

    public static bool TryMap(string sourceFunctionName, out string eventId)
    {
        if (string.IsNullOrEmpty(sourceFunctionName)
            || !sourceFunctionName.StartsWith(SourceFunctionPrefix, StringComparison.Ordinal)
            || sourceFunctionName.Length == SourceFunctionPrefix.Length)
        {
            eventId = string.Empty;
            return false;
        }

        ReadOnlySpan<char> suffix = sourceFunctionName.AsSpan(SourceFunctionPrefix.Length);
        for (int i = 0; i < suffix.Length; i++)
        {
            char character = suffix[i];
            if (!char.IsAsciiLetterOrDigit(character)
                && character is not '_' and not '-' and not '.')
            {
                eventId = string.Empty;
                return false;
            }
        }

        eventId = string.Concat(EventIdPrefix, suffix);
        return true;
    }
}
