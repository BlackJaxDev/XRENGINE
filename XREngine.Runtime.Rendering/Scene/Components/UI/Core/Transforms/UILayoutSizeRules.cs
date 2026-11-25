using System.Numerics;

namespace XREngine.Rendering.UI;

/// <summary>Defines empty space when finite layout insets exceed the available extent.</summary>
internal static class UILayoutSizeRules
{
    internal static float NonnegativeFiniteExtent(float value)
        => float.IsFinite(value) && value < 0 ? 0 : value;

    internal static Vector2 InsetSize(Vector2 size, Vector4 insets)
        => new(NonnegativeFiniteExtent(size.X - insets.X - insets.Z),
            NonnegativeFiniteExtent(size.Y - insets.Y - insets.W));
}
