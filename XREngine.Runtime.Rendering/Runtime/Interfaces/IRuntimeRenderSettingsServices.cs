using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Data.Transforms.Rotations;
using XREngine.Input;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Shadows;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;

namespace XREngine.Rendering;

/// <summary>
/// Cold render configuration and shadow policy supplied by the application host.
/// </summary>
public interface IRuntimeRenderSettingsServices
{
    double TextureSlowCpuDecodeResizeMilliseconds { get; }
    double TextureSlowMipBuildMilliseconds { get; }
    double TextureSlowUploadChunkMilliseconds { get; }
    double TextureSlowTransitionMilliseconds { get; }
    double TextureSlowQueueWaitMilliseconds { get; }
    double TextureUploadFrameBudgetMilliseconds { get; }


    /// <summary>
    /// Gets whether shader pipeline objects may be used instead of monolithic shader programs.
    /// </summary>
    bool AllowShaderPipelines { get; }

    /// <summary>
    /// Gets whether exact transparency techniques such as depth peeling and per-pixel fragment storage are enabled.
    /// </summary>
    bool EnableExactTransparencyTechniques { get; }

    /// <summary>
    /// Gets whether mesh vertex attributes should be packed into an interleaved GPU buffer layout by default.
    /// </summary>
    bool UseInterleavedMeshBuffer { get; }

    /// <summary>
    /// Gets whether shader uniforms that represent integer data should use integer uniform APIs.
    /// </summary>
    bool UseIntegerUniformsInShaders { get; }

    /// <summary>
    /// Gets whether imported blendshape deltas should be remapped before runtime mesh data is populated.
    /// </summary>
    bool RemapBlendshapeDeltas { get; }

    /// <summary>
    /// Gets whether imported and runtime meshes should retain blendshape data.
    /// </summary>
    bool AllowBlendshapes { get; }

    /// <summary>
    /// Gets whether mesh vertex data population may use parallel CPU work.
    /// </summary>
    bool PopulateVertexDataInParallel { get; }

    /// <summary>
    /// Gets whether mesh import processing may be scheduled asynchronously.
    /// </summary>
    bool ProcessMeshImportsAsynchronously { get; }

    /// <summary>
    /// Gets whether imported and runtime meshes should retain skinning data.
    /// </summary>
    bool AllowSkinning { get; }

    /// <summary>
    /// Gets whether skeletal skinning should be evaluated by the compute pre-pass.
    /// </summary>
    bool CalculateSkinningInComputeShader { get; }

    /// <summary>
    /// Gets whether blendshapes should be evaluated by the compute pre-pass.
    /// </summary>
    bool CalculateBlendshapesInComputeShader { get; }

    /// <summary>
    /// Gets whether skinned mesh bounds should be evaluated on the GPU.
    /// </summary>
    bool CalculateSkinnedBoundsInComputeShader { get; }

    /// <summary>
    /// Gets whether GPU skinned bounds may write directly into GPU command AABB buffers.
    /// </summary>
    bool SkinnedBoundsGpuDirectAabbWrite { get; }

    /// <summary>
    /// Gets whether eligible blendshape renderers may run a pre-pass that combines active shapes into per-vertex deltas.
    /// </summary>
    bool EnableBlendshapePrecombinePass { get; }

    /// <summary>
    /// Gets whether the precombined blendshape pass may feed direct vertex-shader blendshape evaluation.
    /// </summary>
    bool EnableBlendshapePrecombineForDirectVertexPath { get; }

    /// <summary>
    /// Gets whether eligible cooked blendshape basis payloads may use PCA/SVD basis compression.
    /// </summary>
    bool EnableBlendshapePcaBasisCompression { get; }

    /// <summary>
    /// Gets the minimum active shape count before the compute deformation path may pay the extra precombine dispatch.
    /// </summary>
    int BlendshapePrecombineComputeMinActiveShapes { get; }

    /// <summary>
    /// Gets the minimum active shape count before the direct vertex path may pay the extra precombine dispatch.
    /// </summary>
    int BlendshapePrecombineDirectMinActiveShapes { get; }

    /// <summary>
    /// Gets the minimum affected vertex count before the precombine pass is considered.
    /// </summary>
    int BlendshapePrecombineMinAffectedVertices { get; }

    /// <summary>
    /// Gets whether non-essential mesh LOD levels defer atlas upload until the GPU LOD-select pass requests them.
    /// </summary>
    bool StreamMeshLodsOnDemand { get; }

