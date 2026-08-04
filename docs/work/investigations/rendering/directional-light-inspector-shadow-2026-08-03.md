# Directional Light Inspector Assertion and Shadow Investigation

## Problem statement

Selecting the primary directional-light scene node in the ImGui hierarchy triggered a native Dear ImGui assertion. In the same Vulkan editor view, cascaded directional shadows were incorrect and destabilized the frame loop: debug geometry flickered, the 3D result could be constrained to the top-left atlas-tile-sized region, and moving the camera caused a major slowdown. Non-cascaded directional shadows remained correct. The procedural skybox also did not render.

## Initial evidence

- The assertion originates in `imgui_widgets.cpp` and requires `ImGuiInputTextFlags_EnterReturnsTrue` to be absent from an `InputScalar` call.
- `LightComponentEditorShared.DrawCommittedIntInput` passes `EnterReturnsTrue` to `ImGui.InputInt`. Current Dear ImGui implements `InputInt` through `InputScalar` and explicitly rejects that flag.
- The user-provided frame uses Vulkan and the default render pipeline. The selected-light visualization contains many cyan cascade-volume wireframes, while the lit scene shows discontinuous/incorrect shadowing.
- RenderDoc 1.44 and its Vulkan capture layer pass `rdc doctor` on the reproduction machine.

## Root causes

### Inspector assertion

`LightComponentEditorShared.DrawCommittedIntInput` supplied `ImGuiInputTextFlags.EnterReturnsTrue` to `ImGui.InputInt`. The current Dear ImGui scalar-input implementation asserts when that flag is present. The editor only needs to commit when the control deactivates after an edit, so the unsupported flag and redundant return-value check were removed.

### Cascaded directional shadows and frame stability

Six defects combined:

1. The initial atlas rendered before the asynchronous Sponza import registered its shadow casters. Atlas content hashes did not include scene shadow-caster membership, so the clear result could remain cached after model registration.
2. Cascade collection used a fitted OBB whose generation could differ from the cascade matrices being published. At that collect/publish boundary it rejected valid casters. Disabling culling populated the map, but multiplied full-scene collection and recording by four and was not an acceptable fix. Cascade collection now uses a conservative world-space AABB derived from the fitted cascade corners.
3. Vulkan had been forced onto sequential per-cascade rendering. With 125 casters this recorded 500 shadow draws for every dirty update. Vulkan now uses the same capability-gated `AtlasPage` / `InstancedLayered` grouped path as other backends, except for the known Monado OpenXR incompatibility. Each caster draw is recorded once with four instances, and the four-cascade generation remains atomic.
4. Vulkan's global viewport/scissor state could retain the last 1024x1024 atlas tile region after the shadow pass. Later pass recording then consumed that stale state, producing the top-left rendering symptom and destabilizing secondary-command reuse. Each render-graph pass now derives viewport and scissor from its logical pipeline render/crop region; the scissor defaults to the full active target when no crop is specified.
5. The Vulkan auto-uniform schema assigned every struct snapshot to `Material` frequency, ignoring the frequency declared by the shader-rewritten block. Deferred `LightData` is object-owned, so reused frame slots could receive old light matrices and atlas metadata. This was the direct cause of alternating full-frame outputs, disappearing Sponza meshes, quadrant-sized post-process results, and frames that appeared vertically inverted. Struct snapshots now inherit their owning block's frequency.
6. A clean, content-matching resident cascade tile accumulated "stale age" merely because it had not been redrawn. After the shader's bounded stale-data window elapsed, valid atlas pages were rejected even though their request content and rendered matrices still matched. Stale age is now non-zero only while the atlas explicitly reports `StaleTile`; clean reusable pages remain age zero.

Published cascade metadata also now preserves `StaleTile` instead of reporting `None` while a previous atomic generation is intentionally sampled. This prevents false diagnostics and mixed-generation assumptions during camera motion.

### Procedural skybox

