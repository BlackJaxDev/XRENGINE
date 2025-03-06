using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

public class UserInterfaceRenderPipeline : RenderPipeline
{
    public const string SceneShaderPath = "Scene3D";

    //TODO: Some UI components need to rendered after their parent specifically for render clipping. breadth-first
    private readonly NearToFarRenderCommandSorter _nearToFarSorter = new();
    private readonly FarToNearRenderCommandSorter _farToNearSorter = new();

    protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
        => new()
        {
            { (int)EDefaultRenderPass.PreRender, null },
            { (int)EDefaultRenderPass.Background, null },
            { (int)EDefaultRenderPass.OpaqueForward, _nearToFarSorter },
            { (int)EDefaultRenderPass.TransparentForward, _farToNearSorter },
            { (int)EDefaultRenderPass.OnTopForward, null },
            { (int)EDefaultRenderPass.PostRender, null }
        };

    protected override Lazy<XRMaterial> InvalidMaterialFactory => new(MakeInvalidMaterial, LazyThreadSafetyMode.PublicationOnly);

    private XRMaterial MakeInvalidMaterial()
        => XRMaterial.CreateUnlitColorMaterialForward();

    //FBOs
    public const string ForwardPassFBOName = "ForwardPassFBO";
    public const string PostProcessFBOName = "PostProcessFBO";

    //Textures
    public const string DepthViewTextureName = "DepthView";
    public const string StencilViewTextureName = "StencilView";
    public const string DepthStencilTextureName = "DepthStencil";

    protected override ViewportRenderCommandContainer GenerateCommandChain()
    {
        ViewportRenderCommandContainer c = [];
        var ifElse = c.Add<VPRC_IfElse>();
        ifElse.ConditionEvaluator = () => State.WindowViewport is not null;
        ifElse.TrueCommands = CreateViewportTargetCommands();
        ifElse.FalseCommands = CreateFBOTargetCommands();
        return c;
    }

    public static ViewportRenderCommandContainer CreateFBOTargetCommands()
    {
        ViewportRenderCommandContainer c = [];

        c.Add<VPRC_SetClears>().Set(ColorF4.Red, 1.0f, 0);
        c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.PreRender;

        using (c.AddUsing<VPRC_PushOutputFBORenderArea>())
        {
            using (c.AddUsing<VPRC_BindOutputFBO>())
            {
                //c.Add<VPRC_StencilMask>().Set(~0u);
                //c.Add<VPRC_ClearByBoundFBO>();

                c.Add<VPRC_DepthFunc>().Comp = EComparison.Less;
                c.Add<VPRC_DepthWrite>().Allow = true;

                c.Add<VPRC_DepthTest>().Enable = false;
                c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.Background;
                c.Add<VPRC_DepthWrite>().Allow = true;
                c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.OpaqueForward;
                c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.TransparentForward;
                c.Add<VPRC_DepthFunc>().Comp = EComparison.Always;
                c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.OnTopForward;
            }
        }
        c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.PostRender;
        return c;
    }

    private ViewportRenderCommandContainer CreateViewportTargetCommands()
    {
        ViewportRenderCommandContainer c = [];

        CacheTextures(c);

        //Create FBOs only after all their texture dependencies have been cached.

        c.Add<VPRC_SetClears>().Set(ColorF4.Red, 1.0f, 0);
        c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.PreRender;
        
        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = false))
        {
            using (c.AddUsing<VPRC_BindOutputFBO>())
            {
                //c.Add<VPRC_StencilMask>().Set(~0u);
                //c.Add<VPRC_ClearByBoundFBO>();

                c.Add<VPRC_DepthFunc>().Comp = EComparison.Less;
                c.Add<VPRC_DepthWrite>().Allow = true;

                c.Add<VPRC_DepthTest>().Enable = false;
                c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.Background;
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.OpaqueForward;
                c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.TransparentForward;
                c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.OnTopForward;
            }
        }
        c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.PostRender;
        return c;
    }

    XRTexture CreateDepthStencilTexture()
    {
        var dsTex = XRTexture2D.CreateFrameBufferTexture(InternalWidth, InternalHeight,
            EPixelInternalFormat.Depth24Stencil8,
            EPixelFormat.DepthStencil,
            EPixelType.UnsignedInt248,
            EFrameBufferAttachment.DepthStencilAttachment);
        dsTex.MinFilter = ETexMinFilter.Nearest;
        dsTex.MagFilter = ETexMagFilter.Nearest;
        dsTex.Resizable = false;
        dsTex.Name = DepthStencilTextureName;
        dsTex.SizedInternalFormat = ESizedInternalFormat.Depth24Stencil8;
        return dsTex;
    }

    XRTexture CreateDepthViewTexture()
        => new XRTexture2DView(
            GetTexture<XRTexture2D>(DepthStencilTextureName)!,
            0, 1,
            EPixelInternalFormat.Depth24Stencil8,
            false, false)
        {
            DepthStencilViewFormat = EDepthStencilFmt.Depth,
            Name = DepthViewTextureName,
        };

    XRTexture CreateStencilViewTexture()
        => new XRTexture2DView(
            GetTexture<XRTexture2D>(DepthStencilTextureName)!,
            0, 1,
            EPixelInternalFormat.Depth24Stencil8,
            false, false)
        {
            DepthStencilViewFormat = EDepthStencilFmt.Stencil,
            Name = StencilViewTextureName,
        };

    private void CacheTextures(ViewportRenderCommandContainer c)
    {
        //Depth + Stencil GBuffer texture
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            DepthStencilTextureName,
            CreateDepthStencilTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        //Depth view texture
        //This is a view of the depth/stencil texture that only shows the depth values.
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            DepthViewTextureName,
            CreateDepthViewTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        //Stencil view texture
        //This is a view of the depth/stencil texture that only shows the stencil values.
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            StencilViewTextureName,
            CreateStencilViewTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);
    }
}
