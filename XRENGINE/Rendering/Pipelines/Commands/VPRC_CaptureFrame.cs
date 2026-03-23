using ImageMagick;
using System;
using System.IO;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Captures a named texture, a named FBO color attachment, or the current output FBO color attachment.
/// The captured pixels can be written to a pipeline data buffer and optionally exported to disk.
/// </summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_CaptureFrame : ViewportRenderCommand
{
    public string? SourceTextureName { get; set; }
    public string? SourceFBOName { get; set; }
    public string? DestinationBufferName { get; set; }
    public string? OutputFilePath { get; set; }
    public int SourceMipLevel { get; set; }
    public int SourceLayerIndex { get; set; }
    public bool UploadToGpuBuffer { get; set; }
    public bool FlipVertically { get; set; } = true;
    public string? WidthVariableName { get; set; }
    public string? HeightVariableName { get; set; }
    public string? SuccessVariableName { get; set; }

    protected override void Execute()
    {
        XRRenderPipelineInstance instance = ActivePipelineInstance;
        if (!VPRCSourceTextureHelpers.TryResolveColorTexture(instance, SourceTextureName, SourceFBOName, out XRTexture? texture, out string failure) ||
            texture is null)
            throw new InvalidOperationException($"CaptureFrame failed: {failure}");

        if (!VPRCReadbackHelpers.TryReadTextureMip(texture, SourceMipLevel, SourceLayerIndex, out float[] rgbaFloats, out int width, out int height, out failure))
            throw new InvalidOperationException($"CaptureFrame readback failed: {failure}");

        if (!string.IsNullOrWhiteSpace(DestinationBufferName))
        {
            XRDataBuffer? destinationBuffer = instance.GetBuffer(DestinationBufferName!);
            if (destinationBuffer is null)
                throw new InvalidOperationException($"CaptureFrame destination buffer '{DestinationBufferName}' was not found.");

            destinationBuffer.SetRawBytes(VPRCReadbackHelpers.ToBytes(rgbaFloats));
            if (UploadToGpuBuffer)
                destinationBuffer.PushData();
        }

        bool wroteFile = false;
        if (!string.IsNullOrWhiteSpace(OutputFilePath))
        {
            string filePath = Path.GetFullPath(OutputFilePath!);
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            using MagickImage image = CreateImage(rgbaFloats, width, height);
            if (FlipVertically)
                image.Flip();

            image.Write(filePath);
            wroteFile = true;
        }

        if (!string.IsNullOrWhiteSpace(WidthVariableName))
            instance.Variables.Set(WidthVariableName!, width);
        if (!string.IsNullOrWhiteSpace(HeightVariableName))
            instance.Variables.Set(HeightVariableName!, height);
        if (!string.IsNullOrWhiteSpace(SuccessVariableName))
            instance.Variables.Set(SuccessVariableName!, wroteFile || !string.IsNullOrWhiteSpace(DestinationBufferName));
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);

        string? sourceName = !string.IsNullOrWhiteSpace(SourceTextureName)
            ? MakeTextureResource(SourceTextureName!)
            : !string.IsNullOrWhiteSpace(SourceFBOName)
                ? MakeFboColorResource(SourceFBOName!)
                : context.CurrentRenderTarget?.Name is { Length: > 0 } currentTarget
                    ? MakeFboColorResource(currentTarget)
                    : null;

        if (sourceName is null)
            return;

        context.GetOrCreateSyntheticPass($"CaptureFrame_{GetSourceDisplayName()}")
            .WithStage(ERenderGraphPassStage.Transfer)
            .SampleTexture(sourceName);
    }

    private string GetSourceDisplayName()
        => SourceTextureName
            ?? SourceFBOName
            ?? ActivePipelineInstance.RenderState.OutputFBO?.Name
            ?? "Output";

    private static MagickImage CreateImage(float[] rgbaFloats, int width, int height)
    {
        byte[] rgba8 = new byte[rgbaFloats.Length];
        for (int i = 0; i < rgbaFloats.Length; i++)
        {
            float value = Math.Clamp(rgbaFloats[i], 0.0f, 1.0f);
            rgba8[i] = (byte)MathF.Round(value * 255.0f);
        }

        return new MagickImage(rgba8, new MagickReadSettings
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = MagickFormat.Rgba,
            Depth = 8,
        });
    }
}
