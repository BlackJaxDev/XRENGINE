using System.Numerics;
using System.Runtime.CompilerServices;

namespace XREngine.Scene.Transforms;

/// <summary>Owns lazy legacy replication baselines without charging every scene transform for network state.</summary>
internal sealed class TransformReplicationState
{
    private static readonly ConditionalWeakTable<TransformBase, TransformReplicationState> States = new();
    internal static TransformReplicationState For(TransformBase transform) => States.GetValue(transform, static _ => new());
    internal static float Elapsed(TransformBase transform) => States.TryGetValue(transform, out var state) ? state.TimeSinceKeyframe : 0;
    internal float TimeSinceKeyframe;
    internal Matrix4x4 Matrix = Matrix4x4.Identity;
    internal Vector3 Scale = Vector3.One;
    internal Vector3 Translation;
    internal Quaternion Rotation = Quaternion.Identity;
}
