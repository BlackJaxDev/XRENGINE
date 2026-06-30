using System;
using System.Threading;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                public static class RendererState
                {
                    private static int _indirectCountCalls;
                    private static int _shaderProgramSwitches;
                    private static int _programPipelineSwitches;
                    private static int _vaoBinds;
                    private static int _vaoBindSkips;
                    private static int _arrayBufferBinds;
                    private static int _elementArrayBufferBinds;
                    private static int _drawIndirectBufferBinds;
                    private static int _parameterBufferBinds;
                    private static int _ssboBinds;
                    private static int _uboBinds;
                    private static int _textureBinds;
                    private static int _textureBindSkips;
                    private static int _textureUnitSwitches;
                    private static int _uniformCalls;
                    private static int _samplerUniformCalls;
                    private static long _bufferUploadBytes;
                    private static int _barrierCalls;
                    private static int _barrierAll;
                    private static int _barrierCommand;
                    private static int _barrierBufferUpdate;
                    private static int _barrierShaderStorage;
                    private static int _barrierTextureFetch;
                    private static int _barrierTextureUpdate;
                    private static int _barrierFramebuffer;
                    private static int _timestampQueryCount;
                    private static long _timestampQueryReadbackBytes;
                    private static int _timestampDenseModeFrames;
                    private static int _redundantStateSkips;
                    private static int _cpuDirectDrawCalls;
                    private static int _gpuIndirectDrawCalls;
                    private static int _gpuMeshletDrawCalls;
                    private static int _unknownStrategyDrawCalls;

                    private static int _lastFrameIndirectCountCalls;
                    private static int _lastFrameShaderProgramSwitches;
                    private static int _lastFrameProgramPipelineSwitches;
                    private static int _lastFrameVaoBinds;
                    private static int _lastFrameVaoBindSkips;
                    private static int _lastFrameArrayBufferBinds;
                    private static int _lastFrameElementArrayBufferBinds;
                    private static int _lastFrameDrawIndirectBufferBinds;
                    private static int _lastFrameParameterBufferBinds;
                    private static int _lastFrameSsboBinds;
                    private static int _lastFrameUboBinds;
                    private static int _lastFrameTextureBinds;
                    private static int _lastFrameTextureBindSkips;
                    private static int _lastFrameTextureUnitSwitches;
                    private static int _lastFrameUniformCalls;
                    private static int _lastFrameSamplerUniformCalls;
                    private static long _lastFrameBufferUploadBytes;
                    private static int _lastFrameBarrierCalls;
                    private static int _lastFrameBarrierAll;
                    private static int _lastFrameBarrierCommand;
                    private static int _lastFrameBarrierBufferUpdate;
                    private static int _lastFrameBarrierShaderStorage;
                    private static int _lastFrameBarrierTextureFetch;
                    private static int _lastFrameBarrierTextureUpdate;
                    private static int _lastFrameBarrierFramebuffer;
                    private static int _lastFrameTimestampQueryCount;
                    private static long _lastFrameTimestampQueryReadbackBytes;
                    private static int _lastFrameTimestampDenseModeFrames;
                    private static int _lastFrameRedundantStateSkips;
                    private static int _lastFrameCpuDirectDrawCalls;
                    private static int _lastFrameGpuIndirectDrawCalls;
                    private static int _lastFrameGpuMeshletDrawCalls;
                    private static int _lastFrameUnknownStrategyDrawCalls;

                    private static readonly object _contextLock = new();
                    private static string _activeTextureBindingRung = "unknown";
                    private static string _activeStereoMode = "mono";
                    private static string _activeVrViewRenderModeRequested = "unknown";
                    private static string _activeVrViewRenderModeEffective = "unknown";
                    private static string _activeVrViewRenderImplementationPath = "unknown";
                    private static string _activeVrTemporalHistoryPolicy = "unknown";
                    private static string _activeSubmissionStrategy = "unknown";
                    private static string _activeRenderBackend = "unknown";
                    private static bool _validationLayersEnabled;
                    private static bool _debugOutputEnabled;
                    private static bool _gpuTimestampsDenseMode;

                    private static string _lastFrameActiveTextureBindingRung = "unknown";
                    private static string _lastFrameActiveStereoMode = "mono";
                    private static string _lastFrameActiveVrViewRenderModeRequested = "unknown";
                    private static string _lastFrameActiveVrViewRenderModeEffective = "unknown";
                    private static string _lastFrameActiveVrViewRenderImplementationPath = "unknown";
                    private static string _lastFrameActiveVrTemporalHistoryPolicy = "unknown";
                    private static string _lastFrameActiveSubmissionStrategy = "unknown";
                    private static string _lastFrameActiveRenderBackend = "unknown";
                    private static bool _lastFrameValidationLayersEnabled;
                    private static bool _lastFrameDebugOutputEnabled;
                    private static bool _lastFrameGpuTimestampsDenseMode;

                    public static int IndirectCountCalls => _lastFrameIndirectCountCalls;
                    public static int ShaderProgramSwitches => _lastFrameShaderProgramSwitches;
                    public static int ProgramPipelineSwitches => _lastFrameProgramPipelineSwitches;
                    public static int VaoBinds => _lastFrameVaoBinds;
                    public static int VaoBindSkips => _lastFrameVaoBindSkips;
                    public static int ArrayBufferBinds => _lastFrameArrayBufferBinds;
                    public static int ElementArrayBufferBinds => _lastFrameElementArrayBufferBinds;
                    public static int DrawIndirectBufferBinds => _lastFrameDrawIndirectBufferBinds;
                    public static int ParameterBufferBinds => _lastFrameParameterBufferBinds;
                    public static int SsboBinds => _lastFrameSsboBinds;
                    public static int UboBinds => _lastFrameUboBinds;
                    public static int TextureBinds => _lastFrameTextureBinds;
                    public static int TextureBindSkips => _lastFrameTextureBindSkips;
                    public static int TextureUnitSwitches => _lastFrameTextureUnitSwitches;
                    public static int UniformCalls => _lastFrameUniformCalls;
                    public static int SamplerUniformCalls => _lastFrameSamplerUniformCalls;
                    public static long BufferUploadBytes => _lastFrameBufferUploadBytes;
                    public static int BarrierCalls => _lastFrameBarrierCalls;
                    public static int BarrierAll => _lastFrameBarrierAll;
                    public static int BarrierCommand => _lastFrameBarrierCommand;
                    public static int BarrierBufferUpdate => _lastFrameBarrierBufferUpdate;
                    public static int BarrierShaderStorage => _lastFrameBarrierShaderStorage;
                    public static int BarrierTextureFetch => _lastFrameBarrierTextureFetch;
                    public static int BarrierTextureUpdate => _lastFrameBarrierTextureUpdate;
                    public static int BarrierFramebuffer => _lastFrameBarrierFramebuffer;
                    public static int TimestampQueryCount => _lastFrameTimestampQueryCount;
                    public static long TimestampQueryReadbackBytes => _lastFrameTimestampQueryReadbackBytes;
                    public static int TimestampDenseModeFrames => _lastFrameTimestampDenseModeFrames;
                    public static int RedundantStateSkips => _lastFrameRedundantStateSkips;
                    public static int CpuDirectDrawCalls => _lastFrameCpuDirectDrawCalls;
                    public static int GpuIndirectDrawCalls => _lastFrameGpuIndirectDrawCalls;
                    public static int GpuMeshletDrawCalls => _lastFrameGpuMeshletDrawCalls;
                    public static int UnknownStrategyDrawCalls => _lastFrameUnknownStrategyDrawCalls;
                    public static string ActiveTextureBindingRung => _lastFrameActiveTextureBindingRung;
                    public static string ActiveStereoMode => _lastFrameActiveStereoMode;
                    public static string ActiveVrViewRenderModeRequested => _lastFrameActiveVrViewRenderModeRequested;
                    public static string ActiveVrViewRenderModeEffective => _lastFrameActiveVrViewRenderModeEffective;
                    public static string ActiveVrViewRenderImplementationPath => _lastFrameActiveVrViewRenderImplementationPath;
                    public static string ActiveVrTemporalHistoryPolicy => _lastFrameActiveVrTemporalHistoryPolicy;
                    public static string ActiveSubmissionStrategy => _lastFrameActiveSubmissionStrategy;
                    public static string ActiveRenderBackend => _lastFrameActiveRenderBackend;
                    public static bool ValidationLayersEnabled => _lastFrameValidationLayersEnabled;
                    public static bool DebugOutputEnabled => _lastFrameDebugOutputEnabled;
                    public static bool GpuTimestampsDenseMode => _lastFrameGpuTimestampsDenseMode;

                    internal static void SnapshotAndReset()
                    {
                        _lastFrameIndirectCountCalls = Interlocked.Exchange(ref _indirectCountCalls, 0);
                        _lastFrameShaderProgramSwitches = Interlocked.Exchange(ref _shaderProgramSwitches, 0);
                        _lastFrameProgramPipelineSwitches = Interlocked.Exchange(ref _programPipelineSwitches, 0);
                        _lastFrameVaoBinds = Interlocked.Exchange(ref _vaoBinds, 0);
                        _lastFrameVaoBindSkips = Interlocked.Exchange(ref _vaoBindSkips, 0);
                        _lastFrameArrayBufferBinds = Interlocked.Exchange(ref _arrayBufferBinds, 0);
                        _lastFrameElementArrayBufferBinds = Interlocked.Exchange(ref _elementArrayBufferBinds, 0);
                        _lastFrameDrawIndirectBufferBinds = Interlocked.Exchange(ref _drawIndirectBufferBinds, 0);
                        _lastFrameParameterBufferBinds = Interlocked.Exchange(ref _parameterBufferBinds, 0);
                        _lastFrameSsboBinds = Interlocked.Exchange(ref _ssboBinds, 0);
                        _lastFrameUboBinds = Interlocked.Exchange(ref _uboBinds, 0);
                        _lastFrameTextureBinds = Interlocked.Exchange(ref _textureBinds, 0);
                        _lastFrameTextureBindSkips = Interlocked.Exchange(ref _textureBindSkips, 0);
                        _lastFrameTextureUnitSwitches = Interlocked.Exchange(ref _textureUnitSwitches, 0);
                        _lastFrameUniformCalls = Interlocked.Exchange(ref _uniformCalls, 0);
                        _lastFrameSamplerUniformCalls = Interlocked.Exchange(ref _samplerUniformCalls, 0);
                        _lastFrameBufferUploadBytes = Interlocked.Exchange(ref _bufferUploadBytes, 0);
                        _lastFrameBarrierCalls = Interlocked.Exchange(ref _barrierCalls, 0);
                        _lastFrameBarrierAll = Interlocked.Exchange(ref _barrierAll, 0);
                        _lastFrameBarrierCommand = Interlocked.Exchange(ref _barrierCommand, 0);
                        _lastFrameBarrierBufferUpdate = Interlocked.Exchange(ref _barrierBufferUpdate, 0);
                        _lastFrameBarrierShaderStorage = Interlocked.Exchange(ref _barrierShaderStorage, 0);
                        _lastFrameBarrierTextureFetch = Interlocked.Exchange(ref _barrierTextureFetch, 0);
                        _lastFrameBarrierTextureUpdate = Interlocked.Exchange(ref _barrierTextureUpdate, 0);
                        _lastFrameBarrierFramebuffer = Interlocked.Exchange(ref _barrierFramebuffer, 0);
                        _lastFrameTimestampQueryCount = Interlocked.Exchange(ref _timestampQueryCount, 0);
                        _lastFrameTimestampQueryReadbackBytes = Interlocked.Exchange(ref _timestampQueryReadbackBytes, 0);
                        _lastFrameTimestampDenseModeFrames = Interlocked.Exchange(ref _timestampDenseModeFrames, 0);
                        _lastFrameRedundantStateSkips = Interlocked.Exchange(ref _redundantStateSkips, 0);
                        _lastFrameCpuDirectDrawCalls = Interlocked.Exchange(ref _cpuDirectDrawCalls, 0);
                        _lastFrameGpuIndirectDrawCalls = Interlocked.Exchange(ref _gpuIndirectDrawCalls, 0);
                        _lastFrameGpuMeshletDrawCalls = Interlocked.Exchange(ref _gpuMeshletDrawCalls, 0);
                        _lastFrameUnknownStrategyDrawCalls = Interlocked.Exchange(ref _unknownStrategyDrawCalls, 0);

                        lock (_contextLock)
                        {
                            _lastFrameActiveTextureBindingRung = _activeTextureBindingRung;
                            _lastFrameActiveStereoMode = _activeStereoMode;
                            _lastFrameActiveVrViewRenderModeRequested = _activeVrViewRenderModeRequested;
                            _lastFrameActiveVrViewRenderModeEffective = _activeVrViewRenderModeEffective;
                            _lastFrameActiveVrViewRenderImplementationPath = _activeVrViewRenderImplementationPath;
                            _lastFrameActiveVrTemporalHistoryPolicy = _activeVrTemporalHistoryPolicy;
                            _lastFrameActiveSubmissionStrategy = _activeSubmissionStrategy;
                            _lastFrameActiveRenderBackend = _activeRenderBackend;
                            _lastFrameValidationLayersEnabled = _validationLayersEnabled;
                            _lastFrameDebugOutputEnabled = _debugOutputEnabled;
                            _lastFrameGpuTimestampsDenseMode = _gpuTimestampsDenseMode;
                        }
                    }

                    public static void UpdateFrameContext(
                        string? submissionStrategy,
                        string? textureBindingRung,
                        string? stereoMode,
                        string? vrViewRenderModeRequested,
                        string? vrViewRenderModeEffective,
                        string? vrViewRenderImplementationPath,
                        string? vrTemporalHistoryPolicy,
                        string? renderBackend,
                        bool validationLayersEnabled,
                        bool debugOutputEnabled,
                        bool gpuTimestampsDenseMode)
                    {
                        lock (_contextLock)
                        {
                            _activeSubmissionStrategy = string.IsNullOrWhiteSpace(submissionStrategy) ? "unknown" : submissionStrategy!;
                            _activeTextureBindingRung = string.IsNullOrWhiteSpace(textureBindingRung) ? "unknown" : textureBindingRung!;
                            _activeStereoMode = string.IsNullOrWhiteSpace(stereoMode) ? "mono" : stereoMode!;
                            _activeVrViewRenderModeRequested = string.IsNullOrWhiteSpace(vrViewRenderModeRequested) ? "unknown" : vrViewRenderModeRequested!;
                            _activeVrViewRenderModeEffective = string.IsNullOrWhiteSpace(vrViewRenderModeEffective) ? "unknown" : vrViewRenderModeEffective!;
                            _activeVrViewRenderImplementationPath = string.IsNullOrWhiteSpace(vrViewRenderImplementationPath) ? "unknown" : vrViewRenderImplementationPath!;
                            _activeVrTemporalHistoryPolicy = string.IsNullOrWhiteSpace(vrTemporalHistoryPolicy) ? "unknown" : vrTemporalHistoryPolicy!;
                            _activeRenderBackend = string.IsNullOrWhiteSpace(renderBackend) ? "unknown" : renderBackend!;
                            _validationLayersEnabled = validationLayersEnabled;
                            _debugOutputEnabled = debugOutputEnabled;
                            _gpuTimestampsDenseMode = gpuTimestampsDenseMode;
                        }
                    }

                    public static void RecordCounter(ERendererProfilerCounter counter, long count = 1)
                    {
                        if (!EnableTracking || count <= 0)
                            return;

                        int intCount = count > int.MaxValue ? int.MaxValue : (int)count;
                        switch (counter)
                        {
                            case ERendererProfilerCounter.IndirectCountCalls:
                                Interlocked.Add(ref _indirectCountCalls, intCount);
                                break;
                            case ERendererProfilerCounter.ShaderProgramSwitches:
                                Interlocked.Add(ref _shaderProgramSwitches, intCount);
                                break;
                            case ERendererProfilerCounter.ProgramPipelineSwitches:
                                Interlocked.Add(ref _programPipelineSwitches, intCount);
                                break;
                            case ERendererProfilerCounter.VaoBinds:
                                Interlocked.Add(ref _vaoBinds, intCount);
                                break;
                            case ERendererProfilerCounter.VaoBindSkips:
                                Interlocked.Add(ref _vaoBindSkips, intCount);
                                Interlocked.Add(ref _redundantStateSkips, intCount);
                                break;
                            case ERendererProfilerCounter.ArrayBufferBinds:
                                Interlocked.Add(ref _arrayBufferBinds, intCount);
                                break;
                            case ERendererProfilerCounter.ElementArrayBufferBinds:
                                Interlocked.Add(ref _elementArrayBufferBinds, intCount);
                                break;
                            case ERendererProfilerCounter.DrawIndirectBufferBinds:
                                Interlocked.Add(ref _drawIndirectBufferBinds, intCount);
                                break;
                            case ERendererProfilerCounter.ParameterBufferBinds:
                                Interlocked.Add(ref _parameterBufferBinds, intCount);
                                break;
                            case ERendererProfilerCounter.SsboBinds:
                                Interlocked.Add(ref _ssboBinds, intCount);
                                break;
                            case ERendererProfilerCounter.UboBinds:
                                Interlocked.Add(ref _uboBinds, intCount);
                                break;
                            case ERendererProfilerCounter.TextureBinds:
                                Interlocked.Add(ref _textureBinds, intCount);
                                break;
                            case ERendererProfilerCounter.TextureBindSkips:
                                Interlocked.Add(ref _textureBindSkips, intCount);
                                Interlocked.Add(ref _redundantStateSkips, intCount);
                                break;
                            case ERendererProfilerCounter.TextureUnitSwitches:
                                Interlocked.Add(ref _textureUnitSwitches, intCount);
                                break;
                            case ERendererProfilerCounter.UniformCalls:
                                Interlocked.Add(ref _uniformCalls, intCount);
                                break;
                            case ERendererProfilerCounter.SamplerUniformCalls:
                                Interlocked.Add(ref _samplerUniformCalls, intCount);
                                break;
                            case ERendererProfilerCounter.BufferUploadBytes:
                                Interlocked.Add(ref _bufferUploadBytes, count);
                                break;
                            case ERendererProfilerCounter.BarrierCalls:
                                Interlocked.Add(ref _barrierCalls, intCount);
                                break;
                            case ERendererProfilerCounter.TimestampQueryCount:
                                Interlocked.Add(ref _timestampQueryCount, intCount);
                                break;
                            case ERendererProfilerCounter.TimestampQueryReadbackBytes:
                                Interlocked.Add(ref _timestampQueryReadbackBytes, count);
                                break;
                            case ERendererProfilerCounter.TimestampDenseModeFrames:
                                Interlocked.Add(ref _timestampDenseModeFrames, intCount);
                                break;
                            case ERendererProfilerCounter.RedundantStateSkips:
                                Interlocked.Add(ref _redundantStateSkips, intCount);
                                break;
                            case ERendererProfilerCounter.CpuDirectDrawCalls:
                                Interlocked.Add(ref _cpuDirectDrawCalls, intCount);
                                break;
                            case ERendererProfilerCounter.GpuIndirectDrawCalls:
                                Interlocked.Add(ref _gpuIndirectDrawCalls, intCount);
                                break;
                            case ERendererProfilerCounter.GpuMeshletDrawCalls:
                                Interlocked.Add(ref _gpuMeshletDrawCalls, intCount);
                                break;
                            case ERendererProfilerCounter.UnknownStrategyDrawCalls:
                                Interlocked.Add(ref _unknownStrategyDrawCalls, intCount);
                                break;
                        }
                    }

                    public static void RecordMemoryBarrier(EMemoryBarrierMask mask)
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Increment(ref _barrierCalls);
                        if (mask.HasFlag(EMemoryBarrierMask.All))
                            Interlocked.Increment(ref _barrierAll);
                        if (mask.HasFlag(EMemoryBarrierMask.Command))
                            Interlocked.Increment(ref _barrierCommand);
                        if (mask.HasFlag(EMemoryBarrierMask.BufferUpdate))
                            Interlocked.Increment(ref _barrierBufferUpdate);
                        if (mask.HasFlag(EMemoryBarrierMask.ShaderStorage))
                            Interlocked.Increment(ref _barrierShaderStorage);
                        if (mask.HasFlag(EMemoryBarrierMask.TextureFetch))
                            Interlocked.Increment(ref _barrierTextureFetch);
                        if (mask.HasFlag(EMemoryBarrierMask.TextureUpdate))
                            Interlocked.Increment(ref _barrierTextureUpdate);
                        if (mask.HasFlag(EMemoryBarrierMask.Framebuffer))
                            Interlocked.Increment(ref _barrierFramebuffer);
                    }

                    public static void RecordDrawCallsForStrategy(int count, string? strategy)
                    {
                        if (!EnableTracking || count <= 0)
                            return;

                        if (string.IsNullOrWhiteSpace(strategy))
                        {
                            RecordCounter(ERendererProfilerCounter.UnknownStrategyDrawCalls, count);
                            return;
                        }

                        if (strategy.Contains("CpuDirect", StringComparison.OrdinalIgnoreCase))
                            RecordCounter(ERendererProfilerCounter.CpuDirectDrawCalls, count);
                        else if (strategy.Contains("Meshlet", StringComparison.OrdinalIgnoreCase))
                            RecordCounter(ERendererProfilerCounter.GpuMeshletDrawCalls, count);
                        else if (strategy.Contains("GpuIndirect", StringComparison.OrdinalIgnoreCase))
                            RecordCounter(ERendererProfilerCounter.GpuIndirectDrawCalls, count);
                        else
                            RecordCounter(ERendererProfilerCounter.UnknownStrategyDrawCalls, count);
                    }
                }
            }
        }
    }
}
