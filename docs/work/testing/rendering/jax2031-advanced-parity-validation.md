# Jax2031 Advanced Parity Validation

Scope: The saved `jax2031 (1).prefab`, its active Poiyomi Pro 9.3.66 features, and Advanced Vulkan output. The source assets remain in the author's Unity project.

Architecture: [Advanced Render Pipeline](../../../architecture/rendering/advanced-render-pipeline.md), [Uber Shader Varianting](../../../architecture/rendering/uber-shader-varianting.md)
Code todos: [Unity Poiyomi Advanced Parity TODO](../../todo/rendering/unity-poiyomi-advanced-parity-todo.md)
Import work: [Unity Prefab Parity Import TODO](../../todo/assets/unity-prefab-parity-import-todo.md)
Investigation: [Jax2031 Unity And Advanced Vulkan Parity](../../investigations/rendering/2026-10-08-jax2031-unity-advanced-parity.md)

## Setup

Use the isolated editor workflow in [Editor And Tooling Workflows](../../../developer-guides/ai/agent-editor-workflows.md). Use ImGui, desktop mode, and Vulkan for the first check. Load `<unity-project-root>/Assets/Avatars/JAX/Mine/jax2031 (1).prefab` through the serialized prefab importer. Use a dedicated external unit-world settings file. Do not change the author's Unity assets or normal editor settings.

Request `XRE_ADVANCED_RENDER_PIPELINE_MODE=Required` and `XRE_FORCE_MESH_SUBMISSION_STRATEGY=GpuIndirectZeroReadback`. Enable Vulkan validation for correctness checks. For performance, use a separate Release run with validation, RenderDoc, and code-profiler logging disabled. Record those settings with the results. Use Debug only when detailed failure diagnostics are needed. Do not use `-NoBuild` unless the session binaries contain the source under review.

Record whether RenderDoc suppresses validation-layer debug output. When it does,
an empty VUID log does not prove a clean validation-layer run. Keep GPU capture,
engine fault, ownership, and image results separate from that claim.

The 2026-10-11 coordinate comparison used `Kind=Static` with skinning enabled. Source animation checks use `Kind=Animated` and require `VisualAndAvatarBehavior`. Both profiles use `XRE_GPU_DRIVEN_VALIDATION_CAPACITY_FLOOR=1024` to avoid the known scene-growth failure. This keeps GPU submission, but does not validate capacity growth.

For the native material comparison, use `Kind=Static`, disable the skybox, set
camera `colorGrading.AutoExposure=false` and `colorGrading.Exposure=1`, and set
`bloom.Enabled=false`. Read `get_camera_post_process` before setting these keys.
These settings make the two routes comparable; they are not a matched Unity
lighting reference. Wait for texture uploads and canonical mesh preparation.
Inspect the captured dispatches because profile admission can remain true while
a transient source mismatch rejects a particular frame.
Before a GPU capture, read `get_render_state`. Require an empty preparation
deferral reason, published GPU resources, an executed aggregate dispatch, and
nonzero draw count. Record the scene database epoch and topology generation.
Pending mesh preparation can retain earlier opaque color in late passes. Such
an image does not prove native shading execution in the captured frame.

For source-light controls, use a constant world ambient input. Compare Body
color modes 0 and 2 under a low-intensity colored primary light. Set primary
intensity to zero and compare unlit intensity 1 and 2. Add a second directional
light and check the saved suppression flag, then temporarily clear it on Tank.
Toggle Face clearcoat and the hair backface flag separately. Restore each value
and remove the temporary light. Compare fixed pixel regions and all material
identities. A scene without probes validates direct clearcoat only; use a known
nonzero probe environment for reflection acceptance.

For directional SH, disable the primary light and enable authored world SH.
Use `Ar=(0.08,0,0,0.12)`, `Ag=(0.04,0,0,0.10)`, and
`Ab=(0.02,0,0,0.08)`, with the other vectors zero. Reverse the three X values.
Compare that pair, then compare constant coefficients with
`C=(0.05,0.03,0.02,0)`. Check enabled zero coefficients separately. Restore the
saved world and light values. Add a point and a spot light near the avatar;
compare additive enable, Lambert, gradient, and monochromatic controls. Engine
attenuation and per-pixel assignment are the supported local-light inputs.

For probe reflection, create an inactive probe node. Disable automatic and
realtime capture. Set resolutions and influence, then activate the node before assigning
`Build/CommonAssets/Textures/studio_small_09_4k.exr`. The texture setter starts
static convolution; do not invoke `InitializeStatic` again. Wait for valid IBL
output. Inspect the selected probe row, weight, texture binding,
and exported HDR radiance in RenderDoc. Compare Tank reflection strength zero
with its saved value. Also compare black and saved Mochie reflection tint to
isolate probe specular without changing diffuse energy. If the saved Tank mask
suppresses the response, temporarily set `_PBRMochiePackedMasksEnabled=0` and
`_PBRReflectionStrength=1` for this positive control. Restore both afterward.
For Face, set direct coat strength to zero and compare
reflection strength 0 with 0.245. Restore all material values. Keep the probe
alive through the capture and stop the owned editor afterward. Record any
probe-publication error separately from the final shading result.

For buffer-lifetime acceptance, use a fresh process for each explicit prefab
route. Run the first point and spot activation before adding a probe. Then
publish, remove, and recreate the static probe. Move the replacement probe three
times, remove it, and capture front and oblique output again. Keep the process
alive through all operations. Check both Vulkan logs and changed control pixels;
profile admission or a null general timing fault alone cannot prove that the
renderer is still running. Inspect a GPU capture after probe replacement.

