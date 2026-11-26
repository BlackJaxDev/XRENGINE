using System.Numerics;
using XREngine.Data.Colors;

namespace XREngine.Rendering.UI;

/// <summary>
/// Declarative CSS-inspired visual properties for a native UI element or interaction state override.
/// Null properties inherit from the base state or leave the existing component property unchanged.
/// </summary>
public struct UIStyleProperties
{
    public ColorF4? BackgroundColor;
    public ColorF4? GradientBottomColor;
    public ColorF4? TextColor;
    public Vector4? CornerRadii;
    public float? CornerExponent;
    public float? BorderWidth;
    public ColorF4? BorderColor;
    public float? BorderHighlightBoost;
    public ColorF4? ShadowColor;
    public Vector4? ShadowParams;
    public float? BackdropBlurRadius;
    public float? BackdropBlurBlend;
    public float? BackdropVibrancySaturation;
    public float? BackdropVibrancyBrightness;
    public bool? DecodeSrgbColors;
    /// <summary>A vector icon to draw instead of the box. The background color is the icon color.</summary>
    public EUIQuadIcon? Icon;
    public float? Opacity;
    public Vector2? VisualScale;
    public Vector2? VisualOffset;

    /// <summary>
    /// Creates a copy of <paramref name="baseStyle"/> with all non-null properties from this override applied.
    /// </summary>
    public readonly UIStyleProperties ResolveOver(in UIStyleProperties baseStyle)
        => new()
        {
            BackgroundColor = BackgroundColor ?? baseStyle.BackgroundColor,
            GradientBottomColor = GradientBottomColor ?? baseStyle.GradientBottomColor,
            TextColor = TextColor ?? baseStyle.TextColor,
            CornerRadii = CornerRadii ?? baseStyle.CornerRadii,
            CornerExponent = CornerExponent ?? baseStyle.CornerExponent,
            BorderWidth = BorderWidth ?? baseStyle.BorderWidth,
            BorderColor = BorderColor ?? baseStyle.BorderColor,
            BorderHighlightBoost = BorderHighlightBoost ?? baseStyle.BorderHighlightBoost,
            ShadowColor = ShadowColor ?? baseStyle.ShadowColor,
            ShadowParams = ShadowParams ?? baseStyle.ShadowParams,
            BackdropBlurRadius = BackdropBlurRadius ?? baseStyle.BackdropBlurRadius,
            BackdropBlurBlend = BackdropBlurBlend ?? baseStyle.BackdropBlurBlend,
            BackdropVibrancySaturation = BackdropVibrancySaturation ?? baseStyle.BackdropVibrancySaturation,
            BackdropVibrancyBrightness = BackdropVibrancyBrightness ?? baseStyle.BackdropVibrancyBrightness,
            DecodeSrgbColors = DecodeSrgbColors ?? baseStyle.DecodeSrgbColors,
            Icon = Icon ?? baseStyle.Icon,
            Opacity = Opacity ?? baseStyle.Opacity,
            VisualScale = VisualScale ?? baseStyle.VisualScale,
            VisualOffset = VisualOffset ?? baseStyle.VisualOffset,
        };

    /// <summary>
    /// Builds the packed <see cref="UIQuadStyle"/> representation for the current properties.
    /// </summary>
    public readonly UIQuadStyle ToQuadStyle()
    {
        Vector4 radii = CornerRadii ?? Vector4.Zero;
        float cornerExp = CornerExponent ?? 2.0f;
        float borderWidth = BorderWidth ?? 0.0f;
        Vector4 borderColor = BorderColor is { } bc ? ToVector4(bc) : Vector4.Zero;
        float borderHighlight = BorderHighlightBoost ?? 0.0f;
        Vector4 gradientBottom = GradientBottomColor is { } gc ? ToVector4(gc) : Vector4.Zero;
        Vector4 shadowColor = ShadowColor is { } sc ? ToVector4(sc) : Vector4.Zero;
        Vector4 shadowParams = ShadowParams ?? Vector4.Zero;
        float blurRadius = BackdropBlurRadius ?? 0.0f;
        float blurBlend = BackdropBlurBlend ?? (blurRadius > 0.001f ? 1.0f : 0.0f);
        float saturation = BackdropVibrancySaturation ?? 1.25f;
        float brightness = BackdropVibrancyBrightness ?? 1.0f;

        EUIQuadStyleFlags flags = EUIQuadStyleFlags.None;
        if (radii != Vector4.Zero || borderWidth > 0.0f)
            flags |= EUIQuadStyleFlags.StyledBox;
        if (DecodeSrgbColors == true)
            flags |= EUIQuadStyleFlags.DecodeSrgbColors;
        if (GradientBottomColor.HasValue)
            flags |= EUIQuadStyleFlags.VerticalGradient;
        if (ShadowColor.HasValue && shadowColor.W > 0.0001f && (shadowParams.Z > 0.0f || shadowParams.W != 0.0f))
            flags |= EUIQuadStyleFlags.DropShadow;
        if (blurRadius > 0.001f && blurBlend > 0.001f)
            flags |= EUIQuadStyleFlags.BackdropBlur | EUIQuadStyleFlags.StyledBox;

        UIQuadStyle style = new()
        {
            CornerRadii = radii,
            CornerExponent = cornerExp,
            BorderWidth = borderWidth,
            BorderHighlightBoost = borderHighlight,
            BorderColor = borderColor,
            GradientBottomColor = gradientBottom,
            ShadowColor = shadowColor,
            ShadowParams = shadowParams,
            BackdropBlurParams = new Vector4(blurRadius, blurBlend, saturation, brightness),
            Flags = flags,
        };
        style.Icon = Icon ?? EUIQuadIcon.None;
        return style;
    }

    private static Vector4 ToVector4(ColorF4 color) => new(color.R, color.G, color.B, color.A);
}
