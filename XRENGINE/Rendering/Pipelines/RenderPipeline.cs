using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using static XREngine.Engine.Rendering.State;
using static XREngine.Rendering.XRRenderPipelineInstance;

namespace XREngine.Rendering;

public abstract class RenderPipeline : XRBase
{
    public List<XRRenderPipelineInstance> Instances { get; } = [];

    protected abstract Lazy<XRMaterial> InvalidMaterialFactory { get; }
    public XRMaterial InvalidMaterial 
        => InvalidMaterialFactory.Value;

    private bool _isShadowPass;
    public bool IsShadowPass
    {
        get => _isShadowPass;
        set => SetField(ref _isShadowPass, value);
    }

    public ViewportRenderCommandContainer CommandChain { get; protected set; }
    public Dictionary<int, IComparer<RenderCommand>?> PassIndicesAndSorters { get; protected set; }

    protected RenderPipeline(bool deferCommandChainGeneration = false)
    {
        if (!deferCommandChainGeneration)
            CommandChain = GenerateCommandChain();
        PassIndicesAndSorters = GetPassIndicesAndSorters();
    }

    protected abstract ViewportRenderCommandContainer GenerateCommandChain();
    protected abstract Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters();

    public static RenderingState State 
        => CurrentRenderingPipeline!.RenderState;

    public static T? GetTexture<T>(string name) where T : XRTexture
        => CurrentRenderingPipeline!.GetTexture<T>(name);

    public static bool TryGetTexture(string name, out XRTexture? texture)
        => CurrentRenderingPipeline!.TryGetTexture(name, out texture);

    public static void SetTexture(XRTexture texture)
        => CurrentRenderingPipeline!.SetTexture(texture);

    public static T? GetFBO<T>(string name) where T : XRFrameBuffer
        => CurrentRenderingPipeline!.GetFBO<T>(name);

    public static bool TryGetFBO(string name, out XRFrameBuffer? fbo)
        => CurrentRenderingPipeline!.TryGetFBO(name, out fbo);

    public static void SetFBO(XRFrameBuffer fbo)
        => CurrentRenderingPipeline!.SetFBO(fbo);

    protected static uint InternalWidth
        => (uint)State.WindowViewport!.InternalWidth;
    protected static uint InternalHeight
        => (uint)State.WindowViewport!.InternalHeight;
    protected static uint FullWidth
        => (uint)State.WindowViewport!.Width;
    protected static uint FullHeight
        => (uint)State.WindowViewport!.Height;

    protected static bool NeedsRecreateTextureInternalSize(XRTexture t)
    {
        uint w = InternalWidth;
        uint h = InternalHeight;
        switch (t)
        {
            case XRTexture2D t2d:
                return t2d.Width != w || t2d.Height != h;
            case XRTexture2DArray t2da:
                return t2da.Width != w || t2da.Height != h;
            case XRTexture2DView:
            case XRTexture2DArrayView:
                return false;
            default:
                Debug.LogWarning($"Texture {t.Name} is not a 2D or 2DArray texture. Cannot resize.");
                return false;
        }
    }

    protected static bool NeedsRecreateTextureFullSize(XRTexture t)
    {
        uint w = FullWidth;
        uint h = FullHeight;
        switch (t)
        {
            case XRTexture2D t2d:
                return t2d.Width != w || t2d.Height != h;
            case XRTexture2DArray t2da:
                return t2da.Width != w || t2da.Height != h;
            case XRTexture2DView:
            case XRTexture2DArrayView:
                return false;
            default:
                Debug.LogWarning($"Texture {t.Name} is not a 2D or 2DArray texture. Cannot resize.");
                return false;
        }
    }

    protected static void ResizeTextureInternalSize(XRTexture t)
    {
        switch (t)
        {
            case XRTexture2D t2d:
                if (t2d.Resizable)
                    t2d.Resize(InternalWidth, InternalHeight);
                else
                    t2d.Destroy();
                break;
            case XRTexture2DArray t2da:
                if (t2da.Resizable)
                    t2da.Resize(InternalWidth, InternalHeight);
                else
                    t2da.Destroy();
                break;
            case XRTexture2DView:
            case XRTexture2DArrayView:
                break;
            default:
                Debug.LogWarning($"Texture {t.Name} is not a 2D or 2DArray texture. Cannot resize.");
                break;
        }
    }
    protected static void ResizeTextureFullSize(XRTexture t)
    {
        switch (t)
        {
            case XRTexture2D t2d:
                if (t2d.Resizable)
                    t2d.Resize(FullWidth, FullHeight);
                else
                    t2d.Destroy();
                break;
            case XRTexture2DArray t2da:
                if (t2da.Resizable)
                    t2da.Resize(FullWidth, FullHeight);
                else
                    t2da.Destroy();
                break;
            case XRTexture2DView:
            case XRTexture2DArrayView:
                break;
            default:
                Debug.LogWarning($"Texture {t.Name} is not a 2D or 2DArray texture. Cannot resize.");
                break;
        }
    }

    protected static (uint x, uint y) GetDesiredFBOSizeInternal()
        => (InternalWidth, InternalHeight);
    protected static (uint x, uint y) GetDesiredFBOSizeFull()
        => ((uint)State.WindowViewport!.Width, (uint)State.WindowViewport!.Height);

    /// <summary>
    /// Creates a texture used by PBR shading to light an opaque surface.
    /// Input is an incoming light direction and an outgoing direction (calculated using the normal)
    /// Output from this texture is ratio of refleced radiance in the outgoing direction to irradiance from the incoming direction.
    /// https://en.wikipedia.org/wiki/Bidirectional_reflectance_distribution_function
    /// </summary>
    /// <param name="width"></param>
    /// <param name="height"></param>
    /// <returns></returns>
    public static XRTexture2D PrecomputeBRDF(uint width = 2048, uint height = 2048)
    {
        XRTexture2D brdf = new(
            width, height,
            EPixelInternalFormat.RG16f,
            EPixelFormat.Rg,
            EPixelType.HalfFloat,
            false)
        {
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.ClampToEdge,
            MinFilter = ETexMinFilter.Linear,
            MagFilter = ETexMagFilter.Linear,
            SamplerName = "BRDF",
            Name = "BRDF",
            Resizable = true,
            SizedInternalFormat = ESizedInternalFormat.Rg16f
        };

        XRShader shader = XRShader.EngineShader(Path.Combine("Scene3D", "BRDF.fs"), EShaderType.Fragment);
        XRMaterial mat = new(shader)
        {
            RenderOptions = new()
            {
                DepthTest = new()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
            }
        };

        XRQuadFrameBuffer fbo = new(mat);
        fbo.SetRenderTargets((brdf, EFrameBufferAttachment.ColorAttachment0, 0, -1));
        BoundingRectangle region = new(IVector2.Zero, new IVector2((int)width, (int)height));

        //Now render the texture to the FBO using the quad
        using (fbo.BindForWritingState())
        {
            using (State.PushRenderArea(region))
            {
                //ClearColor(new ColorF4(0.0f, 0.0f, 0.0f, 1.0f));
                //Clear(true, true, false);
                fbo.Render(null, true);
            }
        }
        return brdf;
    }
}
