using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources.Authoring;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// The built-in palette snippets. Each snippet adds the shared HDR target when the draft does not declare it, so it
/// works in a blank pipeline.
/// </summary>
public static class RenderPipelineSnippets
{
    /// <summary>The name of the HDR color texture that the snippets share.</summary>
    public const string HdrColorTexture = "HDRSceneTex";

    /// <summary>The name of the HDR depth texture that the snippets share.</summary>
    public const string HdrDepthTexture = "HDRSceneDepth";

    /// <summary>The name of the HDR framebuffer that the snippets share.</summary>
    public const string HdrFrameBuffer = "HDRSceneFBO";

    /// <summary>The engine shader that copies <see cref="HdrColorTexture"/> to the bound target.</summary>
    public const string CopyShader = "Scene3D/SceneCopy.fs";

    /// <summary>Gets the built-in snippets in palette order.</summary>
    public static IReadOnlyList<RenderPipelineSnippet> All { get; } =
    [
        new("Fullscreen pass", "A quad material that samples the HDR target and draws into a new output framebuffer.", BuildFullscreenPass),
        new("Bind target and clear", "Binds the HDR framebuffer, clears color and depth, and restores the previous target.", BuildBindAndClear),
        new("Scene pass", "Adds a scene pass to the pass list and draws its meshes into the HDR framebuffer.", BuildScenePass),
        new("Tonemap to output", "Tonemaps the HDR target into the output framebuffer.", BuildTonemapToOutput),
        new("Bloom chain", "Runs the bloom downsample and upsample chain on the HDR framebuffer.", BuildBloomChain),
        new("FXAA", "Applies FXAA to the HDR target into a new output framebuffer.", BuildFxaa),
        new("SMAA", "Applies SMAA to the HDR target into a new output framebuffer.", BuildSmaa),
    ];

    /// <summary>Adds the shared HDR color, depth, and framebuffer declarations when they are missing.</summary>
    public static void EnsureHdrTarget(RenderPipelineDraftState state)
    {
        EnsureTexture(state, HdrColorTexture, ESizedInternalFormat.Rgba16f);
        EnsureTexture(state, HdrDepthTexture, ESizedInternalFormat.Depth24Stencil8);
        EnsureFrameBuffer(state, HdrFrameBuffer, HdrColorTexture, HdrDepthTexture);
    }

    /// <summary>Adds a texture declaration when no declaration has the name.</summary>
    public static AuthoredRenderResource EnsureTexture(RenderPipelineDraftState state, string name, ESizedInternalFormat format)
    {
        if (RenderPipelineDraftResourceEdits.FindResource(state, name) is { } existing)
            return existing;

        AuthoredRenderResource texture = new()
        {
            Name = name,
            Kind = EAuthoredRenderResourceKind.Texture,
            Format = format,
            Size = AuthoredSizePolicy.Internal(),
        };
        state.Resources.Add(texture);
        return texture;
    }

    /// <summary>
    /// Adds a framebuffer declaration with one color attachment and an optional depth attachment when no declaration
    /// has the name.
    /// </summary>
    public static AuthoredRenderResource EnsureFrameBuffer(RenderPipelineDraftState state, string name, string colorTexture, string? depthTexture = null)
    {
        if (RenderPipelineDraftResourceEdits.FindResource(state, name) is { } existing)
            return existing;

        AuthoredRenderResource frameBuffer = new()
        {
            Name = name,
            Kind = EAuthoredRenderResourceKind.FrameBuffer,
        };
        frameBuffer.Attachments.Add(new AuthoredAttachment { ResourceName = colorTexture, Attachment = EFrameBufferAttachment.ColorAttachment0 });
        if (depthTexture is not null)
            frameBuffer.Attachments.Add(new AuthoredAttachment { ResourceName = depthTexture, Attachment = EFrameBufferAttachment.DepthStencilAttachment });
        state.Resources.Add(frameBuffer);
        return frameBuffer;
    }

