# Backend Renderer Folder Organization Todo

Status: Draft.

Created: 2026-06-17.

Owner: Rendering.

Target branch: `renderer-backend-folder-organization`.

## Objective

Reorganize the OpenGL and Vulkan renderer subfolders so both backend trees use
the same responsibility-based map where it makes sense, while still allowing
backend-specific differences to remain obvious.

This is primarily a move-only and file-splitting refactor. The first goal is to
make navigation predictable for contributors working on rendering bugs,
performance work, shader compilation, resource lifetimes, and backend wrapper
parity. Behavior changes should be avoided until the folder moves and large
partial splits are independently validated.

## Why This Matters

The renderer backends are currently hard to navigate for different reasons:

- Vulkan has many unrelated top-level siblings: lifecycle, frame loop,
  command recording, descriptors, shader tooling, render-graph planning,
  resource allocation, feature integrations, and object wrappers all sit
  beside each other.
- Vulkan `Objects/` and `Objects/Types/` are vague names. They mix startup
  Vulkan handles, command-buffer helpers, swapchain objects, backend wrappers,
  textures, framebuffers, render programs, and mesh renderers.
- OpenGL already has some folders, but `Types/` mixes wrappers, queues,
  contexts, shader/program infrastructure, query objects, and render resources.
- OpenGL has folder names with spaces (`Types/Mesh Renderer`,
  `Types/Render Targets`) that make shell commands and links clumsier than
  necessary.
- Both backends have very large files that hide multiple responsibilities.
  Notable examples include:
  - `Vulkan/Objects/CommandBuffers.cs`
  - `Vulkan/VulkanShaderTools.cs`
  - `Vulkan/VulkanRenderer.State.cs`
  - `OpenGL/Types/Meshes/GLRenderProgram.Linking.cs`
  - `OpenGL/OpenGLRenderer.ImGuiViewports.cs`
  - `OpenGL/OpenGLRenderer.Luminance.cs`
- Backend docs still describe older shapes in places and should become the
  source of truth for the new folder map.

## Non-Goals

- Do not rewrite renderer architecture while moving files.
- Do not change public rendering behavior as part of move-only phases.
- Do not rename namespaces in the first pass. Keep
  `XREngine.Rendering.Vulkan` and `XREngine.Rendering.OpenGL` stable until all
  file moves build cleanly.
- Do not introduce new abstractions merely to match folder names.
- Do not move backend-agnostic contracts into backend folders.
- Do not hide explicitly requested GPU or accelerated paths behind silent CPU
  fallbacks.

## Shared Backend Taxonomy

Use this common folder vocabulary for both backends where applicable:

```text
<Backend>/
  Bootstrap/        Context/API creation, instance/device setup, extension probes, startup validation.
  Frame/            Frame lifecycle, swapchain/present or automatic swap notes, sync, fences, timing.
  Commands/         Draw submission, command buffers, blits, readbacks, indirect draw, state application.
  RenderGraph/      Backend render-graph compiler, pass planning, barriers, graph resource planning.
  Resources/        Backend resource allocation, staging/uploads, buffers, textures, framebuffers.
  Descriptors/      Descriptor/binding tables, bindless tables, sampler/image binding policy.
  Pipelines/        Pipeline/program caches, compile queues, prewarm databases, render target modes.
  Shaders/          Shader compile/link helpers, reflection, source compatibility, artifact caches.
  Features/         Optional or specialized backend features.
  UI/               ImGui and editor UI renderer integration.
  BackendObjects/   API wrapper objects around engine resources.
  Types/            Small backend-specific value types, enums, and interop structs.
```

Backend-specific interpretation:

- Vulkan uses `Frame/` heavily because acquire, command recording, submit, and
  present are explicit.
- OpenGL uses `Frame/` lightly because Silk.NET swaps automatically after the
  render event. Put frame-adjacent helpers there only when they are truly about
  per-frame lifecycle, pending upload polling, fences, or timing.
- Vulkan uses `Descriptors/` for descriptor set layouts, update templates, and
  descriptor-indexing contracts.
- OpenGL uses `Descriptors/` only for GL binding equivalents such as texture
  bindings, bindless residency handles, uniform binding policy, and sampler
  binding helpers.
- `BackendObjects/` is for wrappers around engine resources such as
  `GLDataBuffer`, `VkDataBuffer`, `GLRenderProgram`, `VkRenderProgram`,
  `GLTexture2D`, `VkTexture2D`, `GLFrameBuffer`, and `VkFrameBuffer`.
- `Types/` is for small shared backend structs/enums only, not large resource
  wrappers.

## Target Vulkan Shape

Target path:

```text
XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/
  Bootstrap/
  Frame/
  Commands/
  RenderGraph/
  Resources/
    Buffers/
    Textures/
    Framebuffers/
    Memory/
    Uploads/
  Descriptors/
  Pipelines/
  Shaders/
  Features/
    Meshlets/
    Raytracing/
    RTXIO/
    Streaming/
    Upscaling/
  UI/
  BackendObjects/
    Buffers/
    Framebuffers/
    Materials/
    MeshRendering/
    Programs/
    Queries/
    Samplers/
    Textures/
  Types/
```

