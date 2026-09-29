using System.Numerics;

namespace XREngine.Rendering;

/// <summary>CPU linear-blend skinning for the browser position/UV vertex profile; retains bind data and upload storage separately.</summary>
public sealed class BrowserCpuSkinnedMesh
{
    public const int MaximumVertices = 16384;
    private readonly BrowserSkeleton _skeleton;
    private readonly float[] _bindVertices;
    private readonly float[] _vertices;
    private readonly BrowserSkinWeights[] _weights;

    public BrowserCpuSkinnedMesh(BrowserSkeleton skeleton, BrowserMeshData mesh, ReadOnlySpan<BrowserSkinWeights> weights)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.VertexCount > MaximumVertices || weights.Length != mesh.VertexCount)
            throw new ArgumentException("CPU skinning requires one four-influence record per vertex and at most 16384 vertices.");
        for (int i = 0; i < weights.Length; i++)
            weights[i].Validate(skeleton.BoneCount);
        _skeleton = skeleton;
        Mesh = mesh;
        _bindVertices = mesh.CopyVertices();
        _vertices = mesh.CopyVertices();
        _weights = weights.ToArray();
        BoundsMinimum = mesh.BoundsMinimum;
        BoundsMaximum = mesh.BoundsMaximum;
    }

    /// <summary>Immutable descriptor used for initial allocation; its bind bounds must not cull the animated output.</summary>
    public BrowserMeshData Mesh { get; }
    public ReadOnlySpan<float> Vertices => _vertices;
    public Vector3 BoundsMinimum { get; private set; }
    public Vector3 BoundsMaximum { get; private set; }

    public void Update(BrowserCpuAnimator animator)
    {
        ArgumentNullException.ThrowIfNull(animator);
        if (!ReferenceEquals(animator.Skeleton, _skeleton))
            throw new ArgumentException("The skinned mesh and animator must share their skeleton identity.", nameof(animator));
        ReadOnlySpan<Matrix4x4> palette = animator.Palette;
        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        for (int vertex = 0; vertex < _weights.Length; vertex++)
        {
            int offset = vertex * 5;
            Vector3 bind = new(_bindVertices[offset], _bindVertices[offset + 1], _bindVertices[offset + 2]);
            BrowserSkinWeights skin = _weights[vertex];
            Vector3 position = Vector3.Transform(bind, palette[skin.Bone0]) * skin.Weights.X +
                Vector3.Transform(bind, palette[skin.Bone1]) * skin.Weights.Y +
                Vector3.Transform(bind, palette[skin.Bone2]) * skin.Weights.Z +
                Vector3.Transform(bind, palette[skin.Bone3]) * skin.Weights.W;
            if (!BrowserBoneTransform.Finite(position))
                throw new InvalidOperationException("CPU skinning produced a non-finite position.");
            _vertices[offset] = position.X;
            _vertices[offset + 1] = position.Y;
            _vertices[offset + 2] = position.Z;
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }
        BoundsMinimum = minimum;
        BoundsMaximum = maximum;
    }
}
