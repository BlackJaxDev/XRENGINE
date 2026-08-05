# Directional Light Vulkan Stability Investigation

Last Updated: 2026-08-04
Status: Active; canonical tracker for the directional-light Vulkan stability path

Supersedes active ownership in:

- [Shadow Atlas Framerate Regression Investigation](archive/shadow-atlas-framerate-regression-2026-07-02.md)
- [Directional Cascade Atlas Stale Frame And Reprojection TODO](../../todo/rendering/shadows/directional-cascade-atlas-stale-frame-and-reprojection-todo.md)
- [Editor Origin / Eye Camera Flicker Investigation](archive/editor-origin-eye-camera-flicker-2026-06-28.md)
- [Vulkan Mesh Jitter And Command-Buffer Retirement Failure](archive/vulkan-mesh-jitter-command-buffer-retirement-2026-07-21.md)
- The Vulkan crash, cropped-output, and debug-flicker follow-up in
  [Continuous Window Resize Frame Lifecycle](archive/continuous-window-resize-frame-lifecycle-2026-07-23.md)

Related work that remains separate and is not part of this acceptance pass:

- [Vulkan Optimization Workstreams 03-05 Validation](../../testing/rendering/03-05-optimization-validation-todo.md)
- [Vulkan Zero-Readback Production Scheduling](../../progress/rendering/vulkan-zero-readback-production-scheduling-2026-08-03.md)

## Current status

- Fix 3 (cold magenta Vulkan presentation): **orientation regression corrected; overall acceptance remains open**. Recording the mutable desktop-present primary fresh removes the magenta clear while keeping secondaries reusable. The subsequent upside-down HWND regression came from exempting FXAA/SMAA outputs from the backend-wide framebuffer-texture Y contract; that source-name exception is removed. Warm actual-window captures are upright through camera motion and resize, but a fresh isolated run still exposed separate cold/resize render-target content corruption, so Fix 3 is not closed as a complete presentation-stability item.
- ImGui directional-light inspector assertion: fixed and live-validated.
- Non-cascaded directional shadows: working baseline.
- Vulkan cascaded directional shadows: engine-side packet identity, matrix/atlas publication, split selection, and viewport containment are implemented and accepted in the isolated Vulkan path. Final user-facing shadow-quality validation remains.
- Vulkan quadrant/inversion and disappearing-mesh artifacts: root causes have been narrowed and several state-lifetime leaks fixed, but final user-facing stability is not yet proven.
- Procedural skybox: numeric-only publisher fixed; frequency mismatch and final user validation remain open.
- Remaining item 2 (scheduled-chain descriptor preflight): **implemented and accepted**. Valid scheduled mesh chains no longer repeat the logical per-draw descriptor walk, while inline and fallback paths retain it. The three primary-encoding sub-stages are now exposed through MCP.
- Remaining item 3 (CSM packet identity): **implemented and accepted**. Shadow packets are capped at 24 draws and use eight stable renderer/material buckets. Warm repeated sweeps caused no further descriptor-variant growth; moving shadow work remained bounded to 17 chains in ordinary motion and 22 on a membership transition.
- Remaining item 4 (cascade positioning/atlas sampling): **accepted without an additional coordinate patch**. RenderDoc, matrix provenance, cascade debug colors, and viewport/scissor inspection all agree on the current single-flip Vulkan contract. The remaining 146-chain spike is a separate main-view resource-plan invalidation wave, tracked as item 5 below. Its `BufferAllocationGeneration` label currently contains a coarse resource-plan revision and is not proof that a Vulkan buffer was reallocated.

### Latest user validation (2026-08-04)

- First startup is now mostly stable: no immediate persistent magenta or black frame, but the editor renders at approximately 30 Hz.
- Camera motion can produce a single-frame magenta flash, either during motion or immediately after motion stops.
- Resizing the window still turns the scene black and makes magenta flicker much more frequent and sustained.
- Interpretation: cold source readiness improved, but the motion dirty/reuse handoff and resize-driven native resource generation handoff remain incorrect. The 30 Hz result is consistent with the deliberately fresh desktop primary plus the still-duplicated scheduled-chain descriptor preflight; performance work must follow correctness isolation rather than mask these lifecycle failures.

## Problem statement

Selecting the primary directional-light scene node in the ImGui hierarchy triggered a native Dear ImGui assertion. In the same Vulkan editor view, cascaded directional shadows were incorrect and destabilized the frame loop: debug geometry flickered, the 3D result could be constrained to the top-left atlas-tile-sized region, and moving the camera caused a major slowdown. Non-cascaded directional shadows remained correct. The procedural skybox also did not render.

