# Jax2031 Advanced Parity Validation

Scope: The saved `jax2031 (1).prefab`, its active Poiyomi Pro 9.3.66 features, and Advanced Vulkan output. The source assets remain in the author's Unity project.

Architecture: [Advanced Render Pipeline](../../../architecture/rendering/advanced-render-pipeline.md), [Uber Shader Varianting](../../../architecture/rendering/uber-shader-varianting.md)
Code todos: [Unity Poiyomi Advanced Parity TODO](../../todo/rendering/unity-poiyomi-advanced-parity-todo.md)
Import work: [Unity Prefab Parity Import TODO](../../todo/assets/unity-prefab-parity-import-todo.md)
Investigation: [Jax2031 Unity And Advanced Vulkan Parity](../../investigations/rendering/2026-10-08-jax2031-unity-advanced-parity.md)

## Setup

Use the isolated editor workflow in [Editor And Tooling Workflows](../../../developer-guides/ai/agent-editor-workflows.md). Use ImGui, desktop mode, and Vulkan for the first check. Load `<unity-project-root>/Assets/Avatars/JAX/Mine/jax2031 (1).prefab` through the serialized prefab importer. Use a dedicated external unit-world settings file. Do not change the author's Unity assets or normal editor settings.

Request `XRE_ADVANCED_RENDER_PIPELINE_MODE=Required` and `XRE_FORCE_MESH_SUBMISSION_STRATEGY=GpuIndirectZeroReadback`. Enable Vulkan validation. Build Release for measurements. Use Debug only when detailed failure diagnostics are needed. Do not use `-NoBuild` unless the session binaries contain the source under review.

Use the same active outfit, pose, expression, camera, lights, reflection environment, color space, and exposure as Unity. The supplied image does not define those settings. Establish the reference settings before a quantitative image comparison. Start without bloom or temporal upscaling, then compare each authored effect separately. Repeat the final check with the selected production AA mode.

The [Toon 9.3.64 corpus](poiyomi-parity-validation.md) remains a separate validation area. A corpus pass does not satisfy this Pro 9.3.66 fixture.

## Checks

### Prefab Backend Matrix

The entry for both routes is `jax2031 (1).prefab`. Its FBX model prefab is a dependency. Importing `jax2031.fbx` directly does not validate nested prefab expansion, source file IDs, or the final overrides. An automatic native-to-Assimp fallback does not satisfy the explicit native row.

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Assimp prefab route | Import the exact `.prefab` with `AssimpOnly`. Inspect the actual backend, hierarchy, material slots, and deformed bounds. | Assimp supplies the model base. Unity composition completes with the intended identities and overrides. | Partial | 2026-10-08: composition completes with 69 model components and 67 materials. Visible deformation remains incorrect. |
| Native prefab route | Import the same `.prefab` with `NativeOnly`. Inspect the same data. | Native FBX supplies the model base. Unity composition completes with equivalent identities and valid cluster binds. | Blocked at normalization | 2026-10-08: only `xrengine.native-fbx@5` is selected. FBX parsing completes. The explicit Unity normalization guard stops publication. |
| Backend comparison | Compare final source identities, active objects, transforms, bounds, renderer slots, and visible output from the two routes. | Both routes represent the same composed prefab. Backend helper nodes do not change source identity. | Blocked by import | none |

