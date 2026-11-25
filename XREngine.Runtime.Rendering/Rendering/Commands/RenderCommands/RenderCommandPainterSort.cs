using System.Numerics;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Gives the draws of one direct-world UI canvas a shared sort distance and a fixed order.
/// A canvas publication sets one key on each draw. Every draw of the canvas then sorts by
/// the distance to <see cref="Anchor"/>, so the camera position cannot reorder them, and
/// <see cref="Index"/> keeps the canvas painter order: a parent draws before its children,
/// and an earlier sibling draws before a later one.
/// </summary>
/// <param name="Anchor">The world-space point that gives every draw of the canvas the same sort distance.</param>
/// <param name="Index">The position of the draw in the canvas painter order. Index 0 draws first.</param>
internal sealed record RenderCommandPainterSort(Vector3 Anchor, int Index);