## Initial evidence

- The assertion originates in `imgui_widgets.cpp` and requires `ImGuiInputTextFlags_EnterReturnsTrue` to be absent from an `InputScalar` call.
- `LightComponentEditorShared.DrawCommittedIntInput` passes `EnterReturnsTrue` to `ImGui.InputInt`. Current Dear ImGui implements `InputInt` through `InputScalar` and explicitly rejects that flag.
- The user-provided frame uses Vulkan and the default render pipeline. The selected-light visualization contains many cyan cascade-volume wireframes, while the lit scene shows discontinuous/incorrect shadowing.
- RenderDoc 1.44 and its Vulkan capture layer pass `rdc doctor` on the reproduction machine.

## Root causes found in the initial pass

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

## Superseded provisional validation

The evidence below closed the initial reproduction, but the user-visible
regression recurred. It remains useful historical evidence and is not the
current acceptance result; the active follow-up and remaining gates below are
authoritative.

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

## Active 2026-08-04 regression follow-up

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

### Frame-source descriptor liveness work (implemented; insufficient alone)

The ImGui editor's solid-magenta scene at Vulkan startup was provisionally attributed to final-presentation descriptor liveness. Published descriptor snapshots retained a stable logical `SourceTexture` wrapper while the physical `TsrOutputTexture` image, view, sampler, or readiness changed from the placeholder to the live render target. `ComputeRecordedDescriptorResourceSignature` used the snapshot's frozen physical-resource signature for every published binding, so a final-present command chain could remain reusable until camera motion or light work dirtied it for an unrelated reason. This explains the symptom and exposed real lifetime defects, but actual-window validation below proves that descriptor liveness alone did not complete the fix.

The initial correctness patch made Vulkan command-chain identity take the authoritative live descriptor-resource fingerprint when a binding snapshot contained a frame-source sampler. That detected placeholder-to-ready physical publication and routed the draw through the existing descriptor-set refresh path, but it also restored a full reflected-binding walk for every affected draw.

The follow-up publication fix removes that provisional cost. `ComputeDispatchSnapshot` now classifies mutable frame-source samplers when it publishes its immutable layout signatures, retains the immutable image/buffer signature components, and resolves the current exact sampler plus combined resource signatures once per render frame, pipeline, and stable view family. A non-allocating lock protects snapshots shared by parallel view planning. Command-chain identity, exact per-slot frame-source validation, sampler descriptor refresh, and reusable compute descriptors consume the same cached publication. Snapshots without mutable frame sources still return the frozen signature immediately without locking or walking descriptor dictionaries.

Validation on 2026-08-04:

- `XREngine.Runtime.Rendering.Vulkan.csproj` built with 0 warnings and 0 errors.
- A cold directional-light-on run logged a `SourceTexture` placeholder fingerprint followed by live `TsrOutputTexture` publication, then produced a valid **MCP pre-present capture** without camera movement: `Build/_AgentValidation/mcp-sessions/present-source-live-on-20260804/mcp-captures/light-on-cold/Screenshot_20260804_111235_614_7defdf291d31440fa8ab4e55f052fdbc.png`.
- A separate cold run with directional, spot, and point lights disabled also produced a valid **MCP pre-present capture** without camera movement: `Build/_AgentValidation/mcp-sessions/present-source-live-on-20260804/mcp-captures/light-off-cold-fixed/Screenshot_20260804_111650_002_456d3d882582416d92bc1a4a6e860dfc.png`.
- The zero-light frame trace contained only the expected clear in `VPRC_LightCombinePass`, followed by the final `RenderToWindow_TsrOutputTexture` draw. Vulkan reported zero validation messages, zero descriptor binding failures, and zero skipped draws.
- The isolated editor session was stopped cleanly. Automated regression tests remain deferred until the live feature is accepted and the user clears test work.

Cached-publication validation on 2026-08-04:

