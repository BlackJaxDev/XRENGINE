using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Components;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Compute;

public sealed partial class GPUPhysicsChainDispatcher
{
    private const int MaximumGlobalDebugChains = 256;
    private const int MaximumGlobalDebugParticles = 16_384;

    private readonly Dictionary<IRuntimeWorldContext, PhysicsChainGpuDebugBatch> _gpuDebugBatches =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly HashSet<IRuntimeWorldContext> _gpuDebugWorldsPendingRetirement =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly List<PhysicsChainGpuDebugBatch> _gpuDebugBatchesReadyToRetire = [];
    private int _selectedGpuDebugChainCount;
    private int _gpuDebugSelectionRevision;
    private PhysicsChainGpuDebugDiagnostics _gpuDebugDiagnostics;

    /// <summary>True when at least one registered chain requests GPU debug drawing.</summary>
    internal bool HasSelectedGpuDebugChains
        => Volatile.Read(ref _selectedGpuDebugChainCount) != 0;

    private sealed class PhysicsChainGpuDebugBatch : RenderResourceLeaseOwner
    {
        internal readonly List<PhysicsChainGpuDebugItem> Items = [];
        internal XRDataBuffer<PhysicsChainGpuDebugItem>? ItemBuffer;
        internal XRDataBuffer? PointsBuffer;
        internal XRDataBuffer? LinesBuffer;
        internal XRDataBuffer? IndirectArguments;
        // ShaderHelper owns this shared cached shader. The batch only borrows it.
        internal XRShader? Shader;
        internal XRRenderProgram? Program;
        internal XRMeshRenderer? PointsRenderer;
        internal XRMeshRenderer? LinesRenderer;
        internal ulong GeneratedFrame = ulong.MaxValue;
        internal int GeneratedSelectionRevision = -1;
        internal AbstractRenderer? GeneratedRenderer;
        internal uint GeneratedBackendGeneration;
        internal int SelectedChainCount;

        internal void RequestRetirement() => RetireAuthoringResources();

        protected override void DisposeRetainedResources()
        {
            ItemBuffer?.Dispose();
            PointsBuffer?.Dispose();
            LinesBuffer?.Dispose();
            IndirectArguments?.Dispose();
            PointsRenderer?.Destroy();
            LinesRenderer?.Destroy();
            Program?.Destroy();
            Items.Clear();
        }
    }

    /// <summary>Updates the count when an explicit chain debug choice changes.</summary>
    internal void SetDebugDrawChainsSelection(IPhysicsChainComputeSource component, bool selected)
    {
        lock (_registeredComponentsSync)
        {
            if (!_registeredComponents.TryGetValue(component, out GPUPhysicsChainRequest? request) ||
                request.DebugSelected == selected)
                return;

            request.DebugSelected = selected;
            _selectedGpuDebugChainCount += selected ? 1 : -1;
            ++_gpuDebugSelectionRevision;
            if (selected)
            {
                request.DebugSelectedWorld = component.World;
                if (request.DebugSelectedWorld is { } selectedWorld)
                    _gpuDebugWorldsPendingRetirement.Remove(selectedWorld);
            }
            else
            {
                QueueGpuDebugWorldRetirementIfUnused(request.DebugSelectedWorld);
                request.DebugSelectedWorld = null;
            }
            if (_selectedGpuDebugChainCount == 0)
                _gpuDebugDiagnostics = default;
        }
    }

    /// <summary>Tracks a selected chain that moved to another runtime world.</summary>
    internal void RefreshDebugDrawChainsWorld(IPhysicsChainComputeSource component)
    {
        lock (_registeredComponentsSync)
        {
            if (!_registeredComponents.TryGetValue(component, out GPUPhysicsChainRequest? request) ||
                !request.DebugSelected ||
                ReferenceEquals(request.DebugSelectedWorld, component.World))
                return;

            IRuntimeWorldContext? oldWorld = request.DebugSelectedWorld;
            request.DebugSelectedWorld = component.World;
            if (request.DebugSelectedWorld is { } selectedWorld)
                _gpuDebugWorldsPendingRetirement.Remove(selectedWorld);
            QueueGpuDebugWorldRetirementIfUnused(oldWorld);
            ++_gpuDebugSelectionRevision;
        }
    }

