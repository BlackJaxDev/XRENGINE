using System.Diagnostics;
using System.Text;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using XREngine.Rendering.OpenGL;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

public sealed unsafe class AsyncShaderPipelineFrameBudgetHarness : IDisposable
{
    private const int FrameCount = 240;
    private const double EightMillisecondBudget = 8.3;
    private const double SixteenMillisecondBudget = 16.6;

    private const string ComplexFS = """
        #version 460 core
        layout(location = 0) out vec4 FragColor;
        layout(location = 0) in vec3 vNormal;
        layout(location = 1) in vec2 vTexCoord;

        vec3 cheapNoise(vec2 p) {
            float n = sin(dot(p, vec2(127.1, 311.7)));
            n = fract(n * 43758.5453123);
            float m = sin(dot(p * 1.13, vec2(269.5, 183.3)));
            m = fract(m * 43758.5453123);
            return vec3(n, m, (n + m) * 0.5);
        }

        void main() {
            vec3 N = normalize(vNormal);
            vec3 L = normalize(vec3(0.5, 1.0, 0.3));
            float NdotL = max(dot(N, L), 0.0);

            vec3 diffuse = vec3(0.0);
            for (int i = 0; i < 8; i++) {
                vec2 offset = vec2(float(i) * 0.123, float(i) * 0.456);
                diffuse += cheapNoise(vTexCoord + offset) * NdotL;
            }
            diffuse /= 8.0;

            vec3 ambient = vec3(0.03);
            FragColor = vec4(ambient + diffuse, 1.0);
        }
        """;

    private const string ComplexVS = """
        #version 460 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aTexCoord;
        layout(location = 0) out vec3 vNormal;
        layout(location = 1) out vec2 vTexCoord;
        void main() {
            gl_Position = vec4(aPos, 1.0);
            vNormal = aNormal;
            vTexCoord = aTexCoord;
        }
        """;

    private static readonly GLProgramCompileLinkQueue.ShaderInput[] ComplexInputs =
    [
        new(ComplexVS, ShaderType.VertexShader),
        new(ComplexFS, ShaderType.FragmentShader),
    ];

    private readonly List<ScenarioResult> _results = [];
    private readonly string _outputPath;

    private IWindow _primaryWindow = null!;
    private GL _gl = null!;
    private IWindow _sharedWindow = null!;
    private GLSharedContext _sharedContext = null!;
    private GLProgramBinaryUploadQueue _uploadQueue = null!;
    private GLProgramCompileLinkQueue _compileQueue = null!;
    private byte[] _complexBinary = null!;
    private GLEnum _complexBinaryFormat;
    private uint _complexBinaryLength;

    public AsyncShaderPipelineFrameBudgetHarness(string outputPath)
    {
        _outputPath = outputPath;
        Initialize();
    }

    public static int Run(string[] args)
    {
        string outputPath = GetOutputPath(args);
        using var harness = new AsyncShaderPipelineFrameBudgetHarness(outputPath);
        harness.RunAll();
        return 0;
    }

    public void Dispose()
    {
        _sharedContext.Dispose();
        try { _sharedWindow.Close(); } catch { }
        try { _sharedWindow.Dispose(); } catch { }
        try { _primaryWindow.Close(); } catch { }
        try { _primaryWindow.Dispose(); } catch { }
    }

