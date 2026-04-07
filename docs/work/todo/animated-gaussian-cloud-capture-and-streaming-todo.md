# Animated Gaussian Cloud Capture And Streaming TODO

Last Updated: 2026-04-07
Status: Active backlog
Scope: build an offline-first pipeline that captures animated model frames as multi-view datasets, reconstructs or bakes them into streamable Gaussian clips, and renders each visible clip with one draw submission per frame in the main pass.

## Goal

Build a production-ready path that:

- captures an animated model or authored clip from engine-controlled cameras,
- reconstructs or bakes per-frame Gaussian clouds using external research-driven tooling as the initial implementation path,
- packages the result into streamable engine assets instead of loose per-frame debug files,
- plays the clip back in-engine with fixed-cost draw submission and zero CPU readbacks in the visible hot path, and
- leaves room for later upgrades such as persistent Gaussians, better anti-aliasing, and higher-fidelity splat shading.

The research report in `deep-research-report(4).md` is the starting point, but this tracker is intentionally engine-specific and should stay valid even if the reconstruction backend changes from Free-Range Gaussians to another sparse-view Gaussian pipeline.

## Requirement Clarification: "One Draw Call Each Frame"

For this backlog, the requirement means:

- one graphics API draw submission per visible animated Gaussian actor per render pass in the shipping main-color path,
- no CPU-side loop over individual splats at playback time,
- no per-frame `XRMesh` rebuilds or per-splat material changes in the visible path, and
- no GPU readback just to decide how many splats to draw.

This does **not** mean one draw call for the whole scene. Multiple animated Gaussian actors can each consume one draw submission. Debug overlays, selection outlines, bake preview tools, and offline validation tools are out of scope for this rule.

## Recommended V1 Strategy

The pragmatic v1 is:

1. Capture frames offline from engine-owned cameras.
2. Run reconstruction and optional refinement out of process.
3. Pack the result into an engine-owned clip container.
4. Stream clip data into GPU-resident fixed-capacity buffers.
5. Submit the visible clip with one draw call per actor in the main pass.

Do **not** start with real-time fitting. That would couple the riskiest parts of the project together: GPU capture, ML inference, differentiable refinement, runtime streaming, and final rendering. The engine should first prove that offline bake plus deterministic playback works end-to-end.

## Current Starting Point

What already exists:

- `XRENGINE/Models/Gaussian/GaussianSplatCloud.cs` loads a static `.splat`-style asset containing position, scale, rotation, color, and opacity for a single cloud.
- `XRENGINE/Scene/Components/Mesh/GaussianSplatComponent.cs` converts the cloud into instanced point-sprite data and rebuilds the render model when the cloud changes.
- `Build/CommonAssets/Shaders/Gaussian/GaussianSplat.vs` and `Build/CommonAssets/Shaders/Gaussian/GaussianSplat.fs` already render an ellipse-shaped Gaussian billboard from per-instance position, scale, rotation, and color.
- `XRENGINE/Rendering/Commands/GPUScene.cs` already has streaming-tier infrastructure such as `RegisterStreamingMesh(...)` and `AdvanceStreamingAtlasFrame()`.
- The GPU-driven rendering backlog already targets zero-readback draw submission through `MultiDrawElementsIndirectCount`; see [gpu-rendering.md](gpu-rendering.md).

What does **not** exist yet:

- animated Gaussian clip assets,
- capture tooling for multi-view frame bundles,
- a reconstruction worker contract,
- runtime streaming of per-frame Gaussian payloads,
- a fixed-capacity clip playback path that avoids rebuilding models every frame,
- a clip-aware scene component and editor workflow,
- validation harnesses for image quality, temporal stability, and draw-call budgets,
- a final renderer path for high-count transparent splats with robust sorting/visibility handling.

## Architecture Direction

Recommended data flow:

```text
Authored animation clip
  -> multi-view capture job
  -> frame bundle dataset
  -> reconstruction/refinement worker
  -> Gaussian clip packer
  -> streamable Gaussian clip asset
  -> background decode + prefetch
  -> persistently mapped GPU upload slot
  -> one draw submission per visible actor
```

Recommended implementation stance:

- bring up playback first with the current point-sprite ellipse shader path if it gets the data model and streaming architecture proven quickly,
- treat the current static `GaussianSplatComponent` path as a bootstrap path only, not the final animated runtime path,
- reuse the existing GPU streaming-tier ideas instead of inventing a second unrelated upload system,
- keep the reconstruction backend replaceable behind a manifest-driven worker boundary,
- keep authored timing integer-backed and clip-relative instead of inventing a separate float-timestamp playback path.

## Non-Goals

- Real-time per-frame reconstruction in the shipping runtime.
- One draw call for the entire scene.
- Shipping dependence on unvetted research code or licenses that fail the repo's commercial-use bar.
- Full view-dependent spherical-harmonic shading in the first end-to-end milestone unless the initial backend already makes it unavoidable.
- CPU readback in the visible playback hot path.
- Immediate parity across OpenGL, Vulkan, and every editor/debug path on day one. The first implementation should target the primary OpenGL path cleanly and leave a clear Vulkan follow-up list.

## Success Criteria

- A selected animated model or authored clip can be captured into deterministic multi-view frame bundles.
- The reconstruction worker can turn those bundles into a streamable Gaussian clip asset with resumable or restartable bake jobs.
- Runtime playback advances using clip-relative authored cadence and loops/seeks correctly.
- The visible playback path submits one draw call per visible animated Gaussian actor in the main pass.
- The hot path performs no CPU readback and no avoidable per-frame managed allocations.
- Quality validation exists for training-view reprojection, held-out views, and temporal stability.
- Performance instrumentation exists for draw calls, CPU submission cost, decode cost, upload bandwidth, and GPU frame time.

## Phase 0 - Scope Lock, Budgets, And Approval Gates

Outcome: the project stops being "Gaussian splatting in general" and becomes a concrete v1 target with measurable budgets.

- [ ] Lock v1 scope as offline bake plus runtime playback. Explicitly defer live fitting, persistent-Gaussian research, and scene-wide batching until the first end-to-end path is working.
- [ ] Define the primary use cases: character clip playback, prop animation playback, cinematic hero assets, or all three.
- [ ] Lock the meaning of the draw-call requirement and write it down in engine terms.
- [ ] Choose the reconstruction integration path: Python/CUDA sidecar, ONNX-exportable inference, or native interop bridge.
- [ ] Run a license and commercial-use audit for every candidate external dependency before integrating any backend.
- [ ] Define runtime budgets: maximum visible splats per actor, clip duration targets, prefetch depth, CPU decode budget, upload budget, and memory budget.
- [ ] Define bake budgets: camera count, capture resolution, optional G-buffer outputs, reconstruction wall time expectations, and acceptable storage size per minute of animation.
- [ ] Decide whether v1 stores full frames, shared static channels plus deltas, or a fixed-capacity frame table with per-frame active counts.
- [ ] Decide whether v1 shading is RGBA-only or includes low-order SH coefficients.
- [ ] Decide whether v1 uses the current point-sprite ellipse path for bring-up or jumps directly to a tile-based splat prepass.
- [ ] Define acceptance scenes and clips that will serve as the recurring validation corpus.

### Phase 0 Exit Criteria

- [ ] The supported v1 use cases are written down.
- [ ] The external dependency path is approved or explicitly deferred.
- [ ] Runtime and bake budgets are documented.
- [ ] The draw-call rule is no longer ambiguous.

## Phase 1 - Runtime Data Model And Asset Contract

Outcome: the engine has stable asset contracts for static clouds, animated clips, and runtime GPU payloads.

