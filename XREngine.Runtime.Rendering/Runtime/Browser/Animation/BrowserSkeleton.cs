using System.Numerics;

namespace XREngine.Rendering;

/// <summary>Immutable parent-before-child skeleton with mesh-space inverse bind matrices.</summary>
public sealed class BrowserSkeleton
{
    public const int MaximumBones = 128;
    private readonly int[] _parents;
    private readonly Matrix4x4[] _inverseBind;

    public BrowserSkeleton(ReadOnlySpan<int> parents, ReadOnlySpan<BrowserBoneTransform> localBindPose)
    {
        if (parents.Length is < 1 or > MaximumBones || parents.Length != localBindPose.Length)
            throw new ArgumentException("A browser skeleton requires one to 128 parent-ordered bones and matching bind transforms.");
        _parents = parents.ToArray();
        _inverseBind = new Matrix4x4[parents.Length];
        Span<Matrix4x4> world = stackalloc Matrix4x4[MaximumBones];
        for (int bone = 0; bone < parents.Length; bone++)
        {
            int parent = parents[bone];
            if (parent < -1 || parent >= bone || (bone == 0 && parent != -1))
                throw new ArgumentException("Bone parents must be -1 for a root or precede their child.", nameof(parents));
            localBindPose[bone].Validate();
            Matrix4x4 local = localBindPose[bone].ToMatrix();
            world[bone] = parent < 0 ? local : local * world[parent];
            if (!BrowserBoneTransform.Finite(world[bone]) || !Matrix4x4.Invert(world[bone], out _inverseBind[bone]) ||
                !BrowserBoneTransform.Finite(_inverseBind[bone]))
                throw new ArgumentException("The skeleton bind hierarchy must be finite and invertible.", nameof(localBindPose));
        }
    }

    public int BoneCount => _parents.Length;

    internal void Evaluate(ReadOnlySpan<BrowserBoneTransform> localPose, Span<Matrix4x4> world, Span<Matrix4x4> palette)
    {
        for (int bone = 0; bone < _parents.Length; bone++)
        {
            Matrix4x4 local = localPose[bone].ToMatrix();
            int parent = _parents[bone];
            world[bone] = parent < 0 ? local : local * world[parent];
            palette[bone] = _inverseBind[bone] * world[bone];
            if (!BrowserBoneTransform.Finite(palette[bone]))
                throw new InvalidOperationException("Animated bone hierarchy exceeded the finite matrix range.");
        }
    }
}
