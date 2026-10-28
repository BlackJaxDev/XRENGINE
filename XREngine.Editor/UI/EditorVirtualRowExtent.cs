namespace XREngine.Editor.UI;

/// <summary>
/// Computes the measured height of an inspector virtual row from its display line count. The
/// row content does not wrap, so the explicit line count is the full height requirement.
/// </summary>
internal static class EditorVirtualRowExtent
{
    /// <summary>The height of a row with one line of text or one fixed-height editor.</summary>
    internal const float SingleLineHeight = 28;

    /// <summary>
    /// The line advance as a multiple of the font size. It includes a margin for glyphs that are
    /// taller than the font size.
    /// </summary>
    internal const float LineAdvanceScale = 1.3f;

    internal static float ForLines(int lineCount, float fontSize)
    {
        if (lineCount <= 1 || !float.IsFinite(fontSize) || fontSize <= 0)
            return SingleLineHeight;
        return SingleLineHeight + MathF.Ceiling((lineCount - 1) * fontSize * LineAdvanceScale);
    }
}