For probe diffuse acceptance, disable the primary light and world ambient.
Enable zero authored SH. Disable reflections, clearcoat, emission, rim, and
matcaps on the selected Face and Tank materials. Set their metallic multiplier
and minimum light brightness to zero. Move the static probe outside its influence
and back for lighting modes 0, 2, and 6. Compare front and oblique images. Restore
all saved values. Confirm nonzero irradiance and valid native shading in a GPU
capture. This separates diffuse probe lighting from source SH and reflections.

Check late-array consumption with a temporary lit transparent sphere. Use FXAA
with motion blur disabled for this control. Add, remove, and recreate the static
probe while the sphere remains visible. Require a visible response and inspect
the sphere's fragment bindings 7 and 8 in RenderDoc. They must reference the
nonzero irradiance and prefilter arrays, not the 1 by 1 dummy. Native opaque
probe output alone does not validate these bindings. Remove the control objects.
Motion-blur-only transparent velocity needs a separate frame-matrix check.

Read `get_vulkan_resource_planner_states` and `get_vulkan_memory_statistics`
between probe controls. Repeated moves must not retain another full target set
per registry revision. Check native allocated bytes after retirement settles;
a bounded managed planner census alone does not prove native resource release.
Keep output dimensions fixed or record their change with the memory results.

Check scene capture separately from static environment conversion. Create an
inactive probe with no environment asset, enable `UseAdvancedCapturePipeline`
and `AutoCaptureOnActivate`, disable
realtime capture and preview drawing, and configure its resolution and influence.
Activate it once. Require a nonzero capture version and usable IBL output. Inspect
the captured irradiance, final native shading, and front and oblique images.
Remove the probe and stop the owned editor after the check.
The viewport's Required Advanced setting does not select the probe capture
pipeline. Record the probe's capture-pipeline setting separately. Nonzero output
and a usable publication flag alone do not establish complete scene capture.

For continuous capture, use a fresh process for each explicit route. Select
Advanced probe capture, 128-pixel cube faces, irradiance resolution 16, and the
default 100 ms request interval. Keep transient environment release enabled.
Measure a fixed camera before capture, during capture, after moving the probe,
and after removing it. Keep capture active for at least 180 seconds. Require
capture-version progress across both continuous windows. Record actual progress;
the request interval does not guarantee ten completed captures per second.
Inspect native allocation bytes, live planner storage, and the retirement meter.
Use `metering.pendingRetirementCount` and per-class backlog instead of the legacy
pending count. Export and view irradiance and prefilter images, plus front and
oblique viewport images after capture stops and after probe removal.

Use separate 30-second windows without profile logging to measure completed
desktop presentations per elapsed wall time. Then collect detailed CPU callback,
GPU timestamp, and presentation-interval distributions. Do not report
`frame_output_whole_frame_ms` as complete frame cadence: it excludes the prior
wait for collection. Deduplicate GPU sequence identifiers and report sample age.
Match a stored failure's frame identifier before treating it as a current error.
Retain each completed profile before starting the fourth one; runtime capture
retention keeps only three profiles per process. Record current deferrals
separately from stale last-failure text.

For a source-clip check, disable only the empty `AnimStateMachineComponent` that the unit-world setup adds to the import wrapper. Keep its `HumanoidComponent` active. The empty controller otherwise overwrites the child source clip's pose each frame. This setup gives the generic source clip sole pose ownership; it does not validate controller blending or Unity humanoid retargeting. Seek `Armature|ArmatureAction` at 0, 0.9583333, 1.2083333, and 1.5833334 seconds. Let rendering settle for two seconds after each seek or camera move before capture. Then play it looped at half speed with a fixed camera. Compare `Velocity` while still and moving. The source action has constant-zero shape curves, so use a separate expression check for animated morph acceptance.

Use the same active outfit, pose, expression, camera, lights, reflection environment, color space, and exposure as Unity. The supplied image does not define those settings. Establish the reference settings before a quantitative image comparison. Start without bloom or temporal upscaling, then compare each authored effect separately. Repeat the final check with the selected production AA mode.

The [Toon 9.3.64 corpus](poiyomi-parity-validation.md) remains a separate validation area. A corpus pass does not satisfy this Pro 9.3.66 fixture.

## Checks

### Prefab Backend Matrix

The entry for both routes is `jax2031 (1).prefab`. Its FBX model prefab is a dependency. Importing `jax2031.fbx` directly does not validate nested prefab expansion, source file IDs, or the final overrides. An automatic native-to-Assimp fallback does not satisfy the explicit native row.

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Assimp prefab route | Import the exact `.prefab` with `AssimpOnly`. Inspect the actual backend, hierarchy, material slots, and deformed bounds. | Assimp supplies the model base. Unity composition completes with the intended identities and overrides. | Composition and active bindings passed; visual parity partial | 2026-10-11: 69 model components, 101 mesh/LOD rows, 30 active rows, and 21 active Unity materials. |
| Native prefab route | Import the same `.prefab` with `NativeOnly`. Inspect the same data. | Native FBX supplies the model base. Unity composition completes with equivalent identities and valid cluster binds. | Composition and active bindings passed; visual parity partial | 2026-10-11: explicit native route has 69 model components, 107 mesh/LOD rows, 35 active rows, and 21 active Unity materials. No fallback. |
| Backend comparison | Compare final source identities, active objects, transforms, bounds, renderer slots, and visible output from the two routes. | Both routes represent the same composed prefab. Backend helper nodes do not change source identity. | Active bindings passed; full geometry and output partial | 2026-10-11: all 69 model node IDs agree. Both routes have 19 active model nodes and 30 active renderer slots with matching Unity materials and source texture settings. Native uses extra mesh chunks. One inactive Emote slot has an extra native default sampler. |
| Skinning snapshot coherence | Read `RuntimeSkinningDiagnosticSummary` for every model component after publication and source playback. Compare `bones`, `snapshotBones`, and `snapshotBindingMismatches`. | The snapshot and mesh use the same completed bone table. Packed influence indices retain their meaning. | Passed for both routes | 2026-10-11: Assimp has 101 mesh/LOD rows; native has 107. Zero snapshot mismatches, detached or missing palette bones, invalid influences, or non-finite values. Body live palette difference rounds to zero for both. |
| Indexed morphs and hair bind | Fresh-import the exact prefab with each explicit backend. Read Body, Base1, Base2, and CLO Tank morph names and weights. View front, oblique, and wide captures. | Saved indices address the intended shapes. Hair stays at its authored bind location. | Passed for this prefab | 2026-10-11: both routes keep the required body blendshape at 0% and the saved clothing shapes at 100%. The bangs are visible on the head and the tank covers the body. All 607 source channels retain authored order. All 62 source skins pass the Assimp bind resolver checks. |

