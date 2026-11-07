namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Holds the preview controls for one texture: layer, mip, channel, and the RGB split option.
/// </summary>
public sealed class RenderTexturePreviewState
{
    public int Layer;
    public int Mip;
    public ETexturePreviewChannel Channel = ETexturePreviewChannel.RGBA;
    public bool ShowSeparateRgbChannels;
}
