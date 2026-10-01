using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.WebGPU;

/// <summary>Submits canonical engine mesh draws using cooked programs and per-draw uniform snapshots.</summary>
public sealed class WebGpuMeshRenderer(WebGpuRendererHost renderer, XRMeshRenderer.BaseVersion data)
    : WebGpuObject<XRMeshRenderer.BaseVersion>(renderer, data), IApiMeshRenderer, IRenderPreparationState
{
    private readonly record struct DrawKey(WebGpuMaterial Material, WebGpuRasterState State,
        WebGpuFrameBuffer? FrameBuffer, ulong AttachmentRevision);
    private readonly Dictionary<DrawKey, WebGpuMeshDraw> _draws = [];
    private XRMesh? _mesh;
    private long _geometryRevision;
    private long _bufferRevision;
    private ulong _surfaceGeneration;
    private string _lastPrepareDetail = "NeverPrepared";
    private bool _generated;

    public override bool IsGenerated => _generated;
    public bool IsPreparedForRendering => _generated;
    public string LastPrepareDetail => _lastPrepareDetail;

    public override void Generate() => TryPrepareForRendering();

    public bool TryPrepareForRendering()
    {
        ValidateOwnerGeneration();
        XRMaterial? material = Data.Parent.Material;
        if (material is null)
            return Pending("MaterialMissing");
        Renderer.ApplyRenderParameters(material.RenderOptions);
        return TryPrepareDraw(material, out _, out _);
    }

    private bool TryPrepareDraw(XRMaterial material, out WebGpuMaterial? apiMaterial, out WebGpuMeshDraw? draw)
    {
        apiMaterial = null;
        draw = null;
        XRMesh? mesh = Data.Parent.Mesh;
        if (mesh is null)
            return Pending("MeshMissing");
        if (mesh.Type != EPrimitiveType.Triangles || mesh.HasSkinning || mesh.HasBlendshapes)
            throw Unsupported("the current engine vertex profile admits rigid indexed triangles only");
        if (Renderer.CurrentFrameOutput is not { } output)
            return Pending("OutputPending");
        WebGpuFrameBuffer? frameBuffer = Renderer.GetBoundEngineFrameBuffer();
        ulong attachmentRevision = frameBuffer?.Revision ?? 0;
        if (!ReferenceEquals(_mesh, mesh) || _geometryRevision != mesh.GeometryRevision ||
            _bufferRevision != mesh.Buffers.MutationRevision || _surfaceGeneration != output.TargetGeneration)
        {
            DestroyDraws();
            SetField(ref _mesh, mesh);
            SetField(ref _geometryRevision, mesh.GeometryRevision);
            SetField(ref _bufferRevision, mesh.Buffers.MutationRevision);
            SetField(ref _surfaceGeneration, output.TargetGeneration);
        }
        apiMaterial = (WebGpuMaterial)Renderer.GetOrCreateAPIRenderObject(material)!;
        if (!apiMaterial.TryPrepareForRendering())
            return Pending("ProgramsPending");
        XRDataBuffer? indices = mesh.GetIndexBuffer(EPrimitiveType.Triangles, out var indexSize);
        if (indices is null)
            return Pending("IndicesPending");
        DrawKey key = new(apiMaterial, Renderer.RasterState, frameBuffer, attachmentRevision);
        if (!_draws.TryGetValue(key, out draw))
        {
            if (_draws.Count >= 32)
                throw Unsupported("the mesh exceeds the bounded 32 material/raster variants for its current resource generation");
            draw = new WebGpuMeshDraw(Renderer, apiMaterial.Program, mesh, indices, indexSize, key.State, output, frameBuffer);
            _draws.Add(key, draw);
        }
        if (!draw.IsReady)
            return Pending("PipelinesPending");
        SetField(ref _generated, true);
        SetField(ref _lastPrepareDetail, "Ready");
        return true;
    }

    public void Render(Matrix4x4 modelMatrix, Matrix4x4 previousModelMatrix, XRMaterial? materialOverride,
        RenderingParameters? renderOptionsOverride, uint instances, EMeshBillboardMode billboardMode,
        bool forceNoStereo, in AdvancedGpuSceneDrawIdentitySnapshot canonicalDrawIdentitySnapshot)
    {
        ValidateOwnerGeneration();
        if (instances == 0)
            return;
        if (instances != 1 || billboardMode != EMeshBillboardMode.None || RuntimeEngine.Rendering.State.IsStereoPass)
            throw Unsupported("the current vertex profile admits one rigid mono instance without billboarding");
        ResolvedMeshRenderMaterial resolved = MeshRenderMaterialResolver.Resolve(Data.Parent, materialOverride, instances);
        Renderer.ApplyRenderParameters(renderOptionsOverride ?? resolved.Material.RenderOptions);
        if (Renderer.RasterState.CullMode == ECullMode.Both)
            return;
        WebGpuMaterial material = (WebGpuMaterial)Renderer.GetOrCreateAPIRenderObject(resolved.Material)!;
        if (!material.TryPrepareForRendering())
        {
            Renderer.MarkEngineDrawPending();
            return;
        }
        XRCamera camera = RuntimeEngine.Rendering.State.RenderingCamera
            ?? throw new InvalidOperationException("WebGPU.Mesh.CameraMissing: an engine camera must own the current mesh pass.");
        if (camera.DepthMode != XRCamera.EDepthMode.Normal)
            throw Unsupported("the cooked coordinate contract has not admitted reversed-Z cameras");
        WebGpuRenderProgram program = material.Program;
        program.BeginResourceBindings();
        Renderer.SetEngineUniforms(program.Data, camera);
        program.SetMatrix("ModelMatrix", modelMatrix);
        program.SetMatrix("PreviousModelMatrix", previousModelMatrix);
        if (!Matrix4x4.Invert(modelMatrix, out Matrix4x4 inverseModel))
            throw Unsupported("the model transform is singular");
        program.SetMatrix("NormalMatrix", Matrix4x4.Transpose(inverseModel));
        Renderer.SetMaterialUniforms(resolved.Material, program.Data);
        Data.Parent.OnSettingUniforms(program.Data, program.Data);
        if (!ReferenceEquals(resolved.Material, Data.Parent.Material))
            resolved.Material.OnSettingVertexUniforms(program.Data);
        if (resolved.IsShadowVariant)
            MeshRenderMaterialResolver.ApplyShadowUniforms(program.Data, resolved.Material);
        bool deferMissingResources = PublishBindings(resolved.Material.BindingPublishers, program.Data);
        deferMissingResources |= PublishBindings(Data.Parent.BindingPublishers, program.Data);
        if (!program.TrySnapshotBindings(deferMissingResources, out WebGpuBindingSet? bindings) ||
            !TryPrepareDraw(resolved.Material, out _, out WebGpuMeshDraw? draw))
        {
            Renderer.MarkEngineDrawPending();
            return;
        }
        draw!.Record(bindings!);
    }

    private static bool PublishBindings(RenderBindingPublisherCollection publishers, XRRenderProgram program)
    {
        bool requiresReadyResources = false;
        for (int i = 0; i < publishers.Count; i++)
        {
            IRenderBindingPublisher publisher = publishers[i];
            if (publisher.Generation == 0)
                throw Unsupported("typed binding publishers require a nonzero content generation");
            publisher.PublishUniforms(program, program);
            if (publisher is not IRenderResourceBindingPublisher resources) continue;
            if (resources.ResourceGeneration == 0)
                throw Unsupported("typed resource publishers require a nonzero descriptor generation");
            requiresReadyResources |= resources.RequiresReadyDescriptorResources;
            resources.PublishResources(program, program);
        }
        return requiresReadyResources;
    }

    private bool Pending(string reason)
    {
        SetField(ref _lastPrepareDetail, reason);
        SetField(ref _generated, false);
        return false;
    }

    internal void DestroyDraws()
    {
        foreach (WebGpuMeshDraw draw in _draws.Values)
            draw.Dispose();
        _draws.Clear();
        SetField(ref _generated, false);
    }

    internal void ReleaseDrawsUsing(AbstractRenderAPIObject resource)
    {
        foreach (WebGpuMeshDraw draw in _draws.Values)
        {
            if (!draw.DependsOn(resource)) continue;
            DestroyDraws();
            return;
        }
    }

    public override void Destroy() => DestroyDraws();

    private static NotSupportedException Unsupported(string reason)
        => new($"WebGPU.Mesh.FeatureUnsupported: {reason}.");
}