The Vulkan mesh-binding snapshot fast path returned before invoking typed binding publishers when a material had no legacy uniform callback and no descriptor resource. The procedural skybox is numeric-only, so its publisher was skipped and all 12 shader constants remained zero. `VkMeshRenderer` now treats typed binding publishers as sufficient reason to capture and retain a binding snapshot even when no texture descriptors exist.

## Iteration evidence

### Baseline Vulkan capture

- Capture: `Build/_AgentValidation/20260803-directional-light-shadow/renderdoc/baseline-vulkan-root_frame80.rdc`.
- The main Sponza pass contained 101 mesh draws.
- The directional atlas bound by the deferred light was a 4096x4096 D24 resource.
- Exported atlas tiles were uniform clear depth: exactly two visualization colors (unused black and clear-depth red), with no caster geometry.

### Cache invalidation

- `VisualScene3D.ShadowCasterMembershipRevision` now advances when a shadow-casting renderable is added or removed.
- The revision participates in `Lights3DCollection.BuildShadowContentHash`, so asynchronous model registration makes prior atlas contents dirty.
- Audit logs confirmed the atlas re-rendered after Sponza registration instead of retaining the startup result.

### Vulkan render-path isolation and final grouped capture

- Sequential rendering with the same fitted per-cascade collection volumes remained clear, proving that caster collection, rather than grouped rasterization alone, was the root of the empty atlas.
- RenderDoc capture of the failed re-render contained the 101 main-scene draws but no shadow-caster draws; commands were rejected before shadow rasterization.
- Disabling culling populated every cascade but produced 4 x 125 = 500 shadow draws and a 55.9 ms CPU frame. This ruled the workaround out on performance grounds.
- The conservative cascade AABB retains culling without the collect/publish generation mismatch.
- Camera-dirty grouped capture: `Build/_AgentValidation/20260803-directional-light-shadow/renderdoc/cascade-grouped-camera-dirty.rdc`.
- Atlas export: `Build/_AgentValidation/20260803-directional-light-shadow/renderdoc/atlas-dirty-grouped.png`. All four 1024x1024 tiles contain caster depth.
- RenderDoc shows approximately 100 shadow-caster draws with `Instances=4`, one clear per tile, and segmented secondary-command execution inside a single atlas-page pass. The deferred/forward lighting passes subsequently sample the same 4096x4096 D24 atlas resource.
- No RenderDoc validation messages were reported.

### Skybox capture

- Before the binding fix, the sky draw passed depth on background pixels but its fragment output was zero. The procedural constant buffer contained twelve zero values.
- Fixed capture: `Build/_AgentValidation/20260803-directional-light-shadow/renderdoc/cascade-grouped-skybox-fixed2.rdc`.
- After the fix, the sky constant buffer contains the component defaults, including intensity `1.0`, coverage `0.45`, scale `1.4`, and non-zero sun/moon parameters. A sampled background pixel outputs approximately `(0.1961, 0.4765, 1.0017)`.
- Final export: `Build/_AgentValidation/20260803-directional-light-shadow/renderdoc/fixed2-final.png`. It shows the procedural blue sky behind the scene.

### Vulkan frame-slot and clean-atlas follow-up

