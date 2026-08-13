using System.Globalization;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.RenderBench;

public sealed record RenderBenchOptions
{
    private static readonly HashSet<string> s_allowedArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        "backend", "execution-mode", "recipe", "fixture", "output-dir", "mcp-policy", "mcp-port",
        "session-name", "session-token", "shutdown-event", "wait-for-mcp-start", "width", "height",
        "layers", "frame-slots", "samples", "color-format", "depth-format", "warmup-frames",
        "stability-frames", "capture-frames", "fixed-step", "random-seed", "frozen-world", "help",
    };

    public string Backend { get; init; } = "Vulkan";
    public RenderExecutionMode ExecutionMode { get; init; } = RenderExecutionMode.Presentationless;
    public string Recipe { get; init; } = "deterministic-clear";
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
            bool flag = key is "wait-for-mcp-start" or "frozen-world" or "help";
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
        };

        if (!result.Recipe.Equals("deterministic-clear", StringComparison.OrdinalIgnoreCase) ||
            !result.Fixture.Equals("synthetic-clear", StringComparison.OrdinalIgnoreCase))
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
          --recipe deterministic-clear --fixture synthetic-clear
          --width N --height N --layers N --samples N --frame-slots N
          --color-format Rgba8 --depth-format DepthComponent32f
          --warmup-frames N --stability-frames N --capture-frames N
          --fixed-step seconds --random-seed N --frozen-world
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
