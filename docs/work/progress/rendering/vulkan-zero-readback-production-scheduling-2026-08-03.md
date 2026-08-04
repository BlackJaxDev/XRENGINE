# Vulkan Zero-Readback Production Scheduling Progress

Last Updated: 2026-08-04
Owner: Rendering / Vulkan Command Scheduling and GPU Indirect Submission
Status: The former next steps 1-3 are implemented and compile-validated. The
generated MaskedForward and motion-vector paths now have production shader
contracts, including stereo/multiview handling and render-frame transform
history. Clean live acceptance is still pending because the bounded editor run
exposed a Vulkan descriptor-layout defect and then a native ImGui assertion.
Both defects are fixed in source and build cleanly, but the editor was stopped
and was not relaunched in this task.

This work is separate from the active
[Directional Light Vulkan Stability Investigation](../../investigations/rendering/directional-light-inspector-shadow-2026-08-03.md).

Related investigation:

- [Vulkan camera-motion frame-rate regression](../../investigations/rendering/archive/vulkan-camera-motion-framerate-regression-2026-07-21.md)

## Objective

Implement the production recording schedule for camera-moving frames without
discarding reusable command-chain work, and make
`GpuIndirectZeroReadback` fully GPU-resident across mesh render paths. In this
strategy, MaskedForward participates in generated indirect rendering; mesh
submission must not fall back to CPU direct draws or GPU-to-CPU readbacks.

## Baseline Evidence

The pre-change Vulkan trace showed camera movement dominated by CPU-direct mesh
recording:

- An earlier complete sample contained 836 direct `MeshDrawOp` records and
  reported MaskedForward as skipped because no generated layout existed.
- A bounded 512-operation snapshot contained 336 direct motion-vector draws in
  `RenderMotionVectors_VelocityFBO`, 32 direct prepass draws in `PreRender` /
  `ForwardDepthPrePassMergeFBO`, and only two indirect OpaqueDeferred draws.
- MaskedForward published compute and barrier work but no indirect draw.
- Command chains were globally quarantined: zero chains were scheduled, one
  fresh primary was recorded, and five secondary command buffers appeared
  without stable chain scheduling.

The top-left/black-padding artifact has not been rechecked against the current
binaries. It must not be considered fixed until the camera is moved through
multiple views and captures are inspected visually.

## Completed And Build-Validated Work

### Mixed production command-chain schedule

The scheduler no longer disables command chains merely because a frame uses
zero-readback submission or contains mutable GPU-driven operations.

- Stable mesh packets remain eligible for reusable secondary command buffers.
- Mutable publication and indirect dispatch remain inline in a fresh primary.
- The schedule carries `RequiresFreshPrimary` and `InlineFrameOpCount`.
- Schedule, primary-identity, and reuse signatures include the mixed-work
  metadata.
- Camera-generation changes no longer invalidate reusable secondary work when
  all scheduled work is secondary. A mixed schedule visibly forces a fresh
  primary with reason `command-chain-inline-publication`.

### Base zero-readback mesh-residency contract

Strict zero-readback now treats every published mesh workload as GPU-owned.

- CPU callbacks may still execute for non-mesh commands, but CPU mesh draws are
  suppressed in traditional, meshlet, forward depth/normal, and full-overdraw
  paths.
- OpenGL warmup and safety-net CPU mesh fallbacks are disabled for the strict
  strategy.
- Commands marked `ForceCpu` or `ExcludeFromGpuIndirect` are still registered
  with `GPUScene`; they receive `CpuFallbackOnly` instead of being removed from
  GPU publication.
- Non-zero-readback strategies cull `CpuFallbackOnly`. Strict zero-readback
  clears that disabled-flags mask so those commands remain GPU-resident.
- GPU eligibility checks no longer route a published strict-mode mesh workload
  back through CPU direct rendering.

### Render-frame transform history

`GPUScene` now publishes current and previous transforms at render snapshot
boundaries instead of treating update-thread writes as rendered history.

- The previous render buffer is copied from the last current render snapshot
  before the new current snapshot is published.
- Multiple update-thread writes between rendered frames collapse into one
  rendered-frame motion delta.
- A prior dirty range is copied for one additional quiet frame so an object
  that stops moving stops emitting stale velocity.
- Newly published transforms initialize `previous = current`, using sorted,
  coalesced copy ranges rather than one copy per transform.
- The redundant update-side previous-transform buffer and dirty range were
  removed.

### Generated MaskedForward and OpaqueForward material-table shading

The generated forward path is no longer an unlit base-color placeholder.

- ForwardOpaque and MaskedForward retain the material table's per-material
  `AlphaCutoff`.
- Albedo, tangent-space normal, and metallic/roughness/AO texture references
  are sampled from the GPU-resident material table.
