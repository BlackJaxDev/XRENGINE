using ImageMagick;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
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
    public int MaxCaptures { get; set; }
    public int SkipFramesBeforeCapture { get; set; }
    public bool CapturePhase524bTemporalScenarios { get; set; }
    public bool CompletesPhase524bTemporalScenarioFrame { get; set; }
    public string? TemporalScenarioPipelineName { get; set; }
    public string? TemporalScenarioStage { get; set; }

    private int _captureCount;
    private int _framesSkipped;
    private ulong _temporalScenarioCaptureMask;

    protected override void Execute()
    {
        bool standardCaptureDue = false;
        if (MaxCaptures <= 0 || _captureCount < MaxCaptures)
        {
            if (_framesSkipped < SkipFramesBeforeCapture)
                _framesSkipped++;
            else
                standardCaptureDue = true;
        }

        bool temporalCaptureDue = false;
        int temporalSampleIndex = -1;
        Phase524bTemporalSampleDefinition temporalDefinition = default;
        int temporalSequenceFrame = -1;
        if (CapturePhase524bTemporalScenarios &&
            Phase524bTemporalScenarioDiagnostics.TryGetActiveCaptureSample(
                out temporalSampleIndex,
                out temporalDefinition))
        {
            ulong sampleBit = 1UL << temporalSampleIndex;
            temporalCaptureDue = (_temporalScenarioCaptureMask & sampleBit) == 0UL;
            temporalSequenceFrame = Phase524bTemporalScenarioDiagnostics.SequenceFrame;
        }

        if (!standardCaptureDue && !temporalCaptureDue)
        {
            CompleteTemporalScenarioFrameIfNeeded();
            return;
        }

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

        bool wroteStandardFile = false;
        if (standardCaptureDue && !string.IsNullOrWhiteSpace(OutputFilePath))
        {
            RenderedOutputCaptureMetrics metrics = StereoRenderedOutputMetrics.MeasureCapture(
                rgbaFloats,
                width,
                height);
            WriteCapture(OutputFilePath!, rgbaFloats, width, height, metrics);
            wroteStandardFile = true;
        }

        if (temporalCaptureDue)
        {
            if (string.IsNullOrWhiteSpace(TemporalScenarioPipelineName) ||
                string.IsNullOrWhiteSpace(TemporalScenarioStage))
            {
                throw new InvalidOperationException(
                    "Phase 5.2.4b temporal capture requires pipeline and stage names.");
            }

            string temporalPath = DefaultPipelineDiagnosticCapture.ResolveTemporalScenarioOutputPath(
                TemporalScenarioPipelineName!,
                temporalDefinition.Sample,
                TemporalScenarioStage!,
                SourceLayerIndex);
            RenderedOutputCaptureMetrics temporalMetrics = StereoRenderedOutputMetrics.MeasureCapture(
                rgbaFloats,
                width,
                height);
            temporalMetrics.TemporalScenario = temporalDefinition.Scenario.ToString();
            temporalMetrics.TemporalSample = temporalDefinition.Sample.ToString();
            temporalMetrics.VelocityOracle = temporalDefinition.VelocityOracle.ToString();
            temporalMetrics.TemporalSequenceFrame = temporalSequenceFrame;
            temporalMetrics.RenderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
            WriteCapture(temporalPath, rgbaFloats, width, height, temporalMetrics);
            _temporalScenarioCaptureMask |= 1UL << temporalSampleIndex;
        }

        if (standardCaptureDue && !string.IsNullOrWhiteSpace(WidthVariableName))
            instance.Variables.Set(WidthVariableName!, width);
        if (standardCaptureDue && !string.IsNullOrWhiteSpace(HeightVariableName))
            instance.Variables.Set(HeightVariableName!, height);
        if (standardCaptureDue && !string.IsNullOrWhiteSpace(SuccessVariableName))
            instance.Variables.Set(SuccessVariableName!, wroteStandardFile || !string.IsNullOrWhiteSpace(DestinationBufferName));

        if (standardCaptureDue)
            _captureCount++;
        CompleteTemporalScenarioFrameIfNeeded();
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

    private void CompleteTemporalScenarioFrameIfNeeded()
    {
        if (CompletesPhase524bTemporalScenarioFrame)
        {
            Phase524bTemporalScenarioDiagnostics.CompleteStrictSpsFrame(
                RuntimeEngine.Rendering.State.RenderFrameId);
        }
    }

    private void WriteCapture(
        string outputFilePath,
        float[] rgbaFloats,
        int width,
        int height,
        RenderedOutputCaptureMetrics metrics)
    {
        string filePath = Path.GetFullPath(outputFilePath);
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using MagickImage image = CreateImage(rgbaFloats, width, height);
        if (FlipVertically)
            image.Flip();

        image.Write(filePath);
        using (FileStream captureStream = File.OpenRead(filePath))
            metrics.CaptureSha256 = Convert.ToHexString(SHA256.HashData(captureStream));
        metrics.CapturePath = filePath;
        metrics.CapturedAtUtc = DateTimeOffset.UtcNow;
        File.WriteAllText(
            filePath + ".metrics.json",
            JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
    }

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
