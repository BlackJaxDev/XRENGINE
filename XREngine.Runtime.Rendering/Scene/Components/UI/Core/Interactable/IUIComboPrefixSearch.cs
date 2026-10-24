namespace XREngine.Rendering.UI;

/// <summary>Finds an enabled ordinal ignore-case prefix without scanning the full provider.</summary>
public interface IUIComboPrefixSearch
{
    /// <summary>Returns an enabled matching index at or after startIndex, with wrap, or -1 when none exists.</summary>
    int FindPrefix(string prefix, int startIndex);
}
