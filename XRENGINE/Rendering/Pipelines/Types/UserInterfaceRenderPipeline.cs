using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.UI;

namespace XREngine.Rendering;

public class UserInterfaceRenderPipeline : RenderPipeline
{
    public const string SceneShaderPath = "Scene3D";

    /// <summary>
    /// When set, UI render passes dispatch batched instanced draws for material quads and text quads
    /// instead of individual per-component draw calls. Set by the owning <see cref="UICanvasComponent"/>.
    /// </summary>
    public UIBatchCollector? BatchCollector { get; set; }

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
        ViewportRenderCommandContainer c = new(this);
        var ifElse = c.Add<VPRC_IfElse>();
        ifElse.ConditionEvaluator = () => State.WindowViewport is not null;
        ifElse.TrueCommands = CreateViewportTargetCommands();
        ifElse.FalseCommands = CreateFBOTargetCommands(this);
        return c;
    }

    public static ViewportRenderCommandContainer CreateFBOTargetCommands(RenderPipeline? pipeline = null)
    {
        ViewportRenderCommandContainer c = new(pipeline);

        // Clear the FBO to transparent black so only the UI content is visible
        // when the texture is composited onto the world-space quad.
        c.Add<VPRC_SetClears>().Set(new ColorF4(0f, 0f, 0f, 0f), 1.0f, 0);
        c.Add<VPRC_RenderUIBatched>().RenderPass = (int)EDefaultRenderPass.PreRender;

        using (c.AddUsing<VPRC_PushOutputFBORenderArea>())
        {
            using (c.AddUsing<VPRC_BindOutputFBO>(t => t.SetOptions(write: true, clearColor: true, clearDepth: false, clearStencil: false)))
            {
                c.Add<VPRC_DepthFunc>().Comp = EComparison.Less;
                c.Add<VPRC_DepthWrite>().Allow = true;

                c.Add<VPRC_DepthTest>().Enable = false;
                c.Add<VPRC_RenderUIBatched>().RenderPass = (int)EDefaultRenderPass.Background;
                c.Add<VPRC_DepthWrite>().Allow = true;
                c.Add<VPRC_RenderUIBatched>().RenderPass = (int)EDefaultRenderPass.OpaqueForward;
                c.Add<VPRC_RenderUIBatched>().RenderPass = (int)EDefaultRenderPass.TransparentForward;
                c.Add<VPRC_DepthFunc>().Comp = EComparison.Always;
                c.Add<VPRC_RenderUIBatched>().RenderPass = (int)EDefaultRenderPass.OnTopForward;
            }
        }
        c.Add<VPRC_RenderUIBatched>().RenderPass = (int)EDefaultRenderPass.PostRender;
        return c;
    }

    private ViewportRenderCommandContainer CreateViewportTargetCommands()
    {
        ViewportRenderCommandContainer c = new(this);

        CacheTextures(c);

        //Create FBOs only after all their texture dependencies have been cached.

        c.Add<VPRC_SetClears>().Set(null, 1.0f, 0);
        c.Add<VPRC_RenderUIBatched>().RenderPass = (int)EDefaultRenderPass.PreRender;
        
        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = false))
        {
            using (c.AddUsing<VPRC_BindOutputFBO>(t => t.SetOptions(write: true, clearColor: false, clearDepth: false, clearStencil: false)))
            {
                //c.Add<VPRC_StencilMask>().Set(~0u);
                //c.Add<VPRC_ClearByBoundFBO>();

                c.Add<VPRC_DepthFunc>().Comp = EComparison.Less;
                c.Add<VPRC_DepthWrite>().Allow = true;

                c.Add<VPRC_DepthTest>().Enable = false;
                c.Add<VPRC_RenderUIBatched>().RenderPass = (int)EDefaultRenderPass.Background;
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_RenderUIBatched>().RenderPass = (int)EDefaultRenderPass.OpaqueForward;
                c.Add<VPRC_RenderUIBatched>().RenderPass = (int)EDefaultRenderPass.TransparentForward;
                c.Add<VPRC_RenderUIBatched>().RenderPass = (int)EDefaultRenderPass.OnTopForward;
            }
        }
        c.Add<VPRC_RenderUIBatched>().RenderPass = (int)EDefaultRenderPass.PostRender;
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
            ESizedInternalFormat.Depth24Stencil8,
            false, false)
        {
            DepthStencilViewFormat = EDepthStencilFmt.Depth,
            Name = DepthViewTextureName,
        };

    XRTexture CreateStencilViewTexture()
        => new XRTexture2DView(
            GetTexture<XRTexture2D>(DepthStencilTextureName)!,
            0, 1,
            ESizedInternalFormat.Depth24Stencil8,
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