    /// <summary>
    /// Gets the render-frame interval between drains of the GPU mesh LOD request buffer.
    /// </summary>
    int MeshLodStreamingDrainIntervalFrames { get; }

    /// <summary>
    /// Gets the maximum number of LOD atlas loads serviced per request-buffer drain.
    /// </summary>
    int MeshLodStreamingMaxLoadsPerDrain { get; }

    /// <summary>
    /// Gets the host shader configuration revision used to invalidate runtime render programs.
    /// </summary>
    int ShaderConfigVersion { get; }

    /// <summary>
    /// Gets the requested logical clip-space Y direction.
    /// </summary>
    ERenderClipSpaceYDirection ClipSpaceYDirection { get; }

    /// <summary>
    /// Gets the requested clip-space depth range.
    /// </summary>
    ERenderClipDepthRange ClipDepthRange { get; }

    /// <summary>
    /// Gets whether linked OpenGL shader programs may be cached as driver binaries.
    /// </summary>
    bool AllowBinaryProgramCaching { get; }

    /// <summary>
    /// Gets whether cached OpenGL program binaries may be uploaded on a shared GL context thread.
    /// </summary>
    bool AsyncProgramBinaryUpload { get; }

    /// <summary>
    /// Gets whether uncached OpenGL shader source programs may compile and link asynchronously.
    /// </summary>
    bool AsyncProgramCompilation { get; }

    /// <summary>
    /// Gets the requested shared-context OpenGL source compile/link worker count.
    /// </summary>
    int OpenGLProgramCompileLinkWorkerCount { get; }

    /// <summary>
    /// Gets the maximum number of async OpenGL shader programs advanced per render frame.
    /// </summary>
    int MaxAsyncShaderProgramsPerFrame { get; }

    /// <summary>
    /// Gets the OpenGL shader compile/link backend selection strategy.
    /// </summary>
    EOpenGLShaderLinkStrategy OpenGLShaderLinkStrategy { get; }

    /// <summary>
    /// Gets the GL_ARB/KHR_parallel_shader_compile compiler thread request.
    /// </summary>
    int OpenGLShaderCompilerThreadCount { get; }

    /// <summary>
    /// Gets whether the startup driver-parallel OpenGL shader link probe is enabled.
    /// </summary>
    bool OpenGLParallelShaderCompileProbeEnabled { get; }

    /// <summary>
    /// Gets the startup driver-parallel OpenGL shader link probe timeout in milliseconds.
    /// </summary>
    int OpenGLParallelShaderCompileProbeTimeoutMs { get; }

    /// <summary>
    /// Gets the Vulkan memory allocation backend requested by the host.
    /// </summary>
    EVulkanAllocatorBackend VulkanAllocatorBackend { get; }

    /// <summary>
    /// Gets the Vulkan synchronization backend requested by the host.
    /// </summary>
    EVulkanSynchronizationBackend VulkanSynchronizationBackend { get; }

    /// <summary>
    /// Gets the Vulkan descriptor update backend requested by the host.
    /// </summary>
    EVulkanDescriptorUpdateBackend VulkanDescriptorUpdateBackend { get; }

    /// <summary>
    /// Gets whether Vulkan dynamic uniform ring buffers should be enabled.
    /// </summary>
    bool VulkanDynamicUniformBufferEnabled { get; }

    /// <summary>
    /// Gets whether Vulkan bindless material-table population is enabled.
    /// </summary>
    bool EnableVulkanBindlessMaterialTable => RuntimeRenderingHostServiceDefaults.EnableVulkanBindlessMaterialTable;

    /// <summary>
    /// Gets whether Vulkan descriptor indexing should be requested.
    /// </summary>
    bool EnableVulkanDescriptorIndexing => RuntimeRenderingHostServiceDefaults.EnableVulkanDescriptorIndexing;

    /// <summary>
    /// Gets whether Vulkan descriptor contracts should be validated.
    /// </summary>
    bool ValidateVulkanDescriptorContracts => RuntimeRenderingHostServiceDefaults.ValidateVulkanDescriptorContracts;

    /// <summary>
    /// Gets the Vulkan bindless material-table mode.
    /// </summary>
    EVulkanBindlessMaterialMode VulkanBindlessMaterialMode => RuntimeRenderingHostServiceDefaults.VulkanBindlessMaterialMode;

