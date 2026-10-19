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
Disable authored SH. Disable reflections, clearcoat, emission, rim, and
matcaps on the selected Face and Tank materials. Set their metallic multiplier
and minimum light brightness to zero. Move the static probe outside its influence
and back for lighting modes 0, 2, and 6. Compare front and oblique images. Restore
all saved values. Confirm nonzero irradiance and valid native shading in a GPU
capture. This separates diffuse probe lighting from source SH and reflections.

Repeat the source stylized controls with authored SH enabled. Use both the
recorded Unity coefficients and zero coefficients. Probe movement must not add
diffuse light in either case. Restore reflection features and confirm that
probe specular remains visible. Lighting mode 6 must retain probe diffuse.

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

Native advances 453 capture generations and Assimp advances 463. Both remain active
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
reclamation. Frame-data reservation counts grew to 8,875 and 9,283 in that run.
The new allocation ownership implementation still needs runtime acceptance.
Continuous capture does
not yet satisfy a long-run resource or production frame-budget acceptance gate.

### Continuous Capture Follow-up Checks

The allocation ownership, shader uniform rewrite, and source-target retention
changes pass isolated Release builds with zero warnings and errors. Native
startup settles all 125 textures with no pending transitions, active uploads,
or texture validation failures. The 36 initial shadow-descriptor failures stop
increasing. Viewed front and oblique images retain the full avatar.

Native component and realtime-property stop/restart checks pass with two
probes. Both continue to publish after restart. Reservations settle at 91 with
two probes, 64 with one, and 34 after removal, compared with the initial desktop
baseline of 32. Desktop rendering stays active. This does not establish full
physical reclamation or long-run acceptance. Source-target retention removes
the dominant sampled face-target planner replacement. Desktop registry
publication still rebuilds internal buffer storage.

Assimp passes component and realtime-property restart, three rapid activation
cycles, timed capture stop/restart, and removal with a second probe active.
Two active probes retain 91 reservations and 24,112 bytes. Removing both leaves
34 reservations and 14,800 bytes. Standard Vulkan validation remains at zero
errors. One extra warning repeats an existing unused vertex-attribute message;
there is no new message identity. Synchronization validation is disabled.
The native long-run check remains open. The Assimp reservation result is below.