    private void Initialize()
    {
        var opts = WindowOptions.Default;
        opts.Size = new Vector2D<int>(64, 64);
        opts.IsVisible = false;
        opts.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));

        Window.PrioritizeGlfw();
        _primaryWindow = Window.Create(opts);
        _primaryWindow.Initialize();
        _primaryWindow.MakeCurrent();
        _gl = GL.GetApi(_primaryWindow);

        var sharedOpts = WindowOptions.Default;
        sharedOpts.Size = new Vector2D<int>(1, 1);
        sharedOpts.IsVisible = false;
        sharedOpts.API = _primaryWindow.API;
        sharedOpts.SharedContext = _primaryWindow.GLContext;
        sharedOpts.ShouldSwapAutomatically = false;

        Window.PrioritizeGlfw();
        _sharedWindow = Window.Create(sharedOpts);
        _sharedWindow.Initialize();
        _primaryWindow.MakeCurrent();

        _sharedContext = new GLSharedContext();
        if (!_sharedContext.Initialize(_sharedWindow))
            throw new InvalidOperationException("Failed to start shared GL context background thread.");

        _uploadQueue = new GLProgramBinaryUploadQueue(_sharedContext);
        _compileQueue = new GLProgramCompileLinkQueue(_sharedContext);
        (_complexBinary, _complexBinaryFormat, _complexBinaryLength) = ExtractBinary(ComplexInputs);
    }

    private void RunAll()
    {
        _results.Add(RunScenario("IdleFrameLoop", DoIdleFrame));
        _results.Add(RunScenario("SyncCompileLink_Complex_x4", DoSyncCompileFrame));
        _results.Add(RunScenario("AsyncCompileLink_Complex_x4", DoAsyncCompileFrame));
        _results.Add(RunScenario("SyncBinaryUpload_Complex_x8", DoSyncUploadFrame));
        _results.Add(RunScenario("AsyncBinaryUpload_Complex_x8", DoAsyncUploadFrame));

        string report = BuildMarkdownReport();
        Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
        File.WriteAllText(_outputPath, report);
        Console.WriteLine(report);
        Console.WriteLine($"Report written to {_outputPath}");
    }

    private ScenarioResult RunScenario(string name, Func<int, ScenarioState, bool> frameAction)
    {
        var state = new ScenarioState();
        var frameTimesMs = new double[FrameCount];

        for (int frameIndex = 0; frameIndex < FrameCount; frameIndex++)
        {
            long start = Stopwatch.GetTimestamp();
            bool completed = frameAction(frameIndex, state);
            SimulateFrameCpuWork();
            frameTimesMs[frameIndex] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            if (completed && state.CompletedFrame < 0)
                state.CompletedFrame = frameIndex;
        }

        DrainState(state);
        return BuildScenarioResult(name, frameTimesMs, state.CompletedFrame);
    }

    private bool DoIdleFrame(int frameIndex, ScenarioState state)
    {
        return frameIndex == 0;
    }

    private bool DoSyncCompileFrame(int frameIndex, ScenarioState state)
    {
        if (frameIndex != 0)
            return true;

        for (int i = 0; i < GLProgramCompileLinkQueue.MaxInFlight; i++)
            SyncCompileLink(ComplexInputs);

        return true;
    }

    private bool DoAsyncCompileFrame(int frameIndex, ScenarioState state)
    {
        if (frameIndex == 0)
        {
            for (int i = 0; i < GLProgramCompileLinkQueue.MaxInFlight; i++)
            {
                uint program = _gl.CreateProgram();
                state.ProgramIds.Add(program);
                _compileQueue.EnqueueCompileAndLink(program, ComplexInputs);
            }
        }

        for (int i = state.ProgramIds.Count - 1; i >= 0; i--)
        {
            uint program = state.ProgramIds[i];
            if (_compileQueue.TryGetResult(program, out _))
            {
                _gl.DeleteProgram(program);
                state.ProgramIds.RemoveAt(i);
            }
        }

        return state.ProgramIds.Count == 0;
    }

    private bool DoSyncUploadFrame(int frameIndex, ScenarioState state)
    {
        if (frameIndex != 0)
            return true;

        for (int i = 0; i < GLProgramBinaryUploadQueue.MaxInFlight; i++)
            SyncBinaryUpload(_complexBinary, _complexBinaryFormat, _complexBinaryLength);

        return true;
    }

    private bool DoAsyncUploadFrame(int frameIndex, ScenarioState state)
    {
        if (frameIndex == 0)
        {
            for (int i = 0; i < GLProgramBinaryUploadQueue.MaxInFlight; i++)
            {
                uint program = _gl.CreateProgram();
                state.ProgramIds.Add(program);
                _uploadQueue.EnqueueUpload(program, _complexBinary, _complexBinaryFormat, _complexBinaryLength, (ulong)i);
            }
        }

        for (int i = state.ProgramIds.Count - 1; i >= 0; i--)
        {
            uint program = state.ProgramIds[i];
            if (_uploadQueue.TryGetResult(program, out _))
            {
                _gl.DeleteProgram(program);
                state.ProgramIds.RemoveAt(i);
            }
        }

        return state.ProgramIds.Count == 0;
    }

    private void DrainState(ScenarioState state)
    {
        while (state.ProgramIds.Count > 0)
        {
            for (int i = state.ProgramIds.Count - 1; i >= 0; i--)
            {
                uint program = state.ProgramIds[i];
                bool completed = _compileQueue.TryGetResult(program, out _) || _uploadQueue.TryGetResult(program, out _);
                if (!completed)
                    continue;

                _gl.DeleteProgram(program);
                state.ProgramIds.RemoveAt(i);
            }

            if (state.ProgramIds.Count > 0)
                Thread.Sleep(1);
        }
    }

    private static void SimulateFrameCpuWork()
    {
        Thread.SpinWait(25_000);
    }

    private ScenarioResult BuildScenarioResult(string name, double[] frameTimesMs, int completedFrame)
    {
        double[] ordered = [.. frameTimesMs.OrderBy(value => value)];
        double max = frameTimesMs.Max();
        double average = frameTimesMs.Average();
        double p95 = Percentile(ordered, 0.95);
        int over8 = frameTimesMs.Count(value => value > EightMillisecondBudget);
        int over16 = frameTimesMs.Count(value => value > SixteenMillisecondBudget);
        return new ScenarioResult(name, average, p95, max, over8, over16, completedFrame);
    }

    private string BuildMarkdownReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Async Shader Pipeline Frame Budget Report");
        builder.AppendLine();
        builder.AppendLine($"Frames per scenario: {FrameCount}");
        builder.AppendLine($"Frame budgets: >{EightMillisecondBudget:F1} ms and >{SixteenMillisecondBudget:F1} ms");
        builder.AppendLine();
        builder.AppendLine("| Scenario | Avg Frame ms | P95 ms | Max Frame ms | Frames > 8.3 ms | Frames > 16.6 ms | Completed Frame |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|");

        foreach (ScenarioResult result in _results)
        {
            builder.AppendLine($"| {result.Name} | {result.AverageFrameMs:F3} | {result.P95FrameMs:F3} | {result.MaxFrameMs:F3} | {result.FramesOverEightMs} | {result.FramesOverSixteenMs} | {result.CompletedFrame} |");
        }

        return builder.ToString();
    }

    private (byte[] binary, GLEnum format, uint length) ExtractBinary(GLProgramCompileLinkQueue.ShaderInput[] inputs)
    {
        uint program = _gl.CreateProgram();
        Span<uint> shaders = stackalloc uint[inputs.Length];

        for (int i = 0; i < inputs.Length; i++)
        {
            shaders[i] = CompileShader(inputs[i].Type, inputs[i].ResolvedSource);
            _gl.AttachShader(program, shaders[i]);
        }

        _gl.LinkProgram(program);

        for (int i = 0; i < shaders.Length; i++)
        {
            _gl.DetachShader(program, shaders[i]);
            _gl.DeleteShader(shaders[i]);
        }

        _gl.GetProgram(program, GLEnum.ProgramBinaryLength, out int len);
        if (len <= 0)
            throw new InvalidOperationException("Driver does not support program binary retrieval.");

        byte[] binary = new byte[len];
        uint binaryLength;
        GLEnum format;
        fixed (byte* ptr = binary)
            _gl.GetProgramBinary(program, (uint)len, &binaryLength, &format, ptr);

        _gl.DeleteProgram(program);
        return (binary, format, binaryLength);
    }

    private int SyncCompileLink(GLProgramCompileLinkQueue.ShaderInput[] inputs)
    {
        uint program = _gl.CreateProgram();
        Span<uint> shaders = stackalloc uint[inputs.Length];

        for (int i = 0; i < inputs.Length; i++)
        {
            shaders[i] = CompileShader(inputs[i].Type, inputs[i].ResolvedSource);
            _gl.AttachShader(program, shaders[i]);
        }

        _gl.LinkProgram(program);

        for (int i = 0; i < shaders.Length; i++)
        {
            _gl.DetachShader(program, shaders[i]);
            _gl.DeleteShader(shaders[i]);
        }

        _gl.Finish();
        _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int status);
        _gl.DeleteProgram(program);
        return status;
    }

    private int SyncBinaryUpload(byte[] binary, GLEnum format, uint length)
    {
        uint program = _gl.CreateProgram();
        fixed (byte* ptr = binary)
            _gl.ProgramBinary(program, format, ptr, length);

        _gl.Finish();
        _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int status);
        _gl.DeleteProgram(program);
        return status;
    }

    private uint CompileShader(ShaderType type, string source)
    {
        uint shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int status);
        if (status == 0)
        {
            string log = _gl.GetShaderInfoLog(shader);
            _gl.DeleteShader(shader);
            throw new InvalidOperationException($"{type} compilation failed: {log}");
        }

        return shader;
    }

    private static string GetOutputPath(string[] args)
    {
        int outputIndex = Array.IndexOf(args, "--frame-budget-output");
        if (outputIndex >= 0 && outputIndex + 1 < args.Length)
            return Path.GetFullPath(args[outputIndex + 1]);

        return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BenchmarkDotNet.Artifacts", "results", "AsyncShaderPipelineFrameBudget-report.md");
    }

    private static double Percentile(double[] orderedValues, double percentile)
    {
        if (orderedValues.Length == 0)
            return 0.0;

        double position = (orderedValues.Length - 1) * percentile;
        int lowerIndex = (int)Math.Floor(position);
        int upperIndex = (int)Math.Ceiling(position);
        if (lowerIndex == upperIndex)
            return orderedValues[lowerIndex];

        double weight = position - lowerIndex;
        return orderedValues[lowerIndex] + ((orderedValues[upperIndex] - orderedValues[lowerIndex]) * weight);
    }

    private sealed class ScenarioState
    {
        public List<uint> ProgramIds { get; } = [];
        public int CompletedFrame { get; set; } = -1;
    }

    private readonly record struct ScenarioResult(
        string Name,
        double AverageFrameMs,
        double P95FrameMs,
        double MaxFrameMs,
        int FramesOverEightMs,
        int FramesOverSixteenMs,
        int CompletedFrame);
}
