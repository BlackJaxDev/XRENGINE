namespace XREngine.Rendering.UI;

/// <summary>Finds the next enabled option in a large virtual source.</summary>
public interface IUIComboEnabledNavigation
{
    /// <summary>Returns an enabled index after startIndex, with wrap, or -1 when none exists.</summary>
    int FindNextEnabled(int startIndex, int direction);
}
