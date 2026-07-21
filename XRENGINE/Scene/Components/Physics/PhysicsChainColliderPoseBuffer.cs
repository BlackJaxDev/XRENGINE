using System.Numerics;

namespace XREngine.Components;

/// <summary>
/// Independently versioned dynamic pose stream for a shared collider set.
/// Dirty bounds allow backends to upload only the changed contiguous range.
/// </summary>
public sealed class PhysicsChainColliderPoseBuffer
{
    private readonly Matrix4x4[] _poses;
    private int _dirtyStart;
    private int _dirtyEnd;

    public PhysicsChainColliderPoseBuffer(int colliderCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(colliderCount);
        _poses = new Matrix4x4[colliderCount];
        Array.Fill(_poses, Matrix4x4.Identity);
        _dirtyStart = colliderCount == 0 ? 0 : 0;
        _dirtyEnd = colliderCount;
    }

    public ReadOnlyMemory<Matrix4x4> Poses => _poses;
    public uint PoseVersion { get; private set; } = 1u;
    public int DirtyStart => _dirtyStart;
    public int DirtyCount => _dirtyEnd - _dirtyStart;

    public bool TrySetPose(int index, in Matrix4x4 pose)
    {
        if ((uint)index >= (uint)_poses.Length || !IsFinite(pose))
            return false;
        if (_poses[index] == pose)
            return true;

        _poses[index] = pose;
        _dirtyStart = DirtyCount == 0 ? index : Math.Min(_dirtyStart, index);
        _dirtyEnd = Math.Max(_dirtyEnd, index + 1);
        PoseVersion = NextVersion(PoseVersion);
        return true;
    }

    public void ClearDirtyRange()
    {
        _dirtyStart = 0;
        _dirtyEnd = 0;
    }

    private static uint NextVersion(uint version)
    {
        unchecked
        {
            ++version;
        }

        return version == 0u ? 1u : version;
    }

    private static bool IsFinite(in Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14)
        && float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24)
        && float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34)
        && float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
