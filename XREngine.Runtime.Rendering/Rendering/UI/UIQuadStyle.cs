using System;
using System.Numerics;

namespace XREngine.Rendering.UI;

/// <summary>
/// Per-instance CSS-inspired box styling parameters packed into the batched UI quad style SSBO.
/// </summary>
public struct UIQuadStyle : IEquatable<UIQuadStyle>
{
    /// <summary>
    /// Corner radii in logical pixels: X = top-left, Y = top-right, Z = bottom-right, W = bottom-left.
    /// </summary>
    public Vector4 CornerRadii;

    /// <summary>
    /// Superellipse corner curvature exponent. Use 2.0 for circular arcs and 2.6 to 2.8 for smooth squircle corners.
    /// </summary>
    public float CornerExponent;

    /// <summary>
    /// Inner hairline border thickness in logical pixels.
    /// </summary>
    public float BorderWidth;

    /// <summary>
    /// Specular luminance boost applied toward the top edge of the border for frosted glass cards.
    /// </summary>
    public float BorderHighlightBoost;

    /// <summary>
    /// Border RGBA color. Decoded from sRGB when <see cref="EUIQuadStyleFlags.DecodeSrgbColors"/> is set.
    /// </summary>
    public Vector4 BorderColor;

    /// <summary>
    /// Bottom RGBA color for vertical gradients when <see cref="EUIQuadStyleFlags.VerticalGradient"/> is set.
    /// </summary>
    public Vector4 GradientBottomColor;

    /// <summary>
    /// Drop-shadow RGBA color when <see cref="EUIQuadStyleFlags.DropShadow"/> is set.
    /// </summary>
    public Vector4 ShadowColor;

    /// <summary>
    /// Drop-shadow geometry in logical pixels: X = offsetX, Y = offsetY (positive shifts downward), Z = blurRadius, W = spread.
    /// </summary>
    public Vector4 ShadowParams;

    /// <summary>
    /// Frosted-glass backdrop parameters: X = blurRadiusPx, Y = blurBlend [0..1], Z = vibrancySaturation, W = vibrancyBrightness.
    /// </summary>
    public Vector4 BackdropBlurParams;

    /// <summary>
    /// Feature bitmask controlling active SDF, gradient, shadow, blur, and sRGB decoding stages.
    /// </summary>
    public EUIQuadStyleFlags Flags;

    /// <summary>
    /// Gets the default unstyled flat-quad configuration.
    /// </summary>
    public static UIQuadStyle Default => default;

    /// <summary>
    /// Gets a value indicating whether any non-default styling or color decoding is active.
    /// </summary>
    public readonly bool HasAnyStyling => Flags != EUIQuadStyleFlags.None;

    /// <summary>
    /// Gets the active backdrop blur radius in logical pixels when backdrop blur is enabled and visible.
    /// </summary>
    public readonly float EffectiveBackdropBlurRadius
        => (Flags & EUIQuadStyleFlags.BackdropBlur) != 0 && BackdropBlurParams.Y > 0.001f
            ? MathF.Max(0.0f, BackdropBlurParams.X)
            : 0.0f;

    /// <summary>
    /// Returns a copy of this style with border, gradient, shadow, and backdrop blur alpha scaled by <paramref name="opacity"/>.
    /// </summary>
    public readonly UIQuadStyle WithOpacity(float opacity)
    {
        if (opacity >= 0.9999f || Flags == EUIQuadStyleFlags.None)
            return this;

        UIQuadStyle copy = this;
        copy.BorderColor.W *= opacity;
        copy.GradientBottomColor.W *= opacity;
        copy.ShadowColor.W *= opacity;
        copy.BackdropBlurParams.Y *= opacity;
        return copy;
    }

    public readonly bool Equals(UIQuadStyle other)
        => CornerRadii.Equals(other.CornerRadii) &&
           CornerExponent.Equals(other.CornerExponent) &&
           BorderWidth.Equals(other.BorderWidth) &&
           BorderHighlightBoost.Equals(other.BorderHighlightBoost) &&
           BorderColor.Equals(other.BorderColor) &&
           GradientBottomColor.Equals(other.GradientBottomColor) &&
           ShadowColor.Equals(other.ShadowColor) &&
           ShadowParams.Equals(other.ShadowParams) &&
           BackdropBlurParams.Equals(other.BackdropBlurParams) &&
           Flags == other.Flags;

    public override readonly bool Equals(object? obj)
        => obj is UIQuadStyle other && Equals(other);

    public override readonly int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(CornerRadii);
        hash.Add(CornerExponent);
        hash.Add(BorderWidth);
        hash.Add(BorderHighlightBoost);
        hash.Add(BorderColor);
        hash.Add(GradientBottomColor);
        hash.Add(ShadowColor);
        hash.Add(ShadowParams);
        hash.Add(BackdropBlurParams);
        hash.Add(Flags);
        return hash.ToHashCode();
    }

    public static bool operator ==(UIQuadStyle left, UIQuadStyle right) => left.Equals(right);

    public static bool operator !=(UIQuadStyle left, UIQuadStyle right) => !left.Equals(right);
}