- [ ] Decide whether `GaussianSplatCloud` remains the static-cloud asset or becomes a shared primitive container used by both static and animated assets.
- [ ] Add a dedicated animated asset type, for example `GaussianSplatClip`, instead of overloading the current static cloud class.
- [ ] Define a binary clip header containing version, authored cadence, frame count, frame-table offsets, bounds, feature flags, and compression flags.
- [ ] Define per-frame metadata: active splat count, chunk offsets, local bounds, decode sizes, and optional keyframe/delta markers.
- [ ] Define the GPU payload layout explicitly: position, covariance or scale/rotation, color/opacity, optional SH, and any frame-local auxiliary fields.
- [ ] Keep the runtime payload as structure-of-arrays or tightly packed GPU-friendly blocks rather than per-splat managed objects.
- [ ] Decide whether frame data is fixed-capacity-per-actor, fixed-capacity-per-clip, or variable-count with a max-capacity slot and per-frame draw count.
- [ ] Include debug export paths for loose `.splat` or PLY-style inspection, but keep the shipping runtime path on the engine-owned binary format.
- [ ] Add versioning and feature negotiation so old baked assets fail clearly instead of partially loading incorrect data.
- [ ] Document coordinate conventions, handedness, color space, and unit scale in the asset format contract.

### Phase 1 Exit Criteria

- [ ] There is a documented on-disk format for animated Gaussian clips.
- [ ] There is a documented in-memory GPU payload format.
- [ ] Static and animated Gaussian asset ownership is clear.

## Phase 2 - Multi-View Capture Pipeline

Outcome: the engine can deterministically emit frame bundles that are suitable inputs to reconstruction.

- [ ] Add an editor-facing or CLI-driven capture job for a selected model, animation clip, and frame range.
- [ ] Add a reusable camera-rig generator that can emit 4, 6, 8, 12, or 16 capture views with deterministic transforms.
- [ ] Capture RGBA beauty by default.
- [ ] Decide whether depth, mask, normals, and motion vectors are required, optional, or backend-specific extras.
- [ ] Store per-view camera metadata: intrinsics, extrinsics, resolution, near/far, color space, frame number, and capture timestamp.
- [ ] Keep post-processing, exposure, tone mapping, and color transforms under explicit control so the bake input is stable.
- [ ] Use asynchronous GPU readback and staged disk writes so capture does not stall the render loop unnecessarily.
- [ ] Add deterministic folder and manifest naming so bake inputs can be cached, resumed, and diffed.
- [ ] Provide a narrow capture harness scene that exercises skinning, alpha hair, accessories, and fast motion.
- [ ] Record enough provenance to rebuild the same dataset later: source asset, clip name, engine commit, capture settings, and backend version.

### Phase 2 Exit Criteria

- [ ] Re-running the same capture job produces the same manifest and view set.
- [ ] Camera metadata is complete enough for reconstruction without manual patch-up.
- [ ] Capture can run across a representative animation clip without visible stalls or corrupted frames.

## Phase 3 - Reconstruction And Bake Worker Integration

Outcome: the engine can hand frame bundles to a backend worker and receive reconstructed Gaussian frames back in a repeatable way.

- [ ] Define a worker manifest format that is engine-owned and backend-neutral.
- [ ] Implement the first backend adapter for Free-Range Gaussians or an equivalent sparse-view Gaussian reconstructor.
- [ ] Decide whether inference is launched as a local process, a service, or a script-driven toolchain.
- [ ] Add a dry-run mode that validates manifests and expected outputs without doing the expensive work.
- [ ] Capture and preserve backend logs, model versions, guidance settings, and command lines for reproducibility.
- [ ] Add resumable job handling so failed frames or chunks can be retried without restarting a full clip bake.
- [ ] Decide whether engine-perfect depth or masks are used as initialization hints, refinement losses, or validation-only buffers.
- [ ] Add an optional refinement phase for deterministic correction against captured views when the generative result is too unconstrained.
- [ ] Define backend output validation: frame count match, NaN or invalid covariance rejection, bounds sanity checks, and channel completeness.
- [ ] Keep the backend boundary narrow enough that another method can replace Free-Range Gaussians later.

