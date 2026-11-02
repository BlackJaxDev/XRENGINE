using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Deforms skinned and blendshape meshes for the GPU indirect path of the default pipeline. That
/// path draws from the shared mesh atlas with a generated vertex shader that has no bone inputs.
/// This pass runs the deformation compute shader, then writes each result over the mesh's atlas
/// range, so all later passes in the frame draw the deformed vertices.
/// </summary>
/// <remarks>
/// The atlas holds one range per mesh, so two renderers that share one deformed mesh also share
/// one result. Previous-frame positions are not published, so these draws have no skinned
/// motion vectors.
/// </remarks>
internal sealed class SkinnedAtlasPublisher
{
    private const string ShaderPath = "Compute/Animation/SkinnedAtlasCopy.comp";
    private const uint ThreadGroupSize = 256u;

    public static SkinnedAtlasPublisher Instance { get; } = new();

    private readonly HashSet<XRMeshRenderer> _visited = new(ReferenceEqualityComparer.Instance);
    private XRDataBuffer? _emptySource;
    private XRRenderProgram? _program;
    private ulong _currentSequence;
    private ulong _committedSequence;

    /// <summary>Publishes the current deformation of every resident deformed mesh in the scene.</summary>
    /// <param name="scene">The scene that owns the mesh atlas.</param>
    /// <param name="history">The history ledger state of the viewport that renders this frame, if any.</param>
    public void Run(GPUScene scene, RenderFrameViewHistorySnapshot? history)
    {
        // The pending maximum is the current frame, because the viewport issues it before the pipeline runs.
        _currentSequence = history is { PendingCount: > 0 } pending ? pending.PendingMaximumSequence : 0UL;
        _committedSequence = history is { CommittedHistoryValid: true } committed ? committed.CommittedSequence : 0UL;

        scene.RequestMeshSubmissionPublication();
        if (!scene.TryAcquireMeshSubmissionPublication(out GpuMeshSubmissionPublicationLease lease))
            return;

        using (lease)
        {
            _visited.Clear();
            foreach (ref readonly GpuMeshSubmissionRecord record in lease.Publication.Records)
            {
                if ((record.HasSkinning || record.HasBlendshapes) && _visited.Add(record.Renderer))
                    Publish(scene, record.Renderer);
            }
        }
    }

    private void Publish(GPUScene scene, XRMeshRenderer renderer)
    {
        XRMesh? mesh = renderer.Mesh;
        if (mesh is null || mesh.VertexCount <= 0)
            return;

        if (mesh.Interleaved)
        {
            Debug.RenderingWarningEvery(
                $"SkinnedAtlasPublisher.Interleaved.{mesh.Name}",
                TimeSpan.FromSeconds(10),
                "GPU indirect draws of mesh '{0}' are not deformed, because its vertices are interleaved. Use CpuDirect for this mesh.",
                mesh.Name ?? "<unnamed>");
            return;
        }

        if (!scene.TryGetAtlasVertexRange(mesh, out XRDataBuffer? atlasPositions, out XRDataBuffer? atlasNormals,
                out XRDataBuffer? atlasTangents, out uint firstVertex) ||
            !SkinningPrepassDispatcher.Instance.TryRunForMeshAtlas(renderer, _currentSequence, _committedSequence, out XRDataBuffer? positions))
        {
            Debug.RenderingWarningEvery(
                $"SkinnedAtlasPublisher.NoOutput.{mesh.Name}",
                TimeSpan.FromSeconds(10),
                "GPU indirect draws of mesh '{0}' are not deformed this frame, because it has no atlas range or deformation output.",
                mesh.Name ?? "<unnamed>");
            return;
        }

        XRRenderProgram? program = GetProgram();
        if (program is null)
            return;

        var (_, normals, tangents, _) = SkinningPrepassDispatcher.Instance.GetSkinnedBuffers(renderer);
        XRDataBuffer empty = _emptySource ??= CreateEmptySource();
        bool copyNormals = normals is not null && atlasNormals is not null;
        bool copyTangents = tangents is not null && atlasTangents is not null;

        program.BindBuffer(positions!, 0);
        program.BindBuffer(normals ?? empty, 1);
        program.BindBuffer(tangents ?? empty, 2);
        program.BindBuffer(atlasPositions!, 3);
        program.BindBuffer(atlasNormals ?? empty, 4);
        program.BindBuffer(atlasTangents ?? empty, 5);

        uint vertexCount = (uint)mesh.VertexCount;
        program.Uniform("firstVertex", firstVertex);
        program.Uniform("vertexCount", vertexCount);
        program.Uniform("hasNormals", copyNormals ? 1 : 0);
        program.Uniform("hasTangents", copyTangents ? 1 : 0);
        program.DispatchCompute(
            Math.Max(1u, (vertexCount + ThreadGroupSize - 1u) / ThreadGroupSize),
            1u,
            1u,
            EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.VertexAttribArray);
    }

    private XRRenderProgram? GetProgram()
    {
        if (_program is null)
        {
            XRShader? shader = ShaderHelper.LoadEngineShader(ShaderPath, EShaderType.Compute);
            if (shader is null)
                return null;
            _program = new XRRenderProgram(true, false, shader);
        }

        if (!_program.IsLinked && AbstractRenderer.Current is { } renderer)
        {
            renderer.GetOrCreateAPIRenderObject(_program, generateNow: true);
            _program.Link();
        }
        return _program.IsLinked ? _program : null;
    }

    private static XRDataBuffer CreateEmptySource()
        => new("SkinnedAtlasEmptySource", EBufferTarget.ShaderStorageBuffer, 1u, EComponentType.Float, 4, false, false)
        {
            Usage = EBufferUsage.DynamicDraw,
            DisposeOnPush = false,
        };
}
