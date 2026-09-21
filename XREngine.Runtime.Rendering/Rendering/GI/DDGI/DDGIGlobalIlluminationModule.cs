using System.Runtime.InteropServices;
using XREngine.Components.Lights;
using XREngine.Data.Rendering;
using XREngine.Rendering.GI.Contracts;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>
/// Provider-owned DDGI resources and pass sequence. Hosts supply only their neutral surface bindings and phase anchor.
/// </summary>
public sealed class DDGIGlobalIlluminationModule : IGlobalIlluminationModule
{
    public GlobalIlluminationProviderDescriptor Descriptor
        => GlobalIlluminationProviderRegistry.GetRequiredDescriptor(EGlobalIlluminationMode.DDGI);

    public GlobalIlluminationSupportResult EvaluateSupport(in GlobalIlluminationModuleContext context)
        => context.Plan.Support;

    /// <summary>
    /// Freezes the selected authored field layout into the generation key.
    /// This cold-path decision belongs to DDGI because hosts must not know
    /// about probe dimensions, ray budgets, or baked atlas layout.
    /// </summary>
    public RenderPipelineResourceVariant BuildResourceVariant(XRViewport? viewport)
    {
        var world = viewport?.World;
        return world is not null &&
            DDGIVolumeComponent.Registry.TrySelectActive(world, out DDGIVolumeComponent? volume) &&
            volume is not null
            ? BuildResourceDescriptor(volume).ToVariant()
            : DDGIResourceDescriptor.Default.ToVariant();
    }

    public void DeclareResources(RenderPipelineResourceLayoutBuilder builder, in GlobalIlluminationModuleContext context)
    {
        uint layers = Math.Max(builder.Profile.ViewCount, builder.Profile.Stereo ? 2u : 1u);
        uint width = Math.Max(1u, builder.Profile.InternalWidth);
        uint height = Math.Max(1u, builder.Profile.InternalHeight);

        builder.Texture(DDGIResourceNames.ScreenDiffuse)
            .Size(RenderResourceSizePolicy.Internal())
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.StorageImage)
            .Format(EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
            .SizedFormat(ESizedInternalFormat.Rgba16f)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .RequiresStorageUsage()
            .Factory(() => CreateScreenDiffuseTexture(width, height, layers))
            .Add();

        builder.QuadMaterial(DDGIResourceNames.CompositeMaterial)
            .Lifetime(RenderResourceLifetime.Transient)
            .DependsOn(DDGIResourceNames.ScreenDiffuse)
            .Factory(() => CreateCompositeMaterial(builder.Profile.Stereo))
            .Add();

        DDGIResourceImports.Declare(builder, _ => true);
        DDGIResourceDescriptor descriptor = DDGIResourceDescriptor.FromVariant(builder.Profile.ResourceVariant);

        DeclareBuffer(builder, DDGILightResources.BufferName, (uint)Marshal.SizeOf<DDGILightGPU>(),
            DDGILightResources.Capacity + 1u, DDGILightResources.CreateBuffer, EBufferTarget.UniformBuffer,
            EBufferUsage.StreamDraw);

        builder.Texture(DDGIEnvironmentResources.TextureName)
            .Size(RenderResourceSizePolicy.Absolute(DDGIEnvironmentResources.Resolution, DDGIEnvironmentResources.Resolution))
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.ColorAttachment)
            .Format(EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
            .SizedFormat(ESizedInternalFormat.Rgba16f)
            .Factory(DDGIEnvironmentResources.CreateRadianceTexture)
            .Add();

        DeclareAtlas(builder, DDGIResourceNames.IrradianceAtlas, descriptor.IrradianceWidth, descriptor.IrradianceHeight,
            descriptor.Cascades, EPixelInternalFormat.R11fG11fB10f, EPixelFormat.Rgb, EPixelType.Float,
            ESizedInternalFormat.R11fG11fB10f, () => DDGIVolumeRuntimeState.CreateIrradianceAtlasTextureArray(
                (uint)descriptor.Cascades, checked((int)descriptor.IrradianceWidth), checked((int)descriptor.IrradianceHeight)));
        DeclareAtlas(builder, DDGIResourceNames.VisibilityAtlas, descriptor.VisibilityWidth, descriptor.VisibilityHeight,
            descriptor.Cascades, EPixelInternalFormat.RG16f, EPixelFormat.Rg, EPixelType.HalfFloat,
            ESizedInternalFormat.Rg16f, () => DDGIVolumeRuntimeState.CreateVisibilityAtlasTextureArray(
                (uint)descriptor.Cascades, checked((int)descriptor.VisibilityWidth), checked((int)descriptor.VisibilityHeight)));

        DeclareBuffer(builder, DDGIResourceNames.ProbeStateBuffer, (uint)Marshal.SizeOf<DDGIProbeGPU>(), descriptor.ProbeElements,
            () => DDGIVolumeRuntimeState.CreateDeclaredProbeBuffer(descriptor.ProbeElements));
        DeclareBuffer(builder, DDGIResourceNames.RayBuffer, (uint)Marshal.SizeOf<DDGIRayGPU>(), descriptor.RayElements,
            () => DDGIVolumeRuntimeState.CreateDeclaredRayBuffer(descriptor.RayElements));
        DeclareBuffer(builder, DDGIResourceNames.HitBuffer, (uint)Marshal.SizeOf<DDGIHitGPU>(), descriptor.RayElements,
            () => DDGIVolumeRuntimeState.CreateDeclaredHitBuffer(descriptor.RayElements));
        DeclareBuffer(builder, DDGIResourceNames.RayRadianceBuffer, (uint)Marshal.SizeOf<DDGIRayRadianceGPU>(), descriptor.RayElements,
            () => DDGIVolumeRuntimeState.CreateDeclaredRayRadianceBuffer(descriptor.RayElements));
    }

