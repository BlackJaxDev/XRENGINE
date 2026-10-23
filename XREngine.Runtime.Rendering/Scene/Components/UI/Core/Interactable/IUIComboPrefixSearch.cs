namespace XREngine.Rendering.UI;

/// <summary>Finds a matching virtual option without scanning the full provider.</summary>
public interface IUIComboPrefixSearch
{
    /// <summary>Returns a matching index, or -1 when there is no match.</summary>
    int FindPrefix(string prefix, int startIndex);
}