### Vulkan Move Map

Initial move-only targets:

| Current Path | Target Path |
| --- | --- |
| `Vulkan/Init.cs` | `Vulkan/Bootstrap/VulkanRenderer.Initialization.cs` |
| `Vulkan/Extensions.cs` | `Vulkan/Bootstrap/VulkanExtensions.cs` |
| `Vulkan/PhysicalDevice.cs` | `Vulkan/Bootstrap/VulkanRenderer.PhysicalDevice.cs` |
| `Vulkan/Validation.cs` | `Vulkan/Bootstrap/VulkanRenderer.Validation.cs` |
| `Vulkan/Objects/Instance.cs` | `Vulkan/Bootstrap/VulkanRenderer.Instance.cs` |
| `Vulkan/Objects/Surface.cs` | `Vulkan/Bootstrap/VulkanRenderer.Surface.cs` |
| `Vulkan/Objects/LogicalDevice.cs` | `Vulkan/Bootstrap/VulkanRenderer.LogicalDevice.cs` |
| `Vulkan/Objects/CommandPool.cs` | `Vulkan/Commands/VulkanRenderer.CommandPool.cs` |
| `Vulkan/Objects/CommandBuffers.cs` | `Vulkan/Commands/VulkanRenderer.CommandBuffers.cs` |
| `Vulkan/Objects/CommandBuffers.Dlss.cs` | `Vulkan/Features/Upscaling/VulkanRenderer.CommandBuffers.Dlss.cs` |
| `Vulkan/Drawing.Core.cs` | `Vulkan/Frame/VulkanRenderer.FrameLoop.cs` |
| `Vulkan/Drawing.ResourceRetirement.cs` | `Vulkan/Frame/VulkanRenderer.ResourceRetirement.cs` |
| `Vulkan/VulkanRenderer.FrameTiming.cs` | `Vulkan/Frame/VulkanRenderer.FrameTiming.cs` |
| `Vulkan/SwapChain.cs` | `Vulkan/Frame/VulkanRenderer.Swapchain.cs` |
| `Vulkan/VulkanSynchronization.cs` | `Vulkan/Frame/VulkanRenderer.Synchronization.cs` |
| `Vulkan/Objects/SyncObjects.cs` | `Vulkan/Frame/VulkanRenderer.SyncObjects.cs` |
| `Vulkan/Drawing.Blit.cs` | `Vulkan/Commands/VulkanRenderer.Blit.cs` |
| `Vulkan/Drawing.IndirectDraw.cs` | `Vulkan/Commands/VulkanRenderer.IndirectDraw.cs` |
| `Vulkan/Drawing.Readback.cs` | `Vulkan/Commands/VulkanRenderer.Readback.cs` |
| `Vulkan/Drawing.RenderState.cs` | `Vulkan/Commands/VulkanRenderer.RenderState.cs` |
| `Vulkan/FrameBufferRenderPasses.cs` | `Vulkan/Resources/Framebuffers/VulkanRenderer.FrameBufferRenderPasses.cs` |
| `Vulkan/Objects/FrameBuffers.cs` | `Vulkan/Resources/Framebuffers/VulkanRenderer.SwapchainFramebuffers.cs` |
| `Vulkan/Objects/ImageViews.cs` | `Vulkan/Resources/Textures/VulkanRenderer.SwapchainImageViews.cs` |
| `Vulkan/Objects/RenderPasses.cs` | `Vulkan/Pipelines/VulkanRenderer.RenderPasses.cs` |
| `Vulkan/Objects/GraphicsPipeline.cs` | `Vulkan/Pipelines/VulkanRenderer.GraphicsPipeline.cs` |
| `Vulkan/VulkanRenderTargetMode.cs` | `Vulkan/Pipelines/VulkanRenderTargetMode.cs` |
| `Vulkan/VulkanPipelineCache.cs` | `Vulkan/Pipelines/VulkanPipelineCache.cs` |
| `Vulkan/VulkanPipelineCompileQueue.cs` | `Vulkan/Pipelines/VulkanPipelineCompileQueue.cs` |
| `Vulkan/VulkanPipelinePrewarmDatabase.cs` | `Vulkan/Pipelines/VulkanPipelinePrewarmDatabase.cs` |
| `Vulkan/VulkanGraphicsPipelineLibraryCache.cs` | `Vulkan/Pipelines/VulkanGraphicsPipelineLibraryCache.cs` |
| `Vulkan/VulkanRenderGraphCompiler.cs` | `Vulkan/RenderGraph/VulkanRenderGraphCompiler.cs` |
| `Vulkan/VulkanBarrierPlanner.cs` | `Vulkan/RenderGraph/VulkanBarrierPlanner.cs` |
| `Vulkan/VulkanResourcePlanner.cs` | `Vulkan/RenderGraph/VulkanResourcePlanner.cs` |
| `Vulkan/VulkanResourceAllocator.cs` | `Vulkan/Resources/VulkanResourceAllocator.cs` |
| `Vulkan/VulkanStagingManager.cs` | `Vulkan/Resources/Uploads/VulkanStagingManager.cs` |
| `Vulkan/VulkanDynamicUniformRingBuffer.cs` | `Vulkan/Resources/Buffers/VulkanDynamicUniformRingBuffer.cs` |
| `Vulkan/VulkanSceneDatabaseAddresses.cs` | `Vulkan/Resources/Buffers/VulkanSceneDatabaseAddresses.cs` |
| `Vulkan/Memory/*` | `Vulkan/Resources/Memory/*` |
| `Vulkan/VulkanDescriptorLayoutCache.cs` | `Vulkan/Descriptors/VulkanDescriptorLayoutCache.cs` |
| `Vulkan/VulkanDescriptorUpdateTemplates.cs` | `Vulkan/Descriptors/VulkanDescriptorUpdateTemplates.cs` |
| `Vulkan/VulkanDescriptorContracts.cs` | `Vulkan/Descriptors/VulkanDescriptorContracts.cs` |
| `Vulkan/VulkanDescriptorImageLayouts.cs` | `Vulkan/Descriptors/VulkanDescriptorImageLayouts.cs` |
| `Vulkan/VulkanComputeDescriptors.cs` | `Vulkan/Descriptors/VulkanRenderer.ComputeDescriptors.cs` |
| `Vulkan/VulkanBindlessMaterialDescriptors.cs` | `Vulkan/Descriptors/VulkanBindlessMaterialDescriptors.cs` |
| `Vulkan/VulkanImmutableSamplers.cs` | `Vulkan/Descriptors/VulkanRenderer.ImmutableSamplers.cs` |
| `Vulkan/VulkanShaderTools.cs` | `Vulkan/Shaders/VulkanShaderTools.cs` initially, then split |
| `Vulkan/VulkanShaderArtifactCache.cs` | `Vulkan/Shaders/VulkanShaderArtifactCache.cs` |
| `Vulkan/Objects/DescriptorPool.cs` | `Vulkan/Descriptors/VulkanRenderer.DescriptorPool.cs` |
| `Vulkan/Objects/DescriptorSets.cs` | `Vulkan/Descriptors/VulkanRenderer.DescriptorSets.cs` |
| `Vulkan/Objects/DescriptorSetLayout.cs` | `Vulkan/Descriptors/VulkanRenderer.DescriptorSetLayout.cs` |
| `Vulkan/Objects/UniformBuffers.cs` | `Vulkan/Resources/Buffers/VulkanRenderer.UniformBuffers.cs` |
| `Vulkan/VulkanFeatureProfile.cs` | `Vulkan/Features/VulkanFeatureProfile.cs` |
| `Vulkan/VulkanAutoExposure.cs` | `Vulkan/Features/VulkanRenderer.AutoExposure.cs` |
| `Vulkan/VulkanRaytracing.cs` | `Vulkan/Features/Raytracing/VulkanRenderer.Raytracing.cs` |
| `Vulkan/MemoryDecompression.cs` | `Vulkan/Features/RTXIO/VulkanRenderer.MemoryDecompression.cs` |
| `Vulkan/MemoryCopyIndirect.cs` | `Vulkan/Features/RTXIO/VulkanRenderer.MemoryCopyIndirect.cs` |
| `Vulkan/VulkanTextureStreamingHooks.cs` | `Vulkan/Features/Streaming/VulkanRenderer.TextureStreamingHooks.cs` |
| `Vulkan/VulkanStreamlineInterop.cs` | `Vulkan/Features/Upscaling/VulkanRenderer.StreamlineInterop.cs` |
| `Vulkan/VulkanUpscaleBridge*.cs` | `Vulkan/Features/Upscaling/VulkanUpscaleBridge*.cs` |
| `Vulkan/VulkanRenderer.Meshlets.cs` | `Vulkan/Features/Meshlets/VulkanRenderer.Meshlets.cs` |
| `Vulkan/VulkanRenderer.ImGui.cs` | `Vulkan/UI/VulkanRenderer.ImGui.cs` |
| `Vulkan/VulkanRenderer.PlaceholderTexture.cs` | `Vulkan/Resources/Textures/VulkanRenderer.PlaceholderTexture.cs` |
| `Vulkan/Objects/Types/Textures/*` | `Vulkan/BackendObjects/Textures/*` |
| `Vulkan/Objects/Types/MeshRenderer/*` | `Vulkan/BackendObjects/MeshRendering/*` |
| `Vulkan/Objects/Types/VkDataBuffer.cs` | `Vulkan/BackendObjects/Buffers/VkDataBuffer.cs` |
| `Vulkan/Objects/Types/VkRenderBuffer.cs` | `Vulkan/BackendObjects/Buffers/VkRenderBuffer.cs` |
| `Vulkan/Objects/Types/VkFrameBuffer.cs` | `Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs` |
| `Vulkan/Objects/Types/VkMaterial.cs` | `Vulkan/BackendObjects/Materials/VkMaterial.cs` |
| `Vulkan/Objects/Types/VKSampler.cs` | `Vulkan/BackendObjects/Samplers/VKSampler.cs` |
| `Vulkan/Objects/Types/VkShader.cs` | `Vulkan/BackendObjects/Programs/VkShader.cs` |
| `Vulkan/Objects/Types/VkRenderProgram*.cs` | `Vulkan/BackendObjects/Programs/VkRenderProgram*.cs` |
| `Vulkan/Objects/Types/VkRenderQuery.cs` | `Vulkan/BackendObjects/Queries/VkRenderQuery.cs` |
| `Vulkan/Objects/Types/VkObject*.cs` | `Vulkan/BackendObjects/VkObject*.cs` |
| `Vulkan/Objects/Types/IVk*.cs` | `Vulkan/BackendObjects/IVk*.cs` |
| `Vulkan/Objects/Types/VkFormatConversions.cs` | `Vulkan/Types/VkFormatConversions.cs` |
| `Vulkan/Objects/Types/VkTransformFeedback.cs` | `Vulkan/Types/VkTransformFeedback.cs` |
| `Vulkan/Objects/Types/VkObjectType.cs` | `Vulkan/Types/VkObjectType.cs` |
| `Vulkan/Types/QueueFamilyIndices.cs` | `Vulkan/Types/QueueFamilyIndices.cs` |
| `Vulkan/VulkanDepthClipControlExt.cs` | `Vulkan/Types/VulkanDepthClipControlExt.cs` |

