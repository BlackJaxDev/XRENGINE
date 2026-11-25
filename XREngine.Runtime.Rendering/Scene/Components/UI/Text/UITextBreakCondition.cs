namespace XREngine.Rendering.UI;

/// <summary>Break priority at a text boundary.</summary>
public enum UITextBreakCondition : byte
{
    Neutral,
    CanBreak,
    MayNotBreak,
    MustBreak
}
