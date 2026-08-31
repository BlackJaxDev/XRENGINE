using System.Globalization;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.RenderBench;

public sealed record RenderBenchOptions
{
    private static readonly HashSet<string> s_allowedArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        "backend", "execution-mode", "recipe", "recipe-file", "fixture", "output-dir", "mcp-policy", "mcp-port",
        "session-name", "session-token", "shutdown-event", "wait-for-mcp-start", "width", "height",
        "layers", "frame-slots", "samples", "color-format", "depth-format", "warmup-frames",
        "stability-frames", "capture-frames", "fixed-step", "random-seed", "frozen-world", "help",
        "scenario", "scenario-lane", "scenario-depth", "scenario-frames", "scenario-repeats", "scenario-workload", "scenario-timing", "scenario-renderdoc", "scenario-renderdoc-step",
        "scenario-cache-root",
    };

    public string Backend { get; init; } = "Vulkan";
    public RenderExecutionMode ExecutionMode { get; init; } = RenderExecutionMode.Presentationless;
    public string Recipe { get; init; } = "deterministic-clear";
    public string? RecipeFile { get; init; }
    public string Fixture { get; init; } = "synthetic-clear";
    public required string OutputDirectory { get; init; }
    public RenderBenchMcpPolicy McpPolicy { get; init; } = RenderBenchMcpPolicy.Disabled;
    public int McpPort { get; init; } = 5467;
    public string? SessionToken { get; init; }
    public string? SessionName { get; init; }
    public string? ShutdownEventName { get; init; }
    public bool WaitForMcpStart { get; init; }
    public uint Width { get; init; } = 1920;
    public uint Height { get; init; } = 1080;
    public uint Layers { get; init; } = 1;
    public uint FrameSlots { get; init; } = 3;
    public uint Samples { get; init; } = 1;
    public EPixelInternalFormat ColorFormat { get; init; } = EPixelInternalFormat.Rgba8;
    public EPixelInternalFormat DepthFormat { get; init; } = EPixelInternalFormat.DepthComponent32f;
    public int WarmupFrames { get; init; } = 30;
    public int StabilityFrames { get; init; } = 5;
    public int CaptureFrames { get; init; } = 120;
    public double FixedStepSeconds { get; init; } = 1.0 / 60.0;
    public int RandomSeed { get; init; } = 0x585245;
    public bool FrozenWorld { get; init; }
    /// <summary>Opt-in correctness scenarios, separate from measured component recipes.</summary>
    public string? Scenario { get; init; }
    public string? ScenarioLane { get; init; }
    public string ScenarioDepth { get; init; } = "both";
    public int ScenarioFrames { get; init; } = 24;
    public int ScenarioRepeats { get; init; } = 2;
    /// <summary>Real production-scene fixture, or <c>all</c> for the representative matrix.</summary>
    public string ScenarioWorkload { get; init; } = RenderBenchScenarioWorkloads.Default;
    /// <summary>Opt-in delayed GPU timestamp diagnostics; does not make correctness scenarios performance evidence.</summary>
    public bool ScenarioTiming { get; init; }
    public bool ScenarioRenderDoc { get; init; }
    public int ScenarioRenderDocStep { get; init; }
    /// <summary>Explicit cache owner for isolated cold/warm pipeline correctness runs.</summary>
    public string? ScenarioCacheRoot { get; init; }

    public RenderTargetOutputProperties OutputProperties
        => new(Width, Height, Layers, ColorFormat, DepthFormat, "Linear", Samples, FrameSlots);

    public static RenderBenchOptions Parse(string[] args)
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected positional argument '{argument}'.");

            string key = argument[2..];
            if (!s_allowedArguments.Contains(key))
                throw new ArgumentException($"Unknown argument '--{key}'.");
            if (values.ContainsKey(key))
                throw new ArgumentException($"Argument '--{key}' may only be specified once.");
            bool flag = key is "wait-for-mcp-start" or "frozen-world" or "help" or "scenario-timing" or "scenario-renderdoc";
            values[key] = flag ? "true" : ReadValue(args, ref index, argument);
        }

        if (values.ContainsKey("help"))
            throw new RenderBenchHelpRequestedException();

        string backend = Get(values, "backend", "Vulkan");
        if (!backend.Equals("Vulkan", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"RenderBench backend '{backend}' is unavailable. This executable requires the real Vulkan backend.");

        string outputDirectory = Get(values, "output-dir", string.Empty);
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("--output-dir is required so benchmark evidence has an explicit bounded owner.");

        RenderExecutionMode mode = ParseExecutionMode(Get(values, "execution-mode", "Presentationless"));
        if (mode is not (RenderExecutionMode.Presentationless or RenderExecutionMode.Component))
            throw new NotSupportedException($"Execution mode '{mode}' is not owned by the dedicated RenderBench process.");

        RenderBenchMcpPolicy mcpPolicy = ParseEnum<RenderBenchMcpPolicy>(Get(values, "mcp-policy", "Disabled"), "MCP policy");
        int mcpPort = ParseInt(values, "mcp-port", 5467, 1, 65535);
        string? sessionToken = values.GetValueOrDefault("session-token");
        if (mcpPolicy == RenderBenchMcpPolicy.Control && string.IsNullOrWhiteSpace(sessionToken))
            throw new ArgumentException("Control MCP policy requires --session-token.");
        bool waitForMcpStart = values.ContainsKey("wait-for-mcp-start");
        if (waitForMcpStart && mcpPolicy != RenderBenchMcpPolicy.Control)
            throw new ArgumentException("--wait-for-mcp-start requires Control MCP policy.");
        string? sessionName = values.GetValueOrDefault("session-name");
        if (waitForMcpStart && string.IsNullOrWhiteSpace(sessionName))
            throw new ArgumentException("--wait-for-mcp-start requires --session-name for endpoint identity validation.");

        RenderBenchOptions result = new()
        {
            Backend = "Vulkan",
            ExecutionMode = mode,
            Recipe = Get(values, "recipe", "deterministic-clear"),
            RecipeFile = values.TryGetValue("recipe-file", out string? recipeFile) && !string.IsNullOrWhiteSpace(recipeFile)
                ? Path.GetFullPath(recipeFile)
                : null,
            Fixture = Get(values, "fixture", "synthetic-clear"),
            OutputDirectory = Path.GetFullPath(outputDirectory),
            McpPolicy = mcpPolicy,
            McpPort = mcpPort,
            SessionToken = sessionToken,
            SessionName = sessionName,
            ShutdownEventName = values.GetValueOrDefault("shutdown-event"),
            WaitForMcpStart = waitForMcpStart,
            Width = ParseUInt(values, "width", 1920, 1),
            Height = ParseUInt(values, "height", 1080, 1),
            Layers = ParseUInt(values, "layers", 1, 1),
            FrameSlots = ParseUInt(values, "frame-slots", 3, 1),
            Samples = ParseUInt(values, "samples", 1, 1),
            ColorFormat = ParseEnum<EPixelInternalFormat>(Get(values, "color-format", nameof(EPixelInternalFormat.Rgba8)), "color format"),
            DepthFormat = ParseEnum<EPixelInternalFormat>(Get(values, "depth-format", nameof(EPixelInternalFormat.DepthComponent32f)), "depth format"),
            WarmupFrames = ParseInt(values, "warmup-frames", 30, 0, int.MaxValue),
            StabilityFrames = ParseInt(values, "stability-frames", 5, 1, int.MaxValue),
            CaptureFrames = ParseInt(values, "capture-frames", 120, 1, int.MaxValue),
            FixedStepSeconds = ParseDouble(values, "fixed-step", 1.0 / 60.0, double.Epsilon),
            RandomSeed = ParseInt(values, "random-seed", 0x585245, int.MinValue, int.MaxValue),
            FrozenWorld = values.ContainsKey("frozen-world"),
            Scenario = values.GetValueOrDefault("scenario"),
            ScenarioLane = values.GetValueOrDefault("scenario-lane"),
            ScenarioDepth = Get(values, "scenario-depth", "both").ToLowerInvariant(),
            ScenarioFrames = ParseInt(values, "scenario-frames",
                string.Equals(values.GetValueOrDefault("scenario"), "phase53-streaming", StringComparison.OrdinalIgnoreCase) ? 240 : 24,
                12, 240),
            ScenarioRepeats = ParseInt(values, "scenario-repeats", 2, 2, 4),
            ScenarioWorkload = Get(values, "scenario-workload", RenderBenchScenarioWorkloads.Default).ToLowerInvariant(),
            ScenarioTiming = values.ContainsKey("scenario-timing"),
            ScenarioRenderDoc = values.ContainsKey("scenario-renderdoc"),
            ScenarioRenderDocStep = ParseInt(values, "scenario-renderdoc-step", 0, 0, 239),
            ScenarioCacheRoot = values.TryGetValue("scenario-cache-root", out string? cacheRoot) && !string.IsNullOrWhiteSpace(cacheRoot)
                ? Path.GetFullPath(cacheRoot)
                : null,
        };

        if (result.Scenario is not null)
        {
            bool phase53 = result.Scenario is "phase53-streaming" or "phase53-materials" or "phase53-pipelines";
            if (values.ContainsKey("scenario-renderdoc-step") && !result.ScenarioRenderDoc)
                throw new ArgumentException("--scenario-renderdoc-step requires --scenario-renderdoc.");
            if (result.ScenarioRenderDocStep >= result.ScenarioFrames)
                throw new ArgumentException("The RenderDoc step must fall within the scripted frame sequence.");
            if (result.ScenarioRenderDoc && result.ScenarioLane is not ("eligibility" or "disabled" or "hiz"))
                throw new ArgumentException("--scenario-renderdoc requires one visibility child lane and an attached RenderDoc module.");
            if (!phase53 && result.Scenario is not ("phase52-visibility" or "phase52-buffers" or "phase52-all"))
                throw new ArgumentException("Unknown scenario. Use phase52-visibility, phase52-buffers, phase52-all, phase53-streaming, phase53-materials, or phase53-pipelines.");
            if (result.Scenario == "phase53-pipelines" && result.ScenarioCacheRoot is null)
                throw new ArgumentException("Pipeline cold/warm scenarios require an explicit --scenario-cache-root.");
            if (phase53 && (result.ScenarioRenderDoc || result.ScenarioTiming || values.ContainsKey("scenario-workload")))
                throw new ArgumentException("Phase 5.3 scenarios own their workloads and diagnostics; Phase 5.2 workload/timing/capture controls do not apply.");
            if (result.Scenario != "phase53-pipelines" && result.ScenarioCacheRoot is not null)
                throw new ArgumentException("--scenario-cache-root requires --scenario phase53-pipelines.");
            if (result.RecipeFile is not null || values.ContainsKey("recipe") || values.ContainsKey("fixture") ||
                result.McpPolicy != RenderBenchMcpPolicy.Disabled || mode != RenderExecutionMode.Presentationless)
                throw new ArgumentException("Correctness scenarios require Presentationless mode, disabled MCP, and no component recipe.");
            if (values.ContainsKey("warmup-frames") || values.ContainsKey("stability-frames") ||
                values.ContainsKey("capture-frames") || values.ContainsKey("frozen-world"))
                throw new ArgumentException("Correctness scenarios retain every scripted frame; use --scenario-frames instead of component warmup/capture controls.");
            if (result.ScenarioDepth is not ("normal" or "reversed" or "both"))
                throw new ArgumentException("--scenario-depth must be normal, reversed, or both.");
            if (result.ScenarioWorkload != "all" && !RenderBenchScenarioWorkloads.IsKnown(result.ScenarioWorkload))
                throw new ArgumentException("--scenario-workload must be default, all, open-static, moderate-static, heavy-static, heavy-moving-cut, masked-static, or masked-moving.");
            if (result.ScenarioLane is not null && result.ScenarioWorkload == "all")
                throw new ArgumentException("A scenario child lane requires one concrete --scenario-workload.");
            if (!phase53 && result.ScenarioLane is not null &&
                (result.ScenarioLane is not ("eligibility" or "disabled" or "hiz" or "buffers") || result.ScenarioDepth == "both"))
                throw new ArgumentException("A scenario child lane requires eligibility, disabled, hiz, or buffers and one explicit depth convention.");
            if (phase53 && result.ScenarioLane is not null &&
                (result.ScenarioDepth == "both" ||
                 (result.Scenario == "phase53-pipelines" ? result.ScenarioLane is not ("cold" or "warm") : result.ScenarioLane != "production")))
                throw new ArgumentException("Phase 5.3 child lanes require an explicit depth and cold/warm for pipelines or production for streaming/materials.");
            if (result.Width > 4096 || result.Height > 4096 || result.Layers != 1 || result.Samples != 1 ||
                result.FrameSlots is < 2 or > 4 || result.ColorFormat != EPixelInternalFormat.Rgba8)
                throw new ArgumentException("Scenario output is bounded to 4096x4096 RGBA8, one layer/sample, and 2-4 frame slots.");
            if (result.ScenarioLane is not null &&
                ((result.ScenarioLane == "buffers" && result.Scenario == "phase52-visibility") ||
                 (result.ScenarioLane != "buffers" && result.Scenario == "phase52-buffers")))
                throw new ArgumentException("The child lane must belong to the selected scenario.");
        }
        else if (values.Keys.Any(static key => key.StartsWith("scenario-", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Scenario controls require --scenario.");

        if (result.RecipeFile is null &&
            (!result.Recipe.Equals("deterministic-clear", StringComparison.OrdinalIgnoreCase) ||
             !result.Fixture.Equals("synthetic-clear", StringComparison.OrdinalIgnoreCase)))
        {
            throw new NotSupportedException(
                $"Phase 2 supports only recipe 'deterministic-clear' with fixture 'synthetic-clear'; received '{result.Recipe}'/'{result.Fixture}'.");
        }

        result.OutputProperties.Validate();
        return result;
    }

    public static string Usage => """
        XREngine.RenderBench --output-dir <path> [options]
          --backend Vulkan
          --execution-mode Presentationless|Component
          --recipe-file <versioned-jsonc-recipe>
          --recipe deterministic-clear --fixture synthetic-clear (legacy control shortcut)
          --width N --height N --layers N --samples N --frame-slots N
          --color-format Rgba8 --depth-format DepthComponent32f
          --warmup-frames N --stability-frames N --capture-frames N
          --fixed-step seconds --random-seed N --frozen-world
          --scenario phase52-visibility|phase52-buffers|phase52-all
          --scenario phase53-streaming|phase53-materials|phase53-pipelines
          --scenario-cache-root <path> (required only for isolated pipeline cold/warm evidence)
          --scenario-depth normal|reversed|both --scenario-frames 12..240 --scenario-repeats 2..4
          --scenario-workload default|all|open-static|moderate-static|heavy-static|heavy-moving-cut|masked-static|masked-moving
          --scenario-timing (record delayed receipt-attributed Hi-Z GPU timestamp diagnostics)
          --scenario-lane eligibility|disabled|hiz|buffers|production|cold|warm (scenario-specific child)
          --scenario-renderdoc [--scenario-renderdoc-step N] (capture one child step; defaults to 0; requires injection)
          --mcp-policy Disabled|ReadOnly|Control --mcp-port N
          --session-token token --wait-for-mcp-start
          --session-name name --shutdown-event Local\\event-name
        """;

    private static string ReadValue(string[] args, ref int index, string argument)
        => ++index < args.Length
            ? args[index]
            : throw new ArgumentException($"Missing value after '{argument}'.");

    private static string Get(Dictionary<string, string?> values, string name, string fallback)
        => values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static int ParseInt(Dictionary<string, string?> values, string name, int fallback, int minimum, int maximum)
    {
        string text = Get(values, name, fallback.ToString(CultureInfo.InvariantCulture));
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, $"--{name} must be between {minimum} and {maximum}.");
        return value;
    }

    private static uint ParseUInt(Dictionary<string, string?> values, string name, uint fallback, uint minimum)
    {
        string text = Get(values, name, fallback.ToString(CultureInfo.InvariantCulture));
        if (!uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint value) || value < minimum)
            throw new ArgumentOutOfRangeException(name, $"--{name} must be at least {minimum}.");
        return value;
    }

    private static double ParseDouble(Dictionary<string, string?> values, string name, double fallback, double minimum)
    {
        string text = Get(values, name, fallback.ToString("R", CultureInfo.InvariantCulture));
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || value < minimum)
            throw new ArgumentOutOfRangeException(name, $"--{name} must be a finite value of at least {minimum}.");
        return value;
    }

    private static T ParseEnum<T>(string text, string description) where T : struct, Enum
        => Enum.TryParse(text.Replace("-", string.Empty), ignoreCase: true, out T value)
            ? value
            : throw new ArgumentException($"Unknown {description} '{text}'. Expected one of: {string.Join(", ", Enum.GetNames<T>())}.");

    private static RenderExecutionMode ParseExecutionMode(string text)
        => text.Replace("-", string.Empty).ToLowerInvariant() switch
        {
            "presentationless" => RenderExecutionMode.Presentationless,
            "component" => RenderExecutionMode.Component,
            "headlesswsi" => RenderExecutionMode.HeadlessWsi,
            "desktopwsi" => RenderExecutionMode.DesktopWsi,
            "openxr" => RenderExecutionMode.OpenXr,
            _ => throw new ArgumentException($"Unknown execution mode '{text}'."),
        };
}
