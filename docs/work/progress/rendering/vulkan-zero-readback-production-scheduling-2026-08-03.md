# Vulkan Zero-Readback Production Scheduling Progress

Last Updated: 2026-08-03
Owner: Rendering / Vulkan Command Scheduling and GPU Indirect Submission
Status: Paused at requested handoff; scheduling and base residency slices build, material and motion-vector integration is incomplete and unvalidated

Related investigation:

- [Vulkan camera-motion frame-rate regression](../../investigations/rendering/vulkan-camera-motion-framerate-regression-2026-07-21.md)

## Objective

Implement the production recording schedule for camera-moving frames without
discarding reusable command-chain work, and make
`GpuIndirectZeroReadback` fully GPU-resident across the mesh render paths. In
that strategy, MaskedForward must participate in generated indirect rendering;
mesh submission must not fall back to CPU direct draw calls or GPU-to-CPU
readbacks.

## Baseline Evidence

The pre-change Vulkan trace showed that camera movement was still dominated by
CPU-direct mesh recording:

- An earlier complete sample contained 836 direct `MeshDrawOp` records and
  reported MaskedForward as skipped because no generated layout existed.
- A later bounded 512-operation snapshot contained 336 direct motion-vector
  draws in `RenderMotionVectors_VelocityFBO`, 32 direct prepass draws in
  `PreRender` / `ForwardDepthPrePassMergeFBO`, and only two indirect draws in
  OpaqueDeferred.
- MaskedForward published compute and barrier work but no indirect draw.
- Command chains were globally quarantined for this workload: zero chains were
  scheduled, one fresh primary was recorded, and five secondary command
  buffers appeared without stable chain scheduling.
- That bounded sample reported no Vulkan validation errors, frame drops, or
  device loss. It is scheduling evidence, not proof that the visual artifact
  is resolved.

The top-left/black-padding camera-motion artifact has not been rechecked against
the edited code. It must not be considered fixed until the new binaries are
run, the camera is moved from multiple positions, and the captured images are
visually inspected.

## Completed And Build-Validated Work

### Mixed production command-chain schedule

The scheduler no longer disables command chains merely because the frame uses
zero-readback submission or contains mutable GPU-driven frame operations.
Instead, it represents the mixed recording contract explicitly:

- stable mesh packets remain schedule groups eligible for reusable secondary
  command buffers;
- mutable GPU publication and indirect dispatch operations remain inline in a
  freshly recorded primary;
- the schedule carries `RequiresFreshPrimary` and `InlineFrameOpCount`;
- schedule, primary-identity, and reuse signatures include the mixed-work
  metadata; and
- camera-generation changes no longer invalidate reusable secondary work when
  all scheduled work is secondary, while a mixed schedule visibly forces a
  fresh primary with reason `command-chain-inline-publication`.

This slice built successfully:

```powershell
dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj --no-restore
```

Result: zero warnings and zero errors. The first sandboxed attempt reached an
MSVC `FileTracker` access-denied condition; the identical approved build
outside that restriction succeeded.

### Base zero-readback mesh-residency contract

The runtime path was changed so zero-readback treats every published mesh
workload as GPU-owned:

- CPU callbacks may still execute for non-mesh commands, but CPU mesh draws are
  suppressed in traditional, meshlet, forward depth/normal, and full-overdraw
  paths.
- The OpenGL warmup/safety-net CPU mesh fallbacks are disabled for the strict
  zero-readback strategy.
- Commands marked `ForceCpu` or `ExcludeFromGpuIndirect` are still registered
  with `GPUScene`; they receive a `CpuFallbackOnly` GPU flag instead of being
  removed from GPU publication.
- Non-zero-readback strategies cull `CpuFallbackOnly` commands. Strict
  zero-readback clears that disabled-flags mask so those commands remain
  GPU-resident.
- GPU eligibility checks no longer cause the zero-readback path to route a
  published mesh workload back through CPU direct rendering.

This slice built successfully before the later material/motion edits:

```powershell
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore
```

Result: zero errors and one pre-existing unrelated nullable warning at
`RendererHostContext.cs(78,20)` (`CS8603`).

## Incomplete Work In The Current Worktree

### MaskedForward generated material path

The material table now has an `AlphaCutoff` word and the layout generator knows
the `alphacutoff` semantic. OpaqueDeferred was extended with that field, and
generated layouts were started for ForwardOpaque and MaskedForward. Material
publication now writes the per-material cutoff.

The generated forward fragment program is currently only a base-color output.
It does not yet implement the production forward-lighting contract. Before
calling MaskedForward complete, either integrate the real forward lighting
inputs and shading behavior or explicitly decide that this generated path is
intentionally unlit. Material-specific alpha testing must remain intact in
either case.

### GPU motion-vector variant

A render-state variant was started so the motion-vector pass can use generated
GPU indirect material-table dispatch. The path begins binding previous
transforms and publishing current/previous view-projection uniforms, and the
default command chain now permits GPU render dispatch for Velocity.

This edit is not compile-complete. `HybridRenderingManager.cs` currently calls
`AppendMaterialTableTransformLoader(...)`, but that helper has not been
defined. No build was run after these material and motion-vector edits, so
additional compiler errors may remain. Desktop matrix handling was the active
design target; stereo/multiview behavior has not been implemented or validated.

The meshlet material-table shader also retains its separate alpha-cutoff path
and hard-coded fallback behavior. It still needs parity review.