## Target OpenGL Shape

Target path:

```text
XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/
  Bootstrap/
  Frame/
  Commands/
  Resources/
    Buffers/
    Framebuffers/
    Queries/
    Textures/
    Uploads/
  Descriptors/
  Pipelines/
  Shaders/
  Features/
    Bindless/
    Meshlets/
    SparseTextures/
    Streaming/
  UI/
  BackendObjects/
    Buffers/
    Framebuffers/
    Materials/
    MeshRendering/
    Programs/
    Queries/
    Samplers/
    Textures/
  Types/
```

OpenGL does not need a backend-local `RenderGraph/` folder unless new
OpenGL-specific graph planning code appears. The shared render pipeline and
frame-op contracts should remain outside the backend folder.

### OpenGL Move Map

Initial move-only targets:

| Current Path | Target Path |
| --- | --- |
| `OpenGL/OpenGLRenderer.cs` | `OpenGL/Bootstrap/OpenGLRenderer.cs` |
| `OpenGL/OpenGLRenderer.Initialization.cs` | `OpenGL/Bootstrap/OpenGLRenderer.Initialization.cs` |
| `OpenGL/OpenGLRenderer.Debug.cs` | `OpenGL/Bootstrap/OpenGLRenderer.Debug.cs` |
| `OpenGL/OpenGLRenderer.DebugTracking.cs` | `OpenGL/Frame/OpenGLRenderer.DebugTracking.cs` |
| `OpenGL/OpenGLRenderer.GpuFence.cs` | `OpenGL/Frame/OpenGLRenderer.GpuFence.cs` |
| `OpenGL/OpenGLRenderer.GpuStatsReadback.cs` | `OpenGL/Frame/OpenGLRenderer.GpuStatsReadback.cs` |
| `OpenGL/OpenGLRenderer.DrawSubmission.cs` | `OpenGL/Commands/OpenGLRenderer.DrawSubmission.cs` |
| `OpenGL/OpenGLRenderer.Blit.cs` | `OpenGL/Commands/OpenGLRenderer.Blit.cs` |
| `OpenGL/OpenGLRenderer.ReadbackCapture.cs` | `OpenGL/Commands/OpenGLRenderer.ReadbackCapture.cs` |
| `OpenGL/OpenGLRenderer.RenderParameters.cs` | `OpenGL/Commands/OpenGLRenderer.RenderParameters.cs` |
| `OpenGL/OpenGLRenderer.ClipSpace.cs` | `OpenGL/Commands/OpenGLRenderer.ClipSpace.cs` |
| `OpenGL/OpenGLRenderer.Framebuffer.cs` | `OpenGL/Resources/Framebuffers/OpenGLRenderer.Framebuffer.cs` |
| `OpenGL/OpenGLRenderer.Objects.cs` | `OpenGL/BackendObjects/OpenGLRenderer.Objects.cs` |
| `OpenGL/OpenGLRenderer.TextureBindings.cs` | `OpenGL/Descriptors/OpenGLRenderer.TextureBindings.cs` |
| `OpenGL/OpenGLRenderer.Bindless.cs` | `OpenGL/Features/Bindless/OpenGLRenderer.Bindless.cs` |
| `OpenGL/OpenGLRenderer.Uniforms.cs` | `OpenGL/Descriptors/OpenGLRenderer.Uniforms.cs` |
| `OpenGL/OpenGLRenderer.AsyncPrograms.cs` | `OpenGL/Pipelines/OpenGLRenderer.AsyncPrograms.cs` |
| `OpenGL/OpenGLRenderer.ParallelShaderCompile.cs` | `OpenGL/Pipelines/OpenGLRenderer.ParallelShaderCompile.cs` |
| `OpenGL/OpenGLRenderer.ProgramPool.cs` | `OpenGL/Pipelines/OpenGLRenderer.ProgramPool.cs` |
| `OpenGL/OpenGLShaderLinkBackendSelector.cs` | `OpenGL/Pipelines/OpenGLShaderLinkBackendSelector.cs` |
| `OpenGL/OpenGLRenderer.Luminance.cs` | `OpenGL/Features/Luminance/OpenGLRenderer.Luminance.cs` |
| `OpenGL/OpenGLRenderer.Meshlets.cs` | `OpenGL/Features/Meshlets/OpenGLRenderer.Meshlets.cs` |
| `OpenGL/OpenGLRenderer.SparseTextures.cs` | `OpenGL/Features/SparseTextures/OpenGLRenderer.SparseTextures.cs` |
| `OpenGL/OpenGLRenderer.DetailPreservingMipmaps.cs` | `OpenGL/Features/SparseTextures/OpenGLRenderer.DetailPreservingMipmaps.cs` |
| `OpenGL/OpenGLRenderer.TextureStreamingCacheCook.cs` | `OpenGL/Features/Streaming/OpenGLRenderer.TextureStreamingCacheCook.cs` |
| `OpenGL/OpenGLRenderer.ImGui.cs` | `OpenGL/UI/OpenGLRenderer.ImGui.cs` |
| `OpenGL/OpenGLRenderer.ImGuiViewports.cs` | `OpenGL/UI/OpenGLRenderer.ImGuiViewports.cs` |
| `OpenGL/Types/Buffers/*` | `OpenGL/BackendObjects/Buffers/*` |
| `OpenGL/Types/Textures/*` | `OpenGL/BackendObjects/Textures/*` |
| `OpenGL/Types/Render Targets/*` | `OpenGL/BackendObjects/Framebuffers/*` |
| `OpenGL/Types/Queries/*` | `OpenGL/BackendObjects/Queries/*` |
| `OpenGL/Types/Mesh Renderer/*` | `OpenGL/BackendObjects/MeshRendering/*` |
| `OpenGL/Types/Meshes/GLMaterial.cs` | `OpenGL/BackendObjects/Materials/GLMaterial.cs` |
| `OpenGL/Types/Meshes/GLShader.cs` | `OpenGL/BackendObjects/Programs/GLShader.cs` |
| `OpenGL/Types/Meshes/GLRenderProgram*.cs` | `OpenGL/BackendObjects/Programs/GLRenderProgram*.cs` |
| `OpenGL/Types/Meshes/GLShader*.cs` | `OpenGL/Shaders/GLShader*.cs` if not object-owned |
| `OpenGL/Types/Meshes/ShaderProgramLifecycleDiagnostics.cs` | `OpenGL/Pipelines/ShaderProgramLifecycleDiagnostics.cs` |
| `OpenGL/Types/GLProgramCompileLinkQueue.cs` | `OpenGL/Pipelines/GLProgramCompileLinkQueue.cs` |
| `OpenGL/Types/GLProgramBinaryUploadQueue.cs` | `OpenGL/Pipelines/GLProgramBinaryUploadQueue.cs` |
| `OpenGL/Types/GLMeshGenerationQueue.cs` | `OpenGL/Commands/GLMeshGenerationQueue.cs` |
| `OpenGL/Types/GLSharedContext.cs` | `OpenGL/Bootstrap/GLSharedContext.cs` |
| `OpenGL/Types/GLObject*.cs` | `OpenGL/BackendObjects/GLObject*.cs` |
| `OpenGL/Types/IGLObject.cs` | `OpenGL/BackendObjects/IGLObject.cs` |
| `OpenGL/Enums/*` | `OpenGL/Types/*` |

