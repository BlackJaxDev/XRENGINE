using XREngine.Data.Colors;

namespace XREngine.Rendering.Pipelines.Commands;

[RenderPipelineScriptCommand]
public class VPRC_ClearTextureByName : ViewportRenderCommand
{
    public string? TextureName { get; set; }
    public ColorF4 ClearColor { get; set; } = ColorF4.Transparent;

    public VPRC_ClearTextureByName SetOptions(string textureName, ColorF4 clearColor)
    {
        TextureName = textureName;
        ClearColor = clearColor;
        return this;
    }

    protected override void Execute()
    {
        if (TextureName is null)
            return;

        ActivePipelineInstance.GetTexture<XRTexture>(TextureName)?.Clear(ClearColor);
    }
}