    public void ContributePasses(ViewportRenderCommandContainer commands, in GlobalIlluminationModuleContext context)
    {
        if (context.Anchor != EGlobalIlluminationExecutionAnchor.SurfaceResolve || context.Resources is not { } resources)
            return;

        commands.Add<VPRC_BuildAccelerationStructure>();
        commands.Add<VPRC_DDGIEnvironmentPass>();
        commands.Add<VPRC_DDGIPrepareGeometryPass>();
        commands.Add<VPRC_DDGIRaygenPass>();
        commands.Add<VPRC_DDGITracePass>();
        commands.Add<VPRC_DDGIHitShadePass>();
        commands.Add<VPRC_DDGIRelocatePass>();
        commands.Add<VPRC_DDGIUpdateIrradiancePass>();
        commands.Add<VPRC_DDGIUpdateVisibilityPass>();
        commands.Add<VPRC_DDGIBorderCopyPass>();

        VPRC_DDGICompositePass composite = commands.Add<VPRC_DDGICompositePass>();
        composite.DepthTextureName = resources.DepthTexture;
        composite.NormalTextureName = resources.NormalTexture;
        composite.AlbedoTextureName = resources.AlbedoTexture;
        composite.RMSETextureName = resources.RmseTexture;
        composite.AmbientOcclusionTextureName = resources.AmbientOcclusionTexture;
        composite.OutputTextureName = DDGIResourceNames.ScreenDiffuse;
        composite.IrradianceAtlasTextureName = DDGIResourceNames.IrradianceAtlas;
        composite.VisibilityAtlasTextureName = DDGIResourceNames.VisibilityAtlas;
        composite.ProbeStateBufferName = DDGIResourceNames.ProbeStateBuffer;

        VPRC_GlobalIlluminationCompositePass presentation = commands.Add<VPRC_GlobalIlluminationCompositePass>();
        presentation.SourceQuadFBOName = DDGIResourceNames.CompositeMaterial;
        presentation.DestinationFBOName = resources.CompositionTarget;
        presentation.OutputTextureName = DDGIResourceNames.ScreenDiffuse;
        presentation.ProducerPassName = nameof(VPRC_DDGICompositePass);
        commands.Add<VPRC_DDGICompositeCompletionPass>();

        VPRC_DDGIDebugVisualization debug = commands.Add<VPRC_DDGIDebugVisualization>();
        debug.ProbeStateBufferName = DDGIResourceNames.ProbeStateBuffer;
        debug.ForwardFBOName = resources.CompositionTarget;
    }

    public void Invalidate(string reason) { }
    public void Release() { }

    private static DDGIResourceDescriptor BuildResourceDescriptor(DDGIVolumeComponent volume)
        => volume.UpdateMode == EDDGIUpdateMode.Baked && volume.BakedAsset is { } asset
            ? new DDGIResourceDescriptor(
                asset.ProbeCounts,
                volume.RaysPerProbe,
                asset.CascadeCount,
                volume.MaxProbesUpdatedPerFrame)
            : DDGIResourceDescriptor.FromVolume(volume);

