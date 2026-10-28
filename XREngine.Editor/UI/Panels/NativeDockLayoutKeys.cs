namespace XREngine.Editor;

/// <summary>
/// Selects the saved layout record for the primary editor window. The canonical key is
/// <see cref="Primary"/>. Old records used <c>index:title</c> keys. The lookup reads the
/// exact legacy key for the current title, then the legacy key for the default editor title.
/// It never infers a match from dictionary order or from other index-zero records, and it
/// never removes or changes an old record.
/// </summary>
internal static class NativeDockLayoutKeys
{
    /// <summary>The canonical key for the single primary editor window.</summary>
    public const string Primary = "primary";

    /// <summary>The window title that the default editor startup profile sets.</summary>
    public const string DefaultEditorTitle = "XRE Editor";

    private const string LegacyPrimaryPrefix = "0:";

    /// <summary>Returns the legacy key that a primary window with this title used.</summary>
    public static string LegacyPrimary(string title) => LegacyPrimaryPrefix + title;

    /// <summary>
    /// Selects the primary layout record. Returns the match kind, and sets
    /// <paramref name="layout"/> when a record matched. <paramref name="unmatchedCount"/> is
    /// the number of other index-zero records when no record matched.
    /// </summary>
    public static ENativeDockLayoutKeyMatch ResolvePrimary(
        IReadOnlyDictionary<string, NativeDockLayout?> layouts, string? currentTitle,
        out NativeDockLayout? layout, out int unmatchedCount)
    {
        ArgumentNullException.ThrowIfNull(layouts);
        unmatchedCount = 0;
        if (layouts.TryGetValue(Primary, out layout) && layout is not null)
            return ENativeDockLayoutKeyMatch.Canonical;
        if (!string.IsNullOrEmpty(currentTitle) &&
            layouts.TryGetValue(LegacyPrimary(currentTitle), out layout) && layout is not null)
            return ENativeDockLayoutKeyMatch.CurrentTitle;
        if (layouts.TryGetValue(LegacyPrimary(DefaultEditorTitle), out layout) && layout is not null)
            return ENativeDockLayoutKeyMatch.DefaultTitle;
        layout = null;
        foreach (KeyValuePair<string, NativeDockLayout?> entry in layouts)
            if (entry.Value is not null && entry.Key.StartsWith(LegacyPrimaryPrefix, StringComparison.Ordinal))
                unmatchedCount++;
        return unmatchedCount == 0 ? ENativeDockLayoutKeyMatch.None : ENativeDockLayoutKeyMatch.Unmatched;
    }
}