- A fresh `XREngine.Runtime.Rendering.Vulkan.csproj` build completed with 0 warnings and 0 errors.
- The all-lights-off cold run produced a valid **MCP pre-present capture** without camera movement: `Build/_AgentValidation/mcp-sessions/present-source-cache-20260804/mcp-captures/light-off-cold/Screenshot_20260804_114711_997_effcfb1f89fe4235951c525e9529cbf4.png`.
- The directional-light-on cold run also produced a valid **MCP pre-present capture** without camera movement: `Build/_AgentValidation/mcp-sessions/present-source-cache-20260804/mcp-captures/light-on-cold/Screenshot_20260804_115009_305_c40150d1e9c8476d9bb965168e869c0c.png`.
- The light-on log recorded multiple physical `TsrOutputTexture` handle publications while final `RenderToWindow_TsrOutputTexture` draws continued. It no longer emitted the provisional full-fingerprint `SourceTexture` placeholder diagnostic from `ComputeDescriptorResourceFingerprint`.
- The settled light-on profiler sample reported zero Vulkan validation messages, descriptor binding failures, skipped draws, descriptor records validated or written, descriptor owner lookup/generation/frame-source misses, and zero allocations in `command_chain_fast_signature`.
- RenderDoc tooling passed `rdc doctor`; an `.rdc` capture was unnecessary because MCP captures, the physical-image publication log, and descriptor-owner counters directly exercised the transition. The isolated session was stopped cleanly.

### Fix 3 closeout and handoff: actual-window presentation

Status: **upside-down regression fixed; full presentation acceptance remains open**. The Vulkan rendering project builds cleanly. The actual editor HWND now applies the same backend-wide Y convention as the internal framebuffer texture instead of special-casing the final AA source. Separate stale/quadrant/black content after cold start or resize is still reproducible upstream of the orientation transform.

#### Capture-method correction

The earlier validation conflated two different outputs:

- MCP viewport and render-pipeline capture reads an engine render target before final presentation.
- Win32 `PrintWindow` reads the actual composed editor HWND, including the scene presentation and ImGui.

The MCP `TsrOutputTexture` capture is a valid 1920x1080 scene while the corresponding `PrintWindow` image is solid magenta behind otherwise functioning ImGui. Therefore the scene is being rendered, and the remaining fault is after TSR output generation in final-present recording, descriptor binding, or execution. MCP captures remain useful supporting evidence but are not acceptance evidence for this bug.

#### Actual-window isolation matrix

| Configuration | Cold actual-window result | Meaning |
| --- | --- | --- |
| Default command chains and primary reuse, after resource-registry/epoch work | Magenta before and after camera movement | Current failure: `present-epoch-fix3-planner-20260804/window-captures/.../final-window-print.png` |
| `XRE_VULKAN_COMMAND_CHAINS=0` | Correct scene without camera movement | Inline control bypasses the failing scheduled-command path: `present-fix3-inline-control-20260804/window-captures/cold/final-window-print.png` |
| Command chains enabled, `XRE_VULKAN_PRIMARY_COMMAND_BUFFER_REUSE=0` | Correct scene without camera movement | Strongest isolation: the defect depends on primary-command-buffer reuse or state gated by it: `present-fix3-primary-fresh-control-20260804/window-captures/cold/final-window-print.png` |
| Default settings after adding descriptor variant to primary ordered-node identity and comparing the primary schedule signature | Magenta | The schedule-key additions are defensive but insufficient: `present-fix3-primary-key-20260804/window-captures/cold/final-window-print.png` |
| Default settings after routing mutable frame-source samplers around the earliest clean-reuse return | Magenta before and after camera movement | The early-reuse guard is also insufficient: `present-fix3-validated-reuse-20260804/window-captures/.../final-window-print.png` |
| Exact recorded-secondary artifact sequence plus exact native descriptor-set binding validation | Magenta | Both identities matched while the internal final texture remained valid, ruling out the two proposed stale-secondary/updated-wrong-set failure modes: `present-fix3-exactbinding-v2-20260804/window-captures/steady/final-window-print.png` |
| Command chains enabled; mutable desktop-present primary recorded fresh; all scheduled secondaries reusable | Correct scene | Accepted correctness boundary: `present-fix3-nativecontent-v3-20260804/window-captures/thinprimary-v2/cold-final-window-print.png` |

The paired pre-present evidence is `present-epoch-fix3-planner-20260804/mcp-captures/tsr-live/RenderPipeline_TsrOutputTexture_20260804_152558.png`. All paths above are under `Build/_AgentValidation/mcp-sessions/` and are disposable investigation evidence.

#### Implemented fix-3 work currently in the worktree