- Before the auto-uniform frequency correction, a 12-frame static production capture alternated between two exact final-output hashes, with approximately 15.06% of pixels changing. `AlbedoOpacity` remained stable while the difference first appeared in `FxaaOutputTexture`, ruling out Sponza visibility and occlusion culling.
- Enabling the existing packed-versus-authoritative parity diagnostic produced repeated `DeferredLightingDir.fs` / `LightData` mismatches. The packed bytes remained stale while the authoritative object-frequency bytes advanced, identifying the incorrectly hard-coded material frequency.
- After inheriting `block.Frequency`, the same 12-frame production capture produced one hash for every frame and a maximum changed-pixel ratio of zero: `Build/_AgentValidation/20260803-directional-light-shadow/mcp-captures/frequency-root-fix/static/ViewportSequence_20260804_101352_326_e50ad8904372442480f8bf6997c23bfd/`.
- The clean-atlas audit showed a resident generation with matching request/allocation content and matching current/rendered matrix hashes being retained for thousands of frames with `fallback=None`. That generation was valid and reusable, so elapsed render age was not stale age.
- After restricting stale age to `StaleTile`, cascade debug selection remained active throughout an 11-frame static capture beyond the former eight-frame rejection threshold. Every captured frame was identical: `Build/_AgentValidation/20260803-directional-light-shadow/mcp-captures/clean-stale-fix/static-cascade-colors/ViewportSequence_20260804_102510_364_ce4edc2b32a7432b8bfccb3856201aab/`.
- A synchronized camera-motion capture remained full-frame and upright with no disappearing Sponza draws or quadrant-sized passes: `Build/_AgentValidation/20260803-directional-light-shadow/mcp-captures/clean-stale-fix/moving-synchronized/ViewportSequence_20260804_102947_639_1c830be435274ccda661fa797ea7fc13/`.
- During an audited camera-fit change, the previous atomic generation was preserved for one frame, then all four cascades refreshed together in 4.02 ms. On the following frame every request/allocation content hash and current/rendered matrix hash matched, `fallback=None`, and no mixed generation was sampled.