### Phase 3 Exit Criteria

- [ ] A frame bundle can be baked end-to-end without manual file editing.
- [ ] Failed jobs are diagnosable and restartable.
- [ ] The backend contract does not leak research-project-specific assumptions into the engine runtime format.

## Phase 4 - Clip Packaging, Compression, And Import

Outcome: baked Gaussian frames are packed into a streamable engine asset rather than a folder of ad hoc files.

- [ ] Add a clip packer that ingests backend output and emits the engine-owned animated Gaussian clip format.
- [ ] Decide chunk granularity: per frame, group-of-frames, or keyframe plus delta segments.
- [ ] Store both clip-wide bounds and per-frame bounds for culling and prefetch.
- [ ] Add an indexed frame table for fast seek and loop operations.
- [ ] Decide whether channel data is compressed independently or as whole-frame blobs.
- [ ] Evaluate built-in compression versus a permissively licensed external codec only after the format and access pattern are stable.
- [ ] Support optional quantization for scale, rotation, color, or SH data if it materially reduces bandwidth without breaking quality goals.
- [ ] Add offline validation that can round-trip the packed asset back into a debug export for inspection.
- [ ] Add import-cache invalidation rules so changed source animation, capture settings, or backend version force a rebuild.
- [ ] Make the packer produce a concise build summary: frame count, splat count range, byte size, compression ratio, and bake provenance.

### Phase 4 Exit Criteria

- [ ] There is one engine-owned asset format for animated Gaussian clips.
- [ ] Seeking any frame does not require scanning the whole file.
- [ ] Packaging preserves enough provenance to understand what generated the asset.

## Phase 5 - Runtime Renderer Bring-Up

Outcome: the engine can render animated Gaussian clips without rebuilding meshes every frame.

### 5.1 Replace The Static-Rebuild Playback Path

- [ ] Do not use `GaussianSplatComponent.Cloud = ...` as the animated runtime path if that implies rebuilding the model every frame.
- [ ] Add a dedicated animated playback component, or cleanly split static and animated modes behind a common renderer surface.
- [ ] Allocate a fixed-capacity GPU slot for each active animated actor.
- [ ] Upload per-frame data into persistently mapped or equivalently cheap GPU buffers.
- [ ] Keep material state stable so playback does not require per-frame material rebinding logic per splat.
- [ ] Expose per-frame active count without CPU readback.

### 5.2 Choose The First Shipping Draw Path

- [ ] Decide whether the first shipping path is `DrawArraysInstanced`, `DrawElementsInstanced`, or the engine's GPU-driven indirect path.
- [ ] If the existing point-sprite ellipse shader is used for bring-up, harden it for clip playback rather than loose static clouds only.
- [ ] If the GPU-driven indirect path is used, keep animated Gaussian clips in a known material bucket so the CPU can issue a fixed draw submission without reading batch ranges back.
- [ ] Ensure the chosen path increments the engine's draw-call counters correctly so regression tests can verify the one-draw rule.

### 5.3 Close The Quality Gap To Research-Grade Splatting

- [ ] Audit how far the current point-sprite ellipse path can go before artifacts become unacceptable.
- [ ] Decide whether final quality needs a tile-based splat prepass, better sorting, or more exact covariance projection.
- [ ] Add a follow-up path for high-count splats where plain point sprites or naive alpha blending break down.
- [ ] Keep the bootstrap renderer and the higher-quality renderer sharing the same clip asset format.

### Phase 5 Exit Criteria

- [ ] An animated Gaussian clip can advance frame-to-frame without rebuilding an `XRMesh` each tick.
- [ ] The visible playback path uses one draw submission per actor in the main pass.
- [ ] Draw-count verification is testable through engine stats.

## Phase 6 - Streaming And Playback Runtime

Outcome: clip playback streams data predictably and respects the engine's timing model.