### Visual And Runtime Checks

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Texture publication and sampler edits | Import each exact prefab route. Observe preview-to-full mip promotion. Change LodBias during an upload, restore it, then change tank detail strength `2 -> 0 -> 2`. Inspect streaming generations, errors, identities, and front/oblique images. | Logical metadata changes with the completed native image. Stale candidates recover under the same logical generation. All work settles and visible meshes remain. | Passed for the focused Debug fixture | 2026-10-12: the final binary passes both explicit routes with Advanced admitted and GPU strategy 2, without a downgrade. Each session settles at 126 publications with zero pending work, upload failures, or texture validation failures. Native recovers from four stale ACC Metalic candidates and publishes generation 2 at 4096 by 4096 with 13 mips. All 21 material identities remain stable. All 12 scalar-check images were viewed. |
| Source specular, rim, and harness state | Import each explicit prefab route. View front and oblique output through tank detail strength `2 -> 0 -> 2`. Inspect source adapter constants, GPU preparation, native dispatch, and diagnostic pixels. | Source smoothness and pre-light rim replace the generic hard highlight. Saved geometry state remains intact. | Focused Debug check passed for both routes; matched lighting open | 2026-10-12: the final editor build has zero warnings and errors. Both routes retain all 21 identities and visible meshes. The hard tank circles and wide skin bands are absent. Final RenderDoc captures execute native shading with zero reconstruction or coverage rejection pixels. The patch belongs to Acc - Top Harness and remains enabled, as requested. |
| Source light, Face clearcoat, and backface normals | Compare imported controls across routes. Toggle Face coat, hair backface flip, Body color mode, zero-primary unlit intensity, and additional-directional suppression. Restore values and inspect final native GPU output. | Source controls select their intended equations. Saved values restore the output without lost meshes or GPU submission. | Focused Debug checks passed for both routes; probe and matched-lighting checks open | 2026-10-13: all 21 materials have identical source control values across routes. Clearcoat, light mode, unlit intensity, and directional suppression controls change the intended regions and restore them exactly. Hair backface shading changes; small restored differences remain with active glitter. Unrelated checked arm pixels remain unchanged. Both routes retain strategy 2, all identities, and no terminal fault. Final GPU captures execute native shading with zero reconstruction or coverage rejection pixels. The build has zero warnings and errors. This fixture has no probes. |
| Texture failure and transfer-path coverage | Reject replacement admission after a stale candidate. Exercise cancellation, owner retirement, and the recorded-transfer completion path. | Terminal errors remain terminal; actual cancellation stays retryable; the original completion owner settles once. | Code-reviewed; extended live coverage open | 2026-10-12: review covers both completion paths and rejected admission. Live retries use the graphics upload batch. These fixture runs do not inject admission failure or exercise every retirement path. |
| Exact prefab import | Import the saved prefab in the isolated session. Inspect jobs, scene nodes, and mesh logs. | Import completes. The avatar enters the scene with stable Unity object identity. | Passed for visual composition | 2026-10-11: both routes complete the saved prefab and produce the same 69 model node IDs. This does not validate every component or source identity. |
| Final materials and textures | After import, inspect every active and inherited slot. Compare Unity GUID references and effective texture metadata. | Active slots use resolved Unity materials. No visible texture depends on an unresolved FBX source path. | Passed for required active slots | 2026-10-11: all 30 active renderer slots agree on 21 Unity materials, raw source-slot sets, and source-backed 2D texture settings. Source inventory resolves 74 remaps, 94 non-null outer overrides, and 106 texture assets. Generated defaults and converted HDR cubes require separate interpretation. Inactive producer materials are not fully accepted. |
| Required Release admission | Start Release, prewarm shaders, and inspect the bound Advanced profile through a stable interval. | Advanced remains admitted with no submission downgrade or repeating shader regeneration. | Passed for both routes | 2026-10-14: both explicit prefab routes complete Release texture warmup, live material controls, and continuous Advanced probe capture with GPU indirect submission. The old shader-regeneration block does not recur. |
| Debug startup readiness | Repeat the isolated prefab startup with the same binary and Required profile. Inspect shader readiness after import. | Advanced admits the frame without a restart or manual shader reload. | Passed for both routes | 2026-10-12: the corrected final binary completes Advanced preparation in 5,783 ms for Assimp and 6,052 ms for native FBX. Both exact prefab routes retain 21 material identities through detail strength `2 -> 0 -> 2`. Front and oblique captures were viewed at each value. No terminal fault, shader regeneration message, or invalid-stage message occurs. Assimp has two empty frame-package samples; three later reads each publish strategy 2 without a downgrade. Native reports strategy 2 at all three scalar checks. |
| Native Uber execution | Inspect material publication and native evaluator selection for each active opaque or masked material. | The bound native evaluator implements the active Uber features. Unsupported features have explicit diagnostics. | Execution passed; image parity remains open | 2026-10-11: both routes admit all 16 active opaque or masked Uber materials. Final GPU captures have nonzero indirect native shading dispatches and source-contract-11 rows with 2312 words and 72 texture roles. A later corrected Assimp capture has 16 source-contract-11 rows with 2324 words and 72 roles. Both corrected captures have nonzero indirect shading and viewed compute-color exports. Opaque and masked layouts have separate hashes. Four standard materials retain their standard rows. The transparent horn uses the late lane. |
| Still-image parity | Capture and view front, side, and rear images. Add close views of eyes, skin, hair, and black clothing. Compare with matched Unity references. | Silhouette, normals, texture detail, shading, eye highlights, emission, and clothing reflections match. | Failed | 2026-10-12: corrected front and oblique views use the same static pose, black background, manual exposure 1, and no bloom. Fresh imports after the bind and morph-order fixes retain the bangs and tank coverage in both routes. Later dissolve and detail-color fixes remove the moving clothing patches and excess detail-color multiplier. The source glitter correction removes the broad moving hair bands. Later matcap and detail-normal adapters preserve source blur and composition. The accessory reconstruction fix removes the magenta rejection pixels in both routes. The later source BRDF and rim correction removes the hard tank circles and wide skin highlight bands in both routes. The fixed patch is the enabled top harness. Its saved state is preserved at the user's request. These controlled views still lack a matched Unity lighting reference. |
| Directional SH and local composition | Reverse signed SH X terms, add L2, set enabled black coefficients, then restore. Compare local enable, Lambert, gradient, and monochrome. | Each control changes the intended contribution and restores stable regions. | Focused controls passed for both routes | 2026-10-13: both routes retain 21 active material identities. SH direction, L2, enabled black, point, gradient, and monochrome controls respond. SH and point restores are exact in checked Face and Tank regions. Correcting the spot record to use the emitter transform makes spot enable change 34,328 Tank pixels on each route, with Face unchanged and exact regional restores. An earlier native run stopped on first Spot activation; the fresh repeat passes the control but does not clear the lifetime defect. |
| Mochie and clearcoat probe reflection | Assign a nonzero static environment. Isolate reflection tint and reflection-only Face coat. Inspect GPU probe data and native output. | Both source reflection terms respond and restore without changing other material regions. | Focused controls passed for both routes | 2026-10-13: static generation waits for an active renderer and completes without manual retry. Reflection-only Face coat changes 376 Assimp and 380 native checked pixels, with Tank unchanged. Unmasked Tank tint changes 34,319 Assimp and 34,316 native pixels, with Face unchanged. Each control restores both regions exactly. The saved Tank mask gives no measurable tint response. Final GPU captures show nonzero probe radiance, 176-byte probe rows, 240-byte environment rows, and zero reconstruction or coverage rejection pixels. |
| Probe publication lifetime | Add, replace, move, and remove probe data while frames remain live. | Published buffers and arrays stay valid through their last use. Obsolete target sets retire. | Focused checks passed on both routes | 2026-10-14: final native PID 97292 and Assimp PID 72228 complete publication, removal, recreation, repeated moves, and final removal. No disposed-resource failure or renderer pause occurs. Planner target storage stays at 616.40 MiB. Native allocation bytes settle at 2,914.13 MiB and 2,626.22 MiB respectively after capture transients. Replacement captures execute native shading with valid probe records and no rejection pixels. Sustained realtime capture remains open. |
| Probe diffuse GI | Disable direct light, ambient, SH, reflections, and other indirect material terms. Move the probe outside its influence and back. | Probe diffuse changes the intended surfaces and restores stable regions. | Static-environment checks passed on both routes | 2026-10-14: lighting modes 0, 2, and 6 respond on Face and Tank. Front regions restore exactly. Small oblique differences occur over hair at the left edge of the Tank region. Replacement captures contain nonzero irradiance, valid probe and environment records, and 2,073,600 valid diagnostic pixels. This confirms engine probe GI consumption. It does not validate Unity baked-probe assignment. |
| Lit transparent probe arrays | Add, remove, and recreate a static probe with a lit transparent sphere present. Inspect its image response and bound fragment arrays. | BRDF readiness recovers after publication. The shader samples real arrays and returns to its baseline after removal. | Focused image and GPU checks passed on both routes | 2026-10-14: native PID 5636 and Assimp PID 6072 each change 29,940 of 30,000 checked pixels. Both removed-probe images return the same baseline. Native event 528 and Assimp event 522 bind nonzero 32 by 32 irradiance and 256 by 256 prefilter arrays, with 6 and 9 mips. Both routes keep target storage at 616.40 MiB and retain the avatar after control removal. The initial empty-array result exposed a BRDF attachment-preparation deadlock; the corrected producer now retries and settles. Motion-blur-only velocity and sustained realtime capture remain open. |
| Advanced scene-probe capture | Explicitly select Advanced capture. Capture the scene once without an environment asset. Inspect output radiance and native shading. | The probe publishes visible scene lighting and the viewport remains active. | One-shot checks passed on both routes | 2026-10-14: native and Assimp publish capture version 1. Irradiance means are 0.01718 and 0.01714; prefilter means are 0.01650 and 0.01647. The viewed native prefilter contains the avatar. Both captures have 2,073,600 valid native diagnostic pixels. Front and oblique views remain intact after removal. The later Release result below covers continuous capture. Per-face geometry completeness and production performance remain open. The earlier default-pipeline capture produced nearly black output and does not establish Advanced capture acceptance. |
| Local-light buffer lifetime | Add the first local light while canonical late draws remain active. Inspect descriptor admission and actual presentation. | Captured bindings remain valid through buffer growth. | Focused checks passed on both routes | 2026-10-14: fresh native and Assimp processes complete first point and spot activation without retired-buffer rejection. Each spot control changes 34,328 Tank pixels and restores the checked regions exactly. GPU indirect strategy 2 remains active. An earlier Assimp attempt stops at a transient deformation-readiness gate; the engine recovers, and the complete fresh repeat passes. |
| Source Basic dissolve | Import each prefab route with saved amount 0 and edge width 0.334. View fixed-camera and oblique output. Temporarily set amount 0.5, then restore it. | Zero amount produces no dissolve edge. Nonzero amount retains the source transition. | Passed for this prefab; other dissolve modes open | 2026-10-11: both corrected routes retain the authored widths without moving tank or shorts patches. Mode 5 and progress 0 reach runtime. Amount 0.5 produces a visible patterned transition. Shared source math now runs in canonical and native coverage. Checks use shadows off. |
| Detail color neutrality | Import each prefab route at fixed exposure with the saved face detail intensity 1. Inspect normalized tint and brightness. | An absent detail texture with source brightness 1 is neutral. | Passed for this prefab | 2026-10-11: both routes retain intensity 1, normalized tint approximately 1, and engine additive brightness 0. Viewed front and oblique captures show skin shading without disabling detail color. Gamma projects and authored detail textures need separate acceptance. |
| Hair flicker | Hold camera and pose fixed. Capture pairs with source glitter brightness 2.7 and a temporary value 0. Restore 2.7. | Source sparkles do not become broad moving bands. | Passed for the active source circle glitter | 2026-10-12: both routes retain frequency 2000, two layers, angle 3.9, contrast 640, and brightness 2.7. Viewed close, front, and oblique captures have no broad moving glitter bands. Mean RGB byte change in the fixed hair region is 0.338 then 0.343 for native, and 0.330 then 0.326 for Assimp. Both disabled pairs are exactly stable. Glitter remains active. Checks use static pose and shadows off; matched lighting and production AA remain open. |
| Matcap and detail-normal semantics | Compare source constants, mip selection, mask channels, and blend stage with both Uber evaluators. Then use matched source lighting for visual acceptance. | Source blur and normal composition are preserved. | Source adapters implemented; both routes execute; full visual acceptance open | 2026-10-12: canonical and native evaluators use green-mask tangent-space composition and imported green inversion. Matcaps retain source mip selection and pre-light base-color blending, with separate light and emission contributions. Both routes retain Tank slot-1 smoothness 0.225. Detail strength remains 2. Canonical all-feature, detail-only, and matcap-only shader compile checks pass. Front and oblique output and native compute color were viewed. Matched source lighting remains open. |
| Small accessory reconstruction | Inspect the native compute color and rejection diagnostics for visible horns, choker, and wrist accessories. | Small raster-visible triangles reconstruct without a world-area cutoff. | Both saved-state front views passed | 2026-10-12: the old settled Assimp capture has 1,297 reason-11 rejection pixels. Both corrected front captures have zero rejection pixels at 1920 by 1080. Nonzero native indirect shading executes; its exported color was viewed. |
| Live material edits | Change tank matcap smoothness and detail-normal strength, wait for publication, then restore saved values. View all opaque groups. Verify native Uber identity before and after edits. | The edited material changes without omitting other groups. | Passed for the scalar sequence | 2026-10-12: fresh Assimp and native FBX prefab imports retain visible meshes through detail strength `2 -> 0 -> 2`, with 15-second waits and front/oblique views. GPU constant comparison changes only Tank row 9, word 148, from 2 to 0. Admission remains active with GPU indirect submission, no downgrade, and no terminal fault. A pixel audit of the earlier matcap/detail captures finds unchanged arm, abdomen, and leg regions; the earlier omission report was incorrect. |
| Shader reload identity | Import the prefab, reload unchanged shaders twice, then read semantics and native preflight. Change source in memory as a negative control. | Unchanged canonical source retains native Uber encoding; authored edits clear eligibility. | Passed for canonical prefab materials | 2026-10-12: the corrected build retains 17 native Uber and four standard identities through two reloads in both routes. Advanced GPU indirect submission stays admitted without a downgrade or terminal fault. Front and oblique images retain meshes. An in-memory root edit clears tank identity in Assimp. See the generated-variant check below and the investigation for captures and log limits. |
| Internal generated-variant adoption | Force Tank variant preparation, reload twice, then change detail strength `2 -> 0 -> 2`. Check shader revision, identity, native preflight, and front/oblique output. Edit generated and canonical source separately. | Trusted internal replacement preserves identity; authored source edits clear it. | Passed for both routes after shader readiness | 2026-10-12: both exact prefab routes adopt a generated fragment with revision `4 -> 5`, retain all 21 identities through both reloads and scalar edits, and clear identity on both source-edit controls. Later profile reads use GPU indirect strategy 2, no downgrade, and no terminal fault. Assimp canonical return advances revision `5 -> 6` with native preflight true. Viewed images retain meshes. Initial Assimp output waits on `ShadeBackground.comp` and recovers after reload. Native has one early empty frame-package read. Reentrant callback injection and snapshot round trips were reviewed in code, not exercised live. |
| Coverage and shadows | View hair against bright and dark backgrounds. Inspect masked shadows and mixed clipping/blending materials. Test the intended MSAA mode. | Strand edges, hidden surfaces, depth, and shadow coverage match the effective source shader. | Shared evaluator executes; visual and MSAA acceptance open | 2026-10-11: native masked rows execute with shared cutout, alpha-mask, dissolve, face-discard, and supported pixel UDIM rules. Hair and tail edges appear in viewed front and oblique captures. UV2 and UV3 remain zero when absent. Alpha-to-coverage is explicitly rejected. A matched shadow and production-AA comparison is still required. |
| Late transparency | Inspect the horn material eligibility, actual draw, depth state, and blend state. | Supported transparency enters the sorted alpha lane with the source state. Unsupported temporal replay is explicit. | Sorted alpha execution passed; temporal replay unavailable | 2026-10-11: `Mat_Glow 5` has explicit sorted alpha metadata. RenderDoc records the 440-triangle horn draw with One/InvSrcAlpha/Add color blending. Exported output was viewed. The lane uses filtered direct submission. The importer reports the missing matching velocity and reactive variants. |
| Texture sampling | View close and distant poses. Inspect sRGB color textures, linear normal/data textures, UV transforms, and mip selection. | No double sRGB conversion, wrong normal decode, or fixed-mip compute sampling appears. | Metadata and resident formats passed; shader sampling open | 2026-10-11: both active binding audits preserve source-backed 2D settings. Viewed RenderDoc exports show sRGB clothing colors and linear normals for both routes, plus native body color. This does not prove native Uber feature use, sampling gradients, or close/distant mip behavior. |
| Deformation | Apply the bind pose, arm motion, face expressions, and the active secondary motion setup. Compare current and previous surfaces. | Skinning, morphs, normals, bounds, shadows, and motion vectors stay correct. | Source arm motion passed; full coverage open | 2026-10-11: four seek times have identical local, world, and render transforms for five sampled nodes across routes. Viewed front and oblique captures show arm deformation without long hand spikes. Final bind-fix binaries were also checked at four source-clip seek times through both routes. Viewed front and oblique samples retain the hair placement and tank coverage. Accessory palettes, nonzero expressions, animated bounds, shadows, and secondary motion need separate acceptance. |
| Source animation conversion | Import the exact prefab through the animated startup profile with each backend. Inspect clips, paths, and completion diagnostics. | Supported source clips use the final hierarchy and coordinates. Unsupported behavior stops the required behavior profile explicitly. | Passed for this source action | 2026-10-11: both explicit routes publish `Armature\|ArmatureAction`, 1.5833334 seconds, with 786 node bindings and 607 shape bindings (2,965 property tracks). Seeks and looped playback complete with independent playback tracks. The shape curves are constant zero. |
| Source motion output | Pause and play the source action with a fixed camera. Capture and view `Velocity`. Inspect Advanced submission and engine fault state. | Still output is zero within storage precision. Moving output is finite and follows deformed geometry. GPU submission stays active. | Passed for arm motion on both routes | 2026-10-11: paused output is zero within storage precision; playback produces finite, nonzero arm velocity. Advanced stays admitted with requested and resolved submission strategy 2, no downgrade, and no terminal fault. This is not a complete temporal AA or shadow acceptance check. |
| Runtime plugin behavior | Exercise only the required outfit, expression, and physics controls. Inspect the expanded hierarchy and generated runtime components. | Required source behavior has an engine equivalent. Missing Unity build/menu behavior is identified. | Open | Source contains VRCFT, GoGoLoco, and physics setup instances. |
| Scene capacity growth | Import into a fresh live Vulkan session and inspect `get_time_state` after publication. Repeat across capacity growth. | Collection remains live. Structural growth preserves GPU-authored LOD state through the render owner. | Failed intermittently | 2026-10-11: one corrected run stops in `SyncLodTransitionBufferFromGpu` without a render owner. An identical-binary restart stays live. |
| Coordinate comparison | Use the same static prefab settings and front/oblique camera views for both backends. Inspect bind palettes and view both captures. | Geometry and pose match the reference. | Targeted geometry fixes passed; full comparison open | 2026-10-11: final runs use the same T-pose, black background, manual exposure 1, and no bloom. Front, oblique, and wide views retain the bangs and tank coverage. The Unity lighting reference is not matched. |
| Existing import and mesh regressions | Run the existing `XRMeshSerializationTests`, `GpuSkinningBufferCompressionTests`, and `SerializedSceneImporterTests`. | All selected tests pass. | Partial | 2026-10-11: 21 pass; `YamlSerializer_Reads_CookedBinaryWrappedRuntimeMeshPayload` fails with a placeholder mesh. No test source changed. |
| Existing bind and backend regressions | Run `NativeFbxImporterTests`, `SerializedSceneImporterTests`, and `ModelImportBackendResolverTests` after live validation. | All selected tests pass. | Partial | 2026-10-11: 23 pass; one backend candidate-hash snapshot still expects Assimp version 1. Assimp version 2 is required to invalidate the old geometry cache. The final editor build has zero warnings and errors. No test source changed; the test update is in the import todo. |
| Existing animation and import regressions | Run `AnimationClipComponentTests`, `AnimationClipSerializationTests`, `NativeFbxImporterTests`, and `SerializedSceneImporterTests`. | All selected tests pass. | Passed | 2026-10-11: 46 pass, zero failures. The editor build has zero warnings and errors. No test source changed. This selection does not clear the separate cooked YAML failure. |
| Existing material and import regressions | Run `SerializedSceneImporterTests`, `NativeFbxImporterTests`, and `SourceToonConversionBaselineTests`. | All selected tests pass. | Passed | 2026-10-11: 31 pass, zero failures. The final editor build has zero warnings and errors. A broader selection has nine stale source-text assertion failures in `ImportedTextureStreamingContractTests`; expected tokens are absent at the asserted paths in both HEAD and the working tree. No test source changed. |
| Native material contract regressions | Run the existing material database, scene identity, and source toon conversion tests. | All selected tests pass. | Passed | 2026-10-11: 26 pass after the Mochie correction. The isolated editor build has zero warnings and errors. A separate deformation selection has one existing owner-history confirmation failure; see the investigation. No test source changed. |
| Backend logs | Stop the owned session and inspect import, shader, Vulkan, and rendering logs. | No unresolved non-teardown error affects the measured frame. | Focused lifetime checks passed; recoverable deferrals remain | 2026-10-14: final native PID 97292 and Assimp PID 72228 have no disposed-resource failure, retired-buffer rejection, renderer pause, or target-allocation failure. Cold mesh preparation, import-time deformation publication, material-table growth, and target preparation can defer a frame. They recover before measured output. Replacement captures execute native shading with all 2,073,600 diagnostic pixels valid. RenderDoc suppresses validation-layer debug output, so these runs do not establish clean VUID validation. The final Debug build has zero warnings and errors. No test source changed. |