## Validation

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore`: succeeded twice with 0 warnings and 0 errors, including after the numeric-only binding-publisher fix.
- Isolated editor session `dirlight-shadow-20260803`: rebuilt and launched successfully on Vulkan.
- Selected `TestDirectionalLightNode` five consecutive times through MCP; every selection succeeded and the editor remained responsive to `ping`, with no native assertion.
- Final isolated session `dirlight-final-20260803` used Vulkan with directional-shadow and recording diagnostics enabled. It was closed through the named session manager after inspection.
- The live component snapshot reports four active cascades, effective mode `InstancedLayered`, backend `AtlasPage`, and fallback reason `None`. All four atlas allocations are resident and sampleable.
- Twelve paced camera moves produced grouped four-cascade updates with 3.29-4.56 ms CPU atlas scheduling/recording time. The diagnostic log contains no sequential fallback, failed render, queue overflow, or post-startup rejected frame.
- Final steady-state profiler snapshot: 12.88 ms whole frame, 13.11 ms p50, 22.45 ms p95, 5.08 ms scene-render CPU, 4.55 ms Vulkan scene recording, 9.49 ms GPU command time, and 0.074 ms fence wait.
- Vulkan validation reported zero messages and zero errors, including shutdown.
- Full-resolution live capture: `Build/_AgentValidation/20260803-directional-light-shadow/mcp-captures/Screenshot_20260803_222040_694_1cdeaf947e854e679bb635c2629c5b5d.png`. It remains 1920x1080 after paced camera motion, contains the scene/debug geometry across the intended viewport, and includes the procedural sky.
- Follow-up isolated session `dirlight-stabilize-20260804` rebuilt and launched the corrected Vulkan path. A live motion profiler sample reported 11.33 ms whole-frame time, 12.69 ms p50, 22.10 ms p95, 76.51 Hz achieved desktop rate, 3.41 ms scene-render CPU, zero Vulkan validation errors, and zero dropped draw or frame-operation records.
- Final full-resolution skybox capture: `Build/_AgentValidation/20260803-directional-light-shadow/mcp-captures/final-skybox/Screenshot_20260804_033214_579_89ddc7c5d36d4dfba82b3a5e0e2d469c.png`. The procedural sky fills the complete 1920x1080 background around Sponza.
- RenderDoc and all named editor sessions were closed after inspection. Temporary editor preferences were restored.
- Automated tests were not added or run because repository policy requires live feature validation and explicit user clearance before test work for an active regression.

## 2026-08-04 regression follow-up

This section supersedes the earlier closeout claims above. User validation found that the Vulkan cascaded path was still not complete: shadows flickered and were positioned incorrectly, Sponza meshes disappeared intermittently with occlusion culling disabled, post-process outputs sometimes occupied a quadrant or appeared inverted, camera motion caused severe CPU stalls, and the skybox was not consistently visible. Non-cascaded directional shadows remained correct, which continues to isolate the problem to cascade atlas generation/publication and Vulkan recording rather than generic directional-shadow sampling.

### Additional fixes completed

- Vulkan now keeps directional-cascade atlas allocation atomic but records the tiles sequentially. The experimental grouped/indexed-viewport path is disabled on Vulkan because it replayed a union caster set into every tile and leaked indexed viewport/scissor state when a grouped recording was rejected and retried.
- Cascade collection uses a conservative world-space AABB, and the light camera near/far placement was corrected so the orthographic volume contains the fitted cascade slice instead of being offset from it.
- Cascade operations are sorted by `FrameOpContext.SchedulingIdentity`, keeping one cascade cohort contiguous for command-chain planning.
- Vulkan framebuffer clears now publish explicit clear values rather than depending on stale/default attachment state.
- Logical viewport and scissor regions are copied into immutable recording snapshots. Physical render-graph resources use stable physical-owner identities so logical atlas tiles cannot re-key a shared image while a frame is being recorded.
- Command-chain keys include the descriptor-binding variant. Main and shadow program link generations are tracked independently, and descriptor signatures use stable arena reservation generations instead of mutable per-frame uniform maps.
- Reusable frame data gained a direct owner-only refresh path. During camera motion this reduced refresh work from hundreds of milliseconds and a full caster walk to approximately `1.0-2.4 ms` with `frame_data_draws_visited=0`.
- Fully reusable chain runs no longer construct placeholder prepared draws or initialize an empty worker batch. Mixed runs reserve source-index-addressable storage but prepare only draws belonging to chains that actually require recording.
- Descriptor-image entry requirements from a secondary run are now collected under one layout lock and deduplicated before barrier emission. Layout, resource-generation, queue-family, descriptor-layout, and ownership conflicts remain hard failures.

### Latest performance evidence

The latest isolated Vulkan sweep used `XRE_VULKAN_COMMAND_CHAINS=1`, four cascades, and occlusion culling disabled.

- Stable frames record the scene in approximately `13.4-17.0 ms`, with zero Vulkan validation errors.
- Direct frame-data refresh stays near `1-2.4 ms`, visits zero mesh draws, and the descriptor arena settles at 877 reservations / 386 allocation variants after warming.
- Camera motion still produces dirty bursts. The forward sweep contained 6 slow frames with a `668.7 ms` maximum; the reverse sweep contained 8 slow frames with a `705.6 ms` maximum.
- A representative all-reused frame still spent `117.8 ms` in primary command encoding even though `chains_recorded=0`. Dirty frames spent as much as `371.2 ms` in encoding and `191.5 ms` constructing prepared draws.
- The final completed sweeps reported zero Vulkan validation errors. The named isolated process had exited by the later cleanup RPC; stdout/stderr contained no managed exception or assertion, so that exit remains unclassified and must not be counted as a clean stability pass.
- The current Vulkan rendering project builds with 0 warnings and 0 errors. Automated tests remain deferred under repository policy until the live feature is functionally correct and the user explicitly clears regression-test work.

## Remaining fixes, in priority order

### 1. Remove the scheduled-chain per-draw descriptor preflight

The remaining all-reused encoding cost is now localized to the pass-transition path. `TransitionToPrimaryOperationPass` calls `TransitionFrameOpDescriptorSnapshotsForSampling`, which walks every mesh operation in the pass, enters pipeline/planner scopes, resolves a uniform slot, and transitions published descriptor images one draw at a time. Scheduled secondary chains then establish the same descriptor entry requirements again from their recorded artifacts. This duplicated scan explains a high primary-encoding time even when no secondary is recorded.

Required change:

- Pass scheduled-chain membership into `TransitionFrameOpDescriptorSnapshotsForSampling`.
- Skip the per-draw descriptor transition for `MeshDrawOp` entries owned by a valid scheduled chain.
- Establish the deduplicated requirements once from the executable secondary buffers immediately before the render scope begins.
- Retain the existing per-draw path for inline, unscheduled, indirect, compute, and fallback operations.
- Expose the already-recorded `ContextPassTransitions`, `BarrierPlanningEmission`, and `OpDispatch` CPU stages through `get_render_profiler_stats` so this optimization can be verified without inference.

Relevant files:

- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Operations.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Synchronization/VulkanRenderer.BarrierEmission.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Synchronization/VulkanRenderer.Synchronization.cs`
- `XREngine.Editor/Mcp/Actions/EditorMcpActions.Profiler.cs`

