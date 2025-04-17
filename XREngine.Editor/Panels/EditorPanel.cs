using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;

namespace XREngine.Editor;

[RequiresTransform(typeof(UIBoundableTransform))]
public partial class EditorPanel : XRComponent
{
    public UIBoundableTransform BoundableTransform => SceneNode.GetTransformAs<UIBoundableTransform>()!;

    public EditorPanel() { }

    /// <summary>
    /// The window that this panel is displayed in.
    /// </summary>
    public XRWindow? Window { get; set; }

    private static XRMaterial? _backgroundMaterial;
    public static XRMaterial BackgroundMaterial
    {
        get => _backgroundMaterial ??= MakeBackgroundMaterial();
        set => _backgroundMaterial = value;
    }

    public static XRMaterial MakeBackgroundMaterial()
    {
        var bgShader = ShaderHelper.LoadEngineShader("UI\\GrabpassGaussian.frag");
        ShaderVar[] parameters =
        [
            new ShaderVector4(new ColorF4(66/255.0f, 79/255.0f, 78/255.0f, 1.0f), "MatColor"),
            new ShaderFloat(10.0f, "BlurStrength"),
            new ShaderInt(15, "SampleCount"),
        ];
        XRTexture2D grabTex = XRTexture2D.CreateGrabPassTextureResized(1.0f, EReadBufferMode.Front, true, false, false, false);
        var bgMat = new XRMaterial(parameters, [grabTex], bgShader)
        {
            RenderPass = (int)EDefaultRenderPass.OpaqueForward,
            RenderOptions = RenderParameters
        };
        //bgMat.EnableTransparency();
        return bgMat;
    }
    private static RenderingParameters RenderParameters { get; } = new()
    {
        CullMode = ECullMode.None,
        DepthTest = new()
        {
            Enabled = ERenderParamUsage.Disabled,
            Function = EComparison.Always
        },
        RequiredEngineUniforms = EUniformRequirements.ViewportDimensions,
        BlendModeAllDrawBuffers = BlendMode.EnabledTransparent(),
    };
}