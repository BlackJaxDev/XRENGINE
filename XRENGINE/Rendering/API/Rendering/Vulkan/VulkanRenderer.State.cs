using System;
using Silk.NET.Vulkan;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly VulkanStateTracker _state = new();
    private bool[]? _commandBufferDirtyFlags;

    private sealed class VulkanStateTracker
    {
        private Extent2D _swapchainExtent;
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
            if (!_viewportExplicitlySet)
            {
                _viewport = DefaultViewport(extent);
            }

            if (!CroppingEnabled)
            {
                _scissor = DefaultScissor(extent);
            }
        }

        public Viewport GetViewport()
            => _viewportExplicitlySet ? _viewport : DefaultViewport(_swapchainExtent);

        public void SetViewport(BoundingRectangle region)
        {
            _viewport = new Viewport
            {
                X = region.X,
                Y = region.Y,
                Width = region.Width,
                Height = region.Height,
                MinDepth = 0.0f,
                MaxDepth = 1.0f
            };
            _viewportExplicitlySet = true;
        }

        public Rect2D GetScissor()
            => CroppingEnabled ? _scissor : DefaultScissor(_swapchainExtent);

        public void SetScissor(BoundingRectangle region)
        {
            _scissor = new Rect2D
            {
                Offset = new Offset2D(region.X, region.Y),
                Extent = new Extent2D((uint)Math.Max(region.Width, 0), (uint)Math.Max(region.Height, 0))
            };
        }

        public void SetCroppingEnabled(bool enabled)
        {
            CroppingEnabled = enabled;
            if (!enabled)
                _scissor = DefaultScissor(_swapchainExtent);
        }

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
        MarkCommandBuffersDirty();
    }

    private void MarkCommandBuffersDirty()
    {
        if (_commandBufferDirtyFlags is null)
            return;

        for (int i = 0; i < _commandBufferDirtyFlags.Length; i++)
            _commandBufferDirtyFlags[i] = true;
    }
}