### Visual And Runtime Checks

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Exact prefab import | Import the saved prefab in the isolated session. Inspect jobs, scene nodes, and mesh logs. | Import completes. The avatar enters the scene with stable Unity object identity. | Partial | 2026-10-08: Assimp composition completes and the avatar appears. Cross-backend identity acceptance remains open. |
| Final materials and textures | After import, inspect every active and inherited slot. Compare Unity GUID references and effective texture metadata. | Active slots use resolved Unity materials. No visible texture depends on an unresolved FBX source path. | Open | 2026-10-08: final import reports 67 materials. The complete texture audit remains open; the FBX reports 80 unresolved references before overrides. |
| Required Release admission | Start Release, prewarm shaders, and inspect the bound Advanced profile through a stable interval. | Advanced remains admitted with no submission downgrade or repeating shader regeneration. | Failed | 2026-10-08: Release remains pending on `ShadeNativeOpaqueMsaa.comp`. Debug reaches ready. |
| Native Uber execution | Inspect material publication and native evaluator selection for each active opaque or masked material. | The bound native evaluator implements the active Uber features. Unsupported features have explicit diagnostics. | Open | Static review on 2026-10-08 finds the standard PBR bridge. |
| Still-image parity | Capture and view front, side, and rear images. Add close views of eyes, skin, hair, and black clothing. Compare with matched Unity references. | Silhouette, normals, texture detail, shading, eye highlights, emission, and clothing reflections match. | Failed | 2026-10-08: front and oblique Assimp captures show the avatar with detached geometry, stretched triangles, pose differences, and incorrect shading. |
| Coverage and shadows | View hair against bright and dark backgrounds. Inspect masked shadows and mixed clipping/blending materials. Test the intended MSAA mode. | Strand edges, hidden surfaces, depth, and shadow coverage match the effective source shader. | Open | none |
| Texture sampling | View close and distant poses. Inspect sRGB color textures, linear normal/data textures, UV transforms, and mip selection. | No double sRGB conversion, wrong normal decode, or fixed-mip compute sampling appears. | Open | Source metadata inspected on 2026-10-08; GPU output unavailable. |
| Deformation | Apply the bind pose, arm motion, face expressions, and the active secondary motion setup. Compare current and previous surfaces. | Skinning, morphs, normals, bounds, shadows, and motion vectors stay correct. | Failed for the initial visible pose | 2026-10-08: visible geometry is deformed. Full motion and landmark checks remain open. |
| Runtime plugin behavior | Exercise only the required outfit, expression, and physics controls. Inspect the expanded hierarchy and generated runtime components. | Required source behavior has an engine equivalent. Missing Unity build/menu behavior is identified. | Open | Source contains VRCFT, GoGoLoco, and physics setup instances. |
| Backend logs | Stop the owned session and inspect import, shader, Vulkan, and rendering logs. | No unresolved non-teardown error affects the measured frame. | Failed | 2026-10-08: startup reservation error and a GPU deformation slice retry during publication. Native publication has an explicit normalization rejection. |

## Performance Checks

Measure warmed Release frames after import and shader readiness. Keep the camera and output fixed. Record an empty-scene baseline, one still avatar, and one moving avatar. Report CPU and GPU frame time at P50, P95, and P99. Record active vertices, triangles, bones, morph channels, material kernels, draw calls, texture memory, skinning cost, shadow cost, late transparency cost, overdraw, shader cache misses, and frame allocations.

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Frame budget | Measure at the chosen production output and target refresh rate. Include the whole frame. | P95 fits the selected 16.7 ms, 11.1 ms, or 8.3 ms budget for 60, 90, or 120 Hz. Report P99 stalls separately. | Blocked by visual correctness and Release readiness | No valid avatar timing. |
| Feature variants | Compare cold preparation and warmed output while values and compatible materials change. | Values use material rows. Warmed frames do not compile known feature variants or multiply kernels per material value. | Open | none |
| Submission and allocation | Inspect the resolved submission strategy, GPU work counters, and frame allocation data. | Native opaque work keeps GPU submission without count readback. Any permitted CPU late submission is reported separately. | Open | 2026-10-08: Advanced is admitted with the visible avatar and no submission downgrade. Allocation and deformation-path acceptance remain open. |
| Visibility and LOD | Measure the selected outfit before changing geometry or textures. Disable unused alternatives. Compare close and distant output after any LOD change. | Cost falls while the required silhouette, deformation, and material borders stay correct. | Open | Current geometry counts are unknown. |
| VR | Repeat with both views, production shadows, active animation, and required physics. | The stereo frame meets the selected full-frame budget without eye mismatch or temporal artifacts. | Open | none |

## Hardware Matrix

| Hardware | Backend and output | Result |
|---|---|---|
| NVIDIA GeForce RTX 3090 in the local system | NVIDIA Vulkan capabilities, desktop mono, 1920 by 1080 | Assimp prefab composition and Debug admission pass. The avatar is visible but incorrect. Native parsing passes; prefab publication is guarded. No avatar FPS result. |
| Production VR hardware | Vulkan stereo, production per-eye resolution | Not measured. |

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
| Native prefab publication | Native content basis and cluster binds need Unity normalization. | Linked investigation and native conversion code items. |
| Visible deformation | Detached geometry and stretched triangles remain after Assimp composition. | Linked investigation and coordinate, metadata-pose, and bind-palette code items. |
| Release admission | Native MSAA compute shader remains in regeneration. | Linked investigation. |
| Native Uber execution | Standard material publication does not execute the authored Uber shading modules. | Linked native Uber code items. |
