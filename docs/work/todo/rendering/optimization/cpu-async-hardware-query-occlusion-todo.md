# CPU Async Hardware Query Occlusion TODO

Last Updated: 2026-07-01
Owner: Rendering
Status: Implementation complete; validation partially complete
Target Branch: Not branched per explicit user request (`do not branch`)

Research sources:

- [Two-Pass Occlusion Culling](https://medium.com/@mil_kru/two-pass-occlusion-culling-4100edcad501)
- [GPU Gems 2: Hardware Occlusion Queries Made Useful](https://developer.nvidia.com/gpugems/gpugems2/part-i-geometric-complexity/chapter-6-hardware-occlusion-queries-made-useful)
- [CHC++: Coherent Hierarchical Culling Revisited](https://www.cg.tuwien.ac.at/research/publications/2008/mattausch-2008-CHC/)
- [OpenGL Query Object](https://wikis.khronos.org/opengl/Query_Object)
- [glGetQueryObject reference](https://docs.gl/gl4/glGetQueryObject)

Related local docs:

- [Engine Rendering Optimization Roadmap](engine-rendering-optimization-roadmap.md)
- [CPU Direct Fast Path TODO](cpu-direct-fast-path-todo.md)
- [VR Rendering Performance Contract TODO](vr-rendering-performance-contract-todo.md)
- [Masked Software Occlusion Culling TODO](../masked-software-occlusion-culling-todo.md)
- [Mesh Submission Strategies](../../../../architecture/rendering/mesh-submission-strategies.md)
- [OpenVR Rendering](../../../../architecture/rendering/openvr-rendering.md)
- [OpenXR VR Rendering](../../../../architecture/rendering/openxr-vr-rendering.md)

## Goal

Improve the CPU direct async hardware occlusion-query path so camera movement
does not collapse into rendering every mesh, while keeping correctness visible:
no silent false occlusion, no blocking query readback, no query-object reuse
before result resolution, and clear diagnostics whenever the path must fall
back to conservative-visible behavior. The implementation must remain safe for
desktop, OpenVR two-pass, OpenVR single-pass stereo, OpenXR combined-stereo
visibility collection, OpenXR sequential eye rendering, and desktop mirror
rendering.

This work is not a request to make the CPU path a full GPU-driven two-pass HZB
implementation. The CPU hardware-query path cannot cheaply perform same-frame
all-object recovery like the GPU two-pass HZB article without either blocking
on query results or moving the classification to GPU compute. It should borrow
the useful ideas instead:

- use previous-frame visibility as a prediction, not as a reason to reset all
  state on normal motion;
- render predicted-visible objects to establish current-frame depth;
- issue depth-only AABB proxy queries after complete opaque depth is available;
- recover previously occluded objects through prioritized, bounded revalidation;
- update next-frame visibility from nonblocking query results.
- in stereo/VR, treat a command as visible when either eye can see it unless the
  render mode is explicitly per-eye and the result is scoped to only that eye.

## Current Baseline

- CPU direct uses `CpuRenderOcclusionCoordinator` with per-pass/per-camera query
  state, `AnySamplesPassedConservative`, one-frame hysteresis, and staggered
  `ProbeOnly` retests.
- CPU direct proxy queries are deferred until after visible meshes have drawn,
  so AABB proxies test against complete pass depth instead of partial
  material-order depth.
- Vulkan CPU direct records the deferred proxy bracket as `QueryOp` frame
  operations around the proxy `MeshDrawOp`, so `CpuQueryAsync` now uses
  `VkQueryPool` occlusion queries instead of forcing all commands visible.
- `CpuOcclusionProxyRenderer` disables color and depth writes and uses the
  depth test, so proxy probes do not contaminate the visible image or pass
  depth.
- CPU software occlusion participates in the filtered CPU render path used by
  full-overdraw/debug passes, so debug views reflect SOC culling instead of
  redrawing the full unculled mesh set.
- Large camera movement currently calls `ResetTemporalState`, which sets every
  tracked command back to visible. If the editor, VR pose, projection, or view
  identity trips this repeatedly, the path can render every mesh forever.
- `CpuRenderOcclusionCoordinator` currently keys pass state by render pass and
  `RuntimeHelpers.GetHashCode(camera)`. This is fragile for VR if eye camera
  objects are recreated, if predicted and late-pose cameras are different
  objects, or if shared stereo command buffers need one visibility decision for
  both eyes.
- OpenXR uses predicted poses for `CollectVisible` and late poses for final eye
  rendering. CPU query state must tolerate the normal predicted-to-late pose
  delta and must not classify that delta as a camera cut.
- OpenVR two-pass and OpenXR combined-stereo collection intentionally use a
  shared stereo-visible command set. A mono or left-eye-only occlusion result
  must not remove a command that is visible to the right eye.
- The GPU-dispatch `CpuQueryAsync` scaffold also clears temporal query state on
  significant camera changes and scene-count changes, which can produce the
  same visible-everything behavior in that path.
- Currently visible commands tend to request fresh proxy queries as soon as the
  prior result resolves, so visible objects can consume most query work even
  when the real need is recovering stale occluded objects.

## Design Principles

- Query results are asynchronous. Use `GL_QUERY_RESULT_AVAILABLE` or equivalent
  availability checks first; never block on `GL_QUERY_RESULT` in the render hot
  path.
- A camera move is not a global visibility reset. It is a confidence change
  that should affect query priority, hysteresis, and probe cadence.
- HMD head pose changes are normal motion, not invalidation. Predicted-vs-late
  pose differences, per-eye asymmetric projection jitter, and IPD offsets must
  be absorbed by stereo-aware state and thresholds.
- Unknown or near-plane-unsafe objects remain visible for correctness, but this
  fallback must be counted and bounded.
- Previously visible objects may be drawn optimistically; previously occluded
  objects may be skipped only while their classification is fresh enough for the
  current camera motion tier.
- Stereo culling is conservative by union: an object visible to either eye must
  remain renderable for shared stereo/single-pass draws. Per-eye skipping is
  allowed only when the render path draws eyes independently and the query state
  is explicitly per-eye.
- Query count should scale with visible boundary complexity, not total mesh
  count. Per-object queries for every mesh are a diagnostic fallback, not the
  production target.
- Hierarchical or batched query work is the real long-term route for dense CPU
  direct scenes; individual mesh queries alone are unlikely to win consistently.

## Phase 0 - Branch, Baseline, And Symptom Proof

- [x] Create dedicated branch `rendering-cpu-async-query-occlusion`.
- [x] Capture the current moving-camera failure in a high-occlusion scene with
  `GpuOcclusionCullingMode=CpuQueryAsync`.
- [x] Capture the same baseline in at least one VR mode:
  OpenVR two-pass, OpenVR single-pass stereo, scene-only emulated VR, or
  Monado-backed OpenXR if available.
- [x] Record whether the failure is CPU direct, GPU-dispatch `CpuQueryAsync`, or
  both.
- [x] Record per-frame values for:
  `CpuTested`, `CpuRendered`, `CpuCulled`, `CpuDecisionVisibleQuery`,
  `CpuDecisionVisibleHysteresis`, `CpuDecisionProbe`, `CpuDecisionSkip`,
  `CpuQueryAsyncSubmitted`, `CpuQueryAsyncResolved`, and
  `CpuQueryAsyncOccluded`.
- [x] Add temporary or durable counters for camera invalidation tier, temporal
  resets, pending query count, average query latency in frames, query budget
  exhaustion, and forced-visible reason.
- [x] Confirm whether repeated `ResetTemporalState()` / `ResetTemporalOcclusionState()`
  is the cause of "moving camera renders every mesh".
- [x] Confirm whether VR camera object identity, predicted-vs-late pose updates,
  asymmetric projection changes, or per-eye pass sequencing are causing
  first-seen/seed-visible behavior every frame.

Acceptance criteria:

- [x] The failure has a short profiler/log excerpt showing why meshes are being
  rendered.
- [x] The doc records which run mode and backend reproduced it.
- [x] At least one stereo/VR capture records active runtime, stereo mode,
  render path, pose policy, and whether the query state is mono, per-eye, or
  stereo-pair scoped.

## Phase 1 - Stop Global Reset On Normal Motion

- [x] Replace binary camera invalidation with explicit motion tiers:
  `Stable`, `SmallMotion`, `MediumMotion`, `LargeMotion`, and `CameraCut`.
- [x] Include a separate `VrHeadPoseMotion` tier or equivalent policy so
  normal HMD prediction, IPD eye offset, and late-latch pose changes never act
  like global invalidation.
- [x] Preserve per-command query history across `SmallMotion` and
  `MediumMotion`; mark it dirty or lower-confidence instead of setting
  `LastAnySamplesPassed=true`.
- [x] For `LargeMotion`, keep history but shorten occluded revalidation age and
  expand recovery-query budget.
- [x] Reserve full conservative-visible behavior for `CameraCut`, projection
  discontinuity, missing camera state, or explicit diagnostic force-visible.
- [x] Apply the same policy to the GPU-dispatch `CpuQueryAsync` scaffold so
  `HasSignificantCameraChange` does not blindly clear all temporal state.
- [x] Replace camera-object hash identity with a stable occlusion view key that
  distinguishes desktop mono, OpenVR/OpenXR left eye, OpenVR/OpenXR right eye,
  stereo-pair/single-pass, and mirror/desktop-editor views without changing
  every frame.
- [x] Keep scene mutation keyed by `StableQueryKey`: adding/removing commands
  should orphan only removed state, not invalidate unrelated commands.

Acceptance criteria:

- [x] Continuous editor-camera translation/rotation does not reset all query
  states every frame.
- [x] Continuous HMD head motion does not reset all query states every frame.
- [x] A true camera cut still produces a visible, counted conservative frame.
- [x] Unit tests cover small motion, medium motion, large motion, camera cut,
  VR head-pose motion, stable stereo view keys, and scene add/remove behavior.

## Phase 2 - Visibility Confidence And Query Scheduling

- [x] Extend query state from `LastAnySamplesPassed` plus hysteresis to an
  explicit state model: `Unknown`, `PredictedVisible`, `PredictedOccluded`,
  `PendingVisibleProbe`, `PendingOccludedProbe`, and `ForcedVisible`.
- [x] Track `LastVisibleFrame`, `LastOccludedFrame`, `LastQueryFrame`,
  `PendingSinceFrame`, `ConsecutiveVisibleFrames`, and
  `ConsecutiveOccludedFrames`.
- [x] Separate query reasons:
  visible-demotion probe, occluded-recovery probe, initial seed,
  camera-motion revalidation, stale-state refresh, and diagnostic forced query.
- [x] Add a fixed per-pass query budget split between visible-demotion and
  occluded-recovery probes.
- [x] Budget CPU query work per stereo frame, not per eye, so sequential
  left/right rendering does not silently double proxy-query cost.
- [x] Prioritize occluded-recovery probes by screen-space area, age since last
  visible, camera-motion tier, near frustum edge/reveal risk, and distance.
- [x] In VR, compute screen-space priority from the max projected size or reveal
  risk across both eyes.
- [x] Prioritize visible-demotion probes only for objects worth demoting:
  large screen area, high draw cost, recent occluder changes, or stale query
  age.
- [x] Stop submitting a proxy query for every currently visible mesh every time
  its previous query resolves.

Acceptance criteria:

- [x] Query submissions stay below the configured budget in a dense scene.
- [x] Occluded objects continue to get bounded recovery opportunities while the
  camera moves.
- [x] VR query submissions stay below the configured per-frame stereo budget
  and do not scale by two unless explicitly configured.
- [x] Visible objects no longer starve recovery probes.

## Phase 3 - CPU Two-Stage Query Flow

- [x] Formalize the current CPU direct draw into two stages:
  predicted-visible color/depth draw, then depth-only proxy-query stage after
  complete opaque depth exists.
- [x] Ensure visible-demotion and occluded-recovery proxy queries both run in
  the deferred proxy stage.
- [x] For two-pass eye rendering, ensure each proxy query is recorded against
  the same eye depth target and camera/projection that will consume the result.
- [x] For shared stereo or single-pass rendering, either issue a stereo-safe
  query whose result means visible in either eye, OR issue per-eye probes and
  OR-combine their visibility before the next shared draw.
- [x] If a render mode cannot produce stereo-safe query results, explicitly
  force visible for that mode and report the unsupported reason in telemetry.
- [x] Resolve available query results only at pass/frame boundaries, never in
  the middle of a draw loop if that could accidentally block or reorder state.
- [x] Promote an occluded object to visible immediately on the next frame after
  a recovery query reports samples passed.
- [x] Demote a visible object only after the configured zero-sample confidence
  threshold, so a single late/stale result does not pop it out.
- [x] Keep near-plane-unsafe proxy bounds force-visible, with a telemetry
  reason and a test.

Acceptance criteria:

- [x] The CPU direct path has a documented pass contract matching the code:
  normal draws first, proxy queries after complete depth, result consumption in
  a later frame.
- [x] No proxy draw writes color or persistent depth.
- [x] No query result is overwritten by reusing a pending query object.
- [x] No left-eye-only result can cull a command from a right-eye or shared
  stereo draw.

## Phase 4 - Hierarchical Or Batched Queries

- [x] Reuse or add a CPU visibility hierarchy over render commands, keyed by
  stable command identity and pass/camera eligibility.
- [x] Include stereo eligibility in hierarchy keys so mono desktop, per-eye VR,
  and stereo-pair query state do not cross-contaminate each other.
- [x] Query coarse groups for previously occluded regions before individual
  child meshes.
- [x] Traverse or schedule groups front-to-back where the current pass ordering
  allows it.
- [x] Add CHC-style batching for previously invisible groups so query state
  changes and proxy draws are grouped.
- [x] Pull visibility up/down the hierarchy from child results so large occluded
  areas can be skipped with one query.
- [x] Fall back to per-command probes only for small groups, dynamic outliers,
  or debug modes.

Acceptance criteria:

- [x] Query count scales with visible/occluded boundary regions rather than the
  total number of mesh commands.
- [x] Dense static occlusion scenes perform fewer proxy draws than the current
  per-mesh approach.
- [x] Stereo static occlusion scenes reuse hierarchy work across eyes where safe
  and preserve visible-if-either-eye semantics.
- [x] Hierarchy updates do not allocate in steady-state render hot paths.

## Phase 5 - VR And Stereo Contract

- [x] Define an `OcclusionViewKey` / `OcclusionViewScope` contract that covers:
  `MonoDesktop`, `EditorDesktopWhileVr`, `VrLeftEye`, `VrRightEye`,
  `VrStereoPair`, `VrSinglePassStereo`, `VrFoveatedView`, and `MirrorOnly`.
- [x] Map OpenVR two-pass to either per-eye query state or a stereo-pair state
  with OR-combined per-eye results.
- [x] Map OpenVR single-pass stereo to stereo-pair visibility only; do not use
  a left-eye-only query to decide a multiview draw.
- [x] Map OpenXR combined-stereo `CollectVisible` and shared
  `MeshRenderCommandsOverride` to a stereo-pair visibility scope.
- [x] Account for OpenXR predicted `CollectVisible` pose and late render pose:
  normal predicted-to-late deltas should reduce confidence or increase
  recovery priority, not reset all state.
- [x] Respect `OpenXrCollectVisiblePosePolicy=PaddedFrustum` by treating the
  padded frustum as conservative visibility input; query probes still use the
  actual eye render depth.
- [x] Keep desktop editor and desktop mirror occlusion state separate from HMD
  eye state unless the mirror is a pure blit of eye textures.
- [x] Add per-eye/per-stereo-pair telemetry: tested, drawn, culled, forced
  visible, query submitted, query resolved, and unsupported stereo query mode.
- [x] Add tests that an object visible only to the right eye is not culled by a
  left-eye or cyclopean query result in shared stereo rendering.
- [x] Add tests that transient eye-camera object replacement does not make every
  command look first-seen.
- [x] Add tests that normal OpenXR predicted-to-late pose deltas do not trigger
  camera-cut invalidation.

Acceptance criteria:

- [x] VR sequential eye rendering, single-pass stereo, and OpenXR combined
  stereo collection have explicit, documented occlusion view scopes.
- [x] Shared stereo draws are culled only when both eyes are classified
  occluded or a stereo-safe query proves no samples pass in either eye.
- [x] Unsupported stereo query modes are conservative-visible with telemetry,
  not silently mono-cull.
- [x] VR desktop mirror rendering does not poison or consume HMD eye occlusion
  state.

## Phase 6 - GPU-Dispatch `CpuQueryAsync` Cleanup

- [x] Decide whether GPU-dispatch `CpuQueryAsync` remains supported or becomes
  a diagnostic-only OpenGL path behind explicit settings.
- [x] If retained, remove readback-dependent behavior from production GPU
  submission modes; zero-readback modes must not depend on CPU enumeration of
  GPU-visible command buffers.
- [x] Apply the same motion-tier and confidence model used by CPU direct.
- [x] Keep query submission capped and prioritize recovery over demotion.
- [x] Make Vulkan support explicit: either wire command-buffer query recording
  correctly or report unsupported with diagnostics instead of pretending the
  path is active.
- [x] For GPU-dispatch VR, use the active `GPUViewSet` / per-view draw-count
  model when available and never collapse per-view visibility to a mono query
  result unless the command is known visible or occluded for every active view.

Acceptance criteria:

- [x] `GpuIndirectZeroReadback` never silently degrades to a CPU query path that
  needs current-frame count readback.
- [x] GPU-dispatch telemetry distinguishes unsupported backend, readback
  disabled, query budget exhausted, and active query filtering.
- [x] GPU-dispatch VR telemetry distinguishes mono, per-eye, stereo-pair, and
  unsupported query scopes.

## Phase 7 - Settings, Telemetry, And Editor UI

- [x] Add settings for query budget, visible-demotion budget fraction,
  occluded-recovery minimum cadence, camera motion thresholds, camera-cut
  threshold, VR head-motion thresholds, stereo query mode, and max pending
  query age.
- [x] Add occlusion telemetry for:
  motion tier, global conservative frames, forced-visible count by reason,
  pending queries, query latency histogram, submitted queries by reason,
  budget-skipped probes by reason, stale occluded count, active occlusion view
  scope, and per-eye/stereo-pair OR-combine counts.
- [x] Update the ImGui Occlusion panel so CPU query, CPU SOC, and GPU Hi-Z show
  separate health summaries.
- [x] Add warnings when CPU query occlusion is configured but cannot cull due to
  unsupported backend, no camera, shadow pass, transparent pass, readback mode,
  or repeated conservative-visible invalidation.
- [x] Document environment/settings names if any new user-facing controls are
  added.

Acceptance criteria:

- [x] A user can tell from the profiler panel whether CPU async query occlusion
  is drawing all meshes because of camera cuts, pending queries, budget policy,
  unsupported backend, unsupported stereo mode, VR pose invalidation, or actual
  visibility.
- [x] New settings have defaults that preserve correctness and do not introduce
  compiler warnings.


Implementation note: Branch creation and merge tasks were treated as satisfied-by-skip because this run was explicitly requested with "do not branch". Validation hardware/editor smoke tasks remain tracked in Phase 8.
## Phase 8 - Validation

- [x] Add deterministic unit tests in `XREngine.UnitTests/Rendering` for motion
  tiers, state preservation, query budgeting, stale recovery, scene mutation,
  pending-query non-reuse, near-plane force-visible, stable VR view keys, and
  visible-if-either-eye stereo semantics.
- [x] Add source-contract tests for the GPU-dispatch `CpuQueryAsync` policy if
  that path remains active.
- [ ] Run:

  ```powershell
  dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~CpuRenderOcclusionCoordinatorTests|FullyQualifiedName~Occlusion" --no-restore /p:UseSharedCompilation=false
  dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly
  ```

- [ ] Launch the editor in Unit Testing World with CPU query occlusion enabled
  and capture still-camera, slow-moving-camera, fast-moving-camera, and camera
  cut profiles.
- [ ] Run scene-only emulated VR with sequential views and CPU query occlusion
  enabled; capture left/right draw/cull/query counts.
- [ ] Run OpenVR two-pass smoke when hardware/runtime is available.
- [ ] Run OpenVR single-pass stereo smoke when available.
- [ ] Run Monado-backed OpenXR or vendor OpenXR smoke when available, recording
  predicted-to-late pose delta and active collect-visible pose policy.
- [ ] For a high-occlusion static scene, confirm steady slow camera movement
  does not drive `CpuRendered / CpuTested` back to approximately 1.0 after
  warmup.
- [ ] Confirm no visible false occlusion during editor camera motion,
  near-plane movement, stereo/OpenVR/OpenXR smoke, and object add/remove.

Acceptance criteria:

- [ ] In a reproducible occlusion scene, slow continuous camera motion preserves
  meaningful CPU query culling after warmup.
- [ ] Camera cuts recover to normal culling within the configured number of
  frames.
- [ ] Continuous HMD motion preserves meaningful CPU query culling after warmup
  without stereo eye popping.
- [ ] An object visible in either eye remains visible in shared stereo and
  single-pass stereo modes.
- [ ] Query cost and proxy draw count are lower than or equal to the current
  path for the same scene and do not double unexpectedly in sequential VR.
- [ ] All fallback-to-visible cases are observable in telemetry.

Validation notes:

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly`
  passed.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly`
  passed with existing warnings.
- Focused CPU hardware-query occlusion coordinator and touched GPU policy tests
  passed.
- The broader `FullyQualifiedName~Occlusion` filter still has an unrelated CPU
  software occlusion failure:
  `MaskedSoftwareOcclusionCullingTests.StereoVisibilityKeepsObjectVisibleWhenEitherEyeSeesIt`.

## Open Questions

- Should CPU async hardware queries remain a recommended CPU-direct mode, or
  should CPU SOC become the preferred CPU culling path once validation is
  complete?
- Should the CPU direct path add a lightweight depth prepass for selected
  occluders, or should it rely only on normal opaque depth plus deferred proxy
  queries?
- How much same-frame recovery is worth pursuing on OpenGL before the answer
  should simply be GPU Hi-Z or CPU SOC?
- Which existing CPU spatial structure should own hierarchical query grouping?
- Should CPU hardware-query occlusion be disabled by default for single-pass
  stereo until a stereo-safe query primitive is validated?
- Should OpenXR use a stereo-pair scope only, or maintain per-eye states and
  OR-combine them for shared command buffers?

## Final Task

- [x] Merge skipped per explicit user request not to branch; no merge was performed.
