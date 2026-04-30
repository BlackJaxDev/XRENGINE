using System;
using System.Numerics;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Reads a single pixel from a named texture mip/layer into a pipeline vector variable.
/// Coordinates are mip-space texel coordinates.
/// </summary>
[RenderPipelineScriptCommand]
public class VPRC_ReadbackPixel : ViewportRenderCommand
{
    public string? SourceTextureName { get; set; }
    public string? ResultVariableName { get; set; }
    public int SourceMipLevel { get; set; }
    public int SourceLayerIndex { get; set; }
    public int PixelX { get; set; }
    public int PixelY { get; set; }

    protected override void Execute()
    {
        if (SourceTextureName is null || ResultVariableName is null)
            return;

        XRRenderPipelineInstance instance = ActivePipelineInstance;
        if (!instance.TryGetTexture(SourceTextureName, out XRTexture? texture) || texture is null)
            return;

        if (!VPRCReadbackHelpers.TryReadTextureMip(texture, SourceMipLevel, SourceLayerIndex, out float[] rgbaFloats, out int width, out int height, out string failure))
            throw new InvalidOperationException($"Texture '{SourceTextureName}' pixel readback failed: {failure}");

        if (!VPRCReadbackHelpers.TryReadPixel(rgbaFloats, width, height, PixelX, PixelY, out Vector4 rgba))
            throw new InvalidOperationException($"Texture '{SourceTextureName}' pixel ({PixelX}, {PixelY}) is outside mip {SourceMipLevel} bounds {width}x{height}.");

        instance.Variables.Set(ResultVariableName, rgba);
    }
}
