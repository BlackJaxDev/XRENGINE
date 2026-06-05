# GPU Skinned Bounds Live Preview Issue

Status as of 2026-06-02: live cyan bounds fixed; compute-toggle propagation and bounds path split repaired.

This note summarizes the editor bug where skinned submesh bounds and GPU mesh BVH preview state were updating at the old throttled skinned-bounds cadence instead of every frame from the live GPU-skinned vertex data.

## Expected Behavior

When `Calculate Skinned Bounds In Compute Shader` is enabled and a runtime submesh has `Render Bounds` enabled:

- The cyan bounds preview should be generated from GPU-skinned vertex positions every rendered frame.
- The cyan bounds preview should not depend on CPU readback or the CPU-visible culling bounds refresh.
- The preview should use the same GPU-skinned pose that a GPU BVH refit would use.
- If the GPU path cannot provide bounds, the editor should make that failure visible with diagnostics, not silently draw a stale CPU box.
- For BVH preview, skinned GPU BVH refits should happen only when requested unless the debug UI explicitly disables request gating.

## Observed Behavior

The original editor behavior showed bounds that updated only about every 5 seconds.

Recent observed state:

- A cyan bounds box now renders from live GPU-skinned bounds in realtime when `Calculate Skinned Bounds In Compute Shader` is enabled.
- A green bounds box also rendered for a while and appeared to lag the cyan box by roughly one frame. That duplicate green path was removed from `RenderableMesh.BeforeAdd()`.
- The BVH hover/request gate now uses the same bounds source as the cyan bounds preview.
- When `Calculate Skinned Bounds In Compute Shader` is disabled, the cyan preview and BVH hover/request gate fall back to the static/CPU world bounds instead of forcing live GPU bounds.
- The editor checkbox did not initially affect runtime rendering because the runtime facade had local-only auto-properties for `CalculateSkinnedBoundsInComputeShader` and `SkinnedBoundsGpuDirectAabbWrite`; these now forward through the host rendering services.

The old 5-second cadence strongly suggested that a visible bounds path was still coupled to the throttled CPU-visible skinned culling-bounds refresh, or that the forced GPU path was reading pose data that only changed when that refresh dirtied or reseeded the relevant skinning state.

## Important Files

- `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.Debug.cs`
  - Direct mesh bounds debug drawing.
  - GPU skinned bounds debug renderer invocation.
  - Red diagnostic path when GPU bounds cannot render.

- `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.Skinning.cs`
  - CPU-visible skinned bounds refresh policy.
  - `SkinnedBoundsRefreshInterval` is 5 seconds.
  - `ProcessSkinnedBoundsRefresh()` still owns culling bounds and CPU-visible bounds state.

- `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.cs`
  - `BeforeAdd()` render-command collection callback.
  - Render bounds command registration.
  - Current code removed the collected mesh bounds draw call from this path.

- `XREngine.Runtime.Rendering/Rendering/Compute/SkinningPrepassDispatcher.cs`
  - Compute skinning dispatch.
  - Forced skinning entry point: `RunForGpuMeshBvh()`.
  - Live skinned bounds tail setup and dispatch.

- `XREngine.Runtime.Rendering/Rendering/Compute/SkinningPrepassDispatcher/SkinningPrepassDispatcher.RendererResources.cs`
  - Per-renderer skinned output and bounds buffer state.
  - Tail offsets for fused bounds in skinned output buffers.

- `Build/CommonAssets/Shaders/Compute/Animation/SkinningPrepass*.comp`
  - Compute skinning shaders.
  - Current experimental path writes live min/max bounds into reserved output-buffer tail records.

- `Build/CommonAssets/Shaders/Scene3D/RenderPipeline/skinned_bounds_debug_lines.comp`
  - GPU debug-line generation from a bounds buffer.

- `XREngine.Runtime.Rendering/Rendering/Compute/GpuBoundsDebugLineRenderer.cs`
  - CPU-side wrapper for GPU debug-line generation.

