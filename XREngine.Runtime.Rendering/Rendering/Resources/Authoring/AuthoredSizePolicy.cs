namespace XREngine.Rendering.Resources.Authoring;

/// <summary>The size of an authored resource, in a form that saves to YAML and to script.</summary>
public sealed class AuthoredSizePolicy
{
    /// <summary>Gets or sets what the size is relative to.</summary>
    public RenderResourceSizeClass SizeClass { get; set; } = RenderResourceSizeClass.InternalResolution;

    /// <summary>Gets or sets the horizontal scale of a relative size.</summary>
    public float ScaleX { get; set; } = 1f;

    /// <summary>Gets or sets the vertical scale of a relative size.</summary>
    public float ScaleY { get; set; } = 1f;

    /// <summary>Gets or sets the width of an absolute size, in pixels.</summary>
    public uint Width { get; set; }

    /// <summary>Gets or sets the height of an absolute size, in pixels.</summary>
    public uint Height { get; set; }

    /// <summary>Gets or sets a divisor that rounds up a relative size. A value of 1 or less uses the scale.</summary>
    public uint RoundUpDivisor { get; set; } = 1u;

    /// <summary>Creates an internal-resolution size with a scale.</summary>
    public static AuthoredSizePolicy Internal(float scale = 1f)
        => new() { SizeClass = RenderResourceSizeClass.InternalResolution, ScaleX = scale, ScaleY = scale };

    /// <summary>Creates a window-resolution size with a scale.</summary>
    public static AuthoredSizePolicy Window(float scale = 1f)
        => new() { SizeClass = RenderResourceSizeClass.WindowResolution, ScaleX = scale, ScaleY = scale };

    /// <summary>Creates an absolute size.</summary>
    public static AuthoredSizePolicy Absolute(uint width, uint height)
        => new() { SizeClass = RenderResourceSizeClass.AbsolutePixels, Width = width, Height = height };

    /// <summary>Converts the authored size to the runtime size policy.</summary>
    public RenderResourceSizePolicy ToPolicy()
        => new(SizeClass, ScaleX, ScaleY, Width, Height, Math.Max(RoundUpDivisor, 1u));

    /// <summary>Creates an authored size from a runtime size policy.</summary>
    public static AuthoredSizePolicy FromPolicy(RenderResourceSizePolicy policy)
        => new()
        {
            SizeClass = policy.SizeClass,
            ScaleX = policy.ScaleX,
            ScaleY = policy.ScaleY,
            Width = policy.Width,
            Height = policy.Height,
            RoundUpDivisor = policy.RoundUpDivisor,
        };

    /// <summary>Returns a copy.</summary>
    public AuthoredSizePolicy Clone()
        => (AuthoredSizePolicy)MemberwiseClone();
}
