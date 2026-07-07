using System;
using System.Collections.Generic;
using System.IO;
using XREngine.Core.Attributes;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Renders an FBO quad to another FBO.
    /// Useful for transforming every pixel of previous FBO.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="destination"></param>
    [XRTypeRedirect("XREngine.Rendering.Pipelines.Commands.VPRC_RenderQuadFBO")]
    [RenderPipelineScriptCommand]
    public class VPRC_RenderQuadToFBO : ViewportRenderCommand
    {
        private static bool _diagEnabled => RenderDiagnosticsFlags.DiagQuadBlit;

        private enum EDescriptorResourceKind
        {
            Texture,
            FboColor,
            ColorTarget,
        }

        public sealed class RenderGraphResourceDescriptor
        {
            private readonly List<SampledTextureUsage> _sampledTextures = [];
            private readonly List<BufferUsage> _readBuffers = [];
            private readonly List<ColorAttachmentUsage> _colorAttachments = [];
            private readonly List<int> _dependencies = [];
            private readonly List<QuadBlitDependency> _quadBlitDependencies = [];
            private readonly List<DependentPass> _dependentPasses = [];

            public ERenderGraphPassStage Stage { get; set; } = ERenderGraphPassStage.Graphics;
            public bool UseDestinationDepthStencil { get; set; }

            internal bool HasExplicitColorAttachments => _colorAttachments.Count > 0;

            public RenderGraphResourceDescriptor SampleTexture(string textureName)
            {
                _sampledTextures.Add(new(textureName, EDescriptorResourceKind.Texture, null, 0u));
                return this;
            }

            public RenderGraphResourceDescriptor SampleTextureMip(string textureName, uint mipLevel)
            {
                _sampledTextures.Add(new(textureName, EDescriptorResourceKind.Texture, mipLevel, 1u));
                return this;
            }

            public RenderGraphResourceDescriptor SampleTextureMips(string textureName, uint baseMipLevel, uint mipLevelCount)
            {
                _sampledTextures.Add(new(textureName, EDescriptorResourceKind.Texture, baseMipLevel, mipLevelCount));
                return this;
            }

            public RenderGraphResourceDescriptor SampleFboColor(string fboName)
            {
                _sampledTextures.Add(new(fboName, EDescriptorResourceKind.FboColor, null, 0u));
                return this;
            }

            public RenderGraphResourceDescriptor ReadBuffer(string bufferName, ERenderPassResourceType bufferType = ERenderPassResourceType.StorageBuffer)
            {
                _readBuffers.Add(new(bufferName, bufferType));
                return this;
            }

            public RenderGraphResourceDescriptor UseColorTexture(
                string textureName,
                ERenderGraphAccess? access = null,
                ERenderPassLoadOp? load = null,
                ERenderPassStoreOp? store = null)
            {
                _colorAttachments.Add(new(textureName, EDescriptorResourceKind.Texture, null, access, load, store));
                return this;
            }

            public RenderGraphResourceDescriptor UseColorTextureMip(
                string textureName,
                uint mipLevel,
                ERenderGraphAccess? access = null,
                ERenderPassLoadOp? load = null,
                ERenderPassStoreOp? store = null)
            {
                _colorAttachments.Add(new(textureName, EDescriptorResourceKind.Texture, mipLevel, access, load, store));
                return this;
            }

            public RenderGraphResourceDescriptor UseColorFbo(
                string fboName,
                ERenderGraphAccess? access = null,
                ERenderPassLoadOp? load = null,
                ERenderPassStoreOp? store = null)
            {
                _colorAttachments.Add(new(fboName, EDescriptorResourceKind.FboColor, null, access, load, store));
                return this;
            }

            public RenderGraphResourceDescriptor UseColorTarget(
                string targetName,
                ERenderGraphAccess? access = null,
                ERenderPassLoadOp? load = null,
                ERenderPassStoreOp? store = null)
            {
                _colorAttachments.Add(new(targetName, EDescriptorResourceKind.ColorTarget, null, access, load, store));
                return this;
            }

            public RenderGraphResourceDescriptor DependsOn(int passIndex)
            {
                _dependencies.Add(passIndex);
                return this;
            }

            public RenderGraphResourceDescriptor DependsOnQuadBlit(string sourceFboName, string destinationFboName, string? variant = null)
            {
                _quadBlitDependencies.Add(new(sourceFboName, destinationFboName, variant));
                return this;
            }

            public RenderGraphResourceDescriptor MakePassDependOnThis(
                int passIndex,
                string passName,
                ERenderGraphPassStage stage = ERenderGraphPassStage.Graphics)
            {
                _dependentPasses.Add(new(passIndex, passName, stage));
                return this;
            }

            public RenderGraphResourceDescriptor UseDestinationDepthStencilAttachments(bool enabled = true)
            {
                UseDestinationDepthStencil = enabled;
                return this;
            }

            internal void DescribeDependencies(RenderGraphDescribeContext context, RenderPassBuilder builder)
            {
                for (int i = 0; i < _dependencies.Count; i++)
                    builder.DependsOn(_dependencies[i]);

                for (int i = 0; i < _quadBlitDependencies.Count; i++)
                {
                    QuadBlitDependency dependency = _quadBlitDependencies[i];
                    builder.DependsOn(context.GetOrCreateSyntheticPass(
                        BuildQuadBlitPassName(dependency.SourceFboName, dependency.DestinationFboName, dependency.Variant)).PassIndex);
                }

                for (int i = 0; i < _dependentPasses.Count; i++)
                {
                    DependentPass dependent = _dependentPasses[i];
                    context.Metadata
                        .ForPass(dependent.PassIndex, dependent.PassName, dependent.Stage)
                        .DependsOn(builder.PassIndex);
                }
            }

            internal void DescribeInputs(RenderPassBuilder builder)
            {
                for (int i = 0; i < _sampledTextures.Count; i++)
                {
                    SampledTextureUsage usage = _sampledTextures[i];
                    string resourceName = ResolveDescriptorResourceName(usage.Kind, usage.Name);
                    if (usage.BaseMipLevel.HasValue)
                    {
                        if (usage.MipLevelCount == 1u)
                            builder.SampleTextureMip(resourceName, usage.BaseMipLevel.Value);
                        else
                            builder.SampleTextureMips(resourceName, usage.BaseMipLevel.Value, usage.MipLevelCount);
                    }
                    else
                    {
                        builder.SampleTexture(resourceName);
                    }
                }

                for (int i = 0; i < _readBuffers.Count; i++)
                {
                    BufferUsage usage = _readBuffers[i];
                    builder.ReadBuffer(usage.Name, usage.Type);
                }
            }

            internal void DescribeColorAttachments(
                RenderPassBuilder builder,
                ERenderGraphAccess defaultAccess,
                ERenderPassLoadOp defaultLoad,
                ERenderPassStoreOp defaultStore)
            {
                for (int i = 0; i < _colorAttachments.Count; i++)
                {
                    ColorAttachmentUsage usage = _colorAttachments[i];
                    string resourceName = ResolveDescriptorResourceName(usage.Kind, usage.Name);
                    ERenderGraphAccess access = usage.Access ?? defaultAccess;
                    ERenderPassLoadOp load = usage.Load ?? defaultLoad;
                    ERenderPassStoreOp store = usage.Store ?? defaultStore;

                    if (usage.MipLevel.HasValue)
                        builder.UseColorAttachmentMip(resourceName, usage.MipLevel.Value, access, load, store);
                    else
                        builder.UseColorAttachment(resourceName, access, load, store);
                }
            }

            private readonly record struct SampledTextureUsage(
                string Name,
                EDescriptorResourceKind Kind,
                uint? BaseMipLevel,
                uint MipLevelCount);

            private readonly record struct BufferUsage(string Name, ERenderPassResourceType Type);

            private readonly record struct ColorAttachmentUsage(
                string Name,
                EDescriptorResourceKind Kind,
                uint? MipLevel,
                ERenderGraphAccess? Access,
                ERenderPassLoadOp? Load,
                ERenderPassStoreOp? Store);

            private readonly record struct QuadBlitDependency(string SourceFboName, string DestinationFboName, string? Variant);

            private readonly record struct DependentPass(int PassIndex, string PassName, ERenderGraphPassStage Stage);
        }

        public string? SourceQuadFBOName { get; set; }
        public string? DestinationFBOName { get; set; } = null;
        public string? FrameBufferName
        {
            get => SourceQuadFBOName;
            set => SourceQuadFBOName = value;
        }
        public string? TargetFrameBufferName
        {
            get => DestinationFBOName;
            set => DestinationFBOName = value;
        }
        public bool RenderToSourceFrameBuffer { get; set; }
        public bool MatchDestinationRenderArea { get; set; }
        public string? RenderGraphPassVariant { get; set; }
        public RenderGraphResourceDescriptor? RenderGraphResources { get; set; }

        public override string GpuProfilingName
        {
            get
            {
                if (string.IsNullOrWhiteSpace(SourceQuadFBOName))
                    return base.GpuProfilingName;

                XRRenderPipelineInstance? activeInstance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
                string destination = ResolveDestinationLabel(activeInstance);
                string shaderLabel = ResolveShaderLabel(activeInstance);

                return string.IsNullOrWhiteSpace(shaderLabel)
                    ? $"{base.GpuProfilingName}[{SourceQuadFBOName}->{destination}]"
                    : $"{base.GpuProfilingName}[{SourceQuadFBOName}->{destination}; {shaderLabel}]";
            }
        }

        public VPRC_RenderQuadToFBO SetTargets(string sourceQuadFBOName, string? destinationFBOName = null, bool matchDestinationRenderArea = false)
        {
            SourceQuadFBOName = sourceQuadFBOName;
            DestinationFBOName = destinationFBOName;
            RenderToSourceFrameBuffer = false;
            MatchDestinationRenderArea = matchDestinationRenderArea;
            return this;
        }

        public VPRC_RenderQuadToFBO SetRenderGraphPassVariant(string? variant)
        {
            RenderGraphPassVariant = variant;
            return this;
        }

        public VPRC_RenderQuadToFBO SetRenderGraphResources(RenderGraphResourceDescriptor? resources)
        {
            RenderGraphResources = resources;
            return this;
        }

        public VPRC_RenderQuadToFBO ConfigureRenderGraphResources(Action<RenderGraphResourceDescriptor> configure)
        {
            RenderGraphResourceDescriptor resources = RenderGraphResources ??= new();
            configure(resources);
            return this;
        }

        public VPRC_RenderQuadToFBO SetOptions(
            string frameBufferName,
            string? targetFrameBufferName = null,
            bool renderToSourceFrameBuffer = false)
        {
            SourceQuadFBOName = frameBufferName;
            DestinationFBOName = targetFrameBufferName;
            RenderToSourceFrameBuffer = renderToSourceFrameBuffer;
            MatchDestinationRenderArea = false;
            return this;
        }

        protected string ResolveDestinationLabel(XRRenderPipelineInstance? activeInstance)
            => DestinationFBOName
                ?? (RenderToSourceFrameBuffer ? SourceQuadFBOName : null)
                ?? activeInstance?.RenderState.CurrentRenderTargetBinding?.Name
                ?? activeInstance?.RenderState.OutputFBO?.Name
                ?? RenderGraphResourceNames.OutputRenderTarget;

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

        protected static string BuildQuadBlitPassName(string sourceFboName, string destination, string? variant = null)
            => string.IsNullOrWhiteSpace(variant)
                ? $"QuadBlit_{sourceFboName}_to_{destination}"
                : $"QuadBlit_{variant}_{sourceFboName}_to_{destination}";

        private static string ResolveDescriptorResourceName(EDescriptorResourceKind kind, string name)
            => kind switch
            {
                EDescriptorResourceKind.Texture => MakeTextureResource(name),
                EDescriptorResourceKind.FboColor => MakeFboColorResource(name),
                EDescriptorResourceKind.ColorTarget => MakeColorTargetResource(name),
                _ => name,
            };

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

        private static string GetShaderDisplayName(XRShader? shader)
        {
            if (shader is null)
                return string.Empty;

            string? path = shader.Source?.FilePath ?? shader.FilePath;
            if (!string.IsNullOrWhiteSpace(path))
                return Path.GetFileName(path);

            return shader.Name ?? string.Empty;
        }

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
                if (_diagEnabled)
                    Debug.RenderingWarning($"[QuadBlitDiag] Source FBO '{SourceQuadFBOName}' not found as XRQuadFrameBuffer.");
                return;
            }

            XRFrameBuffer? destFBO = ResolveDestinationFbo(activeInstance, sourceFBO);
            if (_diagEnabled && DestinationFBOName is not null && destFBO is null)
                Debug.RenderingWarning($"[QuadBlitDiag] Dest FBO '{DestinationFBOName}' not found.");

            if (_diagEnabled)
            {
                bool hasTargets = destFBO?.Targets is { Length: > 0 };
                Debug.Log(ELogCategory.Rendering, $"[QuadBlitDiag] Rendering '{SourceQuadFBOName}' → '{DestinationFBOName ?? "<current>"}' (dest has targets: {hasTargets}, dest type: {destFBO?.GetType().Name ?? "null"})");
            }

            using var renderAreaScope = MatchDestinationRenderArea && TryResolveDestinationRenderArea(destFBO, out int renderWidth, out int renderHeight)
                ? activeInstance.RenderState.PushRenderArea(renderWidth, renderHeight)
                : default;

            sourceFBO.Render(destFBO);
        }

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

        private static bool TryDescribeActualColorOutputs(
            RenderPassBuilder builder,
            string destination,
            ERenderGraphAccess access,
            ERenderPassLoadOp colorLoad,
            ERenderPassStoreOp colorStore)
        {
            XRRenderPipelineInstance? instance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            if (instance is null || !instance.TryGetFBO(destination, out XRFrameBuffer? fbo) || fbo?.Targets is not { Length: > 0 } targets)
                return false;

            bool described = false;
            for (int i = 0; i < targets.Length; i++)
            {
                var (target, attachment, mipLevel, _) = targets[i];
                if (!IsColorAttachment(attachment) || target is not XRTexture texture || string.IsNullOrWhiteSpace(texture.Name))
                    continue;

                uint mip = mipLevel > 0 ? (uint)mipLevel : 0u;
                if (mip == 0u)
                    builder.UseColorAttachment(MakeTextureResource(texture.Name), access, colorLoad, colorStore);
                else
                    builder.UseColorAttachmentMip(MakeTextureResource(texture.Name), mip, access, colorLoad, colorStore);

                described = true;
            }

            return described;
        }

        private static bool TryDescribeActualMaterialTextures(RenderPassBuilder builder, string sourceFboName)
        {
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
                if (texture is null || string.IsNullOrWhiteSpace(texture.Name))
                    continue;

                builder.SampleTexture(MakeTextureResource(texture.Name));
                described = true;
            }

            return described;
        }

        protected static bool TryDescribeActualFboColorInputs(RenderPassBuilder builder, string sourceFboName)
        {
            XRRenderPipelineInstance? instance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            if (instance is null ||
                !instance.TryGetFBO(sourceFboName, out XRFrameBuffer? fbo) ||
                fbo?.Targets is not { Length: > 0 } targets)
            {
                return false;
            }

            bool described = false;
            for (int i = 0; i < targets.Length; i++)
            {
                var (target, attachment, mipLevel, _) = targets[i];
                if (!IsColorAttachment(attachment) || target is not XRTexture texture || string.IsNullOrWhiteSpace(texture.Name))
                    continue;

                uint mip = mipLevel > 0 ? (uint)mipLevel : 0u;
                if (mip == 0u)
                    builder.SampleTexture(MakeTextureResource(texture.Name));
                else
                    builder.SampleTextureMip(MakeTextureResource(texture.Name), mip);

                described = true;
            }

            return described;
        }

        private static bool IsColorAttachment(EFrameBufferAttachment attachment)
            => attachment >= EFrameBufferAttachment.ColorAttachment0 &&
               attachment <= EFrameBufferAttachment.ColorAttachment31;
    }
}