The 2026-10-12 Debug readiness check also repeated Assimp startup with the same
final binary. Preparation completed in 5,712 ms without a shader reload. The
settled package used strategy 2 with no downgrade or terminal fault. Front and
oblique output was viewed. The log had zero VUID and shader regeneration
messages, one startup reservation error, and 42 texture source-mismatch stage
warnings. This startup timing is not a frame-time measurement.

## Model Inspector Checks

Use the default model editor with the composed prefab. Enable and disable
`Render Bounds` on one component. Inspect front and oblique views. Other
components' bounds switches must remain independent. Repeat with the selected
CPU or GPU bounds source; the source setting is separate from mesh submission.

Open **Blendshapes (All LODs)** and the per-submesh list. The **Hide Blendshapes
Without Effect** switch must start enabled. A shape with nonzero position,
normal, or tangent deltas must remain visible even when its weight is zero.
Disable the filter to show all imported channels. The operation must preserve
weights and source data. A name used by any LOD stays in the all-LOD list.

The 2026-10-14 Release native check shows the bounds in front and oblique
views. The Body and Face switches act independently. The default filter shows
three Body entries, including an effective zero-weight entry. The unfiltered
list exposes the other channels. The check does not yet cover GPU bounds
readback or unavailable CPU blendshape data.

