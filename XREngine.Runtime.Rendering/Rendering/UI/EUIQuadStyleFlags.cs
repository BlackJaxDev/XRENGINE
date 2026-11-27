using System;

namespace XREngine.Rendering.UI;

/// <summary>
/// Bitmask flags controlling per-quad shader features in batched UI rendering.
/// </summary>
[Flags]
public enum EUIQuadStyleFlags : uint
{
    /// <summary>
    /// Renders a legacy flat rectangle without SDF shaping or color-space conversion.
    /// </summary>
    None = 0u,

    /// <summary>
    /// Enables rounded corners, hairline border rendering, and anti-aliased SDF masking.
    /// </summary>
    StyledBox = 1u << 0,

    /// <summary>
    /// Decodes authored sRGB style and fill colors into linear RGB before writing to sRGB targets.
    /// </summary>
    DecodeSrgbColors = 1u << 1,

    /// <summary>
    /// Blends vertically from the top fill color to <see cref="UIQuadStyle.GradientBottomColor"/>.
    /// </summary>
    VerticalGradient = 1u << 2,

    /// <summary>
    /// Expands the quad vertices and renders an analytic rounded-box drop shadow outside the box.
    /// </summary>
    DropShadow = 1u << 3,

    /// <summary>
    /// Samples the shared blurred backdrop texture behind the box with vibrancy saturation.
    /// </summary>
    BackdropBlur = 1u << 4,
}
