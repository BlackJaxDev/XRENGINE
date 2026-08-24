using System.Text.Json;
using System.Text.Json.Serialization;

namespace XREngine.Rendering.Profiling;

/// <summary>
/// Versioned, self-contained input for one deterministic component profile. Every value which
/// can affect execution, validation, or identity is explicit so a recipe never inherits editor
/// preferences.
/// </summary>
public sealed record RenderProfileRecipe
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("$schema")]
    public string? SchemaUri { get; init; }

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("component")]
    public string Component { get; init; } = string.Empty;

    [JsonPropertyName("execution_mode")]
    public RenderExecutionMode ExecutionMode { get; init; } = RenderExecutionMode.Component;

    [JsonPropertyName("backend")]
    public RuntimeGraphicsApiKind Backend { get; init; } = RuntimeGraphicsApiKind.Vulkan;

    [JsonPropertyName("adapter")]
    public string Adapter { get; init; } = "default";

    [JsonPropertyName("fixture")]
    public string Fixture { get; init; } = string.Empty;

    [JsonPropertyName("width")]
    public uint Width { get; init; } = 1920;

    [JsonPropertyName("height")]
    public uint Height { get; init; } = 1080;

    [JsonPropertyName("render_scale")]
    public double RenderScale { get; init; } = 1.0;

    [JsonPropertyName("color_format")]
    public string ColorFormat { get; init; } = "Rgba8";

    [JsonPropertyName("depth_format")]
    public string DepthFormat { get; init; } = "DepthComponent32f";

    [JsonPropertyName("sample_count")]
    public uint SampleCount { get; init; } = 1;

    [JsonPropertyName("frame_slots")]
    public uint FrameSlots { get; init; } = 3;

    [JsonPropertyName("warmup_frames")]
    public int WarmupFrames { get; init; } = 120;

    [JsonPropertyName("stability_frames")]
    public int StabilityFrames { get; init; } = 60;

    [JsonPropertyName("capture_frames")]
    public int CaptureFrames { get; init; } = 240;

    [JsonPropertyName("repetitions")]
    public int Repetitions { get; init; } = 1;

    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; init; } = 120;

    [JsonPropertyName("instrumentation")]
    public RenderProfileInstrumentation Instrumentation { get; init; } =
        RenderProfileInstrumentation.AggregateCpu | RenderProfileInstrumentation.CoarseGpu;

    [JsonPropertyName("validation_mode")]
    public RenderProfileValidationMode ValidationMode { get; init; } = RenderProfileValidationMode.CountersAndHash;

    [JsonPropertyName("label_policy")]
    public RenderProfileLabelPolicy LabelPolicy { get; init; } = RenderProfileLabelPolicy.StableFixtureLabels;

    [JsonPropertyName("hardware_counter_policy")]
    public RenderProfileHardwareCounterPolicy HardwareCounterPolicy { get; init; } = RenderProfileHardwareCounterPolicy.Disabled;

    [JsonPropertyName("cpu_sampling_policy")]
    public RenderProfileCpuSamplingPolicy CpuSamplingPolicy { get; init; } = RenderProfileCpuSamplingPolicy.AggregateOnly;

    [JsonPropertyName("scene")]
    public RenderProfileSceneConfiguration Scene { get; init; } = new();

    [JsonPropertyName("mutation")]
    public RenderProfileMutationConfiguration Mutation { get; init; } = new();

    [JsonPropertyName("workload")]
    public RenderProfileWorkloadConfiguration Workload { get; init; } = new();

    [JsonPropertyName("contract")]
    public RenderProfileFixtureContract Contract { get; init; } = new();

    [JsonPropertyName("worker_counts")]
    public int[] WorkerCounts { get; init; } = [1];

    [JsonPropertyName("expected")]
    public RenderProfileExpectedWork Expected { get; init; } = new();

    [JsonPropertyName("budgets")]
    public RenderProfileAcceptanceBudgets Budgets { get; init; } = new();

    /// <summary>Scaled width after applying the explicitly declared render scale.</summary>
    [JsonIgnore]
    public uint ScaledWidth => checked((uint)Math.Max(1, Math.Round(Width * RenderScale, MidpointRounding.AwayFromZero)));

    /// <summary>Scaled height after applying the explicitly declared render scale.</summary>
    [JsonIgnore]
    public uint ScaledHeight => checked((uint)Math.Max(1, Math.Round(Height * RenderScale, MidpointRounding.AwayFromZero)));

    /// <summary>Total retained frames across all explicitly requested repetitions.</summary>
    [JsonIgnore]
    public int TotalCaptureFrames => checked(CaptureFrames * Repetitions);

    /// <summary>Validates every field that affects reproducibility before work begins.</summary>
    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException($"Unsupported render-profile recipe schema {SchemaVersion}. Supported schema is {CurrentSchemaVersion}.");
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(Component);
        ArgumentException.ThrowIfNullOrWhiteSpace(Adapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(Fixture);
        ArgumentException.ThrowIfNullOrWhiteSpace(ColorFormat);
        ArgumentException.ThrowIfNullOrWhiteSpace(DepthFormat);
        if (Backend == RuntimeGraphicsApiKind.Unknown)
            throw new ArgumentOutOfRangeException(nameof(Backend));
        if (Width == 0 || Height == 0 || FrameSlots == 0 || SampleCount == 0)
            throw new ArgumentOutOfRangeException(nameof(Width), "Output extent, frame slots, and sample count must be non-zero.");
        if (!double.IsFinite(RenderScale) || RenderScale <= 0.0 || RenderScale > 4.0)
            throw new ArgumentOutOfRangeException(nameof(RenderScale), "Render scale must be finite and in (0, 4].");
        if (WarmupFrames < 0 || StabilityFrames < 0 || CaptureFrames <= 0 || Repetitions <= 0 || TimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(CaptureFrames));
        if (WorkerCounts is null || WorkerCounts.Length == 0 || WorkerCounts.Any(static count => count <= 0))
            throw new ArgumentOutOfRangeException(nameof(WorkerCounts), "At least one positive worker count is required.");
        Scene.Validate();
        Mutation.Validate();
        Workload.Validate();
        Contract.Validate();
        Expected.Validate();
        Budgets.Validate();
        _ = ScaledWidth;
        _ = ScaledHeight;
        _ = TotalCaptureFrames;
    }

    /// <summary>Parses JSON or JSONC without allowing an unknown field to silently alter a run.</summary>
    public static RenderProfileRecipe Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        JsonSerializerOptions options = CreateSerializerOptions();
        RenderProfileRecipe recipe = JsonSerializer.Deserialize<RenderProfileRecipe>(json, options)
            ?? throw new JsonException("Render-profile recipe was empty.");
        recipe.Validate();
        return recipe;
    }

    /// <summary>Creates the canonical serializer used for recipe/configuration artifacts.</summary>
    public static JsonSerializerOptions CreateSerializerOptions(bool writeIndented = false)
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = writeIndented,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
