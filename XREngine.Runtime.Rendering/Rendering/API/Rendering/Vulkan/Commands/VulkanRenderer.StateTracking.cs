using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const int MaxMergedFrameOpRegistryCacheEntries = 8;

    private readonly VulkanStateTracker _state = new();
    private VulkanResourcePlanner _resourcePlanner = new();
    private VulkanResourceAllocator _resourceAllocator = new();
    private readonly VulkanBarrierPlanner _barrierPlanner = new();
    private VulkanCompiledRenderGraph _compiledRenderGraph = VulkanCompiledRenderGraph.Empty;
    private FrameOpContext? _lastActiveFrameOpContext;
    private ulong _resourcePlannerSignature = ulong.MaxValue;
    private ulong _resourceAllocationSignature = ulong.MaxValue;
    private bool _interactiveResizePlannerFrozen;
    private uint _interactiveResizeFrozenDisplayWidth;
    private uint _interactiveResizeFrozenDisplayHeight;
    private uint _interactiveResizeFrozenInternalWidth;
    private uint _interactiveResizeFrozenInternalHeight;
    private ulong _failedResourcePlannerSignature = ulong.MaxValue;
    private ulong _failedResourceAllocationSignature = ulong.MaxValue;
    private long _failedResourceAllocationTimestamp;
    private ResourcePlannerFastPathKey _resourcePlannerFastPathKey;
    private bool _hasResourcePlannerFastPathKey;
    private BarrierPlanFastPathKey _barrierPlanFastPathKey;
    private bool _hasBarrierPlanFastPathKey;
    private ResourcePlannerSignatureBreakdown _resourcePlannerSignatureBreakdown;
    private ulong _resourcePlannerRevision;
    private bool _isRecordingCommandBuffer;
    private int _commandChainFrozenPlanReaders;
    private ulong _commandChainFrozenResourcePlanRevision;
    private readonly Dictionary<string, XRDataBuffer> _trackedBuffersByName = new(StringComparer.OrdinalIgnoreCase);
    internal VulkanResourcePlanner ResourcePlanner => _resourcePlanner;
    internal VulkanResourcePlan ResourcePlan => _resourcePlanner.CurrentPlan;
    internal VulkanResourceAllocator ResourceAllocator => _resourceAllocator;
    internal VulkanCompiledRenderGraph CompiledRenderGraph => _compiledRenderGraph;
    internal ulong ResourcePlannerRevision => _resourcePlannerRevision;
    private bool IsCommandChainResourcePlanFrozen => Volatile.Read(ref _commandChainFrozenPlanReaders) > 0;
    private bool[]? _commandBufferDirtyFlags;
    private readonly object _commandBufferDirtyReasonLock = new();
    private readonly Dictionary<string, int> _commandBufferDirtyReasons = new(StringComparer.Ordinal);
    private long _lastCommandBufferDirtyReasonLogTimestamp;
    private XRFrameBuffer? _boundDrawFrameBuffer;
    private XRFrameBuffer? _boundReadFrameBuffer;
    private XRTexture? _lastWindowPresentColorTexture;
    private XRFrameBuffer? _lastWindowPresentFrameBuffer;
    private EReadBufferMode _readBufferMode = EReadBufferMode.ColorAttachment0;
    private EVulkanQueueOverlapMode _autoQueueOverlapMode = EVulkanQueueOverlapMode.GraphicsOnly;
    private EVulkanQueueOverlapMode _lastResolvedQueueOverlapMode = EVulkanQueueOverlapMode.GraphicsOnly;
    private int _queueOverlapPromotionStabilityFrames;
    private int _queueOverlapFramesInMode;
    private long _lastQueueOverlapSampleTimestamp;
    private ulong _lastQueueOverlapSampleFrameId = ulong.MaxValue;
    private double _queueOverlapFrameDeltaEmaMs = -1.0;
    private double _queueOverlapModeStartFrameDeltaMs = -1.0;
    private readonly List<MergedFrameOpRegistryCacheEntry> _mergedFrameOpRegistryCache = new(MaxMergedFrameOpRegistryCacheEntries);
    private IReadOnlyCollection<RenderPassMetadata>? _lastActiveFilterSourcePassMetadata;
    private IReadOnlyCollection<RenderPassMetadata>? _lastActiveFilterResult;
    private int _lastActiveFilterPassSetSignature = int.MinValue;
    private int _lastActiveFilterResourceSetSignature = int.MinValue;
    private static readonly TimeSpan ResourceAllocationFailureRetryDelay = TimeSpan.FromMilliseconds(750);

    private static readonly HashSet<string> VulkanPlannerOptionalResourceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "LightProbeIrradianceArray",
        "LightProbePrefilterArray",
        "LightProbePositions",
        "LightProbeTetrahedra",
        "LightProbeParameters",
        "LightProbeGridCells",
        "LightProbeGridIndices",
        "AtmosphereColor",
        "VolumetricFogColor"
    };

    private readonly record struct QueueOverlapMetrics(
        int ComputePassCount,
        int TransferUsageCount,
        int OverlapCandidatePassCount,
        int TransferCost,
        int QueueOwnershipTransfers,
        int BarrierStageFlushes,
        TimeSpan FrameDelta);

    private readonly record struct FrameOpRegistryCacheSource(
        RenderResourceRegistry Registry,
        int DescriptorSignature,
        int ResourceGenerationStamp);

    private sealed class MergedFrameOpRegistryCacheEntry(
        RenderResourceRegistry? primaryRegistry,
        FrameOpRegistryCacheSource[] sources,
        RenderResourceRegistry mergedRegistry,
        ulong lastUsedFrameId)
    {
        public RenderResourceRegistry? PrimaryRegistry { get; } = primaryRegistry;
        public FrameOpRegistryCacheSource[] Sources { get; } = sources;
        public RenderResourceRegistry MergedRegistry { get; } = mergedRegistry;
        public ulong LastUsedFrameId { get; set; } = lastUsedFrameId;
    }

    private readonly record struct ResourcePlannerSignatureBreakdown(
        int Registry,
        uint DisplayWidth,
        uint DisplayHeight,
        uint InternalWidth,
        uint InternalHeight,
        int PassMetadata,
        int GraphBatches,
        int GraphEdges,
        uint GraphicsQueueFamily,
        uint ComputeQueueFamily,
        uint TransferQueueFamily)
    {
        public override string ToString()
            => $"registry=0x{Registry:X8} dims={DisplayWidth}x{DisplayHeight}/{InternalWidth}x{InternalHeight} " +
               $"passes=0x{PassMetadata:X8} batches=0x{GraphBatches:X8} edges=0x{GraphEdges:X8} " +
               $"queues=g{GraphicsQueueFamily}/c{ComputeQueueFamily}/t{TransferQueueFamily}";

        public string DescribeDelta(in ResourcePlannerSignatureBreakdown previous)
        {
            StringBuilder builder = new();
            AppendDelta(builder, "resource-registry", previous.Registry, Registry, hexadecimal: true);
            AppendDelta(builder, "display-width", previous.DisplayWidth, DisplayWidth);
            AppendDelta(builder, "display-height", previous.DisplayHeight, DisplayHeight);
            AppendDelta(builder, "internal-width", previous.InternalWidth, InternalWidth);
            AppendDelta(builder, "internal-height", previous.InternalHeight, InternalHeight);
            AppendDelta(builder, "pass-metadata", previous.PassMetadata, PassMetadata, hexadecimal: true);
            AppendDelta(builder, "graph-batches", previous.GraphBatches, GraphBatches, hexadecimal: true);
            AppendDelta(builder, "graph-edges", previous.GraphEdges, GraphEdges, hexadecimal: true);
            AppendDelta(builder, "graphics-queue-family", previous.GraphicsQueueFamily, GraphicsQueueFamily);
            AppendDelta(builder, "compute-queue-family", previous.ComputeQueueFamily, ComputeQueueFamily);
            AppendDelta(builder, "transfer-queue-family", previous.TransferQueueFamily, TransferQueueFamily);
            return builder.Length == 0 ? "none" : builder.ToString();
        }

        private static void AppendDelta(StringBuilder builder, string name, int oldValue, int newValue, bool hexadecimal = false)
        {
            if (oldValue == newValue)
                return;

            AppendDeltaPrefix(builder);
            if (hexadecimal)
                builder.Append(name).Append("=0x").Append(oldValue.ToString("X8")).Append("->0x").Append(newValue.ToString("X8"));
            else
                builder.Append(name).Append('=').Append(oldValue).Append("->").Append(newValue);
        }

        private static void AppendDelta(StringBuilder builder, string name, uint oldValue, uint newValue)
        {
            if (oldValue == newValue)
                return;

            AppendDeltaPrefix(builder);
            builder.Append(name).Append('=').Append(oldValue).Append("->").Append(newValue);
        }

        private static void AppendDeltaPrefix(StringBuilder builder)
        {
            if (builder.Length > 0)
                builder.Append(", ");
        }
    }

    private readonly record struct ResourceAllocationSignatureBreakdown(
        int AllocationDescriptors,
        uint DisplayWidth,
        uint DisplayHeight,
        uint InternalWidth,
        uint InternalHeight,
        int PhysicalUsage,
        bool SupportsTransformFeedback)
    {
        public override string ToString()
            => $"allocDescriptors=0x{AllocationDescriptors:X8} dims={DisplayWidth}x{DisplayHeight}/{InternalWidth}x{InternalHeight} " +
               $"physicalUsage=0x{PhysicalUsage:X8} xfb={SupportsTransformFeedback}";
    }

    private readonly record struct ResourcePlannerFastPathKey(
        RenderResourceRegistry? Registry,
        int RegistryDescriptorRevision,
        IReadOnlyCollection<RenderPassMetadata>? ActivePassMetadata,
        int ActivePassMetadataRevision,
        int ActivePassSetSignature,
        int ActiveResourceSetSignature,
        uint DisplayWidth,
        uint DisplayHeight,
        uint InternalWidth,
        uint InternalHeight,
        VulkanBarrierPlanner.QueueOwnershipConfig QueueOwnership,
        bool SupportsTransformFeedback)
    {
        public bool Matches(in ResourcePlannerFastPathKey other)
            => ReferenceEquals(Registry, other.Registry)
                && RegistryDescriptorRevision == other.RegistryDescriptorRevision
                && ReferenceEquals(ActivePassMetadata, other.ActivePassMetadata)
                && ActivePassMetadataRevision == other.ActivePassMetadataRevision
                && ActivePassSetSignature == other.ActivePassSetSignature
                && ActiveResourceSetSignature == other.ActiveResourceSetSignature
                && DisplayWidth == other.DisplayWidth
                && DisplayHeight == other.DisplayHeight
                && InternalWidth == other.InternalWidth
                && InternalHeight == other.InternalHeight
                && QueueOwnership.Equals(other.QueueOwnership)
                && SupportsTransformFeedback == other.SupportsTransformFeedback;
    }

    private readonly record struct ResourcePlanningInputs(
        IReadOnlyCollection<RenderPassMetadata>? ActivePassMetadata,
        VulkanCompiledRenderGraph CompiledGraph,
        VulkanBarrierPlanner.QueueOwnershipConfig QueueOwnership,
        ResourcePlannerFastPathKey FastPathKey);

    private readonly struct CommandChainResourcePlanReadScope : IDisposable
    {
        private readonly VulkanRenderer _renderer;

        public CommandChainResourcePlanReadScope(VulkanRenderer renderer, ulong resourcePlanRevision)
        {
            _renderer = renderer;
            _renderer._commandChainFrozenResourcePlanRevision = resourcePlanRevision;
            Interlocked.Increment(ref _renderer._commandChainFrozenPlanReaders);
        }

        public void Dispose()
        {
            if (Interlocked.Decrement(ref _renderer._commandChainFrozenPlanReaders) == 0)
                _renderer._commandChainFrozenResourcePlanRevision = 0;
        }
    }

    private readonly record struct PhysicalAllocationPlan(
        VulkanResourceExtentContext ExtentContext,
        ulong Signature,
        bool Changed);

    private readonly record struct BarrierPlanFastPathKey(
        VulkanCompiledRenderGraph CompiledGraph,
        ulong ResourceAllocationSignature,
        VulkanBarrierPlanner.QueueOwnershipConfig QueueOwnership)
    {
        public bool Matches(in BarrierPlanFastPathKey other)
            => ReferenceEquals(CompiledGraph, other.CompiledGraph)
                && ResourceAllocationSignature == other.ResourceAllocationSignature
                && QueueOwnership.Equals(other.QueueOwnership);
    }

    internal Viewport GetCurrentViewport()
        => _state.GetViewport(ResolveCurrentDrawTargetExtent());

    internal Rect2D GetCurrentScissor()
        => _state.GetScissor(ResolveCurrentDrawTargetExtent());

    internal IndexedViewportScissorSnapshot GetCurrentIndexedViewportScissorSnapshot()
        => _state.GetIndexedViewportScissorSnapshot(ResolveCurrentDrawTargetExtent());

    internal readonly record struct IndexedViewportScissorSnapshot(
        Viewport[]? Viewports,
        Rect2D[]? Scissors,
        uint Count);

    internal readonly record struct VulkanFixedFunctionStateSnapshot(
        bool DepthTestEnabled,
        bool DepthWriteEnabled,
        CompareOp DepthCompareOp,
        bool StencilTestEnabled,
        StencilOpState FrontStencilState,
        StencilOpState BackStencilState,
        uint StencilWriteMask,
        ColorComponentFlags ColorWriteMask,
        CullModeFlags CullMode,
        FrontFace FrontFace,
        bool BlendEnabled,
        bool AlphaToCoverageEnabled,
        BlendOp ColorBlendOp,
        BlendOp AlphaBlendOp,
        BlendFactor SrcColorBlendFactor,
        BlendFactor DstColorBlendFactor,
        BlendFactor SrcAlphaBlendFactor,
        BlendFactor DstAlphaBlendFactor);

    /// <summary>
    /// Extent of the draw target that is actually bound right now. Quad-blit style
    /// passes bind FBOs through <see cref="XRFrameBuffer.BindForWriting"/> (engine-side
    /// stack) without invoking <see cref="BindFrameBuffer"/>, so the tracker's
    /// last-bound extent can be stale; prefer the live engine binding.
    /// </summary>
    internal Extent2D ResolveCurrentDrawTargetExtent()
    {
        XRFrameBuffer? fbo = GetCurrentDrawFrameBuffer();
        if (fbo is not null)
            return ResolveFrameBufferDrawExtent(fbo);

        return _state.GetCurrentTargetExtent();
    }

    internal XRFrameBuffer? GetCurrentDrawFrameBuffer()
        => XRFrameBuffer.BoundForWriting ?? _boundDrawFrameBuffer;

    internal static Extent2D ResolveFrameBufferDrawExtent(XRFrameBuffer fbo)
    {
        var targets = fbo.Targets;
        if (targets is null || targets.Length == 0)
            return new Extent2D(Math.Max(fbo.Width, 1u), Math.Max(fbo.Height, 1u));

        uint minWidth = uint.MaxValue;
        uint minHeight = uint.MaxValue;
        bool found = false;

        foreach (var (target, _, mip, _) in targets)
        {
            if (target is null)
                continue;

            uint width = Math.Max(target.Width, 1u);
            uint height = Math.Max(target.Height, 1u);
            int mipLevel = Math.Max(mip, 0);
            if (mipLevel > 0)
            {
                width = Math.Max(width >> mipLevel, 1u);
                height = Math.Max(height >> mipLevel, 1u);
            }

            minWidth = Math.Min(minWidth, width);
            minHeight = Math.Min(minHeight, height);
            found = true;
        }

        return found
            ? new Extent2D(minWidth, minHeight)
            : new Extent2D(Math.Max(fbo.Width, 1u), Math.Max(fbo.Height, 1u));
    }

    internal XRFrameBuffer? GetCurrentReadFrameBuffer()
        => _boundReadFrameBuffer;

    internal EReadBufferMode GetReadBufferMode()
        => _readBufferMode;

    internal bool GetDepthTestEnabled()
        => _state.GetDepthTestEnabled();

    internal bool GetDepthWriteEnabled()
        => _state.GetDepthWriteEnabled();

    internal CompareOp GetDepthCompareOp()
        => _state.GetDepthCompareOp();

    internal uint GetStencilWriteMask()
        => _state.GetStencilWriteMask();

    internal ColorComponentFlags GetColorWriteMask()
        => _state.GetColorWriteMask();

    internal CullModeFlags GetCullMode()
        => _state.GetCullMode();

    internal FrontFace GetFrontFace()
        => _state.GetFrontFace();

    internal bool GetBlendEnabled()
        => _state.GetBlendEnabled();

    internal bool GetAlphaToCoverageEnabled()
        => _state.GetAlphaToCoverageEnabled();

    internal BlendOp GetColorBlendOp()
        => _state.GetColorBlendOp();

    internal BlendOp GetAlphaBlendOp()
        => _state.GetAlphaBlendOp();

    internal BlendFactor GetSrcColorBlendFactor()
        => _state.GetSrcColorBlendFactor();

    internal BlendFactor GetDstColorBlendFactor()
        => _state.GetDstColorBlendFactor();

    internal BlendFactor GetSrcAlphaBlendFactor()
        => _state.GetSrcAlphaBlendFactor();

    internal BlendFactor GetDstAlphaBlendFactor()
        => _state.GetDstAlphaBlendFactor();

    internal bool GetStencilTestEnabled()
        => _state.GetStencilTestEnabled();

    internal StencilOpState GetFrontStencilState()
        => _state.GetFrontStencilState();

    internal StencilOpState GetBackStencilState()
        => _state.GetBackStencilState();

    internal VulkanFixedFunctionStateSnapshot CaptureFixedFunctionState()
        => _state.CaptureFixedFunctionState();

    internal void RestoreFixedFunctionState(in VulkanFixedFunctionStateSnapshot snapshot)
        => _state.RestoreFixedFunctionState(snapshot);

    internal bool GetCroppingEnabled()
        => _state.GetCroppingEnabled();

    internal ColorF4 GetClearColorValue()
        => _state.GetClearColorValue();

    internal float GetClearDepthValue()
        => _state.GetClearDepthValue();

    internal uint GetClearStencilValue()
        => _state.GetClearStencilValue();

    internal Extent2D GetCurrentTargetExtent()
        => _state.GetCurrentTargetExtent();

    private static bool _reportedNativeNegativeOneToOneDepth;
    private static bool _reportedShaderRemappedNegativeOneToOneDepth;

    private static ERenderClipDepthRange ResolveEffectiveVulkanClipDepthRange()
    {
        ERenderClipDepthRange requested = RuntimeEngine.Rendering.Settings.ClipDepthRange;
        if (requested != ERenderClipDepthRange.NegativeOneToOne)
            return requested;

        if (RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl)
        {
            if (!_reportedNativeNegativeOneToOneDepth)
            {
                _reportedNativeNegativeOneToOneDepth = true;
                Debug.Vulkan(
                    "[Vulkan] ClipDepthRange=NegativeOneToOne is using {0}.",
                    VulkanDepthClipControlExt.ExtensionName);
            }

            return requested;
        }

        if (!_reportedShaderRemappedNegativeOneToOneDepth)
        {
            _reportedShaderRemappedNegativeOneToOneDepth = true;
            Debug.VulkanWarning(
                "[Vulkan] ClipDepthRange=NegativeOneToOne was requested, but {0} is unavailable. " +
                "Keeping the engine's -1..1 clip-depth policy and remapping vertex shader gl_Position.z to Vulkan 0..w clip depth.",
                VulkanDepthClipControlExt.ExtensionName);
        }

        return requested;
    }

    private static Viewport CreateVulkanViewport(Extent2D extent)
    {
        _ = ResolveEffectiveVulkanClipDepthRange();
        return RuntimeEngine.Rendering.Settings.ClipSpaceYDirection == ERenderClipSpaceYDirection.YDown
            ? new Viewport
            {
                X = 0f,
                Y = 0f,
                Width = extent.Width,
                Height = extent.Height,
                MinDepth = 0f,
                MaxDepth = 1f
            }
            : new Viewport
            {
                X = 0f,
                Y = extent.Height,
                Width = extent.Width,
                Height = -(float)extent.Height,
                MinDepth = 0f,
                MaxDepth = 1f
            };
    }

    private static Viewport CreateVulkanViewport(BoundingRectangle region, Extent2D targetExtent)
    {
        _ = ResolveEffectiveVulkanClipDepthRange();
        if (RuntimeEngine.Rendering.Settings.ClipSpaceYDirection == ERenderClipSpaceYDirection.YDown)
        {
            return new Viewport
            {
                X = region.X,
                Y = targetExtent.Height - (region.Y + region.Height),
                Width = region.Width,
                Height = region.Height,
                MinDepth = 0.0f,
                MaxDepth = 1.0f
            };
        }

        return new Viewport
        {
            X = region.X,
            Y = targetExtent.Height - region.Y,
            Width = region.Width,
            Height = -region.Height,
            MinDepth = 0.0f,
            MaxDepth = 1.0f
        };
    }

}
