namespace XREngine.Rendering.UI;

/// <summary>One UTF-16 script and bidi range returned by paragraph analysis.</summary>
public readonly record struct UITextParagraphRun(int Start, int Length, ushort Script,
    int ScriptShapes, byte ExplicitBidiLevel, byte ResolvedBidiLevel);