- Added renderer-neutral texture sampling readiness and descriptor-resource epochs. Placeholder-to-ready and physical image/view/sampler replacement now advance the epoch.
- Made final-present required samplers defer while unavailable instead of publishing a placeholder as if it were ready.
- Made mutable frame-source descriptor snapshots resolve the current exact resource signature and refresh completed descriptor slots when physical resources change while logical handles remain stable.
- Rebuilt the Vulkan merged resource-registry snapshot on registry/FBO changes, with explicitly declared persistent resources winning over framebuffer-derived fallback descriptors. This prevents an `External`/absolute-pixel descriptor from replacing the declared persistent `TsrOutputTexture` contract.
- Included `DescriptorBindingVariant` in primary ordered-node identity and compared the cached primary variant's command-chain schedule signature before fast reuse.
- Routed mutable frame-source samplers through the common primary-reuse validation path instead of the earliest clean-reuse return.
- Cached primaries now retain the exact ordered `(CommandChainKey, native command-buffer artifact, artifact/recording generation, resource identity)` sequence encoded by `vkCmdExecuteCommands`. Reuse rejects a replaced, reordered, or non-executable secondary artifact.
- Reusable draw refresh validates that the active native descriptor sets are the exact sets bound by the scheduled secondary. This proved that the failing final-present draw was neither executing a stale secondary artifact nor refreshing a different descriptor-set handle.
- Mutable frame-source refresh no longer treats a logical sampler signature as proof of native descriptor contents. It resolves the current image view and sampler and uses the cached native write signature as the no-write fast path.
- The desktop compositor now treats its acquired-swapchain render scope and present-cycle state as primary-owned mutable state. It records that thin primary fresh while retaining all reusable scene, shadow, post-process, compute, and dynamic-text secondaries. Unrestricted primary reuse can be reconsidered only after the acquired-image entry/exit state is represented by a complete native reuse identity.

The last targeted command was `dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj --no-restore`; it completed with 0 warnings and 0 errors. No tests were added or run because the larger cascade-quality/performance investigation remains active and the user has not yet cleared regression-test work. All named editor sessions used by this acceptance pass are stopped.

#### Fix 3 rejected acceptance evidence

The accepted session was `present-fix3-nativecontent-v3-20260804`. It used a three-image `PresentModeMailboxKhr` swapchain with command chains enabled. The final profiler sample occurred after more than 4,900 presented frames and reported zero Vulkan validation messages, zero dropped frame operations, and zero dropped draws.

- Cold actual HWND: `window-captures/thinprimary-v2/cold-final-window-print.png`.
- After an immediate camera cut: `window-captures/thinprimary-v2/camera-moved-final-window-print.png`.
- Directional light disabled: `window-captures/thinprimary-v2/light-off-final-window-print.png`.
- Directional light restored: `window-captures/thinprimary-v2/light-on-restored-final-window-print.png`.
- Resized from 1550x902 to 1280x760: `window-captures/thinprimary-v2/resized-1280x760-final-window-print.png`.
- Restored to 1550x902: `window-captures/thinprimary-v2/resize-restored-final-window-print.png`.
- Four additional paced actual-window samples remained correctly presented: `window-captures/thinprimary-v2/slot-sample-1.png` through `slot-sample-4.png`.

All paths above are relative to `Build/_AgentValidation/mcp-sessions/present-fix3-nativecontent-v3-20260804/`. Reinspection after the user's report shows that these actual-window images are vertically inverted: the Sponza ceiling/floor relationship and editor debug geometry are upside down. They do prove that the magenta diagnostic clear was replaced by native scene content, but they do **not** satisfy Fix 3 acceptance. The user report supersedes the earlier classification.

The decisive steady profiler sample reported `chains_scheduled=124`, `chains_reused=124`, `chains_recorded=0`, `primary_command_buffers_recorded=1`, and `primary_command_buffers_reused=0`. GPU command time was 4.57 ms. The diagnostics-heavy run spent 30.52 ms recording the scene primary because the remaining scheduled-chain per-draw preflight still walks the complete pass. That CPU cost is not accepted as final performance; it is the first remaining fix below and can now be addressed without risking an invalid present.

The isolated session was stopped through the named session manager after capture. Automated regression tests were not added or run because repository policy still requires explicit user clearance after live feature acceptance.

#### Upside-down regression correction (2026-08-04)

The paired evidence isolated this regression to final presentation. `FxaaOutputTexture` contained an upright scene, while the actual HWND sampled the same content upside down. Vulkan's engine-default Y-up policy uses a negative-height viewport, so the fullscreen presentation shader must invert the framebuffer-texture V coordinate exactly once. `ShouldFlipVulkanPresentSourceY` incorrectly exempted `FxaaFBO` and `SmaaFBO`, even though those passes preserve the same backend-wide framebuffer-texture row convention as every other engine FBO.

Implemented correction:

- Added `RenderClipSpacePolicy.RequiresVulkanFramebufferTexturePresentationYFlip()` as the single policy query.
- Removed source-name orientation exceptions from the default direct-present and vendor-fallback setup.
- Routed the debug-opaque present path through the same policy.
- Removed the contradictory vendor-fallback `YDown` override so the published policy value is authoritative.

