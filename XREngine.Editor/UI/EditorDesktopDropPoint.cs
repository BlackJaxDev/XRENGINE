using System.Numerics;

namespace XREngine.Editor.UI;

/// <summary>Identifies the native client point used for one dock hit test.</summary>
internal readonly record struct EditorDesktopDropPoint(
    Vector2 ScreenPosition,
    nint TargetWindowHandle,
    Vector2 TargetClientScreenPosition,
    Vector2 TargetClientSize,
    bool IsRelease);