    /// <summary>
    /// Gets the Vulkan geometry fetch strategy.
    /// </summary>
    EVulkanGeometryFetchMode VulkanGeometryFetchMode => RuntimeRenderingHostServiceDefaults.VulkanGeometryFetchMode;

    /// <summary>
    /// Gets the Vulkan render-target mode used to choose dynamic rendering or legacy render passes.
    /// </summary>
    EVulkanRenderTargetMode VulkanRenderTargetMode => RuntimeRenderingHostServiceDefaults.VulkanRenderTargetMode;

    /// <summary>
    /// Gets the Vulkan GPU-driven feature profile.
    /// </summary>
    EVulkanGpuDrivenProfile VulkanGpuDrivenProfile => RuntimeRenderingHostServiceDefaults.VulkanGpuDrivenProfile;

    /// <summary>
    /// Gets the Vulkan queue-overlap policy.
    /// </summary>
    EVulkanQueueOverlapMode VulkanQueueOverlapMode => RuntimeRenderingHostServiceDefaults.VulkanQueueOverlapMode;
    bool EnableVulkanPrimaryCommandBufferReuse => RuntimeRenderingHostServiceDefaults.EnableVulkanPrimaryCommandBufferReuse;

    /// <summary>
    /// Gets the named Vulkan diagnostics preset requested by the host.
    /// </summary>
    EVulkanDiagnosticPreset VulkanDiagnosticPreset => RuntimeRenderingHostServiceDefaults.VulkanDiagnosticPreset;

    /// <summary>
    /// Gets additional Vulkan diagnostics flags requested by the host.
    /// </summary>
    EVulkanDiagnosticFlags VulkanDiagnosticFlags => RuntimeRenderingHostServiceDefaults.VulkanDiagnosticFlags;

    /// <summary>
    /// Subscribes a callback to host rendering setting changes.
    /// </summary>
    void SubscribeRenderingSettingsChanged(Action callback);

    /// <summary>
    /// Unsubscribes a callback from host rendering setting changes.
    /// </summary>
    void UnsubscribeRenderingSettingsChanged(Action callback);

    /// <summary>
    /// Subscribes a callback to host anti-aliasing setting changes.
    /// </summary>
    void SubscribeAntiAliasingSettingsChanged(Action callback);

    /// <summary>
    /// Unsubscribes a callback from host anti-aliasing setting changes.
    /// </summary>
    void UnsubscribeAntiAliasingSettingsChanged(Action callback);


    /// <summary>
    /// Gets whether the host supplies authoritative shadow atlas settings.
    /// </summary>
    bool ProvidesShadowAtlasSettings { get; }

    /// <summary>
    /// Gets whether spot lights should render into the shared shadow atlas when possible.
    /// </summary>
    bool UseSpotShadowAtlas { get; }

    /// <summary>
    /// Gets whether directional lights should render into the shared shadow atlas when possible.
    /// </summary>
    bool UseDirectionalShadowAtlas { get; }

    /// <summary>
    /// Gets whether point lights should render face tiles into the shared shadow atlas when possible.
    /// </summary>
    bool UsePointShadowAtlas { get; }

    /// <summary>
    /// Gets the square page size in pixels for shadow atlas pages.
    /// </summary>
    uint ShadowAtlasPageSize { get; }

    /// <summary>
    /// Gets the maximum number of shadow atlas pages that may be allocated.
    /// </summary>
    int MaxShadowAtlasPages { get; }

    /// <summary>
    /// Gets the maximum shadow atlas memory budget in bytes.
    /// </summary>
    long MaxShadowAtlasMemoryBytes { get; }

    /// <summary>
    /// Gets the maximum number of shadow atlas tiles that may render in a single frame.
    /// </summary>
    int MaxShadowTilesRenderedPerFrame { get; }

    /// <summary>
    /// Gets the maximum target CPU-side shadow rendering budget in milliseconds.
    /// </summary>
    float MaxShadowRenderMilliseconds { get; }

    /// <summary>
    /// Gets the maximum rendered-frame age for directional cascade atlas stale reprojection.
    /// </summary>
    int MaxDirectionalCascadeAtlasStaleFrames { get; }

    /// <summary>
    /// Gets the minimum tile resolution allowed in the shadow atlas.
    /// </summary>
    uint MinShadowAtlasTileResolution { get; }

    /// <summary>
    /// Gets the maximum tile resolution allowed in the shadow atlas.
    /// </summary>
    uint MaxShadowAtlasTileResolution { get; }
}
