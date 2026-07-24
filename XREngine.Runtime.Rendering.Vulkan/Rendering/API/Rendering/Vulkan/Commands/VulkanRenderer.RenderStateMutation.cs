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

        public bool ClearViewport()
        {
            if (!_viewportExplicitlySet)
                return false;

            _viewportExplicitlySet = false;
            _viewportRegion = default;
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

        public bool SetIndexedViewportScissors(
            ReadOnlySpan<BoundingRectangle> viewports,
            ReadOnlySpan<BoundingRectangle> scissors)
        {
            int count = Math.Min(viewports.Length, scissors.Length);
            if (count <= 0)
                return ClearIndexedViewportScissors();

            BoundingRectangle[]? currentViewports = _indexedViewportRegions;
            BoundingRectangle[]? currentScissors = _indexedScissorRegions;
            bool unchanged =
                currentViewports is not null &&
                currentScissors is not null &&
                currentViewports.Length == count &&
                currentScissors.Length == count;

            for (int i = 0; i < count; i++)
            {
                BoundingRectangle viewport = viewports[i];
                BoundingRectangle scissor = scissors[i];
                viewport.CheckProperDimensions();
                scissor.CheckProperDimensions();
                if (unchanged &&
                    (!SameRectangle(currentViewports![i], viewport) ||
                     !SameRectangle(currentScissors![i], scissor)))
                {
                    unchanged = false;
                }
            }

            if (unchanged)
                return false;

            BoundingRectangle[] viewportRegions = new BoundingRectangle[count];
            BoundingRectangle[] scissorRegions = new BoundingRectangle[count];
            for (int i = 0; i < count; i++)
            {
                viewportRegions[i] = viewports[i];
                scissorRegions[i] = scissors[i];
            }

            _indexedViewportRegions = viewportRegions;
            _indexedScissorRegions = scissorRegions;
            _indexedViewportCache = null;
            _indexedScissorCache = null;
            _indexedCacheExtent = default;
            return true;
        }

        public bool ClearIndexedViewportScissors()
        {
            if (_indexedViewportRegions is null &&
                _indexedScissorRegions is null &&
                _indexedViewportCache is null &&
                _indexedScissorCache is null &&
                _indexedCacheExtent.Width == 0 &&
                _indexedCacheExtent.Height == 0)
            {
                return false;
            }

            _indexedViewportRegions = null;
            _indexedScissorRegions = null;
            _indexedViewportCache = null;
            _indexedScissorCache = null;
            _indexedCacheExtent = default;
            return true;
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
}