The final 2026-10-15 repeat passes the CPU bounds on/off and camera-move checks
without selection in both routes. Front, oblique, and off-images were viewed.
The old dark residue is gone after explicit stencil initialization. RenderDoc
confirms that the first visibility pass clears depth and stencil. The exported
stencil channel has zero nonzero pixels after bounds are disabled, compared
with 48,824 before the fix. These checks use FXAA. MSAA and live GPU bounds
remain separate checks.

## Screenshot Display Transfer

Capture the same fixed front and oblique views with
`include_screen_space_ui=false` and `true`. Compare an unobstructed scene region.
The float SDR viewport target must report `readback.output_transfer=LinearToSrgb`.
The composited sRGB swapchain must report `Unchanged`. Alpha and raw texture
readback must keep their existing contracts.

The 2026-10-14 Release check passes for both prefab routes in Vulkan Advanced.
All four image pairs were viewed. Mean absolute RGB error in the fixed scene
region is 0.047 to 0.049 byte levels out of 255; the 99th percentile is one.
The earlier dark PNGs must not be used for absolute brightness or Unity color
acceptance. HDR output and other presentation paths need separate checks.

## Performance Checks

Measure warmed Release frames after import and shader readiness. Keep the camera and output fixed. Record an empty-scene baseline, one still avatar, and one moving avatar. Report CPU and GPU frame time at P50, P95, and P99. Record active vertices, triangles, bones, morph channels, material kernels, draw calls, texture memory, skinning cost, shadow cost, late transparency cost, overdraw, shader cache misses, and frame allocations.

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Frame budget | Measure at the chosen production output and target refresh rate. Include the whole frame. | P95 fits the selected 16.7 ms, 11.1 ms, or 8.3 ms budget for 60, 90, or 120 Hz. Report P99 stalls separately. | Release editor measurements available; production acceptance open | 2026-10-14: the still-avatar baseline is 57.01 FPS in native and 61.82 FPS in Assimp. Continuous 128-pixel Advanced probe capture measures 27.08 to 29.03 FPS. These are 30-second completed-presentation windows at 1920 by 1080. See the workload and recovery limits below. |
| Feature variants | Compare cold preparation and warmed output while values and compatible materials change. | Values use material rows. Warmed frames do not compile known feature variants or multiply kernels per material value. | Open | none |
| Submission and allocation | Inspect the resolved submission strategy, GPU work counters, and frame allocation data. | Native opaque work keeps GPU submission without count readback. Any permitted CPU late submission is reported separately. | Partial | 2026-10-11: both routes retain Advanced admission, requested and resolved strategy 2, and no downgrade during source playback. Allocation and dynamic growth remain open. |
| Visibility and LOD | Measure the selected outfit before changing geometry or textures. Disable unused alternatives. Compare close and distant output after any LOD change. | Cost falls while the required silhouette, deformation, and material borders stay correct. | Open | Current geometry counts are unknown. |
| VR | Repeat with both views, production shadows, active animation, and required physics. | The stereo frame meets the selected full-frame budget without eye mismatch or temporal artifacts. | Open | none |

