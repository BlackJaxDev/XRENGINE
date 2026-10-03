using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.WebGPU;

/// <summary>Submits canonical engine mesh draws using cooked programs and per-draw uniform snapshots.</summary>
public sealed partial class WebGpuMeshRenderer(WebGpuRendererHost renderer, XRMeshRenderer.BaseVersion data)
    : WebGpuObject<XRMeshRenderer.BaseVersion>(renderer, data), IApiMeshRenderer, IRenderPreparationState
{
    private readonly record struct DrawKey(WebGpuMaterial Material, WebGpuRasterState State,
        WebGpuFrameBuffer? FrameBuffer, ulong AttachmentRevision);
    private readonly Dictionary<DrawKey, WebGpuMeshDraw> _draws = [];
    private XRMesh? _mesh;
    private long _geometryRevision;
    private long _bufferRevision;
    private ulong _surfaceGeneration;
    private WebGpuMeshDeformation? _deformation;
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
        if (RuntimeEngine.Rendering.State.RenderingPipelineState?.ShadowPass == true)
            material = MeshRenderMaterialResolver.Resolve(Data.Parent, null, 1).Material;
        Renderer.ApplyRenderParameters(material.RenderOptions);
        return TryPrepareDraw(material, out _, out _, recordDeformation: false);
    }

    private bool TryPrepareDraw(XRMaterial material, out WebGpuMaterial? apiMaterial, out WebGpuMeshDraw? draw, bool recordDeformation = true)
    {
        apiMaterial = null;
        draw = null;
        XRMesh? mesh = Data.Parent.Mesh;
        if (mesh is null)
            return Pending("MeshMissing");
        if (mesh.Type != EPrimitiveType.Triangles)
            throw Unsupported("the current engine vertex profile admits indexed triangles only");
        if (Renderer.CurrentFrameOutput is not { } output)
            return Pending("OutputPending");
        if (!Renderer.TryPrepareMeshDeformation(Data.Parent, out WebGpuMeshDeformation? deformation, recordDeformation))
            return Pending("DeformationPending");
        WebGpuFrameBuffer? frameBuffer = Renderer.GetBoundEngineFrameBuffer();
        Renderer.ValidateEngineDrawArea();
        ulong attachmentRevision = frameBuffer?.Revision ?? 0;
        if (!ReferenceEquals(_mesh, mesh) || _geometryRevision != mesh.GeometryRevision ||
            _bufferRevision != mesh.Buffers.MutationRevision || _surfaceGeneration != output.TargetGeneration ||
            !ReferenceEquals(_deformation, deformation))
        {
            DestroyDraws();
            SetField(ref _mesh, mesh);
            SetField(ref _geometryRevision, mesh.GeometryRevision);
            SetField(ref _bufferRevision, mesh.Buffers.MutationRevision);
            SetField(ref _surfaceGeneration, output.TargetGeneration);
            SetField(ref _deformation, deformation);
        }
        apiMaterial = (WebGpuMaterial)Renderer.GetOrCreateAPIRenderObject(material)!;
        if (!apiMaterial.TryPrepareForRendering())
            return Pending("ProgramsPending");
        if (apiMaterial.UISemantic != EngineMaterialSemantic.None)
        {
            WebGpuRasterState state = Renderer.RasterState;
            if (frameBuffer is not null || output.Properties.SampleCount != 1 ||
                output.Properties.ColorEncoding is not ("rgba8unorm" or "bgra8unorm") ||
                state.DepthEnabled || state.DepthWrite || state.CullMode != ECullMode.None ||
                !state.BlendEnabled || state.SourceRgb != EBlendingFactor.SrcAlpha ||
                state.DestinationRgb != EBlendingFactor.OneMinusSrcAlpha ||
                state.SourceAlpha != EBlendingFactor.SrcAlpha ||
                state.DestinationAlpha != EBlendingFactor.OneMinusSrcAlpha ||
                state.RgbEquation != EBlendEquationMode.FuncAdd ||
                state.AlphaEquation != EBlendEquationMode.FuncAdd)
                throw Unsupported("screen UI requires the display output and exact straight-alpha, no-depth, no-cull raster state");
        }
        WebGpuInstanceStorageContract? instanceStorage = apiMaterial.InstanceStorageContract;
        WebGpuDataBuffer? instanceBuffer = null;
        uint instanceLimit = 0;
        if (instanceStorage is { } contract)
        {
            if (contract.StrideBytes <= 0 || contract.MaximumInstances == 0 || contract.MaximumInstances > 65536 ||
                !Data.Parent.Buffers.TryGetValue(contract.Name, out XRDataBuffer? buffer) ||
                !string.Equals(buffer.AttributeName, contract.Name, StringComparison.Ordinal) ||
                buffer.Target != EBufferTarget.ShaderStorageBuffer ||
                buffer.Length < contract.StrideBytes || !HasDeclaredStorage(apiMaterial.Program, contract.Name))
                throw Unsupported("the cooked instance storage contract does not match an owned renderer buffer and shader binding");
            if (apiMaterial.UISemantic != EngineMaterialSemantic.None &&
                buffer.Length % (uint)contract.StrideBytes != 0)
                throw Unsupported("the UI instance storage length does not match its declared packed stride");
            instanceBuffer = (WebGpuDataBuffer)Renderer.GetOrCreateAPIRenderObject(buffer, generateNow: false)!;
            instanceLimit = (uint)Math.Min(contract.MaximumInstances, buffer.Length / (uint)contract.StrideBytes);
        }
        if (apiMaterial.Program.Artifact.Pass == "tonemap")
        {
            bool supported = frameBuffer is null
                ? output.Properties.SampleCount == 1 && output.Properties.ColorEncoding is "rgba8unorm" or "bgra8unorm"
                : frameBuffer.SampleCount == 1 && frameBuffer.ColorFormats.Length == 1 && frameBuffer.ColorFormats[0] == "rgba8unorm";
            if (!supported)
                throw Unsupported("the gamma-encoding tonemap requires one non-sRGB RGBA8/BGRA8 output attachment with one sample");
        }
        XRDataBuffer? indices = mesh.GetIndexBuffer(EPrimitiveType.Triangles, out var indexSize);
        if (indices is null)
            return Pending("IndicesPending");
        DrawKey key = new(apiMaterial, Renderer.RasterState, frameBuffer, attachmentRevision);
        if (!_draws.TryGetValue(key, out draw))
        {
            if (_draws.Count >= 32)
                throw Unsupported("the mesh exceeds the bounded 32 material/raster variants for its current resource generation");
            draw = new WebGpuMeshDraw(Renderer, apiMaterial.Program, mesh, indices, indexSize, key.State,
                output, frameBuffer, instanceStorage, instanceBuffer, instanceLimit, deformation);
            _draws.Add(key, draw);
        }
        else draw.UpdateInstanceSource(instanceStorage, instanceBuffer, instanceLimit);
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
        if (billboardMode != EMeshBillboardMode.None || RuntimeEngine.Rendering.State.IsStereoPass)
            throw Unsupported("the current vertex profile admits mono rendering without billboarding");
        ResolvedMeshRenderMaterial resolved = MeshRenderMaterialResolver.Resolve(Data.Parent, materialOverride, instances);
        bool depthNormalPrepass = resolved.IsDepthNormalVariant &&
            RuntimeEngine.Rendering.State.RenderingPipelineState?.UseDepthNormalMaterialVariants == true;
        RenderingParameters selectedOptions = depthNormalPrepass
            ? resolved.Material.RenderOptions
            : renderOptionsOverride ?? resolved.Material.RenderOptions;
        Renderer.ApplyRenderParameters(selectedOptions);
        if (depthNormalPrepass)
        {
            // The depth/normal shader enforces its attachment state, but must cover the
            // same authored faces as the subsequent color draw, including local overrides.
            RenderingParameters coverage = renderOptionsOverride ??
                (materialOverride ?? Data.Parent.Material)?.RenderOptions ?? selectedOptions;
            Renderer.ApplyMeshFaceCoverage(coverage);
        }
        int resolutionTraceIndex = -1;
        if (Renderer.EngineMeshResolutionTraceEnabled && Renderer.IsRecordingEngineFrame)
        {
            XRMaterial? source = Data.Parent.Material;
            XRMesh? sourceMesh = Data.Parent.Mesh;
            var pass = RuntimeEngine.Rendering.State.RenderingPipelineState;
            XRMaterial? globalOverride = pass?.GlobalMaterialOverride;
            XRMaterial? pipelineOverride = pass?.OverrideMaterial;
            resolutionTraceIndex = Renderer.RecordEngineMeshResolution(new WebGpuMeshResolutionTrace(
                Renderer.EngineFrameSequence,
                Identity(Data.Parent), Data.Parent.Name,
                Identity(sourceMesh), sourceMesh?.Name, sourceMesh?.VertexCount ?? 0, instances,
                Data.Parent.HasRenderDataPreparation,
                Identity(source), source?.Name, source?.EngineSemantic ?? default,
                Identity(materialOverride), materialOverride?.EngineSemantic ?? default,
                Identity(globalOverride), globalOverride?.EngineSemantic ?? default,
                Identity(pipelineOverride), pipelineOverride?.EngineSemantic ?? default,
                Identity(resolved.Material), resolved.Material.Name, resolved.Material.EngineSemantic,
                resolved.Reason, pass?.ShadowPass ?? false, resolved.IsDepthNormalVariant,
                Renderer.BoundEngineFrameBufferName ?? "canvas",
                source?.RenderOptions.CullMode ?? ECullMode.None,
                resolved.Material.RenderOptions.CullMode, selectedOptions.CullMode,
                Renderer.RasterState.CullMode, renderOptionsOverride is not null, "RasterReady", null, null));
        }
        if (Renderer.RasterState.CullMode == ECullMode.Both)
        {
            if (resolutionTraceIndex >= 0) Renderer.UpdateEngineMeshResolutionStage(resolutionTraceIndex, "CulledBoth");
            return;
        }
        if (resolutionTraceIndex >= 0) Renderer.UpdateEngineMeshResolutionStage(resolutionTraceIndex, "PreparingMaterial");
        WebGpuMaterial material = (WebGpuMaterial)Renderer.GetOrCreateAPIRenderObject(resolved.Material)!;
        if (!material.TryPrepareForRendering())
        {
            if (resolutionTraceIndex >= 0) Renderer.UpdateEngineMeshResolutionStage(resolutionTraceIndex, "MaterialPending");
            Renderer.MarkEngineDrawPending();
            return;
        }
        if (resolutionTraceIndex >= 0) Renderer.UpdateEngineMeshResolutionStage(resolutionTraceIndex, "MaterialReady");
        if (instances > 1 && material.InstanceStorageContract is null)
            throw Unsupported("multiple instances require an explicit cooked storage profile");
        if (instances > 65536)
            throw Unsupported("the cooked instance profile admits at most 65536 instances per draw");
        if (Data.Parent.HasRenderDataPreparation)
            Data.Parent.OnPreparingRenderData();
        if (resolutionTraceIndex >= 0) Renderer.UpdateEngineMeshResolutionStage(resolutionTraceIndex, "DataPrepared");
        try
        {
            WebGpuRenderProgram program = material.Program;
            XRCamera? camera = RuntimeEngine.Rendering.State.RenderingCamera;
            // Fullscreen effect quads intentionally render without a mesh camera;
            // lit and prepass programs still declare camera-provided uniforms.
            if (camera is null && program.RequiresCameraUniforms)
                throw new InvalidOperationException("WebGPU.Mesh.CameraMissing: the cooked pass requires engine camera uniforms.");
            if (camera is not null && camera.DepthMode != XRCamera.EDepthMode.Normal)
                throw Unsupported("the cooked coordinate contract has not admitted reversed-Z cameras");
            program.BeginResourceBindings();
            if (camera is not null) Renderer.SetEngineUniforms(program.Data, camera);
            program.SetMatrix("ModelMatrix", modelMatrix);
            program.SetMatrix("PreviousModelMatrix", previousModelMatrix);
            if (!Matrix4x4.Invert(modelMatrix, out Matrix4x4 inverseModel))
                throw Unsupported("the model transform is singular");
            program.SetMatrix("NormalMatrix", Matrix4x4.Transpose(inverseModel));
            Renderer.SetMaterialUniforms(resolved.Material, program.Data);
            material.PublishSurface();
            Data.Parent.OnSettingUniforms(program.Data, program.Data);
            if (!ReferenceEquals(resolved.Material, Data.Parent.Material))
                resolved.Material.OnSettingVertexUniforms(program.Data);
            if (resolved.IsShadowVariant)
                MeshRenderMaterialResolver.ApplyShadowUniforms(program.Data, resolved.Material);
            bool deferMissingResources = PublishBindings(resolved.Material.BindingPublishers, program.Data);
            deferMissingResources |= PublishBindings(Data.Parent.BindingPublishers, program.Data);
            program.PublishStorageBindings(Data.Parent);
            if (!program.TrySnapshotBindings(deferMissingResources, out WebGpuBindingSet? bindings) ||
                !TryPrepareDraw(resolved.Material, out _, out WebGpuMeshDraw? draw))
            {
                if (resolutionTraceIndex >= 0) Renderer.UpdateEngineMeshResolutionStage(resolutionTraceIndex, "BindingsOrPipelinePending");
                Renderer.MarkEngineDrawPending();
                return;
            }
            draw!.Record(bindings!, instances);
            if (resolutionTraceIndex >= 0) Renderer.UpdateEngineMeshResolutionStage(resolutionTraceIndex, "Recorded");
        }
        catch (Exception error)
        {
            if (resolutionTraceIndex >= 0) Renderer.UpdateEngineMeshResolutionFailure(resolutionTraceIndex, error);
            throw;
        }
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

    private static bool HasDeclaredStorage(WebGpuRenderProgram program, string name)
    {
        foreach (var resource in program.Artifact.Resources)
            if (resource.Contract.Kind == XREngine.Rendering.Shaders.Compilation.ShaderAbiResourceKind.StorageBuffer &&
                resource.Contract.Name == name && resource.BindingType == "read-only-storage")
                return true;
        return false;
    }

    private static int Identity(object? value)
        => value is null ? 0 : RuntimeHelpers.GetHashCode(value);

    private bool Pending(string reason)
    {
        if (Renderer.IsRecordingEngineFrame)
            Renderer.MarkEngineDrawPending();
        SetField(ref _lastPrepareDetail, reason);
        SetField(ref _generated, false);
        return false;
    }

    internal void DestroyDraws()
    {
        DestroyIndirectDraws();
        foreach (WebGpuMeshDraw draw in _draws.Values)
            draw.Dispose();
        _draws.Clear();
        SetField(ref _generated, false);
    }

    internal void ReleaseDrawsUsing(AbstractRenderAPIObject resource)
    {
        foreach (WebGpuMeshDraw draw in _indirectDraws.Values)
        {
            if (!draw.DependsOn(resource)) continue;
            DestroyDraws();
            return;
        }
        foreach (WebGpuMeshDraw draw in _draws.Values)
        {
            if (!draw.DependsOn(resource)) continue;
            DestroyDraws();
            return;
        }
    }

    internal void ReleaseStorageCommandsUsingHandle(AbstractRenderAPIObject resource, int handle)
    {
        foreach (WebGpuMeshDraw draw in _indirectDraws.Values)
            draw.ReleaseCommandsUsingHandle(resource, handle);
        foreach (WebGpuMeshDraw draw in _draws.Values)
            draw.ReleaseCommandsUsingHandle(resource, handle);
    }

    internal void ReleaseCommandUsing(WebGpuRenderProgram program, WebGpuBindingSet bindings)
    {
        foreach (WebGpuMeshDraw draw in _indirectDraws.Values)
            draw.ReleaseCommandUsing(program, bindings);
        foreach (WebGpuMeshDraw draw in _draws.Values)
            draw.ReleaseCommandUsing(program, bindings);
    }

    public override void Destroy() => DestroyDraws();

    private static NotSupportedException Unsupported(string reason)
        => new($"WebGPU.Mesh.FeatureUnsupported: {reason}.");
}