## Naming Rules

- Partial renderer files should use the owning renderer name plus a clear
  responsibility:
  - `VulkanRenderer.FrameLoop.cs`
  - `VulkanRenderer.CommandBuffers.cs`
  - `OpenGLRenderer.DrawSubmission.cs`
  - `OpenGLRenderer.TextureBindings.cs`
- Avoid generic names such as `Objects`, `Types`, `Helpers`, `Misc`, and
  `Utilities` for folders that contain major runtime behavior.
- Remove spaces from folder names during the OpenGL move:
  - `Mesh Renderer` -> `MeshRendering`
  - `Render Targets` -> `Framebuffers`
- Prefer `Framebuffers` over `Render Targets` in backend wrapper folders,
  because both backends expose framebuffer/renderbuffer wrappers there.
- Prefer `Pipelines` for API pipeline/program compile/cache infrastructure.
- Prefer `Shaders` for source rewriting, compatibility helpers, reflection,
  and compiler/linker-facing code.
- Keep backend-specific optional feature code under `Features/<FeatureName>/`.
- Keep generated, audit, and completed TODO path references updated only when
  they are likely to be used as living docs. Do not churn archived evidence
  files purely for path freshness unless a task specifically requires it.

## Phase 0 - Baseline Audit And Branch

- [ ] Create the dedicated branch `renderer-backend-folder-organization`.
- [ ] Confirm no in-flight renderer behavior work is sharing the same files, or
  coordinate merge order before moving large hot files.