## What Has Been Tried

### 1. GPU BVH Preview Replaced CPU BVH Preview

The editor BVH preview was moved away from CPU traversal/rendering. The GPU preview uses a GPU line renderer and GPU BVH node buffers.

Outcome:

- This addressed the original CPU BVH preview performance/flicker direction.
- It did not by itself solve live skinned bounds freshness.

### 2. CPU Mesh BVH Generation/Render Path Removed From Preview

The preview path was changed to use GPU BVH data and avoid the old CPU mesh BVH generation/render path.

Outcome:

- The preview is no longer intended to fall back to CPU BVH drawing.
- This aligned with the repo rule now added to `AGENTS.md`: explicit GPU paths should not be hidden behind silent CPU fallbacks.

### 3. BVH Node Render Modes Added

The BVH preview gained render modes such as:

- Render all nodes.
- Highlight leaf nodes.
- Render only leaf nodes.
- Render only internal nodes.

Outcome:

- Useful preview UI improvement.
- Not directly related to the stale bounds cadence.

### 4. Request-Gated Skinned BVH Updates Added

Controls were added or renamed around:

- Only update BVH on request.
- Only render BVH on request.

The intent was that live skinned BVH refits only happen when the mouse/interactor intersects the submesh bounds, unless the user explicitly disables request gating.

Outcome:

- Hover/request behavior improved in earlier testing.
- Bounds freshness remained broken.

### 5. GPU Renderer Failure Was Made Visible

Silent CPU fallback behavior was removed or reduced. A red diagnostic point/text was added for cases where live GPU skinned bounds could not render.

Outcome:

- This made missing GPU bounds more observable in theory.
- In practice, the user later saw cyan and green bounds rather than the red failure diagnostic, meaning some bounds path was drawing something, but still stale.

### 6. Per-Submesh `Render Bounds` Toggle Was Fixed

`RenderableMesh.RenderBounds` is the local submesh toggle. `DoRenderBounds()` was previously gated only by global `EditorPreferences.Debug.RenderMesh3DBounds`, so the per-submesh checkbox could add the render command and then immediately no-op.

The direct bounds path was changed to use:

- `RenderBounds || debug.RenderMesh3DBounds`

Outcome:

- Bounds started rendering again.
- This did not solve the 5-second update cadence.

### 7. New SSBO Live Bounds Attempt Was Rejected

An earlier attempt added a separate live bounds SSBO to the skinning shaders. Non-interleaved skinning already used a packed binding layout, and the new binding was risky.

Observed result:

- Compute skinning started exploding.

Conclusion:

- Adding a new SSBO binding to the active skinning shaders was too risky for this path.
- The experiment was replaced with an output-tail approach.

### 8. Fused Bounds Were Moved Into Skinned Output Buffer Tails

The current intended design writes live bounds into unused tail records in the existing skinned output buffer:

- Non-interleaved output reserves two extra `vec4` records after `vertexCount`.
- Interleaved output reserves eight extra scalar words aligned to a `vec4` boundary.
- Skinning shaders reduce each workgroup in shared memory and atomically update min/max in the tail.
- `skinned_bounds_debug_lines.comp` reads from the configured tail offset.

Outcome:

- Shader validation passed for the modified skinning shaders.
- Runtime rendering build passed.
- Despite this, observed bounds still update only every 5 seconds.

### 9. Cross-Frame Compute Output Reuse Was Disabled For Live Bounds

The dispatcher previously allowed compute output reuse once the output and skinned bounds were marked valid. This could keep the bounds tail frozen until something dirtied the renderer.

The attempted fix changed `SkinningPrepassDispatcher` so live skinned bounds prevent cross-frame output reuse.

Outcome:

- Runtime rendering build passed.
- User still reports the same 5-second cadence.

### 10. Direct GPU Bounds Draw Forces A Prepass

