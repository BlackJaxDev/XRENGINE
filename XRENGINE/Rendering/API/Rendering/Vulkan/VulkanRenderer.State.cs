using System;
using System.Collections.Generic;
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
    private readonly Dictionary<string, XRDataBuffer> _trackedBuffersByName = new(StringComparer.OrdinalIgnoreCase);
    internal VulkanResourcePlanner ResourcePlanner => _resourcePlanner;
    internal VulkanResourcePlan ResourcePlan => _resourcePlanner.CurrentPlan;
    internal VulkanResourceAllocator ResourceAllocator => _resourceAllocator;
    internal VulkanCompiledRenderGraph CompiledRenderGraph => _compiledRenderGraph;
    private bool[]? _commandBufferDirtyFlags;
    private XRFrameBuffer? _boundDrawFrameBuffer;
    private XRFrameBuffer? _boundReadFrameBuffer;
    private EReadBufferMode _readBufferMode = EReadBufferMode.ColorAttachment0;

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
            // Convert bottom-left origin to Vulkan's top-left scissor origin.
            int yTopLeft = (int)_currentTargetExtent.Height - (region.Y + region.Height);
            _scissor = new Rect2D
            {
                Offset = new Offset2D(region.X, yTopLeft),
                Extent = new Extent2D((uint)Math.Max(region.Width, 0), (uint)Math.Max(region.Height, 0))
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
            pipeline?.Resources,
            pipeline?.Pipeline?.PassMetadata);
    }

    private void UpdateResourcePlannerFromContext(in FrameOpContext context)
    {
        _resourcePlanner.Sync(context.ResourceRegistry);
        _resourceAllocator.UpdatePlan(_resourcePlanner.CurrentPlan);
        _resourceAllocator.RebuildPhysicalPlan(this, context.PassMetadata, _resourcePlanner);
        _resourceAllocator.AllocatePhysicalImages(this);
        _resourceAllocator.AllocatePhysicalBuffers(this);

        _compiledRenderGraph = _renderGraphCompiler.Compile(context.PassMetadata);
        VulkanBarrierPlanner.QueueOwnershipConfig queueOwnership = BuildQueueOwnershipConfig();
        _barrierPlanner.Rebuild(
            context.PassMetadata,
            _resourcePlanner,
            _resourceAllocator,
            _compiledRenderGraph.Synchronization,
            queueOwnership);
    }

    private VulkanBarrierPlanner.QueueOwnershipConfig BuildQueueOwnershipConfig()
    {
        uint graphicsFamily = FamilyQueueIndices.GraphicsFamilyIndex ?? 0u;

        // The current backend records all work into command buffers sourced from the graphics
        // queue family. Keep ownership in that family until async compute/transfer submission
        // paths are implemented.
        return new VulkanBarrierPlanner.QueueOwnershipConfig(
            graphicsFamily,
            graphicsFamily,
            graphicsFamily);
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

        int fallback = hasMetadata
            ? passMetadata!.OrderBy(m => m.PassIndex).First().PassIndex
            : 0;

        string reason = passIndex == int.MinValue
            ? "invalid sentinel value"
            : $"pass {passIndex} is missing from metadata";

        Debug.VulkanWarningEvery(
            $"Vulkan.InvalidPass.{opName}.{passIndex}",
            TimeSpan.FromSeconds(1),
            "[Vulkan] '{0}' emitted with invalid render-graph pass index ({1}). Falling back to pass {2}.",
            opName,
            reason,
            fallback);

        return fallback;
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
