using System.Text.Json;
using System.Text.Json.Serialization;

namespace XREngine.Rendering.Profiling;

/// <summary>
/// Versioned, self-contained input for one deterministic component profile. A recipe is
/// intentionally independent of editor preferences so its hash is a reproducible workload key.
/// </summary>
public sealed record RenderProfileRecipe
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("component")]
    public string Component { get; init; } = string.Empty;

    [JsonPropertyName("execution_mode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RenderExecutionMode ExecutionMode { get; init; } = RenderExecutionMode.Component;

    [JsonPropertyName("backend")]
    public RuntimeGraphicsApiKind Backend { get; init; } = RuntimeGraphicsApiKind.Vulkan;

    [JsonPropertyName("fixture")]
    public string Fixture { get; init; } = string.Empty;

    [JsonPropertyName("width")]
    public uint Width { get; init; } = 1920;

    [JsonPropertyName("height")]
    public uint Height { get; init; } = 1080;

    [JsonPropertyName("frame_slots")]
    public uint FrameSlots { get; init; } = 3;

    [JsonPropertyName("warmup_frames")]
    public int WarmupFrames { get; init; } = 120;

    [JsonPropertyName("stability_frames")]
    public int StabilityFrames { get; init; } = 60;

    [JsonPropertyName("capture_frames")]
    public int CaptureFrames { get; init; } = 240;

    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; init; } = 120;

    [JsonPropertyName("worker_counts")]
    public int[] WorkerCounts { get; init; } = [1];

    [JsonPropertyName("forced_dirty")]
    public bool ForcedDirty { get; init; }

    [JsonPropertyName("instrumentation")]
    public RenderProfileInstrumentation Instrumentation { get; init; } = RenderProfileInstrumentation.AggregateCpu | RenderProfileInstrumentation.CoarseGpu;

    [JsonPropertyName("expected")]
    public RenderProfileExpectedWork Expected { get; init; } = new();

    /// <summary>Validates every field that affects reproducibility before work begins.</summary>
    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException($"Unsupported render-profile recipe schema {SchemaVersion}. Supported schema is {CurrentSchemaVersion}.");
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(Component);
        ArgumentException.ThrowIfNullOrWhiteSpace(Fixture);
        if (Backend == RuntimeGraphicsApiKind.Unknown)
            throw new ArgumentOutOfRangeException(nameof(Backend));
        if (Width == 0 || Height == 0 || FrameSlots == 0)
            throw new ArgumentOutOfRangeException(nameof(Width), "Output extent and frame slots must be non-zero.");
        if (WarmupFrames < 0 || StabilityFrames < 0 || CaptureFrames <= 0 || TimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(CaptureFrames));
        if (WorkerCounts.Length == 0 || WorkerCounts.Any(static count => count <= 0))
            throw new ArgumentOutOfRangeException(nameof(WorkerCounts), "At least one positive worker count is required.");
    }

    /// <summary>Parses JSON or JSONC without allowing an unknown field to silently alter a run.</summary>
    public static RenderProfileRecipe Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        JsonSerializerOptions options = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        RenderProfileRecipe recipe = JsonSerializer.Deserialize<RenderProfileRecipe>(json, options)
            ?? throw new JsonException("Render-profile recipe was empty.");
        recipe.Validate();
        return recipe;
    }
}

/// <summary>Instrumentation explicitly enabled for a run. Non-clean modes are diagnostic only.</summary>
[Flags]
public enum RenderProfileInstrumentation
{
    None = 0,
    AggregateCpu = 1 << 0,
    TargetedCpuSpans = 1 << 1,
    CoarseGpu = 1 << 2,
    TargetedGpuTimestamps = 1 << 3,
    HardwareCounters = 1 << 4,
}

/// <summary>Operation counts the fixture must prove before a result is accepted.</summary>
public sealed record RenderProfileExpectedWork
{
    [JsonPropertyName("draws")]
    public int Draws { get; init; }

    [JsonPropertyName("submissions")]
    public int Submissions { get; init; }

    [JsonPropertyName("command_buffers")]
    public int CommandBuffers { get; init; }

    [JsonPropertyName("descriptors")]
    public int Descriptors { get; init; }
}
