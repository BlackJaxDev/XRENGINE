namespace UnityEditor;

/// <summary>Names source editor play-mode transitions without scheduling callbacks.</summary>
public enum PlayModeStateChange
{
    EnteredEditMode = 0,
    ExitingEditMode = 1,
    EnteredPlayMode = 2,
    ExitingPlayMode = 3
}
