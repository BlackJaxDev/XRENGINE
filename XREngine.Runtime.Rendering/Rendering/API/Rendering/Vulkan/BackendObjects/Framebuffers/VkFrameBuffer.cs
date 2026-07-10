using System.Collections.Generic;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;
using Format = Silk.NET.Vulkan.Format;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public class VkFrameBuffer(VulkanRenderer api, XRFrameBuffer data) : VkObject<XRFrameBuffer>(api, data)
    {
        private Framebuffer _frameBuffer = default;
        private RenderPass _renderPass = default;
        private FrameBufferAttachmentSignature[]? _attachmentSignature;
        private ImageView[]? _attachmentViews;
        private AttachmentTargetInfo[]? _attachmentTargets;
        private Extent2D[]? _attachmentExtents;
        private readonly List<CachedFrameBufferState> _cachedFrameBufferStates = [];

        public override VkObjectType Type { get; } = VkObjectType.Framebuffer;
        public override bool IsGenerated => IsActive;

        public Framebuffer FrameBuffer => _frameBuffer;
        public RenderPass RenderPass => _renderPass;

        /// <summary>
        /// The actual VkFramebuffer width, which may differ from <see cref="Data"/>.<see cref="XRFrameBuffer.Width"/>
        /// when render targets use a mip level &gt; 0 (e.g. bloom downsample chain).
        /// </summary>
        public uint FramebufferWidth { get; private set; }

        /// <summary>
        /// The actual VkFramebuffer height (see <see cref="FramebufferWidth"/>).
        /// </summary>
        public uint FramebufferHeight { get; private set; }

        /// <summary>
        /// Number of framebuffer layers exposed by the attachment views.
        /// Texture-array FBOs use this for layered shadow rendering.
        /// </summary>
        public uint FramebufferLayers { get; private set; } = 1u;

        /// <summary>
        /// Dynamic-rendering multiview mask for stereo texture-array FBOs. A zero
        /// mask means this framebuffer is layered or single-view, not multiview.
        /// </summary>
        public uint MultiviewViewMask { get; private set; }

        internal uint AttachmentCount => (uint)(_attachmentSignature?.Length ?? 0);

        internal void EnsureCurrent()
        {
            if (!IsActive)
            {
                Generate();
                return;
            }

            AttachmentBuildInfo[] attachments = BuildAttachmentInfos();
            var (fbWidth, fbHeight) = ResolveFramebufferExtent();
            if (AttachmentStateMatches(attachments, fbWidth, fbHeight))
                return;

            if (TryActivateCachedFrameBufferState(attachments, fbWidth, fbHeight))
                return;

            CachedFrameBufferState state = CreateFrameBufferState(attachments, fbWidth, fbHeight);
            _cachedFrameBufferStates.Add(state);
            ActivateFrameBufferState(state);
        }

        internal FrameBufferAttachmentSignature[] ResolveAttachmentSignatureForPass(
            int passIndex,
            IReadOnlyCollection<RenderPassMetadata>? passMetadata,
            ImageLayout[]? initialLayoutOverrides = null,
            RenderGraphSynchronizationInfo? synchronization = null,
            bool preserveTrackedClearLoads = false)
        {
            if (_attachmentSignature is null || _attachmentSignature.Length == 0)
                return [];

            // When initial-layout overrides are provided (from per-frame FBO tracking),
            // always build a planned signature so that the render pass uses the correct
            // initialLayout/loadOp combination.
            bool hasLayoutOverrides = initialLayoutOverrides is not null && initialLayoutOverrides.Length == _attachmentSignature.Length;

            if (passMetadata is null || passMetadata.Count == 0)
            {
                if (!hasLayoutOverrides)
                    return _attachmentSignature;

                // No metadata but we have layout overrides — apply them to the base signature.
                return ApplyInitialLayoutOverrides(_attachmentSignature, initialLayoutOverrides!, preserveTrackedClearLoads);
            }

            string? frameBufferName = Data.Name;
            if (string.IsNullOrWhiteSpace(frameBufferName))
            {
                if (!hasLayoutOverrides)
                    return _attachmentSignature;
                return ApplyInitialLayoutOverrides(_attachmentSignature, initialLayoutOverrides!, preserveTrackedClearLoads);
            }

            RenderPassMetadata? pass = null;
            foreach (RenderPassMetadata metadata in passMetadata)
            {
                if (metadata.PassIndex == passIndex)
                {
                    pass = metadata;
                    break;
                }
            }

            if (pass is null)
            {
                if (!hasLayoutOverrides)
                    return _attachmentSignature;
                return ApplyInitialLayoutOverrides(_attachmentSignature, initialLayoutOverrides!, preserveTrackedClearLoads);
            }

            bool referencesFrameBuffer = false;
            string prefix = $"fbo::{frameBufferName}::";
            foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
            {
                if (!usage.IsAttachment)
                    continue;

                if (usage.ResourceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    referencesFrameBuffer = true;
                    break;
                }
            }

            if (!referencesFrameBuffer)
            {
                if (!hasLayoutOverrides)
                    return _attachmentSignature;
                return ApplyInitialLayoutOverrides(_attachmentSignature, initialLayoutOverrides!, preserveTrackedClearLoads);
            }

            FrameBufferAttachmentSignature[] planned = BuildPlannedAttachmentSignature(pass, frameBufferName, synchronization);

            // Apply per-frame initial-layout overrides on top of metadata-driven planning.
            if (hasLayoutOverrides)
                planned = ApplyInitialLayoutOverrides(planned, initialLayoutOverrides!, preserveTrackedClearLoads);

            return planned;
        }

        internal RenderPass ResolveRenderPassForPass(
            int passIndex,
            IReadOnlyCollection<RenderPassMetadata>? passMetadata,
            ImageLayout[]? initialLayoutOverrides = null,
            RenderGraphSynchronizationInfo? synchronization = null,
            bool preserveTrackedClearLoads = false)
        {
            if (_attachmentSignature is null || _attachmentSignature.Length == 0)
                return _renderPass;

            FrameBufferAttachmentSignature[] planned = ResolveAttachmentSignatureForPass(
                passIndex,
                passMetadata,
                initialLayoutOverrides,
                synchronization,
                preserveTrackedClearLoads);
            if (planned.Length == 0 || SignatureEquals(_attachmentSignature, planned))
                return _renderPass;

            return Renderer.GetOrCreateFrameBufferRenderPass(planned);
        }

        internal bool UsesReadOnlyDepthStencilForPass(
            int passIndex,
            IReadOnlyCollection<RenderPassMetadata>? passMetadata,
            ImageLayout[]? initialLayoutOverrides = null,
            bool preserveTrackedClearLoads = false)
        {
            if (_attachmentSignature is null || _attachmentSignature.Length == 0)
                return false;

            bool hasLayoutOverrides = initialLayoutOverrides is not null && initialLayoutOverrides.Length == _attachmentSignature.Length;
            FrameBufferAttachmentSignature[] BaseSignature() => hasLayoutOverrides
                ? ApplyInitialLayoutOverrides(_attachmentSignature, initialLayoutOverrides!, preserveTrackedClearLoads)
                : _attachmentSignature;

            if (passMetadata is null || passMetadata.Count == 0)
                return UsesReadOnlyDepthStencil(BaseSignature());

            string? frameBufferName = Data.Name;
            if (string.IsNullOrWhiteSpace(frameBufferName))
                return UsesReadOnlyDepthStencil(BaseSignature());

            RenderPassMetadata? pass = null;
            foreach (RenderPassMetadata metadata in passMetadata)
            {
                if (metadata.PassIndex == passIndex)
                {
                    pass = metadata;
                    break;
                }
            }

            if (pass is null)
                return UsesReadOnlyDepthStencil(BaseSignature());

            bool referencesFrameBuffer = false;
            string prefix = $"fbo::{frameBufferName}::";
            foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
            {
                if (!usage.IsAttachment)
                    continue;

                if (usage.ResourceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    referencesFrameBuffer = true;
                    break;
                }
            }

            if (!referencesFrameBuffer)
                return UsesReadOnlyDepthStencil(BaseSignature());

            FrameBufferAttachmentSignature[] planned = BuildPlannedAttachmentSignature(pass, frameBufferName, synchronization: null);
            if (hasLayoutOverrides)
                planned = ApplyInitialLayoutOverrides(planned, initialLayoutOverrides!, preserveTrackedClearLoads);

            return UsesReadOnlyDepthStencil(planned);
        }

        /// <summary>
        /// Returns the <see cref="FrameBufferAttachmentSignature.FinalLayout"/> for each attachment
        /// in the order they appear in the framebuffer's attachment array.
        /// </summary>
        internal ImageLayout[] GetFinalLayouts()
            => GetFinalLayouts(_attachmentSignature);

        internal static ImageLayout[] GetFinalLayouts(FrameBufferAttachmentSignature[]? signatures)
        {
            if (signatures is null || signatures.Length == 0)
                return [];

            ImageLayout[] layouts = new ImageLayout[signatures.Length];
            for (int i = 0; i < signatures.Length; i++)
                layouts[i] = signatures[i].FinalLayout;
            return layouts;
        }

        internal bool TryGetAttachmentView(int attachmentIndex, out ImageView view)
        {
            if (_attachmentViews is not null &&
                (uint)attachmentIndex < (uint)_attachmentViews.Length)
            {
                view = _attachmentViews[attachmentIndex];
                return view.Handle != 0;
            }

            view = default;
            return false;
        }

        internal bool TryGetAttachmentTarget(
            int attachmentIndex,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IFrameBufferAttachement? target,
            out EFrameBufferAttachment attachment,
            out int mipLevel,
            out int layerIndex)
        {
            if (_attachmentTargets is not null &&
                (uint)attachmentIndex < (uint)_attachmentTargets.Length)
            {
                AttachmentTargetInfo info = _attachmentTargets[attachmentIndex];
                target = info.Target;
                attachment = info.Attachment;
                mipLevel = info.MipLevel;
                layerIndex = info.LayerIndex;
                return target is not null;
            }

            target = null;
            attachment = default;
            mipLevel = 0;
            layerIndex = 0;
            return false;
        }

        internal Extent2D ResolveAttachmentCompatibleDrawExtent()
        {
            Extent2D[]? extents = _attachmentExtents;
            if (extents is null || extents.Length == 0)
                return ResolveDrawExtent();

            uint minWidth = uint.MaxValue;
            uint minHeight = uint.MaxValue;
            bool found = false;

            for (int i = 0; i < extents.Length; i++)
            {
                Extent2D extent = extents[i];
                if (extent.Width == 0 || extent.Height == 0)
                    continue;

                minWidth = Math.Min(minWidth, extent.Width);
                minHeight = Math.Min(minHeight, extent.Height);
                found = true;
            }

            return found
                ? new Extent2D(Math.Max(minWidth, 1u), Math.Max(minHeight, 1u))
                : ResolveDrawExtent();
        }

        internal Extent2D ResolveDrawExtent()
        {
            if (FramebufferWidth > 0 && FramebufferHeight > 0)
                return new Extent2D(FramebufferWidth, FramebufferHeight);

            var (width, height) = ResolveFramebufferExtent();
            return new Extent2D(width, height);
        }

        private static uint ResolveFramebufferLayers(ReadOnlySpan<AttachmentBuildInfo> attachments)
        {
            uint layers = uint.MaxValue;
            bool found = false;

            for (int i = 0; i < attachments.Length; i++)
            {
                uint attachmentLayers = Math.Max(attachments[i].LayerCount, 1u);
                layers = found ? Math.Min(layers, attachmentLayers) : attachmentLayers;
                found = true;
            }

            return found ? Math.Max(layers, 1u) : 1u;
        }

        private static uint ResolveFramebufferMultiviewViewMask(ReadOnlySpan<AttachmentBuildInfo> attachments)
        {
            uint viewMask = 0u;
            for (int i = 0; i < attachments.Length; i++)
            {
                uint attachmentViewMask = attachments[i].MultiviewViewMask;
                if (attachmentViewMask == 0u)
                    continue;

                if (viewMask != 0u && viewMask != attachmentViewMask)
                {
                    throw new InvalidOperationException(
                        $"Framebuffer attachments use mismatched multiview masks 0x{viewMask:X} and 0x{attachmentViewMask:X}.");
                }

                viewMask = attachmentViewMask;
            }

            if (viewMask == 0u)
                return 0u;

            uint requiredLayers = ResolveRequiredLayerCountForViewMask(viewMask);
            for (int i = 0; i < attachments.Length; i++)
            {
                if (attachments[i].LayerCount < requiredLayers)
                {
                    throw new InvalidOperationException(
                        $"Framebuffer attachment {i} has {attachments[i].LayerCount} layer(s), but multiview mask 0x{viewMask:X} requires at least {requiredLayers}.");
                }
            }

            return viewMask;
        }

        private static uint ResolveRequiredLayerCountForViewMask(uint viewMask)
        {
            uint required = 0u;
            for (uint bit = 0u; bit < 32u; bit++)
            {
                if ((viewMask & (1u << (int)bit)) != 0u)
                    required = bit + 1u;
            }

            return Math.Max(required, 1u);
        }

        private static FrameBufferAttachmentSignature[] ApplyInitialLayoutOverrides(
            FrameBufferAttachmentSignature[] signatures,
            ImageLayout[] overrides,
            bool preserveClearLoads = false)
        {
            FrameBufferAttachmentSignature[] result = (FrameBufferAttachmentSignature[])signatures.Clone();
            int count = Math.Min(result.Length, overrides.Length);
            for (int i = 0; i < count; i++)
            {
                ImageLayout overrideLayout = overrides[i];
                if (overrideLayout == ImageLayout.Undefined)
                    continue; // No override for this attachment — keep existing initialLayout.

                FrameBufferAttachmentSignature existing = result[i];

                // A concrete tracked layout means this attachment already holds valid
                // content from an earlier pass this frame — possibly a DIFFERENT
                // framebuffer sharing the same image (e.g. the GBuffer depth reused by
                // the forward pass).  Promote a DontCare load to Load so the render
                // pass preserves that content instead of discarding it.  Passes that
                // intend to clear still issue an explicit CmdClearAttachments, which
                // overrides the loaded content, so this is safe for clearing passes.
                // preserveClearLoads is only used for FBO re-entry after an explicit clear
                // command has had the chance to define the intended contents.
                AttachmentLoadOp preservedLoadOp = PreserveTrackedLoadOp(existing.LoadOp, preserveClearLoads);
                AttachmentLoadOp preservedStencilLoadOp = PreserveTrackedLoadOp(existing.StencilLoadOp, preserveClearLoads);

                result[i] = new FrameBufferAttachmentSignature(
                    existing.Format,
                    existing.Samples,
                    existing.AspectMask,
                    existing.Role,
                    existing.ColorIndex,
                    preservedLoadOp,
                    existing.StoreOp,
                    preservedStencilLoadOp,
                    existing.StencilStoreOp,
                    overrideLayout,
                    existing.FinalLayout,
                    existing.ReferenceLayout);
            }
            return result;
        }

        private static AttachmentLoadOp PreserveTrackedLoadOp(AttachmentLoadOp loadOp, bool preserveClearLoads)
            => loadOp switch
            {
                AttachmentLoadOp.DontCare => AttachmentLoadOp.Load,
                AttachmentLoadOp.Clear when preserveClearLoads => AttachmentLoadOp.Load,
                _ => loadOp
            };

        internal void WriteClearValues(ClearValue* destination, uint clearValueCount)
            => WriteClearValues(destination, clearValueCount, _attachmentSignature);

        internal void WriteClearValues(ClearValue* destination, uint clearValueCount, FrameBufferAttachmentSignature[]? signatures)
        {
            if (signatures is null || clearValueCount == 0)
                return;

            var clearColor = Renderer.GetClearColorValue();
            float clearDepth = Renderer.GetClearDepthValue();
            uint clearStencil = Renderer.GetClearStencilValue();

            uint count = Math.Min(clearValueCount, (uint)signatures.Length);
            for (uint i = 0; i < count; i++)
            {
                var sig = signatures[i];
                if (IsColorLikeAttachmentRole(sig.Role))
                {
                    destination[i] = new ClearValue
                    {
                        Color = new ClearColorValue
                        {
                            Float32_0 = clearColor.R,
                            Float32_1 = clearColor.G,
                            Float32_2 = clearColor.B,
                            Float32_3 = clearColor.A
                        }
                    };
                }
                else
                {
                    destination[i] = new ClearValue
                    {
                        DepthStencil = new ClearDepthStencilValue
                        {
                            Depth = clearDepth,
                            Stencil = clearStencil
                        }
                    };
                }
            }
        }

        internal uint WriteClearAttachments(ClearAttachment* destination, bool clearColor, bool clearDepth, bool clearStencil)
        {
            if (_attachmentSignature is null)
                return 0;

            uint count = 0;

            if (clearColor)
            {
                var clearColorValue = Renderer.GetClearColorValue();
                uint colorIndexInSubpass = 0;
                for (int i = 0; i < _attachmentSignature.Length; i++)
                {
                    var sig = _attachmentSignature[i];
                    if (sig.Role != AttachmentRole.Color)
                        continue;

                    destination[count++] = new ClearAttachment
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        ColorAttachment = colorIndexInSubpass,
                        ClearValue = new ClearValue
                        {
                            Color = new ClearColorValue
                            {
                                Float32_0 = clearColorValue.R,
                                Float32_1 = clearColorValue.G,
                                Float32_2 = clearColorValue.B,
                                Float32_3 = clearColorValue.A
                            }
                        }
                    };

                    colorIndexInSubpass++;
                }
            }

            if (clearDepth || clearStencil)
            {
                ImageAspectFlags requestedAspects = ImageAspectFlags.None;
                if (clearDepth)
                    requestedAspects |= ImageAspectFlags.DepthBit;
                if (clearStencil)
                    requestedAspects |= ImageAspectFlags.StencilBit;

                ImageAspectFlags supportedDepthStencilAspects = ImageAspectFlags.None;
                for (int i = 0; i < _attachmentSignature.Length; i++)
                {
                    var sig = _attachmentSignature[i];
                    if (sig.Role is AttachmentRole.Depth or AttachmentRole.Stencil or AttachmentRole.DepthStencil)
                    {
                        supportedDepthStencilAspects = sig.AspectMask;
                        break;
                    }
                }

                ImageAspectFlags aspects = requestedAspects & supportedDepthStencilAspects;
                if (aspects == ImageAspectFlags.None)
                    return count;

                destination[count++] = new ClearAttachment
                {
                    AspectMask = aspects,
                    ClearValue = new ClearValue
                    {
                        DepthStencil = new ClearDepthStencilValue
                        {
                            Depth = Renderer.GetClearDepthValue(),
                            Stencil = Renderer.GetClearStencilValue()
                        }
                    }
                };
            }

            return count;
        }

        public override void Destroy()
        {
            if (!IsActive) return;
            PreDeleted();
            // Defer actual VkFramebuffer destruction — the handle may still be
            // referenced by an in-flight command buffer.  The retirement queue
            // delays VkDestroyFramebuffer until the frame slot's timeline fence
            // signals that the GPU is done with it.
            RetireCachedFrameBufferStates();
            _frameBuffer = default;
            _renderPass = default;
            _attachmentSignature = null;
            _attachmentViews = null;
            _attachmentTargets = null;
            _attachmentExtents = null;
            FramebufferWidth = 0;
            FramebufferHeight = 0;
            FramebufferLayers = 1u;
            MultiviewViewMask = 0u;
            PostDeleted();
        }

        internal bool InvalidateCachedAttachmentState()
        {
            bool hadState = IsActive ||
                _frameBuffer.Handle != 0 ||
                _renderPass.Handle != 0 ||
                _attachmentSignature is not null ||
                _attachmentViews is not null ||
                _attachmentTargets is not null ||
                _attachmentExtents is not null ||
                _cachedFrameBufferStates.Count != 0;
            if (!hadState)
                return false;

            if (IsActive)
            {
                Destroy();
                return true;
            }

            RetireCachedFrameBufferStates();
            _frameBuffer = default;
            _renderPass = default;
            _attachmentSignature = null;
            _attachmentViews = null;
            _attachmentTargets = null;
            _attachmentExtents = null;
            FramebufferWidth = 0;
            FramebufferHeight = 0;
            FramebufferLayers = 1u;
            MultiviewViewMask = 0u;
            return true;
        }

        protected override uint CreateObjectInternal()
        {
            AttachmentBuildInfo[] attachments = BuildAttachmentInfos();
            var (fbWidth, fbHeight) = ResolveFramebufferExtent();
            CachedFrameBufferState state = CreateFrameBufferState(attachments, fbWidth, fbHeight);
            _cachedFrameBufferStates.Add(state);
            ActivateFrameBufferState(state);
            return CacheObject(this);
        }

        private CachedFrameBufferState CreateFrameBufferState(AttachmentBuildInfo[] attachments, uint fbWidth, uint fbHeight)
        {
            ImageView[] views = new ImageView[attachments.Length];
            FrameBufferAttachmentSignature[] signatures = new FrameBufferAttachmentSignature[attachments.Length];
            AttachmentTargetInfo[] targetInfos = new AttachmentTargetInfo[attachments.Length];
            Extent2D[] extents = new Extent2D[attachments.Length];

            for (int i = 0; i < attachments.Length; i++)
            {
                views[i] = attachments[i].View;
                signatures[i] = attachments[i].Signature;
                targetInfos[i] = attachments[i].TargetInfo;
                extents[i] = attachments[i].Extent;
            }

            uint framebufferLayers = ResolveFramebufferLayers(attachments);
            uint multiviewViewMask = ResolveFramebufferMultiviewViewMask(attachments);

            if (Renderer.UseDynamicRenderingRenderTargets)
            {
                return new CachedFrameBufferState(
                    default,
                    default,
                    signatures,
                    views,
                    targetInfos,
                    extents,
                    fbWidth,
                    fbHeight,
                    framebufferLayers,
                    multiviewViewMask);
            }

            RenderPass renderPass = Renderer.GetOrCreateFrameBufferRenderPass(signatures);
            Framebuffer frameBuffer = default;

            fixed (ImageView* viewsPtr = views)
            {
                FramebufferCreateInfo framebufferInfo = new()
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = renderPass,
                    AttachmentCount = (uint)views.Length,
                    PAttachments = viewsPtr,
                    Width = fbWidth,
                    Height = fbHeight,
                    Layers = framebufferLayers,
                };

                if (Api!.CreateFramebuffer(Device, ref framebufferInfo, null, &frameBuffer) != Result.Success)
                    throw new Exception("Failed to create framebuffer.");
            }

            Renderer.RegisterVulkanFramebuffer(
                frameBuffer,
                views,
                $"Framebuffer.{Data?.Name ?? "<unnamed>"}");

            string debugName = string.IsNullOrWhiteSpace(Data?.Name)
                ? $"FBO.0x{frameBuffer.Handle:X}"
                : $"FBO.{Data.Name}";
            Renderer.SetDebugObjectName(
                ObjectType.Framebuffer,
                frameBuffer.Handle,
                $"{debugName}.{fbWidth}x{fbHeight}.Layers{framebufferLayers}");

            return new CachedFrameBufferState(
                frameBuffer,
                renderPass,
                signatures,
                views,
                targetInfos,
                extents,
                fbWidth,
                fbHeight,
                framebufferLayers,
                multiviewViewMask);
        }

        private void ActivateFrameBufferState(CachedFrameBufferState state)
        {
            _frameBuffer = state.FrameBuffer;
            _renderPass = state.RenderPass;
            _attachmentSignature = state.AttachmentSignature;
            _attachmentViews = state.AttachmentViews;
            _attachmentTargets = state.AttachmentTargets;
            _attachmentExtents = state.AttachmentExtents;
            FramebufferWidth = state.FramebufferWidth;
            FramebufferHeight = state.FramebufferHeight;
            FramebufferLayers = state.FramebufferLayers;
            MultiviewViewMask = state.MultiviewViewMask;
        }

        private bool TryActivateCachedFrameBufferState(AttachmentBuildInfo[] attachments, uint fbWidth, uint fbHeight)
        {
            for (int i = 0; i < _cachedFrameBufferStates.Count; i++)
            {
                CachedFrameBufferState state = _cachedFrameBufferStates[i];
                if (!state.Matches(attachments, fbWidth, fbHeight))
                    continue;

                ActivateFrameBufferState(state);
                return true;
            }

            return false;
        }

        private void RetireCachedFrameBufferStates()
        {
            HashSet<ulong> retiredHandles = [];
            for (int i = 0; i < _cachedFrameBufferStates.Count; i++)
            {
                Framebuffer frameBuffer = _cachedFrameBufferStates[i].FrameBuffer;
                if (frameBuffer.Handle == 0 || !retiredHandles.Add(frameBuffer.Handle))
                    continue;

                Renderer.RetireFramebuffer(frameBuffer);
            }

            if (_cachedFrameBufferStates.Count == 0 &&
                _frameBuffer.Handle != 0 &&
                retiredHandles.Add(_frameBuffer.Handle))
            {
                Renderer.RetireFramebuffer(_frameBuffer);
            }

            _cachedFrameBufferStates.Clear();
        }

        private bool AttachmentStateMatches(AttachmentBuildInfo[] attachments, uint fbWidth, uint fbHeight)
        {
            if (_attachmentViews is null || _attachmentSignature is null || _attachmentTargets is null)
                return false;

            if (_attachmentViews.Length != attachments.Length ||
                _attachmentSignature.Length != attachments.Length ||
                _attachmentTargets.Length != attachments.Length)
                return false;

            if (FramebufferWidth != fbWidth || FramebufferHeight != fbHeight ||
                FramebufferLayers != ResolveFramebufferLayers(attachments) ||
                MultiviewViewMask != ResolveFramebufferMultiviewViewMask(attachments))
                return false;

            for (int i = 0; i < attachments.Length; i++)
            {
                if (_attachmentViews[i].Handle != attachments[i].View.Handle)
                    return false;

                if (!_attachmentSignature[i].Equals(attachments[i].Signature))
                    return false;

                if (!_attachmentTargets[i].Equals(attachments[i].TargetInfo))
                    return false;
            }

            return true;
        }

        private (uint Width, uint Height) ResolveFramebufferExtent()
        {
            // Compute framebuffer dimensions accounting for mip-level targets.
            // When an FBO targets a specific mip level (e.g. bloom downsample), the
            // VkFramebuffer width/height must match the mip-level extent, not the base.
            var targets = Data.Targets;
            if (targets is null || targets.Length == 0)
                return (Math.Max(Data.Width, 1u), Math.Max(Data.Height, 1u));

            uint fbWidth = uint.MaxValue;
            uint fbHeight = uint.MaxValue;
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

                fbWidth = Math.Min(fbWidth, width);
                fbHeight = Math.Min(fbHeight, height);
                found = true;
            }

            return found
                ? (fbWidth, fbHeight)
                : (Math.Max(Data.Width, 1u), Math.Max(Data.Height, 1u));
        }

        private string DescribeFrameBuffer()
            => string.IsNullOrWhiteSpace(Data.Name)
                ? $"FBO[{Data.GetHashCode()}]"
                : Data.Name!;

        private FrameBufferAttachmentSignature[] BuildPlannedAttachmentSignature(
            RenderPassMetadata pass,
            string frameBufferName,
            RenderGraphSynchronizationInfo? synchronization)
        {
            FrameBufferAttachmentSignature[] planned = (FrameBufferAttachmentSignature[])_attachmentSignature!.Clone();
            HashSet<int> touchedAttachments = [];
            HashSet<int> writeCapableDepthStencilAttachments = CollectWriteCapableDepthStencilAttachments(planned, pass, frameBufferName);
            string prefix = $"fbo::{frameBufferName}::";

            foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
            {
                if (!usage.IsAttachment)
                    continue;

                if (!usage.ResourceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string slot = usage.ResourceName[prefix.Length..];
                if (string.IsNullOrWhiteSpace(slot))
                    continue;

                int[] matchingIndices = ResolveMatchingAttachmentIndices(planned, slot, usage, pass);
                if (matchingIndices.Length == 0)
                    continue;

                AttachmentLoadOp loadOp = ToVkLoadOp(usage.LoadOp);
                AttachmentStoreOp storeOp = ToVkStoreOp(usage.StoreOp);
                foreach (int index in matchingIndices)
                {
                    FrameBufferAttachmentSignature existing = planned[index];

                    // First-use safety: when an attachment starts in UNDEFINED layout
                    // (i.e. no prior render pass has written to it this frame), Vulkan
                    // cannot preserve prior contents.  Only demote LOAD → DONT_CARE when
                    // the attachment is truly in its first-use Undefined state; subsequent
                    // passes that received an initial-layout override will have a concrete
                    // layout and should keep their LOAD op to preserve content.
                    AttachmentLoadOp effectiveLoadOp = loadOp;
                    if (loadOp == AttachmentLoadOp.Load && existing.InitialLayout == ImageLayout.Undefined)
                        effectiveLoadOp = AttachmentLoadOp.DontCare;

                    FrameBufferAttachmentSignature updated = usage.ResourceType switch
                    {
                        ERenderPassResourceType.ResolveAttachment => WithRoleAndColorIndex(
                            WithOps(
                                existing,
                                AttachmentLoadOp.DontCare,
                                storeOp,
                                existing.StencilLoadOp,
                                existing.StencilStoreOp),
                            AttachmentRole.Resolve,
                            ResolveResolveSourceColorIndex(planned, index, slot, usage, pass)),
                        ERenderPassResourceType.StencilAttachment => WithOps(
                            existing,
                            existing.LoadOp,
                            existing.StoreOp,
                            effectiveLoadOp,
                            storeOp),
                        _ => WithOps(
                            existing,
                            effectiveLoadOp,
                            storeOp,
                            existing.StencilLoadOp,
                            existing.StencilStoreOp),
                    };
                    updated = WithReferenceLayout(
                        updated,
                        ResolveAttachmentReferenceLayout(updated, usage, writeCapableDepthStencilAttachments.Contains(index)));
                    ImageLayout finalLayout = ResolveAttachmentFinalLayoutFromNextConsumer(synchronization, pass, usage, updated);
                    if (finalLayout != ImageLayout.Undefined)
                        updated = WithFinalLayout(updated, finalLayout);

                    planned[index] = updated;
                    touchedAttachments.Add(index);
                }
            }

            ValidateAttachmentSampleCounts(planned, touchedAttachments, pass, frameBufferName);
            ValidateResolveAttachmentPairings(planned, pass, frameBufferName);
            return planned;
        }

        private static HashSet<int> CollectWriteCapableDepthStencilAttachments(
            FrameBufferAttachmentSignature[] signatures,
            RenderPassMetadata pass,
            string frameBufferName)
        {
            HashSet<int> result = [];
            string prefix = $"fbo::{frameBufferName}::";

            foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
            {
                if (!usage.IsAttachment ||
                    usage.ResourceType is not (ERenderPassResourceType.DepthAttachment or ERenderPassResourceType.StencilAttachment) ||
                    usage.Access == ERenderGraphAccess.Read ||
                    !usage.ResourceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string slot = usage.ResourceName[prefix.Length..];
                if (string.IsNullOrWhiteSpace(slot))
                    continue;

                foreach (int index in ResolveMatchingAttachmentIndices(signatures, slot, usage, pass))
                    result.Add(index);
            }

            return result;
        }

        private static int[] ResolveMatchingAttachmentIndices(
            FrameBufferAttachmentSignature[] signatures,
            string slot,
            RenderPassResourceUsage usage,
            RenderPassMetadata pass)
        {
            if (slot.StartsWith("resolve", StringComparison.OrdinalIgnoreCase))
            {
                if (usage.ResourceType != ERenderPassResourceType.ResolveAttachment)
                {
                    throw new InvalidOperationException(
                        $"Render pass '{pass.Name}' declares '{usage.ResourceType}' for slot '{slot}', but resolve slots require resolve usage.");
                }

                if (slot.Length <= 7 || !uint.TryParse(slot.AsSpan(7), out uint resolveTargetColorIndex))
                {
                    throw new InvalidOperationException(
                        $"Render pass '{pass.Name}' references invalid resolve slot '{slot}'. Use 'resolveN' where N is the target color attachment index.");
                }

                for (int i = 0; i < signatures.Length; i++)
                {
                    FrameBufferAttachmentSignature signature = signatures[i];
                    if (signature.Role == AttachmentRole.Color && signature.ColorIndex == resolveTargetColorIndex)
                        return [i];
                }

                throw new InvalidOperationException(
                    $"Render pass '{pass.Name}' references resolve slot '{resolveTargetColorIndex}' but framebuffer has no matching color attachment target.");
            }

            if (slot.StartsWith("color", StringComparison.OrdinalIgnoreCase))
            {
                if (usage.ResourceType is not ERenderPassResourceType.ColorAttachment and not ERenderPassResourceType.ResolveAttachment)
                {
                    throw new InvalidOperationException(
                        $"Render pass '{pass.Name}' declares '{usage.ResourceType}' for slot '{slot}', but color slots require color/resolve usage.");
                }

                bool hasExplicitIndex = false;
                uint colorIndex = 0;
                if (slot.Length > 5)
                {
                    if (!uint.TryParse(slot.AsSpan(5), out colorIndex))
                    {
                        throw new InvalidOperationException(
                            $"Render pass '{pass.Name}' references invalid color slot '{slot}'. Use 'color' or 'colorN'.");
                    }

                    hasExplicitIndex = true;
                }
                List<int> colorMatches = [];
                for (int i = 0; i < signatures.Length; i++)
                {
                    FrameBufferAttachmentSignature signature = signatures[i];
                    if (signature.Role != AttachmentRole.Color)
                        continue;

                    if (hasExplicitIndex && signature.ColorIndex != colorIndex)
                        continue;

                    colorMatches.Add(i);
                }

                if (colorMatches.Count == 0)
                {
                    string suffix = hasExplicitIndex ? colorIndex.ToString() : "any";
                    throw new InvalidOperationException(
                        $"Render pass '{pass.Name}' references color slot '{suffix}' but framebuffer has no matching color attachment.");
                }

                return [.. colorMatches];
            }

            if (slot.Equals("depth", StringComparison.OrdinalIgnoreCase))
            {
                if (usage.ResourceType is not ERenderPassResourceType.DepthAttachment and not ERenderPassResourceType.StencilAttachment)
                {
                    throw new InvalidOperationException(
                        $"Render pass '{pass.Name}' declares '{usage.ResourceType}' for depth slot '{slot}'.");
                }

                for (int i = 0; i < signatures.Length; i++)
                {
                    FrameBufferAttachmentSignature signature = signatures[i];
                    if (signature.Role is not (AttachmentRole.Depth or AttachmentRole.DepthStencil))
                        continue;

                    if (usage.ResourceType == ERenderPassResourceType.StencilAttachment &&
                        (signature.AspectMask & ImageAspectFlags.StencilBit) == 0)
                    {
                        throw new InvalidOperationException(
                            $"Render pass '{pass.Name}' expects stencil access on depth slot, but attachment has no stencil aspect.");
                    }

                    return [i];
                }

                if (usage.ResourceType == ERenderPassResourceType.StencilAttachment)
                    return [];

                throw new InvalidOperationException(
                    $"Render pass '{pass.Name}' references depth slot '{slot}' but framebuffer has no depth attachment.");
            }

            if (slot.Equals("stencil", StringComparison.OrdinalIgnoreCase))
            {
                if (usage.ResourceType is not ERenderPassResourceType.StencilAttachment and not ERenderPassResourceType.DepthAttachment)
                {
                    throw new InvalidOperationException(
                        $"Render pass '{pass.Name}' declares '{usage.ResourceType}' for stencil slot '{slot}'.");
                }

                for (int i = 0; i < signatures.Length; i++)
                {
                    FrameBufferAttachmentSignature signature = signatures[i];
                    if (signature.Role is not (AttachmentRole.Stencil or AttachmentRole.DepthStencil))
                        continue;

                    if ((signature.AspectMask & ImageAspectFlags.StencilBit) == 0)
                    {
                        throw new InvalidOperationException(
                            $"Render pass '{pass.Name}' references stencil slot but the attachment has no stencil aspect.");
                    }

                    return [i];
                }

                if (usage.ResourceType == ERenderPassResourceType.StencilAttachment)
                    return [];

                throw new InvalidOperationException(
                    $"Render pass '{pass.Name}' references stencil slot '{slot}' but framebuffer has no stencil attachment.");
            }

            throw new InvalidOperationException(
                $"Render pass '{pass.Name}' references unsupported framebuffer slot '{slot}'. Expected color/resolve/depth/stencil.");
        }

        private static uint ResolveResolveSourceColorIndex(
            FrameBufferAttachmentSignature[] signatures,
            int resolveTargetAttachmentIndex,
            string slot,
            RenderPassResourceUsage usage,
            RenderPassMetadata pass)
        {
            if (usage.ResolveSourceColorIndex is { } explicitSource)
                return explicitSource;

            if (slot.StartsWith("resolve", StringComparison.OrdinalIgnoreCase))
            {
                if (slot.Length > 7 && uint.TryParse(slot.AsSpan(7), out uint parsedSource))
                    return parsedSource;
            }

            if (slot.StartsWith("color", StringComparison.OrdinalIgnoreCase) &&
                slot.Length > 5 &&
                uint.TryParse(slot.AsSpan(5), out uint colorSlot))
            {
                return colorSlot;
            }

            FrameBufferAttachmentSignature resolveTarget = signatures[resolveTargetAttachmentIndex];
            uint inferredSource = 0;
            bool foundSource = false;
            for (int i = 0; i < signatures.Length; i++)
            {
                if (i == resolveTargetAttachmentIndex)
                    continue;

                FrameBufferAttachmentSignature candidate = signatures[i];
                if (candidate.Role != AttachmentRole.Color ||
                    candidate.Samples == SampleCountFlags.Count1Bit ||
                    candidate.Format != resolveTarget.Format)
                {
                    continue;
                }

                if (foundSource)
                {
                    throw new InvalidOperationException(
                        $"Render pass '{pass.Name}' resolve attachment '{usage.ResourceName}' is ambiguous. Use UseResolveAttachment(resourceName, sourceColorIndex, ...) to select the multisampled color source.");
                }

                inferredSource = candidate.ColorIndex;
                foundSource = true;
            }

            if (!foundSource)
            {
                throw new InvalidOperationException(
                    $"Render pass '{pass.Name}' resolve attachment '{usage.ResourceName}' has no explicit source color index and no unique multisampled color source could be inferred.");
            }

            return inferredSource;
        }

        private static void ValidateAttachmentSampleCounts(
            FrameBufferAttachmentSignature[] signatures,
            HashSet<int> touchedAttachments,
            RenderPassMetadata pass,
            string frameBufferName)
        {
            if (touchedAttachments.Count <= 1)
                return;

            bool hasBaseline = false;
            SampleCountFlags baseline = SampleCountFlags.Count1Bit;

            foreach (int index in touchedAttachments)
            {
                FrameBufferAttachmentSignature signature = signatures[index];
                if (signature.Role == AttachmentRole.Resolve)
                    continue;

                if (!hasBaseline)
                {
                    baseline = signature.Samples;
                    hasBaseline = true;
                    continue;
                }

                if (signature.Samples != baseline)
                {
                    throw new InvalidOperationException(
                        $"Render pass '{pass.Name}' references framebuffer '{frameBufferName}' attachments with mismatched sample counts ({baseline} vs {signature.Samples}).");
                }
            }
        }

        private static void ValidateResolveAttachmentPairings(
            FrameBufferAttachmentSignature[] signatures,
            RenderPassMetadata pass,
            string frameBufferName)
        {
            for (int i = 0; i < signatures.Length; i++)
            {
                FrameBufferAttachmentSignature resolve = signatures[i];
                if (resolve.Role != AttachmentRole.Resolve)
                    continue;

                int sourceIndex = -1;
                for (int j = 0; j < signatures.Length; j++)
                {
                    if (signatures[j].Role == AttachmentRole.Color &&
                        signatures[j].ColorIndex == resolve.ColorIndex)
                    {
                        sourceIndex = j;
                        break;
                    }
                }

                if (sourceIndex < 0)
                {
                    throw new InvalidOperationException(
                        $"Render pass '{pass.Name}' framebuffer '{frameBufferName}' has a resolve attachment for color {resolve.ColorIndex}, but no matching color source.");
                }

                FrameBufferAttachmentSignature source = signatures[sourceIndex];
                if (source.Samples == SampleCountFlags.Count1Bit)
                {
                    throw new InvalidOperationException(
                        $"Render pass '{pass.Name}' framebuffer '{frameBufferName}' resolve source color {resolve.ColorIndex} is single-sampled. Vulkan resolve sources must be multisampled.");
                }

                if (resolve.Samples != SampleCountFlags.Count1Bit)
                {
                    throw new InvalidOperationException(
                        $"Render pass '{pass.Name}' framebuffer '{frameBufferName}' resolve target for color {resolve.ColorIndex} is {resolve.Samples}. Vulkan resolve targets must be single-sampled.");
                }

                if (source.Format != resolve.Format || source.AspectMask != resolve.AspectMask)
                {
                    throw new InvalidOperationException(
                        $"Render pass '{pass.Name}' framebuffer '{frameBufferName}' resolve target for color {resolve.ColorIndex} has format/aspect {resolve.Format}/{resolve.AspectMask}, but source has {source.Format}/{source.AspectMask}.");
                }
            }
        }

        private static FrameBufferAttachmentSignature WithOps(
            FrameBufferAttachmentSignature signature,
            AttachmentLoadOp loadOp,
            AttachmentStoreOp storeOp,
            AttachmentLoadOp stencilLoadOp,
            AttachmentStoreOp stencilStoreOp)
            => new(
                signature.Format,
                signature.Samples,
                signature.AspectMask,
                signature.Role,
                signature.ColorIndex,
                loadOp,
                storeOp,
                stencilLoadOp,
                stencilStoreOp,
                signature.InitialLayout,
                signature.FinalLayout,
                signature.ReferenceLayout);

        private static FrameBufferAttachmentSignature WithReferenceLayout(
            FrameBufferAttachmentSignature signature,
            ImageLayout referenceLayout)
            => new(
                signature.Format,
                signature.Samples,
                signature.AspectMask,
                signature.Role,
                signature.ColorIndex,
                signature.LoadOp,
                signature.StoreOp,
                signature.StencilLoadOp,
                signature.StencilStoreOp,
                signature.InitialLayout,
                signature.FinalLayout,
                referenceLayout);

        private static FrameBufferAttachmentSignature WithFinalLayout(
            FrameBufferAttachmentSignature signature,
            ImageLayout finalLayout)
            => new(
                signature.Format,
                signature.Samples,
                signature.AspectMask,
                signature.Role,
                signature.ColorIndex,
                signature.LoadOp,
                signature.StoreOp,
                signature.StencilLoadOp,
                signature.StencilStoreOp,
                signature.InitialLayout,
                finalLayout,
                signature.ReferenceLayout);

        private static FrameBufferAttachmentSignature WithRoleAndColorIndex(
            FrameBufferAttachmentSignature signature,
            AttachmentRole role,
            uint colorIndex)
            => new(
                signature.Format,
                signature.Samples,
                signature.AspectMask,
                role,
                colorIndex,
                signature.LoadOp,
                signature.StoreOp,
                signature.StencilLoadOp,
                signature.StencilStoreOp,
                signature.InitialLayout,
                signature.FinalLayout,
                signature.ReferenceLayout);

        private static ImageLayout ResolveAttachmentFinalLayoutFromNextConsumer(
            RenderGraphSynchronizationInfo? synchronization,
            RenderPassMetadata pass,
            RenderPassResourceUsage usage,
            FrameBufferAttachmentSignature signature)
        {
            if (synchronization is null)
                return ImageLayout.Undefined;

            foreach (RenderGraphSynchronizationEdge edge in synchronization.Edges)
            {
                if (edge.DependencyOnly ||
                    edge.ProducerPassIndex != pass.PassIndex ||
                    string.IsNullOrEmpty(edge.ResourceName) ||
                    !edge.ResourceName.Equals(usage.ResourceName, StringComparison.OrdinalIgnoreCase) ||
                    !SubresourceRangesOverlap(edge.SubresourceRange, usage.SubresourceRange) ||
                    edge.ConsumerState.Layout is not { } nextLayout)
                    continue;

                ImageLayout finalLayout = ToVkImageLayout(nextLayout, signature);
                if (finalLayout != ImageLayout.Undefined)
                    return finalLayout;
            }

            return ImageLayout.Undefined;
        }

        private static bool SubresourceRangesOverlap(
            RenderGraphSubresourceRange first,
            RenderGraphSubresourceRange second) => 
                first.IsWholeResource || 
                second.IsWholeResource || 
                (RangesOverlap(first.BaseMipLevel, first.MipLevelCount, second.BaseMipLevel, second.MipLevelCount) &&
                RangesOverlap(first.BaseArrayLayer, first.ArrayLayerCount, second.BaseArrayLayer, second.ArrayLayerCount));

        private static bool RangesOverlap(uint firstStart, uint firstCount, uint secondStart, uint secondCount)
        {
            ulong firstEnd = firstCount == RenderGraphSubresourceRange.Remaining
                ? ulong.MaxValue
                : (ulong)firstStart + firstCount;
            ulong secondEnd = secondCount == RenderGraphSubresourceRange.Remaining
                ? ulong.MaxValue
                : (ulong)secondStart + secondCount;

            return (ulong)firstStart < secondEnd && (ulong)secondStart < firstEnd;
        }

        private static ImageLayout ToVkImageLayout(
            RenderGraphImageLayout layout,
            FrameBufferAttachmentSignature signature)
            => layout switch
            {
                RenderGraphImageLayout.ColorAttachment => IsColorLikeAttachmentRole(signature.Role)
                    ? ImageLayout.ColorAttachmentOptimal
                    : ImageLayout.DepthStencilAttachmentOptimal,
                RenderGraphImageLayout.DepthStencilAttachment => IsColorLikeAttachmentRole(signature.Role)
                    ? ImageLayout.ColorAttachmentOptimal
                    : ImageLayout.DepthStencilAttachmentOptimal,
                RenderGraphImageLayout.RenderingLocalRead => ImageLayout.RenderingLocalRead,
                RenderGraphImageLayout.ShaderReadOnly => IsColorLikeAttachmentRole(signature.Role)
                    ? ImageLayout.ShaderReadOnlyOptimal
                    : ImageLayout.DepthStencilReadOnlyOptimal,
                RenderGraphImageLayout.General => ImageLayout.General,
                RenderGraphImageLayout.TransferSource => ImageLayout.TransferSrcOptimal,
                RenderGraphImageLayout.TransferDestination => ImageLayout.TransferDstOptimal,
                RenderGraphImageLayout.Present => ImageLayout.PresentSrcKhr,
                _ => ImageLayout.Undefined
            };

        private static ImageLayout ResolveAttachmentReferenceLayout(
            FrameBufferAttachmentSignature signature,
            RenderPassResourceUsage usage,
            bool passHasWriteCapableDepthStencilUsage)
            => IsColorLikeAttachmentRole(signature.Role)
                ? ImageLayout.ColorAttachmentOptimal
                : usage.Access == ERenderGraphAccess.Read && !passHasWriteCapableDepthStencilUsage
                ? ImageLayout.DepthStencilReadOnlyOptimal
                : ImageLayout.DepthStencilAttachmentOptimal;

        private static AttachmentLoadOp ToVkLoadOp(ERenderPassLoadOp op)
            => op switch
            {
                ERenderPassLoadOp.Clear => AttachmentLoadOp.Clear,
                ERenderPassLoadOp.DontCare => AttachmentLoadOp.DontCare,
                _ => AttachmentLoadOp.Load
            };

        private static AttachmentStoreOp ToVkStoreOp(ERenderPassStoreOp op)
            => op == ERenderPassStoreOp.DontCare
                ? AttachmentStoreOp.DontCare
                : AttachmentStoreOp.Store;

        private static bool SignatureEquals(FrameBufferAttachmentSignature[] first, FrameBufferAttachmentSignature[] second)
        {
            if (first.Length != second.Length)
                return false;

            for (int i = 0; i < first.Length; i++)
                if (!first[i].Equals(second[i]))
                    return false;

            return true;
        }

        private static bool UsesReadOnlyDepthStencil(FrameBufferAttachmentSignature[] signatures)
        {
            foreach (FrameBufferAttachmentSignature signature in signatures)
            {
                if (IsColorLikeAttachmentRole(signature.Role))
                    continue;

                if (signature.ReferenceLayout is ImageLayout.DepthStencilReadOnlyOptimal or ImageLayout.DepthReadOnlyOptimal)
                    return true;
            }

            return false;
        }

        private AttachmentBuildInfo[] BuildAttachmentInfos()
        {
            var targets = Data.Targets;
            if (targets is null || targets.Length == 0)
                throw new InvalidOperationException("Framebuffer must have at least one attachment.");

            List<AttachmentBuildInfo> colorAttachments = [];
            AttachmentBuildInfo? depthAttachment = null;
            HashSet<uint> usedColorSlots = [];
            uint nextImplicitColorSlot = 0;

            foreach (var (target, attachment, mip, layer) in targets)
            {
                if (target is null)
                    throw new InvalidOperationException("Framebuffer attachment target cannot be null.");

                ValidateAttachmentDimensions(target);

                AttachmentSource source = ResolveAttachmentSource(target, attachment, mip, layer);
                AttachmentRole role = ResolveAttachmentRole(attachment, source.AspectMask, source.Format);

                if (role == AttachmentRole.Color)
                {
                    uint slot = ResolveColorSlot(attachment, ref nextImplicitColorSlot, usedColorSlots);
                    FrameBufferAttachmentSignature signature = BuildAttachmentSignature(source, role, slot);
                    colorAttachments.Add(new AttachmentBuildInfo(
                        new AttachmentTargetInfo(target, attachment, mip, layer),
                        source.View,
                        signature,
                        slot,
                        source.Extent,
                        source.LayerCount,
                        source.MultiviewViewMask));
                    continue;
                }

                if (depthAttachment.HasValue)
                    throw new InvalidOperationException($"Framebuffer '{Data.Name ?? "<unnamed>"}' defines multiple depth/stencil attachments which is not supported in Vulkan subpasses.");

                FrameBufferAttachmentSignature depthSignature = BuildAttachmentSignature(source, role, 0);
                depthAttachment = new AttachmentBuildInfo(
                    new AttachmentTargetInfo(target, attachment, mip, layer),
                    source.View,
                    depthSignature,
                    0,
                    source.Extent,
                    source.LayerCount,
                    source.MultiviewViewMask);
            }

            colorAttachments.Sort((a, b) => a.ColorIndex.CompareTo(b.ColorIndex));

            List<AttachmentBuildInfo> ordered = new(colorAttachments.Count + (depthAttachment.HasValue ? 1 : 0));
            ordered.AddRange(colorAttachments);
            if (depthAttachment.HasValue)
                ordered.Add(depthAttachment.Value);

            if (ordered.Count == 0)
                throw new InvalidOperationException($"Framebuffer '{Data.Name ?? "<unnamed>"}' does not define any attachments.");

            return [.. ordered];
        }

        private void ValidateAttachmentDimensions(IFrameBufferAttachement attachment)
        {
            uint expectedWidth = Math.Max(Data.Width, 1u);
            uint expectedHeight = Math.Max(Data.Height, 1u);
            if (attachment.Width == expectedWidth && attachment.Height == expectedHeight)
                return;

            throw new InvalidOperationException(
                $"Attachment '{DescribeAttachment(attachment)}' size ({attachment.Width}x{attachment.Height}) does not match framebuffer dimensions ({expectedWidth}x{expectedHeight}).");
        }

        private static Extent2D ResolveAttachmentExtent(IFrameBufferAttachement attachment, int mipLevel)
        {
            uint width = Math.Max(attachment.Width, 1u);
            uint height = Math.Max(attachment.Height, 1u);
            int mip = Math.Max(mipLevel, 0);
            if (mip > 0)
            {
                width = Math.Max(width >> mip, 1u);
                height = Math.Max(height >> mip, 1u);
            }

            return new Extent2D(width, height);
        }

        private static string DescribeAttachment(IFrameBufferAttachement attachment)
            => attachment switch
            {
                XRTexture texture => texture.Name ?? texture.GetDescribingName(),
                XRRenderBuffer renderBuffer => renderBuffer.Name ?? renderBuffer.GetType().Name,
                _ => attachment.GetType().Name
            } ?? attachment.GetType().Name;

        private AttachmentSource ResolveAttachmentSource(IFrameBufferAttachement target, EFrameBufferAttachment attachment, int mipLevel, int layerIndex)
            => target switch
            {
                XRTexture texture => ResolveTextureAttachment(target, texture, attachment, mipLevel, layerIndex),
                XRRenderBuffer renderBuffer => ResolveRenderBufferAttachment(renderBuffer),
                _ => throw new NotSupportedException($"Framebuffer attachment type '{target.GetType().Name}' is not supported yet.")
            };

        private AttachmentSource ResolveRenderBufferAttachment(XRRenderBuffer renderBuffer)
        {
            if (Renderer.GetOrCreateAPIRenderObject(renderBuffer) is not VkRenderBuffer vkRenderBuffer)
                throw new InvalidOperationException("Render buffer is not backed by a Vulkan object.");

            vkRenderBuffer.Generate();
            vkRenderBuffer.RefreshIfStale();
            ImageAspectFlags aspect = NormalizeAttachmentAspectMask(vkRenderBuffer.Format, vkRenderBuffer.Aspect);
            return new AttachmentSource(
                vkRenderBuffer.View,
                vkRenderBuffer.Format,
                vkRenderBuffer.Samples,
                aspect,
                (aspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0
                    ? ImageUsageFlags.DepthStencilAttachmentBit
                    : ImageUsageFlags.ColorAttachmentBit,
                1u,
                0u,
                vkRenderBuffer.ResolveAttachmentExtent());
        }

        private AttachmentSource ResolveTextureAttachment(IFrameBufferAttachement textureAttachment, XRTexture texture, EFrameBufferAttachment attachment, int mipLevel, int layerIndex)
        {
            if (Renderer.GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkFrameBufferAttachmentSource source)
                throw new InvalidOperationException($"Texture '{texture.Name ?? texture.GetDescribingName()}' is not backed by a Vulkan texture.");

            bool depthStencilAttachment = attachment is EFrameBufferAttachment.DepthAttachment
                or EFrameBufferAttachment.DepthStencilAttachment
                or EFrameBufferAttachment.StencilAttachment;
            source.EnsureAttachmentLayout(depthStencilAttachment);

            ImageView view = source.GetAttachmentView(mipLevel, layerIndex);
            if (view.Handle == 0)
                throw new InvalidOperationException(
                    $"Texture '{texture.Name ?? texture.GetDescribingName()}' could not provide a Vulkan image view for framebuffer attachment '{attachment}'.");

            ImageAspectFlags aspect = NormalizeAttachmentAspectMask(source.DescriptorFormat, source.DescriptorAspect);
            ImageUsageFlags usage = source.DescriptorUsage;
            if (VkFormatConversions.IsDepthStencilFormat(source.DescriptorFormat))
            {
                usage &= ~ImageUsageFlags.ColorAttachmentBit;
                usage |= ImageUsageFlags.DepthStencilAttachmentBit;
            }

            uint layerCount = layerIndex < 0
                ? Math.Max(source.DescriptorArrayLayers, 1u)
                : 1u;
            uint multiviewViewMask = 0u;
            if (layerIndex < 0 && IsTextureArrayAttachment(texture))
            {
                if (TryGetTextureArrayMultiviewParameters(texture, out XRTexture2DArray.OVRMultiView? ovr))
                {
                    multiviewViewMask = BuildMultiviewViewMask(ovr.Offset, ovr.NumViews, layerCount);
                }
                else if (IsStereoCompatibleTextureArrayAttachment(texture, layerCount))
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.Framebuffer.RecoveredStereoMultiview.{texture.Name ?? texture.GetDescribingName()}",
                        TimeSpan.FromSeconds(2),
                        "[Vulkan] Recovering stereo multiview mask for texture-array framebuffer attachment '{0}' from its stereo-compatible resource descriptor because OVR multiview metadata is missing.",
                        texture.Name ?? texture.GetDescribingName());
                    multiviewViewMask = BuildMultiviewViewMask(0, 2u, layerCount);
                }
                else if (layerCount >= 2u && RuntimeEngine.Rendering.State.IsStereoPass)
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.Framebuffer.RecoveredStereoMultiview.State.{texture.Name ?? texture.GetDescribingName()}",
                        TimeSpan.FromSeconds(2),
                        "[Vulkan] Recovering stereo multiview mask for texture-array framebuffer attachment '{0}' because render state is stereo but OVR multiview metadata is missing.",
                        texture.Name ?? texture.GetDescribingName());
                    multiviewViewMask = BuildMultiviewViewMask(0, 2u, layerCount);
                }
            }

            Extent2D extent = source.TryGetAttachmentExtent(mipLevel, layerIndex, out Extent2D resolvedExtent)
                ? resolvedExtent
                : ResolveAttachmentExtent(textureAttachment, mipLevel);

            return new AttachmentSource(view, source.DescriptorFormat, source.DescriptorSamples, aspect, usage, layerCount, multiviewViewMask, extent);
        }

        private static uint BuildMultiviewViewMask(XRTexture2DArray.OVRMultiView multiview, uint availableLayers)
            => BuildMultiviewViewMask(multiview.Offset, multiview.NumViews, availableLayers);

        private static bool IsTextureArrayAttachment(XRTexture texture)
            => texture is XRTexture2DArray or XRTexture2DArrayView;

        private static bool TryGetTextureArrayMultiviewParameters(
            XRTexture texture,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out XRTexture2DArray.OVRMultiView? multiview)
        {
            multiview = texture switch
            {
                XRTexture2DArray textureArray => textureArray.OVRMultiViewParameters,
                XRTexture2DArrayView textureArrayView => textureArrayView.ViewedTexture.OVRMultiViewParameters,
                _ => null
            };

            return multiview is { NumViews: > 1u };
        }

        internal static bool IsStereoCompatibleTextureArrayAttachment(XRTexture texture, uint layerCount)
        {
            uint effectiveLayerCount = layerCount;
            XRTexture descriptorTexture = texture;
            if (texture is XRTexture2DArray textureArray)
            {
                effectiveLayerCount = Math.Max(effectiveLayerCount, textureArray.Depth);
            }
            else if (texture is XRTexture2DArrayView textureArrayView)
            {
                descriptorTexture = textureArrayView.ViewedTexture;
                effectiveLayerCount = Math.Max(effectiveLayerCount, textureArrayView.NumLayers);
            }
            else
            {
                return false;
            }

            if (effectiveLayerCount < 2u)
                return false;

            string? textureName = descriptorTexture.Name ?? texture.Name;
            if (string.IsNullOrWhiteSpace(textureName))
                return false;

            RenderResourceRegistry? registry = RuntimeEngine.Rendering.State.CurrentResourceRegistry;
            if (registry is null)
                return false;

            if (!registry.TextureRecords.TryGetValue(textureName, out RenderTextureResource? record))
                return false;

            TextureResourceDescriptor descriptor = record.Descriptor;
            uint descriptorLayers = Math.Max(descriptor.ArrayLayers, descriptor.LayerCount);
            return descriptor.StereoCompatible && descriptorLayers >= 2u;
        }

        private static uint BuildMultiviewViewMask(int offset, uint numViews, uint availableLayers)
        {
            if (numViews <= 1u || availableLayers == 0u)
                return 0u;

            uint firstLayer = (uint)Math.Max(offset, 0);
            if (firstLayer >= availableLayers)
                return 0u;

            uint viewCount = Math.Min(numViews, availableLayers - firstLayer);
            uint viewMask = 0u;
            for (uint i = 0u; i < viewCount; i++)
            {
                uint layer = firstLayer + i;
                if (layer >= 32u)
                    break;

                viewMask |= 1u << (int)layer;
            }

            return viewMask;
        }

        private static AttachmentRole ResolveAttachmentRole(EFrameBufferAttachment attachment, ImageAspectFlags aspect, Format format)
        {
            if (attachment == EFrameBufferAttachment.DepthAttachment)
                return AttachmentRole.Depth;
            if (attachment == EFrameBufferAttachment.DepthStencilAttachment)
                return AttachmentRole.DepthStencil;
            if (attachment == EFrameBufferAttachment.StencilAttachment)
                return AttachmentRole.Stencil;

            ImageAspectFlags normalizedAspect = NormalizeAttachmentAspectMask(format, aspect);
            return normalizedAspect switch
            {
                _ when (normalizedAspect & ImageAspectFlags.DepthBit) != 0 && (normalizedAspect & ImageAspectFlags.StencilBit) != 0 => AttachmentRole.DepthStencil,
                _ when (normalizedAspect & ImageAspectFlags.DepthBit) != 0 => AttachmentRole.Depth,
                _ when (normalizedAspect & ImageAspectFlags.StencilBit) != 0 => AttachmentRole.Stencil,
                _ => AttachmentRole.Color
            };
        }

        private static uint ResolveColorSlot(EFrameBufferAttachment attachment, ref uint nextImplicitSlot, HashSet<uint> usedSlots)
        {
            if (TryGetExplicitColorIndex(attachment, out uint explicitSlot))
            {
                if (!usedSlots.Add(explicitSlot))
                    throw new InvalidOperationException($"Color attachment slot {explicitSlot} is already bound for this framebuffer.");

                nextImplicitSlot = Math.Max(nextImplicitSlot, explicitSlot + 1);
                return explicitSlot;
            }

            uint assignedSlot = nextImplicitSlot++;
            while (!usedSlots.Add(assignedSlot))
                assignedSlot = nextImplicitSlot++;

            return assignedSlot;
        }

        private static bool TryGetExplicitColorIndex(EFrameBufferAttachment attachment, out uint index)
        {
            if (attachment >= EFrameBufferAttachment.ColorAttachment0 && attachment <= EFrameBufferAttachment.ColorAttachment31)
            {
                index = (uint)(attachment - EFrameBufferAttachment.ColorAttachment0);
                return true;
            }

            index = 0;
            return false;
        }

        private static FrameBufferAttachmentSignature BuildAttachmentSignature(AttachmentSource source, AttachmentRole role, uint colorIndex)
        {
            ImageAspectFlags aspectMask = NormalizeAttachmentAspectMask(source.Format, source.AspectMask);
            bool hasStencil = (aspectMask & ImageAspectFlags.StencilBit) != 0;
            AttachmentLoadOp stencilLoad = AttachmentLoadOp.DontCare;
            AttachmentStoreOp stencilStore = hasStencil ? AttachmentStoreOp.Store : AttachmentStoreOp.DontCare;

            // Sampled attachments must leave the pass in a descriptor-compatible
            // layout. Render-graph passes can infer this from declared consumers,
            // but off-graph FBOs such as shadow maps still need a safe default.
            ImageLayout finalLayout = IsColorLikeAttachmentRole(role)
                ? (source.Usage & ImageUsageFlags.SampledBit) != 0
                    ? ImageLayout.ShaderReadOnlyOptimal
                    : ImageLayout.ColorAttachmentOptimal
                : (source.Usage & ImageUsageFlags.SampledBit) != 0
                    ? ImageLayout.DepthStencilReadOnlyOptimal
                    : ImageLayout.DepthStencilAttachmentOptimal;

            // The reference layout is the layout the attachment holds WHILE the framebuffer
            // is bound for rendering. Depth/stencil attachments must be writable during
            // rendering so the geometry passes that populate them (deferred GBuffer, forward
            // opaque/masked) can clear and write depth — even when the texture is also sampled
            // by later passes through a DepthView alias. Forcing a read-only reference layout
            // here silently drops every depth write, which leaves the shared depth buffer empty
            // and lets the forward skybox overwrite all deferred geometry.
            //
            // The final layout above still transitions sampled depth to read-only at pass end,
            // so subsequent sampled descriptors match the image layout. Passes that genuinely
            // require read-only depth (sampling the same depth they test against) opt in via
            // render-pass metadata, which overrides this reference layout to read-only.
            ImageLayout referenceLayout = IsColorLikeAttachmentRole(role)
                ? ImageLayout.ColorAttachmentOptimal
                : ImageLayout.DepthStencilAttachmentOptimal;

            return new FrameBufferAttachmentSignature(
                source.Format,
                source.Samples,
                aspectMask,
                role,
                colorIndex,
                AttachmentLoadOp.DontCare,
                AttachmentStoreOp.Store,
                stencilLoad,
                stencilStore,
                ImageLayout.Undefined,
                finalLayout,
                referenceLayout);
        }

        private static ImageAspectFlags NormalizeAttachmentAspectMask(Format format, ImageAspectFlags requested)
        {
            if (!VkFormatConversions.IsDepthStencilFormat(format))
            {
                ImageAspectFlags colorMask = requested & ImageAspectFlags.ColorBit;
                return colorMask != ImageAspectFlags.None ? colorMask : ImageAspectFlags.ColorBit;
            }

            ImageAspectFlags supported = format switch
            {
                Format.S8Uint => ImageAspectFlags.StencilBit,
                Format.D16UnormS8Uint or Format.D24UnormS8Uint or Format.D32SfloatS8Uint =>
                    ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
                _ => ImageAspectFlags.DepthBit
            };

            ImageAspectFlags normalized = requested & supported;
            return normalized != ImageAspectFlags.None ? normalized : supported;
        }

        private sealed class CachedFrameBufferState(
            Framebuffer frameBuffer,
            RenderPass renderPass,
            FrameBufferAttachmentSignature[] attachmentSignature,
            ImageView[] attachmentViews,
            AttachmentTargetInfo[] attachmentTargets,
            Extent2D[] attachmentExtents,
            uint framebufferWidth,
            uint framebufferHeight,
            uint framebufferLayers,
            uint multiviewViewMask)
        {
            public Framebuffer FrameBuffer { get; } = frameBuffer;
            public RenderPass RenderPass { get; } = renderPass;
            public FrameBufferAttachmentSignature[] AttachmentSignature { get; } = attachmentSignature;
            public ImageView[] AttachmentViews { get; } = attachmentViews;
            public AttachmentTargetInfo[] AttachmentTargets { get; } = attachmentTargets;
            public Extent2D[] AttachmentExtents { get; } = attachmentExtents;
            public uint FramebufferWidth { get; } = framebufferWidth;
            public uint FramebufferHeight { get; } = framebufferHeight;
            public uint FramebufferLayers { get; } = framebufferLayers;
            public uint MultiviewViewMask { get; } = multiviewViewMask;

            public bool Matches(AttachmentBuildInfo[] attachments, uint width, uint height)
            {
                if (FramebufferWidth != width ||
                    FramebufferHeight != height ||
                    FramebufferLayers != ResolveFramebufferLayers(attachments) ||
                    MultiviewViewMask != ResolveFramebufferMultiviewViewMask(attachments) ||
                    AttachmentViews.Length != attachments.Length ||
                    AttachmentSignature.Length != attachments.Length ||
                    AttachmentTargets.Length != attachments.Length)
                {
                    return false;
                }

                for (int i = 0; i < attachments.Length; i++)
                {
                    if (AttachmentViews[i].Handle != attachments[i].View.Handle)
                        return false;

                    if (!AttachmentSignature[i].Equals(attachments[i].Signature))
                        return false;

                    if (!AttachmentTargets[i].Equals(attachments[i].TargetInfo))
                        return false;
                }

                return true;
            }
        }

        private readonly record struct AttachmentSource(
            ImageView View,
            Format Format,
            SampleCountFlags Samples,
            ImageAspectFlags AspectMask,
            ImageUsageFlags Usage,
            uint LayerCount,
            uint MultiviewViewMask,
            Extent2D Extent);

        private readonly record struct AttachmentTargetInfo(
            IFrameBufferAttachement Target,
            EFrameBufferAttachment Attachment,
            int MipLevel,
            int LayerIndex);

        private readonly record struct AttachmentBuildInfo(
            AttachmentTargetInfo TargetInfo,
            ImageView View,
            FrameBufferAttachmentSignature Signature,
            uint ColorIndex,
            Extent2D Extent,
            uint LayerCount,
            uint MultiviewViewMask);

        protected override void DeleteObjectInternal() { }

        protected override void LinkData()
        {
            Data.Resized += OnFramebufferResized;
            Data.BindForReadRequested += BindForReading;
            Data.BindForWriteRequested += BindForWriting;
            Data.UnbindFromReadRequested += UnbindFromReading;
            Data.UnbindFromWriteRequested += UnbindFromWriting;
        }

        protected override void UnlinkData()
        {
            Data.Resized -= OnFramebufferResized;
            Data.BindForReadRequested -= BindForReading;
            Data.BindForWriteRequested -= BindForWriting;
            Data.UnbindFromReadRequested -= UnbindFromReading;
            Data.UnbindFromWriteRequested -= UnbindFromWriting;
        }

        private void BindForReading()
            => Renderer.BindFrameBuffer(EFramebufferTarget.ReadFramebuffer, Data);

        private void UnbindFromReading()
            => Renderer.BindFrameBuffer(EFramebufferTarget.ReadFramebuffer, null);

        private void BindForWriting()
            => Renderer.BindFrameBuffer(EFramebufferTarget.DrawFramebuffer, Data);

        private void UnbindFromWriting()
            => Renderer.BindFrameBuffer(EFramebufferTarget.DrawFramebuffer, null);

        private void OnFramebufferResized()
            => Destroy();
    }
}
