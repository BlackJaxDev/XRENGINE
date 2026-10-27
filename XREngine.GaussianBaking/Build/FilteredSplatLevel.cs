namespace XREngine.Rendering.Models.Gaussian.Baking.Build;

/// <summary>
/// Holds the attribute streams of one splat level before the vertex codec. The rotation, scale, and color streams have
/// already passed through their meshoptimizer filters. Each filter works on one splat at a time, so any node range of
/// the level can go through the vertex codec alone.
/// </summary>
/// <param name="Level">The splat level.</param>
/// <param name="Count">The number of splats in the level.</param>
/// <param name="Binding">Four u16 values for each splat: A, B, C, and the flags.</param>
/// <param name="Triangle">One u32 value for each splat: the stored driver triangle, or the static value.</param>
/// <param name="Rotation">Eight bytes for each splat from the quaternion filter.</param>
/// <param name="Scale">Twelve bytes for each splat from the exponent filter.</param>
/// <param name="Color">Four bytes for each splat from the color filter.</param>
/// <param name="ChildCounts">One byte for each splat, or null for the last level.</param>
internal sealed record FilteredSplatLevel(
    int Level,
    int Count,
    byte[] Binding,
    byte[] Triangle,
    byte[] Rotation,
    byte[] Scale,
    byte[] Color,
    byte[]? ChildCounts);