- [ ] Capture current file inventory for both backend folders with `rg --files`.
- [ ] Capture current large-file list for both backend folders.
- [ ] Confirm all `.csproj`, `.props`, `.targets`, scripts, and docs that
  reference explicit backend paths.
- [ ] Note explicit project file entries before moving:
  - `Compile Remove="Rendering\API\Rendering\Vulkan\VulkanRaytracing.cs"`
  - `None Include="Rendering\API\Rendering\Vulkan\VulkanRaytracing.cs"`
- [ ] Decide whether the move is one PR per backend or one combined PR. Prefer
  one combined move-only PR if the taxonomy must stay mirrored.
- [ ] Run a baseline build before moving files:

  ```powershell
  dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj
  ```

## Phase 1 - Add Folder Contract Documentation

- [ ] Add `README.md` files to the new Vulkan top-level folders explaining
  ownership and examples.
- [ ] Add `README.md` files to the new OpenGL top-level folders explaining
  ownership and examples.
- [ ] Update `docs/architecture/rendering/code-map.md` with this backend
  taxonomy.
- [ ] Add an old-to-new backend folder mapping table to
  `docs/architecture/rendering/code-map.md`.
- [ ] Document that namespaces intentionally remain unchanged during the first
  move-only pass.
- [ ] Document that path changes should be reviewed as move-only diffs before
  any semantic refactor.

