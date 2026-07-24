using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.Components.Scene.Mesh;

/// <summary>
/// Combines independently transformed, differently colored boxes into one dynamic opaque draw.
/// Positions and flat normals are baked into a retained vertex buffer immediately before render,
/// avoiding one scene render command and draw call per box.
/// </summary>
[Serializable]
[Category("Rendering")]
[DisplayName("Lit Box Batch")]
[Description("Renders multiple independently transformed boxes as one lit, depth-writing opaque mesh.")]
public sealed class LitBoxBatchComponent : XRComponent, IRenderable
{
    private const int VerticesPerBox = 24;
    private const int IndicesPerBox = 36;

    private static readonly Vector3[] s_faceNormals =
    [
        -Vector3.UnitX,
        Vector3.UnitX,
        Vector3.UnitY,
        -Vector3.UnitY,
        Vector3.UnitZ,
        -Vector3.UnitZ,
    ];

    // Four corners per face, ordered for triangles 0-1-2 and 0-2-3.
    private static readonly Vector3[] s_faceCornerSigns =
    [
        new(-1.0f, -1.0f, -1.0f), new(-1.0f, -1.0f,  1.0f), new(-1.0f,  1.0f,  1.0f), new(-1.0f,  1.0f, -1.0f),
        new( 1.0f, -1.0f,  1.0f), new( 1.0f, -1.0f, -1.0f), new( 1.0f,  1.0f, -1.0f), new( 1.0f,  1.0f,  1.0f),
        new(-1.0f,  1.0f,  1.0f), new( 1.0f,  1.0f,  1.0f), new( 1.0f,  1.0f, -1.0f), new(-1.0f,  1.0f, -1.0f),
        new(-1.0f, -1.0f, -1.0f), new( 1.0f, -1.0f, -1.0f), new( 1.0f, -1.0f,  1.0f), new(-1.0f, -1.0f,  1.0f),
        new(-1.0f, -1.0f,  1.0f), new( 1.0f, -1.0f,  1.0f), new( 1.0f,  1.0f,  1.0f), new(-1.0f,  1.0f,  1.0f),
        new( 1.0f, -1.0f, -1.0f), new(-1.0f, -1.0f, -1.0f), new(-1.0f,  1.0f, -1.0f), new( 1.0f,  1.0f, -1.0f),
    ];

    private List<LitBoxBatchEntry> _entries = [];
    private readonly RenderCommandMesh3D _renderCommand;
    private readonly RenderInfo3D _renderInfo;
    private readonly RenderInfo[] _renderInfos;

    private TransformBase[] _boxTransforms = [];
    private XRMesh? _mesh;
    private XRMeshRenderer? _renderer;
    private XRMaterial? _material;
    private bool _built;
    private bool _performedFullUpload;

    public LitBoxBatchComponent()
    {
        _renderCommand = new RenderCommandMesh3D(EDefaultRenderPass.OpaqueDeferred)
        {
            ForceCpuRendering = true,
            Instances = 1u,
            WorldMatrix = Matrix4x4.Identity,
            WorldMatrixIsModelMatrix = false,
            GpuProfilingLabel = "LitBoxBatch",
        };

        _renderInfo = RenderInfo3D.New(this, _renderCommand);
        _renderInfo.CastsShadows = false;
        _renderInfo.ReceivesShadows = true;
        _renderInfo.LocalCullingVolume = null;
        _renderInfos = [_renderInfo];
    }

    /// <summary>
    /// Number of boxes retained by this batch.
    /// </summary>
    [YamlIgnore]
    public int BoxCount => Entries.Count;

    /// <summary>
    /// Persistent box descriptions used to rebuild runtime render resources after scene
    /// serialization, including the clean Edit-to-Play world clone.
    /// </summary>
    [Browsable(false)]
    public List<LitBoxBatchEntry> Entries
    {
        get => _entries;
        set
        {
            if (_built)
                throw new InvalidOperationException("Batch entries cannot be replaced after the lit box batch has been built.");

            SetField(ref _entries, value ?? []);
        }
    }

