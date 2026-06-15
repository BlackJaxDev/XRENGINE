using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    private readonly VulkanStateTracker _state = new();
    private VulkanResourcePlanner _resourcePlanner = new();
    private VulkanResourceAllocator _resourceAllocator = new();
    private readonly VulkanBarrierPlanner _barrierPlanner = new();
    private VulkanCompiledRenderGraph _compiledRenderGraph = VulkanCompiledRenderGraph.Empty;
    private FrameOpContext? _lastActiveFrameOpContext;
    private ulong _resourcePlannerSignature = ulong.MaxValue;
    private ulong _resourcePlannerRevision;
    private bool _isRecordingCommandBuffer;
    private readonly Dictionary<string, XRDataBuffer> _trackedBuffersByName = new(StringComparer.OrdinalIgnoreCase);
    internal VulkanResourcePlanner ResourcePlanner => _resourcePlanner;
    internal VulkanResourcePlan ResourcePlan => _resourcePlanner.CurrentPlan;
    internal VulkanResourceAllocator ResourceAllocator => _resourceAllocator;
    internal VulkanCompiledRenderGraph CompiledRenderGraph => _compiledRenderGraph;
    internal ulong ResourcePlannerRevision => _resourcePlannerRevision;
    private bool[]? _commandBufferDirtyFlags;
    private XRFrameBuffer? _boundDrawFrameBuffer;
    private XRFrameBuffer? _boundReadFrameBuffer;
    private EReadBufferMode _readBufferMode = EReadBufferMode.ColorAttachment0;
    private EVulkanQueueOverlapMode _autoQueueOverlapMode = EVulkanQueueOverlapMode.GraphicsOnly;
    private EVulkanQueueOverlapMode _lastResolvedQueueOverlapMode = EVulkanQueueOverlapMode.GraphicsOnly;
    private int _queueOverlapPromotionStabilityFrames;
    private int _queueOverlapFramesInMode;
    private long _lastQueueOverlapSampleTimestamp;
    private ulong _lastQueueOverlapSampleFrameId = ulong.MaxValue;
    private double _queueOverlapFrameDeltaEmaMs = -1.0;
    private double _queueOverlapModeStartFrameDeltaMs = -1.0;

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

    private static Extent2D ResolveFrameBufferDrawExtent(XRFrameBuffer fbo)
    {
        uint width = Math.Max(fbo.Width, 1u);
        uint height = Math.Max(fbo.Height, 1u);
        var targets = fbo.Targets;
        if (targets is null || targets.Length == 0)
            return new Extent2D(width, height);

        int maxMip = 0;
        foreach (var (_, _, mip, _) in targets)
            maxMip = Math.Max(maxMip, mip);

        if (maxMip <= 0)
            return new Extent2D(width, height);

        return new Extent2D(
            Math.Max(width >> maxMip, 1u),
            Math.Max(height >> maxMip, 1u));
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

        public void SetCurrentTargetExtent(Extent2D extent)
            => _currentTargetExtent = extent;

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

        public void SetViewport(BoundingRectangle region)
        {
            // Engine regions remain bottom-left rectangles. The clip-space policy chooses
            // whether Vulkan uses a negative-height GL-style viewport or native Y-down mapping.
            _viewportRegion = region;
            _viewportExplicitlySet = true;
        }

        public Rect2D GetScissor(Extent2D targetExtent)
            => CroppingEnabled
                ? CreateVulkanScissor(_scissorRegion, targetExtent)
                : DefaultScissor(targetExtent);

        public void SetScissor(BoundingRectangle region)
            => _scissorRegion = region;

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

        public void SetCroppingEnabled(bool enabled)
            => CroppingEnabled = enabled;

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

        private static Rect2D DefaultScissor(Extent2D extent)
            => new()
            {
                Offset = new Offset2D(0, 0),
                Extent = new Extent2D(extent.Width, extent.Height)
            };

        public void SetClearColor(ColorF4 color)
            => ClearColor = color;

        public void SetClearDepth(float depth)
            => ClearDepth = depth;

        public void SetClearStencil(int stencil)
            => ClearStencil = (uint)stencil;

        public void SetClearState(bool color, bool depth, bool stencil)
        {
            ClearColorEnabled = color;
            ClearDepthEnabled = depth;
            ClearStencilEnabled = stencil;
        }

        public void SetDepthTestEnabled(bool enabled)
            => DepthTestEnabled = enabled;

        public void SetDepthWriteEnabled(bool enabled)
            => DepthWriteEnabled = enabled;

        public void SetDepthCompare(CompareOp op)
            => DepthCompareOp = op;

        public void SetStencilWriteMask(uint mask)
            => StencilWriteMask = mask;

        public void SetStencilEnabled(bool enabled)
            => StencilTestEnabled = enabled;

        public void SetStencilStates(StencilOpState front, StencilOpState back)
        {
            FrontStencilState = front;
            BackStencilState = back;
        }

        public void SetColorMask(bool red, bool green, bool blue, bool alpha)
        {
            ColorWriteMask = ColorComponentFlags.None;
            if (red)
                ColorWriteMask |= ColorComponentFlags.RBit;
            if (green)
                ColorWriteMask |= ColorComponentFlags.GBit;
            if (blue)
                ColorWriteMask |= ColorComponentFlags.BBit;
            if (alpha)
                ColorWriteMask |= ColorComponentFlags.ABit;
        }

        public void SetCullMode(CullModeFlags mode)
            => CullMode = mode;

        public void SetFrontFace(FrontFace frontFace)
            => FrontFace = frontFace;

        public void SetBlendState(
            bool enabled,
            BlendOp colorOp,
            BlendOp alphaOp,
            BlendFactor srcColor,
            BlendFactor dstColor,
            BlendFactor srcAlpha,
            BlendFactor dstAlpha)
        {
            BlendEnabled = enabled;
            ColorBlendOp = colorOp;
            AlphaBlendOp = alphaOp;
            SrcColorBlendFactor = srcColor;
            DstColorBlendFactor = dstColor;
            SrcAlphaBlendFactor = srcAlpha;
            DstAlphaBlendFactor = dstAlpha;
        }

        public void SetAlphaToCoverageEnabled(bool enabled)
            => AlphaToCoverageEnabled = enabled;

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
        FrameOpContext primary = SelectPrimaryPlannerContext(ops);
        RenderResourceRegistry? mergedRegistry = BuildMergedFrameOpRegistry(ops, primary.ResourceRegistry);
        FrameOpContext plannerContext = mergedRegistry is null
            ? primary
            : primary with { ResourceRegistry = mergedRegistry };

        plannerContext = RefreshPlannerExtentsFromLiveContext(plannerContext, ops);
        UpdateResourcePlannerFromContext(plannerContext, activePassIndices);
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

            if (score <= bestScore)
                continue;

            bestScore = score;
            best = context;
        }

        return best;
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

    private static RenderResourceRegistry? BuildMergedFrameOpRegistry(
        FrameOp[] ops,
        RenderResourceRegistry? primaryRegistry)
    {
        HashSet<RenderResourceRegistry> registries = new();
        foreach (FrameOp op in ops)
        {
            if (op.Context.ResourceRegistry is { } registry)
                registries.Add(registry);
        }

        if (registries.Count == 0)
            return primaryRegistry;

        if (registries.Count == 1)
            return registries.First();

        RenderResourceRegistry merged = new();
        if (primaryRegistry is not null)
            AddRegistryDescriptors(merged, primaryRegistry, overwrite: true);

        foreach (RenderResourceRegistry registry in registries)
        {
            if (ReferenceEquals(registry, primaryRegistry))
                continue;

            AddRegistryDescriptors(merged, registry, overwrite: false);
        }

        return merged;
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
        HashSet<int>? activePassIndices = null)
    {
        IReadOnlyCollection<RenderPassMetadata>? activePassMetadata = FilterActivePassMetadata(
            context.PassMetadata,
            activePassIndices);
        VulkanCompiledRenderGraph compiledGraph = _renderGraphCompiler.Compile(activePassMetadata);
        VulkanBarrierPlanner.QueueOwnershipConfig queueOwnership = BuildQueueOwnershipConfig(activePassMetadata);
        ulong plannerSignature = ComputeResourcePlannerSignature(context, queueOwnership, compiledGraph, activePassMetadata);
        if (plannerSignature == _resourcePlannerSignature)
            return;

        VulkanResourcePlanner pendingPlanner = new();
        VulkanResourceAllocator pendingAllocator = new();
        try
        {
            pendingPlanner.Sync(context.ResourceRegistry);
            ValidateVulkanResourcePlanMetadata(context.PassMetadata, pendingPlanner, activePassIndices);
            pendingAllocator.UpdatePlan(pendingPlanner.CurrentPlan);
            pendingAllocator.RebuildPhysicalPlan(
                this,
                context.PassMetadata,
                pendingPlanner,
                BuildResourceExtentContext(context));
            pendingAllocator.AllocatePhysicalImages(this);
            pendingAllocator.AllocatePhysicalBuffers(this);
        }
        catch (Exception ex)
        {
            pendingAllocator.DestroyPhysicalImages(this);
            pendingAllocator.DestroyPhysicalBuffers(this);
            Debug.VulkanWarning(
                "[VulkanResourcePlanner] Pending physical resource plan failed. Keeping active plan revision={0}. Reason={1}",
                _resourcePlannerRevision,
                ex.Message);
            return;
        }

        VulkanResourceAllocator oldAllocator = _resourceAllocator;
        int retiredImageCount = oldAllocator.EnumeratePhysicalGroups().Count(static g => g.IsAllocated);
        int retiredBufferCount = oldAllocator.EnumeratePhysicalBufferGroups().Count(static g => g.IsAllocated);

        _resourcePlanner = pendingPlanner;
        _resourceAllocator = pendingAllocator;

        // Transition every newly-allocated physical image from VK_IMAGE_LAYOUT_UNDEFINED
        // to a usable initial layout so that the first render pass that references them
        // does not hit a validation error (images stuck in UNDEFINED).  Depth/stencil
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

        _compiledRenderGraph = compiledGraph;
        _barrierPlanner.Rebuild(
            activePassMetadata,
            _resourcePlanner,
            _resourceAllocator,
            _compiledRenderGraph.Synchronization,
            queueOwnership);

        _resourcePlannerSignature = plannerSignature;
        _resourcePlannerRevision++;
    }

    private static IReadOnlyCollection<RenderPassMetadata>? FilterActivePassMetadata(
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        HashSet<int>? activePassIndices)
    {
        if (passMetadata is null || passMetadata.Count == 0 || activePassIndices is not { Count: > 0 })
            return passMetadata;

        return passMetadata
            .Where(pass => activePassIndices.Contains(pass.PassIndex))
            .OrderBy(pass => pass.PassIndex)
            .ToArray();
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
        HashSet<int>? activePassIndices)
    {
        if (passMetadata is null || passMetadata.Count == 0 || activePassIndices is not { Count: > 0 })
            return;

        foreach (RenderPassMetadata pass in passMetadata)
        {
            if (!activePassIndices.Contains(pass.PassIndex))
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

    private static int ComputeResourceRegistrySignature(RenderResourceRegistry? registry)
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

        foreach (KeyValuePair<string, RenderFrameBufferResource> pair in registry.FrameBufferRecords.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            FrameBufferResourceDescriptor descriptor = pair.Value.Descriptor;
            hash.Add(pair.Key, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)descriptor.Lifetime);
            hash.Add((int)descriptor.SizePolicy.SizeClass);
            hash.Add(descriptor.SizePolicy.ScaleX);
            hash.Add(descriptor.SizePolicy.ScaleY);
            hash.Add(descriptor.SizePolicy.Width);
            hash.Add(descriptor.SizePolicy.Height);
            hash.Add(descriptor.Attachments.Count);

            foreach (FrameBufferAttachmentDescriptor attachment in descriptor.Attachments)
            {
                hash.Add(attachment.ResourceName, StringComparer.OrdinalIgnoreCase);
                hash.Add((int)attachment.Attachment);
                hash.Add(attachment.MipLevel);
                hash.Add(attachment.LayerIndex);
            }
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
        }

        return hash.ToHashCode();
    }

    private static int ComputePassMetadataSignature(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return 0;

        HashCode hash = new();
        hash.Add(passMetadata.Count);

        foreach (RenderPassMetadata pass in passMetadata.OrderBy(static p => p.PassIndex))
        {
            hash.Add(pass.PassIndex);
            hash.Add((int)pass.Stage);
            hash.Add(pass.Name, StringComparer.Ordinal);

            foreach (RenderPassResourceUsage usage in pass.ResourceUsages
                .OrderBy(static u => u.ResourceName, StringComparer.Ordinal)
                .ThenBy(static u => u.ResourceType)
                .ThenBy(static u => u.Access)
                .ThenBy(static u => u.LoadOp)
                .ThenBy(static u => u.StoreOp)
                .ThenBy(static u => u.SubresourceRange.BaseMipLevel)
                .ThenBy(static u => u.SubresourceRange.MipLevelCount)
                .ThenBy(static u => u.SubresourceRange.BaseArrayLayer)
                .ThenBy(static u => u.SubresourceRange.ArrayLayerCount))
            {
                hash.Add(usage.ResourceName, StringComparer.Ordinal);
                hash.Add((int)usage.ResourceType);
                hash.Add((int)usage.Access);
                hash.Add((int)usage.LoadOp);
                hash.Add((int)usage.StoreOp);
                AddSubresourceRangeToHash(ref hash, usage.SubresourceRange);
            }

            foreach (int dependency in pass.ExplicitDependencies.OrderBy(static d => d))
                hash.Add(dependency);

            foreach (string schema in pass.DescriptorSchemas.OrderBy(static s => s, StringComparer.Ordinal))
                hash.Add(schema, StringComparer.Ordinal);
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

    private void MarkCommandBuffersDirty()
    {
        if (_commandBufferDirtyFlags is null)
            return;

        for (int i = 0; i < _commandBufferDirtyFlags.Length; i++)
            _commandBufferDirtyFlags[i] = true;
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

        if (passIndex == int.MinValue)
        {
            int currentPassIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
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

        registry.BindFrameBuffer(frameBuffer);
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

        RenderResourceLifetime lifetime = buffer.Usage is EBufferUsage.StreamDraw or EBufferUsage.StreamCopy
            ? RenderResourceLifetime.Transient
            : RenderResourceLifetime.Persistent;

        _trackedBuffersByName[name] = buffer;

        RenderResourceRegistry? registry = RuntimeEngine.Rendering.State.CurrentResourceRegistry;
        if (registry is not null)
        {
            BufferResourceDescriptor descriptor = RenderResourceDescriptorFactory.FromBuffer(buffer, lifetime) with { Name = name };
            registry.BindBuffer(buffer, descriptor);
        }
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
