using System.Numerics;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Browser;

public sealed partial class BrowserSceneSession
{
    private BrowserCpuAnimator? _animator;
    private BrowserCpuSkinnedMesh? _animatedMesh;
    private BrowserMeshComponent? _animatedComponent;
    private bool _animationDirty;
    private bool _computeSkinning;
    private BrowserSkinningData? _computeSkinningData;
    private SkinPaletteMatrix[]? _computePalette;
    private readonly Vector2[] _activeAnimationMorphs = new Vector2[1];
    private Vector3[]? _referenceMorphDeltas;
    private int _computeSkinningMesh;

    public bool HasCpuAnimation => _animator is not null;
    public string SkinningProfile => _computeSkinning ? "Compute" : "Cpu";
    public float AnimationMovementBlend => _animator?.MovementBlend ?? 0;

    /// <summary>Creates a two-bone weighted strip with an interpolated idle and movement clip in the built-in reference world.</summary>
    private void InitializeAnimation()
    {
        if (_snapshot is not null || _animator is not null)
            return;
        BrowserBoneTransform root = BrowserBoneTransform.Identity;
        BrowserBoneTransform tip = new(new Vector3(0, 0.9f, 0), Quaternion.Identity, Vector3.One);
        BrowserSkeleton skeleton = new([-1, 0], [root, tip]);
        BrowserAnimationClip idle = CreateReferenceClip(3, 0.12f, 0.025f);
        BrowserAnimationClip moving = CreateReferenceClip(0.8f, 0.7f, 0.12f);
        BrowserCpuAnimator animator = new(skeleton, idle, moving);

        const int rows = 7;
        float[] vertices = new float[rows * 2 * 5];
        uint[] indices = new uint[(rows - 1) * 6];
        BrowserSkinWeights[] weights = new BrowserSkinWeights[rows * 2];
        for (int row = 0; row < rows; row++)
        {
            float y = row * 0.3f;
            // Both reference paths use the canonical UNorm8 weights, including quantization.
            float tipWeight = MathF.Round(Math.Clamp((y - 0.6f) / 0.6f, 0, 1) * 255) / 255;
            for (int side = 0; side < 2; side++)
            {
                int vertex = row * 2 + side;
                int offset = vertex * 5;
                vertices[offset] = side == 0 ? -0.28f : 0.28f;
                vertices[offset + 1] = y;
                vertices[offset + 2] = 0;
                vertices[offset + 3] = side;
                vertices[offset + 4] = 1 - y / 1.8f;
                weights[vertex] = new BrowserSkinWeights(0, 1, 0, 0, new Vector4(1 - tipWeight, tipWeight, 0, 0));
            }
            if (row == rows - 1)
                continue;
            int first = row * 6;
            uint lower = (uint)(row * 2);
            indices[first] = lower;
            indices[first + 1] = lower + 1;
            indices[first + 2] = lower + 3;
            indices[first + 3] = lower;
            indices[first + 4] = lower + 3;
            indices[first + 5] = lower + 2;
        }
        BrowserMeshData mesh = new(vertices, indices);
        uint[] coreIndices = new uint[mesh.VertexCount];
        uint[] coreWeights = new uint[mesh.VertexCount];
        Vector3[] normals = new Vector3[mesh.VertexCount];
        Vector4[] tangents = new Vector4[mesh.VertexCount];
        Vector3[] morphDeltas = new Vector3[mesh.VertexCount];
        uint[] sparseRecords = new uint[mesh.VertexCount * 4];
        uint[] quantizedDeltas = new uint[(mesh.VertexCount + 1) * 2];
        for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
        {
            uint tipWeight = (uint)MathF.Round(weights[vertex].Weights.Y * 255);
            coreIndices[vertex] = 1u << 8;
            coreWeights[vertex] = (255u - tipWeight) | (tipWeight << 8);
            weights[vertex] = new BrowserSkinWeights(0, 1, 0, 0,
                new Vector4((255u - tipWeight) / 255f, tipWeight / 255f, 0, 0));
            normals[vertex] = Vector3.UnitZ;
            tangents[vertex] = new Vector4(1, 0, 0, 1);
            short x = (short)MathF.Round((vertex % 2 == 0 ? -1 : 1) * vertices[vertex * 5 + 1] / 1.8f * 32767);
            morphDeltas[vertex] = new Vector3(x / 32767f * 0.12f, 0, 0);
            sparseRecords[vertex * 4] = (uint)vertex;
            sparseRecords[vertex * 4 + 1] = (uint)vertex + 1;
            quantizedDeltas[(vertex + 1) * 2] = unchecked((ushort)x);
        }
        _computeSkinningData = new BrowserSkinningData(mesh, skeleton.BoneCount, 1, coreIndices, coreWeights,
            normals, tangents, shapeRanges: [0, (uint)mesh.VertexCount, 0, 0], sparseRecords: sparseRecords,
            quantizedDeltas: quantizedDeltas, quantizationMetadata: [Vector4.Zero, Vector4.Zero, new Vector4(0.12f, 0, 0, 0), Vector4.Zero]);
        _computePalette = new SkinPaletteMatrix[skeleton.BoneCount];
        _referenceMorphDeltas = morphDeltas;
        BrowserCpuSkinnedMesh animatedMesh = new(skeleton, mesh, weights);
        animatedMesh.Update(animator);
        Transform transform = (Transform)BrowserStaticRegistrations.CreateRequiredTransform(BrowserStaticRegistrations.TransformId);
        transform.Translation = new Vector3(1.8f, 0, -1.5f);
        BrowserMeshComponent component = AddRenderableCore(mesh,
            new BrowserMaterialData(new Vector4(1, 0.75f, 0.35f, 1), _checkerMaterial?.Texture, shading: "lambert"),
            transform, _customRenderables);
        _uploads.EnsureCapacity(1, vertices.Length * sizeof(float));
        _animator = animator;
        _animatedMesh = animatedMesh;
        _animatedComponent = component;
        _animationDirty = true;
    }

