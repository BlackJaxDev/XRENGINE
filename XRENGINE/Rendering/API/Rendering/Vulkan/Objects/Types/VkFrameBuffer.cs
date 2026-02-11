using System.Collections.Generic;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;
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

        internal RenderPass ResolveRenderPassForPass(int passIndex, IReadOnlyCollection<RenderPassMetadata>? passMetadata)
        {
            if (_attachmentSignature is null || _attachmentSignature.Length == 0)
                return _renderPass;

            if (passMetadata is null || passMetadata.Count == 0)
                return _renderPass;

            string? frameBufferName = Data.Name;
            if (string.IsNullOrWhiteSpace(frameBufferName))
                return _renderPass;

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
                return _renderPass;

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
                return _renderPass;

            FrameBufferAttachmentSignature[] planned = BuildPlannedAttachmentSignature(pass, frameBufferName);
            if (SignatureEquals(_attachmentSignature, planned))
                return _renderPass;

            return Renderer.GetOrCreateFrameBufferRenderPass(planned);
        }

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

        private FrameBufferAttachmentSignature[] BuildPlannedAttachmentSignature(RenderPassMetadata pass, string frameBufferName)
        {
            FrameBufferAttachmentSignature[] planned = (FrameBufferAttachmentSignature[])_attachmentSignature!.Clone();
            HashSet<int> touchedAttachments = [];
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
                    FrameBufferAttachmentSignature updated = usage.ResourceType switch
                    {
                        RenderPassResourceType.StencilAttachment => WithOps(
                            existing,
                            existing.LoadOp,
                            existing.StoreOp,
                            loadOp,
                            storeOp),
                        _ => WithOps(
                            existing,
                            loadOp,
                            storeOp,
                            existing.StencilLoadOp,
                            existing.StencilStoreOp),
                    };

                    planned[index] = updated;
                    touchedAttachments.Add(index);
                }
            }

            ValidateAttachmentSampleCounts(planned, touchedAttachments, pass, frameBufferName);
            return planned;
        }

        private static int[] ResolveMatchingAttachmentIndices(
            FrameBufferAttachmentSignature[] signatures,
            string slot,
            RenderPassResourceUsage usage,
            RenderPassMetadata pass)
        {
            if (slot.StartsWith("color", StringComparison.OrdinalIgnoreCase))
            {
                if (usage.ResourceType is not RenderPassResourceType.ColorAttachment and not RenderPassResourceType.ResolveAttachment)
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
                if (usage.ResourceType is not RenderPassResourceType.DepthAttachment and not RenderPassResourceType.StencilAttachment)
                {
                    throw new InvalidOperationException(
                        $"Render pass '{pass.Name}' declares '{usage.ResourceType}' for depth slot '{slot}'.");
                }

                for (int i = 0; i < signatures.Length; i++)
                {
                    FrameBufferAttachmentSignature signature = signatures[i];
                    if (signature.Role is not (AttachmentRole.Depth or AttachmentRole.DepthStencil))
                        continue;

                    if (usage.ResourceType == RenderPassResourceType.StencilAttachment &&
                        (signature.AspectMask & ImageAspectFlags.StencilBit) == 0)
                    {
                        throw new InvalidOperationException(
                            $"Render pass '{pass.Name}' expects stencil access on depth slot, but attachment has no stencil aspect.");
                    }

                    return [i];
                }

                throw new InvalidOperationException(
                    $"Render pass '{pass.Name}' references depth slot '{slot}' but framebuffer has no depth attachment.");
            }

            if (slot.Equals("stencil", StringComparison.OrdinalIgnoreCase))
            {
                if (usage.ResourceType is not RenderPassResourceType.StencilAttachment and not RenderPassResourceType.DepthAttachment)
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

                throw new InvalidOperationException(
                    $"Render pass '{pass.Name}' references stencil slot '{slot}' but framebuffer has no stencil attachment.");
            }

            throw new InvalidOperationException(
                $"Render pass '{pass.Name}' references unsupported framebuffer slot '{slot}'. Expected color/depth/stencil.");
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
                signature.FinalLayout);

        private static AttachmentLoadOp ToVkLoadOp(RenderPassLoadOp op)
            => op switch
            {
                RenderPassLoadOp.Clear => AttachmentLoadOp.Clear,
                RenderPassLoadOp.DontCare => AttachmentLoadOp.DontCare,
                _ => AttachmentLoadOp.Load
            };

        private static AttachmentStoreOp ToVkStoreOp(RenderPassStoreOp op)
            => op == RenderPassStoreOp.DontCare
                ? AttachmentStoreOp.DontCare
                : AttachmentStoreOp.Store;

        private static bool SignatureEquals(FrameBufferAttachmentSignature[] first, FrameBufferAttachmentSignature[] second)
        {
            if (first.Length != second.Length)
                return false;

            for (int i = 0; i < first.Length; i++)
            {
                if (!first[i].Equals(second[i]))
                    return false;
            }

            return true;
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
