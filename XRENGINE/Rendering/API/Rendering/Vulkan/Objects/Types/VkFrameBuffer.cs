using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using Format = Silk.NET.Vulkan.Format;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public class VkFrameBuffer(VulkanRenderer api, XRFrameBuffer data) : VkObject<XRFrameBuffer>(api, data)
    {
        private Framebuffer _frameBuffer = default;
        private RenderPass _renderPass = default;
        private FrameBufferAttachmentSignature[]? _attachmentSignature;

        public override VkObjectType Type { get; } = VkObjectType.Framebuffer;
        public override bool IsGenerated { get; }

        public Framebuffer FrameBuffer => _frameBuffer;
        public RenderPass RenderPass => _renderPass;

        internal uint AttachmentCount => (uint)(_attachmentSignature?.Length ?? 0);

        internal void WriteClearValues(ClearValue* destination, uint clearValueCount)
        {
            if (_attachmentSignature is null || clearValueCount == 0)
                return;

            var clearColor = Renderer.GetClearColorValue();
            float clearDepth = Renderer.GetClearDepthValue();
            uint clearStencil = Renderer.GetClearStencilValue();

            uint count = Math.Min(clearValueCount, (uint)_attachmentSignature.Length);
            for (uint i = 0; i < count; i++)
            {
                var sig = _attachmentSignature[i];
                if (sig.Role == AttachmentRole.Color)
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
                ImageAspectFlags aspects = ImageAspectFlags.None;
                if (clearDepth)
                    aspects |= ImageAspectFlags.DepthBit;
                if (clearStencil)
                    aspects |= ImageAspectFlags.StencilBit;

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
            Api!.DestroyFramebuffer(Device, _frameBuffer, null);
            _frameBuffer = default;
            _renderPass = default;
            _attachmentSignature = null;
        }

        protected override uint CreateObjectInternal()
        {
            AttachmentBuildInfo[] attachments = BuildAttachmentInfos();
            ImageView[] views = new ImageView[attachments.Length];
            FrameBufferAttachmentSignature[] signatures = new FrameBufferAttachmentSignature[attachments.Length];

            for (int i = 0; i < attachments.Length; i++)
            {
                views[i] = attachments[i].View;
                signatures[i] = attachments[i].Signature;
            }

            RenderPass renderPass = Renderer.GetOrCreateFrameBufferRenderPass(signatures);
            _renderPass = renderPass;
            _attachmentSignature = signatures;

            fixed (ImageView* viewsPtr = views)
            {
                FramebufferCreateInfo framebufferInfo = new()
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = renderPass,
                    AttachmentCount = (uint)views.Length,
                    PAttachments = viewsPtr,
                    Width = Math.Max(Data.Width, 1u),
                    Height = Math.Max(Data.Height, 1u),
                    Layers = 1,
                };

                fixed (Framebuffer* frameBufferPtr = &_frameBuffer)
                {
                    if (Api!.CreateFramebuffer(Device, ref framebufferInfo, null, frameBufferPtr) != Result.Success)
                        throw new Exception("Failed to create framebuffer.");
                }
            }

            return CacheObject(this);
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

                AttachmentSource source = ResolveAttachmentSource(target, mip, layer);
                AttachmentRole role = ResolveAttachmentRole(attachment, source.AspectMask);

                if (role == AttachmentRole.Color)
                {
                    uint slot = ResolveColorSlot(attachment, ref nextImplicitColorSlot, usedColorSlots);
                    FrameBufferAttachmentSignature signature = BuildAttachmentSignature(source, role, slot);
                    colorAttachments.Add(new AttachmentBuildInfo(source.View, signature, slot));
                    continue;
                }

                if (depthAttachment.HasValue)
                    throw new InvalidOperationException($"Framebuffer '{Data.Name ?? "<unnamed>"}' defines multiple depth/stencil attachments which is not supported in Vulkan subpasses.");

                FrameBufferAttachmentSignature depthSignature = BuildAttachmentSignature(source, role, 0);
                depthAttachment = new AttachmentBuildInfo(source.View, depthSignature, 0);
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

        private static string DescribeAttachment(IFrameBufferAttachement attachment)
            => attachment switch
            {
                XRTexture texture => texture.Name ?? texture.GetDescribingName(),
                XRRenderBuffer renderBuffer => renderBuffer.Name ?? renderBuffer.GetType().Name,
                _ => attachment.GetType().Name
            } ?? attachment.GetType().Name;

        private AttachmentSource ResolveAttachmentSource(IFrameBufferAttachement target, int mipLevel, int layerIndex)
            => target switch
            {
                XRTexture texture => ResolveTextureAttachment(texture, mipLevel, layerIndex),
                XRRenderBuffer renderBuffer => ResolveRenderBufferAttachment(renderBuffer),
                _ => throw new NotSupportedException($"Framebuffer attachment type '{target.GetType().Name}' is not supported yet.")
            };

        private AttachmentSource ResolveRenderBufferAttachment(XRRenderBuffer renderBuffer)
        {
            if (Renderer.GetOrCreateAPIRenderObject(renderBuffer) is not VkRenderBuffer vkRenderBuffer)
                throw new InvalidOperationException("Render buffer is not backed by a Vulkan object.");

            vkRenderBuffer.Generate();
            return new AttachmentSource(vkRenderBuffer.View, vkRenderBuffer.Format, vkRenderBuffer.Samples, vkRenderBuffer.Aspect);
        }

        private AttachmentSource ResolveTextureAttachment(XRTexture texture, int mipLevel, int layerIndex)
        {
            if (Renderer.GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkFrameBufferAttachmentSource source)
                throw new InvalidOperationException($"Texture '{texture.Name ?? texture.GetDescribingName()}' is not backed by a Vulkan texture.");

            ImageView view = source.GetAttachmentView(mipLevel, layerIndex);
            return new AttachmentSource(view, source.DescriptorFormat, source.DescriptorSamples, source.DescriptorAspect);
        }

        private static AttachmentRole ResolveAttachmentRole(EFrameBufferAttachment attachment, ImageAspectFlags aspect)
            => attachment switch
            {
                EFrameBufferAttachment.DepthAttachment => AttachmentRole.Depth,
                EFrameBufferAttachment.DepthStencilAttachment => AttachmentRole.DepthStencil,
                EFrameBufferAttachment.StencilAttachment => AttachmentRole.Stencil,
                _ when (aspect & ImageAspectFlags.DepthBit) != 0 && (aspect & ImageAspectFlags.StencilBit) != 0 => AttachmentRole.DepthStencil,
                _ when (aspect & ImageAspectFlags.DepthBit) != 0 => AttachmentRole.Depth,
                _ when (aspect & ImageAspectFlags.StencilBit) != 0 => AttachmentRole.Stencil,
                _ => AttachmentRole.Color
            };

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
            bool hasStencil = (source.AspectMask & ImageAspectFlags.StencilBit) != 0;
            AttachmentLoadOp stencilLoad = hasStencil ? AttachmentLoadOp.Load : AttachmentLoadOp.DontCare;
            AttachmentStoreOp stencilStore = hasStencil ? AttachmentStoreOp.Store : AttachmentStoreOp.DontCare;
            ImageLayout layout = role == AttachmentRole.Color ? ImageLayout.ColorAttachmentOptimal : ImageLayout.DepthStencilAttachmentOptimal;

            return new FrameBufferAttachmentSignature(
                source.Format,
                source.Samples,
                source.AspectMask,
                role,
                colorIndex,
                AttachmentLoadOp.Load,
                AttachmentStoreOp.Store,
                stencilLoad,
                stencilStore,
                layout,
                layout);
        }

        private readonly record struct AttachmentSource(ImageView View, Format Format, SampleCountFlags Samples, ImageAspectFlags AspectMask);

        private readonly record struct AttachmentBuildInfo(ImageView View, FrameBufferAttachmentSignature Signature, uint ColorIndex);

        protected override void DeleteObjectInternal() { }

        protected override void LinkData()
            => Data.Resized += OnFramebufferResized;

        protected override void UnlinkData()
            => Data.Resized -= OnFramebufferResized;

        private void OnFramebufferResized()
            => Destroy();
    }
}
