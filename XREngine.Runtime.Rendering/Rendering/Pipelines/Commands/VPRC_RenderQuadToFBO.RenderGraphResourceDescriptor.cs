using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    public partial class VPRC_RenderQuadToFBO
    {
        /// <summary>
        /// Describes the resources used by a render-graph pass that renders a quad from one FBO to another.
        /// </summary>
        public sealed class RenderGraphResourceDescriptor
        {
            private readonly List<SampledTextureUsage> _sampledTextures = new();
            private readonly List<BufferUsage> _readBuffers = new();
            private readonly List<ColorAttachmentUsage> _colorAttachments = new();
            private readonly List<int> _dependencies = new();
            private readonly List<QuadBlitDependency> _quadBlitDependencies = new();
            private readonly List<DependentPass> _dependentPasses = new();

            /// <summary>
            /// Gets or sets the stage of the render-graph pass. 
            /// This is used to determine when the pass will be executed in the render-graph.
            /// </summary>
            public ERenderGraphPassStage Stage { get; set; } = ERenderGraphPassStage.Graphics;
            /// <summary>
            /// Gets or sets a value indicating whether the render-graph pass should use the destination FBO's depth-stencil attachment.
            /// </summary>
            public bool UseDestinationDepthStencil { get; set; }

            internal bool HasExplicitColorAttachments => _colorAttachments.Count > 0;

            /// <summary>
            /// Adds a sampled texture to the render-graph pass descriptor.
            /// The texture will be bound as a sampled texture in the shader.
            /// </summary>
            /// <param name="textureName">The name of the texture to sample.</param>
            /// <returns>The current render-graph resource descriptor.</returns>
            public RenderGraphResourceDescriptor SampleTexture(string textureName)
            {
                _sampledTextures.Add(new(textureName, EDescriptorResourceKind.Texture, null, 0u));
                return this;
            }

            /// <summary>
            /// Adds a sampled texture to the render-graph pass descriptor, specifying a mip level to sample.
            /// The texture will be bound as a sampled texture in the shader.
            /// </summary>
            /// <param name="textureName">The name of the texture to sample.</param>
            /// <param name="mipLevel">The mip level of the texture to sample.</param>
            /// <returns>The current render-graph resource descriptor.</returns>
            public RenderGraphResourceDescriptor SampleTextureMip(string textureName, uint mipLevel)
            {
                _sampledTextures.Add(new(textureName, EDescriptorResourceKind.Texture, mipLevel, 1u));
                return this;
            }

            /// <summary>
            /// Adds a sampled texture to the render-graph pass descriptor, specifying a range of mip levels to sample.
            /// The texture will be bound as a sampled texture in the shader.
            /// </summary>
            /// <param name="textureName">The name of the texture to sample.</param>
            /// <param name="baseMipLevel">The base mip level of the texture to sample.</param>
            /// <param name="mipLevelCount">The number of mip levels to sample.</param>
            /// <returns>The current render-graph resource descriptor.</returns>
            public RenderGraphResourceDescriptor SampleTextureMips(string textureName, uint baseMipLevel, uint mipLevelCount)
            {
                _sampledTextures.Add(new(textureName, EDescriptorResourceKind.Texture, baseMipLevel, mipLevelCount));
                return this;
            }

            /// <summary>
            /// Adds a sampled FBO color attachment to the render-graph pass descriptor.
            /// The FBO color attachment will be bound as a sampled texture in the shader.
            /// </summary>
            /// <param name="fboName">The name of the FBO color attachment to sample.</param>
            /// <returns>The current render-graph resource descriptor.</returns>
            public RenderGraphResourceDescriptor SampleFboColor(string fboName)
            {
                _sampledTextures.Add(new(fboName, EDescriptorResourceKind.FboColor, null, 0u));
                return this;
            }

            /// <summary>
            /// Adds a sampled FBO color attachment to the render-graph pass descriptor, specifying a mip level to sample.
            /// The FBO color attachment will be bound as a sampled texture in the shader.
            /// </summary>
            /// <param name="bufferName">The name of the buffer to read.</param>
            /// <param name="bufferType">The type of the buffer to read.</param>
            /// <returns>The current render-graph resource descriptor.</returns>
            public RenderGraphResourceDescriptor ReadBuffer(string bufferName, ERenderPassResourceType bufferType = ERenderPassResourceType.StorageBuffer)
            {
                _readBuffers.Add(new(bufferName, bufferType));
                return this;
            }

            /// <summary>
            /// Adds a color attachment to the render-graph pass descriptor.
            /// </summary>
            /// <param name="textureName">The name of the texture to use as a color attachment.</param>
            /// <param name="access">The access type for the color attachment.</param>
            /// <param name="load">The load operation for the color attachment.</param>
            /// <param name="store">The store operation for the color attachment.</param>
            /// <returns>The current render-graph resource descriptor.</returns>
            public RenderGraphResourceDescriptor UseColorTexture(
                string textureName,
                ERenderGraphAccess? access = null,
                ERenderPassLoadOp? load = null,
                ERenderPassStoreOp? store = null)
            {
                _colorAttachments.Add(new(textureName, EDescriptorResourceKind.Texture, null, access, load, store));
                return this;
            }

            /// <summary>
            /// Adds a sampled FBO color attachment to the render-graph pass descriptor, specifying a mip level to sample.
            /// The FBO color attachment will be bound as a sampled texture in the shader.
            /// </summary>
            /// <param name="textureName">The name of the texture to use as a color attachment.</param>
            /// <param name="mipLevel">The mip level of the texture to use.</param>
            /// <param name="access">The access type for the color attachment.</param>
            /// <param name="load">The load operation for the color attachment.</param>
            /// <param name="store">The store operation for the color attachment.</param>
            /// <returns>The current render-graph resource descriptor.</returns>
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

            /// <summary>
            /// Adds a color attachment to the render-graph pass descriptor, specifying an FBO color attachment to use.
            /// </summary>
            /// <param name="fboName">The name of the FBO to use as a color attachment.</param>
            /// <param name="access">The access type for the color attachment.</param>
            /// <param name="load">The load operation for the color attachment.</param>
            /// <param name="store">The store operation for the color attachment.</param>
            /// <returns>The current render-graph resource descriptor.</returns>
            public RenderGraphResourceDescriptor UseColorFbo(
                string fboName,
                ERenderGraphAccess? access = null,
                ERenderPassLoadOp? load = null,
                ERenderPassStoreOp? store = null)
            {
                _colorAttachments.Add(new(fboName, EDescriptorResourceKind.FboColor, null, access, load, store));
                return this;
            }

            /// <summary>
            /// Adds a color attachment to the render-graph pass descriptor, specifying a color target to use.
            /// </summary>
            /// <param name="targetName">The name of the color target to use as a color attachment.</param>
            /// <param name="access">The access type for the color attachment.</param>
            /// <param name="load">The load operation for the color attachment.</param>
            /// <param name="store">The store operation for the color attachment.</param>
            /// <returns>The current render-graph resource descriptor.</returns>
            public RenderGraphResourceDescriptor UseColorTarget(
                string targetName,
                ERenderGraphAccess? access = null,
                ERenderPassLoadOp? load = null,
                ERenderPassStoreOp? store = null)
            {
                _colorAttachments.Add(new(targetName, EDescriptorResourceKind.ColorTarget, null, access, load, store));
                return this;
            }

            /// <summary>
            /// Adds a dependency on another render-graph pass to the current render-graph pass descriptor.
            /// </summary>
            /// <param name="passIndex">The index of the render-graph pass to depend on.</param>
            /// <returns>The current render-graph resource descriptor.</returns>
            public RenderGraphResourceDescriptor DependsOn(int passIndex)
            {
                _dependencies.Add(passIndex);
                return this;
            }

            /// <summary>
            /// Adds a dependency on a quad blit operation to the current render-graph pass descriptor.
            /// </summary>
            /// <param name="sourceFboName">The name of the source FBO for the quad blit.</param>
            /// <param name="destinationFboName">The name of the destination FBO for the quad blit.</param>
            /// <param name="variant">An optional variant for the quad blit operation.</param>
            /// <returns>The current render-graph resource descriptor.</returns>
            public RenderGraphResourceDescriptor DependsOnQuadBlit(string sourceFboName, string destinationFboName, string? variant = null)
            {
                _quadBlitDependencies.Add(new(sourceFboName, destinationFboName, variant));
                return this;
            }

            /// <summary>
            /// Adds a dependency on another render-graph pass to the current render-graph pass descriptor, specifying the stage of the dependent pass.
            /// </summary>
            /// <param name="passIndex">The index of the render-graph pass to depend on.</param>
            /// <param name="passName">The name of the render-graph pass to depend on.</param>
            /// <param name="stage">The stage of the render-graph pass to depend on.</param>
            /// <returns>The current render-graph resource descriptor.</returns>
            public RenderGraphResourceDescriptor MakePassDependOnThis(
                int passIndex,
                string passName,
                ERenderGraphPassStage stage = ERenderGraphPassStage.Graphics)
            {
                _dependentPasses.Add(new(passIndex, passName, stage));
                return this;
            }

            /// <summary>
            /// Specifies whether the render-graph pass should use the destination FBO's depth-stencil attachment.
            /// </summary>
            /// <param name="enabled">Whether to use the destination FBO's depth-stencil attachment.</param>
            /// <returns>The current render-graph resource descriptor.</returns>
            public RenderGraphResourceDescriptor UseDestinationDepthStencilAttachments(bool enabled = true)
            {
                UseDestinationDepthStencil = enabled;
                return this;
            }

            /// <summary>
            /// Describes the dependencies of the render-graph pass to the render-graph builder.
            /// </summary>
            /// <param name="context">The render-graph describe context.</param>
            /// <param name="builder">The render-pass builder.</param>
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

            /// <summary>
            /// Describes the inputs of the render-graph pass to the render-pass builder.
            /// </summary>
            /// <param name="builder">The render-pass builder.</param>
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

            /// <summary>
            /// Describes the color attachments of the render-graph pass to the render-pass builder.
            /// </summary>
            /// <param name="builder">The render-pass builder.</param>
            /// <param name="defaultAccess">The default access type for the color attachments.</param>
            /// <param name="defaultLoad">The default load operation for the color attachments.</param>
            /// <param name="defaultStore">The default store operation for the color attachments.</param>
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

            /// <summary>
            /// Describes a sampled texture usage in the render-graph pass descriptor.
            /// </summary>
            /// <param name="Name">The name of the sampled texture.</param>
            /// <param name="Kind">The kind of the descriptor resource.</param>
            /// <param name="BaseMipLevel">The base mip level of the texture.</param>
            /// <param name="MipLevelCount">The number of mip levels.</param>
            private readonly record struct SampledTextureUsage(
                string Name,
                EDescriptorResourceKind Kind,
                uint? BaseMipLevel,
                uint MipLevelCount);

            /// <summary>
            /// Describes a buffer usage in the render-graph pass descriptor.
            /// </summary>
            /// <param name="Name">The name of the buffer.</param>
            /// <param name="Type">The type of the buffer.</param>
            private readonly record struct BufferUsage(string Name, ERenderPassResourceType Type);

            /// <summary>
            /// Describes a color attachment usage in the render-graph pass descriptor.
            /// </summary>
            /// <param name="Name">The name of the color attachment.</param>
            /// <param name="Kind">The kind of the descriptor resource.</param>
            /// <param name="MipLevel">The mip level of the color attachment.</param>
            /// <param name="Access">The access type of the color attachment.</param>
            /// <param name="Load">The load operation of the color attachment.</param>
            /// <param name="Store">The store operation of the color attachment.</param>
            private readonly record struct ColorAttachmentUsage(
                string Name,
                EDescriptorResourceKind Kind,
                uint? MipLevel,
                ERenderGraphAccess? Access,
                ERenderPassLoadOp? Load,
                ERenderPassStoreOp? Store);

            /// <summary>
            /// Describes a quad blit dependency in the render-graph pass descriptor.
            /// </summary>
            /// <param name="SourceFboName">The name of the source framebuffer object.</param>
            /// <param name="DestinationFboName">The name of the destination framebuffer object.</param>
            /// <param name="Variant">The variant of the quad blit operation.</param>
            private readonly record struct QuadBlitDependency(string SourceFboName, string DestinationFboName, string? Variant);

            /// <summary>
            /// Describes a dependent pass in the render-graph pass descriptor.
            /// </summary>
            /// <param name="PassIndex">The index of the dependent pass.</param>
            /// <param name="PassName">The name of the dependent pass.</param>
            /// <param name="Stage">The stage of the dependent pass.</param>
            private readonly record struct DependentPass(int PassIndex, string PassName, ERenderGraphPassStage Stage);
        }
    }
}
