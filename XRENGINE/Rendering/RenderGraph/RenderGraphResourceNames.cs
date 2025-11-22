namespace XREngine.Rendering.RenderGraph;

public static class RenderGraphResourceNames
{
    public const string OutputRenderTarget = "__OUTPUT_FBO__";
    public static string MakeFboColor(string fboName) => $"fbo::{fboName}::color";
    public static string MakeFboDepth(string fboName) => $"fbo::{fboName}::depth";
    public static string MakeTexture(string textureName) => $"tex::{textureName}";
}
