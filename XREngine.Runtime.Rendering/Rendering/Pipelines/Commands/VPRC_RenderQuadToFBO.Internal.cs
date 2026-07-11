using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    public partial class VPRC_RenderQuadToFBO : ViewportRenderCommand
    {
        /// <summary>
        /// Resolves the destination label for this command, which is used for profiling and logging.
        /// </summary>
        /// <param name="activeInstance">The active render pipeline instance.</param>
        /// <returns>The resolved destination label.</returns>
        protected string ResolveDestinationLabel(XRRenderPipelineInstance? activeInstance)
            => DestinationFBOName
                ?? (RenderToSourceFrameBuffer ? SourceQuadFBOName : null)
                ?? activeInstance?.RenderState.CurrentRenderTargetBinding?.Name
                ?? activeInstance?.RenderState.OutputFBO?.Name
                ?? RenderGraphResourceNames.OutputRenderTarget;

        /// <summary>
        /// Resolves the destination FBO for this command, which is used for rendering the quad.
        /// </summary>
        /// <param name="activeInstance">The active render pipeline instance.</param>
        /// <param name="sourceFBO">The source FBO.</param>
        /// <returns>The resolved destination FBO.</returns>
        protected XRFrameBuffer? ResolveDestinationFbo(XRRenderPipelineInstance? activeInstance, XRQuadFrameBuffer? sourceFBO)
        {
            if (DestinationFBOName is not null)
                return activeInstance?.GetFBO<XRFrameBuffer>(DestinationFBOName);

            if (RenderToSourceFrameBuffer)
                return sourceFBO ?? (SourceQuadFBOName is not null
                    ? activeInstance?.GetFBO<XRFrameBuffer>(SourceQuadFBOName)
                    : null);

            // Null destination means "draw into the current render target".  Use
            // the logical pipeline target stack, not the backend's physical FBO
            // stack, because Vulkan records these operations after command
            // execution has already produced frame ops.
            return activeInstance?.RenderState.CurrentRenderTargetBinding?.FrameBuffer
                ?? activeInstance?.RenderState.OutputFBO;
        }

        /// <summary>
        /// Builds a render graph pass name for the quad blit operation, based on the source FBO name, destination, and optional variant.
        /// </summary>
        /// <param name="sourceFboName">The name of the source FBO.</param>
        /// <param name="destination">The name of the destination FBO.</param>
        /// <param name="variant">An optional variant name.</param>
        /// <returns>The constructed render graph pass name.</returns>
        protected static string BuildQuadBlitPassName(string sourceFboName, string destination, string? variant = null)
            => string.IsNullOrWhiteSpace(variant)
                ? $"QuadBlit_{sourceFboName}_to_{destination}"
                : $"QuadBlit_{variant}_{sourceFboName}_to_{destination}";

        /// <summary>
        /// Resolves the resource name for a descriptor based on its kind and original name.
        /// </summary>
        /// <param name="kind">The kind of descriptor resource.</param>
        /// <param name="name">The original name of the resource.</param>
        /// <returns>The resolved resource name.</returns>
        private static string ResolveDescriptorResourceName(EDescriptorResourceKind kind, string name)
            => kind switch
            {
                EDescriptorResourceKind.Texture => MakeTextureResource(name),
                EDescriptorResourceKind.FboColor => MakeFboColorResource(name),
                EDescriptorResourceKind.ColorTarget => MakeColorTargetResource(name),
                _ => name,
            };

        /// <summary>
        /// Resolves the shader label for the source FBO's material, if available, 
        /// for profiling and logging purposes.
        /// </summary>
        /// <param name="activeInstance">The active render pipeline instance.</param>
        /// <returns>The resolved shader label.</returns>
        private string ResolveShaderLabel(XRRenderPipelineInstance? activeInstance)
        {
            XRQuadFrameBuffer? sourceFBO = SourceQuadFBOName is null
                ? null
                : activeInstance?.GetFBO<XRQuadFrameBuffer>(SourceQuadFBOName);

            XRMaterial? material = sourceFBO?.Material;
            if (material is null)
                return string.Empty;

            IReadOnlyList<XRShader> fragmentShaders = material.FragmentShaders;
            XRShader? fragmentShader = fragmentShaders.Count > 0
                ? fragmentShaders[fragmentShaders.Count - 1]
                : null;

            string shaderName = GetShaderDisplayName(fragmentShader);
            if (string.IsNullOrWhiteSpace(material.Name))
                return shaderName;

            return string.IsNullOrWhiteSpace(shaderName)
                ? $"material={material.Name}"
                : $"material={material.Name}; shader={shaderName}";
        }

        /// <summary>
        /// Gets a display-friendly name for the given shader, using its source file path or name.
        /// </summary>
        /// <param name="shader">The shader to get the display name for.</param>
        /// <returns>The display-friendly name of the shader.</returns>
        private static string GetShaderDisplayName(XRShader? shader)
        {
            if (shader is null)
                return string.Empty;

            string? path = shader.Source?.FilePath ?? shader.FilePath;
            if (!string.IsNullOrWhiteSpace(path))
                return Path.GetFileName(path);

            return shader.Name ?? string.Empty;
        }

        /// <summary>
        /// Executes the command to render the source FBO quad into the destination FBO, 
        /// handling render graph pass setup and diagnostics.
        /// </summary>
        protected override void Execute()
        {
            if (SourceQuadFBOName is null)
                return;

            var activeInstance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            if (activeInstance is null)
            {
                Debug.RenderingWarningEvery(
                    $"QuadBlit.MissingPipeline.{SourceQuadFBOName}.{DestinationFBOName}",
                    TimeSpan.FromSeconds(5),
                    "[QuadBlitDiag] Skipping quad blit from '{0}' to '{1}': no active render pipeline instance.",
                    SourceQuadFBOName,
                    DestinationFBOName ?? "<current>");
                return;
            }

            string destination = ResolveDestinationLabel(activeInstance);

            string passName = BuildQuadBlitPassName(SourceQuadFBOName, destination, RenderGraphPassVariant);
            int passIndex = ResolvePassIndex(passName, out bool hasRenderGraphMetadata);
            if (passIndex == int.MinValue && hasRenderGraphMetadata)
            {
                Debug.RenderingWarningEvery(
                    $"QuadBlit.MissingRenderGraphPass.{passName}",
                    TimeSpan.FromSeconds(2),
                    "[QuadBlitDiag] Skipping quad blit '{0}': no matching render-graph pass metadata was generated.",
                    passName);
                return;
            }

            using var passScope = passIndex != int.MinValue
                ? RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex)
                : default;

            XRQuadFrameBuffer? sourceFBO = activeInstance.GetFBO<XRQuadFrameBuffer>(SourceQuadFBOName);
            if (sourceFBO is null)
            {
                if (DiagnosticsEnabled)
                    Debug.RenderingWarning($"[QuadBlitDiag] Source FBO '{SourceQuadFBOName}' not found as XRQuadFrameBuffer.");
                return;
            }

            XRFrameBuffer? destFBO = ResolveDestinationFbo(activeInstance, sourceFBO);
            if (DiagnosticsEnabled && DestinationFBOName is not null && destFBO is null)
                Debug.RenderingWarning($"[QuadBlitDiag] Dest FBO '{DestinationFBOName}' not found.");

            if (DiagnosticsEnabled)
            {
                bool hasTargets = destFBO?.Targets is { Length: > 0 };
                Debug.Log(ELogCategory.Rendering, $"[QuadBlitDiag] Rendering '{SourceQuadFBOName}' → '{DestinationFBOName ?? "<current>"}' (dest has targets: {hasTargets}, dest type: {destFBO?.GetType().Name ?? "null"})");
            }

            bool resolvedDestinationArea = TryResolveDestinationRenderArea(destFBO, out int renderWidth, out int renderHeight);
            if (MatchDestinationRenderArea && !resolvedDestinationArea)
            {
                throw new InvalidOperationException(
                    $"Fullscreen pass '{SourceQuadFBOName}' cannot derive a render area from destination '{destination}'.");
            }

            using var renderAreaScope = MatchDestinationRenderArea
                ? activeInstance.RenderState.PushRenderArea(renderWidth, renderHeight)
                : default;

            if (MatchDestinationRenderArea)
            {
                ValidateDestinationScreenRegionContract(
                    activeInstance,
                    sourceFBO,
                    destFBO!,
                    destination,
                    renderWidth,
                    renderHeight);
            }

            sourceFBO.Render(destFBO);
        }

        private static void ValidateDestinationScreenRegionContract(
            XRRenderPipelineInstance activeInstance,
            XRQuadFrameBuffer sourceFbo,
            XRFrameBuffer destinationFbo,
            string destinationLabel,
            int destinationWidth,
            int destinationHeight)
        {
            BoundingRectangle area = activeInstance.RenderState.CurrentRenderRegion;
            if (area.X != 0 ||
                area.Y != 0 ||
                area.Width != destinationWidth ||
                area.Height != destinationHeight)
            {
                throw new InvalidOperationException(
                    $"Fullscreen pass '{sourceFbo.Name}' screen-region mismatch for '{destinationLabel}'. " +
                    $"Expected=(0,0,{destinationWidth},{destinationHeight}) Actual={area}.");
            }

            bool stereo = RuntimeEngine.Rendering.State.IsStereoPass;
            uint attachmentLayers = 1u;
            uint expectedViewMask = 0u;
            if (destinationFbo.Targets is { Length: > 0 } targets)
            {
                for (int i = 0; i < targets.Length; i++)
                {
                    var (target, _, _, layerIndex) = targets[i];
                    uint targetLayers = ResolveAttachmentLayerCount(target);
                    attachmentLayers = Math.Max(attachmentLayers, targetLayers);

                    if (!stereo)
                        continue;

                    if (layerIndex != -1)
                    {
                        throw new InvalidOperationException(
                            $"Stereo fullscreen pass '{sourceFbo.Name}' selects destination layer {layerIndex} on '{destinationLabel}'. " +
                            "True single-pass stereo must bind the complete two-layer attachment.");
                    }

                    XRTexture? texture = target as XRTexture;
                    XRTexture viewedTexture = texture is XRTextureViewBase view
                        ? view.GetViewedTexture()
                        : texture!;
                    if (texture is null ||
                        targetLayers != 2u ||
                        viewedTexture is not XRTexture2DArray array ||
                        array.OVRMultiViewParameters is not { Offset: 0, NumViews: 2u })
                    {
                        throw new InvalidOperationException(
                            $"Stereo fullscreen pass '{sourceFbo.Name}' destination '{destinationLabel}' is not a complete two-layer multiview attachment. " +
                            $"Target={target.GetType().Name} Layers={targetLayers}.");
                    }
                }

                if (stereo)
                    expectedViewMask = 0x3u;
            }

            if (!RenderDiagnosticsFlags.DiagPostProcess && !DiagnosticsEnabled)
                return;

            XRTexture? primarySource = null;
            XRMaterial? material = sourceFbo.Material;
            if (material is not null)
            {
                for (int i = 0; i < material.Textures.Count; i++)
                {
                    if (material.Textures[i] is XRTexture texture)
                    {
                        primarySource = texture;
                        break;
                    }
                }
            }

            Vector3 sourceDimensions = primarySource?.WidthHeightDepth ?? Vector3.Zero;
            string sourceExtent = primarySource is null
                ? "<none>"
                : $"{Math.Max(1, (int)MathF.Round(sourceDimensions.X))}x{Math.Max(1, (int)MathF.Round(sourceDimensions.Y))}";
            Debug.RenderingEvery(
                $"FullscreenRegion.{activeInstance.InstanceId}.{sourceFbo.Name}.{destinationLabel}",
                TimeSpan.FromSeconds(1),
                "[PostProcessDiag] Fullscreen source={0} destination={1} sourceExtent={2} destinationExtent={3}x{4} " +
                "renderArea=(0,0,{3},{4}) viewport=(0,0,{3},{4}) scissor=(0,0,{3},{4}) " +
                "layers={5} viewMask=0x{6:X} screenOrigin=(0,0) screenSize=({3},{4}) uv=localRaster/destinationExtent->[0,1]",
                sourceFbo.Name ?? sourceFbo.GetType().Name,
                destinationLabel,
                sourceExtent,
                destinationWidth,
                destinationHeight,
                attachmentLayers,
                expectedViewMask);
        }

        private static uint ResolveAttachmentLayerCount(IFrameBufferAttachement attachment)
            => attachment switch
            {
                XRTextureViewBase view => Math.Max(view.NumLayers, 1u),
                XRTexture2DArray array => Math.Max(array.Depth, 1u),
                _ => 1u,
            };

        /// <summary>
        /// Tries to resolve the render area dimensions from the destination FBO, 
        /// returning true if successful and setting the width and height accordingly.
        /// </summary>
        /// <param name="destination">The destination framebuffer to resolve the render area from.</param>
        /// <param name="width">The resolved width of the render area.</param>
        /// <param name="height">The resolved height of the render area.</param>
        /// <returns>True if the render area dimensions were successfully resolved; otherwise, false.</returns>
        internal static bool TryResolveDestinationRenderArea(XRFrameBuffer? destination, out int width, out int height)
        {
            width = 0;
            height = 0;

            if (destination?.Targets is not { Length: > 0 } targets)
            {
                if (destination is { Width: > 0, Height: > 0 })
                {
                    width = (int)destination.Width;
                    height = (int)destination.Height;
                    return true;
                }

                return false;
            }

            uint minWidth = uint.MaxValue;
            uint minHeight = uint.MaxValue;
            bool found = false;

            foreach (var (target, _, mipLevel, _) in targets)
            {
                if (target is null)
                    continue;

                uint targetWidth = Math.Max(target.Width, 1u);
                uint targetHeight = Math.Max(target.Height, 1u);
                int mip = Math.Max(mipLevel, 0);
                if (mip > 0)
                {
                    targetWidth = Math.Max(targetWidth >> mip, 1u);
                    targetHeight = Math.Max(targetHeight >> mip, 1u);
                }

                minWidth = Math.Min(minWidth, targetWidth);
                minHeight = Math.Min(minHeight, targetHeight);
                found = true;
            }

            if (!found)
                return false;

            width = (int)Math.Min(minWidth, int.MaxValue);
            height = (int)Math.Min(minHeight, int.MaxValue);
            return width > 0 && height > 0;
        }

        /// <summary>
        /// Resolves the index of a render pass by its name, 
        /// returning true if the pass exists and setting the index accordingly.
        /// </summary>
        /// <param name="passName">The name of the render pass to resolve.</param>
        /// <param name="hasRenderGraphMetadata">Indicates whether the render graph metadata is available.</param>
        /// <returns>The index of the render pass if found; otherwise, int.MinValue.</returns>
        protected int ResolvePassIndex(string passName, out bool hasRenderGraphMetadata)
        {
            var metadata = ParentPipeline?.PassMetadata;
            if (metadata is not { Count: > 0 } renderPasses)
            {
                hasRenderGraphMetadata = false;
                return int.MinValue;
            }

            hasRenderGraphMetadata = true;

            foreach (var match in renderPasses)
            {
                if (string.Equals(match.Name, passName, StringComparison.OrdinalIgnoreCase))
                    return match.PassIndex;
            }

            return int.MinValue;
        }

        /// <summary>
        /// Describes the render pass for this command in the render graph, 
        /// including its inputs, outputs, and dependencies.
        /// </summary>
        /// <param name="context">The context used to describe the render pass.</param>
        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            if (SourceQuadFBOName is null)
                return;

            string destination = DestinationFBOName
                ?? (RenderToSourceFrameBuffer ? SourceQuadFBOName : null)
                ?? context.CurrentRenderTarget?.Name
                ?? RenderGraphResourceNames.OutputRenderTarget;

            var builder = context.GetOrCreateSyntheticPass(BuildQuadBlitPassName(SourceQuadFBOName, destination, RenderGraphPassVariant));
            RenderGraphResourceDescriptor? resources = RenderGraphResources;
            builder.WithStage(resources?.Stage ?? ERenderGraphPassStage.Graphics);
            if (resources is not null)
            {
                resources.DescribeDependencies(context, builder);
                resources.DescribeInputs(builder);
            }
            else
            {
                DescribeInferredQuadMaterialInputs(builder, SourceQuadFBOName, destination);
            }

            ERenderPassLoadOp colorLoad = ERenderPassLoadOp.Load;
            ERenderPassStoreOp colorStore = ERenderPassStoreOp.Store;
            ERenderGraphAccess access = ERenderGraphAccess.ReadWrite;

            if (context.CurrentRenderTarget is { } bound &&
                string.Equals(bound.Name, destination, StringComparison.OrdinalIgnoreCase))
            {
                colorLoad = bound.ConsumeColorLoadOp();
                colorStore = bound.GetColorStoreOp();
                access = bound.ColorAccess;
            }

            if (resources?.HasExplicitColorAttachments == true)
                resources.DescribeColorAttachments(builder, access, colorLoad, colorStore);
            else
                DescribeInferredColorOutput(builder, destination, access, colorLoad, colorStore);

            if (resources?.UseDestinationDepthStencil == true)
            {
                builder.UseDepthAttachment(
                    MakeFboDepthResource(destination),
                    ERenderGraphAccess.Read,
                    ERenderPassLoadOp.Load,
                    ERenderPassStoreOp.Store);
                builder.UseStencilAttachment(
                    MakeFboStencilResource(destination),
                    ERenderGraphAccess.Read,
                    ERenderPassLoadOp.Load,
                    ERenderPassStoreOp.Store);
            }
        }

        /// <summary>
        /// Describes the inferred material inputs for the quad render pass, 
        /// based on the source FBO's material and its textures. 
        /// If the source and destination FBOs are different, 
        /// it attempts to describe the actual material textures or FBO color inputs; 
        /// if none are found, it samples the source FBO's color resource.
        /// </summary>
        /// <param name="builder">The render pass builder used to describe the pass.</param>
        /// <param name="sourceFboName">The name of the source framebuffer object.</param>
        /// <param name="destination">The name of the destination framebuffer object.</param>
        private static void DescribeInferredQuadMaterialInputs(
            RenderPassBuilder builder,
            string sourceFboName,
            string destination)
        {
            if (!string.Equals(sourceFboName, destination, StringComparison.Ordinal))
            {
                if (TryDescribeActualMaterialTextures(builder, sourceFboName))
                    return;

                if (TryDescribeActualFboColorInputs(builder, sourceFboName))
                    return;

                builder.SampleTexture(MakeFboColorResource(sourceFboName));
            }
        }

        /// <summary>
        /// Describes the inferred color output for the render pass, 
        /// based on the destination FBO's color attachments. 
        /// If the destination FBO has actual color outputs, it describes them; 
        /// otherwise, it uses a default color attachment resource.
        /// </summary>
        /// <param name="builder">The render pass builder used to describe the pass.</param>
        /// <param name="destination">The name of the destination framebuffer object.</param>
        /// <param name="access">The access type for the color attachment.</param>
        /// <param name="colorLoad">The load operation for the color attachment.</param>
        /// <param name="colorStore">The store operation for the color attachment.</param>
        private static void DescribeInferredColorOutput(
            RenderPassBuilder builder,
            string destination,
            ERenderGraphAccess access,
            ERenderPassLoadOp colorLoad,
            ERenderPassStoreOp colorStore)
        {
            if (TryDescribeActualColorOutputs(builder, destination, access, colorLoad, colorStore))
                return;

            builder.UseColorAttachment(MakeColorTargetResource(destination), access, colorLoad, colorStore);
        }

        /// <summary>
        /// Tries to describe the actual color outputs for the render pass, 
        /// based on the destination FBO's color attachments. 
        /// If the destination FBO has valid color attachments, 
        /// it describes them in the render pass builder and returns true; 
        /// otherwise, it returns false.
        /// </summary>
        /// <param name="builder">The render pass builder used to describe the pass.</param>
        /// <param name="destination">The name of the destination framebuffer object.</param>
        /// <param name="access">The access type for the color attachment.</param>
        /// <param name="colorLoad">The load operation for the color attachment.</param>
        /// <param name="colorStore">The store operation for the color attachment.</param>
        /// <returns>True if the actual color outputs were described; otherwise, false.</returns>
        private static bool TryDescribeActualColorOutputs(
            RenderPassBuilder builder,
            string destination,
            ERenderGraphAccess access,
            ERenderPassLoadOp colorLoad,
            ERenderPassStoreOp colorStore)
        {
            //Retrieve the current render pipeline instance and attempt to get the destination FBO. 
            //If successful, iterate through its targets and describe any valid color attachments in the render pass builder.
            XRRenderPipelineInstance? instance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            if (instance is null ||
                !instance.TryGetFBO(destination, out XRFrameBuffer? fbo) ||
                fbo?.Targets is not { Length: > 0 } targets)
                return false;

            bool described = false;
            for (int i = 0; i < targets.Length; i++)
            {
                var (target, attachment, mipLevel, _) = targets[i];

                //If the attachment is not a color attachment, 
                //or the target is not a valid XRTexture with a name,
                //skip this target. 
                //Otherwise, describe it in the render pass builder as a color attachment.
                if (!IsColorAttachment(attachment) ||
                    target is not XRTexture texture ||
                    string.IsNullOrWhiteSpace(texture.Name))
                    continue;

                string resourceName = MakeTextureResource(texture.Name);
                uint mip = mipLevel > 0 ? (uint)mipLevel : 0u;
                if (mip == 0u)
                    builder.UseColorAttachment(resourceName, access, colorLoad, colorStore);
                else
                    builder.UseColorAttachmentMip(resourceName, mip, access, colorLoad, colorStore);

                described = true;
            }

            return described;
        }

        /// <summary>
        /// Tries to describe the actual material textures for the render pass, 
        /// based on the source FBO's material.
        /// If the material has valid textures, 
        /// it describes them in the render pass builder and returns true; 
        /// otherwise, it returns false.
        /// </summary>
        /// <param name="builder">The render pass builder used to describe the pass.</param>
        /// <param name="sourceFboName">The name of the source framebuffer object.</param>
        /// <returns>True if the actual material textures were described; otherwise, false.</returns>
        private static bool TryDescribeActualMaterialTextures(RenderPassBuilder builder, string sourceFboName)
        {
            //Retrieve the current render pipeline instance and attempt to get the source FBO as an XRQuadFrameBuffer. 
            //If successful, check if it has a material with textures, and describe those textures in the render pass builder.
            XRRenderPipelineInstance? instance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            if (instance is null ||
                !instance.TryGetFBO(sourceFboName, out XRFrameBuffer? fbo) ||
                fbo is not XRQuadFrameBuffer quadFbo ||
                quadFbo.Material is not { } material)
                return false;

            bool described = false;
            var textures = material.Textures;
            for (int i = 0; i < textures.Count; i++)
            {
                XRTexture? texture = textures[i];

                //If the texture is null or has no name, skip it.
                //Otherwise, describe it in the render pass builder as a sampled texture.
                if (texture is null || string.IsNullOrWhiteSpace(texture.Name))
                    continue;

                builder.SampleTexture(MakeTextureResource(texture.Name));
                described = true;
            }

            return described;
        }

        /// <summary>
        /// Tries to describe the actual color inputs for the render pass, 
        /// based on the source FBO's color attachments. 
        /// If the source FBO has valid color attachments, 
        /// it describes them in the render pass builder and returns true; 
        /// otherwise, it returns false.
        /// </summary>
        /// <param name="builder">The render pass builder used to describe the pass.</param>
        /// <param name="sourceFboName">The name of the source framebuffer object.</param>
        /// <returns>True if the actual color inputs were described; otherwise, false.</returns>
        protected static bool TryDescribeActualFboColorInputs(RenderPassBuilder builder, string sourceFboName)
        {
            //Retrieve the current render pipeline instance and attempt to get the source FBO.
            //If successful, iterate through its targets and describe any valid color attachments in the render pass builder.
            XRRenderPipelineInstance? instance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            if (instance is null ||
                !instance.TryGetFBO(sourceFboName, out XRFrameBuffer? fbo) ||
                fbo?.Targets is not { Length: > 0 } targets)
                return false;

            bool described = false;
            for (int i = 0; i < targets.Length; i++)
            {
                var (target, attachment, mipLevel, _) = targets[i];

                //If the attachment is not a color attachment, 
                //or the target is not a valid XRTexture with a name, 
                //skip this target.
                if (!IsColorAttachment(attachment) ||
                    target is not XRTexture texture ||
                    string.IsNullOrWhiteSpace(texture.Name))
                    continue;

                //Make the texture resource name for the target and describe it in the render pass builder.
                string resourceName = MakeTextureResource(texture.Name);
                uint mip = mipLevel > 0 ? (uint)mipLevel : 0u;
                if (mip == 0u)
                    builder.SampleTexture(resourceName);
                else
                    builder.SampleTextureMip(resourceName, mip);

                described = true;
            }

            return described;
        }

        /// <summary>
        /// Determines whether the given framebuffer attachment is a color attachment, 
        /// based on its enumeration value.
        /// </summary>
        /// <param name="attachment">The framebuffer attachment to check.</param>
        /// <returns>True if the attachment is a color attachment; otherwise, false.</returns>
        private static bool IsColorAttachment(EFrameBufferAttachment attachment)
            => attachment >= EFrameBufferAttachment.ColorAttachment0 &&
               attachment <= EFrameBufferAttachment.ColorAttachment31;
    }
}
