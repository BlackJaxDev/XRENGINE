using System.Text.Json.Serialization;
using XREngine.Rendering.Models.Gaussian.Cache;

namespace XREngine.Rendering.Models.Gaussian.Baking.Report;

/// <summary>
/// Holds the result of a bake. <see cref="BakeReportWriter"/> writes it as <c>report.json</c> (schema version 1)
/// in the output folder, for a bake that is ready and for a bake that failed.
/// </summary>
/// <remarks>
/// The sections that the bake did not reach are null. For example, a bake that stops at the load has no
/// <see cref="Clip"/>, <see cref="Splats"/>, or <see cref="Cache"/>.
/// </remarks>
public sealed class BakeReport
{
    /// <summary>The schema version of <c>report.json</c>.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The status of a bake that wrote all its outputs.</summary>
    public const string ReadyStatus = "ready";

    /// <summary>The status of a bake that stopped with a failure code.</summary>
    public const string FailedStatus = "failed";

    /// <summary>Gets or sets the schema version, <see cref="CurrentSchemaVersion"/>.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Gets or sets the status: <see cref="ReadyStatus"/> or <see cref="FailedStatus"/>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = ReadyStatus;

    /// <summary>Gets or sets the failure code (see <see cref="GaussianBakeFailureCodes"/>), or null for a bake that is ready.</summary>
    [JsonPropertyName("failureCode")]
    public string? FailureCode { get; set; }

    /// <summary>Gets or sets the message that explains the failure, or null for a bake that is ready.</summary>
    [JsonPropertyName("failureDetail")]
    public string? FailureDetail { get; set; }

    /// <summary>Gets or sets the informational version of the engine rendering assembly that made the bake.</summary>
    [JsonPropertyName("engineVersion")]
    public string EngineVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the version of the native meshoptimizer library, as <c>v1.3 &lt;commit&gt;</c>.</summary>
    [JsonPropertyName("meshoptimizerVersion")]
    public string MeshoptimizerVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the format version of the cache header.</summary>
    [JsonPropertyName("cacheFormatVersion")]
    public int CacheFormatVersion { get; set; } = GaussianCacheHeader.CurrentFormatVersion;

    /// <summary>Gets or sets the representation of the cache. This engine version writes only <c>BakedDriver</c>.</summary>
    [JsonPropertyName("representation")]
    public string Representation { get; set; } = nameof(GaussianCacheRepresentation.BakedDriver);

    /// <summary>Gets or sets the kind of bake: <c>avatar</c>, <c>item</c>, or <c>world</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the input of the bake.</summary>
    [JsonPropertyName("input")]
    public BakeReportInput? Input { get; set; }

    /// <summary>Gets or sets the baked motion.</summary>
    [JsonPropertyName("clip")]
    public BakeReportClip? Clip { get; set; }

    /// <summary>Gets or sets the splat counts.</summary>
    [JsonPropertyName("splats")]
    public BakeReportSplats? Splats { get; set; }

    /// <summary>Gets or sets the size of the driver meshes.</summary>
    [JsonPropertyName("driver")]
    public BakeReportDriver? Driver { get; set; }

    /// <summary>Gets or sets the stored frames.</summary>
    [JsonPropertyName("frames")]
    public BakeReportFrames? Frames { get; set; }

    /// <summary>Gets or sets the number of grid bits on each axis of the cache.</summary>
    [JsonPropertyName("gridBits")]
    public int? GridBits { get; set; }

    /// <summary>Gets or sets the value that the splat colors were divided by.</summary>
    [JsonPropertyName("colorScale")]
    public float? ColorScale { get; set; }

    /// <summary>Gets or sets the written cache.</summary>
    [JsonPropertyName("cache")]
    public BakeReportCache? Cache { get; set; }

    /// <summary>Gets or sets the videos and posters of the preview loop.</summary>
    [JsonPropertyName("outputs")]
    public List<BakeReportOutput> Outputs { get; set; } = [];

    /// <summary>Gets or sets the time of each bake step, in milliseconds.</summary>
    [JsonPropertyName("timingsMs")]
    public BakeReportTimings TimingsMs { get; set; } = new();

    /// <summary>Gets or sets the components that render content but that the bake does not capture.</summary>
    [JsonPropertyName("skipped")]
    public List<BakeReportSkipped> Skipped { get; set; } = [];

    /// <summary>Gets or sets the warnings of the bake, for example <c>clip-truncated</c>.</summary>
    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];

    /// <summary>Sets the status to <see cref="ReadyStatus"/> and removes the failure code and the failure detail.</summary>
    public void MarkReady()
    {
        Status = ReadyStatus;
        FailureCode = null;
        FailureDetail = null;
    }

    /// <summary>Sets the status to <see cref="FailedStatus"/> with a failure code and a message.</summary>
    /// <param name="failureCode">One of the values of <see cref="GaussianBakeFailureCodes"/>.</param>
    /// <param name="failureDetail">The message that explains the failure.</param>
    public void MarkFailed(string failureCode, string failureDetail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        Status = FailedStatus;
        FailureCode = failureCode;
        FailureDetail = failureDetail ?? string.Empty;
    }
}