    /// <summary>Queues a world only after its final selected source leaves.</summary>
    private void QueueGpuDebugWorldRetirementIfUnused(IRuntimeWorldContext? world)
    {
        if (world is null)
            return;
        for (int index = 0; index < _registeredComponentSnapshot.Count; ++index)
        {
            GPUPhysicsChainRequest request = _registeredComponentSnapshot[index];
            if (request.DebugSelected && ReferenceEquals(request.DebugSelectedWorld, world))
                return;
        }
        _gpuDebugWorldsPendingRetirement.Add(world);
    }

    /// <summary>Releases batches after all deferred authoring users retire.</summary>
    private void ProcessGpuDebugRetirements()
    {
        lock (_registeredComponentsSync)
        {
            foreach (IRuntimeWorldContext world in _gpuDebugWorldsPendingRetirement)
            {
                if (_gpuDebugBatches.Remove(world, out PhysicsChainGpuDebugBatch? batch))
                    _gpuDebugBatchesReadyToRetire.Add(batch);
            }
            _gpuDebugWorldsPendingRetirement.Clear();
        }

        for (int index = _gpuDebugBatchesReadyToRetire.Count - 1; index >= 0; --index)
        {
            PhysicsChainGpuDebugBatch batch = _gpuDebugBatchesReadyToRetire[index];
            batch.RequestRetirement();
            _gpuDebugBatchesReadyToRetire.RemoveAt(index);
        }
    }

    public PhysicsChainGpuDebugDiagnostics GetGpuDebugDiagnosticsSnapshot()
        => _gpuDebugDiagnostics;

