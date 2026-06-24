using XREngine.Data.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine;

public readonly record struct EffectiveRenderSettingsSnapshot(
    EffectiveCommonRenderSettings Common,
    EffectiveOpenGLRenderSettings OpenGL,
    EffectiveVulkanRenderSettings Vulkan);

public readonly record struct EffectiveCommonRenderSettings(
    ERenderLibrary PreferredBackend,
    RenderBackendFallbackPolicy BackendFallbackPolicy);

public readonly record struct EffectiveOpenGLRenderSettings(
    bool AllowProgramPipelines,
    bool AllowBinaryProgramCaching,
    bool AsyncProgramBinaryUpload,
    bool AsyncProgramCompilation,
    int ProgramCompileLinkWorkerCount,
    int MaxAsyncShaderProgramsPerFrame,
    EOpenGLShaderLinkStrategy ShaderLinkStrategy,
    int DriverCompilerThreadCount,
    bool DriverParallelProbeEnabled,
    int DriverParallelProbeTimeoutMs,
    bool UseDetailPreservingComputeMipmaps);

public readonly record struct EffectiveVulkanRobustnessSettings(
    EVulkanAllocatorBackend AllocatorBackend,
    EVulkanSynchronizationBackend SyncBackend,
    EVulkanDescriptorUpdateBackend DescriptorUpdateBackend,
    bool DynamicUniformBufferEnabled);

public readonly record struct EffectiveVulkanRenderSettings(
    EVulkanGpuDrivenProfile GpuDrivenProfile,
    EVulkanQueueOverlapMode QueueOverlapMode,
    bool EnableDescriptorIndexing,
    bool EnableBindlessMaterialTable,
    EVulkanBindlessMaterialMode BindlessMaterialMode,
    bool ValidateDescriptorContracts,
    EVulkanGeometryFetchMode GeometryFetchMode,
    EVulkanRenderTargetMode RenderTargetMode,
    RenderBackendFallbackPolicy BackendFallbackPolicy,
    EffectiveVulkanRobustnessSettings Robustness);
