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
    private readonly VulkanResourcePlanner _resourcePlanner = new();
    private readonly VulkanResourceAllocator _resourceAllocator = new();
    private readonly VulkanBarrierPlanner _barrierPlanner = new();
    private VulkanCompiledRenderGraph _compiledRenderGraph = VulkanCompiledRenderGraph.Empty;
    private ulong _resourcePlannerSignature = ulong.MaxValue;
    private ulong _resourcePlannerRevision;
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

    private readonly record struct QueueOverlapMetrics(
        int ComputePassCount,
        int TransferUsageCount,
        int OverlapCandidatePassCount,
        int TransferCost,
        int QueueOwnershipTransfers,
        int BarrierStageFlushes,
        TimeSpan FrameDelta);

    internal Viewport GetCurrentViewport()
        => _state.GetViewport();

    internal Rect2D GetCurrentScissor()
        => _state.GetScissor();

    internal XRFrameBuffer? GetCurrentDrawFrameBuffer()
        => _boundDrawFrameBuffer;

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

    private sealed class VulkanStateTracker
    {
        private Extent2D _swapchainExtent;
        private Extent2D _currentTargetExtent;
        private bool _viewportExplicitlySet;
        private Viewport _viewport;
        private Rect2D _scissor;

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
            if (!_viewportExplicitlySet)
            {
                _viewport = DefaultViewport(_currentTargetExtent);
            }

            if (!CroppingEnabled)
            {
                _scissor = DefaultScissor(_currentTargetExtent);
            }
        }

        public void SetCurrentTargetExtent(Extent2D extent)
        {
            _currentTargetExtent = extent;
            if (!_viewportExplicitlySet)
                _viewport = DefaultViewport(extent);

            if (!CroppingEnabled)
                _scissor = DefaultScissor(extent);
        }

        public Extent2D GetCurrentTargetExtent()
            => _currentTargetExtent;

        public Viewport GetViewport()
            => _viewportExplicitlySet ? _viewport : DefaultViewport(_currentTargetExtent);

        public void SetViewport(BoundingRectangle region)
        {
            // Engine regions are specified in OpenGL-style bottom-left coordinates.
            // Vulkan's viewport/scissor use top-left framebuffer coordinates, so we flip Y.
            // Negative viewport height flips the coordinate system to match OpenGL.
            float viewportY = _currentTargetExtent.Height - region.Y;
            _viewport = new Viewport
            {
                X = region.X,
                Y = viewportY,
                Width = region.Width,
                Height = -region.Height,
                MinDepth = 0.0f,
                MaxDepth = 1.0f
            };
            _viewportExplicitlySet = true;
        }

        public Rect2D GetScissor()
            => CroppingEnabled ? _scissor : DefaultScissor(_currentTargetExtent);

        public void SetScissor(BoundingRectangle region)
        {
            int targetWidth = (int)Math.Max(_currentTargetExtent.Width, 1u);
            int targetHeight = (int)Math.Max(_currentTargetExtent.Height, 1u);

            int clampedX = Math.Clamp(region.X, 0, targetWidth);
            int clampedBottomY = Math.Clamp(region.Y, 0, targetHeight);

            int maxWidth = Math.Max(targetWidth - clampedX, 0);
            int maxHeight = Math.Max(targetHeight - clampedBottomY, 0);

            int clampedWidth = Math.Clamp(Math.Max(region.Width, 0), 0, maxWidth);
            int clampedHeight = Math.Clamp(Math.Max(region.Height, 0), 0, maxHeight);

            // Convert bottom-left origin to Vulkan's top-left scissor origin after clamping.
            int yTopLeft = targetHeight - (clampedBottomY + clampedHeight);
            _scissor = new Rect2D
            {
                Offset = new Offset2D(clampedX, Math.Max(yTopLeft, 0)),
                Extent = new Extent2D((uint)clampedWidth, (uint)clampedHeight)
            };
        }

        public void SetCroppingEnabled(bool enabled)
        {
            CroppingEnabled = enabled;
            if (!enabled)
                _scissor = DefaultScissor(_currentTargetExtent);
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
        public bool GetStencilTestEnabled() => StencilTestEnabled;
        public StencilOpState GetFrontStencilState() => FrontStencilState;
        public StencilOpState GetBackStencilState() => BackStencilState;

        private static Viewport DefaultViewport(Extent2D extent)
            => new()
            {
                X = 0f,
                Y = 0f,
                Width = extent.Width,
                Height = extent.Height,
                MinDepth = 0f,
                MaxDepth = 1f
            };

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
        IReadOnlyCollection<RenderPassMetadata>? PassMetadata)
    {
        public int SchedulingIdentity => HashCode.Combine(PipelineIdentity, ViewportIdentity);
    }

    internal FrameOpContext CaptureFrameOpContext()
    {
        XRRenderPipelineInstance? pipeline = Engine.Rendering.State.CurrentRenderingPipeline;
        XRViewport? viewport = Engine.Rendering.State.RenderingViewport;
        return new FrameOpContext(
            pipeline?.GetHashCode() ?? 0,
            viewport?.GetHashCode() ?? 0,
            pipeline,
            pipeline?.Resources,
            pipeline?.Pipeline?.PassMetadata);
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

    private void UpdateResourcePlannerFromContext(in FrameOpContext context)
    {
        VulkanCompiledRenderGraph compiledGraph = _renderGraphCompiler.Compile(context.PassMetadata);
        VulkanBarrierPlanner.QueueOwnershipConfig queueOwnership = BuildQueueOwnershipConfig(context.PassMetadata);
        ulong plannerSignature = ComputeResourcePlannerSignature(context, queueOwnership, compiledGraph, context.PassMetadata);
        if (plannerSignature == _resourcePlannerSignature)
            return;

        // Destroy old physical Vulkan resources BEFORE UpdatePlan clears the allocator
        // dictionaries. UpdatePlan wipes _physicalGroups / _physicalBufferGroups, which
        // would orphan live VkImage / DeviceMemory handles and leak GPU memory.
        _resourceAllocator.DestroyPhysicalImages(this);
        _resourceAllocator.DestroyPhysicalBuffers(this);

        _resourcePlanner.Sync(context.ResourceRegistry);
        _resourceAllocator.UpdatePlan(_resourcePlanner.CurrentPlan);
        _resourceAllocator.RebuildPhysicalPlan(this, context.PassMetadata, _resourcePlanner);
        _resourceAllocator.AllocatePhysicalImages(this);
        _resourceAllocator.AllocatePhysicalBuffers(this);

        _compiledRenderGraph = compiledGraph;

        _barrierPlanner.Rebuild(
            context.PassMetadata,
            _resourcePlanner,
            _resourceAllocator,
            _compiledRenderGraph.Synchronization,
            queueOwnership);

        _resourcePlannerSignature = plannerSignature;
        _resourcePlannerRevision++;
    }

    private static ulong ComputeResourcePlannerSignature(
        in FrameOpContext context,
        in VulkanBarrierPlanner.QueueOwnershipConfig queueOwnership,
        VulkanCompiledRenderGraph compiledGraph,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        HashCode hash = new();
        hash.Add(ComputeResourceRegistrySignature(context.ResourceRegistry));

        XRViewport? viewport = Engine.Rendering.State.RenderingViewport;
        hash.Add(viewport?.Width ?? 0);
        hash.Add(viewport?.Height ?? 0);
        hash.Add(viewport?.InternalWidth ?? 0);
        hash.Add(viewport?.InternalHeight ?? 0);

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
                .ThenBy(static u => u.StoreOp))
            {
                hash.Add(usage.ResourceName, StringComparer.Ordinal);
                hash.Add((int)usage.ResourceType);
                hash.Add((int)usage.Access);
                hash.Add((int)usage.LoadOp);
                hash.Add((int)usage.StoreOp);
            }

            foreach (int dependency in pass.ExplicitDependencies.OrderBy(static d => d))
                hash.Add(dependency);

            foreach (string schema in pass.DescriptorSchemas.OrderBy(static s => s, StringComparer.Ordinal))
                hash.Add(schema, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
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

        Engine.Rendering.Stats.RecordVulkanQueueOverlapWindow(
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
        int computePassCount = hasMetadata ? passMetadata!.Count(static p => p.Stage == RenderGraphPassStage.Compute) : 0;
        int transferUsageCount = hasMetadata
            ? passMetadata!.Sum(static p => p.ResourceUsages.Count(static u => u.ResourceType is RenderPassResourceType.TransferSource or RenderPassResourceType.TransferDestination))
            : 0;
        int overlapCandidatePassCount = hasMetadata
            ? passMetadata!.Count(IsQueueOverlapCandidatePass)
            : 0;

        int queueOwnershipTransfers = Engine.Rendering.Stats.VulkanQueueOwnershipTransfers;
        int stageFlushes = Engine.Rendering.Stats.VulkanBarrierStageFlushes;
        int transferCost = transferUsageCount + queueOwnershipTransfers + stageFlushes;

        TimeSpan frameDelta = TimeSpan.Zero;
        ulong frameId = Engine.Rendering.State.RenderFrameId;
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
        if (pass.Stage != RenderGraphPassStage.Compute)
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

        EVulkanQueueOverlapMode requestedMode = Engine.EffectiveSettings.VulkanQueueOverlapMode;
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
            MipLevels = 1,
            ArrayLayers = Math.Max(group.Template.Layers, 1u),
            Format = group.Format,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = group.Usage,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive,
        };

        fixed (Image* imagePtr = &image)
        {
            Result result = Api!.CreateImage(device, ref imageInfo, null, imagePtr);
            if (result != Result.Success)
                throw new Exception($"Failed to create Vulkan image for resource group '{group.Key}'. Result={result}.");
        }

        Api!.GetImageMemoryRequirements(device, image, out MemoryRequirements memRequirements);

        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = FindMemoryType(memRequirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };

        fixed (DeviceMemory* memPtr = &memory)
        {
            AllocateMemory(allocInfo, memPtr);
        }

        Result bindResult = Api!.BindImageMemory(device, image, memory, 0);
        if (bindResult != Result.Success)
            throw new Exception($"Failed to bind device memory for Vulkan image group '{group.Key}'. Result={bindResult}.");
    }

    internal void DestroyPhysicalImage(ref Image image, ref DeviceMemory memory)
    {
        if (image.Handle != 0)
        {
            Api!.DestroyImage(device, image, null);
            image = default;
        }

        if (memory.Handle != 0)
        {
            Api!.FreeMemory(device, memory, null);
            memory = default;
        }
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

        Api!.GetBufferMemoryRequirements(device, buffer, out MemoryRequirements memRequirements);

        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = FindMemoryType(memRequirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };

        fixed (DeviceMemory* memPtr = &memory)
            AllocateMemory(allocInfo, memPtr);

        Result bindResult = Api!.BindBufferMemory(device, buffer, memory, 0);
        if (bindResult != Result.Success)
            throw new Exception($"Failed to bind device memory for Vulkan buffer group '{group.Key}'. Result={bindResult}.");
    }

    internal void DestroyPhysicalBuffer(ref Buffer buffer, ref DeviceMemory memory)
    {
        if (buffer.Handle != 0)
        {
            Api!.DestroyBuffer(device, buffer, null);
            buffer = default;
        }

        if (memory.Handle != 0)
        {
            Api!.FreeMemory(device, memory, null);
            memory = default;
        }
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

    internal int EnsureValidPassIndex(
        int passIndex,
        string opName,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata = null)
    {
        passMetadata ??= Engine.Rendering.State.CurrentRenderingPipeline?.Pipeline?.PassMetadata;

        bool hasMetadata = passMetadata is { Count: > 0 };
        bool passDefinedInMetadata = hasMetadata && passMetadata!.Any(m => m.PassIndex == passIndex);

        if (passIndex != int.MinValue && (!hasMetadata || passDefinedInMetadata))
            return passIndex;

        if (passIndex == int.MinValue)
        {
            int currentPassIndex = Engine.Rendering.State.CurrentRenderGraphPassIndex;
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
            Engine.Rendering.State.CurrentRenderingPipeline?.GetType().Name ?? "null");

        return fallback;
    }

    private static int ResolveFallbackPassIndex(string opName, IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return 0;

        RenderGraphPassStage? preferredStage = ResolvePreferredFallbackStage(opName, passMetadata);
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

    private static RenderGraphPassStage? ResolvePreferredFallbackStage(string opName, IReadOnlyCollection<RenderPassMetadata> passMetadata)
    {
        if (opName.Contains("Compute", StringComparison.OrdinalIgnoreCase))
            return RenderGraphPassStage.Compute;

        if (opName.Contains("Blit", StringComparison.OrdinalIgnoreCase))
        {
            bool hasTransferPass = passMetadata.Any(m => m.Stage == RenderGraphPassStage.Transfer);
            return hasTransferPass ? RenderGraphPassStage.Transfer : RenderGraphPassStage.Graphics;
        }

        return RenderGraphPassStage.Graphics;
    }

    private void EnsureFrameBufferRegistered(XRFrameBuffer frameBuffer)
    {
        var registry = Engine.Rendering.State.CurrentResourceRegistry;
        if (registry is null)
            return;

        string? name = frameBuffer.Name;
        if (string.IsNullOrWhiteSpace(name))
            return;

        registry.BindFrameBuffer(frameBuffer);
    }

    private void EnsureFrameBufferAttachmentsRegistered(XRFrameBuffer frameBuffer)
    {
        var registry = Engine.Rendering.State.CurrentResourceRegistry;
        if (registry is null)
            return;

        var targets = frameBuffer.Targets;
        if (targets is null)
            return;

        foreach (var (target, _, _, _) in targets)
        {
            if (target is XRTexture texture)
            {
                string? textureName = texture.Name;
                if (!string.IsNullOrWhiteSpace(textureName))
                    registry.BindTexture(texture);
            }
        }
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

        RenderResourceRegistry? registry = Engine.Rendering.State.CurrentResourceRegistry;
        if (registry is not null)
        {
            BufferResourceDescriptor descriptor = BufferResourceDescriptor.FromBuffer(buffer, lifetime) with { Name = name };
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
