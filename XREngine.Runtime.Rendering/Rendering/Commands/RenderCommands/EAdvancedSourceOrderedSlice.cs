namespace XREngine.Rendering.Commands;

/// <summary>Selects a range of the sorted source-authored late pass.</summary>
public enum EAdvancedSourceOrderedSlice
{
    All = 0,
    BeforeSceneColor = 1,
    FromSceneColor = 2,
}
