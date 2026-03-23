using System;
using System.Numerics;

namespace XREngine.Rendering.Pipelines.Commands;

public enum EHistogramSourceChannel
{
    Luminance,
    Red,
    Green,
    Blue,
    Alpha,
    MaxRgb,
}

/// <summary>
/// Computes a histogram from a texture readback and stores the bins in a pipeline buffer.
/// Summary statistics can also be exposed as pipeline variables for later commands.
/// </summary>
[RenderPipelineScriptCommand]
public class VPRC_ComputeHistogram : ViewportRenderCommand
{
    public string? SourceTextureName { get; set; }
    public string? DestinationBufferName { get; set; }
    public int SourceMipLevel { get; set; }
    public int SourceLayerIndex { get; set; }
    public int BinCount { get; set; } = 64;
    public float HistogramMinValue { get; set; } = 0.0f;
    public float HistogramMaxValue { get; set; } = 16.0f;
    public EHistogramSourceChannel SourceChannel { get; set; } = EHistogramSourceChannel.Luminance;
    public Vector3 LuminanceWeights { get; set; } = new(0.2126f, 0.7152f, 0.0722f);
    public bool UploadToGpuBuffer { get; set; } = true;
    public string? PixelCountVariableName { get; set; }
    public string? MinValueVariableName { get; set; }
    public string? MaxValueVariableName { get; set; }
    public string? AverageValueVariableName { get; set; }
    public string? LogAverageValueVariableName { get; set; }

    protected override void Execute()
    {
        if (SourceTextureName is null || BinCount <= 0)
            return;

        if (HistogramMaxValue <= HistogramMinValue)
            throw new InvalidOperationException($"Histogram range for texture '{SourceTextureName}' is invalid: min={HistogramMinValue}, max={HistogramMaxValue}.");

        XRRenderPipelineInstance instance = ActivePipelineInstance;
        if (!instance.TryGetTexture(SourceTextureName, out XRTexture? texture) || texture is null)
            return;

        if (!VPRCReadbackHelpers.TryReadTextureMip(texture, SourceMipLevel, SourceLayerIndex, out float[] rgbaFloats, out _, out _, out string failure))
            throw new InvalidOperationException($"Texture '{SourceTextureName}' histogram readback failed: {failure}");

        uint[] histogram = new uint[BinCount];
        uint pixelCount = 0u;
        uint logSampleCount = 0u;
        float observedMin = float.PositiveInfinity;
        float observedMax = float.NegativeInfinity;
        double sum = 0.0;
        double logSum = 0.0;
        float histogramRange = HistogramMaxValue - HistogramMinValue;

        for (int index = 0; index + 3 < rgbaFloats.Length; index += 4)
        {
            float value = SelectSampleValue(rgbaFloats[index + 0], rgbaFloats[index + 1], rgbaFloats[index + 2], rgbaFloats[index + 3]);
            observedMin = MathF.Min(observedMin, value);
            observedMax = MathF.Max(observedMax, value);
            sum += value;
            pixelCount++;

            if (value > 0.0f)
            {
                logSum += Math.Log(value);
                logSampleCount++;
            }

            int binIndex = value <= HistogramMinValue
                ? 0
                : value >= HistogramMaxValue
                    ? BinCount - 1
                    : Math.Clamp((int)(((value - HistogramMinValue) / histogramRange) * BinCount), 0, BinCount - 1);
            histogram[binIndex]++;
        }

        if (pixelCount == 0u)
        {
            observedMin = 0.0f;
            observedMax = 0.0f;
        }

        float average = pixelCount == 0u ? 0.0f : (float)(sum / pixelCount);
        float logAverage = logSampleCount == 0u ? 0.0f : (float)Math.Exp(logSum / logSampleCount);

        if (!string.IsNullOrWhiteSpace(DestinationBufferName))
        {
            XRDataBuffer? destinationBuffer = instance.GetBuffer(DestinationBufferName!);
            if (destinationBuffer is not null)
            {
                destinationBuffer.SetRawBytes(VPRCReadbackHelpers.ToBytes(histogram));
                if (UploadToGpuBuffer)
                    destinationBuffer.PushData();
            }
        }

        if (!string.IsNullOrWhiteSpace(PixelCountVariableName))
            instance.Variables.Set(PixelCountVariableName!, pixelCount);
        if (!string.IsNullOrWhiteSpace(MinValueVariableName))
            instance.Variables.Set(MinValueVariableName!, observedMin);
        if (!string.IsNullOrWhiteSpace(MaxValueVariableName))
            instance.Variables.Set(MaxValueVariableName!, observedMax);
        if (!string.IsNullOrWhiteSpace(AverageValueVariableName))
            instance.Variables.Set(AverageValueVariableName!, average);
        if (!string.IsNullOrWhiteSpace(LogAverageValueVariableName))
            instance.Variables.Set(LogAverageValueVariableName!, logAverage);
    }

    private float SelectSampleValue(float red, float green, float blue, float alpha)
        => SourceChannel switch
        {
            EHistogramSourceChannel.Red => red,
            EHistogramSourceChannel.Green => green,
            EHistogramSourceChannel.Blue => blue,
            EHistogramSourceChannel.Alpha => alpha,
            EHistogramSourceChannel.MaxRgb => MathF.Max(red, MathF.Max(green, blue)),
            _ => (red * LuminanceWeights.X) + (green * LuminanceWeights.Y) + (blue * LuminanceWeights.Z),
        };
}
