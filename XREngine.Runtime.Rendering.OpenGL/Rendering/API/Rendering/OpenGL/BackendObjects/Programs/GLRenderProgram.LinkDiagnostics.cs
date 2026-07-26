using XREngine.Extensions;
using Silk.NET.OpenGL;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using XREngine;
using XREngine.Data.Profiling;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders;
using static XREngine.Rendering.XRRenderProgram;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public partial class GLRenderProgram
        {
            private bool TryResolveUberVariantHash(out ulong variantHash)
            {
                if (_preparedCompileInputs is { Length: > 0 } preparedInputs && TryResolveUberVariantHash(preparedInputs, out variantHash))
                    return true;

                foreach (GLShader shader in _shaderCache.Values)
                {
                    if (UberShaderVariantTelemetry.TryParseVariantHash(shader.Data.Source?.Text, out variantHash))
                        return true;
                }

                variantHash = 0;
                return false;
            }

            private bool ShouldBypassBinaryCacheForLiveUberVariant()
                => Renderer.UseDriverParallelShaderCompile &&
                   !IsKnownAsyncLinkHazard &&
                   TryResolveUberVariantHash(out _);

            /// <summary>
            /// Programs known to hang or stall NVIDIA's
            /// <c>GL_ARB_parallel_shader_compile</c> link worker on the main
            /// context. Covers:
            ///  * Single-stage separable programs (imported model materials whose
            ///    vertex/fragment stages are split into individual programs).
            ///  * Compute programs (always single-stage; NVIDIA's parallel-link
            ///    worker can leave the program waiting forever, and the first
            ///    <c>glUseProgram</c>/<c>glDispatchCompute</c> implicitly waits for
            ///    completion which deadlocks the render thread â€” observed during
            ///    BVH/physics-chain dispatch in <c>GlobalPreRender</c>).
            ///  * Any program with a single attached shader, which exhibits the
            ///    same hazard regardless of the <c>Separable</c> flag.
            /// For these we always bypass the driver-parallel lane. Single-stage
            /// graphics programs are still routed to the shared-context source
            /// lane (when available) so their cold link runs on a worker thread
            /// on a separate GL context instead of stalling the render thread.
            /// Compute programs are denied the shared-context lane as well â€” the
            /// queue's <c>ContainsKnownAsyncLinkHazard</c> filter still rejects
            /// them â€” and fall back to the guarded synchronous path which
            /// temporarily disables driver compiler threads, links inline on the
            /// render thread under the per-frame shader-work budget, and leaves
            /// any previously linked hot-reload program visible.
            /// </summary>
            private bool IsKnownAsyncLinkHazard
            {
                get
                {
                    if (_shaderCache.Count <= 1)
                        return true;
                    foreach (GLShader shader in _shaderCache.Values)
                    {
                        if (shader.Data.Type == EShaderType.Compute)
                            return true;
                    }
                    return false;
                }
            }

            private static bool TryResolveUberVariantHash(IEnumerable<GLProgramCompileLinkQueue.ShaderInput> inputs, out ulong variantHash)
            {
                foreach (GLProgramCompileLinkQueue.ShaderInput input in inputs)
                {
                    if (UberShaderVariantTelemetry.TryParseVariantHash(input.ResolvedSource, out variantHash))
                        return true;
                }

                variantHash = 0;
                return false;
            }

            private void BeginUberBackendCompileTracking(ulong variantHash)
            {
                if (variantHash == 0)
                    return;

                _uberVariantHash = variantHash;
                _uberCompileMilliseconds = 0.0;
                _uberCompileStartTimestamp = Stopwatch.GetTimestamp();
                _uberLinkStartTimestamp = 0;
                UberShaderVariantTelemetry.RecordBackendCompileStarted(variantHash);
            }

            private double CompleteUberBackendCompileTracking()
            {
                if (_uberVariantHash == 0 || _uberCompileStartTimestamp == 0)
                    return _uberCompileMilliseconds;

                _uberCompileMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - _uberCompileStartTimestamp);
                _uberCompileStartTimestamp = 0;
                return _uberCompileMilliseconds;
            }

            private void BeginUberBackendLinkTracking(double compileMilliseconds)
            {
                if (_uberVariantHash == 0)
                    return;

                _uberCompileMilliseconds = compileMilliseconds > 0.0 ? compileMilliseconds : _uberCompileMilliseconds;
                _uberLinkStartTimestamp = Stopwatch.GetTimestamp();
                UberShaderVariantTelemetry.RecordBackendLinkStarted(_uberVariantHash, _uberCompileMilliseconds);
            }

            private void CompleteUberBackendTracking(bool linked, string? failureReason = null, double? compileMilliseconds = null, double? linkMilliseconds = null)
            {
                if (_uberVariantHash == 0)
                    return;

                double resolvedCompileMilliseconds = compileMilliseconds ?? _uberCompileMilliseconds;
                double resolvedLinkMilliseconds = linkMilliseconds ??
                    (_uberLinkStartTimestamp == 0 ? 0.0 : StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - _uberLinkStartTimestamp));

                if (linked)
                    UberShaderVariantTelemetry.RecordBackendSuccess(_uberVariantHash, resolvedCompileMilliseconds, resolvedLinkMilliseconds);
                else
                    UberShaderVariantTelemetry.RecordBackendFailure(_uberVariantHash, failureReason, resolvedCompileMilliseconds, resolvedLinkMilliseconds);

                ResetUberBackendTracking();
            }

            private void ResetUberBackendTracking()
            {
                _uberVariantHash = 0;
                _uberCompileStartTimestamp = 0;
                _uberLinkStartTimestamp = 0;
                _uberCompileMilliseconds = 0.0;
            }

            private void PublishBackendStatus(
                EShaderProgramBackendStage stage,
                string? backend,
                string? detail = null,
                string? failureReason = null,
                double compileMilliseconds = 0.0,
                double linkMilliseconds = 0.0,
                string? fingerprint = null)
            {
                Data.SetShaderBackendStatus(new ShaderProgramBackendStatus(
                    stage,
                    compileMilliseconds,
                    linkMilliseconds,
                    failureReason,
                    backend,
                    detail,
                    fingerprint ?? _activeBuildFingerprint));
            }

            private void BeginBuildTelemetry(string backend, string? fingerprint)
            {
                _activeBuildBackend = backend;
                _activeBuildFingerprint = fingerprint;
                _activeBuildQueueTimestamp = Stopwatch.GetTimestamp();
            }

            private string GetProgramDebugName()
            {
                if (!string.IsNullOrWhiteSpace(Data.Name))
                    return Data.Name!;

                string stageTopology = GetShaderStageTopology();
                ShaderProgramVariantMetadata variant = Data.ShaderMetadata.Variant;
                string variantSegment = variant.HasVariant
                    ? string.Concat(
                        string.IsNullOrWhiteSpace(variant.Kind) ? "variant" : variant.Kind,
                        ":",
                        variant.VariantHash.ToString("x16", CultureInfo.InvariantCulture))
                    : "no-variant";

                return string.IsNullOrWhiteSpace(stageTopology)
                    ? string.Concat("<unnamed ", variantSegment, " hash=", Hash.ToString(CultureInfo.InvariantCulture), ">")
                    : string.Concat("<unnamed ", stageTopology, " ", variantSegment, " hash=", Hash.ToString(CultureInfo.InvariantCulture), ">");
            }

            private string GetProgramDescriptorLogKey()
                => string.IsNullOrWhiteSpace(Data.ProgramDescriptor.StableKey)
                    ? "<none>"
                    : Data.ProgramDescriptor.StableKey;

            private string GetCurrentHandleSourceLabel()
            {
                if (_sharedLinkedProgram is not null)
                    return "shared";
                if (_cachedProgram is not null || _preparedIsCached)
                    return "binary";
                return IsLinked ? "source" : "none";
            }

            private void CompleteBuildTelemetry(
                bool success,
                double compileMilliseconds = 0.0,
                double linkMilliseconds = 0.0,
                double binaryLoadMilliseconds = 0.0,
                double reflectionMilliseconds = 0.0,
                string? failureReason = null)
            {
                double queueLatencyMilliseconds = _activeBuildQueueTimestamp == 0
                    ? 0.0
                    : StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - _activeBuildQueueTimestamp);

                Data.SetShaderBuildTelemetry(new ShaderProgramBuildTelemetry(
                    GetProgramDebugName(),
                    _activeBuildFingerprint,
                    GetShaderStageTopology(),
                    Data.Separable,
                    _activeBuildBackend,
                    queueLatencyMilliseconds,
                    compileMilliseconds,
                    linkMilliseconds,
                    binaryLoadMilliseconds,
                    reflectionMilliseconds,
                    failureReason));

                string result = success ? "READY" : "FAILED";
                string programDebugName = GetProgramDebugName();
                string descriptorKey = GetProgramDescriptorLogKey();
                Debug.OpenGL(
                    $"[ShaderBackend] {result} program='{programDebugName}' hash={Hash} " +
                    $"backend={_activeBuildBackend ?? "<unknown>"} fingerprint={_activeBuildFingerprint ?? "<none>"} " +
                    $"descriptor={descriptorKey} handleSource={GetCurrentHandleSourceLabel()} " +
                    $"queueMs={queueLatencyMilliseconds:F2} compileMs={compileMilliseconds:F2} linkMs={linkMilliseconds:F2} " +
                    $"binaryMs={binaryLoadMilliseconds:F2} reflectionMs={reflectionMilliseconds:F2}" +
                    (string.IsNullOrWhiteSpace(failureReason) ? "." : $" failure='{failureReason}'."));
                LogSlowLinkShaderSources(linkMilliseconds, result, failureReason);
                if (ShouldLogRenderingShaderLinkVerbose())
                {
                    ShaderProgramSourceSummary sourceSummary = CollectShaderProgramSourceSummary(_preparedCompileInputs);
                    Debug.Rendering(
                        EOutputVerbosity.Verbose,
                        false,
                        "[ShaderBackend] {0} program='{1}' hash={2} backend={3} fingerprint={4} descriptor={5} separable={6} hazard={7} shaderCount={8} shaderTypes={9} sourceBytes={10} sourceLines={11} shaderSources={12} queueMs={13:F2} compileMs={14:F2} linkMs={15:F2} binaryMs={16:F2} reflectionMs={17:F2}{18}.",
                        result,
                        programDebugName,
                        Hash,
                        _activeBuildBackend ?? "<unknown>",
                        _activeBuildFingerprint ?? "<none>",
                        descriptorKey,
                        Data.Separable,
                        IsKnownAsyncLinkHazard,
                        sourceSummary.ShaderCount,
                        sourceSummary.StageList,
                        sourceSummary.SourceBytes,
                        sourceSummary.SourceLines,
                        sourceSummary.SourceLabels,
                        queueLatencyMilliseconds,
                        compileMilliseconds,
                        linkMilliseconds,
                        binaryLoadMilliseconds,
                        reflectionMilliseconds,
                        FormatRenderingDetail(string.IsNullOrWhiteSpace(failureReason) ? null : $"failure={failureReason}"));
                }

                _activeBuildBackend = null;
                _activeBuildFingerprint = null;
                _activeBuildQueueTimestamp = 0;
            }

            private void LogSlowLinkShaderSources(double linkMilliseconds, string result, string? failureReason)
            {
                if (linkMilliseconds <= SlowShaderLinkSourceDumpMilliseconds)
                    return;

                GLProgramCompileLinkQueue.ShaderInput[]? inputs = _preparedCompileInputs;
                string programName = Data.Name ?? "<unnamed>";
                string backend = _activeBuildBackend ?? "<unknown>";
                string fingerprint = _activeBuildFingerprint ?? "<none>";
                int shaderCount = inputs is { Length: > 0 } ? inputs.Length : Data.Shaders.Count;
                ShaderProgramSourceSummary sourceSummary = CollectShaderProgramSourceSummary(inputs);
                Debug.OpenGL(
                    $"[ShaderSlowLink] thresholdMs={SlowShaderLinkSourceDumpMilliseconds:F2} linkMs={linkMilliseconds:F2} " +
                    $"result={result} program='{programName}' hash={Hash} backend={backend} fingerprint={fingerprint} " +
                    $"separable={Data.Separable} hazard={IsKnownAsyncLinkHazard} shaderCount={shaderCount} " +
                    $"shaderTypes={sourceSummary.StageList} sourceBytes={sourceSummary.SourceBytes} sourceLines={sourceSummary.SourceLines} " +
                    $"sourceLabels={sourceSummary.SourceLabels} dumpSources={DumpSlowShaderSources}" +
                    (string.IsNullOrWhiteSpace(failureReason) ? "." : $" failure='{failureReason}'."));

                if (!DumpSlowShaderSources)
                    return;

                Debug.OpenGL(
                    $"[ShaderSourceDump] BEGIN reason=slow-link thresholdMs={SlowShaderLinkSourceDumpMilliseconds:F2} " +
                    $"linkMs={linkMilliseconds:F2} result={result} program='{programName}' hash={Hash} backend={backend} " +
                    $"fingerprint={fingerprint} separable={Data.Separable} hazard={IsKnownAsyncLinkHazard} shaderCount={shaderCount}" +
                    (string.IsNullOrWhiteSpace(failureReason) ? "." : $" failure='{failureReason}'."));

                if (inputs is { Length: > 0 })
                {
                    for (int i = 0; i < inputs.Length; i++)
                        LogSlowLinkShaderSource(i, inputs.Length, inputs[i].Type.ToString(), inputs[i].ResolvedSource);
                }
                else
                {
                    for (int i = 0; i < Data.Shaders.Count; i++)
                    {
                        XRShader shaderData = Data.Shaders[i];
                        string? source = ResolveShaderSourceForDump(shaderData);
                        LogSlowLinkShaderSource(i, Data.Shaders.Count, shaderData.Type.ToString(), source);
                    }
                }

                Debug.OpenGL($"[ShaderSourceDump] END reason=slow-link program='{programName}' hash={Hash}.");
            }

            private void LogSlowLinkShaderSource(int index, int count, string stage, string? source)
            {
                source ??= string.Empty;
                Debug.OpenGL(
                    $"[ShaderSourceDump] SOURCE_BEGIN index={index} count={count} stage={stage} " +
                    $"bytes={CountUtf8Bytes(source)} lines={CountLines(source)}{Environment.NewLine}" +
                    source +
                    $"{Environment.NewLine}[ShaderSourceDump] SOURCE_END index={index} stage={stage}.");
            }

            private string? ResolveShaderSourceForDump(XRShader shaderData)
            {
                if (_shaderCache.TryGetValue(shaderData, out GLShader? shader) && shader is not null)
                    return shader.ResolveFullSource();

                return shaderData.TryGetOptimizedSource(out string optimizedSource, logFailures: false)
                    ? GLShaderSourceCompatibility.InjectMissingGLPerVertexBlocks(optimizedSource, shaderData.Type, Data.Separable)
                    : null;
            }

            private readonly record struct ShaderProgramSourceSummary(
                int ShaderCount,
                long SourceBytes,
                int SourceLines,
                string StageList,
                string SourceLabels);

            private void LogRenderingProgramBuildEvent(
                string eventName,
                string? backend,
                string? detail = null,
                string? fingerprint = null,
                uint programId = 0,
                GLProgramCompileLinkQueue.ShaderInput[]? inputs = null,
                long binaryBytes = 0,
                string? binaryFormat = null)
            {
                if (!ShouldLogRenderingShaderLinkVerbose())
                    return;

                if (programId == 0 && !TryGetBuildBindingId(out programId))
                    programId = 0;

                ShaderProgramSourceSummary sourceSummary = CollectShaderProgramSourceSummary(inputs);
                Debug.Rendering(
                    EOutputVerbosity.Verbose,
                    false,
                    "[ShaderLink] {0} program='{1}' hash={2} descriptor={3} programId={4} backend={5} separable={6} hazard={7} shaderCount={8} shaderTypes={9} sourceBytes={10} sourceLines={11} shaderSources={12} binaryBytes={13} binaryFormat={14} fingerprint={15} frame={16} renderThread={17}{18}.",
                    eventName,
                    GetProgramDebugName(),
                    Hash,
                    GetProgramDescriptorLogKey(),
                    programId,
                    backend ?? "<unknown>",
                    Data.Separable,
                    IsKnownAsyncLinkHazard,
                    sourceSummary.ShaderCount,
                    sourceSummary.StageList,
                    sourceSummary.SourceBytes,
                    sourceSummary.SourceLines,
                    sourceSummary.SourceLabels,
                    binaryBytes,
                    binaryFormat ?? "<none>",
                    fingerprint ?? _activeBuildFingerprint ?? "<none>",
                    RuntimeEngine.Rendering.State.RenderFrameId,
                    RuntimeEngine.IsRenderThread,
                    FormatRenderingDetail(detail));
            }

            private ShaderProgramSourceSummary CollectShaderProgramSourceSummary(GLProgramCompileLinkQueue.ShaderInput[]? inputs = null)
            {
                if (inputs is { Length: > 0 })
                {
                    long inputBytes = 0;
                    int inputLines = 0;
                    var inputStages = new StringBuilder(inputs.Length * 16);
                    for (int i = 0; i < inputs.Length; i++)
                    {
                        if (i > 0)
                            inputStages.Append('|');

                        inputStages.Append(inputs[i].Type);
                        inputBytes += CountUtf8Bytes(inputs[i].ResolvedSource);
                        inputLines += CountLines(inputs[i].ResolvedSource);
                    }

                    return new ShaderProgramSourceSummary(inputs.Length, inputBytes, inputLines, inputStages.ToString(), "<prepared-inputs>");
                }

                int shaderCount = Data.Shaders.Count;
                if (shaderCount == 0)
                    return new ShaderProgramSourceSummary(0, 0, 0, "<none>", "<none>");

                long bytes = 0;
                int lines = 0;
                var stages = new StringBuilder(shaderCount * 16);
                var sourceLabels = new StringBuilder(shaderCount * 48);
                for (int index = 0; index < shaderCount; index++)
                {
                    if (index > 0)
                    {
                        stages.Append('|');
                        sourceLabels.Append('|');
                    }

                    XRShader shaderData = Data.Shaders[index];
                    stages.Append(shaderData.Type);
                    sourceLabels.Append(shaderData.Type)
                        .Append(':')
                        .Append(string.IsNullOrWhiteSpace(shaderData.Source?.FilePath)
                            ? "<inline>"
                            : shaderData.Source.FilePath);
                    string? source = null;
                    if (_shaderCache.TryGetValue(shaderData, out GLShader? shader) && shader is not null)
                    {
                        source = shader.ResolveFullSource();
                    }
                    else if (shaderData.TryGetOptimizedSource(out string optimizedSource, logFailures: false))
                    {
                        source = GLShaderSourceCompatibility.InjectMissingGLPerVertexBlocks(optimizedSource, shaderData.Type, Data.Separable);
                    }

                    bytes += CountUtf8Bytes(source);
                    lines += CountLines(source);
                }

                return new ShaderProgramSourceSummary(shaderCount, bytes, lines, stages.ToString(), sourceLabels.ToString());
            }

            private double MeasureRenderingProgramGlCall(string callName, uint programId, Action action, string? detail = null)
            {
                if (!ShouldLogRenderingShaderLinkVerbose())
                {
                    action();
                    return 0.0;
                }

                long startTimestamp = Stopwatch.GetTimestamp();
                action();
                double elapsedMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - startTimestamp);
                if (ShouldLogRenderingShaderGlCall(callName, detail, elapsedMilliseconds))
                    LogRenderingProgramGlCall(callName, programId, elapsedMilliseconds, detail);
                return elapsedMilliseconds;
            }

            private void LogRenderingProgramGlCall(string callName, uint programId, double elapsedMilliseconds, string? detail = null)
            {
                bool renderThread = RuntimeEngine.IsRenderThread;
                Debug.Rendering(
                    EOutputVerbosity.Verbose,
                    false,
                    "[ShaderGLCall] call={0} program='{1}' hash={2} programId={3} separable={4} elapsedMs={5:F3} renderThread={6} renderThreadStallMs={7:F3}{8}.",
                    callName,
                    GetProgramDebugName(),
                    Hash,
                    programId,
                    Data.Separable,
                    elapsedMilliseconds,
                    renderThread,
                    renderThread ? elapsedMilliseconds : 0.0,
                    FormatRenderingDetail(detail));
            }

            private static double MeasureRenderingProgramGlCallStatic(string callName, uint programId, Action action, string? detail = null)
            {
                if (!ShouldLogRenderingShaderLinkVerbose())
                {
                    action();
                    return 0.0;
                }

                long startTimestamp = Stopwatch.GetTimestamp();
                action();
                double elapsedMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - startTimestamp);
                if (!ShouldLogRenderingShaderGlCall(callName, detail, elapsedMilliseconds))
                    return elapsedMilliseconds;

                bool renderThread = RuntimeEngine.IsRenderThread;
                Debug.Rendering(
                    EOutputVerbosity.Verbose,
                    false,
                    "[ShaderGLCall] call={0} program='<unknown>' hash=0 programId={1} separable=<unknown> elapsedMs={2:F3} renderThread={3} renderThreadStallMs={4:F3}{5}.",
                    callName,
                    programId,
                    elapsedMilliseconds,
                    renderThread,
                    renderThread ? elapsedMilliseconds : 0.0,
                    FormatRenderingDetail(detail));
                return elapsedMilliseconds;
            }

            private static bool ShouldLogRenderingShaderLinkVerbose()
                => Debug.AllowOutput && RuntimeDebugHostServices.Current.OutputVerbosity >= EOutputVerbosity.Verbose;

            private static bool ShouldLogRenderingShaderGlCall(string callName, string? detail, double elapsedMilliseconds)
            {
                if (!IsShaderCompletionPollGlCall(callName, detail))
                    return true;

                return TraceShaderCompletionPollGlCalls ||
                       elapsedMilliseconds >= ShaderCompletionPollGlCallSlowLogMilliseconds;
            }

            private static bool IsShaderCompletionPollGlCall(string callName, string? detail)
                => callName.Contains("GL_COMPLETION_STATUS", StringComparison.OrdinalIgnoreCase) ||
                   (detail?.Contains("completion-poll", StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (detail?.Contains("deferred-cleanup", StringComparison.OrdinalIgnoreCase) ?? false);

            private static string FormatRenderingDetail(string? detail)
                => string.IsNullOrWhiteSpace(detail) ? string.Empty : $" detail='{detail.Replace('\'', '"')}'";

            private static int CountUtf8Bytes(string? source)
                => string.IsNullOrEmpty(source) ? 0 : Encoding.UTF8.GetByteCount(source);

            private static int CountLines(string? source)
            {
                if (string.IsNullOrEmpty(source))
                    return 0;

                int lines = 1;
                for (int i = 0; i < source.Length; i++)
                {
                    if (source[i] == '\n')
                        lines++;
                }
                return lines;
            }

            private static double StopwatchTicksToMilliseconds(long ticks)
                => ticks <= 0L ? 0.0 : ticks * 1000.0 / Stopwatch.Frequency;

            private static double StopwatchTicksToSeconds(long ticks)
                => ticks <= 0L ? 0.0 : (double)ticks / Stopwatch.Frequency;

            private void PrintLinkDebug(uint bindingId)
            {
                string info = string.Empty;
                MeasureRenderingProgramGlCall(
                    "glGetProgramInfoLog",
                    bindingId,
                    () => Api.GetProgramInfoLog(bindingId, out info),
                    "phase=link-debug");
                PrintLinkDebug(bindingId, info, "Link failed");
            }

            private void PrintLinkDebug(uint bindingId, string? info, string? failureKind)
            {
                string programName = GetProgramDebugName();
                var builder = new StringBuilder();
                builder
                    .Append("GLRenderProgram ")
                    .Append(string.IsNullOrWhiteSpace(failureKind) ? "link failed" : failureKind)
                    .AppendLine(".")
                    .Append("Program='")
                    .Append(programName)
                    .Append("', BindingId=")
                    .Append(bindingId)
                    .Append(", Hash=")
                    .Append(Hash)
                    .Append(", Separable=")
                    .Append(Data.Separable)
                    .Append(", ShaderCount=")
                    .AppendLine(_shaderCache.Count.ToString());

                builder.AppendLine("[Driver Log]");
                builder.AppendLine(string.IsNullOrWhiteSpace(info)
                    ? "Unable to link program, but no error was returned."
                    : info.TrimEnd());

                builder.AppendLine("[Shaders]");
                foreach (GLShader shader in _shaderCache.Values)
                {
                    string? filePath = shader.Data.Source?.FilePath;
                    builder
                        .Append("  - ")
                        .Append(shader.Data.Type)
                        .Append(": ")
                        .AppendLine(string.IsNullOrWhiteSpace(filePath) ? "<inline>" : filePath);
                }

                if (!string.IsNullOrWhiteSpace(_linkRequestStackTrace))
                {
                    builder.AppendLine("[Link Request StackTrace]");
                    builder.AppendLine(_linkRequestStackTrace.TrimEnd());
                }

                Debug.OpenGLError(builder.ToString());
            }

            private void CaptureLinkRequestStackTrace()
            {
#if DEBUG || EDITOR
                _linkRequestStackTrace ??= Debug.GetStackTrace(3, 24, ignoreBeforeWndProc: false);
#endif
            }

        }
    }
}
