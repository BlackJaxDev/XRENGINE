using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
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
    private ResourcePlannerFastPathKey _resourcePlannerFastPathKey;
    private bool _hasResourcePlannerFastPathKey;
    private BarrierPlanFastPathKey _barrierPlanFastPathKey;
    private bool _hasBarrierPlanFastPathKey;
    private ResourcePlannerSignatureBreakdown _resourcePlannerSignatureBreakdown;
    private ulong _resourcePlannerRevision;
    private bool _isRecordingCommandBuffer;
    private readonly Dictionary<string, XRDataBuffer> _trackedBuffersByName = new(StringComparer.OrdinalIgnoreCase);
    internal VulkanResourcePlanner ResourcePlanner => _resourcePlanner;
    internal VulkanResourcePlan ResourcePlan => _resourcePlanner.CurrentPlan;
    internal VulkanResourceAllocator ResourceAllocator => _resourceAllocator;
    internal VulkanCompiledRenderGraph CompiledRenderGraph => _compiledRenderGraph;
    internal ulong ResourcePlannerRevision => _resourcePlannerRevision;
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

    private sealed class VulkanStateTracker
    {
        private Extent2D _swapchainExtent;
        private Extent2D _currentTargetExtent;
        private bool _viewportExplicitlySet;
        private BoundingRectangle _viewportRegion;
        private BoundingRectangle _scissorRegion;
        private BoundingRectangle[]? _indexedViewportRegions;
        private BoundingRectangle[]? _indexedScissorRegions;
        private Viewport[]? _indexedViewportCache;
        private Rect2D[]? _indexedScissorCache;
        private Extent2D _indexedCacheExtent;

        public VulkanStateTracker()
        {
            ClearColor = new ColorF4(0, 0, 0, 1);
            ClearDepth = 1.0f;
            ClearStencil = 0;
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit;
            DepthCompareOp = CompareOp.Less;
        }

        public ColorF4 ClearColor { get; private set; }
        public float ClearDepth { get; private set; }
        public uint ClearStencil { get; private set; }
        public bool ClearColorEnabled { get; private set; } = true;
        public bool ClearDepthEnabled { get; private set; } = true;
        public bool ClearStencilEnabled { get; private set; }

        public bool DepthTestEnabled { get; private set; }
        public bool DepthWriteEnabled { get; private set; } = true;
        public CompareOp DepthCompareOp { get; private set; }
        public uint StencilWriteMask { get; private set; } = uint.MaxValue;
        public bool StencilTestEnabled { get; private set; }
        public StencilOpState FrontStencilState { get; private set; }
        public StencilOpState BackStencilState { get; private set; }

        public ColorComponentFlags ColorWriteMask { get; private set; }
        public CullModeFlags CullMode { get; private set; } = CullModeFlags.BackBit;
        public FrontFace FrontFace { get; private set; } = FrontFace.CounterClockwise;
        public bool BlendEnabled { get; private set; }
        public bool AlphaToCoverageEnabled { get; private set; }
        public BlendOp ColorBlendOp { get; private set; } = BlendOp.Add;
        public BlendOp AlphaBlendOp { get; private set; } = BlendOp.Add;
        public BlendFactor SrcColorBlendFactor { get; private set; } = BlendFactor.One;
        public BlendFactor DstColorBlendFactor { get; private set; } = BlendFactor.Zero;
        public BlendFactor SrcAlphaBlendFactor { get; private set; } = BlendFactor.One;
        public BlendFactor DstAlphaBlendFactor { get; private set; } = BlendFactor.Zero;

        public bool CroppingEnabled { get; private set; }

        public EMemoryBarrierMask PendingMemoryBarrierMask { get; private set; }

        /// <summary>
        /// Per-pass memory barrier masks accumulated during the frame. When a pass index
        /// is known at barrier registration time, the mask is stored here instead of the
        /// global <see cref="PendingMemoryBarrierMask"/>.
        /// </summary>
        private readonly Dictionary<int, EMemoryBarrierMask> _perPassMemoryBarriers = new();

        public void RegisterMemoryBarrier(EMemoryBarrierMask mask)
            => PendingMemoryBarrierMask |= mask;

        /// <summary>
        /// Associates a memory barrier mask with a specific render-graph pass index so the
        /// barrier can be emitted at the correct point during command buffer recording.
        /// </summary>
        public void RegisterMemoryBarrierForPass(int passIndex, EMemoryBarrierMask mask)
        {
            if (mask == EMemoryBarrierMask.None)
                return;

            if (_perPassMemoryBarriers.TryGetValue(passIndex, out EMemoryBarrierMask existing))
                _perPassMemoryBarriers[passIndex] = existing | mask;
            else
                _perPassMemoryBarriers[passIndex] = mask;
        }

        /// <summary>
        /// Drains and returns the per-pass barrier mask for the given pass index, or
        /// <see cref="EMemoryBarrierMask.None"/> if nothing was registered.
        /// </summary>
        public EMemoryBarrierMask DrainMemoryBarrierForPass(int passIndex)
        {
            if (!_perPassMemoryBarriers.Remove(passIndex, out EMemoryBarrierMask mask))
                return EMemoryBarrierMask.None;
            return mask;
        }

        public void ClearPendingMemoryBarrierMask()
        {
            PendingMemoryBarrierMask = EMemoryBarrierMask.None;
            _perPassMemoryBarriers.Clear();
        }

        public void SetSwapchainExtent(Extent2D extent)
        {
            _swapchainExtent = extent;
            if (_currentTargetExtent.Width == 0 && _currentTargetExtent.Height == 0)
                _currentTargetExtent = extent;
        }

        public bool SetCurrentTargetExtent(Extent2D extent)
        {
            if (_currentTargetExtent.Width == extent.Width &&
                _currentTargetExtent.Height == extent.Height)
            {
                return false;
            }

            _currentTargetExtent = extent;
            return true;
        }

        public Extent2D GetCurrentTargetExtent()
            => _currentTargetExtent;

        // Viewport/scissor regions are stored in engine bottom-left coordinates and
        // converted to Vulkan coordinates lazily, against the extent of the target
        // bound at read (draw-enqueue) time. Eager conversion at Set time used
        // whatever target was bound then; pipeline code sets the render area before
        // binding the destination FBO, which produced viewports computed against the
        // previous pass's target height (off-target rasterization on half-res passes).
        public Viewport GetViewport(Extent2D targetExtent)
            => _viewportExplicitlySet
                ? CreateVulkanViewport(_viewportRegion, targetExtent)
                : CreateVulkanViewport(targetExtent);

        public bool SetViewport(BoundingRectangle region)
        {
            if (_viewportExplicitlySet && SameRectangle(_viewportRegion, region))
                return false;

            // Engine regions remain bottom-left rectangles. The clip-space policy chooses
            // whether Vulkan uses a negative-height GL-style viewport or native Y-down mapping.
            _viewportRegion = region;
            _viewportExplicitlySet = true;
            return true;
        }

        public Rect2D GetScissor(Extent2D targetExtent)
            => CroppingEnabled
                ? CreateVulkanScissor(_scissorRegion, targetExtent)
                : DefaultScissor(targetExtent);

        public bool SetScissor(BoundingRectangle region)
        {
            if (SameRectangle(_scissorRegion, region))
                return false;

            _scissorRegion = region;
            return true;
        }

        public void SetIndexedViewportScissors(
            ReadOnlySpan<BoundingRectangle> viewports,
            ReadOnlySpan<BoundingRectangle> scissors)
        {
            int count = Math.Min(viewports.Length, scissors.Length);
            if (count <= 0)
            {
                ClearIndexedViewportScissors();
                return;
            }

            BoundingRectangle[] viewportRegions = new BoundingRectangle[count];
            BoundingRectangle[] scissorRegions = new BoundingRectangle[count];
            for (int i = 0; i < count; i++)
            {
                BoundingRectangle viewport = viewports[i];
                BoundingRectangle scissor = scissors[i];
                viewport.CheckProperDimensions();
                scissor.CheckProperDimensions();
                viewportRegions[i] = viewport;
                scissorRegions[i] = scissor;
            }

            _indexedViewportRegions = viewportRegions;
            _indexedScissorRegions = scissorRegions;
            _indexedViewportCache = null;
            _indexedScissorCache = null;
            _indexedCacheExtent = default;
        }

        public void ClearIndexedViewportScissors()
        {
            _indexedViewportRegions = null;
            _indexedScissorRegions = null;
            _indexedViewportCache = null;
            _indexedScissorCache = null;
            _indexedCacheExtent = default;
        }

        public IndexedViewportScissorSnapshot GetIndexedViewportScissorSnapshot(Extent2D targetExtent)
        {
            if (_indexedViewportRegions is null ||
                _indexedScissorRegions is null ||
                _indexedViewportRegions.Length == 0 ||
                _indexedViewportRegions.Length != _indexedScissorRegions.Length)
            {
                return default;
            }

            if (_indexedViewportCache is not null &&
                _indexedScissorCache is not null &&
                _indexedCacheExtent.Width == targetExtent.Width &&
                _indexedCacheExtent.Height == targetExtent.Height)
            {
                return new IndexedViewportScissorSnapshot(
                    _indexedViewportCache,
                    _indexedScissorCache,
                    (uint)_indexedViewportCache.Length);
            }

            int count = _indexedViewportRegions.Length;
            Viewport[] viewports = new Viewport[count];
            Rect2D[] scissors = new Rect2D[count];
            for (int i = 0; i < count; i++)
            {
                viewports[i] = CreateVulkanViewport(_indexedViewportRegions[i], targetExtent);
                scissors[i] = CreateVulkanScissor(_indexedScissorRegions[i], targetExtent);
            }

            _indexedViewportCache = viewports;
            _indexedScissorCache = scissors;
            _indexedCacheExtent = targetExtent;
            return new IndexedViewportScissorSnapshot(viewports, scissors, (uint)count);
        }

        private static Rect2D CreateVulkanScissor(BoundingRectangle region, Extent2D targetExtent)
        {
            int targetWidth = (int)Math.Max(targetExtent.Width, 1u);
            int targetHeight = (int)Math.Max(targetExtent.Height, 1u);

            int clampedX = Math.Clamp(region.X, 0, targetWidth);
            int clampedBottomY = Math.Clamp(region.Y, 0, targetHeight);

            int maxWidth = Math.Max(targetWidth - clampedX, 0);
            int maxHeight = Math.Max(targetHeight - clampedBottomY, 0);

            int clampedWidth = Math.Clamp(Math.Max(region.Width, 0), 0, maxWidth);
            int clampedHeight = Math.Clamp(Math.Max(region.Height, 0), 0, maxHeight);

            // Convert bottom-left origin to Vulkan's top-left scissor origin after clamping.
            int yTopLeft = targetHeight - (clampedBottomY + clampedHeight);
            return new Rect2D
            {
                Offset = new Offset2D(clampedX, Math.Max(yTopLeft, 0)),
                Extent = new Extent2D((uint)clampedWidth, (uint)clampedHeight)
            };
        }

        public bool SetCroppingEnabled(bool enabled)
        {
            if (CroppingEnabled == enabled)
                return false;

            CroppingEnabled = enabled;
            return true;
        }

        public bool GetDepthTestEnabled() => DepthTestEnabled;
        public bool GetDepthWriteEnabled() => DepthWriteEnabled;
        public CompareOp GetDepthCompareOp() => DepthCompareOp;
        public uint GetStencilWriteMask() => StencilWriteMask;
        public ColorComponentFlags GetColorWriteMask() => ColorWriteMask;
        public bool GetCroppingEnabled() => CroppingEnabled;
        public ColorF4 GetClearColorValue() => ClearColor;
        public float GetClearDepthValue() => ClearDepth;
        public uint GetClearStencilValue() => ClearStencil;
        public CullModeFlags GetCullMode() => CullMode;
        public FrontFace GetFrontFace() => FrontFace;
        public bool GetBlendEnabled() => BlendEnabled;
        public BlendOp GetColorBlendOp() => ColorBlendOp;
        public BlendOp GetAlphaBlendOp() => AlphaBlendOp;
        public BlendFactor GetSrcColorBlendFactor() => SrcColorBlendFactor;
        public BlendFactor GetDstColorBlendFactor() => DstColorBlendFactor;
        public BlendFactor GetSrcAlphaBlendFactor() => SrcAlphaBlendFactor;
        public BlendFactor GetDstAlphaBlendFactor() => DstAlphaBlendFactor;
        public bool GetAlphaToCoverageEnabled() => AlphaToCoverageEnabled;
        public bool GetStencilTestEnabled() => StencilTestEnabled;
        public StencilOpState GetFrontStencilState() => FrontStencilState;
        public StencilOpState GetBackStencilState() => BackStencilState;

        public VulkanFixedFunctionStateSnapshot CaptureFixedFunctionState()
            => new(
                DepthTestEnabled,
                DepthWriteEnabled,
                DepthCompareOp,
                StencilTestEnabled,
                FrontStencilState,
                BackStencilState,
                StencilWriteMask,
                ColorWriteMask,
                CullMode,
                FrontFace,
                BlendEnabled,
                AlphaToCoverageEnabled,
                ColorBlendOp,
                AlphaBlendOp,
                SrcColorBlendFactor,
                DstColorBlendFactor,
                SrcAlphaBlendFactor,
                DstAlphaBlendFactor);

        public void RestoreFixedFunctionState(in VulkanFixedFunctionStateSnapshot snapshot)
        {
            DepthTestEnabled = snapshot.DepthTestEnabled;
            DepthWriteEnabled = snapshot.DepthWriteEnabled;
            DepthCompareOp = snapshot.DepthCompareOp;
            StencilTestEnabled = snapshot.StencilTestEnabled;
            FrontStencilState = snapshot.FrontStencilState;
            BackStencilState = snapshot.BackStencilState;
            StencilWriteMask = snapshot.StencilWriteMask;
            ColorWriteMask = snapshot.ColorWriteMask;
            CullMode = snapshot.CullMode;
            FrontFace = snapshot.FrontFace;
            BlendEnabled = snapshot.BlendEnabled;
            AlphaToCoverageEnabled = snapshot.AlphaToCoverageEnabled;
            ColorBlendOp = snapshot.ColorBlendOp;
            AlphaBlendOp = snapshot.AlphaBlendOp;
            SrcColorBlendFactor = snapshot.SrcColorBlendFactor;
            DstColorBlendFactor = snapshot.DstColorBlendFactor;
            SrcAlphaBlendFactor = snapshot.SrcAlphaBlendFactor;
            DstAlphaBlendFactor = snapshot.DstAlphaBlendFactor;
        }

        private static Rect2D DefaultScissor(Extent2D extent)
            => new()
            {
                Offset = new Offset2D(0, 0),
                Extent = new Extent2D(extent.Width, extent.Height)
            };

        public bool SetClearColor(ColorF4 color)
        {
            if (SameColor(ClearColor, color))
                return false;

            ClearColor = color;
            return true;
        }

        public bool SetClearDepth(float depth)
        {
            if (ClearDepth == depth)
                return false;

            ClearDepth = depth;
            return true;
        }

        public bool SetClearStencil(int stencil)
        {
            uint value = (uint)stencil;
            if (ClearStencil == value)
                return false;

            ClearStencil = value;
            return true;
        }

        public bool SetClearState(bool color, bool depth, bool stencil)
        {
            if (ClearColorEnabled == color &&
                ClearDepthEnabled == depth &&
                ClearStencilEnabled == stencil)
            {
                return false;
            }

            ClearColorEnabled = color;
            ClearDepthEnabled = depth;
            ClearStencilEnabled = stencil;
            return true;
        }

        public bool SetDepthTestEnabled(bool enabled)
        {
            if (DepthTestEnabled == enabled)
                return false;

            DepthTestEnabled = enabled;
            return true;
        }

        public bool SetDepthWriteEnabled(bool enabled)
        {
            if (DepthWriteEnabled == enabled)
                return false;

            DepthWriteEnabled = enabled;
            return true;
        }

        public bool SetDepthCompare(CompareOp op)
        {
            if (DepthCompareOp == op)
                return false;

            DepthCompareOp = op;
            return true;
        }

        public bool SetStencilWriteMask(uint mask)
        {
            if (StencilWriteMask == mask)
                return false;

            StencilWriteMask = mask;
            return true;
        }

        public bool SetStencilEnabled(bool enabled)
        {
            if (StencilTestEnabled == enabled)
                return false;

            StencilTestEnabled = enabled;
            return true;
        }

        public bool SetStencilStates(StencilOpState front, StencilOpState back)
        {
            if (SameStencilState(FrontStencilState, front) &&
                SameStencilState(BackStencilState, back))
            {
                return false;
            }

            FrontStencilState = front;
            BackStencilState = back;
            return true;
        }

        public bool SetColorMask(bool red, bool green, bool blue, bool alpha)
        {
            ColorComponentFlags mask = ColorComponentFlags.None;
            if (red)
                mask |= ColorComponentFlags.RBit;
            if (green)
                mask |= ColorComponentFlags.GBit;
            if (blue)
                mask |= ColorComponentFlags.BBit;
            if (alpha)
                mask |= ColorComponentFlags.ABit;

            if (ColorWriteMask == mask)
                return false;

            ColorWriteMask = mask;
            return true;
        }

        public bool SetCullMode(CullModeFlags mode)
        {
            if (CullMode == mode)
                return false;

            CullMode = mode;
            return true;
        }

        public bool SetFrontFace(FrontFace frontFace)
        {
            if (FrontFace == frontFace)
                return false;

            FrontFace = frontFace;
            return true;
        }

        public bool SetBlendState(
            bool enabled,
            BlendOp colorOp,
            BlendOp alphaOp,
            BlendFactor srcColor,
            BlendFactor dstColor,
            BlendFactor srcAlpha,
            BlendFactor dstAlpha)
        {
            if (BlendEnabled == enabled &&
                ColorBlendOp == colorOp &&
                AlphaBlendOp == alphaOp &&
                SrcColorBlendFactor == srcColor &&
                DstColorBlendFactor == dstColor &&
                SrcAlphaBlendFactor == srcAlpha &&
                DstAlphaBlendFactor == dstAlpha)
            {
                return false;
            }

            BlendEnabled = enabled;
            ColorBlendOp = colorOp;
            AlphaBlendOp = alphaOp;
            SrcColorBlendFactor = srcColor;
            DstColorBlendFactor = dstColor;
            SrcAlphaBlendFactor = srcAlpha;
            DstAlphaBlendFactor = dstAlpha;
            return true;
        }

        public bool SetAlphaToCoverageEnabled(bool enabled)
        {
            if (AlphaToCoverageEnabled == enabled)
                return false;

            AlphaToCoverageEnabled = enabled;
            return true;
        }

        private static bool SameRectangle(BoundingRectangle left, BoundingRectangle right)
            => left.X == right.X &&
               left.Y == right.Y &&
               left.Width == right.Width &&
               left.Height == right.Height &&
               left.LocalOriginPercentage == right.LocalOriginPercentage;

        private static bool SameColor(ColorF4 left, ColorF4 right)
            => left.R == right.R &&
               left.G == right.G &&
               left.B == right.B &&
               left.A == right.A;

        private static bool SameStencilState(StencilOpState left, StencilOpState right)
            => left.FailOp == right.FailOp &&
               left.PassOp == right.PassOp &&
               left.DepthFailOp == right.DepthFailOp &&
               left.CompareOp == right.CompareOp &&
               left.CompareMask == right.CompareMask &&
               left.WriteMask == right.WriteMask &&
               left.Reference == right.Reference;

        public void WriteClearValues(ClearValue* destination, uint attachmentCount)
        {
            if (attachmentCount == 0)
                return;

            destination[0] = new ClearValue
            {
                Color = new ClearColorValue
                {
                    Float32_0 = ClearColor.R,
                    Float32_1 = ClearColor.G,
                    Float32_2 = ClearColor.B,
                    Float32_3 = ClearColor.A
                }
            };

            if (attachmentCount > 1)
            {
                destination[1] = new ClearValue
                {
                    DepthStencil = new ClearDepthStencilValue
                    {
                        Depth = ClearDepth,
                        Stencil = ClearStencil
                    }
                };
            }
        }
    }

    private void OnSwapchainExtentChanged(Extent2D extent)
    {
        _state.SetSwapchainExtent(extent);
        if (_boundDrawFrameBuffer is null)
            _state.SetCurrentTargetExtent(extent);
        MarkCommandBuffersDirty();
    }

    private void UpdateResourcePlannerFromPipeline()
    {
        UpdateResourcePlannerFromContext(CaptureFrameOpContext());
    }

    internal readonly record struct FrameOpContext(
        int PipelineIdentity,
        int ViewportIdentity,
        XRRenderPipelineInstance? PipelineInstance,
        RenderResourceRegistry? ResourceRegistry,
        IReadOnlyCollection<RenderPassMetadata>? PassMetadata,
        uint DisplayWidth = 1u,
        uint DisplayHeight = 1u,
        uint InternalWidth = 1u,
        uint InternalHeight = 1u)
    {
        public int SchedulingIdentity => HashCode.Combine(PipelineIdentity, ViewportIdentity);
    }

    internal FrameOpContext CaptureFrameOpContext()
    {
        XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        XRViewport? viewport = RuntimeEngine.Rendering.State.RenderingViewport;
        uint displayWidth = ResolvePositiveDimension(
            pipeline?.ResourceDisplayWidth,
            viewport?.Width,
            swapChainExtent.Width,
            1u);
        uint displayHeight = ResolvePositiveDimension(
            pipeline?.ResourceDisplayHeight,
            viewport?.Height,
            swapChainExtent.Height,
            1u);
        uint internalWidth = ResolvePositiveDimension(
            pipeline?.ResourceInternalWidth,
            viewport?.InternalWidth,
            displayWidth,
            1u);
        uint internalHeight = ResolvePositiveDimension(
            pipeline?.ResourceInternalHeight,
            viewport?.InternalHeight,
            displayHeight,
            1u);

        FrameOpContext context = new(
            pipeline?.GetHashCode() ?? 0,
            viewport?.GetHashCode() ?? 0,
            pipeline,
            pipeline?.Resources,
            pipeline?.Pipeline?.PassMetadata,
            displayWidth,
            displayHeight,
            internalWidth,
            internalHeight);

        if (pipeline is not null)
            _lastActiveFrameOpContext = context;

        return context;
    }

    internal FrameOpContext CaptureFrameOpContextOrLastActive()
    {
        FrameOpContext context = CaptureFrameOpContext();
        if (context.PipelineInstance is not null || context.PassMetadata is { Count: > 0 })
            return context;

        return _lastActiveFrameOpContext ?? context;
    }

    internal bool TryEnsurePhysicalImageForTextureResource(
        string? resourceName,
        out VulkanPhysicalImageGroup? group)
    {
        group = null;
        if (string.IsNullOrWhiteSpace(resourceName))
            return false;

        if (_resourceAllocator.TryGetPhysicalGroupForResource(resourceName, out group) &&
            group?.IsAllocated == true)
        {
            return true;
        }

        FrameOpContext context = CaptureFrameOpContextOrLastActive();
        if (context.ResourceRegistry is null ||
            !context.ResourceRegistry.TextureRecords.ContainsKey(resourceName))
        {
            group = null;
            return false;
        }

        if (_isRecordingCommandBuffer)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.ResourcePlanner.LazyRebuildDuringRecord.{resourceName}",
                TimeSpan.FromSeconds(2),
                "[VulkanResourcePlanner] Deferring lazy physical-image plan rebuild for '{0}' during command-buffer recording.",
                resourceName);
            group = null;
            return false;
        }

        UpdateResourcePlannerFromContext(context);

        if (_resourceAllocator.TryGetPhysicalGroupForResource(resourceName, out group) &&
            group is not null)
        {
            group.EnsureAllocated(this);
            if (group.LastKnownLayout == ImageLayout.Undefined)
                TransitionNewPhysicalImagesToInitialLayout();
            return group.IsAllocated;
        }

        group = null;
        return false;
    }

    private FrameOpContext PrepareResourcePlannerForFrameOps(FrameOp[] ops)
    {
        if (ops.Length == 0)
        {
            FrameOpContext context = CaptureFrameOpContext();
            UpdateResourcePlannerFromContext(context);
            return context;
        }

        HashSet<int>? activePassIndices = BuildActiveFrameOpPassSet(ops);
        HashSet<string>? activeFrameBufferNames = BuildActiveFrameOpFrameBufferSet(ops);
        int activeResourceSetSignature = ComputeActiveFrameBufferSetSignature(activeFrameBufferNames);
        FrameOpContext primary = SelectPrimaryPlannerContext(ops);
        RenderResourceRegistry? mergedRegistry = BuildMergedFrameOpRegistry(ops, primary.ResourceRegistry);
        FrameOpContext plannerContext = mergedRegistry is null
            ? primary
            : primary with { ResourceRegistry = mergedRegistry };

        plannerContext = RefreshPlannerExtentsFromLiveContext(plannerContext, ops);
        UpdateResourcePlannerFromContext(
            plannerContext,
            activePassIndices,
            activeFrameBufferNames,
            activeResourceSetSignature);
        return plannerContext;
    }

    private static HashSet<int>? BuildActiveFrameOpPassSet(FrameOp[] ops)
    {
        HashSet<int> passIndices = [];
        foreach (FrameOp op in ops)
        {
            if (op.PassIndex != int.MinValue)
                passIndices.Add(op.PassIndex);
        }

        return passIndices.Count > 0 ? passIndices : null;
    }

    private static HashSet<string>? BuildActiveFrameOpFrameBufferSet(FrameOp[] ops)
    {
        HashSet<string> frameBufferNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (FrameOp op in ops)
        {
            AddFrameBufferName(frameBufferNames, op.Target);

            if (op is BlitOp blit)
            {
                AddFrameBufferName(frameBufferNames, blit.InFbo);
                AddFrameBufferName(frameBufferNames, blit.OutFbo);
            }
        }

        return frameBufferNames.Count > 0 ? frameBufferNames : null;
    }

    private static void AddFrameBufferName(HashSet<string> frameBufferNames, XRFrameBuffer? frameBuffer)
    {
        string? name = frameBuffer?.Name;
        if (!string.IsNullOrWhiteSpace(name))
            frameBufferNames.Add(name);
    }

    private FrameOpContext RefreshPlannerExtentsFromLiveContext(FrameOpContext context, FrameOp[] ops)
    {
        FrameOpContext live = CaptureFrameOpContextOrLastActive();
        bool refreshExtents =
            ReferenceEquals(context.PipelineInstance, live.PipelineInstance) ||
            ReferenceEquals(context.ResourceRegistry, live.ResourceRegistry);

        if (!refreshExtents)
        {
            foreach (FrameOp op in ops)
            {
                if (VulkanRenderGraphCompiler.OpTargetsSwapchain(op))
                {
                    refreshExtents = true;
                    break;
                }
            }
        }

        if (!refreshExtents)
            return context;

        uint displayWidth = live.DisplayWidth > 0 ? live.DisplayWidth : context.DisplayWidth;
        uint displayHeight = live.DisplayHeight > 0 ? live.DisplayHeight : context.DisplayHeight;
        uint internalWidth = live.InternalWidth > 0 ? live.InternalWidth : context.InternalWidth;
        uint internalHeight = live.InternalHeight > 0 ? live.InternalHeight : context.InternalHeight;

        if (displayWidth == context.DisplayWidth &&
            displayHeight == context.DisplayHeight &&
            internalWidth == context.InternalWidth &&
            internalHeight == context.InternalHeight)
        {
            return context;
        }

        Debug.VulkanEvery(
            $"Vulkan.ResourcePlanner.RefreshFrameOpExtents.{context.PipelineIdentity}.{context.ViewportIdentity}",
            TimeSpan.FromSeconds(1),
            "[VulkanResourcePlanner] Refreshing frame-op planner extents from live viewport. Old={0}x{1}/{2}x{3} Live={4}x{5}/{6}x{7}.",
            context.DisplayWidth,
            context.DisplayHeight,
            context.InternalWidth,
            context.InternalHeight,
            displayWidth,
            displayHeight,
            internalWidth,
            internalHeight);

        return context with
        {
            DisplayWidth = displayWidth,
            DisplayHeight = displayHeight,
            InternalWidth = internalWidth,
            InternalHeight = internalHeight
        };
    }

    private static FrameOpContext SelectPrimaryPlannerContext(FrameOp[] ops)
    {
        FrameOpContext fallback = ops[0].Context;
        FrameOpContext best = fallback;
        int bestScore = int.MinValue;

        foreach (FrameOp op in ops)
        {
            FrameOpContext context = op.Context;
            if (context.ResourceRegistry is null)
                continue;

            int score = 1;
            score += Math.Min(context.ResourceRegistry.TextureRecords.Count, 128);
            score += Math.Min(context.ResourceRegistry.FrameBufferRecords.Count, 128) * 2;
            score += (context.PassMetadata?.Count ?? 0) * 4;
            if (VulkanRenderGraphCompiler.OpTargetsSwapchain(op))
                score += 16;

            foreach (XRFrameBuffer target in EnumerateFrameOpFrameBuffers(op))
            {
                if (!string.IsNullOrWhiteSpace(target.Name) &&
                    context.ResourceRegistry.FrameBufferRecords.ContainsKey(target.Name))
                {
                    score += 256;
                }
                else
                {
                    score += 32;
                }
            }

            if (score > bestScore ||
                (score == bestScore && ComparePlannerContextTieBreak(context, best) < 0))
            {
                bestScore = score;
                best = context;
            }
        }

        return best;
    }

    private static int ComparePlannerContextTieBreak(in FrameOpContext left, in FrameOpContext right)
    {
        int compare = left.PipelineIdentity.CompareTo(right.PipelineIdentity);
        if (compare != 0)
            return compare;

        compare = left.ViewportIdentity.CompareTo(right.ViewportIdentity);
        if (compare != 0)
            return compare;

        compare = (left.ResourceRegistry?.DescriptorSignature ?? 0)
            .CompareTo(right.ResourceRegistry?.DescriptorSignature ?? 0);
        if (compare != 0)
            return compare;

        return (left.PassMetadata?.Count ?? 0).CompareTo(right.PassMetadata?.Count ?? 0);
    }

    private static uint ResolvePositiveDimension(uint? primary, int? secondary, uint tertiary, uint fallback)
    {
        if (primary.HasValue && primary.Value > 0)
            return primary.Value;

        if (secondary.HasValue && secondary.Value > 0)
            return (uint)secondary.Value;

        return tertiary > 0 ? tertiary : fallback;
    }

    private VulkanResourceExtentContext BuildResourceExtentContext(in FrameOpContext context)
    {
        uint displayWidth = context.DisplayWidth > 0
            ? context.DisplayWidth
            : Math.Max(swapChainExtent.Width, 1u);
        uint displayHeight = context.DisplayHeight > 0
            ? context.DisplayHeight
            : Math.Max(swapChainExtent.Height, 1u);
        uint internalWidth = context.InternalWidth > 0
            ? context.InternalWidth
            : displayWidth;
        uint internalHeight = context.InternalHeight > 0
            ? context.InternalHeight
            : displayHeight;

        return new VulkanResourceExtentContext(
            displayWidth,
            displayHeight,
            internalWidth,
            internalHeight);
    }

    private RenderResourceRegistry? BuildMergedFrameOpRegistry(
        FrameOp[] ops,
        RenderResourceRegistry? primaryRegistry)
    {
        RenderResourceRegistry[] registries = CollectUniqueFrameOpRegistries(ops);

        if (registries.Length == 0)
            return primaryRegistry;

        if (registries.Length == 1)
            return registries[0];

        if (primaryRegistry is not null && RegistriesCoveredByPrimary(registries, primaryRegistry))
            return primaryRegistry;

        FrameOpRegistryCacheSource[] cacheSources = BuildFrameOpRegistryCacheSources(ops, registries);
        if (TryGetCachedMergedFrameOpRegistry(primaryRegistry, cacheSources, out RenderResourceRegistry? cachedRegistry))
            return cachedRegistry;

        RenderResourceRegistry merged = new();
        if (primaryRegistry is not null)
            AddRegistryDescriptors(merged, primaryRegistry, overwrite: true);

        foreach (RenderResourceRegistry registry in registries)
        {
            if (ReferenceEquals(registry, primaryRegistry))
                continue;

            AddRegistryDescriptors(merged, registry, overwrite: false);
        }

        RememberMergedFrameOpRegistry(primaryRegistry, cacheSources, merged);
        return merged;
    }

    private static RenderResourceRegistry[] CollectUniqueFrameOpRegistries(FrameOp[] ops)
    {
        List<RenderResourceRegistry>? registries = null;
        foreach (FrameOp op in ops)
        {
            if (op.Context.ResourceRegistry is not { } registry)
                continue;

            registries ??= new();
            bool exists = false;
            for (int i = 0; i < registries.Count; i++)
            {
                if (ReferenceEquals(registries[i], registry))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
                registries.Add(registry);
        }

        if (registries is null || registries.Count == 0)
            return [];

        registries.Sort(static (left, right) =>
            RuntimeHelpers.GetHashCode(left).CompareTo(RuntimeHelpers.GetHashCode(right)));
        return registries.ToArray();
    }

    private static FrameOpRegistryCacheSource[] BuildFrameOpRegistryCacheSources(
        FrameOp[] ops,
        RenderResourceRegistry[] registries)
    {
        FrameOpRegistryCacheSource[] sources = new FrameOpRegistryCacheSource[registries.Length];
        for (int i = 0; i < registries.Length; i++)
        {
            RenderResourceRegistry registry = registries[i];
            sources[i] = new FrameOpRegistryCacheSource(
                registry,
                ComputeResourceRegistrySignature(registry),
                FindRegistryGenerationStamp(ops, registry));
        }

        return sources;
    }

    private static int FindRegistryGenerationStamp(FrameOp[] ops, RenderResourceRegistry registry)
    {
        int stamp = 0;
        foreach (FrameOp op in ops)
        {
            if (ReferenceEquals(op.Context.ResourceRegistry, registry) &&
                op.Context.PipelineInstance is { } pipeline)
            {
                stamp = Math.Max(stamp, pipeline.ResourceGeneration);
            }
        }

        return stamp;
    }

    private bool TryGetCachedMergedFrameOpRegistry(
        RenderResourceRegistry? primaryRegistry,
        FrameOpRegistryCacheSource[] sources,
        out RenderResourceRegistry? mergedRegistry)
    {
        for (int i = 0; i < _mergedFrameOpRegistryCache.Count; i++)
        {
            MergedFrameOpRegistryCacheEntry entry = _mergedFrameOpRegistryCache[i];
            if (!ReferenceEquals(entry.PrimaryRegistry, primaryRegistry) ||
                !FrameOpRegistryCacheSourcesMatch(entry.Sources, sources))
            {
                continue;
            }

            entry.LastUsedFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
            mergedRegistry = entry.MergedRegistry;
            return true;
        }

        mergedRegistry = null;
        return false;
    }

    private void RememberMergedFrameOpRegistry(
        RenderResourceRegistry? primaryRegistry,
        FrameOpRegistryCacheSource[] sources,
        RenderResourceRegistry mergedRegistry)
    {
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        _mergedFrameOpRegistryCache.Add(new MergedFrameOpRegistryCacheEntry(
            primaryRegistry,
            sources,
            mergedRegistry,
            frameId));

        if (_mergedFrameOpRegistryCache.Count <= MaxMergedFrameOpRegistryCacheEntries)
            return;

        int oldestIndex = 0;
        ulong oldestFrameId = _mergedFrameOpRegistryCache[0].LastUsedFrameId;
        for (int i = 1; i < _mergedFrameOpRegistryCache.Count; i++)
        {
            ulong candidateFrameId = _mergedFrameOpRegistryCache[i].LastUsedFrameId;
            if (candidateFrameId < oldestFrameId)
            {
                oldestIndex = i;
                oldestFrameId = candidateFrameId;
            }
        }

        _mergedFrameOpRegistryCache.RemoveAt(oldestIndex);
    }

    private static bool FrameOpRegistryCacheSourcesMatch(
        FrameOpRegistryCacheSource[] cached,
        FrameOpRegistryCacheSource[] current)
    {
        if (cached.Length != current.Length)
            return false;

        for (int i = 0; i < cached.Length; i++)
        {
            if (!ReferenceEquals(cached[i].Registry, current[i].Registry) ||
                cached[i].DescriptorSignature != current[i].DescriptorSignature ||
                cached[i].ResourceGenerationStamp != current[i].ResourceGenerationStamp)
            {
                return false;
            }
        }

        return true;
    }

    private static bool RegistriesCoveredByPrimary(
        IEnumerable<RenderResourceRegistry> registries,
        RenderResourceRegistry primaryRegistry)
    {
        foreach (RenderResourceRegistry registry in registries)
        {
            if (ReferenceEquals(registry, primaryRegistry))
                continue;

            if (!TextureDescriptorsCoveredByPrimary(registry, primaryRegistry) ||
                !FrameBufferDescriptorsCoveredByPrimary(registry, primaryRegistry) ||
                !BufferDescriptorsCoveredByPrimary(registry, primaryRegistry))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TextureDescriptorsCoveredByPrimary(
        RenderResourceRegistry source,
        RenderResourceRegistry primary)
    {
        foreach (KeyValuePair<string, RenderTextureResource> pair in source.TextureRecords)
        {
            if (!primary.TextureRecords.TryGetValue(pair.Key, out RenderTextureResource? primaryRecord) ||
                !EqualityComparer<TextureResourceDescriptor>.Default.Equals(primaryRecord.Descriptor, pair.Value.Descriptor))
            {
                return false;
            }
        }

        return true;
    }

    private static bool FrameBufferDescriptorsCoveredByPrimary(
        RenderResourceRegistry source,
        RenderResourceRegistry primary)
    {
        foreach (KeyValuePair<string, RenderFrameBufferResource> pair in source.FrameBufferRecords)
        {
            if (!primary.FrameBufferRecords.TryGetValue(pair.Key, out RenderFrameBufferResource? primaryRecord) ||
                !FrameBufferDescriptorsEquivalent(primaryRecord.Descriptor, pair.Value.Descriptor))
            {
                return false;
            }
        }

        return true;
    }

    private static bool BufferDescriptorsCoveredByPrimary(
        RenderResourceRegistry source,
        RenderResourceRegistry primary)
    {
        foreach (KeyValuePair<string, RenderBufferResource> pair in source.BufferRecords)
        {
            if (!primary.BufferRecords.TryGetValue(pair.Key, out RenderBufferResource? primaryRecord) ||
                !EqualityComparer<BufferResourceDescriptor>.Default.Equals(primaryRecord.Descriptor, pair.Value.Descriptor))
            {
                return false;
            }
        }

        return true;
    }

    private static bool FrameBufferDescriptorsEquivalent(
        FrameBufferResourceDescriptor left,
        FrameBufferResourceDescriptor right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) ||
            left.Lifetime != right.Lifetime ||
            left.SizePolicy != right.SizePolicy ||
            left.Attachments.Count != right.Attachments.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Attachments.Count; i++)
        {
            FrameBufferAttachmentDescriptor leftAttachment = left.Attachments[i];
            FrameBufferAttachmentDescriptor rightAttachment = right.Attachments[i];
            if (!string.Equals(leftAttachment.ResourceName, rightAttachment.ResourceName, StringComparison.OrdinalIgnoreCase) ||
                leftAttachment.Attachment != rightAttachment.Attachment ||
                leftAttachment.MipLevel != rightAttachment.MipLevel ||
                leftAttachment.LayerIndex != rightAttachment.LayerIndex)
            {
                return false;
            }
        }

        return true;
    }

    private static void AddRegistryDescriptors(
        RenderResourceRegistry destination,
        RenderResourceRegistry source,
        bool overwrite)
    {
        foreach (KeyValuePair<string, RenderTextureResource> pair in source.TextureRecords)
        {
            if (overwrite || !destination.TextureRecords.ContainsKey(pair.Key))
                destination.RegisterTextureDescriptor(pair.Value.Descriptor);
        }

        foreach (KeyValuePair<string, RenderFrameBufferResource> pair in source.FrameBufferRecords)
        {
            if (overwrite || !destination.FrameBufferRecords.ContainsKey(pair.Key))
                destination.RegisterFrameBufferDescriptor(pair.Value.Descriptor);
        }

        foreach (KeyValuePair<string, RenderBufferResource> pair in source.BufferRecords)
        {
            if (overwrite || !destination.BufferRecords.ContainsKey(pair.Key))
                destination.RegisterBufferDescriptor(pair.Value.Descriptor);
        }
    }

    private static IEnumerable<XRFrameBuffer> EnumerateFrameOpFrameBuffers(FrameOp op)
    {
        if (op.Target is not null)
            yield return op.Target;

        if (op is BlitOp blit)
        {
            if (blit.InFbo is not null)
                yield return blit.InFbo;
            if (blit.OutFbo is not null)
                yield return blit.OutFbo;
        }
    }

    internal static bool RequiresResourcePlannerRebuild(in FrameOpContext previous, in FrameOpContext next)
    {
        if (!ReferenceEquals(previous.PipelineInstance, next.PipelineInstance))
            return true;

        if (!ReferenceEquals(previous.ResourceRegistry, next.ResourceRegistry))
            return true;

        if (!ReferenceEquals(previous.PassMetadata, next.PassMetadata))
            return true;

        return false;
    }

    private void UpdateResourcePlannerFromContext(
        in FrameOpContext context,
        HashSet<int>? activePassIndices = null,
        HashSet<string>? activeFrameBufferNames = null,
        int activeResourceSetSignature = 0)
    {
        int activePassSetSignature = ComputeActivePassSetSignature(activePassIndices);
        ResourcePlanningInputs planningInputs = PrepareResourcePlanningInputs(
            context,
            activePassIndices,
            activePassSetSignature,
            activeFrameBufferNames,
            activeResourceSetSignature);

        if (CanReuseResourcePlannerFastPath(planningInputs.FastPathKey))
            return;

        ulong plannerSignature = ComputeResourcePlannerSignature(
            context,
            planningInputs.QueueOwnership,
            planningInputs.CompiledGraph,
            planningInputs.ActivePassMetadata);
        if (plannerSignature == _resourcePlannerSignature)
        {
            RememberResourcePlannerFastPath(planningInputs.FastPathKey);
            return;
        }

        ResourcePlannerSignatureBreakdown signatureBreakdown = ComputeResourcePlannerSignatureBreakdown(
            context,
            planningInputs.QueueOwnership,
            planningInputs.CompiledGraph,
            planningInputs.ActivePassMetadata);
        Debug.VulkanEvery(
            $"Vulkan.ResourcePlanner.SignatureChange.{context.PipelineIdentity}.{context.ViewportIdentity}",
            TimeSpan.FromSeconds(1),
            "[VulkanResourcePlanner] Signature changed. Revision={0} Old=0x{1:X16} New=0x{2:X16} ChangedFields=[{3}] OldComponents=[{4}] NewComponents=[{5}]",
            _resourcePlannerRevision,
            _resourcePlannerSignature,
            plannerSignature,
            signatureBreakdown.DescribeDelta(_resourcePlannerSignatureBreakdown),
            _resourcePlannerSignatureBreakdown,
            signatureBreakdown);

        VulkanResourcePlanner pendingPlanner = BuildResourceDescriptorPlan(context, planningInputs.ActivePassMetadata);
        PhysicalAllocationPlan allocationPlan = BuildPhysicalAllocationPlan(
            context,
            pendingPlanner,
            planningInputs.ActivePassMetadata);
        LogPhysicalAllocationPlanStatus(context, pendingPlanner, allocationPlan, planningInputs.ActivePassMetadata);

        VulkanResourceAllocator oldAllocator = _resourceAllocator;
        VulkanResourceAllocator? pendingAllocator = null;
        int retiredImageCount = 0;
        int retiredBufferCount = 0;
        if (allocationPlan.Changed)
        {
            if (!TryBuildPhysicalAllocator(
                context,
                pendingPlanner,
                allocationPlan.ExtentContext,
                planningInputs.ActivePassMetadata,
                out pendingAllocator,
                out retiredImageCount,
                out retiredBufferCount))
            {
                return;
            }
        }

        _resourcePlanner = pendingPlanner;
        if (pendingAllocator is not null)
            _resourceAllocator = pendingAllocator;

        CommitPhysicalAllocatorPlan(allocationPlan.Changed, oldAllocator, retiredImageCount, retiredBufferCount);
        RebuildRenderGraphAndBarriers(planningInputs, allocationPlan.Signature);

        _resourcePlannerSignature = plannerSignature;
        _resourceAllocationSignature = allocationPlan.Signature;
        _resourcePlannerSignatureBreakdown = signatureBreakdown;
        _resourcePlannerRevision++;
        RememberResourcePlannerFastPath(planningInputs.FastPathKey);
    }

    private ResourcePlanningInputs PrepareResourcePlanningInputs(
        in FrameOpContext context,
        HashSet<int>? activePassIndices,
        int activePassSetSignature,
        HashSet<string>? activeFrameBufferNames,
        int activeResourceSetSignature)
    {
        IReadOnlyCollection<RenderPassMetadata>? activePassMetadata = FilterActivePassMetadata(
            context.PassMetadata,
            activePassIndices,
            activePassSetSignature,
            activeFrameBufferNames,
            activeResourceSetSignature);
        VulkanCompiledRenderGraph compiledGraph = _renderGraphCompiler.Compile(activePassMetadata);
        VulkanBarrierPlanner.QueueOwnershipConfig queueOwnership = BuildQueueOwnershipConfig(activePassMetadata);
        ResourcePlannerFastPathKey fastPathKey = new(
            context.ResourceRegistry,
            context.ResourceRegistry?.DescriptorRevision ?? 0,
            activePassMetadata,
            ComputePassMetadataRevisionStamp(activePassMetadata),
            activePassSetSignature,
            activeResourceSetSignature,
            context.DisplayWidth,
            context.DisplayHeight,
            context.InternalWidth,
            context.InternalHeight,
            queueOwnership,
            SupportsTransformFeedback);

        return new ResourcePlanningInputs(activePassMetadata, compiledGraph, queueOwnership, fastPathKey);
    }

    private bool CanReuseResourcePlannerFastPath(in ResourcePlannerFastPathKey key)
        => _hasResourcePlannerFastPathKey
            && _resourcePlannerSignature != ulong.MaxValue
            && key.Matches(_resourcePlannerFastPathKey);

    private void RememberResourcePlannerFastPath(in ResourcePlannerFastPathKey key)
    {
        _resourcePlannerFastPathKey = key;
        _hasResourcePlannerFastPathKey = true;
    }

    private static VulkanResourcePlanner BuildResourceDescriptorPlan(
        in FrameOpContext context,
        IReadOnlyCollection<RenderPassMetadata>? activePassMetadata)
    {
        VulkanResourcePlanner pendingPlanner = new();
        pendingPlanner.Sync(context.ResourceRegistry);
        ValidateVulkanResourcePlanMetadata(activePassMetadata, pendingPlanner);
        return pendingPlanner;
    }

    private PhysicalAllocationPlan BuildPhysicalAllocationPlan(
        in FrameOpContext context,
        VulkanResourcePlanner pendingPlanner,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        VulkanResourceExtentContext extentContext = BuildResourceExtentContext(context);
        ulong allocationSignature = ComputeResourceAllocationSignature(
            context,
            pendingPlanner,
            passMetadata,
            extentContext,
            SupportsTransformFeedback);
        return new PhysicalAllocationPlan(
            extentContext,
            allocationSignature,
            allocationSignature != _resourceAllocationSignature);
    }

    private void LogPhysicalAllocationPlanStatus(
        in FrameOpContext context,
        VulkanResourcePlanner pendingPlanner,
        in PhysicalAllocationPlan allocationPlan,
        IReadOnlyCollection<RenderPassMetadata>? activePassMetadata)
    {
        if (allocationPlan.Changed)
        {
            ResourceAllocationSignatureBreakdown allocationBreakdown = ComputeResourceAllocationSignatureBreakdown(
                context,
                pendingPlanner,
                activePassMetadata,
                allocationPlan.ExtentContext,
                SupportsTransformFeedback);
            Debug.VulkanEvery(
                $"Vulkan.ResourcePlanner.PhysicalPlanChange.{context.PipelineIdentity}.{context.ViewportIdentity}",
                TimeSpan.FromSeconds(1),
                "[VulkanResourcePlanner] Physical resource plan changed. Revision={0} Old=0x{1:X16} New=0x{2:X16} Components=[{3}]",
                _resourcePlannerRevision,
                _resourceAllocationSignature,
                allocationPlan.Signature,
                allocationBreakdown);
            return;
        }

        Debug.VulkanEvery(
            $"Vulkan.ResourcePlanner.PhysicalPlanReuse.{context.PipelineIdentity}.{context.ViewportIdentity}",
            TimeSpan.FromSeconds(1),
            "[VulkanResourcePlanner] Reusing physical resource plan for metadata-only graph change. Revision={0} AllocationSignature=0x{1:X16}",
            _resourcePlannerRevision,
            allocationPlan.Signature);
    }

    private bool TryBuildPhysicalAllocator(
        in FrameOpContext context,
        VulkanResourcePlanner pendingPlanner,
        VulkanResourceExtentContext extentContext,
        IReadOnlyCollection<RenderPassMetadata>? activePassMetadata,
        out VulkanResourceAllocator? pendingAllocator,
        out int retiredImageCount,
        out int retiredBufferCount)
    {
        pendingAllocator = new();
        retiredImageCount = 0;
        retiredBufferCount = 0;

        try
        {
            pendingAllocator.UpdatePlan(pendingPlanner.CurrentPlan);
            pendingAllocator.RebuildPhysicalPlan(
                this,
                activePassMetadata,
                pendingPlanner,
                extentContext);
            pendingAllocator.AllocatePhysicalImages(this);
            pendingAllocator.AllocatePhysicalBuffers(this);
        }
        catch (Exception ex)
        {
            pendingAllocator.DestroyPhysicalImages(this);
            pendingAllocator.DestroyPhysicalBuffers(this);
            pendingAllocator = null;
            Debug.VulkanWarning(
                "[VulkanResourcePlanner] Pending physical resource plan failed. Keeping active plan revision={0}. Reason={1}",
                _resourcePlannerRevision,
                ex.Message);
            return false;
        }

        retiredImageCount = _resourceAllocator.EnumeratePhysicalGroups().Count(static g => g.IsAllocated);
        retiredBufferCount = _resourceAllocator.EnumeratePhysicalBufferGroups().Count(static g => g.IsAllocated);
        return true;
    }

    private void CommitPhysicalAllocatorPlan(
        bool physicalPlanChanged,
        VulkanResourceAllocator oldAllocator,
        int retiredImageCount,
        int retiredBufferCount)
    {
        if (!physicalPlanChanged)
            return;

        // Transition every newly-allocated physical image from VK_IMAGE_LAYOUT_UNDEFINED
        // to a usable initial layout so that the first render pass that references them
        // does not hit a validation error (images stuck in UNDEFINED). Depth/stencil
        // images go to DEPTH_STENCIL_ATTACHMENT_OPTIMAL; colour images go to GENERAL
        // which is compatible with attachment, sampled, and storage usage.
        TransitionNewPhysicalImagesToInitialLayout();

        if (retiredImageCount > 0 || retiredBufferCount > 0)
        {
            WaitForResourcePlanReplacementIdle(retiredImageCount, retiredBufferCount);
            ReleaseDescriptorReferencesForPhysicalResourceDestruction("ResourcePlanReplacement");
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourcePlanReplacement(retiredImageCount, retiredBufferCount);
        }

        oldAllocator.DestroyPhysicalImages(this);
        oldAllocator.DestroyPhysicalBuffers(this);
    }

    private void RebuildRenderGraphAndBarriers(
        in ResourcePlanningInputs planningInputs,
        ulong resourceAllocationSignature)
    {
        _compiledRenderGraph = planningInputs.CompiledGraph;

        BarrierPlanFastPathKey barrierKey = new(
            planningInputs.CompiledGraph,
            resourceAllocationSignature,
            planningInputs.QueueOwnership);
        if (_hasBarrierPlanFastPathKey && barrierKey.Matches(_barrierPlanFastPathKey))
            return;

        _barrierPlanner.Rebuild(
            planningInputs.ActivePassMetadata,
            _resourcePlanner,
            _resourceAllocator,
            _compiledRenderGraph.Synchronization,
            planningInputs.QueueOwnership);
        _barrierPlanFastPathKey = barrierKey;
        _hasBarrierPlanFastPathKey = true;
    }

    private IReadOnlyCollection<RenderPassMetadata>? FilterActivePassMetadata(
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        HashSet<int>? activePassIndices,
        int activePassSetSignature,
        HashSet<string>? activeFrameBufferNames,
        int activeResourceSetSignature)
    {
        if (passMetadata is null || passMetadata.Count == 0 || activePassIndices is not { Count: > 0 })
            return passMetadata;

        if (ReferenceEquals(passMetadata, _lastActiveFilterSourcePassMetadata) &&
            activePassSetSignature == _lastActiveFilterPassSetSignature &&
            activeResourceSetSignature == _lastActiveFilterResourceSetSignature)
        {
            return _lastActiveFilterResult;
        }

        List<RenderPassMetadata> filtered = new(Math.Min(passMetadata.Count, activePassIndices.Count));
        bool removedResourceUsages = false;
        foreach (RenderPassMetadata pass in passMetadata)
        {
            if (!activePassIndices.Contains(pass.PassIndex))
                continue;

            RenderPassMetadata activePass = FilterActivePassResourceUsages(
                pass,
                activePassIndices,
                activeFrameBufferNames,
                ref removedResourceUsages);
            filtered.Add(activePass);
        }

        IReadOnlyCollection<RenderPassMetadata> result;
        if (filtered.Count == passMetadata.Count && !removedResourceUsages)
        {
            result = passMetadata;
        }
        else if (filtered.Count == 0)
        {
            result = Array.Empty<RenderPassMetadata>();
        }
        else
        {
            filtered.Sort(static (left, right) => left.PassIndex.CompareTo(right.PassIndex));
            result = filtered.ToArray();
        }

        _lastActiveFilterSourcePassMetadata = passMetadata;
        _lastActiveFilterPassSetSignature = activePassSetSignature;
        _lastActiveFilterResourceSetSignature = activeResourceSetSignature;
        _lastActiveFilterResult = result;
        return result;
    }

    private static RenderPassMetadata FilterActivePassResourceUsages(
        RenderPassMetadata pass,
        HashSet<int> activePassIndices,
        HashSet<string>? activeFrameBufferNames,
        ref bool removedResourceUsages)
    {
        if (activeFrameBufferNames is not { Count: > 0 })
            return pass;

        List<RenderPassResourceUsage>? activeUsages = null;
        for (int i = 0; i < pass.ResourceUsages.Count; i++)
        {
            RenderPassResourceUsage usage = pass.ResourceUsages[i];
            if (IsInactiveFrameBufferUsage(usage, activeFrameBufferNames))
            {
                removedResourceUsages = true;
                if (activeUsages is null)
                {
                    activeUsages = new List<RenderPassResourceUsage>(pass.ResourceUsages.Count);
                    for (int previous = 0; previous < i; previous++)
                        activeUsages.Add(pass.ResourceUsages[previous]);
                }
                continue;
            }

            activeUsages?.Add(usage);
        }

        if (activeUsages is null)
            return pass;

        RenderPassMetadata filtered = new(pass.PassIndex, pass.Name, pass.Stage);
        foreach (RenderPassResourceUsage usage in activeUsages)
            filtered.AddUsage(usage);

        foreach (int dependency in pass.ExplicitDependencies)
            if (activePassIndices.Contains(dependency))
                filtered.AddDependency(dependency);

        foreach (string schema in pass.DescriptorSchemas)
            filtered.AddDescriptorSchema(schema);

        return filtered;
    }

    private static bool IsInactiveFrameBufferUsage(
        RenderPassResourceUsage usage,
        HashSet<string> activeFrameBufferNames)
    {
        if (!usage.ResourceName.StartsWith("fbo::", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] segments = usage.ResourceName.Split("::", StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return false;

        string frameBufferName = segments[1];
        return !IsVulkanExternalOutputName(frameBufferName) &&
            !activeFrameBufferNames.Contains(frameBufferName);
    }

    private static int ComputeActivePassSetSignature(HashSet<int>? activePassIndices)
    {
        if (activePassIndices is not { Count: > 0 })
            return 0;

        HashCode hash = new();
        hash.Add(activePassIndices.Count);
        long sum = 0;
        long squaredSum = 0;
        int xor = 0;
        foreach (int passIndex in activePassIndices)
        {
            sum += passIndex;
            squaredSum += (long)passIndex * passIndex;
            xor ^= HashCode.Combine(passIndex);
        }

        hash.Add(sum);
        hash.Add(squaredSum);
        hash.Add(xor);
        return hash.ToHashCode();
    }

    private static int ComputeActiveFrameBufferSetSignature(HashSet<string>? activeFrameBufferNames)
    {
        if (activeFrameBufferNames is not { Count: > 0 })
            return 0;

        HashCode hash = new();
        hash.Add(activeFrameBufferNames.Count);
        foreach (string frameBufferName in activeFrameBufferNames.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase))
            hash.Add(frameBufferName, StringComparer.OrdinalIgnoreCase);

        return hash.ToHashCode();
    }

    private void WaitForResourcePlanReplacementIdle(int imageCount, int bufferCount)
    {
        if (IsDeviceLost)
            return;

        Debug.VulkanEvery(
            "Vulkan.ResourcePlanner.PlanReplacementIdle",
            TimeSpan.FromSeconds(2),
            "[VulkanResourcePlanner] Waiting for device idle before retiring replaced physical resource plan. images={0} buffers={1}",
            imageCount,
            bufferCount);

        DeviceWaitIdle();
    }

    private static void ValidateVulkanResourcePlanMetadata(
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VulkanResourcePlanner planner,
        HashSet<int>? activePassIndices = null)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return;

        foreach (RenderPassMetadata pass in passMetadata)
        {
            if (activePassIndices is { Count: > 0 } && !activePassIndices.Contains(pass.PassIndex))
                continue;

            foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
            {
                string resourceName = usage.ResourceName;
                if (string.IsNullOrWhiteSpace(resourceName)
                    || IsVulkanExternalOutputResourceBinding(resourceName))
                {
                    continue;
                }

                if (resourceName.StartsWith("fbo::", StringComparison.OrdinalIgnoreCase))
                {
                    ValidateVulkanFrameBufferBinding(pass, usage, resourceName, planner);
                    continue;
                }

                if (resourceName.StartsWith("tex::", StringComparison.OrdinalIgnoreCase))
                {
                    string textureName = resourceName["tex::".Length..];
                    if (!string.IsNullOrWhiteSpace(textureName)
                        && !IsVulkanPlannerOptionalResource(textureName)
                        && !planner.TryGetTextureDescriptor(textureName, out _))
                    {
                        Debug.VulkanWarningEvery(
                            $"VulkanResourcePlanner.MissingTexture.{pass.PassIndex}.{textureName}",
                            TimeSpan.FromSeconds(2),
                            "[VulkanResourcePlanner] Pass '{0}' references missing declared texture '{1}'.",
                            pass.Name,
                            textureName);
                    }
                    continue;
                }

                if (resourceName.StartsWith("buf::", StringComparison.OrdinalIgnoreCase))
                {
                    string bufferName = resourceName["buf::".Length..];
                    if (!string.IsNullOrWhiteSpace(bufferName)
                        && !IsVulkanPlannerOptionalResource(bufferName)
                        && !planner.TryGetBufferDescriptor(bufferName, out _))
                    {
                        Debug.VulkanWarningEvery(
                            $"VulkanResourcePlanner.MissingBuffer.{pass.PassIndex}.{bufferName}",
                            TimeSpan.FromSeconds(2),
                            "[VulkanResourcePlanner] Pass '{0}' references missing declared buffer '{1}'.",
                            pass.Name,
                            bufferName);
                    }
                }
            }
        }
    }

    private static void ValidateVulkanFrameBufferBinding(
        RenderPassMetadata pass,
        RenderPassResourceUsage usage,
        string resourceName,
        VulkanResourcePlanner planner)
    {
        string[] segments = resourceName.Split("::", StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return;

        string frameBufferName = segments[1];
        if (IsVulkanExternalOutputName(frameBufferName) || IsVulkanPlannerOptionalResource(frameBufferName))
            return;

        string slot = segments.Length >= 3 ? segments[2] : "color";
        if (!planner.TryGetFrameBufferDescriptor(frameBufferName, out FrameBufferResourceDescriptor? descriptor)
            || descriptor is null)
        {
            Debug.VulkanWarningEvery(
                $"VulkanResourcePlanner.MissingFBO.{pass.PassIndex}.{frameBufferName}",
                TimeSpan.FromSeconds(2),
                "[VulkanResourcePlanner] Pass '{0}' references missing declared framebuffer '{1}'.",
                pass.Name,
                frameBufferName);
            return;
        }

        foreach (FrameBufferAttachmentDescriptor attachment in descriptor.Attachments)
        {
            if (!MatchesVulkanFrameBufferSlot(attachment.Attachment, slot))
                continue;

            if (!planner.TryGetTextureDescriptor(attachment.ResourceName, out _))
            {
                if (IsVulkanPlannerOptionalResource(attachment.ResourceName))
                    return;

                Debug.VulkanWarningEvery(
                    $"VulkanResourcePlanner.MissingFBOAttachment.{pass.PassIndex}.{frameBufferName}.{attachment.ResourceName}",
                    TimeSpan.FromSeconds(2),
                    "[VulkanResourcePlanner] Pass '{0}' framebuffer '{1}' references attachment '{2}' that is missing from declared textures.",
                    pass.Name,
                    frameBufferName,
                    attachment.ResourceName);
            }
            return;
        }

        Debug.VulkanWarningEvery(
            $"VulkanResourcePlanner.MissingFBOSlot.{pass.PassIndex}.{frameBufferName}.{slot}",
            TimeSpan.FromSeconds(2),
            "[VulkanResourcePlanner] Pass '{0}' framebuffer '{1}' has no attachment matching slot '{2}' for usage {3}.",
            pass.Name,
            frameBufferName,
            slot,
            usage.ResourceType);
    }

    private static bool IsVulkanExternalOutputResourceBinding(string resourceName)
    {
        if (resourceName.Equals(RenderGraphResourceNames.OutputRenderTarget, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!resourceName.StartsWith("fbo::", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] segments = resourceName.Split("::", StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 && IsVulkanExternalOutputName(segments[1]);
    }

    private static bool IsVulkanExternalOutputName(string resourceName)
        => resourceName.Equals(RenderGraphResourceNames.OutputRenderTarget, StringComparison.OrdinalIgnoreCase);

    private static bool IsVulkanPlannerOptionalResource(string resourceName)
        => VulkanPlannerOptionalResourceNames.Contains(resourceName);

    private static bool MatchesVulkanFrameBufferSlot(EFrameBufferAttachment attachment, string slot)
    {
        if (slot.StartsWith("color", StringComparison.OrdinalIgnoreCase))
        {
            if (slot.Length > 5 && int.TryParse(slot.AsSpan(5), out int colorIndex))
            {
                EFrameBufferAttachment expected = (EFrameBufferAttachment)((int)EFrameBufferAttachment.ColorAttachment0 + colorIndex);
                return attachment == expected;
            }

            return attachment is >= EFrameBufferAttachment.ColorAttachment0 and <= EFrameBufferAttachment.ColorAttachment31;
        }

        if (slot.Equals("depth", StringComparison.OrdinalIgnoreCase))
            return attachment is EFrameBufferAttachment.DepthAttachment or EFrameBufferAttachment.DepthStencilAttachment;

        if (slot.Equals("stencil", StringComparison.OrdinalIgnoreCase))
            return attachment is EFrameBufferAttachment.StencilAttachment or EFrameBufferAttachment.DepthStencilAttachment;

        return false;
    }

    private static ulong ComputeResourceAllocationSignature(
        in FrameOpContext context,
        VulkanResourcePlanner planner,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VulkanResourceExtentContext extentContext,
        bool supportsTransformFeedback)
    {
        ResourceAllocationSignatureBreakdown breakdown = ComputeResourceAllocationSignatureBreakdown(
            context,
            planner,
            passMetadata,
            extentContext,
            supportsTransformFeedback);
        HashCode hash = new();
        hash.Add(breakdown.AllocationDescriptors);
        hash.Add(breakdown.DisplayWidth);
        hash.Add(breakdown.DisplayHeight);
        hash.Add(breakdown.InternalWidth);
        hash.Add(breakdown.InternalHeight);
        hash.Add(breakdown.PhysicalUsage);
        hash.Add(breakdown.SupportsTransformFeedback);
        return unchecked((ulong)hash.ToHashCode());
    }

    private static ResourceAllocationSignatureBreakdown ComputeResourceAllocationSignatureBreakdown(
        in FrameOpContext context,
        VulkanResourcePlanner planner,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VulkanResourceExtentContext extentContext,
        bool supportsTransformFeedback)
        => new(
            ComputePhysicalResourceDescriptorSignature(context.ResourceRegistry),
            extentContext.WindowWidth,
            extentContext.WindowHeight,
            extentContext.InternalWidth,
            extentContext.InternalHeight,
            VulkanResourceAllocator.ComputePhysicalPlanUsageSignature(planner, passMetadata),
            supportsTransformFeedback);

    private static ulong ComputeResourcePlannerSignature(
        in FrameOpContext context,
        in VulkanBarrierPlanner.QueueOwnershipConfig queueOwnership,
        VulkanCompiledRenderGraph compiledGraph,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        HashCode hash = new();
        hash.Add(ComputeResourceRegistrySignature(context.ResourceRegistry));

        hash.Add(context.DisplayWidth);
        hash.Add(context.DisplayHeight);
        hash.Add(context.InternalWidth);
        hash.Add(context.InternalHeight);

        hash.Add(ComputePassMetadataSignature(passMetadata));

        hash.Add(compiledGraph.Batches.Count);
        foreach (VulkanCompiledPassBatch batch in compiledGraph.Batches)
        {
            hash.Add(batch.BatchIndex);
            hash.Add((int)batch.Stage);
            hash.Add(batch.AttachmentSignature, StringComparer.Ordinal);
            hash.Add(batch.PassIndices.Count);
            for (int i = 0; i < batch.PassIndices.Count; i++)
                hash.Add(batch.PassIndices[i]);
        }

        hash.Add(compiledGraph.Synchronization.Edges.Count);
        foreach (RenderGraphSynchronizationEdge edge in compiledGraph.Synchronization.Edges)
        {
            hash.Add(edge.ProducerPassIndex);
            hash.Add(edge.ConsumerPassIndex);
            hash.Add(edge.ResourceName, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)edge.ResourceType);
            AddSubresourceRangeToHash(ref hash, edge.SubresourceRange);
            hash.Add((int)edge.ProducerState.StageMask);
            hash.Add((int)edge.ProducerState.AccessMask);
            hash.Add((int)(edge.ProducerState.Layout ?? RenderGraphImageLayout.Undefined));
            hash.Add((int)edge.ConsumerState.StageMask);
            hash.Add((int)edge.ConsumerState.AccessMask);
            hash.Add((int)(edge.ConsumerState.Layout ?? RenderGraphImageLayout.Undefined));
            hash.Add(edge.DependencyOnly);
        }

        hash.Add(queueOwnership.GraphicsQueueFamilyIndex);
        hash.Add(queueOwnership.ComputeQueueFamilyIndex ?? queueOwnership.GraphicsQueueFamilyIndex);
        hash.Add(queueOwnership.TransferQueueFamilyIndex ?? queueOwnership.GraphicsQueueFamilyIndex);

        return unchecked((ulong)hash.ToHashCode());
    }

    private static ResourcePlannerSignatureBreakdown ComputeResourcePlannerSignatureBreakdown(
        in FrameOpContext context,
        in VulkanBarrierPlanner.QueueOwnershipConfig queueOwnership,
        VulkanCompiledRenderGraph compiledGraph,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
        => new(
            ComputeResourceRegistrySignature(context.ResourceRegistry),
            context.DisplayWidth,
            context.DisplayHeight,
            context.InternalWidth,
            context.InternalHeight,
            ComputePassMetadataSignature(passMetadata),
            ComputeCompiledGraphBatchSignature(compiledGraph),
            ComputeCompiledGraphEdgeSignature(compiledGraph),
            queueOwnership.GraphicsQueueFamilyIndex,
            queueOwnership.ComputeQueueFamilyIndex ?? queueOwnership.GraphicsQueueFamilyIndex,
            queueOwnership.TransferQueueFamilyIndex ?? queueOwnership.GraphicsQueueFamilyIndex);

    private static int ComputeCompiledGraphBatchSignature(VulkanCompiledRenderGraph compiledGraph)
    {
        HashCode hash = new();
        hash.Add(compiledGraph.Batches.Count);
        foreach (VulkanCompiledPassBatch batch in compiledGraph.Batches)
        {
            hash.Add(batch.BatchIndex);
            hash.Add((int)batch.Stage);
            hash.Add(batch.AttachmentSignature, StringComparer.Ordinal);
            hash.Add(batch.PassIndices.Count);
            for (int i = 0; i < batch.PassIndices.Count; i++)
                hash.Add(batch.PassIndices[i]);
        }

        return hash.ToHashCode();
    }

    private static int ComputeCompiledGraphEdgeSignature(VulkanCompiledRenderGraph compiledGraph)
    {
        HashCode hash = new();
        hash.Add(compiledGraph.Synchronization.Edges.Count);
        foreach (RenderGraphSynchronizationEdge edge in compiledGraph.Synchronization.Edges)
        {
            hash.Add(edge.ProducerPassIndex);
            hash.Add(edge.ConsumerPassIndex);
            hash.Add(edge.ResourceName, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)edge.ResourceType);
            AddSubresourceRangeToHash(ref hash, edge.SubresourceRange);
            hash.Add((int)edge.ProducerState.StageMask);
            hash.Add((int)edge.ProducerState.AccessMask);
            hash.Add((int)(edge.ProducerState.Layout ?? RenderGraphImageLayout.Undefined));
            hash.Add((int)edge.ConsumerState.StageMask);
            hash.Add((int)edge.ConsumerState.AccessMask);
            hash.Add((int)(edge.ConsumerState.Layout ?? RenderGraphImageLayout.Undefined));
            hash.Add(edge.DependencyOnly);
        }

        return hash.ToHashCode();
    }

    private static int ComputeResourceRegistrySignature(RenderResourceRegistry? registry)
        => registry?.DescriptorSignature ?? 0;

    private static int ComputePhysicalResourceDescriptorSignature(RenderResourceRegistry? registry)
    {
        if (registry is null)
            return 0;

        HashCode hash = new();

        foreach (KeyValuePair<string, RenderTextureResource> pair in registry.TextureRecords.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            TextureResourceDescriptor descriptor = pair.Value.Descriptor;
            hash.Add(pair.Key, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)descriptor.Lifetime);
            hash.Add((int)descriptor.SizePolicy.SizeClass);
            hash.Add(descriptor.SizePolicy.ScaleX);
            hash.Add(descriptor.SizePolicy.ScaleY);
            hash.Add(descriptor.SizePolicy.Width);
            hash.Add(descriptor.SizePolicy.Height);
            hash.Add(descriptor.FormatLabel, StringComparer.OrdinalIgnoreCase);
            hash.Add(descriptor.ArrayLayers);
            hash.Add(descriptor.StereoCompatible);
            hash.Add(descriptor.SupportsAliasing);
            hash.Add(descriptor.RequiresStorageUsage);
            hash.Add((int)descriptor.Kind);
            hash.Add((int)descriptor.Usage);
            hash.Add(descriptor.InternalFormat.HasValue ? (int)descriptor.InternalFormat.Value : -1);
            hash.Add(descriptor.PixelFormat.HasValue ? (int)descriptor.PixelFormat.Value : -1);
            hash.Add(descriptor.PixelType.HasValue ? (int)descriptor.PixelType.Value : -1);
            hash.Add(descriptor.SizedInternalFormat.HasValue ? (int)descriptor.SizedInternalFormat.Value : -1);
            hash.Add(descriptor.Samples);
            hash.Add(descriptor.MipPolicy.BaseMipLevel);
            hash.Add(descriptor.MipPolicy.MipLevelCount);
            hash.Add(descriptor.MipPolicy.AutoGenerateMipmaps);
            hash.Add(descriptor.MipPolicy.RequireImmutableStorage);
            hash.Add(descriptor.SourceTextureName, StringComparer.OrdinalIgnoreCase);
            hash.Add(descriptor.BaseMipLevel);
            hash.Add(descriptor.MipLevelCount);
            hash.Add(descriptor.BaseLayer);
            hash.Add(descriptor.LayerCount);
            hash.Add((int)descriptor.DepthStencilAspect);
            hash.Add(descriptor.ArrayTarget);
            hash.Add(descriptor.Multisample);
        }

        foreach (KeyValuePair<string, RenderBufferResource> pair in registry.BufferRecords.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            BufferResourceDescriptor descriptor = pair.Value.Descriptor;
            hash.Add(pair.Key, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)descriptor.Lifetime);
            hash.Add(descriptor.SizeInBytes);
            hash.Add((int)descriptor.Target);
            hash.Add((int)descriptor.Usage);
            hash.Add(descriptor.SupportsAliasing);
            hash.Add(descriptor.ElementStride);
            hash.Add(descriptor.ElementCount);
            hash.Add((int)descriptor.AccessPattern);
        }

        return hash.ToHashCode();
    }

    private static int ComputePassMetadataSignature(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return 0;

        HashCode hash = new();
        hash.Add(passMetadata.Count);

        foreach (RenderPassMetadata pass in passMetadata)
        {
            hash.Add(pass.PassIndex);
            hash.Add((int)pass.Stage);
            hash.Add(pass.Name, StringComparer.Ordinal);
            hash.Add(pass.Revision);

            foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
            {
                hash.Add(usage.ResourceName, StringComparer.Ordinal);
                hash.Add((int)usage.ResourceType);
                hash.Add((int)usage.Access);
                hash.Add((int)usage.LoadOp);
                hash.Add((int)usage.StoreOp);
                AddSubresourceRangeToHash(ref hash, usage.SubresourceRange);
            }

            foreach (int dependency in pass.ExplicitDependencies)
                hash.Add(dependency);

            foreach (string schema in pass.DescriptorSchemas)
                hash.Add(schema, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    private static int ComputePassMetadataRevisionStamp(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return 0;

        HashCode hash = new();
        hash.Add(passMetadata.Count);
        foreach (RenderPassMetadata pass in passMetadata)
        {
            hash.Add(pass.PassIndex);
            hash.Add(pass.Revision);
        }

        return hash.ToHashCode();
    }

    private int ComputeResourcePlanningSignature(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        HashCode hash = new();
        hash.Add(swapChainExtent.Width);
        hash.Add(swapChainExtent.Height);

        foreach (VulkanAllocationRequest request in _resourcePlanner.CurrentPlan.AllTextures())
        {
            hash.Add(request.Name, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)request.Lifetime);
            hash.Add(request.AliasKey);
        }

        foreach (VulkanBufferAllocationRequest request in _resourcePlanner.CurrentPlan.AllBuffers())
        {
            hash.Add(request.Name, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)request.Lifetime);
            hash.Add(request.AliasKey);
        }

        if (passMetadata is not null)
        {
            hash.Add(passMetadata.Count);
            foreach (RenderPassMetadata pass in passMetadata.OrderBy(static p => p.PassIndex))
            {
                hash.Add(pass.PassIndex);
                hash.Add((int)pass.Stage);
                hash.Add(pass.Name, StringComparer.Ordinal);

                foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
                {
                    hash.Add(usage.ResourceName, StringComparer.Ordinal);
                    hash.Add((int)usage.ResourceType);
                    hash.Add((int)usage.Access);
                    hash.Add((int)usage.LoadOp);
                    hash.Add((int)usage.StoreOp);
                    AddSubresourceRangeToHash(ref hash, usage.SubresourceRange);
                }
            }
        }

        return hash.ToHashCode();
    }

    private static void AddSubresourceRangeToHash(ref HashCode hash, RenderGraphSubresourceRange range)
    {
        hash.Add(range.BaseMipLevel);
        hash.Add(range.MipLevelCount);
        hash.Add(range.BaseArrayLayer);
        hash.Add(range.ArrayLayerCount);
    }

    private VulkanBarrierPlanner.QueueOwnershipConfig BuildQueueOwnershipConfig(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        QueueFamilyIndices familyIndices = FamilyQueueIndices;
        uint graphicsFamily = familyIndices.GraphicsFamilyIndex ?? 0u;
        uint candidateComputeFamily = familyIndices.ComputeFamilyIndex ?? graphicsFamily;
        uint candidateTransferFamily = familyIndices.TransferFamilyIndex ?? candidateComputeFamily;

        EVulkanGpuDrivenProfile profile = VulkanFeatureProfile.ActiveProfile;
        QueueOverlapMetrics metrics = CaptureQueueOverlapMetrics(passMetadata);

        bool promotedMode;
        bool demotedMode;
        EVulkanQueueOverlapMode overlapMode = ResolveQueueOverlapMode(profile, metrics, out promotedMode, out demotedMode);

        bool useComputeOwnership =
            overlapMode is EVulkanQueueOverlapMode.GraphicsCompute or EVulkanQueueOverlapMode.GraphicsComputeTransfer &&
            candidateComputeFamily != graphicsFamily &&
            metrics.ComputePassCount >= 2;

        bool useTransferOwnership =
            overlapMode == EVulkanQueueOverlapMode.GraphicsComputeTransfer &&
            candidateTransferFamily != graphicsFamily &&
            candidateTransferFamily != candidateComputeFamily &&
            metrics.TransferUsageCount >= 4;

        uint computeFamily = useComputeOwnership ? candidateComputeFamily : graphicsFamily;
        uint transferFamily = useTransferOwnership ? candidateTransferFamily : computeFamily;

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanQueueOverlapWindow(
            metrics.OverlapCandidatePassCount,
            metrics.TransferCost,
            metrics.FrameDelta,
            promotedMode,
            demotedMode);

        _lastResolvedQueueOverlapMode = overlapMode;

        Debug.VulkanEvery(
            "Vulkan.QueueOwnership.Policy",
            TimeSpan.FromSeconds(2),
            "Queue ownership policy: profile={0} mode={1} gfx={2} compute={3} transfer={4} useCompute={5} useTransfer={6} computePasses={7} overlapCandidates={8} transferUsages={9} transferCost={10} qTransfers={11} stageFlushes={12} frameDeltaMs={13:F3}",
            profile,
            overlapMode,
            graphicsFamily,
            computeFamily,
            transferFamily,
            useComputeOwnership,
            useTransferOwnership,
            metrics.ComputePassCount,
            metrics.OverlapCandidatePassCount,
            metrics.TransferUsageCount,
            metrics.TransferCost,
            metrics.QueueOwnershipTransfers,
            metrics.BarrierStageFlushes,
            metrics.FrameDelta.TotalMilliseconds);

        return new VulkanBarrierPlanner.QueueOwnershipConfig(
            graphicsFamily,
            computeFamily,
            transferFamily);
    }

    private QueueOverlapMetrics CaptureQueueOverlapMetrics(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        bool hasMetadata = passMetadata is { Count: > 0 };
        int computePassCount = hasMetadata ? passMetadata!.Count(static p => p.Stage == ERenderGraphPassStage.Compute) : 0;
        int transferUsageCount = hasMetadata
            ? passMetadata!.Sum(static p => p.ResourceUsages.Count(static u => u.ResourceType is ERenderPassResourceType.TransferSource or ERenderPassResourceType.TransferDestination))
            : 0;
        int overlapCandidatePassCount = hasMetadata
            ? passMetadata!.Count(IsQueueOverlapCandidatePass)
            : 0;

        int queueOwnershipTransfers = RuntimeEngine.Rendering.Stats.Vulkan.VulkanQueueOwnershipTransfers;
        int stageFlushes = RuntimeEngine.Rendering.Stats.Vulkan.VulkanBarrierStageFlushes;
        int transferCost = transferUsageCount + queueOwnershipTransfers + stageFlushes;

        TimeSpan frameDelta = TimeSpan.Zero;
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        if (_lastQueueOverlapSampleFrameId != frameId)
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastQueueOverlapSampleTimestamp != 0)
            {
                long elapsedTicks = now - _lastQueueOverlapSampleTimestamp;
                if (elapsedTicks > 0)
                    frameDelta = TimeSpan.FromSeconds(elapsedTicks / (double)Stopwatch.Frequency);
            }

            _lastQueueOverlapSampleTimestamp = now;
            _lastQueueOverlapSampleFrameId = frameId;
        }

        return new QueueOverlapMetrics(
            computePassCount,
            transferUsageCount,
            overlapCandidatePassCount,
            transferCost,
            queueOwnershipTransfers,
            stageFlushes,
            frameDelta);
    }

    private static bool IsQueueOverlapCandidatePass(RenderPassMetadata pass)
    {
        if (pass.Stage != ERenderGraphPassStage.Compute)
            return false;

        string name = pass.Name ?? string.Empty;
        return name.Contains("hiz", StringComparison.OrdinalIgnoreCase)
            || name.Contains("occlusion", StringComparison.OrdinalIgnoreCase)
            || name.Contains("indirect", StringComparison.OrdinalIgnoreCase);
    }

    private EVulkanQueueOverlapMode ResolveQueueOverlapMode(EVulkanGpuDrivenProfile profile, in QueueOverlapMetrics metrics, out bool promotedMode, out bool demotedMode)
    {
        promotedMode = false;
        demotedMode = false;

        EVulkanQueueOverlapMode requestedMode = RuntimeEngine.EffectiveSettings.VulkanQueueOverlapMode;
        if (requestedMode != EVulkanQueueOverlapMode.Auto)
        {
            _autoQueueOverlapMode = requestedMode;
            _queueOverlapPromotionStabilityFrames = 0;
            _queueOverlapFramesInMode = 0;
            _queueOverlapModeStartFrameDeltaMs = -1.0;
            return requestedMode;
        }

        if (!VulkanFeatureProfile.IsActive)
        {
            _autoQueueOverlapMode = EVulkanQueueOverlapMode.GraphicsOnly;
            return _autoQueueOverlapMode;
        }

        bool hasFrameDelta = metrics.FrameDelta.Ticks > 0;
        if (hasFrameDelta)
        {
            double frameDeltaMs = metrics.FrameDelta.TotalMilliseconds;
            _queueOverlapFrameDeltaEmaMs = _queueOverlapFrameDeltaEmaMs < 0.0
                ? frameDeltaMs
                : (_queueOverlapFrameDeltaEmaMs * 0.85) + (frameDeltaMs * 0.15);
        }

        bool hasComputeCandidates = metrics.ComputePassCount >= 1;
        bool hasTransferCandidates = metrics.TransferUsageCount >= 2;

        EVulkanQueueOverlapMode desiredMode = profile switch
        {
            EVulkanGpuDrivenProfile.Diagnostics when hasComputeCandidates && hasTransferCandidates => EVulkanQueueOverlapMode.GraphicsComputeTransfer,
            EVulkanGpuDrivenProfile.Diagnostics when hasComputeCandidates => EVulkanQueueOverlapMode.GraphicsCompute,
            EVulkanGpuDrivenProfile.DevParity when hasComputeCandidates => EVulkanQueueOverlapMode.GraphicsCompute,
            _ => EVulkanQueueOverlapMode.GraphicsOnly,
        };

        _queueOverlapFramesInMode++;
        if (_queueOverlapModeStartFrameDeltaMs < 0.0 && hasFrameDelta)
            _queueOverlapModeStartFrameDeltaMs = metrics.FrameDelta.TotalMilliseconds;

        bool transferCostHealthy = metrics.TransferCost <= 1024;
        bool frameDeltaHealthy = _queueOverlapFrameDeltaEmaMs < 0.0 || _queueOverlapFrameDeltaEmaMs <= 40.0;

        if (desiredMode > _autoQueueOverlapMode && transferCostHealthy && frameDeltaHealthy)
        {
            _queueOverlapPromotionStabilityFrames++;
            int threshold = _autoQueueOverlapMode == EVulkanQueueOverlapMode.GraphicsOnly ? 8 : 16;
            if (_queueOverlapPromotionStabilityFrames >= threshold)
            {
                _autoQueueOverlapMode = _autoQueueOverlapMode == EVulkanQueueOverlapMode.GraphicsOnly
                    ? EVulkanQueueOverlapMode.GraphicsCompute
                    : EVulkanQueueOverlapMode.GraphicsComputeTransfer;

                _queueOverlapPromotionStabilityFrames = 0;
                _queueOverlapFramesInMode = 0;
                _queueOverlapModeStartFrameDeltaMs = _queueOverlapFrameDeltaEmaMs;
                promotedMode = true;
            }
        }
        else
        {
            _queueOverlapPromotionStabilityFrames = 0;
        }

        bool frameRegressed = hasFrameDelta && _queueOverlapModeStartFrameDeltaMs > 0.0 &&
            metrics.FrameDelta.TotalMilliseconds > _queueOverlapModeStartFrameDeltaMs * 1.15;
        bool queueCostTooHigh = metrics.QueueOwnershipTransfers > 256 || metrics.BarrierStageFlushes > 768;

        if (_autoQueueOverlapMode > EVulkanQueueOverlapMode.GraphicsOnly && _queueOverlapFramesInMode >= 12 && (frameRegressed || queueCostTooHigh))
        {
            _autoQueueOverlapMode = _autoQueueOverlapMode == EVulkanQueueOverlapMode.GraphicsComputeTransfer
                ? EVulkanQueueOverlapMode.GraphicsCompute
                : EVulkanQueueOverlapMode.GraphicsOnly;

            _queueOverlapPromotionStabilityFrames = 0;
            _queueOverlapFramesInMode = 0;
            _queueOverlapModeStartFrameDeltaMs = _queueOverlapFrameDeltaEmaMs;
            demotedMode = true;
        }

        return _autoQueueOverlapMode;
    }

    /// <summary>
    /// Transitions every physical-group image whose <see cref="VulkanPhysicalImageGroup.LastKnownLayout"/>
    /// is <see cref="ImageLayout.Undefined"/> to a usable initial layout.
    /// </summary>
    private void TransitionNewPhysicalImagesToInitialLayout()
    {
        List<(Image image, ImageLayout target, ImageAspectFlags aspect, uint mipLevels, uint layers)>? overflow = null;

        foreach (VulkanPhysicalImageGroup group in _resourceAllocator.EnumeratePhysicalGroups())
        {
            if (!group.IsAllocated || group.LastKnownLayout != ImageLayout.Undefined)
                continue;

            bool isDepth = VulkanResourceAllocator.IsDepthStencilFormat(group.Format);
            ImageLayout targetLayout = ResolveInitialPhysicalGroupLayout(group.Usage, isDepth);
            ImageAspectFlags aspect = isDepth
                ? ImageAspectFlags.DepthBit | (HasStencilComponent(group.Format) ? ImageAspectFlags.StencilBit : 0)
                : ImageAspectFlags.ColorBit;

            overflow ??= new();
            overflow.Add((
                group.Image,
                targetLayout,
                aspect,
                Math.Max(1u, group.MipLevels),
                Math.Max(1u, group.Template.Layers)));
            group.LastKnownLayout = targetLayout;
        }

        if (overflow is null || overflow.Count == 0)
            return;

        using var scope = NewCommandScope();

        foreach ((Image image, ImageLayout target, ImageAspectFlags aspect, uint mipLevels, uint layers) in overflow)
        {
            ImageMemoryBarrier barrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = ImageLayout.Undefined,
                NewLayout = target,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = aspect,
                    BaseMipLevel = 0,
                    LevelCount = mipLevels,
                    BaseArrayLayer = 0,
                    LayerCount = layers,
                },
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            };

            // Narrow dst stage based on target layout instead of AllCommandsBit.
            PipelineStageFlags dstStage = target switch
            {
                ImageLayout.DepthStencilAttachmentOptimal =>
                    PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                ImageLayout.ColorAttachmentOptimal =>
                    PipelineStageFlags.ColorAttachmentOutputBit,
                ImageLayout.General =>
                    PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit,
                ImageLayout.ShaderReadOnlyOptimal =>
                    PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                ImageLayout.TransferDstOptimal or ImageLayout.TransferSrcOptimal =>
                    PipelineStageFlags.TransferBit,
                _ => PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit,
            };

            CmdPipelineBarrierTracked(
                scope.CommandBuffer,
                PipelineStageFlags.TopOfPipeBit,
                dstStage,
                DependencyFlags.None,
                0, null, 0, null,
                1, &barrier);
        }
    }

    private static ImageLayout ResolveInitialPhysicalGroupLayout(ImageUsageFlags usage, bool isDepth)
    {
        bool colorAttachment = (usage & ImageUsageFlags.ColorAttachmentBit) != 0;
        bool sampled = (usage & (ImageUsageFlags.SampledBit | ImageUsageFlags.InputAttachmentBit)) != 0;
        bool storage = (usage & ImageUsageFlags.StorageBit) != 0;

        // Images that are used as storage (e.g. compute write targets) must be
        // accessible in GENERAL layout.  VK_IMAGE_LAYOUT_GENERAL is compatible
        // with color-attachment, sampled, and storage operations, so choosing it
        // here avoids the first-frame mismatch where a descriptor is written with
        // GENERAL (for StorageImage) but the image is still in
        // COLOR_ATTACHMENT_OPTIMAL.
        if (storage)
            return ImageLayout.General;

        // Prefer descriptor-compatible read-only layouts for sampled render targets.
        // Dynamic-rendering begin and render-graph pass barriers transition them into
        // attachment layouts at the actual write site. Starting sampled images in an
        // attachment layout made CPU tracking disagree with descriptor/final layouts
        // after plan rebuilds and window resizes.
        if (sampled)
            return isDepth
                ? ImageLayout.DepthStencilReadOnlyOptimal
                : ImageLayout.ShaderReadOnlyOptimal;

        if (isDepth)
            return ImageLayout.DepthStencilAttachmentOptimal;

        if (colorAttachment)
            return ImageLayout.ColorAttachmentOptimal;

        if ((usage & ImageUsageFlags.TransferDstBit) != 0)
            return ImageLayout.TransferDstOptimal;

        if ((usage & ImageUsageFlags.TransferSrcBit) != 0)
            return ImageLayout.TransferSrcOptimal;

        return ImageLayout.General;
    }

    private static bool HasStencilComponent(Format format)
        => format is Format.D24UnormS8Uint or Format.D32SfloatS8Uint or Format.D16UnormS8Uint;

    internal void AllocatePhysicalImage(VulkanPhysicalImageGroup group, ref Image image, ref DeviceMemory memory)
    {
        if (image.Handle != 0)
            return;

        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            Flags = 0, // TODO: add alias bit when Silk.NET exposes VK_IMAGE_CREATE_ALIAS_BIT
            ImageType = ImageType.Type2D,
            Extent = group.ResolvedExtent,
            MipLevels = Math.Max(1u, group.MipLevels),
            ArrayLayers = Math.Max(group.Template.Layers, 1u),
            Format = group.Format,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = group.Usage,
            Samples = group.Samples,
            SharingMode = SharingMode.Exclusive,
        };

        fixed (Image* imagePtr = &image)
        {
            Result result = Api!.CreateImage(device, ref imageInfo, null, imagePtr);
            if (result != Result.Success)
                throw new Exception($"Failed to create Vulkan image for resource group '{group.Key}'. Result={result}.");
        }

        Debug.VulkanEvery(
            $"Vulkan.PhysicalImage.Alloc.{group.Key}",
            TimeSpan.FromSeconds(2),
            "[Vulkan] Physical image allocated: resource='{0}' handle=0x{1:X} format={2} usage={3} extent={4}x{5} layers={6} mips={7} samples={8}",
            group.Key,
            image.Handle,
            group.Format,
            group.Usage,
            group.ResolvedExtent.Width,
            group.ResolvedExtent.Height,
            Math.Max(group.Template.Layers, 1u),
            Math.Max(1u, group.MipLevels),
            group.Samples);

        try
        {
            VulkanMemoryAllocation allocation = AllocateImageMemoryWithFallback(image, MemoryPropertyFlags.DeviceLocalBit);
            _imageAllocations[image.Handle] = allocation;
            memory = allocation.Memory;

            Result bindResult = Api!.BindImageMemory(device, image, memory, allocation.Offset);
            if (bindResult != Result.Success)
            {
                _imageAllocations.TryRemove(image.Handle, out _);
                FreeMemoryAllocation(allocation);
                memory = default;
                throw new Exception($"Failed to bind device memory for Vulkan image group '{group.Key}'. Result={bindResult}.");
            }
        }
        catch
        {
            if (memory.Handle != 0)
            {
                if (_imageAllocations.TryRemove(image.Handle, out VulkanMemoryAllocation fallbackAlloc))
                    FreeMemoryAllocation(fallbackAlloc);
                else
                    Api!.FreeMemory(device, memory, null);
                memory = default;
            }

            if (image.Handle != 0)
            {
                Api!.DestroyImage(device, image, null);
                image = default;
            }

            throw;
        }
    }

    internal void DestroyPhysicalImage(ref Image image, ref DeviceMemory memory)
    {
        // Defer destruction — the image may still be referenced by in-flight
        // command buffers from other frame slots.  The retirement queue ensures
        // the handles are destroyed only after the current slot's timeline fence
        // signals (which is after all earlier submissions have completed).
        RetireImageResources(new RetiredImageResources(
            image, memory,
            PrimaryView: default,
            AttachmentViews: [],
            Sampler: default,
            AllocatedVRAMBytes: 0));

        image = default;
        memory = default;
    }

    internal void AllocatePhysicalBuffer(VulkanPhysicalBufferGroup group, ref Buffer buffer, ref DeviceMemory memory)
    {
        if (buffer.Handle != 0)
            return;

        BufferCreateInfo bufferInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = Math.Max(group.SizeInBytes, 1UL),
            Usage = group.Usage,
            SharingMode = SharingMode.Exclusive
        };

        fixed (Buffer* bufferPtr = &buffer)
        {
            Result createResult = Api!.CreateBuffer(device, ref bufferInfo, null, bufferPtr);
            if (createResult != Result.Success)
                throw new Exception($"Failed to create Vulkan buffer for resource group '{group.Key}'. Result={createResult}.");
        }

        try
        {
            VulkanMemoryAllocation allocation = AllocateBufferMemoryWithFallback(buffer, MemoryPropertyFlags.DeviceLocalBit);
            _bufferAllocations[buffer.Handle] = allocation;
            memory = allocation.Memory;

            Result bindResult = Api!.BindBufferMemory(device, buffer, memory, allocation.Offset);
            if (bindResult != Result.Success)
            {
                _bufferAllocations.TryRemove(buffer.Handle, out _);
                FreeMemoryAllocation(allocation);
                memory = default;
                throw new Exception($"Failed to bind device memory for Vulkan buffer group '{group.Key}'. Result={bindResult}.");
            }
        }
        catch
        {
            if (memory.Handle != 0)
            {
                if (_bufferAllocations.TryRemove(buffer.Handle, out VulkanMemoryAllocation fallbackAlloc))
                    FreeMemoryAllocation(fallbackAlloc);
                else
                    Api!.FreeMemory(device, memory, null);
                memory = default;
            }

            if (buffer.Handle != 0)
            {
                Api!.DestroyBuffer(device, buffer, null);
                buffer = default;
            }

            throw;
        }
    }

    internal void DestroyPhysicalBuffer(ref Buffer buffer, ref DeviceMemory memory)
    {
        RetireBuffer(buffer, memory);
        buffer = default;
        memory = default;
    }

    public bool TryGetPhysicalImage(string resourceName, out Image image)
        => _resourceAllocator.TryGetImage(resourceName, out image);

    public bool TryGetPhysicalBuffer(string resourceName, out Buffer buffer, out ulong size)
        => _resourceAllocator.TryGetBuffer(resourceName, out buffer, out size);

    private void MarkCommandBuffersDirty([CallerMemberName] string? reason = null)
    {
        if (_commandBufferDirtyFlags is null)
            return;

        for (int i = 0; i < _commandBufferDirtyFlags.Length; i++)
            _commandBufferDirtyFlags[i] = true;
        MarkCommandBufferVariantsDirty();

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandBuffersDirty(reason);
        TrackCommandBufferDirtyReason(reason, _commandBufferDirtyFlags.Length);
    }

    private void TrackCommandBufferDirtyReason(string? reason, int swapchainImageCount)
    {
        string key = string.IsNullOrWhiteSpace(reason) ? "<unknown>" : reason;
        string? summary = null;
        lock (_commandBufferDirtyReasonLock)
        {
            _commandBufferDirtyReasons.TryGetValue(key, out int count);
            _commandBufferDirtyReasons[key] = count + 1;

            long now = Stopwatch.GetTimestamp();
            if (_lastCommandBufferDirtyReasonLogTimestamp == 0)
            {
                _lastCommandBufferDirtyReasonLogTimestamp = now;
                return;
            }

            if (Stopwatch.GetElapsedTime(_lastCommandBufferDirtyReasonLogTimestamp, now) < TimeSpan.FromSeconds(1))
                return;

            StringBuilder builder = new();
            foreach (KeyValuePair<string, int> pair in _commandBufferDirtyReasons.OrderByDescending(static p => p.Value))
            {
                if (builder.Length > 0)
                    builder.Append(", ");

                builder.Append(pair.Key).Append('=').Append(pair.Value);
            }

            summary = builder.ToString();
            _commandBufferDirtyReasons.Clear();
            _lastCommandBufferDirtyReasonLogTimestamp = now;
        }

        Debug.Vulkan(
            "[Vulkan] Command buffers marked dirty over the last second. SwapchainImages={0} Reasons={1}",
            swapchainImageCount,
            summary);
    }

    internal override void NotifyRenderResourcesChanged()
        => MarkCommandBuffersDirty();

    internal int EnsureValidPassIndex(
        int passIndex,
        string opName,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata = null)
    {
        passMetadata ??= RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Pipeline?.PassMetadata;

        // Short-circuit: well-known EDefaultRenderPass values are always valid.
        // Metadata may lag behind runtime enqueues (conditional pipeline paths,
        // hot-reload) — accept standard passes without warning.
        if (passIndex != int.MinValue && Enum.IsDefined(typeof(EDefaultRenderPass), passIndex))
            return passIndex;

        bool hasMetadata = passMetadata is { Count: > 0 };
        bool passDefinedInMetadata = hasMetadata && passMetadata!.Any(m => m.PassIndex == passIndex);

        if (passIndex != int.MinValue && (!hasMetadata || passDefinedInMetadata))
            return passIndex;

        int currentPassIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
        if (passIndex != int.MinValue && passIndex == currentPassIndex)
            return passIndex;

        if (passIndex == int.MinValue)
        {
            bool currentPassDefined = currentPassIndex != int.MinValue &&
                (!hasMetadata || passMetadata!.Any(m => m.PassIndex == currentPassIndex));

            if (currentPassDefined)
                return currentPassIndex;

            if (hasMetadata)
            {
                const int preRenderPass = (int)EDefaultRenderPass.PreRender;
                if (passMetadata!.Any(m => m.PassIndex == preRenderPass))
                    return preRenderPass;
            }
        }

        int fallback = ResolveFallbackPassIndex(opName, passMetadata);

        string reason = passIndex == int.MinValue
            ? "invalid sentinel value"
            : $"pass {passIndex} is missing from metadata";

        int? firstKnownBarrierPass = _barrierPlanner.GetFirstKnownPassIndex();

        Debug.VulkanWarningEvery(
            $"Vulkan.InvalidPass.{opName}.{passIndex}",
            TimeSpan.FromSeconds(1),
            "[Vulkan] '{0}' emitted with invalid render-graph pass index ({1}). Falling back to pass {2}. " +
            "MetadataCount={3} BarrierPlannerFirstPass={4} CurrentPipeline={5}",
            opName,
            reason,
            fallback,
            passMetadata?.Count ?? -1,
            firstKnownBarrierPass?.ToString() ?? "none",
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.GetType().Name ?? "null");

        return fallback;
    }

    private static int ResolveFallbackPassIndex(string opName, IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return int.MinValue;

        ERenderGraphPassStage? preferredStage = ResolvePreferredFallbackStage(opName, passMetadata);
        if (preferredStage.HasValue)
        {
            RenderPassMetadata? preferredPass = passMetadata
                .Where(m => m.Stage == preferredStage.Value)
                .OrderBy(m => m.PassIndex)
                .FirstOrDefault();

            if (preferredPass is not null)
                return preferredPass.PassIndex;
        }

        return passMetadata.OrderBy(m => m.PassIndex).First().PassIndex;
    }

    private static ERenderGraphPassStage? ResolvePreferredFallbackStage(string opName, IReadOnlyCollection<RenderPassMetadata> passMetadata)
    {
        if (opName.Contains("Compute", StringComparison.OrdinalIgnoreCase))
            return ERenderGraphPassStage.Compute;

        if (opName.Contains("Blit", StringComparison.OrdinalIgnoreCase))
        {
            bool hasTransferPass = passMetadata.Any(m => m.Stage == ERenderGraphPassStage.Transfer);
            return hasTransferPass ? ERenderGraphPassStage.Transfer : ERenderGraphPassStage.Graphics;
        }

        return ERenderGraphPassStage.Graphics;
    }

    private void EnsureFrameBufferRegistered(XRFrameBuffer frameBuffer)
    {
        var registry = RuntimeEngine.Rendering.State.CurrentResourceRegistry;
        if (registry is null)
            return;

        string? name = frameBuffer.Name;
        if (string.IsNullOrWhiteSpace(name))
            return;

        FrameBufferResourceDescriptor? descriptor = registry.FrameBufferRecords.TryGetValue(name, out RenderFrameBufferResource? record)
            ? record.Descriptor
            : null;
        registry.BindFrameBuffer(frameBuffer, descriptor);
    }

    private void EnsureFrameBufferAttachmentsRegistered(XRFrameBuffer frameBuffer)
    {
        var registry = RuntimeEngine.Rendering.State.CurrentResourceRegistry;
        if (registry is null)
            return;

        var targets = frameBuffer.Targets;
        if (targets is null)
            return;

        foreach (var (target, attachment, mipLevel, layerIndex) in targets)
        {
            if (target is XRTexture texture)
            {
                string? textureName = texture.Name;
                if (!string.IsNullOrWhiteSpace(textureName))
                {
                    TextureResourceDescriptor descriptor = registry.TextureRecords.TryGetValue(textureName, out RenderTextureResource? existingRecord)
                        ? existingRecord.Descriptor
                        : RenderResourceDescriptorFactory.FromTexture(texture);

                    registry.BindTexture(texture, EnrichTextureDescriptorForFrameBufferAttachment(descriptor, texture, attachment, mipLevel, layerIndex));
                }

                if (texture is XRTextureViewBase view)
                {
                    XRTexture viewedTexture = view.GetViewedTexture();
                    string? viewedTextureName = viewedTexture.Name;
                    if (!string.IsNullOrWhiteSpace(viewedTextureName))
                    {
                        TextureResourceDescriptor descriptor = registry.TextureRecords.TryGetValue(viewedTextureName, out RenderTextureResource? existingRecord)
                            ? existingRecord.Descriptor
                            : RenderResourceDescriptorFactory.FromTexture(viewedTexture);

                        int sourceMipLevel = mipLevel >= 0 ? SaturatingAddToInt32(view.MinLevel, (uint)mipLevel) : mipLevel;
                        int sourceLayerIndex = layerIndex >= 0 ? SaturatingAddToInt32(view.MinLayer, (uint)layerIndex) : layerIndex;
                        registry.BindTexture(viewedTexture, EnrichTextureDescriptorForFrameBufferAttachment(descriptor, viewedTexture, attachment, sourceMipLevel, sourceLayerIndex));
                    }
                }
            }
        }
    }

    private static int SaturatingAddToInt32(uint left, uint right)
    {
        ulong sum = (ulong)left + right;
        return sum > int.MaxValue ? int.MaxValue : (int)sum;
    }

    private static TextureResourceDescriptor EnrichTextureDescriptorForFrameBufferAttachment(
        TextureResourceDescriptor descriptor,
        XRTexture texture,
        EFrameBufferAttachment attachment,
        int mipLevel,
        int layerIndex)
    {
        RenderPipelineResourceUsage usage = descriptor.Usage | RenderPipelineResourceUsage.SampledTexture;
        usage |= attachment is EFrameBufferAttachment.DepthAttachment
            or EFrameBufferAttachment.DepthStencilAttachment
            or EFrameBufferAttachment.StencilAttachment
            ? RenderPipelineResourceUsage.DepthStencilAttachment
            : RenderPipelineResourceUsage.ColorAttachment;

        uint requiredMipLevels = mipLevel >= 0
            ? Math.Max(descriptor.MipPolicy.MipLevelCount, (uint)mipLevel + 1u)
            : Math.Max(descriptor.MipPolicy.MipLevelCount, 1u);

        uint requiredLayers = layerIndex >= 0
            ? Math.Max(descriptor.ArrayLayers, (uint)layerIndex + 1u)
            : descriptor.ArrayLayers;

        return descriptor with
        {
            Name = texture.Name ?? descriptor.Name,
            Usage = usage,
            MipPolicy = descriptor.MipPolicy with { MipLevelCount = requiredMipLevels },
            MipLevelCount = Math.Max(descriptor.MipLevelCount, requiredMipLevels),
            ArrayLayers = Math.Max(requiredLayers, 1u),
            LayerCount = Math.Max(descriptor.LayerCount, requiredLayers)
        };
    }

    internal void TrackTextureBinding(XRTexture texture)
    {
        if (texture is null)
            return;

        string? name = texture.Name;
        if (string.IsNullOrWhiteSpace(name))
            return;

        RenderResourceRegistry? registry = RuntimeEngine.Rendering.State.CurrentResourceRegistry;
        if (registry is null)
            return;

        if (!registry.TextureRecords.TryGetValue(name, out RenderTextureResource? record))
            return;

        if (ReferenceEquals(record.Instance, texture))
            return;

        registry.BindTexture(texture, record.Descriptor);
    }

    internal void TrackBufferBinding(XRDataBuffer buffer)
    {
        if (buffer is null)
            return;

        string name = string.IsNullOrWhiteSpace(buffer.AttributeName)
            ? buffer.Name ?? string.Empty
            : buffer.AttributeName;

        if (string.IsNullOrWhiteSpace(name))
            return;

        _trackedBuffersByName[name] = buffer;

        RenderResourceRegistry? registry = RuntimeEngine.Rendering.State.CurrentResourceRegistry;
        if (registry is null)
            return;

        if (!registry.BufferRecords.TryGetValue(name, out RenderBufferResource? record))
            return;

        if (ReferenceEquals(record.Instance, buffer))
            return;

        registry.BindBuffer(buffer, record.Descriptor);
    }

    internal bool TryResolveTrackedBuffer(string resourceName, out Buffer buffer, out ulong size)
    {
        if (_resourceAllocator.TryGetBuffer(resourceName, out buffer, out size))
            return true;

        if (_trackedBuffersByName.TryGetValue(resourceName, out XRDataBuffer? dataBuffer) &&
            GetOrCreateAPIRenderObject(dataBuffer, true) is VkDataBuffer vkBuffer)
        {
            vkBuffer.Generate();
            if (vkBuffer.BufferHandle is { } handle && handle.Handle != 0)
            {
                buffer = handle;
                size = Math.Max(dataBuffer.Length, 1u);
                return true;
            }
        }

        buffer = default;
        size = 0;
        return false;
    }
}
