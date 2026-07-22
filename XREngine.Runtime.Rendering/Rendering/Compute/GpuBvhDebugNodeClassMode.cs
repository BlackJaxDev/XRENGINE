namespace XREngine.Rendering.Compute;

/// <summary>Controls how an optional per-node query classification affects GPU BVH debug lines.</summary>
public enum GpuBvhDebugNodeClassMode : uint
{
    Ignore,
    HighlightClassified,
    ClassifiedOnly,
}
