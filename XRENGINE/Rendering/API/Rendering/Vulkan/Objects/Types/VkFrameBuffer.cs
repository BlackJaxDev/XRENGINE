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

        internal uint AttachmentCount => (uint)(_attachmentSignature?.Length ?? 0);

        internal RenderPass ResolveRenderPassForPass(int passIndex, IReadOnlyCollection<RenderPassMetadata>? passMetadata, ImageLayout[]? initialLayoutOverrides = null)
        {
            if (_attachmentSignature is null || _attachmentSignature.Length == 0)
                return _renderPass;

            // When initial-layout overrides are provided (from per-frame FBO tracking),
            // always build a planned signature so that the render pass uses the correct
            // initialLayout/loadOp combination.
            bool hasLayoutOverrides = initialLayoutOverrides is not null && initialLayoutOverrides.Length == _attachmentSignature.Length;

            if (passMetadata is null || passMetadata.Count == 0)
            {
                if (!hasLayoutOverrides)
                    return _renderPass;

                // No metadata but we have layout overrides — apply them to the base signature.
                FrameBufferAttachmentSignature[] overridden = ApplyInitialLayoutOverrides(_attachmentSignature, initialLayoutOverrides!);
                if (SignatureEquals(_attachmentSignature, overridden))
                    return _renderPass;
                return Renderer.GetOrCreateFrameBufferRenderPass(overridden);
            }

            string? frameBufferName = Data.Name;
            if (string.IsNullOrWhiteSpace(frameBufferName))
            {
                if (!hasLayoutOverrides)
                    return _renderPass;
                FrameBufferAttachmentSignature[] overridden = ApplyInitialLayoutOverrides(_attachmentSignature, initialLayoutOverrides!);
                if (SignatureEquals(_attachmentSignature, overridden))
                    return _renderPass;
                return Renderer.GetOrCreateFrameBufferRenderPass(overridden);
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
                    return _renderPass;
                FrameBufferAttachmentSignature[] overridden = ApplyInitialLayoutOverrides(_attachmentSignature, initialLayoutOverrides!);
                if (SignatureEquals(_attachmentSignature, overridden))
                    return _renderPass;
                return Renderer.GetOrCreateFrameBufferRenderPass(overridden);
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
                    return _renderPass;
                FrameBufferAttachmentSignature[] overridden = ApplyInitialLayoutOverrides(_attachmentSignature, initialLayoutOverrides!);
                if (SignatureEquals(_attachmentSignature, overridden))
                    return _renderPass;
                return Renderer.GetOrCreateFrameBufferRenderPass(overridden);
            }

            FrameBufferAttachmentSignature[] planned = BuildPlannedAttachmentSignature(pass, frameBufferName);

            // Apply per-frame initial-layout overrides on top of metadata-driven planning.
            if (hasLayoutOverrides)
                planned = ApplyInitialLayoutOverrides(planned, initialLayoutOverrides!);

            if (SignatureEquals(_attachmentSignature, planned))
                return _renderPass;

            return Renderer.GetOrCreateFrameBufferRenderPass(planned);
        }

        /// <summary>
        /// Returns the <see cref="FrameBufferAttachmentSignature.FinalLayout"/> for each attachment
        /// in the order they appear in the framebuffer's attachment array.
        /// </summary>
        internal ImageLayout[] GetFinalLayouts()
        {
            if (_attachmentSignature is null || _attachmentSignature.Length == 0)
                return [];

            ImageLayout[] layouts = new ImageLayout[_attachmentSignature.Length];
            for (int i = 0; i < _attachmentSignature.Length; i++)
                layouts[i] = _attachmentSignature[i].FinalLayout;
            return layouts;
        }

        private static FrameBufferAttachmentSignature[] ApplyInitialLayoutOverrides(
            FrameBufferAttachmentSignature[] signatures,
            ImageLayout[] overrides)
        {
            FrameBufferAttachmentSignature[] result = (FrameBufferAttachmentSignature[])signatures.Clone();
            int count = Math.Min(result.Length, overrides.Length);
            for (int i = 0; i < count; i++)
            {
                ImageLayout overrideLayout = overrides[i];
                if (overrideLayout == ImageLayout.Undefined)
                    continue; // No override for this attachment — keep existing initialLayout.

                FrameBufferAttachmentSignature existing = result[i];
                result[i] = new FrameBufferAttachmentSignature(
                    existing.Format,
                    existing.Samples,
                    existing.AspectMask,
                    existing.Role,
                    existing.ColorIndex,
                    existing.LoadOp,
                    existing.StoreOp,
                    existing.StencilLoadOp,
                    existing.StencilStoreOp,
                    overrideLayout,
                    existing.FinalLayout);
            }
            return result;
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
                ImageAspectFlags requestedAspects = ImageAspectFlags.None;
                if (clearDepth)
                    requestedAspects |= ImageAspectFlags.DepthBit;
                if (clearStencil)
                    requestedAspects |= ImageAspectFlags.StencilBit;

                ImageAspectFlags supportedDepthStencilAspects = ImageAspectFlags.None;
                for (int i = 0; i < _attachmentSignature.Length; i++)
                {
                    var sig = _attachmentSignature[i];
                    if (sig.Role is AttachmentRole.Depth or AttachmentRole.Stencil)
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
            if (_frameBuffer.Handle != 0)
                Renderer.RetireFramebuffer(_frameBuffer);
            _frameBuffer = default;
            _renderPass = default;
            _attachmentSignature = null;
            FramebufferWidth = 0;
            FramebufferHeight = 0;
            PostDeleted();
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

            // Compute framebuffer dimensions accounting for mip-level targets.
            // When an FBO targets a specific mip level (e.g. bloom downsample), the
            // VkFramebuffer width/height must match the mip-level extent, not the base.
            uint fbWidth = Math.Max(Data.Width, 1u);
            uint fbHeight = Math.Max(Data.Height, 1u);
            var targets = Data.Targets;
            if (targets is not null && targets.Length > 0)
            {
                int maxMip = 0;
                foreach (var (_, _, mip, _) in targets)
                    maxMip = Math.Max(maxMip, mip);
                if (maxMip > 0)
                {
                    fbWidth = Math.Max(fbWidth >> maxMip, 1u);
                    fbHeight = Math.Max(fbHeight >> maxMip, 1u);
                }
            }

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
                    Layers = 1,
                };

                FramebufferWidth = fbWidth;
                FramebufferHeight = fbHeight;

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

                AttachmentSource source = ResolveAttachmentSource(target, attachment, mip, layer);
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

        private AttachmentSource ResolveAttachmentSource(IFrameBufferAttachement target, EFrameBufferAttachment attachment, int mipLevel, int layerIndex)
            => target switch
            {
                XRTexture texture => ResolveTextureAttachment(texture, attachment, mipLevel, layerIndex),
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

        private AttachmentSource ResolveTextureAttachment(XRTexture texture, EFrameBufferAttachment attachment, int mipLevel, int layerIndex)
        {
            if (Renderer.GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkFrameBufferAttachmentSource source)
                throw new InvalidOperationException($"Texture '{texture.Name ?? texture.GetDescribingName()}' is not backed by a Vulkan texture.");

            bool depthStencilAttachment = attachment is EFrameBufferAttachment.DepthAttachment
                or EFrameBufferAttachment.DepthStencilAttachment
                or EFrameBufferAttachment.StencilAttachment;
            source.EnsureAttachmentLayout(depthStencilAttachment);

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
            AttachmentLoadOp stencilLoad = AttachmentLoadOp.DontCare;
            AttachmentStoreOp stencilStore = hasStencil ? AttachmentStoreOp.Store : AttachmentStoreOp.DontCare;

            // Color attachments use ShaderReadOnlyOptimal as the final layout so
            // the render pass automatically transitions them for sampling by
            // subsequent passes (e.g. fullscreen quad blits in the post-process
            // chain).  Depth/stencil attachments stay in their optimal layout.
            ImageLayout finalLayout = role == AttachmentRole.Color
                ? ImageLayout.ShaderReadOnlyOptimal
                : ImageLayout.DepthStencilAttachmentOptimal;

            return new FrameBufferAttachmentSignature(
                source.Format,
                source.Samples,
                source.AspectMask,
                role,
                colorIndex,
                AttachmentLoadOp.DontCare,
                AttachmentStoreOp.Store,
                stencilLoad,
                stencilStore,
                ImageLayout.Undefined,
                finalLayout);
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