    /// <summary>
    /// Whether the retained mesh and render command have been built.
    /// </summary>
    [YamlIgnore]
    public bool IsBuilt => _built;

    /// <inheritdoc />
    public RenderInfo[] RenderedObjects => _renderInfos;

    /// <summary>
    /// Adds a box whose pose is read from <paramref name="transform"/> immediately before drawing.
    /// All boxes must be added before <see cref="Build"/>.
    /// </summary>
    public void AddBox(TransformBase transform, Vector3 halfExtents, ColorF4 color)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (_built)
            throw new InvalidOperationException("Boxes cannot be added after the lit box batch has been built.");
        if (halfExtents.X <= 0.0f || halfExtents.Y <= 0.0f || halfExtents.Z <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(halfExtents), "Box half extents must be positive.");

        SceneNode owner = transform.SceneNode
            ?? throw new InvalidOperationException("A batched box transform must belong to a scene node.");
        Entries.Add(new LitBoxBatchEntry(
            owner.ID,
            halfExtents,
            new Vector4(color.R, color.G, color.B, color.A)));
    }

    /// <summary>
    /// Builds the retained mesh after all boxes have been registered.
    /// </summary>
    public void Build()
    {
        if (_built)
            return;
        if (Entries.Count == 0)
            throw new InvalidOperationException("At least one box is required before building a lit box batch.");
        if (Entries.Count > ushort.MaxValue / VerticesPerBox)
            throw new InvalidOperationException("The lit box batch exceeds the 16-bit mesh index limit.");

        _boxTransforms = ResolveBoxTransforms();
        Vertex[] vertices = new Vertex[Entries.Count * VerticesPerBox];
        List<ushort> indices = new(Entries.Count * IndicesPerBox);

        for (int boxIndex = 0; boxIndex < Entries.Count; boxIndex++)
        {
            LitBoxBatchEntry entry = Entries[boxIndex];
            int baseVertex = boxIndex * VerticesPerBox;
            Matrix4x4 matrix = _boxTransforms[boxIndex].RenderMatrix;

            for (int faceIndex = 0; faceIndex < s_faceNormals.Length; faceIndex++)
            {
                Vector3 worldNormal = TransformNormal(s_faceNormals[faceIndex], matrix);
                int faceVertex = baseVertex + faceIndex * 4;
                for (int cornerIndex = 0; cornerIndex < 4; cornerIndex++)
                {
                    Vector3 localPosition = s_faceCornerSigns[faceIndex * 4 + cornerIndex] * entry.HalfExtents;
                    vertices[faceVertex + cornerIndex] = new Vertex(
                        Vector3.Transform(localPosition, matrix),
                        worldNormal)
                    {
                        ColorSets = [entry.Color],
                    };
                }

                indices.Add((ushort)faceVertex);
                indices.Add((ushort)(faceVertex + 1));
                indices.Add((ushort)(faceVertex + 2));
                indices.Add((ushort)faceVertex);
                indices.Add((ushort)(faceVertex + 2));
                indices.Add((ushort)(faceVertex + 3));
            }
        }

        _mesh = new XRMesh(vertices, indices);
        ConfigureDynamicBuffer(_mesh.InterleavedVertexBuffer);
        ConfigureDynamicBuffer(_mesh.PositionsBuffer);
        ConfigureDynamicBuffer(_mesh.NormalsBuffer);

        _material = XRMaterial.CreateLitVertexColorMaterialDeferred();
        _renderer = new XRMeshRenderer(_mesh, _material);
        _renderer.PreparingRenderData += PrepareRenderData;

        _renderCommand.Mesh = _renderer;
        _built = true;
    }

    /// <summary>
    /// Rebuilds constructor-owned GPU resources after the serialized entry descriptions have
    /// been restored and the owning scene hierarchy is fully available.
    /// </summary>
    protected override void OwningSceneNodePostDeserialize()
    {
        base.OwningSceneNodePostDeserialize();

        if (Entries.Count > 0)
            Build();
    }

    protected override void OnDestroying()
    {
        _renderCommand.Mesh = null;
        if (_renderer is not null)
            _renderer.PreparingRenderData -= PrepareRenderData;

        _renderer?.Destroy();
        _renderer = null;
        _mesh?.Destroy();
        _mesh = null;
        _material?.Destroy();
        _material = null;
        _boxTransforms = [];

        base.OnDestroying();
    }

    private void PrepareRenderData()
    {
        XRMesh? mesh = _mesh;
        if (mesh is null)
            return;

        for (int boxIndex = 0; boxIndex < Entries.Count; boxIndex++)
        {
            LitBoxBatchEntry entry = Entries[boxIndex];
            uint baseVertex = (uint)(boxIndex * VerticesPerBox);
            Matrix4x4 matrix = _boxTransforms[boxIndex].RenderMatrix;

            for (int faceIndex = 0; faceIndex < s_faceNormals.Length; faceIndex++)
            {
                Vector3 worldNormal = TransformNormal(s_faceNormals[faceIndex], matrix);
                uint faceVertex = baseVertex + (uint)(faceIndex * 4);
                for (int cornerIndex = 0; cornerIndex < 4; cornerIndex++)
                {
                    Vector3 localPosition = s_faceCornerSigns[faceIndex * 4 + cornerIndex] * entry.HalfExtents;
                    uint vertexIndex = faceVertex + (uint)cornerIndex;
                    mesh.SetPosition(vertexIndex, Vector3.Transform(localPosition, matrix));
                    mesh.SetNormal(vertexIndex, worldNormal);
                }
            }
        }

        if (mesh.Interleaved)
        {
            UploadDynamicBuffer(mesh.InterleavedVertexBuffer);
        }
        else
        {
            UploadDynamicBuffer(mesh.PositionsBuffer);
            UploadDynamicBuffer(mesh.NormalsBuffer);
        }

        _performedFullUpload = true;
    }

    private TransformBase[] ResolveBoxTransforms()
    {
        Dictionary<Guid, TransformBase> transformsByNodeId = new(Entries.Count);
        foreach (SceneNode node in SceneNodePrefabUtility.EnumerateHierarchy(SceneNode))
            transformsByNodeId[node.ID] = node.Transform;

        TransformBase[] transforms = new TransformBase[Entries.Count];
        for (int index = 0; index < Entries.Count; index++)
        {
            LitBoxBatchEntry entry = Entries[index];
            if (!transformsByNodeId.TryGetValue(entry.SceneNodeId, out TransformBase? transform))
                throw new InvalidOperationException(
                    $"Lit box batch entry {index} references missing scene node '{entry.SceneNodeId}'.");

            transforms[index] = transform;
        }

        return transforms;
    }

    private void UploadDynamicBuffer(XRDataBuffer? buffer)
    {
        if (buffer is null)
            return;

        if (_performedFullUpload)
            buffer.PushSubData();
        else
            buffer.PushData();
    }

    private static void ConfigureDynamicBuffer(XRDataBuffer? buffer)
    {
        if (buffer is null)
            return;

        buffer.Usage = EBufferUsage.StreamDraw;
        buffer.DisposeOnPush = false;
    }

    // Physics fixture transforms contain translation and rotation only, so transforming and
    // renormalizing the face normal is equivalent to the inverse-transpose normal transform.
    private static Vector3 TransformNormal(Vector3 localNormal, Matrix4x4 matrix)
    {
        Vector3 transformed = Vector3.TransformNormal(localNormal, matrix);
        float lengthSquared = transformed.LengthSquared();
        return lengthSquared > 1.0e-12f
            ? transformed / MathF.Sqrt(lengthSquared)
            : localNormal;
    }
}