The direct cyan bounds renderer was changed to call `SkinningPrepassDispatcher.Instance.RunForGpuMeshBvh(renderer)` before requesting the live GPU bounds buffer.

Intent:

- The bounds debug path should prepare the GPU-skinned output even when the main mesh draw path is vertex-shader skinning.
- The cyan bounds should not depend on the viewport's normal compute-skinning `RunVisible()` path.

Outcome:

- Runtime rendering build passed.
- User still reports the issue.

### 11. Forced Prepass Refreshes Skin Palette From Current Render Pose

The forced GPU bounds/BVH path was changed to call `renderer.RefreshBoneMatricesFromRenderState()` before dispatch when `forceSkinning && needsLiveSkinnedBounds`.

Intent:

- Avoid relying on dirty-bone events or cached skin palettes.
- Ensure the forced debug prepass skins from the current render pose every time.

Outcome:

- Runtime rendering build passed.
- Not yet verified by user at the time this note was written.

### 12. Collected Green Bounds Draw Was Removed From `BeforeAdd`

The extra green box appeared to come from a collected mesh-bounds debug path using `MeshBoundsContainedColor`.

The call to `QueueCollectedMeshBoundsDebug()` was removed from `RenderableMesh.BeforeAdd()`.

Intent:

- Leave the direct cyan GPU bounds command as the single bounds preview path for skinned compute bounds.
- Remove the duplicate green debug box.

Outcome:

- Runtime rendering build passed.
- User verified that the green box is gone.
- The remaining cyan box still updates only every 5 seconds.

### 13. Cyan Bounds Preview Was Decoupled From The Compute-Culling Setting

`RenderableMesh.Debug.cs` previously only used the GPU skinned bounds debug renderer when `CalculateSkinnedBoundsInComputeShader` was enabled. If that setting was disabled, stale, or not the same setting the editor UI implied, the cyan `Render Bounds` path could still silently draw the CPU-visible culling volume.

The debug path now uses the live GPU skinned bounds renderer for any skinned mesh where skinning is allowed. `SkinningPrepassDispatcher.RunForGpuMeshBvh()` also now forces live bounds writes when it forces skinning, instead of relying on `CalculateSkinnedBoundsInComputeShader`. The world-bounds query used by request/hover paths also forces that same prepass before reading the live GPU bounds.

Outcome:

- Runtime rendering build passed.
- User verified that the cyan bounds now update in realtime.
- This exposed that the compute-bounds setting could no longer disable live realtime bounds.

### 14. Bounds Source Was Re-Gated By The Compute-Bounds Setting

The final bounds-source contract is:

- If `CalculateSkinnedBoundsInComputeShader` is enabled, skinned mesh bounds preview uses live GPU-skinned bounds and GPU BVH hover/request tests use the last known GPU-skinned bounds.
- If `CalculateSkinnedBoundsInComputeShader` is disabled, they use the normal static/CPU world bounds.
- BVH hover/request code now calls `RenderableMesh.TryGetGpuMeshBvhRequestWorldBounds()` instead of directly using stale CPU culling bounds.

Outcome:

- Runtime rendering, engine, and editor builds passed.
- The cyan preview and BVH calculate-on-hover gate now share the same bounds source.

### 15. Runtime Setting Propagation And Bounds Path Split Were Fixed

The runtime rendering facade now reads `CalculateSkinnedBoundsInComputeShader` and `SkinnedBoundsGpuDirectAabbWrite` from the host engine settings instead of private auto-properties. The engine host service and effective settings path expose both values.

The live GPU bounds path now chooses the correct GPU calculation route:

- If compute skinning is enabled, `SkinningPrepassDispatcher.Run()` writes the fused live bounds tail from the merged skinning/bounds compute shader path.
- If vertex-shader skinning is enabled, `SkinnedBounds.comp` runs as a standalone GPU skin-and-reduce bounds shader and writes its own bounds SSBO.
- `SkinnedBounds.comp` supports both separate position buffers and interleaved vertex buffers.

Outcome:

- Runtime rendering, engine, and editor builds passed.
- `glslangValidator` plain parser validation passed for `SkinnedBounds.comp`.
- SPIR-V `-V`/`-G` validation is not applicable without additional explicit uniform locations/blocks because this shader is authored for the current OpenGL runtime loader style.

## Validation Performed During Attempts

Validation has included:

- `glslangValidator` on the modified compute skinning shaders and `skinned_bounds_debug_lines.comp`.
- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj`.
- `dotnet build .\XRENGINE\XREngine.csproj`.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj` when the editor DLLs were not locked.

At least one editor build attempt failed only because a running editor process locked output DLLs:

- `XREngine.Editor`
- `Visual Studio Debug Adapter for .NET`

This matters because some tests may have been run against older loaded DLLs unless the editor was restarted after the runtime rendering build.

## Previous Working Theory

The previous symptoms pointed at one or more of these root causes. This section is retained as debugging context.

### A. The Visible Boxes May Still Be CPU Debug Boxes

If `ShouldUseGpuSkinnedBoundsDebug()` returns false, `DoRenderBounds()` draws CPU `RenderDebugBox()` instead of the GPU bounds renderer.

Reasons it could return false:

- The current LOD renderer's mesh does not report `HasSkinning`.
- `AllowSkinning` is false.
- `CalculateSkinnedBoundsInComputeShader` is false in the effective runtime settings.
- The renderable is using a different LOD/renderer than expected.

Evidence:

- User saw cyan and green boxes. Those colors match `Bounds3DColor` and `MeshBoundsContainedColor`, which are CPU debug color paths as well as GPU debug path colors.

Next diagnostic:

- Add a short in-scene label or one-shot log that says exactly which path drew the box:
  - `GPU_LIVE_BOUNDS`
  - `CPU_DO_RENDER_BOUNDS`
  - `CPU_COLLECTED_BOUNDS`
  - `RENDER_INFO_CULLING_VOLUME`

### B. The Forced GPU Bounds Prepass May Not Be Reached

The direct draw should call:

`TryRenderGpuSkinnedBounds()` -> `TryGetLiveGpuSkinnedBoundsBuffer()` -> `RunForGpuMeshBvh()`

If the render command is not present, not collected, or exits early, none of the forced live path runs.

Next diagnostic:

- Count/log calls to `TryRenderGpuSkinnedBounds()` and `RunForGpuMeshBvh()` once per second for the selected submesh.

### C. The Skinning Shader May Not Be Writing The Bounds Tail Each Frame

Even if `RunForGpuMeshBvh()` dispatches, `updateLiveBounds` may be false if `ResetSkinnedBoundsInOutput()` fails or the output buffer is unavailable.

Next diagnostic:

- Add a `SkinningPrepassDispatcher` diagnostic log with:
  - renderer name
  - frame id
  - `forceSkinning`
  - `forceLivePoseRefresh`
  - `needsLiveSkinnedBounds`
  - `updateLiveBounds`
  - `isInterleaved`
  - output buffer existence
  - bounds offset

### D. The Bounds Tail May Be Clobbered After Dispatch

The output tail is seeded with positive/negative infinity before dispatch. If any later upload or buffer initialization writes stale client data over the GPU result, the preview could show stale or invalid data.

Potential clobber points:

- `EnsureSkinningOutputResident()`.
- `ResetSkinnedBoundsInOutput()`.
- Any full `PushData()`/`PushSubData()` on skinned output buffers after dispatch.
- Draw-time buffer generation if storage was not resident before dispatch.

Next diagnostic:

- Enable or add buffer upload tracing for skinned output buffers.
- Log every push to `SkinnedPositionsBuffer`/`SkinnedInterleavedBuffer` after the skinning dispatch.

### E. The Skin Palette May Still Be Stale

The forced path now calls `RefreshBoneMatricesFromRenderState()`, but external or GPU-driven bone sources may bypass that path.

Cases to inspect:

- `renderer.HasExternalSkinPaletteSource`.
- `renderer.HasGpuDrivenBoneSource`.
- Global skin palette buffer path.
- Whether animation updates are publishing `RenderMatrix` before the bounds command runs.

Next diagnostic:

- For the selected mesh, log:
  - `HasExternalSkinPaletteSource`
  - `HasGpuDrivenBoneSource`
  - active skin palette buffer name/id
  - a compact hash of current bone render matrices each frame
  - a compact hash of the uploaded skin palette each frame in the forced debug path

### F. The 5-Second Culling Refresh May Still Be Drawing A Separate Box

`ProcessSkinnedBoundsRefresh()` still updates `RenderInfo.LocalCullingVolume` and `RenderInfo.CullingOffsetMatrix` on the throttled cadence. If any debug path still draws `RenderInfo` culling volume, it will naturally update every 5 seconds.

Known paths to watch:

- `RenderInfo.RenderCullingVolumeDebugOverride`
- `RenderInfo3D.RenderCullingVolume()`
- global `RenderCullingVolumes`
- old collected debug draw paths

Next diagnostic:

- Temporarily disable all CPU `RenderDebugBox()` calls for skinned compute-bounds meshes and verify whether any stale box remains.

## Recommended Next Debugging Steps

1. Add unique labels/colors for every possible bounds draw source.

   Suggested labels:

   - `GPU live bounds`
   - `CPU direct bounds`
   - `CPU collected bounds`
   - `RenderInfo culling volume`

   This should be done before any more structural fixes. The current visual evidence is ambiguous because cyan and green are theme colors reused by multiple debug paths.

2. Add a rate-limited selected-mesh diagnostic in `TryRenderGpuSkinnedBounds()`.

   Log:

   - whether the method ran
   - renderer/mesh name
   - `IsSkinned`
   - `CalculateSkinnedBoundsInComputeShader`
   - `AllowSkinning`
   - `TryGetLiveGpuSkinnedBoundsBuffer` success
   - bounds buffer name
   - bounds offset

3. Add a rate-limited selected-renderer diagnostic in `SkinningPrepassDispatcher.Run()`.

   Log:

   - `forceSkinning`
   - `forceLivePoseRefresh`
   - `doSkinning`
   - `needsLiveSkinnedBounds`
   - `updateLiveBounds`
   - whether the same-frame cache returned early
   - whether cross-frame output reuse returned early

4. Add an optional debug readback of just the two bounds tail records.

   This should be behind a diagnostics flag and should not become the production preview path.

   Purpose:

   - Confirm whether the GPU tail values are changing every frame.
   - Distinguish "GPU bounds are stale" from "GPU bounds are live but the rendered box is not using them."

5. Split `RunForGpuMeshBvh()` into clearer entry points.

   Suggested API:

   - `RunForLiveGpuBounds(renderer)`
   - `RunForGpuMeshBvh(renderer)`

   Reason:

   - The current method name hides that bounds preview depends on it.
   - BVH and bounds have different freshness and request-gating requirements.

6. Keep CPU fallback disabled for the explicit GPU debug path.

   If the live GPU path fails, draw a diagnostic label and log the failed condition. Do not silently draw culling bounds.

## Things Not To Reintroduce

- Do not add a new SSBO binding to the already packed skinning shaders without auditing binding limits across OpenGL and Vulkan paths.
- Do not reintroduce CPU BVH traversal/rendering for the preview.
- Do not make the cyan bounds preview depend on CPU readback.
- Do not hide GPU path failure behind the old CPU culling box.

## Short Version

The visible bug is that bounds for an animated skinned mesh still update at the old 5-second CPU-visible skinned-bounds cadence. Several GPU-path fixes have been implemented, including fused bounds in compute skinning output tails and forced GPU bounds prepasses, but the issue persists. The next step should be source-of-truth instrumentation: identify exactly which draw path is producing each visible box and whether the GPU bounds tail itself changes every frame.