    private static XRTexture CreateScreenDiffuseTexture(uint width, uint height, uint layers)
    {
        XRTexture texture;
        if (layers > 1u)
        {
            var array = XRTexture2DArray.CreateFrameBufferTexture(layers, width, height,
                EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat);
            array.OVRMultiViewParameters = new(0, layers);
            array.Resizable = false;
            array.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            array.MinFilter = ETexMinFilter.Linear;
            array.MagFilter = ETexMagFilter.Linear;
            texture = array;
        }
        else
        {
            var texture2D = XRTexture2D.CreateFrameBufferTexture(width, height,
                EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat);
            texture2D.Resizable = false;
            texture2D.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            texture2D.MinFilter = ETexMinFilter.Linear;
            texture2D.MagFilter = ETexMagFilter.Linear;
            texture = texture2D;
        }

        texture.Name = DDGIResourceNames.ScreenDiffuse;
        texture.SamplerName = DDGIResourceNames.ScreenDiffuse;
        return texture;
    }

    private static XRFrameBuffer CreateCompositeMaterial(bool stereo)
    {
        XRRenderPipelineInstance pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline
            ?? throw new InvalidOperationException("DDGI composition requires an active render-pipeline instance.");
        XRTexture source = pipeline.GetTexture<XRTexture>(DDGIResourceNames.ScreenDiffuse)
            ?? throw new InvalidOperationException("DDGI composition source was not declared by the active pipeline.");
        XRShader shader = XRShader.EngineShader(
            $"Scene3D/{(stereo ? "DDGICompositeStereo.fs" : "DDGIComposite.fs")}", EShaderType.Fragment);
        XRMaterial material = new([source], shader)
        {
            RenderOptions = new RenderingParameters
            {
                DepthTest = new DepthTest
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
                BlendModeAllDrawBuffers = new BlendMode
                {
                    Enabled = ERenderParamUsage.Enabled,
                    RgbSrcFactor = EBlendingFactor.One,
                    AlphaSrcFactor = EBlendingFactor.One,
                    RgbDstFactor = EBlendingFactor.One,
                    AlphaDstFactor = EBlendingFactor.One,
                    RgbEquation = EBlendEquationMode.FuncAdd,
                    AlphaEquation = EBlendEquationMode.FuncAdd,
                },
            },
        };
        var frameBuffer = new XRQuadFrameBuffer(material, useMultiview: stereo)
        {
            Name = DDGIResourceNames.CompositeMaterial,
        };
        frameBuffer.SettingUniforms += program =>
        {
            var region = RuntimeEngine.Rendering.State.RenderingPipelineState?.CurrentRenderRegion;
            program.Uniform("ScreenWidth", region?.Width > 0 ? (float)region.Value.Width : source.WidthHeightDepth.X);
            program.Uniform("ScreenHeight", region?.Height > 0 ? (float)region.Value.Height : source.WidthHeightDepth.Y);
        };
        return frameBuffer;
    }

    private static void DeclareAtlas(
        RenderPipelineResourceLayoutBuilder builder, string name, uint width, uint height, int layers,
        EPixelInternalFormat internalFormat, EPixelFormat pixelFormat, EPixelType pixelType,
        ESizedInternalFormat sizedFormat, Func<XRTexture> factory)
        => builder.Texture(name)
            .Size(RenderResourceSizePolicy.Absolute(width, height))
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.StorageImage)
            .Format(internalFormat, pixelFormat, pixelType)
            .SizedFormat(sizedFormat)
            .Layers((uint)layers)
            .RequiresStorageUsage()
            .Factory(factory)
            .Add();

    private static void DeclareBuffer(
        RenderPipelineResourceLayoutBuilder builder, string name, uint stride, uint elements, Func<XRDataBuffer> factory)
        => DeclareBuffer(builder, name, stride, elements, factory, EBufferTarget.ShaderStorageBuffer, EBufferUsage.DynamicDraw);

    private static void DeclareBuffer(
        RenderPipelineResourceLayoutBuilder builder, string name, uint stride, uint elements, Func<XRDataBuffer> factory,
        EBufferTarget target, EBufferUsage usage)
        => builder.Buffer(name)
            .Size(RenderResourceSizePolicy.Internal())
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.StorageBuffer)
            .BufferFormat((ulong)stride * elements, target, usage)
            .Elements(stride, elements)
            .Factory(factory)
            .Add();
}
