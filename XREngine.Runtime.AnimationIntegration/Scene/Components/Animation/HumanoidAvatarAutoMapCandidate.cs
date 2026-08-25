using System.Numerics;
using XREngine.Scene;

namespace XREngine.Components.Animation;

/// <summary>
/// Initialization-only geometric/topological description of one skeleton node.
/// </summary>
internal sealed class HumanoidAvatarAutoMapCandidate
{
    public required SceneNode Node { get; init; }
    public Matrix4x4 LocalBindTransform { get; init; }
    public Matrix4x4 WorldBindTransform { get; init; }
    public Vector3 Position { get; init; }
    public int Depth { get; init; }
    public int TraversalIndex { get; init; }
    public int SubtreeNodeCount { get; set; }
    public int DescendantLeafCount { get; set; }
    public float SubtreeMinimumY { get; set; }
    public float SubtreeMaximumY { get; set; }
    public float JointAxisScore { get; set; }
}