- [ ] Implement a background IO and decode queue with bounded memory usage.
- [ ] Prefetch upcoming frames or chunks based on clip-relative playback time rather than ad hoc floating-point accumulation.
- [ ] Use authored clip cadence and integer-backed timing for frame selection, loop, and seek behavior.
- [ ] Add loop, clamp, ping-pong, and manual-scrub playback policies only after the base frame-advance logic is stable.
- [ ] Support seeking without forcing a full clip reload.
- [ ] Upload only the frame data required for the current slot, not the whole clip.
- [ ] Add a decode ring buffer and upload ring buffer so background work and render-thread uploads stay decoupled.
- [ ] Integrate with the existing streaming-tier concepts where that reduces duplicated buffer-management logic.
- [ ] Keep the hot path allocation-free aside from intentionally pooled buffers.
- [ ] Add failure handling for missing chunks, corrupt frames, canceled playback, and clip unload during background decode.

### Phase 6 Exit Criteria

- [ ] Long clips can play without unbounded memory growth.
- [ ] Seeking and looping do not desynchronize from authored cadence.
- [ ] The visible path performs no CPU readback just to advance playback.

## Phase 7 - Editor, Import, And Scene Workflow

Outcome: animated Gaussian clips are usable by normal engine workflows instead of only custom scripts.

- [ ] Add an import or bake entry point in the editor for animated Gaussian clip generation.
- [ ] Add a component or asset reference workflow for placing animated Gaussian actors in scenes.
- [ ] Expose clip playback controls: play, pause, loop mode, speed, frame stepping, and debug frame display.
- [ ] Add editor-visible diagnostics for clip load status, active frame, decoded frame queue depth, and GPU upload state.
- [ ] Add preview tooling for inspecting a baked clip frame-by-frame without starting a full play-mode session.
- [ ] Decide how animated Gaussian clips interact with standard animation components and prefab workflows.
- [ ] Add a narrow Unit Testing World harness or equivalent scene for repeated validation.
- [ ] Update docs and any VS Code tasks if bake commands, launch flags, or workflows become user-facing.

### Phase 7 Exit Criteria

- [ ] A developer can bake, import, place, and preview an animated Gaussian clip without hand-editing files.
- [ ] Playback state and failure modes are visible in editor tooling.

## Phase 8 - Validation, Testing, And Performance Gates

Outcome: the project has hard evidence for quality and performance instead of screenshots and anecdotal runs.

### 8.1 Asset And IO Tests

- [ ] Add tests for clip header parsing, version handling, frame-table validation, and bounds correctness.
- [ ] Add tests for corrupt or truncated chunk handling.
- [ ] Add tests that seeking to arbitrary frames resolves the correct chunk and active-count metadata.

### 8.2 Timing And Playback Tests

- [ ] Add deterministic tests for authored frame cadence, looping, seeking, reverse playback if supported, and rate scaling.
- [ ] Add tests that playback stays integer-stable over long durations instead of drifting due to float modulo behavior.

### 8.3 Renderer And Draw-Call Tests

- [ ] Add GPU tests that verify one draw submission per visible animated Gaussian actor in the main pass.
- [ ] Add tests that zero visible splats or culled actors issue zero useful work.
- [ ] Add tests that clip frame swaps do not rebuild meshes or re-register scene geometry every frame.

### 8.4 Image-Quality And Temporal Tests

- [ ] Add an offline validation harness that re-renders from capture cameras and compares against ground truth.
- [ ] Add held-out-view comparisons for novel-view quality on a recurring corpus.
- [ ] Add temporal-stability checks for flicker, alpha popping, and frame-to-frame bounds explosions.
- [ ] Add targeted tests for hair, thin accessories, transparent details, and fast motion.

### 8.5 Performance Tests

- [ ] Track clip decode time, upload bandwidth, GPU frame time, and total draw submissions.
- [ ] Track peak resident memory for decoded frames, GPU clip buffers, and staging buffers.
- [ ] Add regression thresholds for representative clips.

### Phase 8 Exit Criteria

