using XREngine.Rendering;

namespace XREngine;

internal sealed class RuntimeThemePreferences
{
    public ColorF4 Bounds2DColor => RuntimeRenderingHostServices.FrameTiming.Bounds2DColor;

    public ColorF4 MeshBoundsContainedColor { get; set; } = ColorF4.Green;
    public ColorF4 MeshBoundsIntersectedColor { get; set; } = ColorF4.Yellow;
    public ColorF4 MeshBoundsDisjointColor { get; set; } = ColorF4.Red;
    public ColorF4 Bounds3DColor { get; set; } = ColorF4.Cyan;
    public ColorF4 HoverOutlineColor { get; set; } = ColorF4.Yellow;
    public ColorF4 SelectionOutlineColor { get; set; } = ColorF4.White;
    public ColorF4 OctreeIntersectedBoundsColor { get; set; } = ColorF4.LightGray;
    public ColorF4 OctreeContainedBoundsColor { get; set; } = ColorF4.Yellow;
    public ColorF4 QuadtreeIntersectedBoundsColor { get; set; } = ColorF4.LightGray;
    public ColorF4 QuadtreeContainedBoundsColor { get; set; } = ColorF4.Yellow;
}
