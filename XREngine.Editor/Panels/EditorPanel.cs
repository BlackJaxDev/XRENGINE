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

    private static XRMaterial MakeBackgroundMaterial()
    {
        var bgShader = ShaderHelper.UnlitColorFragForward()!; //ShaderHelper.LoadEngineShader("UI\\GrabpassGaussian.frag");
        ShaderVar[] parameters =
        [
            new ShaderVector4(new ColorF4(1.0f, 0.0f, 0.0f, 1.0f), "MatColor"),
            //new ShaderFloat(10.0f, "BlurStrength"),
            //new ShaderInt(30, "SampleCount"),
        ];
        //XRTexture2D grabTex = XRTexture2D.CreateGrabPassTextureResized(1.0f, EReadBufferMode.Front, true, false, false, false);
        var bgMat = new XRMaterial(parameters, [/*grabTex*/], bgShader);
        bgMat.RenderOptions.CullMode = ECullMode.None;
        //bgMat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
        bgMat.RenderPass = (int)EDefaultRenderPass.TransparentForward;
        //bgMat.EnableTransparency();
        return bgMat;
    }
}