Live evidence under `Build/_AgentValidation/mcp-sessions/present-fix3-nativecontent-v3-20260804/orientation-fixed/`:

- `RenderPipeline_FxaaOutputTexture_20260804_175650.png` and `actual-window.png` have the same upright wall/floor and cyan debug-volume orientation.
- `camera-moved-window.png` remains upright after a 20-degree camera yaw.
- `resized-window.png` remains upright at 1280x760.
- The targeted `XREngine.Runtime.Rendering.csproj` and `XREngine.Runtime.Rendering.Vulkan.csproj` builds completed with 0 warnings and 0 errors.

RenderDoc 1.44 passed `rdc doctor`. A named-session launch was successfully hooked and exposed its target-control port, but `capture-trigger` returned no capture artifact; the paired internal-texture/HWND evidence already isolates the Y transform without relying on the failed capture.

Do not overstate this result: restoring the reused session from 1280x760 to its prior size later turned the internal FXAA target black, and a completely fresh isolated build (`present-orientation-final-v1-20260804`) produced zeroed `FxaaOutputTexture` capture data while the HWND showed red/blue quadrant content. That fresh run still reported 123/123 scheduled chains reused, one fresh primary, zero Vulkan validation errors, zero dropped operations, and zero dropped draws. Those failures occur before or independently of the corrected final Y mapping and remain part of the render-target/descriptor/atlas lifetime investigation.

#### Final-presentation ledger implementation and first freeze (2026-08-04)

Priority-1 diagnostics are now implemented rather than remaining a proposed next step. Vulkan owns a fixed 128-entry final-presentation ring that is inactive unless `XRE_VULKAN_FINAL_PRESENT_LEDGER=1` is set or MCP enables it. The enabled hot path retains structs in the bounded ring; JSON/list allocation occurs only when tooling requests a snapshot. It records:

- desktop frame/slot/image identity, live and swapchain extents, swapchain handle and monotonic generation;
- tracked final texture/FBO name, extent, readiness epoch, native image/view/sampler, descriptor generation, and tracked layout;
- the exact `SourceTexture` descriptor set, set/binding, frame-data slot, native view/sampler/layout, resource signature, whether the native write matched or was refreshed, and the command artifact when available;
- scene primary handle/recording generation, planner/context/resource/descriptor generations, fresh-primary decision, dirty generation, swapchain writer counts, prior valid swapchain contents, overlays, and queue-present result.

`get_vulkan_final_presentation_ledger` reads the newest entries. `configure_vulkan_final_presentation_ledger` enables, freezes/unfreezes, and clears capture. The ring automatically freezes on a concrete accepted-present invariant failure: a tracked source that is not descriptor-ready, a missing/wrong frame-data slot, a failed native descriptor write, a native view/sampler mismatch, or a present with neither a writer nor valid prior swapchain contents. Legitimate bootstrap clears and unchanged-image presents were explicitly excluded from auto-freeze after live validation exposed those cases.

The isolated Vulkan session `final-present-ledger-20260804` produced the first decisive freeze on frame 4:

- intended source: `FxaaOutputTexture`, descriptor epoch 4 / native generation 3, image view `2084700040160`;
- bound `SourceTexture`: descriptor set `2048904972592`, set 2 binding 0, frame-data slot 0, image view `2082993655664`, retained from the frame-3 observation;
- sampler matched (`2084681116096`), the source was ready, the full 1920x1080 source/swapchain extents matched, one swapchain write was recorded, and the scene primary was freshly recorded;
- automatic freeze reason: `bound final source descriptor payload differs from the current native source`.

This directly explains the one-frame magenta/black/quadrant family: the logical final source advances to a replacement physical FXAA view, but the final-present descriptor can still contain the previous view. Fresh primary recording does not repair stale descriptor contents and is therefore no longer the primary suspect for this remaining failure. The next fix must make the final-present descriptor publication atomic with the source resource generation, then invalidate/re-record the owning secondary artifact when its bound descriptor slot cannot be safely updated.

Visual evidence after the freeze was captured from two camera positions and inspected:

- `Build/_AgentValidation/20260803-directional-light-shadow/mcp-captures/Screenshot_20260804_192441_669_37075d40158d4264a5263b5cca503158.png`;
- `Build/_AgentValidation/20260803-directional-light-shadow/mcp-captures/Screenshot_20260804_192516_006_27a6b01a348d45e0b07170c926420c0a.png`.