- The shared `ForwardLighting.glsl` snippet now exposes
  `XRENGINE_CalculateForwardLightingMaterial(...)`, accepting per-material
  roughness, metallic, specular, emission, and ambient occlusion while reusing
  the production light, probe, Forward+, shadow, and ambient PBR loops.
- Generated forward shaders bind the real forward-lighting state and use
  dedicated scene SSBO bindings.
- The direct meshlet material-table shader is limited to OpaqueDeferred because
  its current fragment output is a deferred MRT contract. It no longer
  advertises incompatible forward framebuffer support. Its material cutoff,
  normal map, and metallic/roughness sampling were brought into parity for the
  supported deferred path.

### Generated GPU motion vectors

The motion-vector pass now owns a complete generated indirect shader variant.

- The vertex shader reads current and previous GPUScene transform snapshots and
  emits current/previous clip positions to the fragment stage.
- The fragment shader calculates velocity directly from those clip positions;
  it no longer reconstructs transforms with incomplete fragment-stage state.
- Current and previous unjittered temporal view-projection matrices are used.
- Desktop, Vulkan `GL_EXT_multiview`, and OpenGL OVR multiview variants publish
  per-eye current/previous matrices and select them with the active view index.
- The generated-program cache key includes the stereo multiview variant.

### Vulkan bindless descriptor-tier correction

The bounded runtime compile reached the generated forward and motion programs,
then validation reported
`VUID-VkDescriptorSetLayoutBindingFlagsCreateInfo-pBindingFlags-03004`: the
variable bindless array was not the highest binding because fixed
forward-lighting resources had been rewritten into the same material set.

The production layout is now explicit:

- Descriptor set 2 is the shared material tier and contains only
  `XR_BindlessMaterialTextures` at binding 31.
- Forward-lighting samplers and SSBOs are pass-owned resources in descriptor
  set 3. A qualifier macro preserves OpenGL `layout(binding=...)` syntax while
  emitting Vulkan `layout(set=3, binding=...)` declarations that remain visible
  to source optimization and binding reflection.
- The global table layout declares the full 4096-entry runtime-array maximum;
  descriptor allocation supplies the smaller device-clamped live count through
  Vulkan's variable descriptor count.
- Before binding the global table, the backend requires the program's material
  layout to be the exact cached layout used by the shared table. An incompatible
  shader now skips visibly with a diagnostic instead of issuing an invalid
  Vulkan bind or silently falling back to CPU rendering.

### Native ImGui assertion correction

The bounded editor run also displayed the cimgui assertion:

`font->ContainerAtlas->TexID == _CmdHeader.TextureId`

The Vulkan backend previously built the font atlas with texture ID 0, began UI
frames, and changed the atlas to reserved ID 1 later during GPU font-resource
creation. That can change the atlas ID after an ImGui draw-list command header
has captured its texture ID. The backend now assigns ID 1 immediately after
building the atlas and before the first `ImGui.NewFrame()`; the render-resource
path no longer mutates it later.

## Validation Performed

No tests were added or run; repository policy requires the feature to pass its
live runtime path and the user to explicitly clear test work first.

Compile-only validation after all current edits:

```powershell
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore
dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj --no-restore
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore
```

Results:

- Rendering runtime: 0 warnings, 0 errors.
- Vulkan backend: 0 warnings, 0 errors.
- Editor integration build: 0 warnings, 0 errors.
- Targeted `git diff --check`: clean; only Git line-ending notices were emitted.
- The first sandboxed Vulkan build hit MSVC `FileTracker` access denied. The
  identical approved compile-only build outside that restriction succeeded.

The bounded editor session before the final descriptor and ImGui fixes reached
the Unit Testing World with Vulkan, `GpuIndirectZeroReadback`, bindless material
tables, and Standard Validation. It observed 396 viewport commands, including
361 OpaqueDeferred and 32 MaskedForward mesh commands, and queued these
generated shaders without a shader compiler error:

- `GPUIndirect_VulkanDescriptorIndexTableMaterialTableForwardFS`
- `GPUIndirect_VulkanDescriptorIndexTableMaterialTableMotionVectorsFS`
- `GPUIndirect_AutoVS`

That run is evidence that the generated variants were reached, not clean live
acceptance: it exposed the descriptor VUID above, and the native ImGui assertion
interrupted the session. The exact owned session was stopped and no editor or
helper process from this work remains running.

## Important Scope Boundaries And Risks

- `RenderCPUNonMeshOnly` intentionally permits non-mesh callbacks. If “no CPU
  direct render calls” is intended to prohibit all CPU-authored callbacks, UI,
  and explicit `CpuDirect` passes, that requires a broader command architecture
  change.
- Authored `PreRender`, `PostRender`, and `OnTopForward` CPU-direct command
  routes have not been eliminated. The strict contract currently covers mesh
  fallback inside GPU-owned passes.
- Non-triangle or atlas-incompatible meshes can still fail GPUScene
  registration. Strict mode does not silently render them on the CPU; live
  diagnostics must make unsupported geometry visible.
