using XREngine;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Shaders.Compilation;

namespace ModularPipelineParity;

/// <summary>Renders an authored scene into four-sample HDR color and depth, resolves color, and presents it.</summary>
public sealed class ModularMsaaRenderPipeline : CustomRenderPipeline
{
    public const string SceneBindingKey = "custom::msaa-scene";
    public const string PresentBindingKey = "custom::msaa-present";

    private const string MsaaColorName = "ModularMsaaColor";
    private const string MsaaDepthName = "ModularMsaaDepth";
    private const string MsaaFboName = "ModularMsaaFBO";
    private const string ResolvedColorName = "ModularResolvedColor";
    private const string ResolvedFboName = "ModularResolvedFBO";
    private const string PresentQuadName = "ModularMsaaPresentQuad";

    private readonly NearToFarRenderCommandSorter _nearToFar = new();
    private readonly FarToNearRenderCommandSorter _farToNear = new();
    private EMeshSubmissionStrategy _meshSubmissionStrategy = EMeshSubmissionStrategy.CpuDirect;

    public ModularMsaaRenderPipeline()
    {
        // The RenderPipeline base constructor runs before these sorters are initialized.
        PassIndicesAndSorters = GetPassIndicesAndSorters();
        InitializeCommandChain();
    }

    /// <summary>Lets the saved scene publish its mesh materials when this camera receives its cooked catalog.</summary>
    public event Action<ModularMsaaRenderPipeline>? WebProgramsBound;

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        if (propName == nameof(WebPipelineArtifacts) && WebPipelineArtifacts is not null)
            WebProgramsBound?.Invoke(this);
    }

    /// <summary>The saved strategy is authoritative for every scene pass in this source.</summary>
    public EMeshSubmissionStrategy MeshSubmissionStrategy
    {
        get => _meshSubmissionStrategy;
        set
        {
            if (!SetField(ref _meshSubmissionStrategy, value))
                return;
            if (Instances.Count == 0)
                InitializeCommandChain();
            else
                RebuildCommandChain();
        }
    }

    public override void DescribeRequirements(RenderPipelineRequirements requirements)
    {
        base.DescribeRequirements(requirements);
        requirements.SupportedAntiAliasingModes.Clear();
        requirements.SupportedAntiAliasingModes.Add(EAntiAliasingMode.Msaa);
        requirements.RequireOperation("color-resolve");
    }

    protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
        => new()
        {
            [(int)EDefaultRenderPass.OpaqueForward] = _nearToFar,
            [(int)EDefaultRenderPass.TransparentForward] = _farToNear,
        };

    protected override void DescribeResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderPipelineResourceProfile profile = builder.Profile;
        if (profile.AntiAliasingMode != EAntiAliasingMode.Msaa || profile.MsaaSampleCount != 4 ||
            profile.Stereo || profile.ViewCount != 1)
            throw new NotSupportedException("WebGPU.ModularMsaa.ProfileUnsupported: the authored output requires mono x4 MSAA.");

        RenderResourceSizePolicy size = RenderResourceSizePolicy.Internal();
        builder.Texture(MsaaColorName).Size(size).Samples(4)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.SampledTexture)
            .Format(EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
            .SizedFormat(ESizedInternalFormat.Rgba16f)
            .Factory(() => CreateColorTexture(MsaaColorName, profile.InternalWidth, profile.InternalHeight, 4))
            .Add();
        builder.Texture(MsaaDepthName).Size(size).Samples(4)
            .Usage(RenderPipelineResourceUsage.DepthStencilAttachment)
            .Format(EPixelInternalFormat.DepthComponent32, EPixelFormat.DepthComponent, EPixelType.Float)
            .SizedFormat(ESizedInternalFormat.DepthComponent32f)
            .Factory(() => CreateDepthTexture(profile.InternalWidth, profile.InternalHeight))
            .Add();
        builder.FrameBuffer(MsaaFboName).Size(size)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .Color(0, MsaaColorName).Depth(MsaaDepthName)
            .Factory(() => CreateFrameBuffer(MsaaFboName, MsaaColorName, MsaaDepthName))
            .Add();
        builder.Texture(ResolvedColorName).Size(size).Samples(1)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.SampledTexture)
            .Format(EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
            .SizedFormat(ESizedInternalFormat.Rgba16f)
            .Factory(() => CreateColorTexture(ResolvedColorName, profile.InternalWidth, profile.InternalHeight, 1))
            .Add();
        builder.FrameBuffer(ResolvedFboName).Size(size)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, ResolvedColorName)
            .Factory(() => CreateFrameBuffer(ResolvedFboName, ResolvedColorName))
            .Add();
        builder.QuadMaterial(PresentQuadName).DependsOn(ResolvedColorName)
            .Factory(CreatePresentQuad).Add();
    }

    protected override ViewportRenderCommandContainer GenerateCommandChain()
    {
        ViewportRenderCommandContainer commands = new(this);
        commands.Add<VPRC_SetClears>().Set(new ColorF4(0.1f, 0.2f, 0.3f, 1.0f), 1.0f, null);
        commands.Add<VPRC_ColorMask>().Set(true, true, true, true);
        using (commands.AddUsing<VPRC_PushViewportRenderArea>(area => area.UseInternalResolution = true))
        using (commands.AddUsing<VPRC_BindFBOByName>(bind =>
            bind.SetOptions(MsaaFboName, clearColor: true, clearDepth: true, clearStencil: false)))
        {
            commands.Add<VPRC_DepthTest>().Enable = true;
            commands.Add<VPRC_DepthWrite>().Allow = true;
            AddScenePass(commands, EDefaultRenderPass.OpaqueForward);
            commands.Add<VPRC_DepthWrite>().Allow = false;
            AddScenePass(commands, EDefaultRenderPass.TransparentForward);
        }
        commands.Add<VPRC_BlitFrameBuffer>().SetOptions(MsaaFboName, ResolvedFboName,
            EReadBufferMode.ColorAttachment0, blitColor: true, blitDepth: false,
            blitStencil: false, linearFilter: false);
        using (commands.AddUsing<VPRC_PushOutputFBORenderArea>())
        using (commands.AddUsing<VPRC_BindOutputFBO>(bind =>
            bind.SetOptions(clearColor: false, clearDepth: false, clearStencil: false)))
        {
            commands.Add<VPRC_DepthTest>().Enable = false;
            VPRC_RenderQuadToFBO present = commands.Add<VPRC_RenderQuadToFBO>();
            present.RequiredForOutput = true;
            present.SetTargets(PresentQuadName)
                .ConfigureRenderGraphResources(static resources => resources.SampleTexture(ResolvedColorName));
        }
        return commands;
    }

    private void AddScenePass(ViewportRenderCommandContainer commands, EDefaultRenderPass pass)
    {
        VPRC_RenderMeshesPass meshes = commands.Add<VPRC_RenderMeshesPass>();
        meshes.SetOptions((int)pass, MeshSubmissionStrategy);
        meshes.PreserveMeshSubmissionStrategy = true;
    }

    private static XRTexture2D CreateColorTexture(string name, uint width, uint height, uint samples)
    {
        XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(width, height,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat,
            EFrameBufferAttachment.ColorAttachment0);
        texture.Name = name;
        texture.SamplerName = name;
        texture.MultiSampleCount = samples;
        texture.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
        texture.Resizable = false;
        texture.AutoGenerateMipmaps = false;
        texture.MinFilter = ETexMinFilter.Linear;
        texture.MagFilter = ETexMagFilter.Linear;
        return texture;
    }

    private static XRTexture2D CreateDepthTexture(uint width, uint height)
    {
        XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(width, height,
            EPixelInternalFormat.DepthComponent32, EPixelFormat.DepthComponent, EPixelType.Float,
            EFrameBufferAttachment.DepthAttachment);
        texture.Name = MsaaDepthName;
        texture.MultiSampleCount = 4;
        texture.SizedInternalFormat = ESizedInternalFormat.DepthComponent32f;
        texture.Resizable = false;
        texture.AutoGenerateMipmaps = false;
        texture.MinFilter = ETexMinFilter.Nearest;
        texture.MagFilter = ETexMagFilter.Nearest;
        return texture;
    }

    private XRFrameBuffer CreateFrameBuffer(string name, string colorName, string? depthName = null)
    {
        XRTexture2D color = GetTexture<XRTexture2D>(colorName)
            ?? throw new InvalidOperationException($"The '{colorName}' attachment was not materialized.");
        XRFrameBuffer framebuffer = depthName is null
            ? new XRFrameBuffer((color, EFrameBufferAttachment.ColorAttachment0, 0, -1))
            : new XRFrameBuffer((color, EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (GetTexture<XRTexture2D>(depthName)
                    ?? throw new InvalidOperationException($"The '{depthName}' attachment was not materialized."),
                    EFrameBufferAttachment.DepthAttachment, 0, -1));
        framebuffer.Name = name;
        return framebuffer;
    }

    private XRFrameBuffer CreatePresentQuad()
    {
        XRMaterial material = new(Array.Empty<XRTexture?>(),
            WebPipelineRasterProgram.CreateShaders(this, PresentBindingKey))
        {
            Name = PresentQuadName,
            RenderOptions = new RenderingParameters
            {
                CullMode = ECullMode.None,
                DepthTest = { Enabled = ERenderParamUsage.Disabled, UpdateDepth = false,
                    Function = EComparison.Always },
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
                AlphaToCoverage = ERenderParamUsage.Disabled,
            },
        };
        try
        {
            XRQuadFrameBuffer quad = new(material, deriveRenderTargetsFromMaterial: false,
                prepareForInitialRendering: false) { Name = PresentQuadName };
            WebPipelineRasterProgram.OwnMaterial(quad);
            quad.SettingUniforms += BindPresentTexture;
            return quad;
        }
        catch
        {
            foreach (XRShader shader in material.Shaders)
                shader.Destroy(true);
            material.Destroy(true);
            throw;
        }
    }

    private void BindPresentTexture(XRRenderProgram program)
        => program.Sampler("SourceTexture", GetTexture<XRTexture2D>(ResolvedColorName)
            ?? throw new InvalidOperationException("The resolved color texture is unavailable."), 0);
}