Both are full-resolution 1920x1080 scene readbacks and change with the camera, ruling out a permanently stale capture source. The named session was stopped through the session manager. Session file logging produced no runtime log files, so the structured ledger and inspected MCP captures are the durable evidence for this run. `rdc doctor` passed. The Vulkan renderer and full editor builds completed with 0 warnings and 0 errors. No tests were added or run because live acceptance remains open and the user has not cleared regression-test work.

#### Remaining items 2-4 implementation and acceptance (2026-08-04)

Items 2-4 below are now complete. The implementation deliberately leaves ordinary scene packets at 64 draws and preserves every unscheduled/inline fallback. The final editor build completed with 0 warnings and 0 errors. No regression tests were added or run because the active rendering regression still requires user clearance before test work.

Scheduled-chain descriptor preflight:

- `TransitionFrameOpDescriptorSnapshotsForSampling` now receives the scheduled key map and cache. A mesh operation is skipped only when its key resolves to a valid scheduled packet whose source range contains that exact operation.
- The executable secondary still establishes its deduplicated descriptor-image entry requirements immediately before the primary opens the render scope and executes the buffers. If execution falls back inline, the existing mesh path re-establishes its prepared descriptor transitions before opening the inline render scope. Indirect, compute, unscheduled, and other inline operations remain unchanged.
- `get_render_profiler_stats` now exposes `context_pass_transitions`, `barrier_planning_emission`, and `op_dispatch`. A rebuilt isolated editor returned all three fields and zero Vulkan validation errors.
- In the warm live pass, 123 scheduled mesh chains were reused with zero secondary recordings. Primary encoding settled in the approximately 10-18 ms range under the enabled audit/ledger diagnostics, compared with the earlier 30.52 ms all-reused sample that still performed the duplicate scan. This is an improvement, not a claim that the broader 30 Hz frame is solved.

Bounded shadow packet identity:

- Shadow-view render packets are capped at 24 draws; non-shadow packets remain capped at 64.
- Shadow casters are sorted into eight stable renderer/material hash buckets before the existing material/renderer order. Shadow chain ordinals are bucket plus per-bucket occurrence, so membership changes cannot shift every later bucket.
- The first camera sweep warmed descriptor allocation variants from 377 to 426. Repeated sweeps then held both the live count and high-water mark at 426. Ordinary moving frames recorded 17 chains, one shadow-membership transition recorded 22, and settled frames recorded zero while reusing 144-146 chains.
- One transition frame recorded 146 chains. The profiler's first dirty record identifies `RenderViewKind.Main`, dependency field `BufferAllocationGeneration`, and resource-plan revision `1 -> 4`; it is not shadow packet churn. Importantly, `BuildCommandChainDependencySignature` currently assigns `packet.ResourcePlanSnapshot.Revision` to both `BufferAllocationGeneration` and `ResourcePlanGeneration`. The diagnostic therefore reports a coarse renderer-wide planning revision through a buffer-specific field name; it does **not** establish that a `VkBuffer`, allocation, or device address changed. This is now the separate open item 5 below rather than part of CSM acceptance.

Cascade placement and atlas sampling:

- `rdc doctor` passed. `Build/_AgentValidation/20260803-directional-light-shadow/renderdoc/csm-vulkan-dirty-frame150_frame150.rdc` is a valid Vulkan capture with 413 draws. The 4096x4096 D24 cascade atlas is RenderDoc resource 6346; its four populated 1024 allocations were exported and visually inspected at `renderdoc/fix4-atlas-inspection/cascade-atlas-frame150-4096.png`. The `rdc` session was closed after inspection.
- The fresh sequential-atlas run placed the four 1024 allocations at allocator rectangles `(0,0)`, `(1024,0)`, `(0,1024)`, and `(1024,1024)`, with two-texel gutters producing inner rectangles `(2,2)`, `(1026,2)`, `(2,1026)`, and `(1026,1026)`, each 1020x1020. All four were resident and sampleable.
- For all four cascades, `CascadeProvenance` reported identical current and rendered matrix hashes, identical request/allocation/sample content generations, `fallback=None`, `StaleSampled=0`, and `MixedGenerationPrevented=0`. Both deferred and forward binding selected page 0 and records 0-3 from the same published slots.
- The coordinate contract is internally consistent: shadow projection converts clip Y to framebuffer-texture Y once; atlas scale/bias converts the allocator's bottom-origin tile rectangle to the Vulkan texture's top-origin address once. RenderDoc event 73 used tile viewport `(1026,4094,1020,-1020)` and scissor `(1026,3074,1020,1020)`. Later deferred lighting event 9255 used the full 1767x994 viewport/scissor, proving tile state did not leak into post-processing or presentation.
- Cascade debug colors were captured from two camera positions at `mcp-captures/fix4-cascade-debug/enabled-position-a/` and `enabled-position-b/`. The visible geometry changed from the expected green band to the expected red near band as the camera crossed the split. `DebugCascadeColors` was read back as `false` before shutdown.
- Camera sequences and inspected screenshots stayed full-frame and upright, with no black/magenta/quadrant frame, no disappearing Sponza meshes, and no post-process tile inheritance. No additional cascade-matrix or atlas-Y patch was justified by this evidence.