    /// <summary>Selects deformation explicitly before graphics allocation; unsupported requests fail at startup.</summary>
    public void SetComputeSkinning(bool enabled)
    {
        ThrowIfFrameBusy();
        if (_graphicsInitialized)
            throw new InvalidOperationException("Restart the canvas to change its compute skinning profile.");
        if (enabled && (_renderer is not IBrowserComputeSkinningCapability || _animator is null))
            throw new NotSupportedException("Compute skinning requires the packed deformation capability and an admitted animated scene.");
        _computeSkinning = enabled;
    }

    private void InitializeComputeAnimationGraphics()
    {
        if (!_computeSkinning)
            return;
        if (_renderer is not IBrowserComputeSkinningCapability compute || _computeSkinningData is null ||
            _animatedComponent is null || _animatedComponent.MeshHandle == 0)
            throw new InvalidOperationException("Compute skinning startup requires its admitted mesh and immutable inputs.");
        _computeSkinningMesh = _animatedComponent.MeshHandle;
        compute.ConfigureComputeSkinning(BrowserResourceHandle.FromPacked(_computeSkinningMesh), _computeSkinningData);
        _animationDirty = true;
    }

    private static BrowserAnimationClip CreateReferenceClip(float duration, float bend, float sway)
    {
        BrowserBoneTransform[] frames = new BrowserBoneTransform[10];
        for (int frame = 0; frame < 5; frame++)
        {
            float wave = frame switch { 1 => 1, 3 => -1, _ => 0 };
            frames[frame * 2] = new BrowserBoneTransform(new Vector3(0, MathF.Abs(wave) * sway, 0),
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -wave * sway), Vector3.One);
            frames[frame * 2 + 1] = new BrowserBoneTransform(new Vector3(0, 0.9f, 0),
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, wave * bend), Vector3.One);
        }
        return new BrowserAnimationClip(2, duration, frames);
    }

    /// <summary>Runs after fixed-step collision and motion; all arrays are retained from scene creation.</summary>
    private void AdvanceAnimation(float deltaSeconds, float movementSpeed)
    {
        if (_animator is null || _animatedMesh is null || _animatedComponent is null ||
            !_animatedComponent.RenderEnabled || !ReferenceEquals(_animatedComponent.Mesh, _animatedMesh.Mesh))
            return;
        _animator.Advance(deltaSeconds, movementSpeed);
        _animationDirty = true;
    }

    /// <summary>Skins and uploads only the final simulated pose once per render frame, before visibility collection.</summary>
    private void PublishAnimation()
    {
        if (!_animationDirty || _animator is null || _animatedMesh is null || _animatedComponent is null ||
            !_animatedComponent.RenderEnabled || !ReferenceEquals(_animatedComponent.Mesh, _animatedMesh.Mesh) ||
            _animatedComponent.MeshHandle == 0)
            return;
        float morphWeight = 0.25f + _animator.MovementBlend * 0.75f;
        if (_computeSkinning)
        {
            if (_renderer is not IBrowserComputeSkinningCapability compute || _computePalette is null ||
                _animatedComponent.MeshHandle != _computeSkinningMesh)
                throw new InvalidOperationException("The compute deformation resource binding is obsolete; recreate its scene.");
            ReadOnlySpan<Matrix4x4> palette = _animator.Palette;
            for (int bone = 0; bone < palette.Length; bone++)
                _computePalette[bone] = SkinPaletteMatrix.FromRowVectorMatrix(palette[bone]);
            _activeAnimationMorphs[0] = new Vector2(0, morphWeight);
            compute.UpdateComputeSkinning(BrowserResourceHandle.FromPacked(_computeSkinningMesh), _computePalette, _activeAnimationMorphs);
            _animationDirty = false;
            return;
        }
        _animatedMesh.Update(_animator, _referenceMorphDeltas, morphWeight);
        _uploads.Begin(Id);
        try
        {
            _uploads.AddMeshVertices(BrowserResourceHandle.FromPacked(_animatedComponent.MeshHandle), 0, _animatedMesh.Vertices);
            _uploads.Seal();
            _renderer.SubmitUploads(_uploads);
            _animationDirty = false;
        }
        catch
        {
            _uploads.Abort();
            throw;
        }
    }

    /// <summary>The initial descriptor has bind-pose bounds, so this dynamic reference bypasses static frustum rejection.</summary>
    private bool IsAnimationMesh(BrowserMeshData mesh) => _animatedMesh is not null && ReferenceEquals(_animatedMesh.Mesh, mesh);

    /// <summary>Drops CPU animation state; the regular scene resource registry owns GPU release and node teardown.</summary>
    private void DisposeAnimation()
    {
        _animationDirty = false;
        _animatedComponent = null;
        _animatedMesh = null;
        _animator = null;
        _computeSkinningData = null;
        _computePalette = null;
        _referenceMorphDeltas = null;
        _computeSkinningMesh = 0;
    }
}