## Phase 2 - Vulkan Move-Only Reorganization

- [ ] Create the Vulkan target folder tree.
- [ ] Move Vulkan bootstrap files into `Vulkan/Bootstrap/`.
- [ ] Move Vulkan frame-loop, swapchain, sync, timing, and retirement files into
  `Vulkan/Frame/`.
- [ ] Move command recording, blit, readback, indirect draw, command pool, and
  render-state files into `Vulkan/Commands/`.
- [ ] Move render-graph compiler, barrier planner, and resource planner files
  into `Vulkan/RenderGraph/`.
- [ ] Move allocator, staging, dynamic uniform ring, scene database, placeholder
  texture, memory, framebuffer, and texture resource files into
  `Vulkan/Resources/`.
- [ ] Move descriptor layout, descriptor pool, update template, contract,
  immutable sampler, and bindless material descriptor files into
  `Vulkan/Descriptors/`.
- [ ] Move pipeline cache, compile queue, prewarm DB, render-pass, graphics
  pipeline, render target mode, and GPL cache files into `Vulkan/Pipelines/`.
- [ ] Move shader tooling and shader artifact cache files into
  `Vulkan/Shaders/`.
- [ ] Move auto-exposure, meshlet, raytracing, RTX IO, streaming, Streamline,
  and upscale bridge files into `Vulkan/Features/`.
- [ ] Move ImGui backend into `Vulkan/UI/`.
- [ ] Move Vulkan API wrapper files out of `Objects/Types/` into
  `Vulkan/BackendObjects/`.
- [ ] Move remaining small enums/interop structs into `Vulkan/Types/`.
- [ ] Update `XREngine.Runtime.Rendering.csproj` entries for
  `VulkanRaytracing.cs` after it moves.
- [ ] Build after the move-only pass.
- [ ] Fix path-sensitive doc links only where required by the build or current
  developer docs.

## Phase 3 - OpenGL Move-Only Reorganization

- [ ] Create the OpenGL target folder tree.
- [ ] Move OpenGL startup/context/debug initialization files into
  `OpenGL/Bootstrap/`.
- [ ] Move frame-adjacent debug tracking, fences, and GPU stats readback into
  `OpenGL/Frame/`.
- [ ] Move draw submission, blit, readback capture, render parameters,
  clip-space, and mesh generation queue files into `OpenGL/Commands/`.
- [ ] Move framebuffer renderer partials and framebuffer wrappers into
  `OpenGL/Resources/Framebuffers/` or `OpenGL/BackendObjects/Framebuffers/`
  according to whether the file is renderer orchestration or an object wrapper.