## Fix ledger, in priority order

The upside-down regression is corrected. Fix 3 as a whole remains open until cold start and resize reliably publish the intended final render target to every swapchain image without zeroed, stale, or atlas-quadrant content. Items 2-4 are now complete and retained below as the accepted implementation contract. Item 5 tracks the independent main-view command-chain invalidation wave; item 6 tracks the skybox schema mismatch.

### 1. Eliminate cold/resize final-target publication corruption

The orientation boundary is now deterministic, but the source reaching it is not. The new final-presentation ledger proves one concrete transition failure: `FxaaOutputTexture` advanced from native view `2082993655664` to `2084700040160`, while the final `SourceTexture` descriptor remained on the old view for the accepted frame. A fresh primary and correct full-frame extents were present, so the remaining work is descriptor/resource-generation publication and secondary ownership, not another blanket primary-rerecord workaround. Earlier runs also captured an all-zero FXAA source, black resize restores, and red/blue quadrant HWND content from this failure family.

Required validation/fix:

- Use the implemented ledger on the first ready frame and after each swapchain/render-target generation change; keep RenderDoc for the first unresolved writer-side or execution-side mismatch.
- Make `SourceTexture` publication consume one immutable tuple of logical epoch, native generation, image/view/sampler, descriptor slot, and owning command artifact. Reject or defer the frame if that tuple changes before submit.
- Make render-target resize/generation changes invalidate every secondary whose framebuffer, viewport/scissor, or sampled image view depends on the previous generation.
- Ensure a swapchain image is never presented from a stale atlas/post-process descriptor while its intended final source is zeroed or not ready.
- Re-run cold start, resize down/up, camera motion, and paced multi-image captures with both paired internal targets and actual HWND evidence.

### 2. Remove the scheduled-chain per-draw descriptor preflight (complete)

The remaining all-reused encoding cost is now localized to the pass-transition path. `TransitionToPrimaryOperationPass` calls `TransitionFrameOpDescriptorSnapshotsForSampling`, which walks every mesh operation in the pass, enters pipeline/planner scopes, resolves a uniform slot, and transitions published descriptor images one draw at a time. Scheduled secondary chains then establish the same descriptor entry requirements again from their recorded artifacts. This duplicated scan explains a high primary-encoding time even when no secondary is recorded.

Acceptance contract (met):

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

### 3. Stabilize dirty CSM packet identity and bound re-record work (complete)

Command-chain packetization currently permits 64 mesh draws per packet for every view kind. A small caster-membership or cascade-fit change can therefore invalidate and reconstruct dozens of draws, and shifting the sorted caster list can move unrelated draws across packet boundaries.

Acceptance contract (met):

- Add a shadow-view packet limit (start by measuring 16 or 24 draws) while leaving ordinary scene packets at 64.
- Treat the smaller packet size as a bounded mitigation, not the final identity model.
- Replace position-dependent packet identity with stable renderer/material hash buckets, or another deterministic packet identity that does not reshuffle subsequent casters when one caster enters or leaves a cascade.
- Verify repeated camera sweeps do not grow descriptor reservations/variants after warm-up and only re-record the buckets whose membership changed.

Relevant files:

- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Planning/VulkanRenderer.CommandChains.Policy.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Planning/VulkanRenderer.CommandChains.Packetization.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Signatures/VulkanRenderer.CommandChains.Signatures.cs`

### 4. Finish cascade positioning and atlas sampling validation (complete)

The conservative collection and light-camera depth fixes address two known errors, but user validation still reports incorrect cascade placement. The next pass must inspect the actual resource rather than infer correctness from a final screenshot.

Acceptance contract (met):

- Capture all four atlas tiles in RenderDoc while the camera crosses a split and confirm each tile contains the expected slice geometry.
- Compare the exact rendered light view-projection matrix with the matrix published to deferred and forward lighting for the same atomic atlas generation.
- Verify atlas scale/offset uses Vulkan's top-left tile convention consistently and that projection/readback applies the Y inversion exactly once.
- Inspect cascade split selection and blend bands with cascade debug colors, then restore debug colors to off.
- Confirm tile viewport/scissor state ends with the shadow pass and cannot be inherited by bloom, FXAA, swapchain, or debug-shape passes.

### 5. Separate coarse resource-plan revision changes from buffer binding identity (open)

This is the remaining large command-chain recording spike observed during the fix 3 camera sweeps. It is independent of cascaded shadows.

Observed frame:

- Frame 4597 scheduled 206 chains, recorded 146, and reused 60. The comparable 146-recording frame took 184.16 ms overall and 56.86 ms in primary command encoding. Settled frames returned to zero recordings.
- The first dirty chain was `RenderViewKind.Main`, pass 1, with an unchanged structural signature. Its invalidation reason was `ResourcePlan`, dependency field `BufferAllocationGeneration`, with revision `1 -> 4` and diagnostic affected range `4+1`.
- Shadow frames in the same sweep remained bounded to 17 recordings normally and 22 on a caster-membership transition. Descriptor allocation variants remained at the warmed high-water mark of 426. The 146-chain wave is therefore not a cascade packet-identity failure.

What `BufferAllocationGeneration` means in this trace:

- `ResourcePlanSnapshot.Revision` is a coarse resource-planner revision carried by every render packet.
- `BuildCommandChainDependencySignature` currently stores that same revision in both `BufferAllocationGeneration` and `ResourcePlanGeneration`.
- `CommandRecordingDependencySignature.Compare` classifies a `BufferAllocationGeneration` mismatch as a binding-identity change, so an unrelated planner revision can invalidate otherwise reusable command chains.
- The field currently has no independent per-buffer allocation signature. A mismatch does not prove that a vertex, index, uniform, storage, indirect, or device-memory allocation changed. The unchanged structural signature also shows that command topology alone did not require the observed re-record.
- Physical image, framebuffer, pipeline, and descriptor identities are tracked separately. They must be compared directly before attributing this wave to resize, presentation, or a concrete resource replacement.

Required investigation/fix:

- Add telemetry at each resource-planner revision increment that records the reason and the exact logical/physical image and buffer groups added, removed, resized, aliased, or replaced. This must identify what caused revision `1 -> 4` rather than inferring from the command-chain label.
- Give command-chain dependencies a real buffer binding/allocation identity derived only from the buffers and offsets recorded by that packet. Do not populate a buffer-specific field from the global planner revision.
- Keep the coarse resource-plan revision as scheduling/diagnostic metadata. It may invalidate an artifact only when a concrete resource, render-scope, descriptor, or recorded binding used by that artifact changed.
- Verify the command-chain primary comparison also does not re-record the thin primary solely because the global planner revision changed while its concrete pass boundaries, barriers, and executable secondary set remained valid.
- Repeat the warmed back-and-forth camera sweep. Require stable descriptor-allocation high-water marks, zero full-main-view re-record waves, and recordings limited to chains whose concrete resource identities or visible membership changed.
- Preserve validation safety: any changed buffer handle, memory binding, offset, range, device address, descriptor payload, or render-target generation must still invalidate every artifact that recorded it.

Relevant files:

- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Planning/VulkanRenderer.CommandChains.Dependencies.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Reuse/Dependencies/CommandRecordingDependencySignature.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOps/ResourcePlanSnapshot.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Planning/VulkanRenderer.CommandChains.Packetization.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Planning/VulkanRenderer.CommandChains.Planning.cs`

### 6. Resolve the remaining procedural-skybox schema mismatch

Profiler diagnostics still report two auto-uniform frequency mismatches in the procedural skybox program:

- `SkyboxIntensity`: shader-reflected `Material`, runtime-published `View`.
- `SkyboxRotation`: shader-reflected `Material`, runtime-published `View`.

Align the typed publisher with the material-owned shader blocks (or deliberately change both shader declarations and ownership together). Until the reflected and runtime frequencies agree, the fast path can fall back or retain stale sky constants even though the earlier numeric-only publisher fix allows the draw to exist.

### 7. Final acceptance pass

- Run at least three back-and-forth interactive camera sweeps after warm-up.
- Require no quadrant placement, inversion, disappearing or independently displaced Sponza meshes, stale camera frames, cascade popping beyond the configured blend, or skybox loss.
- Complete one interactive resize after warm-up and require no native crash,
  cropped/upper-left output, or floor/debug-geometry flicker.
- Require zero Vulkan validation errors, bounded descriptor reservation/variant counts, and no unclassified editor exit.
- Capture the final frame plus directional atlas tiles and inspect both visually.
- Only after this live path passes and the user clears test work, add targeted regression coverage.
