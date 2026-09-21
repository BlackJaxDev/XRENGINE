using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Core;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>
/// Renders each pipeline's active authored skybox into a GPU-resident octahedral
/// radiance map consumed by DDGI hit shading. No environment texels are read on
/// the CPU, so dynamic and render-target skyboxes retain their current GPU content.
/// </summary>
internal sealed class DDGIEnvironmentResources
{
    public const string TextureName = "DDGIEnvironmentRadiance";
    public const uint Resolution = 256;
    private static readonly ConditionalWeakTable<XRRenderPipelineInstance, DDGIEnvironmentResources> Resources = new();

    private XRTexture2D? _radiance;
    private XRTexture2D? _captureTarget;
    private XRQuadFrameBuffer? _frameBuffer;
    private XRMaterial? _material;
    private SkyboxComponent? _skybox;
    private XRTexture? _sourceTexture;
    private ESkyboxMode _skyboxMode;
    private ESkyboxProjection _skyboxProjection;
    private bool _usesSkyboxFragment;
    private IRuntimeRenderWorld? _world;
    private ulong _preparedFrame = ulong.MaxValue;
    private bool _isAvailable;

    internal static bool IsAvailable(XRRenderPipelineInstance pipeline)
        => Resources.TryGetValue(pipeline, out DDGIEnvironmentResources? resources) && resources._isAvailable;

    private DDGIEnvironmentResources(XRRenderPipelineInstance owner)
    {
        owner.CacheClearing += Clear;
    }

    /// <summary>
    /// Executes the graphics-side environment capture for this pipeline. Call
    /// from a graphics render-graph scope before DDGI hit shading.
    /// </summary>
    public static bool Prepare(IRuntimeRenderWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        XRRenderPipelineInstance pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline
            ?? throw new InvalidOperationException("DDGI environment binding requires an active render pipeline.");
        return Resources.GetValue(pipeline, static instance => new DDGIEnvironmentResources(instance)).PrepareCore(pipeline, world);
    }