    /// <summary>
    /// Generates one debug batch per world and frame, then draws it for each view.
    /// </summary>
    internal void RenderSelectedGpuDebug(IRuntimeWorldContext? world = null)
    {
        if (world is null || Volatile.Read(ref _selectedGpuDebugChainCount) == 0 ||
            !RuntimeEngine.Rendering.State.DebugInstanceRenderingAvailable)
            return;

        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (renderer is null || !_backendEvaluated ||
            !ReferenceEquals(renderer, _evaluatedRenderer) ||
            _computeBackend is null)
        {
            Debug.PhysicsWarningEvery(
                "PhysicsChain.Debug.RendererOwnerMismatch", TimeSpan.FromSeconds(2),
                "GPU chain debug draw skipped because this renderer does not own the current simulation output.");
            return;
        }

        IPhysicsChainComputeBackend backend = _computeBackend;
        if (_particlesBuffer is null
            || _particleStaticBuffer is null)
        {
            _gpuDebugDiagnostics = default;
            return;
        }

        if (!_gpuDebugBatches.TryGetValue(world, out PhysicsChainGpuDebugBatch? batch))
        {
            batch = new PhysicsChainGpuDebugBatch
            {
                GeneratedRenderer = renderer,
                GeneratedBackendGeneration = _readbackBackendGeneration,
            };
            _gpuDebugBatches.Add(world, batch);
        }
        if (!batch.TryAcquireActiveUse())
            return;
        if (batch.GeneratedRenderer is not null &&
            (!ReferenceEquals(batch.GeneratedRenderer, renderer) ||
             batch.GeneratedBackendGeneration != _readbackBackendGeneration))
        {
            PhysicsChainGpuDebugBatch previousBatch = batch;
            try
            {
                batch = new PhysicsChainGpuDebugBatch
                {
                    GeneratedRenderer = renderer,
                    GeneratedBackendGeneration = _readbackBackendGeneration,
                };
                _gpuDebugBatchesReadyToRetire.Add(previousBatch);
                _gpuDebugBatches[world] = batch;
            }
            finally
            {
                previousBatch.ReleaseAuthoringUse();
            }
            if (!batch.TryAcquireActiveUse())
                return;
        }

        try
        {
            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            // A successful batch stays fixed until the next frame. Earlier view
            // requests can still be waiting for deferred native materialization.
            if (batch.GeneratedFrame != frameId ||
                !ReferenceEquals(batch.GeneratedRenderer, renderer) ||
                batch.GeneratedBackendGeneration != _readbackBackendGeneration)
            {
                if (!CanRegenerateGpuDebugBatch(batch, renderer, out string reuseFailure))
                {
                    Debug.PhysicsWarningEvery(
                        "PhysicsChain.Debug.AuthoringBusy", TimeSpan.FromSeconds(2),
                        "GPU chain debug draw skipped: {0}.", reuseFailure);
                    return;
                }
                if (!GenerateGpuDebugBatch(backend, world, batch, frameId))
                    return;
            }

            int particleCount = batch.Items.Count;
            if (particleCount == 0)
                return;

            batch.PointsRenderer?.Material?.SetInt(1, particleCount);
            batch.LinesRenderer?.Material?.SetInt(1, particleCount);
            if (batch.IndirectArguments is not { } arguments ||
                batch.PointsRenderer is not { } pointsRenderer ||
                batch.LinesRenderer is not { } linesRenderer)
                return;
            bool pointsSubmitted = pointsRenderer.RenderIndexedIndirect(
                arguments, 0, EPrimitiveType.Points, out string pointFailure,
                authoringLease: batch);
            bool linesSubmitted = linesRenderer.RenderIndexedIndirect(
                arguments, 0, EPrimitiveType.Points, out string lineFailure,
                authoringLease: batch);
            if (!pointsSubmitted || !linesSubmitted)
            {
                Debug.PhysicsWarningEvery(
                    "PhysicsChain.Debug.IndirectDraw", TimeSpan.FromSeconds(2),
                    "GPU chain debug indirect draw was not submitted: points={0}; lines={1}.",
                    pointFailure, lineFailure);
                return;
            }
            _gpuDebugDiagnostics = new PhysicsChainGpuDebugDiagnostics(
                batch.SelectedChainCount,
                particleCount,
                batch.SelectedChainCount >= MaximumGlobalDebugChains || particleCount >= MaximumGlobalDebugParticles,
                ComputeDispatchCount: 1,
                DrawSubmissionCount: 2,
                UsesCpuReadback: false);
        }
        finally
        {
            batch.ReleaseAuthoringUse();
        }
    }

