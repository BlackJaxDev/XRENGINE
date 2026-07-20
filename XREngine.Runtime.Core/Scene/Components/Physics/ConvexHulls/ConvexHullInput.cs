using System.Numerics;

namespace XREngine.Components.Physics;

/// <summary>
/// Backend-neutral indexed triangle input for convex-hull generation.
/// </summary>
internal readonly struct ConvexHullInput(Vector3[] positions, int[] indices)
{
    public Vector3[] Positions { get; } = positions;
    public int[] Indices { get; } = indices;
}