### 2. Stabilize dirty CSM packet identity and bound re-record work

Command-chain packetization currently permits 64 mesh draws per packet for every view kind. A small caster-membership or cascade-fit change can therefore invalidate and reconstruct dozens of draws, and shifting the sorted caster list can move unrelated draws across packet boundaries.

Required change:

- Add a shadow-view packet limit (start by measuring 16 or 24 draws) while leaving ordinary scene packets at 64.
- Treat the smaller packet size as a bounded mitigation, not the final identity model.
- Replace position-dependent packet identity with stable renderer/material hash buckets, or another deterministic packet identity that does not reshuffle subsequent casters when one caster enters or leaves a cascade.
- Verify repeated camera sweeps do not grow descriptor reservations/variants after warm-up and only re-record the buckets whose membership changed.

Relevant files:

- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Planning/VulkanRenderer.CommandChains.Policy.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Planning/VulkanRenderer.CommandChains.Packetization.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Signatures/VulkanRenderer.CommandChains.Signatures.cs`

### 3. Finish cascade positioning and atlas sampling validation

The conservative collection and light-camera depth fixes address two known errors, but user validation still reports incorrect cascade placement. The next pass must inspect the actual resource rather than infer correctness from a final screenshot.

Required validation/fix:

- Capture all four atlas tiles in RenderDoc while the camera crosses a split and confirm each tile contains the expected slice geometry.
- Compare the exact rendered light view-projection matrix with the matrix published to deferred and forward lighting for the same atomic atlas generation.
- Verify atlas scale/offset uses Vulkan's top-left tile convention consistently and that projection/readback applies the Y inversion exactly once.
- Inspect cascade split selection and blend bands with cascade debug colors, then restore debug colors to off.
- Confirm tile viewport/scissor state ends with the shadow pass and cannot be inherited by bloom, FXAA, swapchain, or debug-shape passes.

### 4. Resolve the remaining procedural-skybox schema mismatch

Profiler diagnostics still report two auto-uniform frequency mismatches in the procedural skybox program:

- `SkyboxIntensity`: shader-reflected `Material`, runtime-published `View`.
- `SkyboxRotation`: shader-reflected `Material`, runtime-published `View`.

Align the typed publisher with the material-owned shader blocks (or deliberately change both shader declarations and ownership together). Until the reflected and runtime frequencies agree, the fast path can fall back or retain stale sky constants even though the earlier numeric-only publisher fix allows the draw to exist.

### 5. Final acceptance pass

- Run at least three back-and-forth interactive camera sweeps after warm-up.
- Require no quadrant placement, inversion, disappearing Sponza meshes, stale camera frames, cascade popping beyond the configured blend, or skybox loss.
- Require zero Vulkan validation errors, bounded descriptor reservation/variant counts, and no unclassified editor exit.
- Capture the final frame plus directional atlas tiles and inspect both visually.
- Only after this live path passes and the user clears test work, add targeted regression coverage.

## Current status

- ImGui directional-light inspector assertion: fixed and live-validated.
- Non-cascaded directional shadows: working baseline.
- Vulkan cascaded directional shadows: partially corrected but not complete; placement, flicker, and camera-motion recording spikes remain open.
- Vulkan quadrant/inversion and disappearing-mesh artifacts: root causes have been narrowed and several state-lifetime leaks fixed, but final user-facing stability is not yet proven.
- Procedural skybox: numeric-only publisher fixed; frequency mismatch and final user validation remain open.