    private bool GenerateGpuDebugBatch(IPhysicsChainComputeBackend backend,
        IRuntimeWorldContext world, PhysicsChainGpuDebugBatch batch, ulong frameId)
    {
        if (_particlesBuffer is not { } particlesBuffer ||
            _particleStaticBuffer is not { } particleStaticBuffer)
            return false;
        int selectedChainCount = BuildGlobalDebugItems(world, batch.Items,
            out int selectionRevision);
        int particleCount = batch.Items.Count;
        if (particleCount == 0)
        {
            batch.SelectedChainCount = 0;
            batch.GeneratedFrame = frameId;
            batch.GeneratedSelectionRevision = selectionRevision;
            batch.GeneratedRenderer = _evaluatedRenderer;
            batch.GeneratedBackendGeneration = _readbackBackendGeneration;
            _gpuDebugDiagnostics = default;
            return true;
        }

        EnsureGlobalDebugProgram(batch);
        EnsureGlobalDebugResources(batch, checked((uint)particleCount));
        if (batch.Program is null
            || batch.ItemBuffer is null
            || batch.PointsBuffer is null
            || batch.LinesBuffer is null
            || batch.IndirectArguments is null)
            return false;

        uint itemBytes = batch.ItemBuffer.WriteDataRaw(CollectionsMarshal.AsSpan(batch.Items));
        PushBufferUpdate(batch.ItemBuffer, fullPush: false, itemBytes);
        RecordCpuUploadBytes(itemBytes, isBatched: true);

        batch.Program.Uniform("ParticleCount", particleCount);
        batch.Program.Uniform("PointColor", new Vector4(1.0f, 1.0f, 0.0f, 1.0f));
        batch.Program.Uniform("LineColor", Vector4.One);
        batch.Program.BindBuffer(particlesBuffer, 0);
        batch.Program.BindBuffer(particleStaticBuffer, 1);
        batch.Program.BindBuffer(batch.PointsBuffer, 2);
        batch.Program.BindBuffer(batch.LinesBuffer, 3);
        batch.Program.BindBuffer(batch.ItemBuffer, 4);
        batch.Program.BindBuffer(batch.IndirectArguments, 5);

        uint groupCount = (checked((uint)particleCount) + 127u) / 128u;
        if (!TryDispatchDirect(
                backend,
                batch.Program,
                Math.Max(groupCount, 1u),
                1u,
                1u,
                PhysicsChainComputePassKind.DebugVisualization,
                batch))
            return false;
        if (!TryCompletePass(
                backend,
                new PhysicsChainComputePass(
                    PhysicsChainComputePassKind.DebugVisualization,
                    EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command)))
            return false;

        batch.SelectedChainCount = selectedChainCount;
        batch.GeneratedFrame = frameId;
        batch.GeneratedSelectionRevision = selectionRevision;
        batch.GeneratedRenderer = _evaluatedRenderer;
        batch.GeneratedBackendGeneration = _readbackBackendGeneration;
        return true;
    }

