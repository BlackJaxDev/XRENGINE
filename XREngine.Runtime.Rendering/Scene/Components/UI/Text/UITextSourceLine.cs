namespace XREngine.Rendering.UI;

/// <summary>One hard-delimited source line in UTF-16 units.</summary>
public readonly record struct UITextSourceLine(int Start, int Length, int BreakLength)
{
    public int End => checked(Start + Length);
    public int NextStart => checked(End + BreakLength);
}
