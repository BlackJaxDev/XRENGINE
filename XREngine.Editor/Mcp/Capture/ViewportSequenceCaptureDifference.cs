using System.Text.Json.Serialization;

namespace XREngine.Editor.Mcp;

/// <summary>
/// Downsampled RGB difference statistics relative to the preceding captured frame.
/// Values are normalized to the range 0..1.
/// </summary>
internal sealed class ViewportSequenceCaptureDifference
{
    [JsonPropertyName("mean_absolute_error")]
    public double MeanAbsoluteError { get; init; }

    [JsonPropertyName("root_mean_square_error")]
    public double RootMeanSquareError { get; init; }

    [JsonPropertyName("changed_pixel_ratio")]
    public double ChangedPixelRatio { get; init; }

    [JsonPropertyName("maximum_channel_delta")]
    public double MaximumChannelDelta { get; init; }

    [JsonPropertyName("identical")]
    public bool Identical { get; init; }
}
