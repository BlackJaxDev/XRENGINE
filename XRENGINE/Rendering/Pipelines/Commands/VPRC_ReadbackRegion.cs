using System;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Reads a rectangular region from a named texture mip/layer into a pipeline data buffer as
/// tightly packed RGBA float32 data.
/// </summary>
[RenderPipelineScriptCommand]
public class VPRC_ReadbackRegion : ViewportRenderCommand
{
    public string? SourceTextureName { get; set; }
    public string? DestinationBufferName { get; set; }
    public int SourceMipLevel { get; set; }
    public int SourceLayerIndex { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool UploadToGpuBuffer { get; set; } = true;
    public string? WidthVariableName { get; set; }
    public string? HeightVariableName { get; set; }

    protected override void Execute()
    {
        if (SourceTextureName is null || DestinationBufferName is null)
            return;

        XRRenderPipelineInstance instance = ActivePipelineInstance;
        if (!instance.TryGetTexture(SourceTextureName, out XRTexture? texture) || texture is null)
            return;

        XRDataBuffer? destinationBuffer = instance.GetBuffer(DestinationBufferName);
        if (destinationBuffer is null)
            return;

        if (!VPRCReadbackHelpers.TryReadTextureMip(texture, SourceMipLevel, SourceLayerIndex, out float[] rgbaFloats, out int width, out int height, out string failure))
            throw new InvalidOperationException($"Texture '{SourceTextureName}' region readback failed: {failure}");

        if (!VPRCReadbackHelpers.TryCropRegion(rgbaFloats, width, height, X, Y, Width, Height, out float[] cropped, out int croppedWidth, out int croppedHeight))
            throw new InvalidOperationException($"Texture '{SourceTextureName}' region ({X}, {Y}, {Width}, {Height}) does not intersect mip {SourceMipLevel} bounds {width}x{height}.");

        destinationBuffer.SetRawBytes(VPRCReadbackHelpers.ToBytes(cropped));
        if (UploadToGpuBuffer)
            destinationBuffer.PushData();

        if (!string.IsNullOrWhiteSpace(WidthVariableName))
            instance.Variables.Set(WidthVariableName, croppedWidth);
        if (!string.IsNullOrWhiteSpace(HeightVariableName))
            instance.Variables.Set(HeightVariableName, croppedHeight);
    }
}
