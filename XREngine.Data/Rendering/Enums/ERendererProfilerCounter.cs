namespace XREngine.Data.Rendering;

/// <summary>
/// Low-level renderer state counters shared by runtime renderers and the host profiler.
/// Numeric values are part of the runtime-host bridge; append new values only.
/// </summary>
public enum ERendererProfilerCounter
{
    IndirectCountCalls = 0,
    ShaderProgramSwitches = 1,
    ProgramPipelineSwitches = 2,
    VaoBinds = 3,
    VaoBindSkips = 4,
    ArrayBufferBinds = 5,
    ElementArrayBufferBinds = 6,
    DrawIndirectBufferBinds = 7,
    ParameterBufferBinds = 8,
    SsboBinds = 9,
    UboBinds = 10,
    TextureBinds = 11,
    TextureBindSkips = 12,
    TextureUnitSwitches = 13,
    UniformCalls = 14,
    SamplerUniformCalls = 15,
    BufferUploadBytes = 16,
    BarrierCalls = 17,
    TimestampQueryCount = 18,
    TimestampQueryReadbackBytes = 19,
    TimestampDenseModeFrames = 20,
    RedundantStateSkips = 21,
    CpuDirectDrawCalls = 22,
    GpuIndirectDrawCalls = 23,
    GpuMeshletDrawCalls = 24,
    UnknownStrategyDrawCalls = 25,
    DirectionalCascadeStaleSampled = 26,
    DirectionalCascadeMixedGenerationPrevented = 27,
    DirectionalCascadePhysicalReprojected = 28,
    DirectionalCascadeForcedFreshRender = 29,
}
