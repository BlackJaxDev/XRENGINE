using System.Numerics;

namespace XREngine.Rendering.UI;

/// <summary>An insertion boundary in logical text order and its placed line position.</summary>
public readonly record struct UITextCaretStop(int Utf16Offset, int Line, Vector2 Position, float LineHeight);
