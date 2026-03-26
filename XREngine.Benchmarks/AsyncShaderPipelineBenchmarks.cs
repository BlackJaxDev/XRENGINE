using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using XREngine.Rendering.OpenGL;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

/// <summary>
/// Measures async shader pipeline behavior in two ways:
/// 1) main-thread submission cost, which is the closest proxy for stutter risk,
/// 2) end-to-end completion latency, which captures total throughput.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[Config(typeof(InProcessShortRunConfig))]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public unsafe class AsyncShaderPipelineBenchmarks
{
    private const string TrivialVS = """
        #version 460 core
        layout(location = 0) in vec3 aPos;
        void main() { gl_Position = vec4(aPos, 1.0); }
        """;

    private const string TrivialFS = """
        #version 460 core
        out vec4 FragColor;
        void main() { FragColor = vec4(1.0, 0.0, 0.0, 1.0); }
        """;

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

    private static readonly GLProgramCompileLinkQueue.ShaderInput[] TrivialInputs =
    [
        new(TrivialVS, ShaderType.VertexShader),
        new(TrivialFS, ShaderType.FragmentShader),
    ];

    private static readonly GLProgramCompileLinkQueue.ShaderInput[] ComplexInputs =
    [
        new(ComplexVS, ShaderType.VertexShader),
        new(ComplexFS, ShaderType.FragmentShader),
    ];

    private IWindow _primaryWindow = null!;
    private GL _gl = null!;
    private IWindow _sharedWindow = null!;
    private GLSharedContext _sharedContext = null!;
    private GLProgramBinaryUploadQueue _uploadQueue = null!;
    private GLProgramCompileLinkQueue _compileQueue = null!;

    private byte[] _trivialBinary = null!;
    private GLEnum _trivialBinaryFormat;
    private uint _trivialBinaryLength;

    private byte[] _complexBinary = null!;
    private GLEnum _complexBinaryFormat;
    private uint _complexBinaryLength;

    private readonly uint[] _pendingProgramIds = new uint[GLProgramBinaryUploadQueue.MaxInFlight];
    private int _pendingProgramCount;
    private PendingKind _pendingKind;

    [Params(1, 4)]
    public int BatchSize { get; set; }

    [GlobalSetup]
    public void Setup()
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

        (_trivialBinary, _trivialBinaryFormat, _trivialBinaryLength) = ExtractBinary(TrivialInputs);
        (_complexBinary, _complexBinaryFormat, _complexBinaryLength) = ExtractBinary(ComplexInputs);
    }

    [IterationCleanup]
    public void CleanupPendingAsyncWork()
    {
        if (_pendingProgramCount == 0 || _pendingKind == PendingKind.None)
            return;

        switch (_pendingKind)
        {
            case PendingKind.Compile:
                WaitForCompileQueue(_pendingProgramIds.AsSpan(0, _pendingProgramCount));
                break;
            case PendingKind.Upload:
                WaitForUploadQueue(_pendingProgramIds.AsSpan(0, _pendingProgramCount));
                break;
        }

        ClearPendingPrograms();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        CleanupPendingAsyncWork();
        _sharedContext.Dispose();
        try { _sharedWindow.Close(); } catch { }
        try { _sharedWindow.Dispose(); } catch { }
        try { _primaryWindow.Close(); } catch { }
        try { _primaryWindow.Dispose(); } catch { }
    }

    [BenchmarkCategory("MainThreadCost")]
    [Benchmark]
    public int SyncCompileLink_MainThreadCost_Complex()
    {
        return SyncCompileLinkBatch(ComplexInputs, BatchSize);
    }

    [BenchmarkCategory("MainThreadCost")]
    [Benchmark]
    public int AsyncCompileLink_SubmitCost_Complex()
    {
        return EnqueueCompileLinkBatch(ComplexInputs, BatchSize);
    }

    [BenchmarkCategory("MainThreadCost")]
    [Benchmark]
    public int SyncBinaryUpload_MainThreadCost_Complex()
    {
        return SyncBinaryUploadBatch(_complexBinary, _complexBinaryFormat, _complexBinaryLength, BatchSize);
    }

    [BenchmarkCategory("MainThreadCost")]
    [Benchmark]
    public int AsyncBinaryUpload_SubmitCost_Complex()
    {
        return EnqueueBinaryUploadBatch(_complexBinary, _complexBinaryFormat, _complexBinaryLength, BatchSize);
    }

    [BenchmarkCategory("MainThreadCost", "Saturation")]
    [Benchmark]
    public int SyncBinaryUpload_MainThreadCost_Complex_Saturated8()
    {
        return SyncBinaryUploadBatch(_complexBinary, _complexBinaryFormat, _complexBinaryLength, GLProgramBinaryUploadQueue.MaxInFlight);
    }

    [BenchmarkCategory("MainThreadCost", "Saturation")]
    [Benchmark]
    public int AsyncBinaryUpload_SubmitCost_Complex_Saturated8()
    {
        return EnqueueBinaryUploadBatch(_complexBinary, _complexBinaryFormat, _complexBinaryLength, GLProgramBinaryUploadQueue.MaxInFlight);
    }

    [BenchmarkCategory("Completion")]
    [Benchmark]
    public int SyncCompileLink_Completion_Trivial()
    {
        return SyncCompileLinkBatch(TrivialInputs, BatchSize);
    }

    [BenchmarkCategory("Completion")]
    [Benchmark]
    public int AsyncCompileLink_Completion_Trivial()
    {
        return CompleteAsyncCompileLinkBatch(TrivialInputs, BatchSize);
    }

    [BenchmarkCategory("Completion")]
    [Benchmark]
    public int SyncCompileLink_Completion_Complex()
    {
        return SyncCompileLinkBatch(ComplexInputs, BatchSize);
    }

    [BenchmarkCategory("Completion")]
    [Benchmark]
    public int AsyncCompileLink_Completion_Complex()
    {
        return CompleteAsyncCompileLinkBatch(ComplexInputs, BatchSize);
    }

    [BenchmarkCategory("Completion")]
    [Benchmark]
    public int SyncBinaryUpload_Completion_Trivial()
    {
        return SyncBinaryUploadBatch(_trivialBinary, _trivialBinaryFormat, _trivialBinaryLength, BatchSize);
    }

    [BenchmarkCategory("Completion")]
    [Benchmark]
    public int AsyncBinaryUpload_Completion_Trivial()
    {
        return CompleteAsyncBinaryUploadBatch(_trivialBinary, _trivialBinaryFormat, _trivialBinaryLength, BatchSize);
    }

    [BenchmarkCategory("Completion")]
    [Benchmark]
    public int SyncBinaryUpload_Completion_Complex()
    {
        return SyncBinaryUploadBatch(_complexBinary, _complexBinaryFormat, _complexBinaryLength, BatchSize);
    }

    [BenchmarkCategory("Completion")]
    [Benchmark]
    public int AsyncBinaryUpload_Completion_Complex()
    {
        return CompleteAsyncBinaryUploadBatch(_complexBinary, _complexBinaryFormat, _complexBinaryLength, BatchSize);
    }

    [BenchmarkCategory("Completion", "Saturation")]
    [Benchmark]
    public int SyncBinaryUpload_Completion_Complex_Saturated8()
    {
        return SyncBinaryUploadBatch(_complexBinary, _complexBinaryFormat, _complexBinaryLength, GLProgramBinaryUploadQueue.MaxInFlight);
    }

    [BenchmarkCategory("Completion", "Saturation")]
    [Benchmark]
    public int AsyncBinaryUpload_Completion_Complex_Saturated8()
    {
        return CompleteAsyncBinaryUploadBatch(_complexBinary, _complexBinaryFormat, _complexBinaryLength, GLProgramBinaryUploadQueue.MaxInFlight);
    }

    private int SyncCompileLinkBatch(GLProgramCompileLinkQueue.ShaderInput[] inputs, int count)
    {
        int linked = 0;
        for (int i = 0; i < count; i++)
            linked += SyncCompileLink(inputs);
        return linked;
    }

    private int SyncBinaryUploadBatch(byte[] binary, GLEnum format, uint length, int count)
    {
        int linked = 0;
        for (int i = 0; i < count; i++)
            linked += SyncBinaryUpload(binary, format, length);
        return linked;
    }

    private int EnqueueCompileLinkBatch(GLProgramCompileLinkQueue.ShaderInput[] inputs, int count)
    {
        BeginPendingPrograms(PendingKind.Compile);

        for (int i = 0; i < count; i++)
        {
            uint program = _gl.CreateProgram();
            _pendingProgramIds[_pendingProgramCount++] = program;
            _compileQueue.EnqueueCompileAndLink(program, inputs);
        }

        return _pendingProgramCount;
    }

    private int EnqueueBinaryUploadBatch(byte[] binary, GLEnum format, uint length, int count)
    {
        BeginPendingPrograms(PendingKind.Upload);

        for (int i = 0; i < count; i++)
        {
            uint program = _gl.CreateProgram();
            _pendingProgramIds[_pendingProgramCount++] = program;
            _uploadQueue.EnqueueUpload(program, binary, format, length, (ulong)i);
        }

        return _pendingProgramCount;
    }

    private int CompleteAsyncCompileLinkBatch(GLProgramCompileLinkQueue.ShaderInput[] inputs, int count)
    {
        Span<uint> programs = stackalloc uint[count];
        for (int i = 0; i < count; i++)
        {
            programs[i] = _gl.CreateProgram();
            _compileQueue.EnqueueCompileAndLink(programs[i], inputs);
        }

        return WaitForCompileQueue(programs);
    }

    private int CompleteAsyncBinaryUploadBatch(byte[] binary, GLEnum format, uint length, int count)
    {
        Span<uint> programs = stackalloc uint[count];
        for (int i = 0; i < count; i++)
        {
            programs[i] = _gl.CreateProgram();
            _uploadQueue.EnqueueUpload(programs[i], binary, format, length, (ulong)i);
        }

        return WaitForUploadQueue(programs);
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

    private int WaitForCompileQueue(Span<uint> programs)
    {
        int successCount = 0;
        int remaining = programs.Length;
        while (remaining > 0)
        {
            for (int i = 0; i < programs.Length; i++)
            {
                if (programs[i] == 0)
                    continue;

                if (_compileQueue.TryGetResult(programs[i], out var result))
                {
                    if (result.Status == GLProgramCompileLinkQueue.CompileStatus.Success)
                        successCount++;

                    _gl.DeleteProgram(programs[i]);
                    programs[i] = 0;
                    remaining--;
                }
            }

            if (remaining > 0)
                Thread.SpinWait(16);
        }

        return successCount;
    }

    private int WaitForUploadQueue(Span<uint> programs)
    {
        int successCount = 0;
        int remaining = programs.Length;
        while (remaining > 0)
        {
            for (int i = 0; i < programs.Length; i++)
            {
                if (programs[i] == 0)
                    continue;

                if (_uploadQueue.TryGetResult(programs[i], out var result))
                {
                    if (result.Status == GLProgramBinaryUploadQueue.UploadStatus.Success)
                        successCount++;

                    _gl.DeleteProgram(programs[i]);
                    programs[i] = 0;
                    remaining--;
                }
            }

            if (remaining > 0)
                Thread.SpinWait(16);
        }

        return successCount;
    }

    private void BeginPendingPrograms(PendingKind kind)
    {
        CleanupPendingAsyncWork();
        _pendingKind = kind;
        _pendingProgramCount = 0;
    }

    private void ClearPendingPrograms()
    {
        Array.Clear(_pendingProgramIds, 0, _pendingProgramCount);
        _pendingProgramCount = 0;
        _pendingKind = PendingKind.None;
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
            _gl.DeleteShader(shaders[i]);

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

    private enum PendingKind
    {
        None,
        Compile,
        Upload,
    }
}