    private static List<ViewportRenderCommand> BuildFullscreenPass(RenderPipelineDraftState state)
    {
        EnsureHdrTarget(state);
        string output = RenderPipelineDraftResourceEdits.MakeUniqueName(state, "FullscreenOutputTex");
        EnsureTexture(state, output, ESizedInternalFormat.Rgba16f);
        string outputFbo = RenderPipelineDraftResourceEdits.MakeUniqueName(state, "FullscreenOutputFBO");
        EnsureFrameBuffer(state, outputFbo, output);

        string quad = RenderPipelineDraftResourceEdits.MakeUniqueName(state, "FullscreenQuad");
        AuthoredRenderResource material = new()
        {
            Name = quad,
            Kind = EAuthoredRenderResourceKind.QuadMaterial,
            FragmentShader = CopyShader,
        };
        material.InputTextures.Add(HdrColorTexture);
        state.Resources.Add(material);

        return [new VPRC_RenderQuadToFBO { SourceQuadFBOName = quad, DestinationFBOName = outputFbo }];
    }

    private static List<ViewportRenderCommand> BuildBindAndClear(RenderPipelineDraftState state)
    {
        EnsureHdrTarget(state);
        VPRC_BindFBOByName bind = new()
        {
            FrameBufferName = HdrFrameBuffer,
            ClearColor = false,
            ClearDepth = false,
            ClearStencil = false,
        };
        return [bind, new VPRC_Clear { Color = true, Depth = true, Stencil = true }, bind.PopCommand];
    }

    private static List<ViewportRenderCommand> BuildScenePass(RenderPipelineDraftState state)
    {
        EnsureHdrTarget(state);
        int passIndex = RenderPipelineDraftPassEdits.AddScenePass(state, new NearToFarRenderCommandSorter());
        VPRC_BindFBOByName bind = new() { FrameBufferName = HdrFrameBuffer };
        return [bind, new VPRC_RenderMeshesPass { RenderPass = passIndex }, bind.PopCommand];
    }

    private static List<ViewportRenderCommand> BuildTonemapToOutput(RenderPipelineDraftState state)
    {
        EnsureHdrTarget(state);
        VPRC_BindOutputFBO bind = new() { ClearDepth = false, ClearStencil = false };
        return [bind, new VPRC_Tonemap { SourceTextureName = HdrColorTexture }, bind.PopCommand];
    }

    private static List<ViewportRenderCommand> BuildBloomChain(RenderPipelineDraftState state)
    {
        EnsureHdrTarget(state);
        return [new VPRC_BloomPass { InputFBOName = HdrFrameBuffer }];
    }

    private static List<ViewportRenderCommand> BuildFxaa(RenderPipelineDraftState state)
    {
        EnsureHdrTarget(state);
        string output = RenderPipelineDraftResourceEdits.MakeUniqueName(state, "FxaaOutputTex");
        EnsureTexture(state, output, ESizedInternalFormat.Rgba16f);
        string outputFbo = RenderPipelineDraftResourceEdits.MakeUniqueName(state, "FxaaOutputFBO");
        EnsureFrameBuffer(state, outputFbo, output);
        return [new VPRC_FXAA { SourceTextureName = HdrColorTexture, DestinationFBOName = outputFbo }];
    }

    private static List<ViewportRenderCommand> BuildSmaa(RenderPipelineDraftState state)
    {
        EnsureHdrTarget(state);
        string output = RenderPipelineDraftResourceEdits.MakeUniqueName(state, "SmaaOutputTexture");
        EnsureTexture(state, output, ESizedInternalFormat.Rgba16f);
        string outputFbo = RenderPipelineDraftResourceEdits.MakeUniqueName(state, "SmaaOutputFBO");
        EnsureFrameBuffer(state, outputFbo, output);
        return [new VPRC_SMAA { SourceTextureName = HdrColorTexture, OutputTextureName = output, OutputFBOName = outputFbo }];
    }
}