- [ ] Move GL data buffers, upload queues, and buffer views into
  `OpenGL/BackendObjects/Buffers/` or `OpenGL/Resources/Uploads/` according to
  ownership.
- [ ] Move texture wrappers into `OpenGL/BackendObjects/Textures/`.
- [ ] Move sampler wrappers into `OpenGL/BackendObjects/Samplers/` if split
  from texture wrappers later.
- [ ] Move query wrappers into `OpenGL/BackendObjects/Queries/`.
- [ ] Move mesh renderer files from `Types/Mesh Renderer/` to
  `OpenGL/BackendObjects/MeshRendering/`.
- [ ] Move material, shader, render program, and render program pipeline wrappers
  from `Types/Meshes/` to `OpenGL/BackendObjects/Programs/` and
  `OpenGL/BackendObjects/Materials/`.
- [ ] Move shader source compatibility and attribute layout resolver files into
  `OpenGL/Shaders/` if they are not tightly owned by `GLRenderProgram`.
- [ ] Move program compile/link queues, program pool, shader link backend
  selector, and shader lifecycle diagnostics into `OpenGL/Pipelines/`.
- [ ] Move bindless, meshlet, sparse texture, texture streaming, and
  detail-preserving mipmap files into `OpenGL/Features/`.
- [ ] Move ImGui files into `OpenGL/UI/`.
- [ ] Move existing `OpenGL/Enums/*` into `OpenGL/Types/`.
- [ ] Remove the old folders with spaces after all files are moved.
- [ ] Build after the OpenGL move-only pass.
- [ ] Update current OpenGL developer docs that link to moved files.

## Phase 4 - Split Large Vulkan Files

Do this after move-only validation so line-level diffs remain reviewable.

- [ ] Split `Vulkan/Commands/VulkanRenderer.CommandBuffers.cs` into focused
  partial files:
  - `VulkanRenderer.CommandBufferState.cs`
  - `VulkanRenderer.CommandBufferAllocation.cs`
  - `VulkanRenderer.CommandBufferRecording.cs`
  - `VulkanRenderer.SecondaryCommandBuffers.cs`
  - `VulkanRenderer.OneTimeSubmit.cs`
  - `VulkanRenderer.FrameOpSignatures.cs`
  - `VulkanRenderer.FrameOpDiagnostics.cs`
- [ ] Keep frame-op signature code close to the current
  `vulkan-frame-loop-performance-todo.md` work so future command-buffer cache
  changes are easy to locate.
- [ ] Split `Vulkan/Shaders/VulkanShaderTools.cs` into:
  - `VulkanShaderAutoUniforms.cs`
  - `VulkanShaderCompiler.cs`
  - `VulkanShaderReflection.cs`
  - `VulkanShaderTransformFeedback.cs`
  - `VulkanShaderSourceFixups.cs`
  - shared records in `VulkanShaderTypes.cs` if useful.
- [ ] Split `Vulkan/Commands` or `Vulkan/RenderGraph`
  `VulkanRenderer.State.cs` responsibilities:
  - state tracker fields and getters
  - render-state mutation
  - resource planner signature and refresh logic
  - command-buffer dirty reason tracking
  - queue-overlap policy and diagnostics
  - framebuffer/resource registration helpers
- [ ] Split `Vulkan/Resources/VulkanResourceAllocator.cs` if resource allocator
  review remains difficult:
  - image alias planning
  - buffer alias planning
  - physical image group allocation
  - physical buffer group allocation
  - usage inference
  - diagnostics.
- [ ] Rebuild after each large-file split.
- [ ] Prefer no logic changes during these splits except trivial access
  modifier adjustments needed by partial files.

## Phase 5 - Split Large OpenGL Files

Do this after OpenGL move-only validation.

- [ ] Split `OpenGL/BackendObjects/Programs/GLRenderProgram.Linking.cs` into
  link orchestration, compile input preparation, binary cache interaction,
  async result consumption, hazard detection, and diagnostics files.
- [ ] Keep the developer guide
  `docs/developer-guides/rendering/opengl-program-linking.md` in sync with the
  new file names.
- [ ] Split `OpenGL/UI/OpenGLRenderer.ImGuiViewports.cs` into:
  - platform viewport lifecycle
  - GL context handling
  - render target setup
  - draw data submission
  - diagnostics.
- [ ] Split `OpenGL/Features/Luminance/OpenGLRenderer.Luminance.cs` into:
  - luminance resources
  - compute/downsample dispatch
  - readback
  - diagnostics.
- [ ] Review `GLDataBuffer.cs`, `GLTexture2D.Upload.cs`, and
  `GLMeshRenderer.Shaders.cs` for future splits only if follow-up work is
  touching those areas.
- [ ] Rebuild after each large-file split.
- [ ] Keep OpenGL program-linking behavior and async compile behavior unchanged
  during the split.

## Phase 6 - Normalize Backend Object Wrappers

