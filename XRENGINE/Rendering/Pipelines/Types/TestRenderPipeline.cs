using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using static XREngine.Engine.Rendering.State;

namespace XREngine.Rendering;
public class TestRenderPipeline : RenderPipeline
{
    private readonly FarToNearRenderCommandSorter _farToNearSorter = new();
    protected override Lazy<XRMaterial> InvalidMaterialFactory => new(MakeInvalidMaterial, LazyThreadSafetyMode.PublicationOnly);

    private XRMaterial MakeInvalidMaterial()
    {
        Debug.Out("Generating invalid material");
        return XRMaterial.CreateUnlitColorMaterialForward();
    }

    protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
        => new() { { (int)EDefaultRenderPass.OpaqueForward, _farToNearSorter }, };

    protected override ViewportRenderCommandContainer GenerateCommandChain()
    {
        ViewportRenderCommandContainer c = new(this);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            DepthStencilTextureName,
            CreateDepthStencilTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            HDRSceneTextureName,
            CreateHDRSceneTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            InternalResFBOName,
            CreateInternalResFBO,
            GetDesiredFBOSizeInternal);

        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = true))
        {
            using (c.AddUsing<VPRC_BindFBOByName>(x => x.FrameBufferName = InternalResFBOName))
            {
                c.Add<VPRC_Manual>().ManualAction = () =>
                {
                    StencilMask(~0u);
                    ClearStencil(0);
                    ClearColor(new ColorF4(0.0f, 0.0f, 0.0f, 1.0f));
                    Clear(true, true, true);
                    DepthFunc(EComparison.Less);
                    ClearDepth(1.0f);
                    AllowDepthWrite(true);
                };
                c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.OpaqueForward;
            }
        }
        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = false))
        {
            using (c.AddUsing<VPRC_BindOutputFBO>())
            {
                c.Add<VPRC_Manual>().ManualAction = () =>
                {
                    StencilMask(~0u);
                    ClearStencil(0);
                    ClearColor(new ColorF4(0.0f, 0.0f, 0.0f, 1.0f));
                    Clear(true, true, true);
                    DepthFunc(EComparison.Less);
                    ClearDepth(1.0f);
                    AllowDepthWrite(true);
                };
                c.Add<VPRC_RenderQuadFBO>().FrameBufferName = InternalResFBOName;
            }
        }
        return c;
    }

    const string UserInterfaceFBOName = "UserInterfaceFBO";

    private const string InternalResFBOName = "InternalResFBO";
    private const string HDRSceneTextureName = "HDRSceneTex";
    private const string DepthStencilTextureName = "DepthStencil";

    public const string HDRShaderSource = @"#version 450

out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D HDRSceneTex;

float rand(vec2 coord)
{
    return fract(sin(dot(coord, vec2(12.9898, 78.233))) * 43758.5453);
}
void main()
{
	vec2 uv = FragPos.xy;
	if (uv.x > 1.0 || uv.y > 1.0)
		discard;

    //Remap from [-1, 1] to [0, 1]
    uv = uv * 0.5 + 0.5;

    //Sample the raw scene color
	vec3 hdrSceneColor = texture(HDRSceneTex, uv).rgb;

	vec3 ldrSceneColor = vec3(1.0) - exp(-hdrSceneColor);
    ldrSceneColor += mix(-0.5 / 255.0, 0.5 / 255.0, rand(uv));
	OutColor = vec4(ldrSceneColor, 1.0);
}";
    private XRFrameBuffer CreateInternalResFBO()
    {
        var hdrTex = GetTexture<XRTexture2D>(HDRSceneTextureName);
        var depthStencilTex = GetTexture<XRTexture2D>(DepthStencilTextureName);

        XRTexture2D[] textures = [hdrTex!];
        XRShader shader = new(EShaderType.Fragment, HDRShaderSource);
        XRMaterial mat = new(textures, shader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Unchanged,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
            }
        };
        var fbo = new XRQuadFrameBuffer(mat);
        fbo.SetRenderTargets(
            (hdrTex!, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (depthStencilTex!, EFrameBufferAttachment.DepthStencilAttachment, 0, -1));
        fbo.SettingUniforms += InternalResFBOSetUniforms;
        return fbo;
    }

    private static XRTexture CreateHDRSceneTexture()
    {
        bool useHdr = Engine.Rendering.Settings.OutputHDR;
        var tex = XRTexture2D.CreateFrameBufferTexture(InternalWidth, InternalHeight,
            useHdr ? EPixelInternalFormat.Rgba16f : EPixelInternalFormat.Rgba8,
            useHdr ? EPixelFormat.Rgba : EPixelFormat.Bgra,
            useHdr ? EPixelType.HalfFloat : EPixelType.UnsignedByte,
            EFrameBufferAttachment.ColorAttachment0);
        tex.Name = HDRSceneTextureName;
        tex.SamplerName = HDRSceneTextureName;
        tex.Resizable = true;
        tex.SizedInternalFormat = useHdr ? ESizedInternalFormat.Rgba16f : ESizedInternalFormat.Rgba8;
        return tex;
    }

    private void InternalResFBOSetUniforms(XRRenderProgram program)
    {

    }

    private XRTexture CreateDepthStencilTexture()
    {
        var tex = XRTexture2D.CreateFrameBufferTexture(InternalWidth, InternalHeight,
            EPixelInternalFormat.Depth24Stencil8,
            EPixelFormat.DepthStencil,
            EPixelType.UnsignedInt248,
            EFrameBufferAttachment.DepthStencilAttachment);
        tex.Name = DepthStencilTextureName;
        tex.Resizable = true;
        tex.SizedInternalFormat = ESizedInternalFormat.Depth24Stencil8;
        return tex;
    }
}