### Release Continuous Capture Result

The 2026-10-14 checks use the Release ImGui editor, Vulkan Required Advanced,
GPU indirect submission, FXAA, manual exposure 1, and no skybox or shadows.
Vulkan validation, RenderDoc, and code-profiler logging are disabled for the
30-second throughput windows. Only the owned editor process is present.
The output is 1920 by 1080. These checks use a still avatar, not animated VR.

| Completed desktop presentations per second | Native FBX prefab route | Assimp prefab route |
|---|---:|---:|
| Before capture | 57.01 | 61.82 |
| Fixed probe, capture active | 29.03 | 27.53 |
| Moved probe, capture active | 28.55 | 27.08 |
| First window after removal | 46.56 | 52.90 |
| Later window after bounds and selection reset | Not repeated | 62.14 |

The later detailed native profile returns to an 18.30 ms median presentation
interval, compared with 17.95 ms before capture. Assimp has Face bounds and
selection visible in the later capture images. Successful commands disable
Body and Face bounds, clear selection, and restore the fixed camera before the
extra recovery window. There is no saved property readback after those setters.
The earlier Assimp windows therefore have an editor-overlay workload caveat;
they are not a controlled comparison of importer cost.

Native completes 453 probe updates and Assimp completes 463. Both remain active
for more than 180 seconds. Their final irradiance and prefilter arrays contain
finite, nonzero radiance. Viewed front and oblique images retain the avatar.
No detailed profile reports a rejected frame. Fixed and moved profiles contain
recoverable preparation deferrals: 536 and 536 in native, 568 and 532 in Assimp.
Detailed profile logging changes throughput; do not substitute its FPS for the
separate unprofiled windows above.