- [ ] Ensure OpenGL and Vulkan wrapper folder names align:
  - `BackendObjects/Buffers`
  - `BackendObjects/Textures`
  - `BackendObjects/Framebuffers`
  - `BackendObjects/Materials`
  - `BackendObjects/MeshRendering`
  - `BackendObjects/Programs`
  - `BackendObjects/Queries`
  - `BackendObjects/Samplers`
- [ ] Keep the existing no-standalone-backend-`XRMesh` rule from
  `docs/architecture/rendering/code-map.md`.
- [ ] Confirm `GLMeshRenderer` and `VkMeshRenderer` remain the owners of mesh
  draw readiness, mesh data invalidation, buffer collection, and draw
  submission.
- [ ] Confirm `GLDataBuffer` and `VkDataBuffer` remain the owners of backend
  buffer upload/readiness state.
- [ ] Do not force exact file parity between OpenGL and Vulkan when the APIs
  have real lifecycle differences.
- [ ] Add small folder README notes where parity is intentionally asymmetric.

## Phase 7 - Docs And Link Updates

- [ ] Update `docs/architecture/rendering/vulkan-renderer.md` source inventory
  and folder map.
- [ ] Update `docs/architecture/rendering/opengl-renderer.md` source inventory
  and folder map.
- [ ] Update `docs/architecture/rendering/code-map.md` old-to-new mapping table.
- [ ] Update `docs/developer-guides/rendering/opengl-program-linking.md` for
  moved OpenGL program files.
- [ ] Update `docs/developer-guides/rendering/vulkan-upscale-bridge.md` for
  moved upscale bridge files.
- [ ] Update active TODOs that reference backend paths, especially:
  - `docs/work/todo/rendering/vulkan-frame-loop-performance-todo.md`
  - `docs/work/todo/rendering/vulkan-dynamic-rendering-migration-todo.md`
  - `docs/work/todo/rendering/resolved-shader-source-optimization-todo.md`
  - `docs/work/todo/rendering/render-pipeline-resource-lifecycle-todo.md`
  - `docs/work/todo/rendering/render-settings-api-separation-refactor-todo.md`
- [ ] Leave completed historical TODOs alone unless stale links block current
  work.
- [ ] Regenerate any docs that depend on source path inventories if a generator
  exists.

## Phase 8 - Validation

- [ ] Build runtime rendering:

  ```powershell
  dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj
  ```

- [ ] Build editor:

  ```powershell
  dotnet build .\XREngine.Editor\XREngine.Editor.csproj
  ```

- [ ] Run targeted tests that cover backend wrapper/resource contracts.
- [ ] Run OpenGL editor startup smoke test if available locally.
- [ ] Run Vulkan editor startup smoke test if available locally.
- [ ] For Vulkan move/split phases, run the narrow Vulkan resource lifecycle or
  frame-loop tests that cover touched files.
- [ ] For OpenGL move/split phases, run shader-linking and program-cache tests
  if available.
- [ ] Inspect build warnings. New code should not introduce warnings.
- [ ] If warnings exist before the move, record them as pre-existing and ensure
  the reorg did not add new ones.

## Phase 9 - Cleanup

- [ ] Delete empty old folders after all references are updated:
  - `Vulkan/Objects/`
  - `Vulkan/Objects/Types/`
  - `OpenGL/Types/Mesh Renderer/`
  - `OpenGL/Types/Render Targets/`
  - any other empty transitional folders.
- [ ] Remove temporary path mapping notes once the new structure has stabilized.
- [ ] Confirm IDE tabs and solution explorer show sensible folder grouping.
- [ ] Confirm `rg --files` under each backend shows the expected taxonomy.
- [ ] Merge the branch back into `main` after completion and validation.

## Review Strategy

- [ ] Prefer one move-only commit for Vulkan.
- [ ] Prefer one move-only commit for OpenGL.
- [ ] Prefer separate commits for large-file splits.
- [ ] Prefer separate commits for docs/link updates if they are noisy.
- [ ] In PR description, include:
  - what moved
  - why the backend taxonomy was chosen
  - what did not change behaviorally
  - validation performed
  - known stale archived links, if any were intentionally left untouched.

## Open Questions

- [ ] Should `VulkanRenderer.State.cs` live under `Commands/`,
  `RenderGraph/`, or be split across both? Current contents span render-state,
  resource planning, command-buffer invalidation, and queue-overlap policy.
- [ ] Should OpenGL `GLRenderProgram*` wrappers live fully under
  `BackendObjects/Programs/`, or should source compatibility and link strategy
  helpers move into `Shaders/` and `Pipelines/`?
- [ ] Should OpenGL `Luminance` be treated as a feature, a readback path, or a
  render-pipeline support utility?
- [ ] Should `BackendObjects` be named `Objects` after cleanup, or is the
  explicit name useful enough to avoid returning to ambiguity?
- [ ] Should backend folder README files be kept permanently, or replaced by the
  architecture docs once contributors have adjusted?
