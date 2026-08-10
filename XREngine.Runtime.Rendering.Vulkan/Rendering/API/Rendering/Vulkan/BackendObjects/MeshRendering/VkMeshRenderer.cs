using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using XREngine;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer(
    VulkanBackendObjectContext backendContext,
    XRMeshRenderer.BaseVersion data) : VkObject<XRMeshRenderer.BaseVersion>(backendContext, data), IRenderPreparationState
{
    private VulkanProgramCommandOperations? _commandOperations;
    private VulkanProgramPlannerPort? _programPlanner;
    private VulkanMeshOperationRequestQueue? _meshRequests;
    private VulkanFinalPresentationDescriptorPort? _finalPresentationDescriptors;
    private VulkanMeshMaterializationSnapshot _materializationSnapshot;

    private ref readonly VulkanMeshMaterializationSnapshot MaterializationSnapshot
        => ref _materializationSnapshot;

    private VulkanProgramCommandOperations CommandOperations => _commandOperations ?? throw new InvalidOperationException("Mesh command operations have not been bound.");

    protected override void BindOperationPorts(VulkanWrapperPortBinding binding)
    {
        _commandOperations = binding.TryGetProgramCommandOperations();
        _programPlanner = binding.TryGetProgramPlanner();
        _meshRequests = binding.TryGetMeshRequests();
        _finalPresentationDescriptors = binding.TryGetFinalPresentationDescriptors();
    }

    private static int s_screenSpaceUiDrawDiagCount;

    private readonly object _bufferStateSync = new();
    private readonly Dictionary<string, VkDataBuffer> _bufferCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BufferStructuralIdentity> _bufferStructuralIdentities = new(StringComparer.Ordinal);
    private ulong _cachedBufferResourceFingerprint;
    private BufferReadinessSnapshot _bufferReadinessSnapshot = BufferReadinessSnapshot.Empty;
    private XRMesh.BufferCollection? _subscribedRendererBuffers;
    private XRMesh.BufferCollection? _subscribedMeshBuffers;
    private bool _cachedHasValidPrecombinedBlendshapeDeltas;
    private BufferStructuralIdentity _cachedSkinnedPositionsIdentity;
    private BufferStructuralIdentity _cachedSkinnedNormalsIdentity;
    private BufferStructuralIdentity _cachedSkinnedTangentsIdentity;
    private BufferStructuralIdentity _cachedSkinnedInterleavedIdentity;
    private BufferStructuralIdentity _cachedPrecombinedBlendshapePositionsIdentity;
    private BufferStructuralIdentity _cachedPrecombinedBlendshapeNormalsIdentity;
    private BufferStructuralIdentity _cachedPrecombinedBlendshapeTangentsIdentity;
    private VkDataBuffer? _triangleIndexBuffer;
    private VkDataBuffer? _lineIndexBuffer;
    private VkDataBuffer? _pointIndexBuffer;
    private IndexSize _triangleIndexSize;
    private IndexSize _lineIndexSize;
    private IndexSize _pointIndexSize;
    private bool _triangleIndexBufferExternallyProvided;
    private bool _indexBuffersSkippedForShaderGeneratedVertices;

    private readonly Dictionary<VulkanGraphicsPipelineKey, Pipeline> _pipelines = new();
    private readonly VulkanPersistentArrayPool<uint> _preparedUIntArrays = new();
    private readonly VulkanPersistentArrayPool<Silk.NET.Vulkan.Buffer> _preparedVertexBufferArrays = new();
    private readonly VulkanPersistentArrayPool<VulkanPreparedDescriptorSetBinding> _preparedDescriptorBindingArrays = new();
    private readonly VulkanPersistentArrayPool<VulkanPreparedFrameDataPayloadHandle> _preparedFrameDataPayloadArrays = new();
    private readonly VulkanPersistentArrayPool<Viewport> _preparedViewportArrays = new();
    private readonly VulkanPersistentArrayPool<Rect2D> _preparedScissorArrays = new();
    private readonly VulkanPersistentArrayPool<byte> _preparedByteArrays = new();

    internal Viewport[] RentPreparedViewportArray(int count)
        => _preparedViewportArrays.Rent(count);

    internal Rect2D[] RentPreparedScissorArray(int count)
        => _preparedScissorArrays.Rent(count);

    internal void ReturnPreparedViewportArray(Viewport[] buffer)
        => _preparedViewportArrays.Return(buffer);

    internal void ReturnPreparedScissorArray(Rect2D[] buffer)
        => _preparedScissorArrays.Return(buffer);

    internal VulkanFrameDrawStats EstimateFrameDrawStats(in PendingMeshDraw draw)
    {
        BufferReadinessSnapshot snapshot = System.Threading.Volatile.Read(ref _bufferReadinessSnapshot);
        bool skipLinePointDraws = MeshRenderMaterialResolver.RequiresTriangleOnlyDrawsForCurrentPass();
        uint instances = draw.Instances;
        int drawCalls = 0;
        int trianglesRendered = 0;

        uint triangleIndexCount = snapshot.TriangleIndexCount;
        if (triangleIndexCount > 0u)
        {
            drawCalls++;
            trianglesRendered = AddSaturated(
                trianglesRendered,
                EstimateTriangleCount(triangleIndexCount, instances));
        }

        if (!skipLinePointDraws)
        {
            if (snapshot.LineIndexCount > 0u)
                drawCalls++;
            if (snapshot.PointIndexCount > 0u)
                drawCalls++;
        }

        if (drawCalls == 0 &&
            snapshot.FallbackVertexCount > 0u &&
            (!skipLinePointDraws || snapshot.FallbackIsTriangleClass))
        {
            drawCalls = 1;
            if (snapshot.FallbackIsTriangleClass)
                trianglesRendered = EstimateTriangleCount(snapshot.FallbackVertexCount, instances);
        }

        return new VulkanFrameDrawStats(drawCalls, MultiDrawCalls: 0, trianglesRendered);
    }

    private static int EstimateTriangleCount(uint vertexOrIndexCount, uint instances)
        => VulkanMeshRenderingConventions.SaturateToInt((ulong)(vertexOrIndexCount / 3u) * instances);

    private static int AddSaturated(int current, int value)
    {
        long total = (long)current + value;
        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    /// <summary>
    /// Resolves a generation through the wrapper's device-lifetime context rather
    /// than the renderer facade. Descriptor and buffer fingerprints must be tied
    /// to this wrapper generation only.
    /// </summary>
    private ulong GetResourceGeneration(ObjectType type, ulong handle)
        => BackendContext.Resources.Lifetime.Tracker.GetPublishedGeneration(
            new VulkanResourceLifetimeKey(type, handle));

    private bool IsLiveDescriptorImageView(ImageView imageView)
        => BackendContext.Resources.Images.IsLiveBackedByLiveImage(imageView);

    private bool TryGetDescriptorImageBacking(ImageView imageView, out Image image)
        => BackendContext.Resources.Images.TryGetBackingImage(imageView, out image);

    private VkRenderProgram? _program;
    private XRRenderProgram? _generatedProgram;
    private string? _activeProgramIdentity;
    private ulong _activeProgramLinkGeneration;
    private readonly Dictionary<VkRenderProgram, ulong> _observedProgramLinkGenerations =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, GeneratedProgramCacheEntry> _programCache = new(4, StringComparer.Ordinal);
    private readonly Dictionary<GeneratedProgramState, GeneratedProgramCacheEntry> _programStateCache = new(4);
    private VertexInputBindingDescription[] _vertexBindings = [];
    private VertexInputAttributeDescription[] _vertexAttributes = [];
    private bool _vertexInputStateDirty = true;
    private MeshGeometryLayoutSignature _geometryLayoutSignature = MeshGeometryLayoutSignature.Empty;
    private readonly Dictionary<uint, VkDataBuffer> _vertexBuffersByBinding = new();
    private readonly Silk.NET.Vulkan.Buffer[] _singleVertexBindingBuffer = new Silk.NET.Vulkan.Buffer[1];
    private readonly ulong[] _singleVertexBindingOffset = [0UL];
    private bool _buffersDirty = true;
    private bool _pipelineDirty = true;
    private XRMaterial? _lastPreparedMaterial;
    private string _lastPrepareResult = "NeverCalled";
    private string _lastPrepareDetail = string.Empty;
    private int _pipelineShaderConfigVersion = -1;
    private bool _pipelineUsesShaderClipDepthRemap;
    private bool _pipelineUsesNativeDepthClipControl;
    private DescriptorPool _descriptorPool;
    private DescriptorSet[][]? _descriptorSets;
    private DescriptorAllocation? _activeDescriptorAllocation;
    private readonly Dictionary<DescriptorAllocationKey, DescriptorAllocation> _descriptorAllocations = new();
    private readonly Dictionary<int, DescriptorAllocation> _descriptorAllocationsByDrawSlot = new();
    private readonly Dictionary<DescriptorOwnerLookupKey, DescriptorAllocation> _descriptorAllocationsByOwner = new();
    private bool _descriptorDirty = true;
    private ulong _descriptorSchemaFingerprint;
    private ulong _descriptorResourceFingerprint;
    private string _descriptorResourceFingerprintDetails = string.Empty;
    private int _uniformDrawSlotCapacity = 1;
    private readonly HashSet<string> _descriptorWarnings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EngineUniformBuffer[]> _engineUniformBuffers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _engineUniformWarnings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AutoUniformBuffer[]> _autoUniformBuffers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VulkanAutoUniformOwnerSlotTable> _autoUniformOwnerSlotTables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VulkanAutoUniformPublicationState[]> _publishedAutoUniformMaterialWritePlans = new(StringComparer.Ordinal);
    private readonly HashSet<(
        string Block,
        EVulkanAutoUniformFallbackReason Reason,
        string? Detail)> _autoUniformWarnings = [];
    private const string VertexUniformSuffix = "_VTX";
    private const string TransformIdUniformName = "TransformId";
    private const string SkinPaletteBaseUniformName = "skinPaletteBase";
    private const string SkinPaletteCountUniformName = "skinPaletteCount";
    private const string SkinningInfluenceCapUniformName = "skinningInfluenceCap";
    private const string BlendshapeActiveCountUniformName = "blendshapeActiveCount";
    private const string BlendshapeWeightThresholdUniformName = "blendshapeWeightThreshold";
    private const string UsePrecombinedBlendshapeDeltasUniformName = "usePrecombinedBlendshapeDeltas";
    private const string FallbackDescriptorUniformName = "__FallbackDescriptorBuffer";
    private const uint FallbackDescriptorUniformSize = 1024u;
    private const uint ComputeInterleavedBinding = 9u;
    private const uint ComputePositionBinding = 11u;
    private const uint ComputeNormalBinding = 12u;
    private const uint PrecombinedBlendshapePositionBinding = 13u;
    private const uint PrecombinedBlendshapeNormalBinding = 14u;
    private const uint ComputeTangentBinding = 15u;
    private const uint PrecombinedBlendshapeTangentBinding = 15u;
    private const string ComputeInterleavedBufferName = "SkinnedInterleaved";
    private const string ComputePositionBufferName = "SkinnedPositions";
    private const string ComputeNormalBufferName = "SkinnedNormals";
    private const string ComputeTangentBufferName = "SkinnedTangents";
    private const string PrecombinedBlendshapePositionBufferName = "PrecombinedBlendshapePositionDeltas";
    private const string PrecombinedBlendshapeNormalBufferName = "PrecombinedBlendshapeNormalDeltas";
    private const string PrecombinedBlendshapeTangentBufferName = "PrecombinedBlendshapeTangentDeltas";

    private static bool IsStencilCapableFormat(Format format)
        => format is Format.D16UnormS8Uint or Format.D24UnormS8Uint or Format.D32SfloatS8Uint;

    public XRMeshRenderer MeshRenderer => Data.Parent;
    public XRMesh? Mesh => MeshRenderer.Mesh;
    public override VkObjectType Type => VkObjectType.MeshRenderer;
    public override bool IsGenerated => IsActive;

    protected override uint CreateObjectInternal() => CacheObject(this);

    protected override void DeleteObjectInternal()
    {
        BackendContext.Resources.PipelineManager.DrainPipelineCompileJobsForOwner(
            PipelineCompileOwnerId,
            GetDescribingName());
        DestroyPipelines();
        DestroyGeneratedPrograms();
        BackendContext.Resources.MappedFrameArena?.ReleaseReservations(this);
        CommandOperations.RemoveMeshFrameDataManifestRenderer(this);
        RemoveCachedObject(BindingId);
    }

    protected override void LinkData()
    {
        Data.RenderRequested += OnRenderRequested;
        MeshRenderer.PropertyChanged += OnMeshRendererPropertyChanged;
        MeshRenderer.PropertyChanging += OnMeshRendererPropertyChanging;
        SubscribeRendererBuffers(MeshRenderer.Buffers);

        Mesh?.DataChanged += OnMeshChanged;
        SubscribeMeshBufferCollection(Mesh?.Buffers);

        CollectBuffers();
    }

    protected override void UnlinkData()
    {
        Data.RenderRequested -= OnRenderRequested;
        MeshRenderer.PropertyChanged -= OnMeshRendererPropertyChanged;
        MeshRenderer.PropertyChanging -= OnMeshRendererPropertyChanging;
        SubscribeRendererBuffers(null);

        Mesh?.DataChanged -= OnMeshChanged;
        SubscribeMeshBufferCollection(null);

        BackendContext.Resources.PipelineManager.DrainPipelineCompileJobsForOwner(
            PipelineCompileOwnerId,
            GetDescribingName());
        DestroyPipelines();
        DestroyGeneratedPrograms();
        BackendContext.Resources.MappedFrameArena?.ReleaseReservations(this);
        CommandOperations.RemoveMeshFrameDataManifestRenderer(this);
        lock (_bufferStateSync)
        {
            _bufferCache.Clear();
            System.Threading.Volatile.Write(ref _cachedBufferResourceFingerprint, 0UL);
            _vertexBuffersByBinding.Clear();
            _triangleIndexBuffer = null;
            _lineIndexBuffer = null;
            _pointIndexBuffer = null;
            _indexBuffersSkippedForShaderGeneratedVertices = false;
            PublishBufferReadinessSnapshot();
        }
    }

    private void OnBuffersChanged() => InvalidateGeometryLayout("RendererBuffersChanged", collectBuffers: true);
    private void OnMeshBuffersChanged() => InvalidateGeometryLayout("MeshBuffersChanged", collectBuffers: true);

    private void OnMeshRendererPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(XRMeshRenderer.Mesh):
                MeshRenderer.Mesh?.DataChanged += OnMeshChanged;
                SubscribeMeshBufferCollection(MeshRenderer.Mesh?.Buffers);
                InvalidateGeometryLayout("MeshChanged", collectBuffers: true);
                break;
            case nameof(XRMeshRenderer.Material):
                _pipelineDirty = true;
                _descriptorDirty = true;
                _lastPreparedMaterial = null;
                break;
        }
    }

    private void OnMeshRendererPropertyChanging(object? sender, IXRPropertyChangingEventArgs e)
    {
        if (e.PropertyName == nameof(XRMeshRenderer.Mesh) && e.CurrentValue is XRMesh currentMesh)
        {
            currentMesh.DataChanged -= OnMeshChanged;
            if (ReferenceEquals(_subscribedMeshBuffers, currentMesh.Buffers))
                SubscribeMeshBufferCollection(null);
        }
    }

    private void OnMeshChanged(XRMesh? mesh)
        => InvalidateGeometryLayout("MeshDataChanged", collectBuffers: true);

    private void SubscribeRendererBuffers(XRMesh.BufferCollection? buffers)
    {
        if (ReferenceEquals(_subscribedRendererBuffers, buffers))
            return;

        _subscribedRendererBuffers?.Changed -= OnBuffersChanged;
        _subscribedRendererBuffers = buffers;
        _subscribedRendererBuffers?.Changed += OnBuffersChanged;
    }

    private void SubscribeMeshBufferCollection(XRMesh.BufferCollection? buffers)
    {
        if (ReferenceEquals(_subscribedMeshBuffers, buffers))
            return;

        _subscribedMeshBuffers?.Changed -= OnMeshBuffersChanged;
        _subscribedMeshBuffers = buffers;
        _subscribedMeshBuffers?.Changed += OnMeshBuffersChanged;
    }

    private void InvalidateGeometryLayout(string reason, bool collectBuffers)
    {
        lock (_bufferStateSync)
        {
            _pipelineDirty = true;
            _buffersDirty = true;
            _descriptorDirty = true;
            _vertexInputStateDirty = true;
            _lastPreparedMaterial = null;
            _triangleIndexBuffer = null;
            _lineIndexBuffer = null;
            _pointIndexBuffer = null;
            _indexBuffersSkippedForShaderGeneratedVertices = false;
            _geometryLayoutSignature = MeshGeometryLayoutSignature.Empty;
            _lastPrepareResult = reason;
            _lastPrepareDetail = "Geometry layout changed.";

            if (collectBuffers)
            {
                CollectBuffers();
            }
            else
            {
                PublishBufferReadinessSnapshot();
                CommandOperations.MarkCommandBuffersDirtyForLegacyMeshState();
            }
        }
    }

    private void OnRenderRequested(Matrix4x4 modelMatrix, Matrix4x4 prevModelMatrix, XRMaterial? materialOverride, RenderingParameters? renderOptionsOverride, uint instances, EMeshBillboardMode billboardMode, bool forceNoStereo)
    {
        FrameOpContext context = _programPlanner?.CaptureFrameOpContext() ?? default;
        VulkanMeshRenderRequest request = new(
            this,
            RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex,
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline,
            context,
            CommandOperations.CaptureMeshProducerSnapshot(in context),
            MeshRenderer.BindingPublishers.CaptureDeferredPublication(),
            modelMatrix,
            prevModelMatrix,
            materialOverride,
            renderOptionsOverride,
            instances,
            billboardMode,
            forceNoStereo);
        if (_meshRequests?.TryEnqueue(in request) == true)
            return;

        CommandOperations.MarkCommandBuffersDirtyForLegacyMeshState();
        Debug.VulkanWarningEvery(
            $"Vulkan.MeshRenderer.RequestQueueFull.{MeshRenderer.Name ?? "UnnamedRenderer"}",
            TimeSpan.FromSeconds(2),
            "[Vulkan] Dropping mesh render request for renderer='{0}' because the bounded frame-loop request queue is unavailable or full.",
            MeshRenderer.Name ?? "<unnamed renderer>");
    }

    /// <summary>
    /// Materializes immutable draw facts after the frame-loop authority has captured
    /// its current output, planner, and command state. Mesh wrappers only enqueue raw
    /// render events; they never retain or consult those authorities directly.
    /// </summary>
    internal bool TryMaterializeQueuedRenderRequest(
        in VulkanMeshRenderRequest request,
        in VulkanMeshProducerSnapshot producer,
        in VulkanMeshMaterializationSnapshot materializationSnapshot,
        out VulkanMeshOperationRequest operationRequest)
    {
        _materializationSnapshot = materializationSnapshot;
        Matrix4x4 modelMatrix = request.ModelMatrix;
        Matrix4x4 prevModelMatrix = request.PreviousModelMatrix;
        XRMaterial? materialOverride = request.MaterialOverride;
        RenderingParameters? renderOptionsOverride = request.RenderOptionsOverride;
        uint instances = request.Instances;
        EMeshBillboardMode billboardMode = request.BillboardMode;
        bool forceNoStereo = request.ForceNoStereo;
        operationRequest = default;
        using VulkanCpuStageScope preparationStage =
            default;

        if (!IsActive)
            Generate();

        // Don't enqueue mesh draw ops when there's no active rendering pipeline;
        // they would be emitted with an invalid pass index and dropped at recording time.
        if (producer.Context.PipelineInstance is null)
            return false;

        int passIndex = request.PassIndex;
        XRFrameBuffer? target = producer.Target;
        VulkanFixedFunctionStateSnapshot producerState = producer.FixedFunctionState;

        // Resolve the effective material and its render options so the
        // pipeline key captures per-material state (CullMode, DepthTest, etc.)
        // instead of inheriting stale values from the global state tracker.
        XRMaterial effectiveMaterial = ResolveMaterial(materialOverride, instances);
        uint drawInstances = MeshRenderMaterialResolver.ResolveLayeredShadowInstanceCount(effectiveMaterial, instances);

        RenderingParameters? matOpts = renderOptionsOverride ?? effectiveMaterial.RenderOptions;

        // Ã¢â€â‚¬Ã¢â€â‚¬ CullMode Ã¢â€â‚¬Ã¢â€â‚¬
        CullModeFlags cullMode;
        if (matOpts is not null)
            cullMode = VulkanMeshRenderingConventions.ToVulkanCullMode(VulkanMeshRenderingConventions.ResolveCullMode(matOpts.CullMode));
        else
            cullMode = producerState.CullMode;

        // Ã¢â€â‚¬Ã¢â€â‚¬ FrontFace Ã¢â€â‚¬Ã¢â€â‚¬
        FrontFace frontFace;
        if (matOpts is not null)
            frontFace = VulkanMeshRenderingConventions.ToVulkanFrontFace(VulkanMeshRenderingConventions.ResolveWinding(matOpts.Winding));
        else
            frontFace = producerState.FrontFace;

        // Ã¢â€â‚¬Ã¢â€â‚¬ DepthTest Ã¢â€â‚¬Ã¢â€â‚¬
        bool depthTestEnabled;
        bool depthWriteEnabled;
        CompareOp depthCompareOp;
        SampleCountFlags rasterizationSamples = ResolveRasterizationSamples(target);
        if (matOpts?.DepthTest is { } dt && dt.Enabled != ERenderParamUsage.Unchanged)
        {
            depthTestEnabled = dt.Enabled == ERenderParamUsage.Enabled;
            depthWriteEnabled = depthTestEnabled && dt.UpdateDepth;
            depthCompareOp = depthTestEnabled
                ? VulkanMeshRenderingConventions.ToVulkanCompareOp(RuntimeEngine.Rendering.State.MapDepthComparison(dt.Function))
                : CompareOp.Always;
        }
        else
        {
            depthTestEnabled = producerState.DepthTestEnabled;
            depthWriteEnabled = producerState.DepthWriteEnabled;
            depthCompareOp = producerState.DepthCompareOp;
        }

        // Ã¢â€â‚¬Ã¢â€â‚¬ Blend Ã¢â€â‚¬Ã¢â€â‚¬
        bool blendEnabled;
        bool alphaToCoverageEnabled;
        BlendOp colorBlendOp, alphaBlendOp;
        BlendFactor srcColor, dstColor, srcAlpha, dstAlpha;
        BlendMode? matBlend = matOpts is not null ? VulkanMeshRenderingConventions.ResolveBlendMode(matOpts) : null;
        bool requestedAlphaToCoverage = matOpts?.AlphaToCoverage == ERenderParamUsage.Enabled;
        if (matBlend is not null && matBlend.Enabled == ERenderParamUsage.Enabled)
        {
            blendEnabled = true;
            alphaToCoverageEnabled = requestedAlphaToCoverage && rasterizationSamples != SampleCountFlags.Count1Bit;
            colorBlendOp = VulkanMeshRenderingConventions.ToVulkanBlendOp(matBlend.RgbEquation);
            alphaBlendOp = VulkanMeshRenderingConventions.ToVulkanBlendOp(matBlend.AlphaEquation);
            srcColor = VulkanMeshRenderingConventions.ToVulkanBlendFactor(matBlend.RgbSrcFactor);
            dstColor = VulkanMeshRenderingConventions.ToVulkanBlendFactor(matBlend.RgbDstFactor);
            srcAlpha = VulkanMeshRenderingConventions.ToVulkanBlendFactor(matBlend.AlphaSrcFactor);
            dstAlpha = VulkanMeshRenderingConventions.ToVulkanBlendFactor(matBlend.AlphaDstFactor);
        }
        else if (matBlend is not null && matBlend.Enabled == ERenderParamUsage.Disabled)
        {
            blendEnabled = false;
            alphaToCoverageEnabled = requestedAlphaToCoverage && rasterizationSamples != SampleCountFlags.Count1Bit;
            colorBlendOp = BlendOp.Add;
            alphaBlendOp = BlendOp.Add;
            srcColor = BlendFactor.One;
            dstColor = BlendFactor.Zero;
            srcAlpha = BlendFactor.One;
            dstAlpha = BlendFactor.Zero;
        }
        else if (matBlend is null && matOpts is not null)
        {
            blendEnabled = false;
            alphaToCoverageEnabled = requestedAlphaToCoverage && rasterizationSamples != SampleCountFlags.Count1Bit;
            colorBlendOp = BlendOp.Add;
            alphaBlendOp = BlendOp.Add;
            srcColor = BlendFactor.One;
            dstColor = BlendFactor.Zero;
            srcAlpha = BlendFactor.One;
            dstAlpha = BlendFactor.Zero;
        }
        else
        {
            blendEnabled = producerState.BlendEnabled;
            alphaToCoverageEnabled = producerState.AlphaToCoverageEnabled && rasterizationSamples != SampleCountFlags.Count1Bit;
            colorBlendOp = producerState.ColorBlendOp;
            alphaBlendOp = producerState.AlphaBlendOp;
            srcColor = producerState.SrcColorBlendFactor;
            dstColor = producerState.DstColorBlendFactor;
            srcAlpha = producerState.SrcAlphaBlendFactor;
            dstAlpha = producerState.DstAlphaBlendFactor;
        }

        bool stencilTestEnabled;
        StencilOpState frontStencilState;
        StencilOpState backStencilState;
        uint stencilWriteMask;
        if (matOpts?.StencilTest is { } stencilTest && stencilTest.Enabled != ERenderParamUsage.Unchanged)
        {
            if (stencilTest.Enabled == ERenderParamUsage.Enabled)
            {
                stencilTestEnabled = true;
                frontStencilState = VulkanMeshRenderingConventions.ToVulkanStencilState(stencilTest.FrontFace);
                backStencilState = VulkanMeshRenderingConventions.ToVulkanStencilState(stencilTest.BackFace);
                stencilWriteMask = stencilTest.FrontFace.WriteMask;
            }
            else
            {
                stencilTestEnabled = false;
                frontStencilState = default;
                backStencilState = default;
                stencilWriteMask = 0u;
            }
        }
        else
        {
            stencilTestEnabled = producerState.StencilTestEnabled;
            frontStencilState = producerState.FrontStencilState;
            backStencilState = producerState.BackStencilState;
            stencilWriteMask = producerState.StencilWriteMask;
        }

        ColorComponentFlags colorWriteMask = matOpts is not null
            ? VulkanMeshRenderingConventions.ToVulkanColorWriteMask(matOpts)
            : producerState.ColorWriteMask;

        // Snapshot camera matrices/vectors now. A pushed null camera is intentional
        // for fullscreen quads; do not fall back to the scene camera in that scope.
        XRRenderPipelineInstance? currentPipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        bool explicitCameraScope = RuntimeEngine.Rendering.State.RenderingPipelineState?.HasRenderingCameraScope == true;
        XRCamera? snapshotCamera = explicitCameraScope
            ? RuntimeEngine.Rendering.State.RenderingCamera
            : RuntimeEngine.Rendering.State.RenderingCamera
                ?? currentPipeline?.RenderState.RenderingCamera
                ?? currentPipeline?.RenderState.SceneCamera
                ?? currentPipeline?.LastRenderingCamera
                ?? currentPipeline?.LastSceneCamera;
        XRCamera? snapshotRightEyeCamera = snapshotCamera is null
            ? null
            : RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera
                ?? currentPipeline?.RenderState.StereoRightEyeCamera;
        bool useUnjitteredProjectionSnapshot = RuntimeEngine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection ?? false;
        Matrix4x4 viewMatrixSnapshot = snapshotCamera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity;
        Matrix4x4 inverseViewMatrixSnapshot = snapshotCamera?.Transform.RenderMatrix ?? Matrix4x4.Identity;
        Matrix4x4 projectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotCamera is not null
            ? snapshotCamera.ProjectionMatrixUnjittered
            : snapshotCamera?.ProjectionMatrix ?? Matrix4x4.Identity;
        Matrix4x4 inverseProjectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotCamera is not null
            ? snapshotCamera.InverseProjectionMatrixUnjittered
            : snapshotCamera?.InverseProjectionMatrix ?? Matrix4x4.Identity;
        Matrix4x4 viewProjectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotCamera is not null
            ? snapshotCamera.ViewProjectionMatrixUnjittered
            : snapshotCamera?.ViewProjectionMatrix ?? Matrix4x4.Identity;
        Matrix4x4 viewProjectionMatrixUnjitteredSnapshot =
            snapshotCamera?.ViewProjectionMatrixUnjittered ?? viewProjectionMatrixSnapshot;
        Matrix4x4 rightEyeViewMatrixSnapshot = snapshotRightEyeCamera?.Transform.InverseRenderMatrix ?? viewMatrixSnapshot;
        Matrix4x4 rightEyeInverseViewMatrixSnapshot = snapshotRightEyeCamera?.Transform.RenderMatrix ?? inverseViewMatrixSnapshot;
        Matrix4x4 rightEyeProjectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotRightEyeCamera is not null
            ? snapshotRightEyeCamera.ProjectionMatrixUnjittered
            : snapshotRightEyeCamera?.ProjectionMatrix ?? projectionMatrixSnapshot;
        Matrix4x4 rightEyeInverseProjectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotRightEyeCamera is not null
            ? snapshotRightEyeCamera.InverseProjectionMatrixUnjittered
            : snapshotRightEyeCamera?.InverseProjectionMatrix ?? inverseProjectionMatrixSnapshot;
        Matrix4x4 rightEyeViewProjectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotRightEyeCamera is not null
            ? snapshotRightEyeCamera.ViewProjectionMatrixUnjittered
            : snapshotRightEyeCamera?.ViewProjectionMatrix ?? viewProjectionMatrixSnapshot;
        Matrix4x4 rightEyeViewProjectionMatrixUnjitteredSnapshot =
            snapshotRightEyeCamera?.ViewProjectionMatrixUnjittered ?? viewProjectionMatrixUnjitteredSnapshot;
        Matrix4x4 previousViewMatrixSnapshot = viewMatrixSnapshot;
        Matrix4x4 previousProjectionMatrixSnapshot = projectionMatrixSnapshot;
        Matrix4x4 previousViewProjectionMatrixSnapshot = viewProjectionMatrixSnapshot;
        Matrix4x4 previousViewProjectionMatrixUnjitteredSnapshot = snapshotCamera?.ViewProjectionMatrixUnjittered ?? viewProjectionMatrixSnapshot;
        Matrix4x4 previousRightEyeViewMatrixSnapshot = rightEyeViewMatrixSnapshot;
        Matrix4x4 previousRightEyeProjectionMatrixSnapshot = rightEyeProjectionMatrixSnapshot;
        Matrix4x4 previousRightEyeViewProjectionMatrixSnapshot = rightEyeViewProjectionMatrixSnapshot;
        Matrix4x4 previousRightEyeViewProjectionMatrixUnjitteredSnapshot =
            snapshotRightEyeCamera?.ViewProjectionMatrixUnjittered ?? rightEyeViewProjectionMatrixSnapshot;
        if (currentPipeline is not null &&
            VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(currentPipeline, out var temporalData))
        {
            viewProjectionMatrixUnjitteredSnapshot = temporalData.CurrViewProjectionUnjittered;
            rightEyeViewProjectionMatrixUnjitteredSnapshot = temporalData.RightEyeCurrViewProjectionUnjittered;
            if (temporalData.HistoryReady)
            {
                previousViewMatrixSnapshot = temporalData.PrevViewMatrix;
                previousProjectionMatrixSnapshot = temporalData.PrevProjection;
                previousViewProjectionMatrixSnapshot = temporalData.PrevViewProjection;
                previousViewProjectionMatrixUnjitteredSnapshot = temporalData.PrevViewProjectionUnjittered;
                previousRightEyeViewMatrixSnapshot = temporalData.RightEyePrevViewMatrix;
                previousRightEyeProjectionMatrixSnapshot = temporalData.RightEyePrevProjection;
                previousRightEyeViewProjectionMatrixSnapshot = temporalData.RightEyePrevViewProjection;
                previousRightEyeViewProjectionMatrixUnjitteredSnapshot = temporalData.RightEyePrevViewProjectionUnjittered;
            }
        }
        Vector3 cameraPositionSnapshot = snapshotCamera?.Transform.RenderTranslation ?? Vector3.Zero;
        Vector3 cameraForwardSnapshot = snapshotCamera?.Transform.RenderForward ?? Vector3.UnitZ;
        Vector3 cameraUpSnapshot = snapshotCamera?.Transform.RenderUp ?? Vector3.UnitY;
        Vector3 cameraRightSnapshot = snapshotCamera?.Transform.RenderRight ?? Vector3.UnitX;
        uint transformIdSnapshot = RuntimeEngine.Rendering.State.CurrentTransformId;
        bool stereoPassSnapshot = !forceNoStereo && RuntimeEngine.Rendering.State.IsStereoPass;
        // Snapshot the render-area dimensions now (the live RenderArea is reset to Empty by
        // deferred record time). For debug-primitive draws the pipeline render-region can
        // already be Empty even at enqueue time, so fall back to the bound draw framebuffer's
        // dimensions, which reflect the actual target the geometry shaders rasterize into.
        var renderAreaSnapshot = RuntimeEngine.Rendering.State.RenderArea;
        int renderAreaWidthSnapshot = renderAreaSnapshot.Width;
        int renderAreaHeightSnapshot = renderAreaSnapshot.Height;
        if (renderAreaWidthSnapshot <= 0 || renderAreaHeightSnapshot <= 0)
        {
            if (target is not null)
            {
                renderAreaWidthSnapshot = (int)target.Width;
                renderAreaHeightSnapshot = (int)target.Height;
            }
            else
            {
                Extent2D targetExtent = producer.TargetExtent;
                renderAreaWidthSnapshot = (int)targetExtent.Width;
                renderAreaHeightSnapshot = (int)targetExtent.Height;
            }
        }

        LayeredShadowUniformState shadowUniformState = LayeredShadowUniformState.CaptureFromCurrentRenderingState();
        // The pipeline frame-resource scope already captured and installed the immutable
        // context that owns this command list. Recomputing it for every visible mesh repeats
        // registry/pass hashing and allocates a new diagnostic context id per draw, putting
        // workstream-04 package consumption back on the render-thread critical path.
        ComputeDispatchSnapshot? programBindingSnapshot;
        VkRenderProgram? preparedProgramSnapshot;
        string? preparedProgramIdentitySnapshot;
        ulong preparedProgramLinkGenerationSnapshot;
        bool deferredBindingsActivated = request.DeferredBindings.TryActivate();
        if (!deferredBindingsActivated)
        {
            CommandOperations.MarkCommandBuffersDirtyForLegacyMeshState();
            return false;
        }

        try
        {
            lock (_recordDrawSync)
            {
                bool prepared;
                string prepareReason;
                using (VulkanCpuStageScope resourcePreparationStage =
                       default)
                {
                    prepared = TryPrepareForDrawEnqueue(
                        effectiveMaterial,
                        out prepareReason);
                }
                if (!prepared)
                {
                    // A skipped draw means the recorded frame is incomplete. Keep the
                    // command buffers invalid until the pending program/buffers/descriptors
                    // are ready on the legacy primary path. Command-chain primaries are
                    // invalidated by the frame-op signature when the draw becomes available.
                    CommandOperations.MarkCommandBuffersDirtyForLegacyMeshState();
                    Debug.VulkanWarningEvery(
                        $"Vulkan.MeshRenderer.PrepareSkip.{MeshRenderer.Name ?? "UnnamedRenderer"}.{prepareReason}",
                        TimeSpan.FromSeconds(2),
                        "[Vulkan] Skipping mesh draw enqueue for renderer='{0}' mesh='{1}' material='{2}' because render preparation is not ready: {3}. {4}",
                        MeshRenderer.Name ?? "<unnamed renderer>",
                        Mesh?.Name ?? "<unnamed mesh>",
                        effectiveMaterial.Name ?? "<unnamed material>",
                        prepareReason,
                    LastPrepareDetail);
                    return false;
                }

                using (VulkanCpuStageScope bindingSnapshotStage =
                       default)
                {
                    programBindingSnapshot =
                        CaptureProgramBindingSnapshot(
                            effectiveMaterial,
                            shadowUniformState);
                }

                // Resource preparation, program selection, and binding capture are
                // one publication transaction. RecordDraw uses the same lock, so a
                // shader reload cannot retire the selected program interface between
                // capture and publication or mix a new program with an old snapshot.
                preparedProgramSnapshot = _program;
                preparedProgramIdentitySnapshot = _activeProgramIdentity;
                preparedProgramLinkGenerationSnapshot = _program?.LinkGeneration ?? 0UL;
            }
        }
        finally
        {
            request.DeferredBindings.Deactivate();
        }
        IndexedViewportScissorSnapshot indexedViewportScissors = producer.IndexedViewportScissors;
        uint viewportScissorCount = indexedViewportScissors.Count > 1 ? indexedViewportScissors.Count : 1u;
        Viewport viewportSnapshot = producer.Viewport;
        Rect2D scissorSnapshot = producer.Scissor;
        var draw = new PendingMeshDraw(
            this,
            viewportSnapshot,
            scissorSnapshot,
            viewportScissorCount > 1 ? indexedViewportScissors.Viewports : null,
            viewportScissorCount > 1 ? indexedViewportScissors.Scissors : null,
            viewportScissorCount,
            rasterizationSamples,
            depthTestEnabled,
            depthWriteEnabled,
            depthCompareOp,
            stencilTestEnabled,
            frontStencilState,
            backStencilState,
            stencilWriteMask,
            colorWriteMask,
            cullMode,
            frontFace,
            blendEnabled,
            alphaToCoverageEnabled,
            colorBlendOp,
            alphaBlendOp,
            srcColor,
            dstColor,
            srcAlpha,
            dstAlpha,
            modelMatrix,
            prevModelMatrix,
            effectiveMaterial,
            drawInstances,
            billboardMode,
            snapshotCamera,
            snapshotRightEyeCamera,
            stereoPassSnapshot,
            useUnjitteredProjectionSnapshot,
            transformIdSnapshot,
            viewMatrixSnapshot,
            inverseViewMatrixSnapshot,
            projectionMatrixSnapshot,
            inverseProjectionMatrixSnapshot,
            viewProjectionMatrixSnapshot,
            viewProjectionMatrixUnjitteredSnapshot,
            previousViewMatrixSnapshot,
            previousProjectionMatrixSnapshot,
            previousViewProjectionMatrixSnapshot,
            previousViewProjectionMatrixUnjitteredSnapshot,
            rightEyeViewMatrixSnapshot,
            rightEyeInverseViewMatrixSnapshot,
            rightEyeProjectionMatrixSnapshot,
            rightEyeInverseProjectionMatrixSnapshot,
            rightEyeViewProjectionMatrixSnapshot,
            rightEyeViewProjectionMatrixUnjitteredSnapshot,
            previousRightEyeViewMatrixSnapshot,
            previousRightEyeProjectionMatrixSnapshot,
            previousRightEyeViewProjectionMatrixSnapshot,
            previousRightEyeViewProjectionMatrixUnjitteredSnapshot,
            cameraPositionSnapshot,
            cameraForwardSnapshot,
            cameraUpSnapshot,
            cameraRightSnapshot,
            renderAreaWidthSnapshot,
            renderAreaHeightSnapshot,
            shadowUniformState,
            preparedProgramSnapshot,
            preparedProgramIdentitySnapshot,
            preparedProgramLinkGenerationSnapshot,
            programBindingSnapshot);
        draw = draw with
        {
            AutoUniformPublication =
                VulkanAutoUniformPublicationSnapshot.Capture(draw),
        };

        if (s_screenSpaceUiDrawDiagCount < 32 &&
            passIndex == (int)EDefaultRenderPass.OnTopForward &&
            MathF.Abs(modelMatrix.M41) > 10.0f &&
            MathF.Abs(modelMatrix.M42) > 10.0f)
        {
            s_screenSpaceUiDrawDiagCount++;
            Matrix4x4 worldViewProjection = modelMatrix * viewProjectionMatrixSnapshot;
            Vector4 p0 = ProjectUiDiagCorner(0.0f, 0.0f, in worldViewProjection);
            Vector4 p1 = ProjectUiDiagCorner(1.0f, 0.0f, in worldViewProjection);
            Vector4 p2 = ProjectUiDiagCorner(0.0f, 1.0f, in worldViewProjection);
            Vector4 p3 = ProjectUiDiagCorner(1.0f, 1.0f, in worldViewProjection);
            Debug.Vulkan(
                "[Vulkan][ScreenUIDraw] #{0} mesh='{1}' material='{2}' forceNoStereo={3} globalStereo={4} drawStereo={5} pass={6} target='{7}' camera='{8}' modelT=({9:F1},{10:F1},{11:F1}) modelScale=({12:F1},{13:F1},{14:F1}) vp=({15:F1},{16:F1},{17:F1},{18:F1}) scissor=({19},{20},{21},{22}) ndc=({23:F3},{24:F3})-({25:F3},{26:F3}) w=({27:F3},{28:F3},{29:F3},{30:F3})",
                s_screenSpaceUiDrawDiagCount,
                Mesh?.Name ?? MeshRenderer.Name ?? "<unnamed mesh>",
                effectiveMaterial.Name ?? "<unnamed material>",
                forceNoStereo,
                RuntimeEngine.Rendering.State.IsStereoPass,
                stereoPassSnapshot,
                passIndex,
                target?.Name ?? "<swapchain>",
                snapshotCamera?.Transform.SceneNode?.Name ?? snapshotCamera?.GetType().Name ?? "<null>",
                modelMatrix.M41,
                modelMatrix.M42,
                modelMatrix.M43,
                modelMatrix.M11,
                modelMatrix.M22,
                modelMatrix.M33,
                viewportSnapshot.X,
                viewportSnapshot.Y,
                viewportSnapshot.Width,
                viewportSnapshot.Height,
                scissorSnapshot.Offset.X,
                scissorSnapshot.Offset.Y,
                scissorSnapshot.Extent.Width,
                scissorSnapshot.Extent.Height,
                MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X)),
                MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y)),
                MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X)),
                MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y)),
                p0.W,
                p1.W,
                p2.W,
                p3.W);
        }

        using (VulkanCpuStageScope enqueueStage =
               default)
        {
            operationRequest = new VulkanMeshOperationRequest(
                this,
                passIndex,
                draw,
                producer,
                ExplicitTarget: null,
                RequiresExternalUploadBlock: producer.IsExternalSwapchainTarget &&
                    !producer.IsPrewarmingExternalSwapchainTarget);
        }
        return true;
    }

    private static Vector4 ProjectUiDiagCorner(float x, float y, in Matrix4x4 worldViewProjection)
    {
        Vector4 clip = Vector4.Transform(new Vector4(x, y, 0.0f, 1.0f), worldViewProjection);
        if (MathF.Abs(clip.W) <= 1e-6f)
            return clip;

        float invW = 1.0f / clip.W;
        return new Vector4(clip.X * invW, clip.Y * invW, clip.Z * invW, clip.W);
    }

    internal bool TryCreatePreparedIndirectDrawSnapshot(
        XRMaterial effectiveMaterial,
        VkRenderProgram preparedProgram,
        string? preparedProgramIdentity,
        ulong preparedProgramLinkGeneration,
        ComputeDispatchSnapshot? programBindingSnapshot,
        Matrix4x4 modelMatrix,
        XRFrameBuffer? target,
        out PendingMeshDraw draw,
        out string reason)
    {
        draw = default;
        reason = "Ready";

        if (RuntimeEngine.Rendering.State.CurrentRenderingPipeline is null)
            return SetPrepareResult(false, "PipelineMissing", "No active rendering pipeline is available for indirect draw capture.", out reason);

        VulkanMeshProducerSnapshot producer = CommandOperations.CaptureIndirectProducerSnapshot(target);
        bool preparedForIndirect;
        if (producer.IsPrewarmingExternalSwapchainTarget)
        {
            preparedForIndirect = TryPrepareCapturedProgramForRecording(effectiveMaterial, preparedProgram, preparedProgramIdentity, preparedProgramLinkGeneration, programBindingSnapshot, 0, out reason);
        }
        else if (producer.IsExternalSwapchainTarget)
        {
            preparedForIndirect = TryReuseCapturedProgramForIndirectDrawSnapshot(effectiveMaterial, preparedProgram, preparedProgramIdentity, preparedProgramLinkGeneration, programBindingSnapshot, 0, out reason);
            if (!preparedForIndirect)
                preparedForIndirect = TryPrepareCapturedProgramForRecording(effectiveMaterial, preparedProgram, preparedProgramIdentity, preparedProgramLinkGeneration, programBindingSnapshot, 0, out reason);
        }
        else
        {
            preparedForIndirect = TryReuseCapturedProgramForIndirectDrawSnapshot(effectiveMaterial, preparedProgram, preparedProgramIdentity, preparedProgramLinkGeneration, programBindingSnapshot, 0, out reason);

            if (!preparedForIndirect)
                preparedForIndirect = TryPrepareCapturedProgramForRecording(effectiveMaterial, preparedProgram, preparedProgramIdentity, preparedProgramLinkGeneration, programBindingSnapshot, 0, out reason);
        }

        if (!preparedForIndirect)
            return false;

        XRFrameBuffer? effectiveTarget = producer.Target;
        SampleCountFlags rasterizationSamples = ResolveRasterizationSamples(effectiveTarget);
        VulkanFixedFunctionStateSnapshot producerState = producer.FixedFunctionState;
        bool alphaToCoverageEnabled = producerState.AlphaToCoverageEnabled && rasterizationSamples != SampleCountFlags.Count1Bit;

        XRRenderPipelineInstance? currentPipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        bool explicitCameraScope = RuntimeEngine.Rendering.State.RenderingPipelineState?.HasRenderingCameraScope == true;
        XRCamera? snapshotCamera = explicitCameraScope
            ? RuntimeEngine.Rendering.State.RenderingCamera
            : RuntimeEngine.Rendering.State.RenderingCamera
                ?? currentPipeline?.RenderState.RenderingCamera
                ?? currentPipeline?.RenderState.SceneCamera
                ?? currentPipeline?.LastRenderingCamera
                ?? currentPipeline?.LastSceneCamera;
        XRCamera? snapshotRightEyeCamera = snapshotCamera is null
            ? null
            : RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera
                ?? currentPipeline?.RenderState.StereoRightEyeCamera;
        bool useUnjitteredProjectionSnapshot = RuntimeEngine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection ?? false;
        Matrix4x4 viewMatrixSnapshot = snapshotCamera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity;
        Matrix4x4 inverseViewMatrixSnapshot = snapshotCamera?.Transform.RenderMatrix ?? Matrix4x4.Identity;
        Matrix4x4 projectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotCamera is not null
            ? snapshotCamera.ProjectionMatrixUnjittered
            : snapshotCamera?.ProjectionMatrix ?? Matrix4x4.Identity;
        Matrix4x4 inverseProjectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotCamera is not null
            ? snapshotCamera.InverseProjectionMatrixUnjittered
            : snapshotCamera?.InverseProjectionMatrix ?? Matrix4x4.Identity;
        Matrix4x4 viewProjectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotCamera is not null
            ? snapshotCamera.ViewProjectionMatrixUnjittered
            : snapshotCamera?.ViewProjectionMatrix ?? Matrix4x4.Identity;
        Matrix4x4 viewProjectionMatrixUnjitteredSnapshot =
            snapshotCamera?.ViewProjectionMatrixUnjittered ?? viewProjectionMatrixSnapshot;
        Matrix4x4 rightEyeViewMatrixSnapshot = snapshotRightEyeCamera?.Transform.InverseRenderMatrix ?? viewMatrixSnapshot;
        Matrix4x4 rightEyeInverseViewMatrixSnapshot = snapshotRightEyeCamera?.Transform.RenderMatrix ?? inverseViewMatrixSnapshot;
        Matrix4x4 rightEyeProjectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotRightEyeCamera is not null
            ? snapshotRightEyeCamera.ProjectionMatrixUnjittered
            : snapshotRightEyeCamera?.ProjectionMatrix ?? projectionMatrixSnapshot;
        Matrix4x4 rightEyeInverseProjectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotRightEyeCamera is not null
            ? snapshotRightEyeCamera.InverseProjectionMatrixUnjittered
            : snapshotRightEyeCamera?.InverseProjectionMatrix ?? inverseProjectionMatrixSnapshot;
        Matrix4x4 rightEyeViewProjectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotRightEyeCamera is not null
            ? snapshotRightEyeCamera.ViewProjectionMatrixUnjittered
            : snapshotRightEyeCamera?.ViewProjectionMatrix ?? viewProjectionMatrixSnapshot;
        Matrix4x4 rightEyeViewProjectionMatrixUnjitteredSnapshot =
            snapshotRightEyeCamera?.ViewProjectionMatrixUnjittered ?? viewProjectionMatrixUnjitteredSnapshot;
        Matrix4x4 previousViewMatrixSnapshot = viewMatrixSnapshot;
        Matrix4x4 previousProjectionMatrixSnapshot = projectionMatrixSnapshot;
        Matrix4x4 previousViewProjectionMatrixSnapshot = viewProjectionMatrixSnapshot;
        Matrix4x4 previousViewProjectionMatrixUnjitteredSnapshot = snapshotCamera?.ViewProjectionMatrixUnjittered ?? viewProjectionMatrixSnapshot;
        Matrix4x4 previousRightEyeViewMatrixSnapshot = rightEyeViewMatrixSnapshot;
        Matrix4x4 previousRightEyeProjectionMatrixSnapshot = rightEyeProjectionMatrixSnapshot;
        Matrix4x4 previousRightEyeViewProjectionMatrixSnapshot = rightEyeViewProjectionMatrixSnapshot;
        Matrix4x4 previousRightEyeViewProjectionMatrixUnjitteredSnapshot =
            snapshotRightEyeCamera?.ViewProjectionMatrixUnjittered ?? rightEyeViewProjectionMatrixSnapshot;
        if (currentPipeline is not null &&
            VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(currentPipeline, out var temporalData))
        {
            viewProjectionMatrixUnjitteredSnapshot = temporalData.CurrViewProjectionUnjittered;
            rightEyeViewProjectionMatrixUnjitteredSnapshot = temporalData.RightEyeCurrViewProjectionUnjittered;
            if (temporalData.HistoryReady)
            {
                previousViewMatrixSnapshot = temporalData.PrevViewMatrix;
                previousProjectionMatrixSnapshot = temporalData.PrevProjection;
                previousViewProjectionMatrixSnapshot = temporalData.PrevViewProjection;
                previousViewProjectionMatrixUnjitteredSnapshot = temporalData.PrevViewProjectionUnjittered;
                previousRightEyeViewMatrixSnapshot = temporalData.RightEyePrevViewMatrix;
                previousRightEyeProjectionMatrixSnapshot = temporalData.RightEyePrevProjection;
                previousRightEyeViewProjectionMatrixSnapshot = temporalData.RightEyePrevViewProjection;
                previousRightEyeViewProjectionMatrixUnjitteredSnapshot = temporalData.RightEyePrevViewProjectionUnjittered;
            }
        }
        Vector3 cameraPositionSnapshot = snapshotCamera?.Transform.RenderTranslation ?? Vector3.Zero;
        Vector3 cameraForwardSnapshot = snapshotCamera?.Transform.RenderForward ?? Vector3.UnitZ;
        Vector3 cameraUpSnapshot = snapshotCamera?.Transform.RenderUp ?? Vector3.UnitY;
        Vector3 cameraRightSnapshot = snapshotCamera?.Transform.RenderRight ?? Vector3.UnitX;
        uint transformIdSnapshot = RuntimeEngine.Rendering.State.CurrentTransformId;

        var renderAreaSnapshot = RuntimeEngine.Rendering.State.RenderArea;
        int renderAreaWidthSnapshot = renderAreaSnapshot.Width;
        int renderAreaHeightSnapshot = renderAreaSnapshot.Height;
        if (renderAreaWidthSnapshot <= 0 || renderAreaHeightSnapshot <= 0)
        {
            if (effectiveTarget is not null)
            {
                renderAreaWidthSnapshot = (int)effectiveTarget.Width;
                renderAreaHeightSnapshot = (int)effectiveTarget.Height;
            }
            else
            {
                Extent2D targetExtent = producer.TargetExtent;
                renderAreaWidthSnapshot = (int)targetExtent.Width;
                renderAreaHeightSnapshot = (int)targetExtent.Height;
            }
        }

        LayeredShadowUniformState shadowUniformState = LayeredShadowUniformState.CaptureFromCurrentRenderingState();
        IndexedViewportScissorSnapshot indexedViewportScissors = producer.IndexedViewportScissors;
        uint viewportScissorCount = indexedViewportScissors.Count > 1 ? indexedViewportScissors.Count : 1u;
        Viewport viewportSnapshot = producer.Viewport;
        Rect2D scissorSnapshot = producer.Scissor;
        FrontFace frontFaceSnapshot = producerState.FrontFace;

        draw = new PendingMeshDraw(
            this,
            viewportSnapshot,
            scissorSnapshot,
            viewportScissorCount > 1 ? indexedViewportScissors.Viewports : null,
            viewportScissorCount > 1 ? indexedViewportScissors.Scissors : null,
            viewportScissorCount,
            rasterizationSamples,
            producerState.DepthTestEnabled,
            producerState.DepthWriteEnabled,
            producerState.DepthCompareOp,
            producerState.StencilTestEnabled,
            producerState.FrontStencilState,
            producerState.BackStencilState,
            producerState.StencilWriteMask,
            producerState.ColorWriteMask,
            producerState.CullMode,
            frontFaceSnapshot,
            producerState.BlendEnabled,
            alphaToCoverageEnabled,
            producerState.ColorBlendOp,
            producerState.AlphaBlendOp,
            producerState.SrcColorBlendFactor,
            producerState.DstColorBlendFactor,
            producerState.SrcAlphaBlendFactor,
            producerState.DstAlphaBlendFactor,
            modelMatrix,
            modelMatrix,
            effectiveMaterial,
            1u,
            effectiveMaterial.BillboardMode,
            snapshotCamera,
            snapshotRightEyeCamera,
            RuntimeEngine.Rendering.State.IsStereoPass,
            useUnjitteredProjectionSnapshot,
            transformIdSnapshot,
            viewMatrixSnapshot,
            inverseViewMatrixSnapshot,
            projectionMatrixSnapshot,
            inverseProjectionMatrixSnapshot,
            viewProjectionMatrixSnapshot,
            viewProjectionMatrixUnjitteredSnapshot,
            previousViewMatrixSnapshot,
            previousProjectionMatrixSnapshot,
            previousViewProjectionMatrixSnapshot,
            previousViewProjectionMatrixUnjitteredSnapshot,
            rightEyeViewMatrixSnapshot,
            rightEyeInverseViewMatrixSnapshot,
            rightEyeProjectionMatrixSnapshot,
            rightEyeInverseProjectionMatrixSnapshot,
            rightEyeViewProjectionMatrixSnapshot,
            rightEyeViewProjectionMatrixUnjitteredSnapshot,
            previousRightEyeViewMatrixSnapshot,
            previousRightEyeProjectionMatrixSnapshot,
            previousRightEyeViewProjectionMatrixSnapshot,
            previousRightEyeViewProjectionMatrixUnjitteredSnapshot,
            cameraPositionSnapshot,
            cameraForwardSnapshot,
            cameraUpSnapshot,
            cameraRightSnapshot,
            renderAreaWidthSnapshot,
            renderAreaHeightSnapshot,
            shadowUniformState,
            preparedProgram,
            preparedProgramIdentity,
            preparedProgramLinkGeneration,
            programBindingSnapshot);
        draw = draw with
        {
            AutoUniformPublication =
                VulkanAutoUniformPublicationSnapshot.Capture(draw),
        };

        return true;
    }

    private ComputeDispatchSnapshot? CaptureProgramBindingSnapshot(
        XRMaterial material,
        in LayeredShadowUniformState shadowUniformState)
    {
        if (_program is not { Data: { } programData } program)
            return null;

        bool measureAllocationBreakdown =
            VulkanCpuStageScope.DetailedDiagnosticsEnabled;
        long allocationStart = measureAllocationBreakdown
            ? GC.GetAllocatedBytesForCurrentThread()
            : 0;

        // Vulkan command recording may run after collection or on a worker.
        // Mutable renderer/material callbacks therefore require the same
        // immutable enqueue-time boundary as explicit capture users. The flag
        // remains useful for scoped pipeline callbacks that are not represented
        // by either event subscription.
        bool captureUniforms =
            MeshRenderer.CaptureUniformsOnRender ||
            MeshRenderer.HasSettingUniformsHandlers ||
            material.HasSettingUniformsHandlers;
        bool hasMutableShadowUniformHandlers =
            shadowUniformState.IsShadowPass &&
            (material.HasSettingShadowUniformHandlers ||
             material.ShadowUniformSourceMaterial?.HasSettingShadowUniformHandlers == true ||
             material.ShadowBindingSourceMaterial?.HasSettingShadowUniformHandlers == true ||
             material.ShadowBindingSourceMaterial?.HasSettingUniformsHandlers == true);
        bool hasTypedBindingPublishers =
            material.BindingPublishers.Count != 0 ||
            MeshRenderer.BindingPublishers.Count != 0;
        bool mayNeedDescriptorResourceSnapshot =
            program.DescriptorBindings.Count != 0 &&
            program.DescriptorSetLayouts.Count != 0;
        if (!captureUniforms &&
            !hasTypedBindingPublishers &&
            !mayNeedDescriptorResourceSnapshot)
            return null;

        IRenderBindingPublisher[] materialBindingPublishers;
        IRenderBindingPublisher[] meshBindingPublishers;
        bool publisherStateValid;
        ulong typedBindingPublisherSignature;
        string? publisherStateFailureDetail;
        bool hasGenerationOwnedPublisherResources;
        long publisherScopeStart = measureAllocationBreakdown
            ? GC.GetAllocatedBytesForCurrentThread()
            : 0;
        using (VulkanCpuStageScope publisherStateStage =
               default)
        {
            materialBindingPublishers =
                material.BindingPublishers.CaptureSnapshot();
            meshBindingPublishers =
                MeshRenderer.BindingPublishers.CaptureSnapshot();
            publisherStateValid = TryComputeTypedBindingPublisherSignature(
                materialBindingPublishers,
                meshBindingPublishers,
                out typedBindingPublisherSignature,
                out publisherStateFailureDetail);
            hasGenerationOwnedPublisherResources =
                HasResourceBindingPublisher(materialBindingPublishers) ||
                HasResourceBindingPublisher(meshBindingPublishers);
        }
        long publisherScopeEnd = measureAllocationBreakdown
            ? GC.GetAllocatedBytesForCurrentThread()
            : 0;
        bool useMaterialPayloadFastPath =
            !shadowUniformState.IsShadowPass;
        EUniformRequirements engineRequirements = EUniformRequirements.None;
        ComputeDispatchSnapshot? engineBindingSnapshot = null;
        EVulkanProgramBindingArtifactFallbackReason artifactFallbackReason =
            EVulkanProgramBindingArtifactFallbackReason.None;
        bool usePersistentProgramBindingArtifact = false;
        long eligibilityScopeStart = measureAllocationBreakdown
            ? GC.GetAllocatedBytesForCurrentThread()
            : 0;
        using (VulkanCpuStageScope eligibilityStage =
               default)
        {
            if (!useMaterialPayloadFastPath)
            {
                artifactFallbackReason =
                    EVulkanProgramBindingArtifactFallbackReason.ShadowPass;
            }
            else if (!publisherStateValid)
            {
                artifactFallbackReason =
                    EVulkanProgramBindingArtifactFallbackReason
                        .InvalidPublisherState;
            }
            else
            {
                usePersistentProgramBindingArtifact =
                    CanUsePersistentProgramBindingArtifact(
                        material,
                        programData,
                        shadowUniformState,
                        materialBindingPublishers,
                        meshBindingPublishers,
                        out engineRequirements,
                        out engineBindingSnapshot,
                        out artifactFallbackReason);
            }
        }
        long eligibilityScopeEnd = measureAllocationBreakdown
            ? GC.GetAllocatedBytesForCurrentThread()
            : 0;
        PersistentProgramBindingArtifactSlotKey persistentArtifactSlot =
            new(material, MeshRenderer);
        PersistentProgramBindingArtifactGeneration persistentArtifactGeneration =
            default;
        ComputeDispatchSnapshot? reusedPersistentArtifact = null;
        if (usePersistentProgramBindingArtifact)
        {
            persistentArtifactGeneration =
                CreatePersistentProgramBindingArtifactGeneration(
                    material,
                    program,
                    typedBindingPublisherSignature,
                    engineRequirements,
                    engineBindingSnapshot);
            long artifactKeyAndGenerationEnd = measureAllocationBreakdown
                ? GC.GetAllocatedBytesForCurrentThread()
                : 0;
            long lookupScopeStart = artifactKeyAndGenerationEnd;
            bool artifactFound;
            using (VulkanCpuStageScope lookupStage =
                   default)
            {
                artifactFound =
                    program.TryGetPersistentProgramBindingArtifact(
                        persistentArtifactSlot,
                        persistentArtifactGeneration,
                        materialBindingPublishers,
                        meshBindingPublishers,
                        out reusedPersistentArtifact);
            }
            long lookupScopeEnd = measureAllocationBreakdown
                ? GC.GetAllocatedBytesForCurrentThread()
                : 0;
            if (artifactFound)
            {
                RuntimeEngine.Rendering.Stats.Vulkan
                    .RecordVulkanProgramBindingArtifactReuse();
                long reusePublicationEnd = measureAllocationBreakdown
                    ? GC.GetAllocatedBytesForCurrentThread()
                    : 0;
                if (measureAllocationBreakdown)
                {
                    RuntimeEngine.Rendering.Stats.Vulkan
                        .RecordVulkanProgramBindingAllocationBreakdown(
                            publisherScopeStart - allocationStart,
                            publisherScopeEnd - publisherScopeStart,
                            eligibilityScopeStart - publisherScopeEnd,
                            eligibilityScopeEnd - eligibilityScopeStart,
                            artifactKeyAndGenerationEnd -
                                eligibilityScopeEnd,
                            lookupScopeEnd - lookupScopeStart,
                            reusePublicationEnd - lookupScopeEnd);
                }
                return reusedPersistentArtifact;
            }
        }
        else
        {
            RuntimeEngine.Rendering.Stats.Vulkan
                .RecordVulkanProgramBindingArtifactFallback(
                    artifactFallbackReason,
                    MeshRenderer.Mesh?.Name,
                    material.Name,
                    programData.Name,
                    publisherStateFailureDetail);
        }

        // The frame cache key includes ScopedBindingRevision. Layered-shadow
        // push/pop now advances that revision, so every draw using the same
        // material inside one exact cascade/face scope can share the immutable
        // snapshot. Callback-owned shadow state has no generation contract and
        // remains on the conservative per-draw path.
        bool shareSnapshot =
            publisherStateValid &&
            !captureUniforms &&
            !hasMutableShadowUniformHandlers;
        MaterialBindingSnapshotCacheKey snapshotCacheKey = default;
        MaterialUniformBindingPayload? materialUniformPayload = null;
        if (shareSnapshot)
        {
            var renderingState = RuntimeEngine.Rendering.State.RenderingPipelineState;
            var renderArea = RuntimeEngine.Rendering.State.RenderArea;
            snapshotCacheKey = new MaterialBindingSnapshotCacheKey(
                material,
                RuntimeEngine.Rendering.State.CurrentRenderingPipeline,
                RuntimeEngine.Rendering.State.RenderingCamera,
                RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera,
                RuntimeEngine.Rendering.State.RenderingWorld,
                CommandOperations.ResolveCurrentDrawTarget(),
                program.LinkGeneration,
                renderingState?.ScopedBindingRevision ?? 0UL,
                typedBindingPublisherSignature,
                RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex,
                renderArea.X,
                renderArea.Y,
                renderArea.Width,
                renderArea.Height,
                RuntimeEngine.Rendering.State.IsStereoPass,
                renderingState?.UseUnjitteredProjection ?? false);
            if (program.TryGetFrameMaterialBindingSnapshot(
                    snapshotCacheKey,
                    out ComputeDispatchSnapshot? cachedSnapshot))
            {
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanFrameMaterialSnapshotCacheLookup(hit: true);
                return cachedSnapshot;
            }
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanFrameMaterialSnapshotCacheLookup(hit: false);
        }

        if (useMaterialPayloadFastPath)
        {
            MaterialUniformBindingCacheKey materialPayloadKey =
                new(material);
            VkMaterial? materialOwner =
				WrapperLookup.GetOrCreate(
                    material,
                    generateNow: true) as VkMaterial;
            bool materialPayloadCacheHit =
                materialOwner?.TryGetMaterialUniformBindingPayload(
                    materialPayloadKey,
                    out materialUniformPayload) == true;
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanMaterialPayloadCacheLookup(
                materialPayloadCacheHit);
            if (!materialPayloadCacheHit)
            {
                if (XREnvironment.IsEnabled(
                        XREngineEnvironmentVariables.VulkanFrameDataReuseDiag))
                {
                    bool hasCachedPayload = false;
                    bool cacheKeyMatches = false;
                    if (materialOwner is not null)
                    {
                        materialOwner.GetMaterialUniformBindingPayloadCacheState(
                            materialPayloadKey,
                            out hasCachedPayload,
                            out cacheKeyMatches);
                    }
                    Debug.VulkanEvery(
                        $"Vulkan.MaterialPayloadCacheMiss.{material.ID}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan.MaterialPayloadCacheMiss] material='{0}' id={1} " +
                        "ownerBinding={2} hasPayload={3} keyMatches={4} " +
                        "layout={5} value={6} shader={7} uber={8}.",
                        material.Name ?? "<unnamed>",
                        material.ID,
                        materialOwner?.BindingId ?? 0u,
                        materialOwner is not null && hasCachedPayload,
                        materialOwner is not null && cacheKeyMatches,
                        material.BindingLayoutVersion,
                        material.BindingValueVersion,
                        material.ShaderStateRevision,
                        material.UberStateRevision);
                }

                // Capture only the material-owned numeric dictionary once per
                // material revision. Resource and render-scope bindings are
                // intentionally omitted because their lifetimes differ.
                using VkRenderProgram.BindingUpdateScope materialBindingUpdate =
                    program.BeginBindingUpdate();
                program.ClearBindings();
                VulkanMeshRenderingConventions.SetMaterialStaticUniforms(material, programData);
                materialUniformPayload = program.CaptureMaterialUniformBindingPayload();
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanMaterialPayloadPacked(
                    materialUniformPayload.Uniforms.Count);
                materialOwner?.CacheMaterialUniformBindingPayload(
                    materialPayloadKey,
                    materialUniformPayload);
            }
        }

        VulkanFixedFunctionStateSnapshot stateSnapshot = CommandOperations.CaptureFixedFunctionState();
        using VkRenderProgram.BindingUpdateScope bindingUpdate = program.BeginBindingUpdate();
        try
        {
            program.ClearBindings();
            using (VulkanCpuStageScope materialBindingsStage =
                   default)
            {
                if (useMaterialPayloadFastPath)
                {
                    CommandOperations.SetMaterialRuntimeUniforms(
                        material,
                        programData,
                        program,
                        shadowUniformState);
                }
                else
                {
                    CommandOperations.SetMaterialUniforms(material, programData, program, shadowUniformState);
                }
            }
            using (VkRenderProgram.MutableLegacyBindingPublicationScope
                   legacyPublication =
                       program.BeginMutableLegacyBindingPublication())
            {
                if (MeshRenderer.HasSettingUniformsHandlers)
                    MeshRenderer.OnSettingUniforms(
                        programData,
                        programData);
                else
                {
                    XRRenderPipelineInstance.RenderingState? renderingState =
                        RuntimeEngine.Rendering.State.RenderingPipelineState;
                    XRRenderPipelineInstance? pipeline =
                        RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
                    if (renderingState?.HasActiveScopedBindings != true &&
                        pipeline?.Variables.HasUniformValues == true)
                    {
                        using VkRenderProgram.TypedBindingPublicationScope
                            pipelineVariables =
                                program.BeginTypedBindingPublication(
                                    ERenderBindingFrequency.Pass,
                                    pipeline.Variables
                                        .UniformContentGeneration);
                        pipeline.Variables.Apply(programData);
                    }
                    else
                    {
                        renderingState?.ApplyScopedProgramBindings(
                            programData);
                    }
                }
                MeshRenderMaterialResolver.ApplyShadowUniforms(
                    programData,
                    material,
                    shadowUniformState);
            }
            bool materialPublishersStable = PublishTypedBindingPublishers(
                program,
                programData,
                materialBindingPublishers);
            bool meshPublishersStable = PublishTypedBindingPublishers(
                program,
                programData,
                meshBindingPublishers);
            bool typedPublishersStable =
                materialPublishersStable && meshPublishersStable;
            if (!typedPublishersStable)
            {
                if (usePersistentProgramBindingArtifact)
                {
                    RuntimeEngine.Rendering.Stats.Vulkan
                        .RecordVulkanProgramBindingArtifactFallback(
                            EVulkanProgramBindingArtifactFallbackReason
                                .PublisherChangedDuringPublication,
                            MeshRenderer.Mesh?.Name,
                            material.Name,
                            programData.Name);
                }
                usePersistentProgramBindingArtifact = false;
                shareSnapshot = false;
            }
            if (!captureUniforms &&
                !hasTypedBindingPublishers &&
                !program.HasBoundDescriptorResources() &&
                !mayNeedDescriptorResourceSnapshot)
            {
                if (usePersistentProgramBindingArtifact)
                {
                    program.CachePersistentProgramBindingArtifact(
                        persistentArtifactSlot,
                        persistentArtifactGeneration,
                        materialBindingPublishers,
                        meshBindingPublishers,
                        artifact: null);
                    RuntimeEngine.Rendering.Stats.Vulkan
                        .RecordVulkanProgramBindingArtifactBuild();
                }
                if (shareSnapshot)
                    program.CacheFrameMaterialBindingSnapshot(snapshotCacheKey, null);
                return null;
            }

            // A descriptor-bearing program still needs a published snapshot when
            // its enqueue-time binding dictionaries are empty. Shadow-only programs
            // commonly bind only renderer-owned or mapped-arena buffers; returning
            // null here forced every caster and frame slot through the reflected
            // descriptor resolver again during command recording. An empty published
            // snapshot carries the immutable layout/resource-generation contract and
            // lets that path use the same O(1) signature as command-chain validation.
            ComputeDispatchSnapshot snapshot;
            using (VulkanCpuStageScope snapshotCopyStage =
                   default)
            {
                snapshot = program.CaptureComputeSnapshot();
            }
            snapshot.SetMaterialUniformBindings(materialUniformPayload);
            if (useMaterialPayloadFastPath)
                snapshot.EnableMaterialBindingFastPath();
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanBindingSnapshotCaptured(
                snapshot.Uniforms.Count +
                snapshot.Samplers.Count +
                snapshot.SamplerNamesByUnit.Count +
                snapshot.SamplersByName.Count +
                snapshot.Images.Count +
                snapshot.Buffers.Count,
                fastPath: useMaterialPayloadFastPath);
            if (usePersistentProgramBindingArtifact)
            {
                if (TryCreatePersistentProgramBindingArtifact(
                         material,
                         snapshot,
                         engineRequirements,
                         hasGenerationOwnedPublisherResources,
                         out ComputeDispatchSnapshot persistentArtifact,
                         out EVulkanProgramBindingArtifactFallbackReason
                             contentFallbackReason,
                         out string? contentFallbackDetail))
                {
                    program.CachePersistentProgramBindingArtifact(
                        persistentArtifactSlot,
                        persistentArtifactGeneration,
                        materialBindingPublishers,
                        meshBindingPublishers,
                        persistentArtifact);
                    RuntimeEngine.Rendering.Stats.Vulkan
                        .RecordVulkanProgramBindingArtifactBuild();
                    if (shareSnapshot)
                    {
                        program.CacheFrameMaterialBindingSnapshot(
                            snapshotCacheKey,
                            persistentArtifact);
                    }
                    LogGizmoBindingSnapshot(
                        material,
                        persistentArtifact,
                        "persistent-artifact");
                    return persistentArtifact;
                }

                RuntimeEngine.Rendering.Stats.Vulkan
                    .RecordVulkanProgramBindingArtifactFallback(
                        contentFallbackReason ==
                            EVulkanProgramBindingArtifactFallbackReason.None
                                ? EVulkanProgramBindingArtifactFallbackReason
                                    .ArtifactContentUnsupported
                                : contentFallbackReason,
                        MeshRenderer.Mesh?.Name,
                        material.Name,
                        programData.Name,
                        contentFallbackDetail);
            }
            if (shareSnapshot)
                program.CacheFrameMaterialBindingSnapshot(snapshotCacheKey, snapshot);
            LogGizmoBindingSnapshot(material, snapshot, "capture");
            return snapshot;
        }
        finally
        {
            CommandOperations.RestoreFixedFunctionState(stateSnapshot);
        }
    }

    private static bool TryComputeTypedBindingPublisherSignature(
        IRenderBindingPublisher[] materialPublishers,
        IRenderBindingPublisher[] meshPublishers,
        out ulong signature,
        out string? failureDetail)
    {
        FrameOpSignatureHasher hash = new();
        if (!TryAddPublishersToSignature(
            ref hash,
            materialPublishers,
            ownerKind: 1,
            out failureDetail) ||
            !TryAddPublishersToSignature(
            ref hash,
            meshPublishers,
            ownerKind: 2,
            out failureDetail))
        {
            signature = 0UL;
            return false;
        }

        signature = hash.ToHash();
        failureDetail = null;
        return true;
    }

    private static bool TryAddPublishersToSignature(
        ref FrameOpSignatureHasher hash,
        IRenderBindingPublisher[] publishers,
        byte ownerKind,
        out string? failureDetail)
    {
        hash.Add(ownerKind);
        hash.Add(publishers.Length);
        for (int index = 0; index < publishers.Length; index++)
        {
            IRenderBindingPublisher publisher = publishers[index];
            ERenderBindingFrequency frequency = publisher.Frequency;
            ulong generation = publisher.Generation;
            if (frequency is <= ERenderBindingFrequency.Unknown or
                >= ERenderBindingFrequency.Count)
            {
                failureDetail =
                    $"Typed binding publisher '{publisher.GetType().FullName}' " +
                    $"declared invalid frequency '{frequency}'.";
                return false;
            }
            if (generation == 0)
            {
                failureDetail =
                    $"Typed binding publisher '{publisher.GetType().FullName}' " +
                    "declared generation zero.";
                return false;
            }

            hash.Add(RuntimeHelpers.GetHashCode(publisher));
            hash.Add((byte)frequency);
            hash.Add(generation);
            if (publisher is
                IPersistentProgramBindingRequirementOwner requirementOwner)
            {
                EUniformRequirements ownedRequirement =
                    requirementOwner.OwnedPersistentArtifactRequirement;
                if (ownedRequirement == EUniformRequirements.None ||
                    !IsSingleRequirement(ownedRequirement))
                {
                    failureDetail =
                        $"Persistent binding requirement owner " +
                        $"'{publisher.GetType().FullName}' declared invalid " +
                        $"requirement '{ownedRequirement}'.";
                    return false;
                }

                hash.Add(true);
                hash.Add((int)ownedRequirement);
            }
            else
            {
                hash.Add(false);
            }
            if (publisher is IRenderResourceBindingPublisher
                resourcePublisher)
            {
                ulong resourceGeneration =
                    resourcePublisher.ResourceGeneration;
                if (resourceGeneration == 0)
                {
                    failureDetail =
                        $"Resource binding publisher " +
                        $"'{publisher.GetType().FullName}' declared " +
                        "resource generation zero.";
                    return false;
                }

                hash.Add(true);
                hash.Add(resourceGeneration);
            }
            else
            {
                hash.Add(false);
            }
        }

        failureDetail = null;
        return true;
    }

    private static bool IsSingleRequirement(EUniformRequirements requirement)
    {
        uint value = unchecked((uint)requirement);
        return (value & (value - 1U)) == 0U;
    }

    private static bool HasResourceBindingPublisher(
        IRenderBindingPublisher[] publishers)
    {
        for (int index = 0; index < publishers.Length; index++)
            if (publishers[index] is IRenderResourceBindingPublisher)
                return true;
        return false;
    }

    private static bool PublishTypedBindingPublishers(
        VkRenderProgram backendProgram,
        XRRenderProgram program,
        IRenderBindingPublisher[] publishers)
    {
        bool stable = true;
        for (int index = 0; index < publishers.Length; index++)
        {
            IRenderBindingPublisher publisher = publishers[index];
            ERenderBindingFrequency frequency = publisher.Frequency;
            ulong generation = publisher.Generation;
            IRenderResourceBindingPublisher? resourcePublisher =
                publisher as IRenderResourceBindingPublisher;
            ulong resourceGeneration =
                resourcePublisher?.ResourceGeneration ?? 0UL;
            if (frequency is <= ERenderBindingFrequency.Unknown or
                >= ERenderBindingFrequency.Count ||
                generation == 0 ||
                (resourcePublisher is not null && resourceGeneration == 0))
            {
                using VkRenderProgram.MutableLegacyBindingPublicationScope
                    legacyPublication =
                        backendProgram.BeginMutableLegacyBindingPublication();
                publisher.PublishUniforms(program, program);
                resourcePublisher?.PublishResources(program, program);
                stable = false;
                continue;
            }

            using (VkRenderProgram.TypedBindingPublicationScope publication =
                backendProgram.BeginTypedBindingPublication(
                    frequency,
                    generation))
            {
                publisher.PublishUniforms(program, program);
            }
            if (resourcePublisher is not null)
            {
                using VkRenderProgram.TypedBindingPublicationScope
                    resourcePublication =
                        backendProgram.BeginTypedResourceBindingPublication(
                            frequency,
                            resourceGeneration,
                            resourcePublisher.RequiresReadyDescriptorResources);
                resourcePublisher.PublishResources(program, program);
            }
            if (publisher.Frequency != frequency ||
                publisher.Generation != generation ||
                (resourcePublisher is not null &&
                 resourcePublisher.ResourceGeneration != resourceGeneration))
            {
                stable = false;
            }
        }

        return stable;
    }

    private void LogGizmoBindingSnapshot(XRMaterial material, ComputeDispatchSnapshot snapshot, string phase)
    {
        if (!MaterialBindingDiagnosticsEnabled || !IsGizmoDiagnosticProgram())
            return;

        Debug.MeshesWarningEvery(
            $"Vulkan.GizmoBindingSnapshot.{GetHashCode()}.{_program?.Data?.Name}.{material.Name}.{phase}",
            TimeSpan.FromSeconds(1),
            "[VkGizmoBindingSnapshot] phase={0} program='{1}' mesh='{2}' material='{3}' uniforms={4} MatColor={5} LineWidth={6} ArrowHeadLengthPixels={7} ArrowHeadHalfWidthPixels={8}",
            phase,
            _program?.Data?.Name ?? "<null>",
            Mesh?.Name ?? "<null>",
            material.Name ?? "<null>",
            snapshot.Uniforms.Count,
            FormatSnapshotUniform(snapshot, "MatColor"),
            FormatSnapshotUniform(snapshot, "LineWidth"),
            FormatSnapshotUniform(snapshot, "ArrowHeadLengthPixels"),
            FormatSnapshotUniform(snapshot, "ArrowHeadHalfWidthPixels"));
    }

    private static string FormatSnapshotUniform(ComputeDispatchSnapshot snapshot, string name)
    {
        if (!snapshot.Uniforms.TryGetValue(name, out ProgramUniformValue value))
            return "<missing>";

        string arraySuffix = value.IsArray ? "[]" : string.Empty;
        return $"{value.Type}{arraySuffix}:{FormatMaterialUniformDiagnosticValue(value.Value)}";
    }

    private static SampleCountFlags ResolveRasterizationSamples(XRFrameBuffer? target)
        => target?.EffectiveSampleCount switch
        {
            >= 64u => SampleCountFlags.Count64Bit,
            >= 32u => SampleCountFlags.Count32Bit,
            >= 16u => SampleCountFlags.Count16Bit,
            >= 8u => SampleCountFlags.Count8Bit,
            >= 4u => SampleCountFlags.Count4Bit,
            >= 2u => SampleCountFlags.Count2Bit,
            _ => SampleCountFlags.Count1Bit,
        };
}