- A strict source audit has not yet proven every zero-readback branch avoids
  `GetData`, `ReadUIntAt`, waits, or diagnostic synchronization.
- Moving all Vulkan forward-lighting fixed resources to the per-pass tier is
  compile-validated but still needs a live descriptor-publication check across
  authored and generated forward programs.
- The top-left/black-padding artifact, masked output, and motion vectors remain
  visually unverified after the final source changes.
- The worktree contains unrelated user changes. They were preserved and must
  not be reverted during continuation.

## Files Changed For This Work

Command scheduling:

- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Planning/CommandChainSchedule.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Planning/VulkanRenderer.CommandChains.Planning.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Signatures/VulkanRenderer.CommandChains.Signatures.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Lifecycle/VulkanRenderer.CommandBufferLifecycle.Reuse.cs`

Zero-readback residency and material dispatch:

- `XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Traditional/VPRC_RenderMeshesPassTraditional.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Meshlet/VPRC_RenderMeshesPassMeshlet.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_ForwardDepthNormalPrePass.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_RenderFullOverdrawPass.cs`
- `XREngine.Runtime.Rendering/Commands/GPUIndirectRenderCommand.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.AddRemove.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.CommandConversion.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.CullingAndSoA.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs`

Forward material, motion, and transform history:

- `Build/CommonAssets/Shaders/Snippets/ForwardLighting.glsl`
- `XREngine.Runtime.Rendering/Rendering/Materials/GPUMaterialEntry.cs`
- `XREngine.Runtime.Rendering/Rendering/Materials/GPUMaterialTable.GPUMaterialEntryWords.cs`
- `XREngine.Runtime.Rendering/Rendering/Materials/GPUMaterialTable.cs`
- `XREngine.Runtime.Rendering/Rendering/Materials/MaterialBindingLayout.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.Soa.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.CommandBuffers.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.Lifecycle.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/RenderingState.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_RenderMotionVectorsPass.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs`
- `XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs`

Vulkan descriptor and ImGui corrections:

- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanBindlessMaterialDescriptors.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.BindlessMaterialTextureTable.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/UI/VulkanImGuiBackend.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/UI/VulkanRenderer.ImGui.Resources.cs`

## Next Steps

1. Audit strict zero-readback behavior in source.
   - Search strict-mode branches for `ReadUIntAt`, `GetData`, `Readback`,
     `WaitForGpu`, and CPU mesh-render helpers.
   - Keep diagnostic synchronization disabled in production and make any
     unsupported GPU-resident workload fail visibly.
2. Run a fresh isolated Vulkan live-validation session when GUI execution is
   acceptable again.
   - Force `GpuIndirectZeroReadback`, Vulkan command chains, Standard
     Validation, command-buffer labels, and frame-op tracing.
   - Confirm generated forward and motion programs link without descriptor
     validation errors and the ImGui assertion no longer occurs.
   - Move the camera to at least two distinct views and visually inspect
     captures rather than trusting successful tool responses.
   - Verify MaskedForward, Velocity, command scheduling/reuse, and the absence
     of direct mesh draws or readbacks.
3. Use RenderDoc if screenshots and logs do not identify a remaining artifact.
   - `rdc doctor` passed on this machine.
   - Export Velocity, forward depth/normal, MaskedForward, and final
     post-process targets, then inspect their pipeline and descriptor bindings.
4. Update the related investigation with live results. Do not add or modify
   tests until the runtime path works and the user explicitly clears test work.

## Live Acceptance Criteria

- No direct mesh `MeshDrawOp` records in motion vectors, forward depth/normal,
  OpaqueDeferred, OpaqueForward, or MaskedForward under
  `GpuIndirectZeroReadback`.
- MaskedForward records a generated indirect draw with production lighting and
  per-material alpha cutoff, without a skipped-layout warning.
- Velocity uses render-frame previous transforms and produces correct desktop
  and stereo/multiview motion.
- Camera-moving frames schedule command chains, record a fresh primary for
  inline publication, and reuse stable secondaries after warmup.
- No CPU readback, CPU mesh fallback, Vulkan validation error, device loss,
  watchdog timeout, or ImGui native assertion occurs.
- Viewport captures remain full-resolution without a top-left image and black
  bottom/right padding after camera movement.
- Motion vectors and masked materials are visually correct from more than one
  camera position.

## Session State

The owned session `zr-generated-shaders-20260804` was stopped with
`Tools/Manage-McpEditorSession.ps1 Stop -Name zr-generated-shaders-20260804`.
Its evidence remains under:

`Build/_AgentValidation/mcp-sessions/zr-generated-shaders-20260804/`

The earlier owned session `vulkan-state-schedule-20260803` was also stopped.
No editor, ShaderEmitter helper, RenderDoc session, or other process launched by
this work remains running.