    private static bool CanRegenerateGpuDebugBatch(
        PhysicsChainGpuDebugBatch batch, AbstractRenderer renderer, out string failure)
    {
        if (batch.AuthoringUseCount != 1)
        {
            failure = "the prior batch still has deferred render work";
            return false;
        }
        if (renderer is not IRuntimeRendererHost host || host.BackendId != RendererBackendId.Vulkan)
        {
            failure = string.Empty;
            return true;
        }
        if (host.IsDeviceLost || !renderer.AcceptsBackendWork)
        {
            failure = "the native renderer is unavailable";
            return false;
        }
        if (!host.TryGetBackendCapability<IGpuBufferContentReuseCapability>(out var reuse) || reuse is null)
        {
            failure = "the native buffer reuse capability is unavailable";
            return false;
        }
        if (!PhysicsChainBufferReuse.IsReady(reuse, batch.ItemBuffer) ||
            !PhysicsChainBufferReuse.IsReady(reuse, batch.PointsBuffer) ||
            !PhysicsChainBufferReuse.IsReady(reuse, batch.LinesBuffer) ||
            !PhysicsChainBufferReuse.IsReady(reuse, batch.IndirectArguments))
        {
            failure = "native debug buffers are still in use";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    private int BuildGlobalDebugItems(IRuntimeWorldContext world,
        List<PhysicsChainGpuDebugItem> items, out int selectionRevision)
    {
        items.Clear();
        int selectedChainCount = 0;
        lock (_registeredComponentsSync)
        {
            selectionRevision = _gpuDebugSelectionRevision;
            for (int requestIndex = 0;
                 requestIndex < _registeredComponentSnapshot.Count
                    && selectedChainCount < MaximumGlobalDebugChains
                    && items.Count < MaximumGlobalDebugParticles;
                 ++requestIndex)
            {
                GPUPhysicsChainRequest request = _registeredComponentSnapshot[requestIndex];
                if (!request.DebugSelected || !ReferenceEquals(request.DebugSelectedWorld, world) ||
                    request.ParticleOffset < 0 || request.Particles.Count == 0)
                    continue;

                ++selectedChainCount;
                float interpolationAlpha = request.Component.GetGpuDebugInterpolationAlpha();
                uint interpolationMode = checked((uint)request.Component.GpuDebugInterpolationMode);
                int remaining = MaximumGlobalDebugParticles - items.Count;
                int count = Math.Min(request.Particles.Count, remaining);
                for (int particleIndex = 0; particleIndex < count; ++particleIndex)
                {
                    items.Add(new PhysicsChainGpuDebugItem(
                        checked((uint)(request.ParticleOffset + particleIndex)),
                        interpolationAlpha,
                        interpolationMode,
                        0u));
                }
            }
        }

        return selectedChainCount;
    }

    private static void EnsureGlobalDebugProgram(PhysicsChainGpuDebugBatch batch)
    {
        if (batch.Program is not null)
            return;

        batch.Shader = ShaderHelper.LoadEngineShader(
            "Compute/PhysicsChain/PhysicsChainDebugDraw.comp",
            EShaderType.Compute);
        batch.Program = new XRRenderProgram(true, false, batch.Shader);
    }

    private void EnsureGlobalDebugResources(PhysicsChainGpuDebugBatch batch, uint particleCount)
    {
        EnsureBufferCapacity(ref batch.ItemBuffer, "PhysicsChainGlobalDebugItems", particleCount);
        EnsureRawDebugBuffer(ref batch.PointsBuffer, "PhysicsChainGlobalDebugPoints", particleCount, 8u);
        EnsureRawDebugBuffer(ref batch.LinesBuffer, "PhysicsChainGlobalDebugLines", particleCount, 12u);
        if (batch.IndirectArguments is null)
        {
            XRDataBuffer arguments = new(
                "PhysicsChainGlobalDebugIndirect", EBufferTarget.DrawIndirectBuffer,
                1u, EComponentType.UInt, 5u, false, false, true);
            try
            {
                arguments.Usage = EBufferUsage.StreamDraw;
                arguments.DisposeOnPush = false;
                arguments.SetDataRaw(new uint[5]);
                arguments.PushData();
            }
            catch
            {
                arguments.Dispose();
                throw;
            }
            batch.IndirectArguments = arguments;
        }

        batch.PointsRenderer ??= new XRMeshRenderer(
            new XRMesh([new Vertex(Vector3.Zero)]),
            CreateGpuDebugPointMaterial());
        batch.LinesRenderer ??= new XRMeshRenderer(
            new XRMesh([new Vertex(Vector3.Zero)]),
            CreateGpuDebugLineMaterial());

        ReplaceDebugRendererBuffer(batch.PointsRenderer, batch.PointsBuffer);
        ReplaceDebugRendererBuffer(batch.LinesRenderer, batch.LinesBuffer);
    }

    /// <summary>
    /// Publishes a resized debug buffer even though its shader attribute name is
    /// stable. Merely checking the key would leave the renderer referencing the
    /// disposed, undersized allocation after the first capacity growth.
    /// </summary>
    private static void ReplaceDebugRendererBuffer(XRMeshRenderer renderer, XRDataBuffer? buffer)
    {
        if (buffer is null)
            return;

        string name = buffer.AttributeName;
        if (renderer.Buffers.TryGetValue(name, out XRDataBuffer? current)
            && ReferenceEquals(current, buffer))
            return;

        renderer.Buffers[name] = buffer;
    }

    private static void EnsureRawDebugBuffer(
        ref XRDataBuffer? buffer,
        string name,
        uint elementCount,
        uint componentCount)
    {
        if (buffer is not null && buffer.ElementCount >= elementCount)
            return;

        XRDataBuffer replacement = new(
            name,
            EBufferTarget.ShaderStorageBuffer,
            Math.Max(elementCount, 1u),
            EComponentType.Float,
            componentCount,
            false,
            false,
            true);
        try
        {
            replacement.BindingIndexOverride = 0;
            replacement.Usage = EBufferUsage.StreamDraw;
            replacement.DisposeOnPush = false;
            replacement.SetDataRaw(new float[Math.Max(elementCount, 1u) * componentCount]);
            replacement.PushData();
        }
        catch
        {
            replacement.Dispose();
            throw;
        }
        XRDataBuffer? previous = buffer;
        buffer = replacement;
        previous?.Dispose();
    }

    private void DisposeGlobalDebugResources()
    {
        foreach (PhysicsChainGpuDebugBatch batch in _gpuDebugBatches.Values)
            batch.RequestRetirement();
        _gpuDebugBatches.Clear();
        for (int index = 0; index < _gpuDebugBatchesReadyToRetire.Count; ++index)
            _gpuDebugBatchesReadyToRetire[index].RequestRetirement();
        _gpuDebugBatchesReadyToRetire.Clear();
        _gpuDebugWorldsPendingRetirement.Clear();
        _gpuDebugDiagnostics = default;
    }

    private static XRMaterial CreateGpuDebugPointMaterial()
    {
        XRShader vertexShader = ShaderHelper.LoadEngineShader(
            Path.Combine("Common", "Debug", "vs", "InstancedDebugPrimitive.vs"),
            EShaderType.Vertex);
        XRShader geometryShader = ShaderHelper.LoadEngineShader(
            Path.Combine("Common", "Debug", "gs", "PointInstance.gs"),
            EShaderType.Geometry);
        XRShader fragmentShader = ShaderHelper.LoadEngineShader(
            Path.Combine("Common", "Debug", "fs", "InstancedDebugPrimitivePoint.fs"),
            EShaderType.Fragment);
        ShaderVar[] variables =
        [
            new ShaderFloat(0.005f, "PointSize"),
            new ShaderInt(0, "TotalPoints"),
        ];
        var material = new XRMaterial(variables, vertexShader, geometryShader, fragmentShader);
        material.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
        material.RenderOptions.CullMode = ECullMode.None;
        material.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
        material.EnableTransparency((int)EDefaultRenderPass.OnTopForward);
        XRMaterial.ConfigureGizmoMaterial(material);
        material.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Enabled;
        material.RenderOptions.DepthTest.UpdateDepth = true;
        material.RenderOptions.DepthTest.Function = EComparison.Lequal;
        return material;
    }

    private static XRMaterial CreateGpuDebugLineMaterial()
    {
        XRShader vertexShader = ShaderHelper.LoadEngineShader(
            Path.Combine("Common", "Debug", "vs", "InstancedDebugPrimitive.vs"),
            EShaderType.Vertex);
        XRShader geometryShader = ShaderHelper.LoadEngineShader(
            Path.Combine("Common", "Debug", "gs", "LineInstance.gs"),
            EShaderType.Geometry);
        XRShader fragmentShader = ShaderHelper.LoadEngineShader(
            Path.Combine("Common", "Debug", "fs", "InstancedDebugPrimitiveLine.fs"),
            EShaderType.Fragment);
        ShaderVar[] variables =
        [
            new ShaderFloat(0.001f, "LineWidth"),
            new ShaderInt(0, "TotalLines"),
        ];
        var material = new XRMaterial(variables, vertexShader, geometryShader, fragmentShader);
        material.RenderOptions.RequiredEngineUniforms =
            EUniformRequirements.Camera | EUniformRequirements.ViewportDimensions;
        material.RenderOptions.CullMode = ECullMode.None;
        material.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
        material.EnableTransparency((int)EDefaultRenderPass.OnTopForward);
        XRMaterial.ConfigureGizmoMaterial(material);
        material.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Enabled;
        material.RenderOptions.DepthTest.UpdateDepth = true;
        material.RenderOptions.DepthTest.Function = EComparison.Lequal;
        return material;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct PhysicsChainGpuDebugItem(
        uint ParticleIndex,
        float InterpolationAlpha,
        uint InterpolationMode,
        uint Padding);
}

public partial class GPUPhysicsChainRequest
{
    internal bool DebugSelected;
    internal IRuntimeWorldContext? DebugSelectedWorld;
}

public readonly record struct PhysicsChainGpuDebugDiagnostics(
    int SelectedChainCount,
    int GeneratedParticleCount,
    bool WasTruncated,
    int ComputeDispatchCount,
    int DrawSubmissionCount,
    bool UsesCpuReadback);