After removal, native allocation bytes settle at 3.26 GB and 3.22 GB. Both have
three planners with 616.40 MiB of image storage. Direct-compute descriptor-set
counts return to eight in each route; compute uniform-buffer counts return to
eight and six. The remaining 36 pending image/view records belong to the fixed
capture bank. This is bounded target retention, not proof of complete resource
reclamation. Frame-data reservation counts still grow to 8,875 and 9,283. The
capture ownership item remains open in the code todo. Continuous capture does
not yet satisfy a long-run resource or production frame-budget acceptance gate.

### Continuous Capture Follow-up Checks

These checks remain open. Track code changes in the [capture ownership](../../todo/rendering/unity-poiyomi-advanced-parity-todo.md#capture-frame-data-ownership) and [capture performance](../../todo/rendering/unity-poiyomi-advanced-parity-todo.md#continuous-probe-capture-performance) items.

| Check | Procedure | Acceptance |
|---|---|---|
| Long-run reservation growth | Run continuous capture through both Unity prefab import routes in Vulkan Required Advanced. Record completed captures, live family and frequency reservation counts, reserved bytes, and retained resources over time. Run enough updates to exceed the old reservation limit at the recorded growth rate. | Counts reach a stable bound for a fixed live workload. Capture continues without reservation exhaustion, rejected preparation, missing meshes, or invalid GPU access. A short run below the old limit is insufficient. |
| Owner retirement and range reuse | Repeat probe creation, movement, capture stop, restart, and removal. Keep a second probe active during removal. Wait for recorded completion and retirement conditions. | Removed owners release their reservations. Surviving owners retain valid ranges and images. Reused ranges have valid allocation identities. Shared resources remain valid until all readers release them. |
| Continuous capture cost | Repeat the recorded Release workload for both prefab routes. Confirm the same camera, bounds and selection state, output resolution, capture resolution, and request interval by readback. Measure fixed and moved probes. Use separate stage-profile and unprofiled throughput windows. | Report before/after completed presentations, completed probe updates, CPU and GPU P50/P95/P99 times, allocation counts, and preparation deferrals. Demonstrate lower capture cost without reducing update throughput or image quality. Production acceptance still requires the selected frame budget. |
| Image and recovery checks | View front and oblique images during capture and after probe removal. Inspect irradiance and prefilter output. Repeat the no-capture measurement after retirement settles. | Both routes preserve geometry, materials, and finite probe lighting. No flicker or stale lighting appears. Memory and frame times return to the expected live-workload range. |

## Hardware Matrix

| Hardware | Backend and output | Result |
|---|---|---|
| NVIDIA GeForce RTX 3090, AMD Ryzen 9 7950X3D, driver 617.14 | Vulkan, desktop mono, 1920 by 1080 | Both prefab routes reach Release Advanced admission and complete continuous probe capture. Native Uber material rows, masked coverage, sorted alpha, and live texture controls execute. The timing table records editor throughput. Matched Unity lighting, long-run reservation lifetime, production frame-budget acceptance, and VR remain open. |
| Production VR hardware | Vulkan stereo, production per-eye resolution | Not measured. |

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
| Scene capacity growth | Collection maps GPU-authored LOD storage without an active render owner. | Linked investigation and scene publication code item. |
| Cooked YAML regression | Wrapped runtime mesh payload becomes a failed-mesh placeholder. | Linked investigation; baseline reproduction remains open. |
| Native Uber image parity | The source lighting adapters are implemented. Matched Unity lighting and production output remain unvalidated. The fixed harness patch retains its saved state. | Linked investigation and the lighting rows above. Focused probe and local-light lifetime controls pass. The Release result below records continuous capture and remaining long-run and frame-budget limits. |