- [ ] Quality metrics exist for at least one representative clip set.
- [ ] Performance budgets are measurable and enforced.
- [ ] Draw-call regressions are test-detectable.

## Phase 9 - Stretch And Post-V1 Research

These items are intentionally deferred until the base offline-bake and one-draw playback path is stable.

- [ ] Persistent or dynamic Gaussian identities across frames to reduce storage and improve temporal stability.
- [ ] Better anti-aliasing and scale-change robustness inspired by Mip-Splatting-style filtering.
- [ ] More exact tile-based or compute-assisted splat rasterization when point sprites stop being sufficient.
- [ ] Higher-order view-dependent shading such as spherical harmonics.
- [ ] Scene-wide batching of multiple animated Gaussian actors into larger shared draw streams.
- [ ] Runtime streaming LOD and clip decimation for distant actors.
- [ ] VR-specific evaluation, including per-eye consistency and stereo artifacts.
- [ ] Optional live-update or in-editor iterative baking workflows.

## Key Design Decisions To Resolve Early

| Decision | Why it matters | Preferred initial answer |
|---|---|---|
| Offline bake vs live fitting | Determines whether runtime architecture stays simple enough to finish | Offline bake first |
| Fixed-capacity actor slot vs variable per-frame realloc | Determines whether one-draw playback stays practical | Fixed-capacity slot with per-frame active count |
| Static cloud path reuse vs dedicated animated component | Determines whether runtime stays allocation-heavy | Dedicated animated component or clean split |
| Bootstrap renderer vs final renderer | Determines delivery risk | Bring up with current ellipse path, then upgrade |
| Sidecar worker vs in-process ML | Determines dependency blast radius | Sidecar first |
| RGBA-only vs SH in v1 | Determines payload size and shader complexity | Start with RGBA unless quality data proves otherwise |

## Risks And Mitigations

| Risk | Why it hurts | Mitigation |
|---|---|---|
| Camera convention mismatch | Reconstruction quality collapses if intrinsics or handedness are wrong | Write a strict manifest contract and validation harness in phase 2 |
| Generative hallucination | Sparse-view inference can invent unsupported geometry or color | Use optional refinement, better masks/depth, and held-out-view validation |
| Variable splat counts | Per-frame realloc or mesh rebuild breaks the one-draw rule | Use fixed-capacity slots and explicit active-count metadata |
| GPU upload bandwidth | Per-frame uploads can dominate CPU or PCIe time | Quantize, chunk, prefetch, and upload only the active frame payload |
| Transparent compositing artifacts | Naive alpha blending can fail badly for dense splats | Keep a renderer-upgrade phase separate from data-model bring-up |
| Dependency/license drift | Research code often has awkward setup or license constraints | Gate all external backends through a documented audit before merge |
| Timing drift | Float-based clip playback causes frame mismatch over long runs | Use integer-backed authored cadence and clip-relative runtime ticks |

## Expected Code Touchpoints

These are the most likely areas to change once implementation starts:

- `XRENGINE/Models/Gaussian/` for static-cloud and animated-clip asset contracts.
- `XRENGINE/Scene/Components/Mesh/` for runtime scene components and editor-facing playback surfaces.
- `Build/CommonAssets/Shaders/Gaussian/` for bootstrap or final splat shader work.
- `XRENGINE/Rendering/Commands/` and `XRENGINE/Rendering/HybridRenderingManager.cs` if animated Gaussian clips are wired into the GPU-driven draw path.
- `XREngine.Editor/` for bake commands, preview tooling, and diagnostics.
- `XREngine.UnitTests/` for asset IO, timing, renderer, and performance regression tests.

## Related Docs

- [gpu-rendering.md](gpu-rendering.md)
- [../design/zero-readback-gpu-driven-rendering-plan.md](../design/zero-readback-gpu-driven-rendering-plan.md)
- [../../architecture/rendering/default-render-pipeline-notes.md](../../architecture/rendering/default-render-pipeline-notes.md)
