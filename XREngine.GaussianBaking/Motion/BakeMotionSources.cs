namespace XREngine.Rendering.Models.Gaussian.Baking.Motion;

/// <summary>Gives the values of <see cref="BakeMotionChoice.Source"/>.</summary>
public static class BakeMotionSources
{
    /// <summary>The clip has the name that the bake options ask for.</summary>
    public const string Option = "option";

    /// <summary>The clip has the name <c>idle</c>, in any letter case.</summary>
    public const string Idle = "idle";

    /// <summary>The clip is the first playable clip of the content.</summary>
    public const string First = "first";

    /// <summary>Every clip of a world plays at the same time.</summary>
    public const string World = "world";

    /// <summary>The content has no playable clip, so the bake has one still frame.</summary>
    public const string None = "none";
}
