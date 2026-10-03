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

    [JsonPropertyName("profile_mode")]
    public RenderProfileMode ProfileMode { get; init; } = RenderProfileMode.DevelopmentProfile;

    [JsonPropertyName("enable_validation")]
    public bool EnableValidation { get; init; }

    [JsonPropertyName("enable_synchronization_validation")]
    public bool EnableSynchronizationValidation { get; init; }

    [JsonPropertyName("cpu_profiling")]
    public RenderProfileCpuConfiguration CpuProfiling { get; init; } = new();

    [JsonPropertyName("gpu_profiling")]
    public RenderProfileGpuConfiguration GpuProfiling { get; init; } = new();

    [JsonPropertyName("external_capture")]
    public RenderProfileExternalCaptureConfiguration ExternalCapture { get; init; } = new();

    /// <summary>Diagnostic observers cannot be used to accept a clean baseline.</summary>
    [JsonIgnore]
    public bool IsIntrusive => EnableValidation || EnableSynchronizationValidation || GpuProfiling.CalibratedTimestamps ||
        ExternalCapture?.IsRequested == true ||
        LabelPolicy != RenderProfileLabelPolicy.Disabled || CpuProfiling.EmitMarkers ||
        CpuSamplingPolicy is RenderProfileCpuSamplingPolicy.TargetedSpans or
            RenderProfileCpuSamplingPolicy.ExternalSamplerOptional or RenderProfileCpuSamplingPolicy.ExternalSamplerRequired ||
        HardwareCounterPolicy != RenderProfileHardwareCounterPolicy.Disabled ||
        (Instrumentation & (RenderProfileInstrumentation.TargetedCpuSpans |
            RenderProfileInstrumentation.TargetedGpuTimestamps | RenderProfileInstrumentation.HardwareCounters)) != 0;

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
        CpuProfiling.Validate();
        GpuProfiling.Validate();
        if (ExternalCapture is null)
            throw new ArgumentException("External capture configuration cannot be null.");
        ExternalCapture.Validate();
        const RenderProfileInstrumentation knownInstrumentation = RenderProfileInstrumentation.AggregateCpu |
            RenderProfileInstrumentation.TargetedCpuSpans | RenderProfileInstrumentation.CoarseGpu |
            RenderProfileInstrumentation.TargetedGpuTimestamps | RenderProfileInstrumentation.HardwareCounters;
        if ((Instrumentation & ~knownInstrumentation) != 0 || !Enum.IsDefined(ProfileMode) ||
            !Enum.IsDefined(CpuSamplingPolicy) || !Enum.IsDefined(HardwareCounterPolicy) ||
            !Enum.IsDefined(LabelPolicy) || !Enum.IsDefined(ValidationMode) || !Enum.IsDefined(ExecutionMode))
            throw new ArgumentException("Unknown profiling mode, instrumentation or policy.");
        if (EnableSynchronizationValidation && !EnableValidation)
            throw new ArgumentException("Synchronization validation requires validation.");
        if (ProfileMode is RenderProfileMode.CleanProfile or RenderProfileMode.ReleaseBenchmark && IsIntrusive)
            throw new ArgumentException("Clean and release profiles prohibit diagnostic observers, validation, labels, spans, sampling and counters.");
        if (CpuSamplingPolicy == RenderProfileCpuSamplingPolicy.ExternalSamplerRequired && CpuProfiling.SamplerIdentity is null)
            throw new ArgumentException("Required external sampling needs the identity of the attached sampler.");
        if (Instrumentation.HasFlag(RenderProfileInstrumentation.TargetedGpuTimestamps) && GpuProfiling.Targets.Length == 0)
            throw new ArgumentException("Targeted GPU timestamps require at least one selected target.");
        if (GpuProfiling.Targets.Length != 0 && !Instrumentation.HasFlag(RenderProfileInstrumentation.TargetedGpuTimestamps))
            throw new ArgumentException("GPU target selection requires targeted GPU timestamp instrumentation.");
        if ((CpuProfiling.Stages.Length != 0 || CpuProfiling.EmitMarkers) &&
            !Instrumentation.HasFlag(RenderProfileInstrumentation.TargetedCpuSpans) && CpuSamplingPolicy != RenderProfileCpuSamplingPolicy.TargetedSpans)
            throw new ArgumentException("CPU stage selection and markers require targeted span instrumentation.");
        if (Instrumentation.HasFlag(RenderProfileInstrumentation.HardwareCounters) || HardwareCounterPolicy != RenderProfileHardwareCounterPolicy.Disabled)
        {
            if (GpuProfiling.HardwareCounterIndices.Length == 0)
                throw new ArgumentException("Hardware counter capture requires explicitly selected counter indices.");
            if (ProfileMode != RenderProfileMode.Diagnostics)
                throw new ArgumentException("Hardware counter replay requires Diagnostics profile mode.");
        }
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