The [ownership contract](../../../architecture/rendering/default-render-pipeline-notes.md)
describes allocation retirement. The [investigation](../../investigations/rendering/2026-10-08-jax2031-unity-advanced-parity.md#2026-10-16-capture-reservation-and-convolution-work)
records failed builds and controls. The [desktop image reuse contract](../../../architecture/rendering/default-render-pipeline-notes.md#desktop-internal-image-reuse)
describes the implemented storage retention rules. This document owns the
remaining runtime acceptance checks.

The diagnostic-disabled native comparison uses the same Release scene and
settings hashes as the preceding allocation-ownership run. At 1920 by 1080,
the no-probe window records 45.92 FPS. One fixed 128-pixel probe with 16-pixel
irradiance and a 100 ms request interval records 32.43 FPS and 2.72 completed
capture generations per second. The preceding fixed-probe result was 26.20 FPS.
The moved-probe window records 36.80 FPS and 2.98 generations per second. These are
30-second windows with only the owned editor running.

The Assimp repeat uses `CompletedIblPublicationCount` for update rates. Baseline,
fixed-probe, moved-probe, and removed-probe windows record 46.76, 31.99, 32.38,
and 46.57 FPS. Fixed and moved capture publish 82 and 83 completed outputs in
about 30.10 seconds each (2.72 and 2.76 per second). Active reservations remain
at 64 entries and 20,128 bytes. Planned image storage returns from 636.64 to
616.40 MiB after capture stops. These measurements keep the same 128/16-pixel
capture sizes and 100 ms request interval. The native completion-counter repeat
remains pending.

Separate detailed CPU profiles show retirement P50/P95/P99 changing from
2.486/38.330/45.321 ms to 1.134/19.723/22.017 ms. Mean retired buffers per frame
fall from 52.74 to 13.44. Render-dispatch P95 changes from 63.495 to 46.155 ms.
Profiles have different sample counts and add measurement cost. They support
the improvement but do not supply an isolated causal timing result.

A separate Assimp diagnostic enables `XRE_GPU_TIMESTAMP_DENSE=1` and standard
Vulkan validation. The 64 baseline samples report named Advanced GPU scope
P50/P95 of 4.191/5.422 ms. The next 41 samples report 4.578/5.702 ms, but six
precede the first frame with capture scopes. Desktop and capture work share the
Advanced scope name, so this dump cannot isolate probe GPU cost. It also does
not measure the whole GPU frame. Keep dense timestamps off for throughput
windows. Standard Vulkan validation remains at zero errors after this check.

| Check | Procedure | Acceptance |
|---|---|---|
| Long-run reservation growth | Run continuous capture through both Unity prefab import routes in Vulkan Required Advanced. Record completed captures, live family and frequency reservation counts, reserved bytes, and retained resources over time. Run enough updates to exceed the old reservation limit at the recorded growth rate. | Counts reach a stable bound for a fixed live workload. Capture continues without reservation exhaustion, rejected preparation, missing meshes, or invalid GPU access. A short run below the old limit is insufficient. |
| Owner retirement and range reuse | Repeat probe creation, movement, capture stop, restart, and removal. Keep a second probe active during removal. Wait for recorded completion and retirement conditions. | Removed owners release their reservations. Surviving owners retain valid ranges and images. Reused ranges have valid allocation identities. Shared resources remain valid until all readers release them. |
| Continuous capture cost | Repeat the recorded Release workload for both prefab routes. Confirm the same camera, bounds and selection state, output resolution, capture resolution, and request interval by readback. Measure fixed and moved probes. Use separate stage-profile and unprofiled throughput windows. | Report before/after completed presentations, completed probe updates, CPU and GPU P50/P95/P99 times, allocation counts, and preparation deferrals. Demonstrate lower capture cost without reducing update throughput or image quality. Production acceptance still requires the selected frame budget. |
| Image and recovery checks | View front and oblique images during capture and after probe removal. Inspect irradiance and prefilter output. Repeat the no-capture measurement after retirement settles. | Both routes preserve geometry, materials, and finite probe lighting. No flicker or stale lighting appears. Memory and frame times return to the expected live-workload range. |

Use `CompletedIblPublicationCount` deltas for completed-update counts and rates.
`CaptureVersion` identifies a published attempt generation. Failed attempts can
leave gaps, so its delta is not an exact completion count. Earlier measurements
that use that property report generation rates only.

Target at least 7,000 completed probe updates per import route for the long-run
check. The earlier run added about 20 reservations per update. At that rate,
7,000 updates exceed the mapped arena's 131,072-reservation limit. Keep the
128-pixel face size, 16-pixel irradiance size, and 100 ms request interval.

The Assimp long run completes 7,025 valid IBL publications in about 44 minutes.
From five minutes onward, all 114 samples contain 64 reservations, 20,128
reserved bytes, and 633.75 MiB of planned image storage. Native allocation bytes
range from 4.05 to 5.07 GB while retirement work drains; this is not monotonic
growth. Removing the probe restores the pre-probe reservation and image values:
34 entries, 14,800 bytes, and 616.40 MiB. Native bytes settle at 2.94 GB versus
2.85 GB before capture. The 36 pending retirement records remain, so this is
not proof of complete physical reclamation. Desktop presents continue without
a terminal fault. This passes the Assimp reservation-growth check only.

The user reports roughly one-second stalls and simultaneous FPS text flicker
during this run. Frame pacing and UI correctness therefore fail acceptance.
The user confirms that both symptoms stop after probe removal. A later Release
correction moves active imported-resource publication to command-collection
swap. Its Assimp control has zero rejected packages and no desktop or UI decline
reason in all 90 samples, including 54 with capture active. Three viewed active
screenshots show the avatar and FPS text. Capture still has a 69.267 ms sampled
render-thread CPU maximum and an 18.240 ms P95 retirement drain in sparse MCP
samples. Frame-time acceptance remains open. The native long run stays pending
while this cost is investigated.

The later internal-image reuse build passes the Assimp probe ownership controls
with Vulkan synchronization validation enabled. This includes two probes,
activation and realtime restart, three rapid restart cycles, timed stop/restart,
and removal. Validation errors remain zero; the one added warning repeats the
existing `Shader-OutputNotConsumed` identity. Reservations return from 91 with
two probes to 34 after removal. Viewed front, oblique, and post-removal images
show the full avatar. The Release build has zero warnings and errors.

This does not establish a performance improvement. The user requested a stop
before the clean repeat completed texture warmup. Donor eligibility needs a
focused check because a new filtered graph object can prevent image reuse.
Keep clean frame-time measurements, final NativeOnly and AssimpOnly comparison,
and the native 7,000-publication run pending. The owned validation editor is
stopped; saved evidence remains available in the existing validation run.

## Image-Retention Performance Repeat

The 2026-10-17 diagnostic confirms MainViewport reuse of 56 internal image
groups. The exact graph-reference guard does not block ordinary probe updates
in this workload. No relaxed graph comparison is needed.

The separate Assimp Release run disables validation and retirement diagnostics.
It retains the 1920 by 1080 output, 128-pixel probe faces, 16-pixel irradiance,
and 100 ms request interval. Only the owned editor runs during measurement.

| Window | Completed presentations per second | Completed IBL publications per second |
|---|---:|---:|
| Capture off | 53.43 | Not applicable |
| Fixed probe | 37.75 | 4.75 |
| Moved probe | 43.33 | 5.25 |
| Probe removed | 51.02 | Not applicable |

Separate detailed profiles add measurement cost. Fixed capture records
presentation interval P95/P99/maximum of 33.144/36.435/46.863 ms, versus
135.496/143.676/160.201 ms in the earlier buffer-only fixed profile. Retirement
P95 falls from 19.077 to 1.247 ms, and image retirements fall from 20,011 to
1,131. Both fixed and moved profiles have zero rejected frame packages.
The removed profile has no physical-plan misses or image/buffer retirements.

Active front, oblique, and HUD-inclusive images were viewed. The complete
avatar and FPS text are visible. All 57 active HUD samples have no rejected
packages, no main/UI decline reason, and nonempty FPS text. Reservations are
93 entries/21,488 bytes during active capture and 63/16,160 after removal;
this is above the cold count and does not prove complete reclamation.
NativeOnly passes the same ownership and restart controls with standard and
synchronization validation enabled: zero errors and no new message identity.
Its one added warning repeats `Shader-OutputNotConsumed`. Reservations return
to 34 entries/14,800 bytes after removal. Active front and oblique images were
viewed and show the complete avatar.

The separate NativeOnly Release run uses the same output and capture settings.
Capture-off, fixed, and moved windows measure 57.81, 45.99, and 45.21 FPS.
Fixed and moved capture complete 5.62 and 5.56 IBL publications per second.
Separate detailed profiles have presentation interval P95/P99/maximum of
27.241/30.322/40.829 ms for fixed capture and 27.584/31.007/42.664 ms for moved
capture. Retirement P95 is 0.983 and 1.001 ms. Neither active profile rejects
a frame package. Reservations stay at 64 entries/20,128 bytes.
Viewed irradiance and prefilter images contain finite output; irradiance is
nonzero and prefilter includes avatar features. Native returns to 56.71 FPS,
34 reservations/14,800 bytes, and 616.40 MiB of planned images after removal.
All 64 active HUD samples have zero package rejections, no main/UI decline,
and nonempty FPS text. Viewed baseline, active, and post-removal front and
oblique images preserve the avatar.

The image-retention NativeOnly long run completes 7,019 valid IBL publications
in about 21 minutes. All 59 samples after one minute contain 64 reservations,
20,128 reserved bytes, and 633.747 MiB of planned images. Native allocation
stays between 2,783,822,128 and 2,785,510,448 bytes without a growth trend.
Completed presentations continue and no terminal fault occurs. Removal restores
34 reservations/14,800 bytes and 616.397 MiB of planned images. Settled native
allocation is 65,536 bytes above the pre-probe value, with 36 retirement records
still pending. This passes the Native reservation-growth check; it does not
prove complete native reclamation.

The Assimp 7,025-publication reservation run predates the final image-retention
change. Current Assimp validation covers synchronization controls, images,
HUD admission, and short-run performance. Native supplies the image-retention
build's long-run result. Both routes pass the measured desktop capture-stall checks.
Production frame-budget, matched Unity lighting, and VR acceptance remain open.

## Scene Publication CPU Repeat

Keep the same Release, Vulkan, Advanced, desktop output, and probe settings
for both prefab routes. The saved image-retention profiles are the baseline.
Their capture-off, active, and removed windows average 12.19/12.35/11.99 ms
per plan-and-commit call for Assimp and 11.08/11.03/11.04 ms for Native.
These baseline values exclude gate time. Every call fails the unchanged-row
gate; neither route has a prepared hit.

After the correction, record publication calls, gate outcomes, and commit
time from the same counters. Measure unprofiled presentation throughput
separately from detailed CPU profiles. Disable diagnostic flags for throughput.
Check capture off, a fixed probe, a moved probe, and probe removal. View front,
oblique, and active HUD images. Record rejected packages and validation errors.

Check live invalidation before accepting faster reuse. Change and restore the
tank detail-normal strength, a source node's visibility, and a transform.
Confirm the runtime values and the visible result. Confirm that publication
handles each change and resumes reuse after the scene settles. Keep the saved
harness state. Lower CPU cost alone does not pass these checks.

Both corrected prefab routes pass scalar, visibility, and transform changes
and restore their original values. Each change rejects the unchanged-row
gate briefly; settled reuse resumes. Native also changes and restores cuff
roughness in the newly admitted standard-texture material. Viewed Native
images during material edits show the full avatar and HUD. Viewed restored
and active Assimp front and oblique images also preserve the avatar and HUD.
Direct texture-binding replacement was not exercised because the available
MCP tools do not expose that operation.

The clean Assimp Release run has diagnostics disabled and 125 settled textures.
Each cadence window lasts about 30 seconds; detailed CPU profiles are separate.

| Capture state | Previous FPS | Corrected FPS | Corrected IBL updates/s | Gate plus commit ms/call | Gate failures | Rejected frame packages |
|---|---:|---:|---:|---:|---:|---:|
| Off | 53.43 | 153.73 | Not applicable | 0.665 | 0 | 0 |
| Fixed probe | 37.75 | 113.08 | 6.175 | 0.685 | 0 | 0 |
| Moved probe | 43.33 | 109.78 | 5.517 | 0.710 | 0 | 0 |
| Probe removed | 51.02 | 160.27 | Not applicable | 0.647 | 0 | 0 |

Active reservations stay at 64 entries/20,128 bytes. Removal returns them to
34/14,800 bytes and restores planned images to 616.397 MiB. Viewed probe
outputs contain finite, nonblack data. Sampled images and package counters
pass; they do not prove the absence of every transient flicker.

Separate Assimp detailed profiles record presentation interval P95/P99/maximum
of 13.678/17.240/39.429 ms for fixed capture (6,245 frames) and
13.764/16.484/55.720 ms for moved capture (6,095 frames). The earlier fixed
profile was 33.144/36.435/46.863 ms. Retirement P95 is 0.681 ms for fixed
and 0.678 ms for moved capture. Capture-off and removed presentation P95 are
8.498 and 8.781 ms. All four profiles have zero rejected frame packages.

The Native repeat uses the same Release binary and settings. Only the owned
editor runs during each measured cadence window. All textures are settled.

| Capture state | Previous FPS | Corrected FPS | Corrected IBL updates/s | Gate plus commit ms/call | Gate failures | Rejected frame packages |
|---|---:|---:|---:|---:|---:|---:|
| Off | 57.81 | 154.34 | Not applicable | 0.604 | 0 | 0 |
| Fixed probe | 45.99 | 105.62 | 5.648 | 0.649 | 0 | 0 |
| Moved probe | 45.21 | 109.56 | 6.013 | 0.630 | 0 | 0 |
| Probe removed | 56.71 | 169.13 | Not applicable | 0.563 | 0 | 0 |

Native reservations remain 64 entries/20,128 bytes during capture and return
to 34/14,800 bytes after removal. Planned images return from 636.639 to
616.397 MiB. Viewed probe outputs are finite and nonblack. Viewed active front
and oblique images show the avatar and HUD. No terminal fault occurs.

Separate Native detailed profiles record presentation interval P95/P99 of
14.130/16.622 ms for fixed capture and 13.960/17.622 ms for moved capture.
The earlier fixed profile was 27.241/30.322 ms. Retirement P95/P99 is
0.853/0.966 ms for fixed capture and 0.813/0.927 ms for moved capture.
Render-dispatch P95/P99 is 13.607/15.887 ms and 13.379/16.992 ms, respectively.

This correction passes the recorded desktop performance and live-edit checks
for both import routes. The Release build has zero warnings and errors. These
clean runs disable Vulkan validation and diagnostic logging; they do not add a
new synchronization-validation or long-run result to the earlier build scope.
No tests are added, changed, or run during this feature-debugging work.

## Live Texture Replacement Repeat

Use typed MCP binding discovery and replacement in a named isolated Release
editor. Import the Unity prefab through each route with Vulkan, Advanced, and
the GPU indirect submission strategy. Wait for 125 settled textures before
changing bindings. Enable Vulkan synchronization validation for this repeat.

| Check | Assimp | Native FBX |
|---|---|---|
| Standard-texture cuff legacy and surface pair, readback, receipt restore, Undo, and Redo | Pass | Pass |
| Native-Uber Tank replacement, readback, and restoration | Pass; GPU trace confirms the donor sample | Pass; active matcap replacement also changes the image |
| Native-Uber Face visible replacement and restoration | Pass | Pass |
| Native-Uber legacy null and restore; StandardLit null rejection | Pass | Pass |
| Invalid slot, invalid texture ID, and stale material guard leave bindings unchanged | Pass on the preceding build; guard code is unchanged | Pass on final paired-helper build |
| Repeated replacements with capture active preserve publication and resource lifetime | Pass; six cycles and 78 IBL updates | Pass; six cycles and 106 IBL updates |
| Edited and restored front/oblique images preserve other meshes and HUD | Pass in sampled images | Pass in sampled images; fixed-camera hair repeat also passes |
| Publication updates changed bindings and resumes steady reuse | Pass | Pass; 568 steady calls, zero gate failures or rejected packages |
| No new Vulkan synchronization error or terminal fault | Pass; zero cumulative validation errors | Pass; zero cumulative validation errors |

For the cuff's native StandardLit path, select each collection in turn. Confirm
that the setter updates the legacy albedo and semantic BaseColor pair together.
Confirm that one receipt, Undo, and Redo each restore or apply both references.
Reject null and incompatible donors without changing either reference. Its native
admission contract requires the two references to match. A binding readback alone
does not prove a rendered change.
The Tank matcap replaces almost all main-texture color. A GPU trace confirms
the donor texel reaches shading, followed by a matcap blend near 0.999999.
Use the Face as the visible native-Uber replacement control. The initial cuff
views did not identify visible cuff pixels, so that result proves pair integrity
and Undo behavior only. Native additionally changes and restores the active Tank
matcap, with a clear blue-to-dark-navy-to-blue result. A suspected hair change
in the first comparison is ruled out by pixel comparison and eleven fixed-camera
images before, during, and after a second edit.

Both route runs use the same paired-helper Release build, with zero compiler
warnings or errors. Each has zero rejected frame packages and zero cumulative
Vulkan validation errors. The Native run records four recoverable deferrals and
nine rejected preparation outcomes during warmup, before texture readiness.
These counts do not grow during edits. Its final 552-frame steady window has
552 completed frames, 552 gate passes, and zero new deferrals or rejections.
Runtime validation reports 16 warnings per route; this is not a warning-free
runtime result. Logs contain no VUID, synchronization hazard, device-loss,
terminal-fault, or unhandled-exception match.

The final paired Assimp build repeats pair integrity, null rejection, Undo,
Face images, and active-probe swaps. Its wrong-target, blank-ID, and consumed-
receipt controls were checked on the preceding build with unchanged guard code.
Native repeats those guards on the final build. Cuff appearance was not accepted
in this initial repeat. The follow-up below closes that gap with isolated views.
These short edit runs do not establish a new long-duration soak or performance
benchmark. No tests were added, modified, or run during feature validation.

All edited references were restored by receipts. Authored assets and settings
were not saved. The owned editor and RenderDoc replay are stopped. Evidence is
in the existing ignored task run under `mcp-output/animation-texture-binding-`
and `mcp-captures/`; the linked investigation records the shader trace findings.

Restore original IDs and values after every control. Do not save authored assets
or destroy borrowed textures.

## Cuff Appearance And Texture Replacement

Import the actual Unity prefab through `AssimpOnly` and `NativeOnly` in Release,
with Vulkan, the Advanced pipeline, GPU indirect submission, and synchronization
validation. Wait for the texture queue to settle. Use the same build for both
routes. Inspect the exact `Acc - Leg Cuffs` node and its `Cuffs 2` material.

Keep the saved outfit as the default. Capture the ankles with boots enabled,
then disable only `SHO Geos` temporarily. The cuff rings must appear. Also inspect
isolated front, back, and oblique views to separate occlusion from missing
geometry. Restore every temporary component state.

Check the converted source albedo and normal map in both the legacy and semantic
collections. Check linear normal metadata, scalar metallic and roughness, and
native admission. Replace BaseColor with a visible compatible donor and inspect
the rendered change. Restore with its receipt and inspect the original image.
Repeat the normal role with a compatible live donor when available. Check pair
identity after each edit and restore. Keep all source assets unchanged.

Record Vulkan errors, frame rejections, readiness failures, and the settled
publication gate after edits. A binding readback alone does not pass appearance.
The corrected Release build passes both routes on 2026-10-17. Each route settles
131 tracked textures with empty decode, transition, and upload queues. The boots
cover the cuffs in the saved outfit. Hiding only `SHO Geos` exposes them; restoring
that component covers them again. No mesh submission correction is needed.

| Check | AssimpOnly | NativeOnly |
|---|---|---|
| Cuff appearance with boots hidden; saved boot state restored | Pass | Pass |
| Source BaseColor and Normal aliases and linear normal metadata | Pass | Pass |
| Visible BaseColor change and receipt restoration | Pass | Pass |
| Visible Normal change and restoration of the source fishnet pattern | Pass | Pass |
| Source numeric values: white tint, opacity 1, metallic 0, roughness 0.5, RGB normal mode 0 | Pass | Pass |
| Three replacement cycles during continuous probe capture | Pass | Pass |
| Settled publication reuse after edits and probe removal | 549/549 calls; zero failures | 638/638 calls; zero failures |
| Restored wide front and oblique avatar and HUD views | Pass | Pass |
| Vulkan synchronization errors and rejected frame packages | Zero | Zero |

The edit windows include expected publication invalidations: Assimp records
1,428 calls, 1,380 passes, and 48 misses; Native records 1,506 calls, 1,458 passes,
and 48 misses. Native's first recovery window has one additional
`row_inputs_changed` miss. Its next settled window passes all 638 calls.
No persistent publication defect is found.

Both routes retain 16 cumulative Vulkan warnings and zero errors. The Release
build has zero compiler warnings and errors. Copied logs contain no VUID,
synchronization hazard, device loss, fatal error, or unhandled exception match.
All texture references and component states are restored. Source assets are
unchanged, and the owned editor is stopped. No tests were added, modified, or run.

Evidence uses `mcp-output/animation-cuffs-standard-{native,assimp}-...` and
`mcp-captures/` in the existing ignored task run. The linked investigation records
source and runtime findings. These checks cover cuff visibility and live edits;
full Unity lighting/pass-state parity, a new performance benchmark, and a new
long-duration soak are outside this result.

## Source Material Comparison

Use the same isolated Release build for `NativeOnly` and `AssimpOnly`. Import the
Unity prefab, retain its saved outfit, and use Vulkan, Advanced, GPU indirect
submission, and synchronization validation. Keep the capacity floor at 1024.
Use the established no-skybox, no-probe, manual-exposure fixture. These settings
compare the routes; they do not reproduce the Unity SceneView light.

Wait for all 131 tracked textures to settle. Read the active Socks and Tank
uniforms and texture sampler settings. Capture and view full front, oblique,
face, torso, and lower-leg images. Check the saved harness and the removal of
broad white Socks edges. Record readiness, frame rejection, publication reuse,
and cumulative Vulkan diagnostics after the images.

| Check | Expected result | NativeOnly | AssimpOnly |
|---|---|---|---|
| Socks ordinary color conversion | Matcap RGB 0.11754919; cubemap RGB 0.15071212; alpha 1 | Pass | Pass |
| Tank HDR theme and source sampler | Theme index 1 retained; matcap uses linear/trilinear filtering and repeat wrap | Pass | Pass |
| Source cubemap and disabled environmental rim | `_SourceCubeMap=1`; Socks `_RimEnviroIntensity=0` and source rim disabled | Pass | Pass |
| Front, oblique, and detail images | Saved outfit and HUD remain visible; no broad white Socks edge bands | Pass | Pass |
| Settled GPU publication and synchronization | No rejected frame packages, final readiness failures, or Vulkan errors | Pass | Pass |
| Tank environmental rim with a lit probe | Source settings retained; intensity on/off changes the rim; restoration returns to the original within small pixel variation | Pass | Pass |

For the probe control, add a temporary lit reflection-probe fixture after the
no-probe captures. Wait for capture and prefilter publication to complete.
Confirm that its prefilter contains light above the Tank threshold 0.07.
Keep the camera and scene fixed. Capture Tank rim intensity 0.26, then zero,
then 0.26 again. Inspect the change and check readback after restoration.
Remove the temporary fixture and repeat the settled diagnostics.

The final 2026-10-17 Release build has zero compiler warnings and errors. Both
routes use this build and serialized prefab producer version 6. Each settles
131 tracked textures with no pending work. Advanced GPU indirect submission
remains admitted without a fallback. The Tank matcap readback reports 512 by
512 pixels, 10 mips, `LinearMipmapLinear` minification, and `Linear` magnification.

The lit-probe front control changes 854 Native pixels and 843 Assimp pixels in
the Tank crop by more than eight channel levels. The restored image differs
from the original by that threshold at only 14 and 10 pixels, respectively.
Back views preserve image integrity, but hair covers most of the Tank there;
they do not independently prove the rim change. Both routes restore intensity
0.26 and remove the temporary probe.

In settled eight-second windows, Native completes 804 frames and records 805
publication reuse passes. Assimp completes 753 frames and records 753 passes.
Gate failure counts remain unchanged. Native retains two deferred and eight
rejected preparation outcomes; Assimp retains three and six. These warmup
counts do not increase in the settled windows and are not rejected frame
packages. Both routes have zero rejected frame packages and zero cumulative
Vulkan validation errors. Native records 15 warnings and Assimp records 16,
with ten suppressed in each run. Warnings concern the loader and unused shader
vertex outputs. Copied logs have no VUID, synchronization hazard, device loss,
fatal error, or unhandled exception match.

The owned editor is stopped. Source assets are unchanged. No tests were added,
modified, or run. Evidence uses `mcp-output/animation-parity-rimv6-...` and
`mcp-captures/` in the existing ignored task run. The linked investigation
records the copied log names and the limits of the image comparison.

The 2026-10-17 diagnosis separates material errors from lighting. On the preceding
build, directional intensity zero and a source-cubemap composition control do
not remove the broad white Socks edges. Changing only `_RimEnviroIntensity` from
1 to 0 removes them. All control values are restored and read back. The locked
source shader omits that disabled module. The linked investigation records the
source values, composition order, sampler policy, and importer gate.

Fresh Unity Prefab Mode images use scene lighting on and off, plus front
perspective and orthographic controls. The harness patch is visible in Unity.
Both projection controls retain dark Socks without broad white edges. The
different Unity light modes change overall brightness substantially. Full image
parity still requires matched lighting, reflections, camera, and post-processing.
These checks do not establish a new performance benchmark or long-duration soak.

## Matched Unity Lighting Comparison

Use an isolated Unity scene with the saved prefab. Record the source ambient SH
and sky settings. Add a sky-only reflection probe with resolution 128,
intensity 1, and box projection disabled. Export the sky in the engine's
equirectangular direction mapping. Verify all six axes with distinct colors
before comparing reflections. Do not change saved source material values.

Use the following controls in both engines:

| Input | Value |
|---|---|
| Output | 1920 by 1080, black background |
| Camera | Vertical FOV 45 degrees, near 0.01, far 100 |
| Front camera | Unity `(0,0.8,2.4)`, engine `(0,0.8,-2.4)`, target `(0,0.8,0)` |
| Oblique camera | Unity `(0.9,0.8,2.2)`, engine `(0.9,0.8,-2.2)`, same target |
| Directional light | White, intensity 1, no shadows |
| Light world forward | Unity `(-0.32139385,-0.76604444,0.5566705)`, engine with negative Z |
| Ambient | Recorded Unity SH with the engine Z reflection |
| Post-processing | Linear tone mapping, exposure 1, no automatic exposure, contrast 0, tint white, hue/saturation/brightness 1 |
| Additional effects | No AA, bloom, AO, or atmospheric scattering |
| Readback | Linear HDR source converted once to sRGB PNG |

The source uses Unity 2022.3.22f1, the Built-in pipeline, and Linear color space.
Set contrast explicitly. The engine default is 1; its shader multiplier is
`(1 + Contrast / 100)^2`. Only contrast 0 gives a multiplier of 1. Leaving the
default removes about 0.01 from dark linear colors before display conversion.
The Unity procedural sky uses exposure 1.3, atmosphere thickness 1, sun disk 1,
sun size 0.04, sun-size convergence 5, sky tint `(0.5,0.5,0.5)`, and ground
color `(0.369,0.349,0.341)`. Ambient and reflection intensity are both 1.
Use these engine-space authored SH vectors. The Z reflection changes A.Z,
B.Y, and B.W from the Unity values:

| Vector | XYZW |
|---|---|
| Ar | `(0.00000165075244, 0.01357105, 0.0102686621, 0.1140771)` |
| Ag | `(0.000003290644, 0.0475531, 0.0179880448, 0.118728213)` |
| Ab | `(0.000006000433, 0.0774984658, 0.0255630389, 0.107968338)` |
| Br | `(0.0000007895586, 0.00159547385, 0.039721176, 0.0000100206253)` |
| Bg | `(0.00000158869307, 0.00336496718, 0.0462580733, 0.0000117163545)` |
| Bb | `(0.00000297061069, 0.00701400451, 0.039331086, 0.000008570518)` |
| C | `(0.01943598, 0.0215091929, 0.0149047095, 1)` |

Capture front, oblique, and back views after all textures and probe convolution
are ready. Repeat with `NativeOnly` and `AssimpOnly` from the Unity prefab entry
asset. Keep GPU indirect submission active. Check the Vulkan validation count,
frame-package admission, and restored material settings.

For authored SH precedence, isolate diffuse on Face and Tank. Compare probe
inside/outside influence with authored SH enabled, enabled zero, and disabled.
Source stylized modes must not add probe diffuse when authored SH is enabled.
Disabled authored SH and generic PBR mode 6 must retain probe diffuse. Restore
the source reflection features and confirm their probe response.

For tail fluorescence, read back `_EmissionSlotSampling0.w` as 0.581. Capture
the saved value, zero, and restored value with the same light and camera.
The saved value must reduce tail emission without changing other emission
sources. Compare the restored tail with Unity. This control does not validate
source emission hue animation or all other emission modules.

For source hair anisotropy, read back `_SourceAnisoSettings`, both strand
layers, and both linear tints. Compare enabled, disabled, and restored states
on the same hair material. Check front and oblique highlights, including the
normal-map tangent basis. Saved source material values must remain intact.

For source matcap orientation, compare GPU texture samples at matching UVs.
Then compare GEOs intensity 2, zero, and restored 2. A temporary V-flip control
must affect only the procedural matcap sample, not the mesh-UV mask. Restore
the saved transform before acceptance captures.

The final comparison captures both prefab routes with 131 settled textures,
Advanced GPU indirect admission, a usable reflection probe, and neutral contrast
0. Native has 34 draws; Assimp has 29. Front, oblique, and back images were
viewed for both routes. Saved source anisotropy settings, matcap intensity 2,
and identity matcap texture transform are read back. Native SH precedence and
tail-fluorescence controls also pass in the earlier recorded run. The linked
investigation records the source contracts, GPU evidence, and discarded capture
attempts. Advanced admission alone is insufficient: require committed source
frames to advance before accepting captures.

Both final runs enable standard and synchronization validation. Each records
zero errors, 16 cumulative warnings with ten suppressed, and no PresentNow
terminal failure. Warnings concern the loader and unused shader outputs.
Native ends at render frame 21,751 with committed source frame 21,749; Assimp
reaches committed source frame 15,220. Earlier recoverable readiness tickets
remain in diagnostic history, but later scene commits confirm recovery.

Front-view interior mean absolute RGB differences use the 0-to-255 scale.
The mask requires a maximum RGB value above 8 in both images and removes a
two-pixel boundary. Masks differ by comparison; these are descriptive image
metrics, not isolated shader-error measurements.

| Region | Native versus Unity | Assimp versus Unity | Native versus Assimp | Native/Unity mask pixels |
|---|---:|---:|---:|---:|
| Tail | 2.50 | 2.52 | 0.51 | 11,307 |
| Tank | 2.91 | 2.76 | 0.71 | 23,115 |
| Hair | 3.40 | 3.27 | 0.57 | 33,506 |
| Skin | 2.67 | 2.59 | 0.54 | 40,667 |
| Boots | 2.81 | 2.77 | 0.54 | 12,028 |

These checks use the Release build with serialized prefab producer version 8
and the source matcap axis corrections. The build reports zero warnings and
zero errors. No tests were added, modified, or run. These image checks do not
extend the earlier performance or long-run acceptance to this build.

The contrast control resolves the broad dark-clothing and warm-hair mismatch.
Earlier images retained contrast 1 and cannot establish neutral post-processing.
Assimp front crop error falls from 10.76 to 5.32 for boots and 7.73 to 4.12 for
hair when only contrast changes to 0. Fine reflected highlight shapes still need
comparison. A source ramp-UV difference is inactive here because all four rows
of the GEOs ramp are identical. Do not compensate for remaining differences by
changing saved material brightness.

The fixture matches the actual Unity reflection probe at mip zero. The engine
then builds its own prefilter mips. Compare the Unity and engine higher mips at
matching roughness before changing the reflection blur mapping. Source shader
review confirms that the active Body, Face, Hair, Socks, and Tank shaders use
the same Mochie reflection and direct-specular formulas. Their probe LOD is
`6 * roughness`; they do not apply the generic Unity roughness remap at this
call site. The earlier source-version difference hypothesis is ruled out for
these active shaders. Current checks do not establish exact higher-mip parity.

## Reflection Filter Checks

For Unity realtime-filter parity, set `ReflectionFilter = UnityRealtime` before
assigning the environment or activating the probe. Keep the saved avatar
materials unchanged. The default `Ggx` producer is a separate filter contract.
Use a 128-pixel probe for the matched Unity fixture.
Its Unity-filter output must have a base extent of at least 512 and exactly
seven levels. Require `PublishedPrefilterConvention = UnityMip` after the
generation completes. Source reflection comparisons use LOD `6*r`; use the
legacy inverse mapping only for the GGX baseline.

First use an analytic environment with a constant red channel and first- and
second-order spherical patterns in green and blue. Check the source cube,
recursive filter levels, resolved cube, and final octahedral output separately.
Use the standard cube face orientation. Do not fit a rotation or reflection to
hide an orientation defect. In a producer GPU capture, require source writes
before dependent filter draws, resolve draws before octahedral conversion, and
nonzero expected data in every used level. A completed writer fence and
`HasUsableIblTextures` do not establish correct texture contents.

Then repeat with the actual Unity sky and the matched avatar fixture. Compare
uniform sphere samples at matching material roughness. Capture front, oblique,
and back views for both prefab routes in fresh processes with standard and
synchronization validation. Confirm that committed source frames advance.

### Previous GGX Comparison

Use the same matched fixture and saved material values. Regenerate the probe
after a prefilter change. For a 256-pixel octahedral output, require six physical
mips ending at 8 by 8. Confirm that the roughness-1 mip contains direction data.
Compare all levels against the exported Unity probe with a uniform sphere mask;
do not use an unweighted equirectangular image metric. Check exported row origin
at mip zero before interpreting higher-mip errors.

The corrected Native GPU capture passes the allocation and direction checks.
Two bound prefilter resources have identical contents at all six levels.
The final mip spans R 0.1322–0.2167, G 0.1337–0.2881, and B 0.1232–0.4299.
The source environment has twelve mip levels, so source-area LOD selection is
active. The separate Unity export matches the earlier source mip zero within
0.000001 linear RGB per channel on average.

Source Mochie and clearcoat use the inverse Unity perceptual remap only for
probe LOD. Environment rim, direct lighting, reflection BRDF factors, and
generic PBR keep their prior roughness contract. Over 20,000 sphere directions,
the conversion reduces probe RMSE at source roughness 0.1 from 0.0173 to 0.0103
and at 0.2 from 0.0246 to 0.0128. It slightly worsens samples from 0.7 through
0.9 and leaves roughness 1 unchanged. Treat it as a compatibility approximation,
not an exact reproduction of Unity's built-in convolution.

The fresh Native avatar control shows a small change: cheek RGB error falls
from 2.122 to 2.105, and skin interior error falls from 2.660 to 2.650.
The full-face crop is nearly unchanged. Front, oblique, and back views have no
material regression. These results do not establish a broad visual improvement
or complete higher-mip parity. The linked investigation owns the capture names,
raw measurements, rejected fitted multiplier, and process-restart record.

Final `NativeOnly` and `AssimpOnly` runs use the original Unity prefab entry,
the same Release binary, the corrected probe, and the same source-only shader
conversion. Both settle 131 textures and admit Advanced GPU indirect rendering
without a final preparation deferral. Native has 34 draws; Assimp has 29.
Front, oblique, and back PNGs are viewed for both routes. Both final runs have
standard and synchronization validation active, zero errors, and 16 cumulative
startup warnings with ten suppressed. Neither has a PresentNow terminal failure.
The separate RenderDoc baseline mutes validation output during capture; it is
GPU-content evidence, not the final validation-error check.

The following front-view results use the same two-pixel-eroded interior metric
and the 0-to-255 RGB scale described above.

| Region | Native versus Unity | Assimp versus Unity | Native versus Assimp |
|---|---:|---:|---:|
| Tail | 2.507 | 2.520 | 0.279 |
| Tank | 2.902 | 2.745 | 0.712 |
| Hair | 3.379 | 3.247 | 0.558 |
| Skin | 2.650 | 2.568 | 0.534 |
| Boots | 2.814 | 2.769 | 0.324 |

The Release build has zero warnings and zero errors. No tests are added,
modified, or run. Both owned editor sessions and the GPU inspection sessions
are stopped. The user's restarted Unity project remains open. These checks do
not extend long-run or production frame-budget acceptance; the open Unity
editor also prevents an isolated GPU throughput measurement.

### Unity Realtime Producer Comparison

The corrected producer GPU capture has one consistent texture-row orientation
at every resolved cube level. The following analytic comparison samples exact
cube texel-center directions. Values are linear RGB RMSE against the Unity
export. Sampling at these directions avoids differences between CPU face-edge
clamping and seamless GPU cube reconstruction.

| Resolved cube mip | RMSE |
|---|---:|
| 0 | 0.000181 |
| 1 | 0.000205 |
| 2 | 0.000252 |
| 3 | 0.000302 |
| 4 | 0.000338 |
| 5 | 0.000412 |
| 6 | 0.000192 |

The constant red channel stays between 0.4995 and 0.5. The initial 8-by-8
octahedral roughness-1 output has full-sphere RMSE 0.00411 against Unity.
This remaining error includes the finite octahedral reconstruction. It does
not establish an error in the recursive cube filter. The linked investigation
records the fixed Vulkan draw-order and face-row defects.

The first output used six canonical roughness levels. It improved broad
reflections but regressed source roughness 0.1 and 0.2. A Unity-only interpolation
control reproduces the error. That storage layout is rejected for final parity.
The corrected layout preserves seven source mip knots and publishes an immutable
sampling convention with the images. The source roughness sweep below checks
direct LOD `6*r` before the final avatar runs. When switching producer
modes, read the published convention and image layout after the new generation
completes; the requested mode must not reinterpret the old generation.

The seven-level GPU capture passes. Its two published 512-by-512 octahedral
images are byte-identical at every level. Each native level 0 through 6 best
matches the same Unity level. The following comparison uses 20,000 uniform
sphere directions and linear RGB RMSE. The Unity output uses direct LOD `6*r`;
the old GGX baseline uses the source inverse remap across its six levels.

| Source roughness | Previous GGX | Unity seven-level output |
|---|---:|---:|
| 0.1 | 0.010303 | 0.000857 |
| 0.2 | 0.012782 | 0.000828 |
| 0.3 | 0.024185 | 0.000876 |
| 0.4 | 0.030553 | 0.000933 |
| 0.5 | 0.034828 | 0.001471 |
| 0.6 | 0.027577 | 0.001634 |
| 0.7 | 0.026772 | 0.002214 |
| 0.8 | 0.018975 | 0.003081 |
| 0.9 | 0.020149 | 0.003361 |
| 1.0 | 0.026247 | 0.004951 |

Every tested roughness improves. The lowest-resolution octahedral levels retain
some reconstruction error. The viewed mip comparison has aligned sky gradients
and no visible orientation or color break. This measures probe radiance, not
complete avatar-image error.

Live filter switching also passes. Unity generation 1 publishes convention 1,
extent 512, and last mip 6. GGX generation 2 publishes convention 0, extent 256,
and last mip 5. Returning to Unity publishes generation 3 with the seven-level
layout. Committed frames advance with 34 draws and no preparation deferral.

The final seven-level avatar captures use the original Unity prefab entry,
the same Release binary, and the matched fixture for both import routes.
Both publish `UnityMip`, capture version 1, extent 512, and last mip 6.
Both settle all 131 textures with no pending transition and admit Advanced
GPU indirect rendering. Native has 34 draws; Assimp has 29. Front, oblique,
and back views are captured and viewed. The saved outfit remains intact.

The following front-view results use the same two-pixel-eroded interior metric
and 0-to-255 RGB scale. These broad region errors are close to the previous GGX
results. The probe-radiance improvement does not establish a similar reduction
in complete avatar-image error.

| Region | Native versus Unity | Assimp versus Unity | Native versus Assimp |
|---|---:|---:|---:|
| Tail | 2.507 | 2.516 | 0.502 |
| Tank | 2.909 | 2.758 | 0.712 |
| Hair | 3.401 | 3.272 | 0.573 |
| Skin | 2.652 | 2.577 | 0.542 |
| Boots | 2.814 | 2.764 | 0.526 |

Both final runs have standard and synchronization validation active: zero
errors, 16 cumulative warnings with ten suppressed, and no overflow. Native
render/committed frames reach 8294/8292; Assimp reaches 15208/15206. Both advance
without a terminal failure or current preparation deferral. Earlier recoverable
preparation retries occur at Native frame 353 and Assimp frame 6104. The final
logs contain no VUID, synchronization error, device loss, fatal, or exception
match. This does not clear the separate failed diagnostic startup recorded in
the investigation.

The final Release build has zero warnings and zero errors. No tests are added,
modified, or run. Scratch probes are removed. Both owned editor sessions and
all GPU replay sessions are stopped. The user's Unity project remains open.
These checks do not extend long-run, isolated performance, or VR acceptance.

## Startup And Resize Recovery

Use a fresh named Release session for each prefab import route. Keep standard
and synchronization validation active. Read the terminal state and committed
source frame during startup, after texture settlement, and after resize.

For a controlled modal-state check, invoke the existing window resize lifecycle
through MCP on the app thread. Read back `isInteractiveResize=true`, then end
the resize and restore the original dimensions in cleanup. Label this check as
a simulated modal callback; it is not an operating-system window drag. Check
ordinary client resize separately. Require resumed committed frames, no terminal
latch, and no current preparation deferral after resources settle. Restore the
matched camera and 1920-by-1080 output before the final avatar captures.

The saved frame-4589 failure is positive evidence of the policy mismatch in the
linked investigation. Two Native baseline starts and one Assimp baseline start
complete without reproducing the rare terminal. Normal resize and a simulated
modal-state interval also recover on the baseline. These controls show that
the failure is intermittent; they do not establish that the baseline is fixed.

The corrected Release build succeeds with zero warnings and errors. The Native
run enters simulated modal resize at startup; frame 423 confirms the active
modal state and committed frame 339. It then exits that state, settles 131
textures, and publishes the Unity probe. Advanced GPU indirect rendering admits
34 draws. Normal resize from 1920 by 1080 to 1600 by 900 and back also recovers.
Render/committed frames reach 13874/13872. Standard and synchronization validation
report zero errors and 16 warnings, with ten suppressed. The terminal remains
null. Front, oblique, back, and HUD captures are viewed. An editor hover outline
appears around the boots in the front capture; it is excluded from material
comparison. This run does not reproduce the precise cold-secondary deferral.

The Assimp run uses the same binary. Its simulated modal snapshot at frame 727
shows committed frame 567 and an active resize state. Ending the resize restores
normal presentation. Ordinary resize also recovers, with render/committed frames
12441/12439 after restoring the original size. The final matched fixture reaches
13145/13143, 131 settled textures, 29 draws, and Advanced admission. Its Unity
probe publishes convention 1 and capture version 1. Standard and synchronization
validation report zero errors and 16 warnings, with ten suppressed. The terminal
remains null. Front, oblique, back, and HUD captures are viewed; clearing the
selection removes the boot outline from the Assimp views.

Both runs finish without a current preparation deferral. The copied logs have
no VUID, synchronization error, device loss, fatal, exception, or secondary-
deferral terminal match. Both scratch probes are removed and owned editors are
stopped. The user's Unity project remains open. No tests are added, modified,
or run. Evidence is under `logs/startup-guard-final` and the
`animation-startup-guard-*` MCP snapshots in the existing validation run.

The policy fix passes code review and these live startup, resize, and avatar
checks. The rare `RecordDeferred` branch does not recur during the bounded
controls. Direct coverage of that exact trigger remains unconfirmed; these
results are not a long-run startup guarantee or an operating-system drag check.

## Hardware Matrix

| Hardware | Backend and output | Result |
|---|---|---|
| NVIDIA GeForce RTX 3090, AMD Ryzen 9 7950X3D, driver 617.14 | Vulkan, desktop mono, 1920 by 1080 | Both prefab routes pass Release Advanced capture, synchronization, image, and HUD checks within the recorded build scopes. The matched Unity fixture has captures for both routes with neutral contrast and an actual source probe environment. The seven-level filter passes the analytic and actual-sky roughness comparisons. Residual surface-image and octahedral reconstruction differences remain. Assimp and Native reservation runs exceed 7,000 completed updates on the earlier build. The timing table records editor throughput. Production frame-budget acceptance and VR remain open. |
| Production VR hardware | Vulkan stereo, production per-eye resolution | Not measured. |

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
| Scene capacity growth | Collection maps GPU-authored LOD storage without an active render owner. | Linked investigation and scene publication code item. |
| Cooked YAML regression | Wrapped runtime mesh payload becomes a failed-mesh placeholder. | Linked investigation; baseline reproduction remains open. |
| Native Uber image parity | Matched camera, lighting, probe environment, and neutral post-processing resolve the broad color mismatch. Residual surface-image differences and finite octahedral reconstruction remain. The fixed harness patch retains its saved state. | Linked investigation and the matched comparison above. Unity probe filtering passes the analytic and actual-sky roughness checks. The active source BRDFs match. Production frame-budget acceptance remains open. |
| PresentNow startup pause | A modal resize deferral is promoted to a strict presentation failure after recording, which latches the renderer. | The saved frame-4589 snapshot identifies the policy mismatch. The fix preserves the resize policy. The linked investigation and startup checks above track live coverage; the precise cold-secondary condition was not captured. |
