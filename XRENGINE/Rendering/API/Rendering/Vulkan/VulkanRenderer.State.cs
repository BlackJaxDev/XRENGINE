using System;
using System.Linq;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly VulkanStateTracker _state = new();
    private readonly VulkanResourcePlanner _resourcePlanner = new();
    private readonly VulkanResourceAllocator _resourceAllocator = new();
    private readonly VulkanBarrierPlanner _barrierPlanner = new();
    internal VulkanResourcePlanner ResourcePlanner => _resourcePlanner;
    internal VulkanResourcePlan ResourcePlan => _resourcePlanner.CurrentPlan;
    internal VulkanResourceAllocator ResourceAllocator => _resourceAllocator;
    private bool[]? _commandBufferDirtyFlags;
    private XRFrameBuffer? _boundDrawFrameBuffer;
    private XRFrameBuffer? _boundReadFrameBuffer;

    internal Viewport GetCurrentViewport()
        => _state.GetViewport();

    internal Rect2D GetCurrentScissor()
        => _state.GetScissor();

    internal XRFrameBuffer? GetCurrentDrawFrameBuffer()
        => _boundDrawFrameBuffer;

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

        public ColorComponentFlags ColorWriteMask { get; private set; }

        public bool CroppingEnabled { get; private set; }

        public EMemoryBarrierMask PendingMemoryBarrierMask { get; private set; }

        public void RegisterMemoryBarrier(EMemoryBarrierMask mask)
            => PendingMemoryBarrierMask |= mask;

        public void ClearPendingMemoryBarrierMask()
            => PendingMemoryBarrierMask = EMemoryBarrierMask.None;

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
        _resourcePlanner.Sync(Engine.Rendering.State.CurrentResourceRegistry);
        _resourceAllocator.UpdatePlan(_resourcePlanner.CurrentPlan);
        _resourceAllocator.RebuildPhysicalPlan(this);
        _resourceAllocator.AllocatePhysicalImages(this);

        IReadOnlyCollection<RenderPassMetadata>? passMetadata = Engine.Rendering.State.CurrentRenderingPipeline?.Pipeline?.PassMetadata;
        _barrierPlanner.Rebuild(passMetadata, _resourcePlanner, _resourceAllocator);
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

    public bool TryGetPhysicalImage(string resourceName, out Image image)
        => _resourceAllocator.TryGetImage(resourceName, out image);

    private void MarkCommandBuffersDirty()
    {
        if (_commandBufferDirtyFlags is null)
            return;

        for (int i = 0; i < _commandBufferDirtyFlags.Length; i++)
            _commandBufferDirtyFlags[i] = true;
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
}