    /// <summary>Equivalent preparation overload for passes that already own their pipeline instance.</summary>
    public static bool Prepare(XRRenderPipelineInstance pipeline, IRuntimeRenderWorld world)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(world);
        return Resources.GetValue(pipeline, static instance => new DDGIEnvironmentResources(instance)).PrepareCore(pipeline, world);
    }

    /// <summary>
    /// Binds the graphics-prepared environment at sampler unit five. It never
    /// submits graphics work from a compute pass.
    /// </summary>
    public static bool Bind(XRRenderProgram program, IRuntimeRenderWorld world)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(world);
        XRRenderPipelineInstance pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline
            ?? throw new InvalidOperationException("DDGI environment binding requires an active render pipeline.");
        return Resources.GetValue(pipeline, static instance => new DDGIEnvironmentResources(instance)).BindCore(program, world);
    }

    public static bool Bind(XRRenderProgram program, XRRenderPipelineInstance pipeline, IRuntimeRenderWorld world)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(world);
        return Resources.GetValue(pipeline, static instance => new DDGIEnvironmentResources(instance)).BindCore(program, world);
    }

    private bool BindCore(XRRenderProgram program, IRuntimeRenderWorld world)
    {
        Vector3 fallback = world.GetEffectiveAmbientColor();
        bool available = _preparedFrame == RuntimeEngine.Rendering.State.RenderFrameId &&
            ReferenceEquals(_world, world) && _isAvailable;
        if (_radiance is not null)
            program.Sampler("uDDGIEnvironment", _radiance, 5);
        program.Uniform("uDDGIEnvironmentAvailable", available);
        program.Uniform("uDDGIEnvironmentFallback", fallback);
        return available;
    }

    private bool PrepareCore(XRRenderPipelineInstance pipeline, IRuntimeRenderWorld world)
    {
        ulong frame = RuntimeEngine.Rendering.State.RenderFrameId;
        if (_preparedFrame == frame && ReferenceEquals(_world, world))
            return _isAvailable;

        _preparedFrame = frame;
        _world = world;
        _radiance = pipeline.GetTexture<XRTexture2D>(TextureName);
        SkyboxComponent.Registry.TryGetFirstActive(world, out SkyboxComponent? skybox);
        EnsureResources(skybox);
        if (_frameBuffer is null || _radiance is null)
            return _isAvailable = false;
        if (!_frameBuffer.TryPrepareForRendering(forceNoStereo: true))
            return _isAvailable = false;

        int extent = checked((int)Resolution);
        var pipelineState = RuntimeEngine.Rendering.State.RenderingPipelineState;
        BoundingRectangle previousCrop = pipelineState?.CurrentCropRegion ?? BoundingRectangle.Empty;
        bool restoreCrop = previousCrop.Width > 0 && previousCrop.Height > 0;
        bool rendered;
        using (_frameBuffer.BindForWritingState())
        {
            AbstractRenderer.Current?.SetCroppingEnabled(false);
            using StateObject? renderArea = pipelineState?.PushRenderArea(extent, extent);
            if (renderArea is null)
                AbstractRenderer.Current?.SetRenderArea(new BoundingRectangle(IVector2.Zero, new IVector2(extent, extent)));
            RuntimeEngine.Rendering.State.ClearColor(ColorF4.Black);
            RuntimeEngine.Rendering.State.ClearByBoundFBO();
            rendered = _frameBuffer.Render(null, true);
        }
        if (restoreCrop)
        {
            AbstractRenderer.Current?.SetCroppingEnabled(true);
            AbstractRenderer.Current?.CropRenderArea(previousCrop);
        }
        if (!rendered)
            return _isAvailable = false;

        AbstractRenderer.Current?.PublishFrameBufferAttachmentsForSampling(_frameBuffer);
        AbstractRenderer.Current?.MemoryBarrier(
            EMemoryBarrierMask.Framebuffer |
            EMemoryBarrierMask.TextureFetch |
            EMemoryBarrierMask.TextureUpdate);
        return _isAvailable = true;
    }

    private void EnsureResources(SkyboxComponent? skybox)
    {
        XRTexture? source = skybox?.EnvironmentTexture;
        ESkyboxMode mode = skybox?.Mode ?? ESkyboxMode.Gradient;
        ESkyboxProjection projection = skybox?.Projection ?? ESkyboxProjection.Equirectangular;
        bool useSkyboxFragment = skybox is not null && (mode != ESkyboxMode.Texture || source is not null);
        if (_frameBuffer is not null && ReferenceEquals(_captureTarget, _radiance) && ReferenceEquals(_skybox, skybox) && ReferenceEquals(_sourceTexture, source) &&
            _skyboxMode == mode && _skyboxProjection == projection && _usesSkyboxFragment == useSkyboxFragment)
            return;

        XRShader vertex = ShaderHelper.LoadEngineShader("Scene3D/DDGIEnvironmentOcta.vs", EShaderType.Vertex);
        XRShader fragment = useSkyboxFragment
            ? skybox!.GetEnvironmentRadianceShader()!
            : ShaderHelper.LoadEngineShader("Scene3D/SkyboxGradient.fs", EShaderType.Fragment);
        fragment = ShaderHelper.CreateDefinedShaderVariant(fragment, "XRENGINE_DDGI_ENVIRONMENT_CAPTURE")
            ?? throw new InvalidOperationException("DDGI environment capture could not create its sky shader variant.");
        ReleaseCaptureObjects();
        _skybox = useSkyboxFragment ? skybox : null;
        _sourceTexture = useSkyboxFragment ? source : null;
        _skyboxMode = mode;
        _skyboxProjection = projection;
        _usesSkyboxFragment = useSkyboxFragment;
        _captureTarget = _radiance;
        RenderingParameters options = new()
        {
            CullMode = ECullMode.None,
            DepthTest = new() { Enabled = ERenderParamUsage.Disabled },
            StencilTest = new() { Enabled = ERenderParamUsage.Disabled },
            BlendModeAllDrawBuffers = BlendMode.Disabled(),
            ExcludeFromGpuIndirect = true,
            WriteAlpha = true,
        };
        _material = new XRMaterial(_sourceTexture is null ? [] : [_sourceTexture], vertex, fragment)
        {
            Name = "DDGI.EnvironmentRadiance",
            RenderOptions = options,
        };
        _material.SettingUniforms += BindSkyboxUniforms;

        _frameBuffer?.Destroy();
        _frameBuffer = new XRQuadFrameBuffer(_material)
        {
            Name = "DDGI.EnvironmentRadiance",
        };
        _frameBuffer.FullScreenMesh.SetShaderPipelinesAllowedForAllVersions(false);
        XRTexture2D radiance = _radiance
            ?? throw new InvalidOperationException("DDGI environment target was not declared by the active pipeline.");
        _frameBuffer.SetRenderTargets((radiance, EFrameBufferAttachment.ColorAttachment0, 0, -1));
    }

    private void BindSkyboxUniforms(XRMaterialBase _, XRRenderProgram program)
    {
        program.Uniform("uDDGIEnvironmentInvResolution", new Vector2(1.0f / Resolution));
        program.Uniform("uDDGIEnvironmentRotation", (_skybox?.Rotation ?? 0.0f) * MathF.PI / 180.0f);
        if (_skybox is not null)
        {
            _skybox.BindEnvironmentUniforms(program);
            if (_sourceTexture is not null)
                program.Sampler("Texture0", _sourceTexture, 0);
            return;
        }

        Vector3 ambient = _world?.GetEffectiveAmbientColor() ?? Vector3.Zero;
        program.Uniform("SkyboxIntensity", 1.0f);
        program.Uniform("SkyboxRotation", 0.0f);
        program.Uniform("SkyboxTopColor", ambient);
        program.Uniform("SkyboxBottomColor", ambient);
    }

    private void Clear()
    {
        ReleaseCaptureObjects();
        _radiance = null;
        _captureTarget = null;
        _skybox = null;
        _sourceTexture = null;
        _world = null;
        _preparedFrame = ulong.MaxValue;
        _isAvailable = false;
    }

    private void ReleaseCaptureObjects()
    {
        if (_material is not null)
            _material.SettingUniforms -= BindSkyboxUniforms;
        _frameBuffer?.Destroy();
        _material?.Destroy();
        _frameBuffer = null;
        _material = null;
    }

    /// <summary>Creates the persistent pipeline target sampled by DDGI hit shading.</summary>
    public static XRTexture2D CreateRadianceTexture()
        => new(Resolution, Resolution, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, false)
        {
            Name = TextureName,
            SamplerName = TextureName,
            SizedInternalFormat = ESizedInternalFormat.Rgba16f,
            Resizable = false,
            AutoGenerateMipmaps = false,
            SmallestAllowedMipmapLevel = 0,
            MinFilter = ETexMinFilter.Linear,
            MagFilter = ETexMagFilter.Linear,
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.ClampToEdge,
        };
}
