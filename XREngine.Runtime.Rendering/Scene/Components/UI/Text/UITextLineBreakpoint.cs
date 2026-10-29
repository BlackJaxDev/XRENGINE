namespace XREngine.Rendering.UI;

/// <summary>Break conditions before and after one UTF-16 code unit.</summary>
public readonly record struct UITextLineBreakpoint(UITextBreakCondition Before, UITextBreakCondition After,
    bool IsWhitespace, bool IsSoftHyphen);