## Important Scope Boundaries And Risks

- `RenderCPUNonMeshOnly` prevents CPU mesh draws, but intentionally still runs
  non-mesh callbacks. If “no CPU direct render calls” is meant to prohibit all
  CPU-authored callbacks, UI, or explicit `CpuDirect` passes, that requires a
  broader render-command architecture change.
- Authored `PreRender`, `PostRender`, and `OnTopForward` CPU-direct command
  routes have not been eliminated. The implemented strict contract currently
  applies to mesh fallback inside GPU-owned passes.
- Non-triangle or atlas-incompatible meshes can still fail GPU-scene
  registration. Zero-readback will not silently draw them on the CPU; the
  production path needs visible diagnostics and an explicit import/conversion
  contract for unsupported geometry.
- The current zero-readback eligibility check assumes a published mesh command
  belongs to the GPU path even if a later registration step rejects it. Live
  diagnostics must compare published, registered, culled, and drawn counts.
- No readback audit has yet proven that all zero-readback branches avoid
  `GetData`, `ReadUIntAt`, waits, or diagnostics that synchronize with the CPU.
- The current dirty worktree contains unrelated user changes. They have been
  preserved and must not be reverted during continuation.

## Files Changed So Far

Command scheduling:

- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Planning/CommandChainSchedule.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Planning/VulkanRenderer.CommandChains.Planning.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Signatures/VulkanRenderer.CommandChains.Signatures.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Lifecycle/VulkanRenderer.CommandBufferLifecycle.Reuse.cs`

Zero-readback residency:

- `XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Traditional/VPRC_RenderMeshesPassTraditional.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Meshlet/VPRC_RenderMeshesPassMeshlet.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_ForwardDepthNormalPrePass.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_RenderFullOverdrawPass.cs`
- `XREngine.Runtime.Rendering/Commands/GPUIndirectRenderCommand.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.AddRemove.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.CommandConversion.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.CullingAndSoA.cs`

Material, MaskedForward, and motion work in progress:

- `XREngine.Runtime.Rendering/Rendering/Materials/GPUMaterialEntry.cs`
- `XREngine.Runtime.Rendering/Rendering/Materials/GPUMaterialTable.GPUMaterialEntryWords.cs`
- `XREngine.Runtime.Rendering/Rendering/Materials/GPUMaterialTable.cs`
- `XREngine.Runtime.Rendering/Rendering/Materials/MaterialBindingLayout.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/RenderingState.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_RenderMotionVectorsPass.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs`
- `XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs`

## Next Steps

1. Finish the current compile slice.
   - Define or replace `AppendMaterialTableTransformLoader(...)` so generated
     shaders load current and previous model transforms consistently.
   - Build the runtime, Vulkan backend, and editor; fix only task-related
     errors and warnings.
2. Finish the generated MaskedForward contract.
   - Preserve per-material alpha cutoff.
   - Integrate production forward lighting, or document and approve a narrower
     unlit contract.
   - Review the meshlet path for the same material and cutoff behavior.
3. Finish motion-vector GPU dispatch.
   - Verify previous-transform descriptor binding and current/previous temporal
     matrices.
   - Add stereo/multiview handling.
   - Validate shader-stage declarations and descriptor bindings on Vulkan.
4. Audit strict zero-readback behavior in source.
   - Search zero-readback branches for `ReadUIntAt`, `GetData`, `Readback`,
     `WaitForGpu`, and CPU mesh rendering helpers.
   - Keep diagnostic synchronization disabled in production and make any
     unsupported GPU-resident workload fail visibly rather than silently
     falling back.
5. Run the live isolated Vulkan path.
   - Force `GpuIndirectZeroReadback`, enable Vulkan command chains, Standard
     Validation, command-buffer labels, and frame-op tracing.
   - Move the camera to at least two distinct views through MCP.
   - Capture and visually inspect screenshots rather than trusting successful
     tool responses.
   - Inspect the named session logs and frame trace after shutdown.
6. Use RenderDoc if screenshots and logs do not identify a remaining artifact.
   - `rdc doctor` already passed on this machine.
   - Export and inspect Velocity, forward depth/normal, MaskedForward, and final
     post-process targets, plus the relevant pipeline and descriptor bindings.
7. Update the related investigation with live results. Do not add or modify
   tests until the feature works through the runtime path and the user
   explicitly clears test work, per repository policy.

## Live Acceptance Criteria

- No direct mesh `MeshDrawOp` records in motion vectors, forward depth/normal,
  OpaqueDeferred, OpaqueForward, or MaskedForward while using
  `GpuIndirectZeroReadback`.
- MaskedForward records a generated indirect draw and emits no skipped-layout
  warning.
- The camera-moving frame reports scheduled command chains, a fresh primary
  for inline publication, and reusable secondaries after warmup.
- No CPU readback, CPU mesh fallback, Vulkan validation error, device loss, or
  watchdog timeout occurs.
- Viewport captures remain full-resolution with no top-left image and black
  bottom/right padding after camera movement.
- Motion vectors and masked materials are visually correct from more than one
  camera position.

## Session State At Handoff

The owned isolated editor session `vulkan-state-schedule-20260803` was stopped
cleanly before the incomplete material/motion edits. Its baseline logs are
under:

`Build/_AgentValidation/mcp-sessions/vulkan-state-schedule-20260803/logs/`

No editor session or RenderDoc session was left running by this work.
