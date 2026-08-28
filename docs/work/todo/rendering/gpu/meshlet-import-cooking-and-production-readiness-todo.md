# Meshlet Import Cooking And Production Readiness TODO

Last Updated: 2026-08-22
Owner: Assets / Rendering / Vulkan
Status: **Completed — 2026-08-22; all unconditional meshlet production gates proven, with broad model-cache hydration retained as an explicit external condition**
Priority: Closed; Vulkan resident draw-stream Phase 1 is unblocked

Related docs:

- [Current meshlet production closeout work guide](meshlet-production-closeout-work-guide.md)
- [Vulkan resident draw stream and render task pool TODO](../optimization/vulkan-resident-draw-stream-and-render-task-pool-todo.md)
- [Model import binary cache TODO](../../assets/model-import-binary-cache-todo.md)
- [Model import binary cache design](../../../design/assets/model-import-binary-cache-design.md)
- [GPU meshlet zero-readback rendering design](../../../design/rendering/gpu-meshlet-zero-readback-rendering-design.md)
- [Historical GPU meshlet zero-readback implementation tracker](../../COMPLETED/gpu-meshlet-zero-readback-rendering-todo.md)
- [Mesh-submission strategy contract](../../../../architecture/rendering/mesh-submission-strategies.md)
- [Third-laptop Phase 0 resident-stream evidence](../../../investigations/rendering/vulkan-resident-draw-stream-phase0-2026-08-17.md)
- [Meshlet import production closeout evidence](../../../investigations/rendering/meshlet-import-production-closeout-2026-08-20.md)

## Ordering And Ownership

This tracker is closed. Its exit gate was satisfied on 2026-08-22, so Phase 1
implementation in the Vulkan resident draw-stream and render-task-pool tracker
is now unblocked and proceeds under that tracker's own acceptance rules.

Use the [closeout work guide](meshlet-production-closeout-work-guide.md) as the
completed execution order and acceptance protocol. This tracker retains the
full requirements history and prevents accepted work from being re-debugged
without contradictory evidence.

This tracker is the implementation authority for the meshlet-specific work that
was found incomplete after the historical meshlet tracker was marked complete:

- first-import LOD and meshlet cooking;
- persistence in normal cooked `XRMesh` assets;
- the meshlet-section slice of the model binary container;
- removal of meshlet cooking, source hashing, and disk access from rendering;
- explicit mixed meshlet/traditional GPU routing;
- Vulkan capability activation and production task/mesh validation; and
- pass, material, deformation, LOD, and lifetime correctness required to use
  meshlets without dropping geometry.

The model import binary cache tracker remains authoritative for the complete
prefab, material, texture-reference, animation-reference, transaction, and
general cache-hydration system. Tasks shared by the two trackers must be updated
in both places when implemented. This tracker does not require unrelated model
cache features to be pulled into the renderer prerequisite.

The historical GPU meshlet tracker remains evidence of the source-contract work
completed on its original branch. Its completion status is not evidence that
first-import cooking, warm-cache hydration, render-hot-path safety, pass parity,
or Vulkan hardware activation is complete.

## Outcome

Meshlets are portable CPU-owned derived mesh data. They are generated once when
a third-party model is first imported, persisted beside the cooked mesh and LOD
data, hydrated without invoking a source parser or meshlet builder on a valid
warm load, and uploaded to GPUScene before a meshlet draw becomes eligible.

The required lifecycle is:

```text
Cold import
  parse source
  -> finish all topology-changing normalization
  -> resolve effective cook settings per submesh
  -> generate base/manual/automatic LODs
  -> build and validate meshlets for every renderable LOD
  -> attach CPU payloads
  -> atomically publish cooked assets/cache

Warm load
  validate manifest and section compatibility
  -> hydrate mesh + LOD + meshlet payloads
  -> register prevalidated payloads with GPUScene
  -> invoke neither source parser nor LOD/meshlet builder

Render
  check O(1) payload revision/eligibility tokens
  -> submit eligible bins through task/mesh shaders
  -> submit planned ineligible bins through traditional GPU indirect
  -> perform no cooking, full source hashing, file access, or cache publication
```

## Wrap-Up Status — Completed 2026-08-22

The core closeout is complete for first-import cooking, standalone cooked-mesh
persistence, exact-root warm hydration, invalidation, runtime-without-cooker,
mixed planned routing, and live Vulkan EXT mesh-task submission. Meshlet
closeout Gates 1–7 are accepted: Sponza has stable production debug colors,
conservative task Hi-Z, debug-off three-view parity, final-frame RenderDoc
attribution, the missing/material/cache/lifetime matrix, validated parallel
graphics/non-graphics command workers, and an uncapped ShippingFast performance/
mouse-pressure characterization. After explicit user clearance, 86 focused
Release tests passed, the final uncapped Vulkan meshlet smoke remained clean,
the documentation handoff was completed, and resident draw-stream Phase 1 was
unblocked.
Detailed commands, environment, counters, hashes, and limitations are recorded
in the [production closeout evidence](../../../investigations/rendering/meshlet-import-production-closeout-2026-08-20.md).

Gate 2 was accepted on 2026-08-22. The Vulkan per-mip alias transition fix kept
the complete pyramid valid; controlled full, partial, near-plane/oblique,
normal-Z, reversed-Z, and OpenXR/Monado stereo-fallback runs passed. Hi-Z on/off
Sponza `DepthView` float hashes matched exactly at the recorded doorway, edge,
and oblique views. The closeout guide and investigation contain the exact
poses, hashes, counters, eye attachments, and remaining preview-copy caveat.

Gates 3 and 4 were accepted on 2026-08-22 under
`Build/_AgentValidation/20260822-023044-meshlet-gates3-4-switch/`. Three fixed
Sponza views matched traditional zero-readback geometry/material output and a
new RenderDoc capture follows EID 139 from EXT indirect-count mesh tasks to the
presented frame. The deterministic mixed fixture and optional-section probe
closed missing payload, masked, material override, transparent/OIT,
`Disabled`/`Empty`, provenance, corrupt repair, and read-only repair rows. Eight
payload remove/reload cycles and a near/oblique/far/return LOD sequence remained
exact once and settled with zero retired bytes, overflow, fallback, readback,
validation errors, or dropped operations. Broad prefab/model cache hydration
remains conditional on its separate provider and is not claimed here.

Gate 5 was accepted on 2026-08-22 under
`Build/_AgentValidation/20260822-122200-meshlet-gates5-6/`. The numeric `FrameOp`
migration had stopped executing planned non-graphics worker secondary ranges,
cached worker artifacts could outlive their output/pool owner, and ImGui font-
atlas descriptor resources bypassed lifetime authority. The corrected primary
recording and retirement order passed the full 16-cell graphics/non-graphics ×
forced/clean × 0/1/2/4-worker matrix with zero worker failures/timeouts, device
loss, VUID, readback, or fault-log match. Clean runs retained worker/chain/
primary reuse beyond frame 15,000, and the quarantine was removed only after
that matrix passed.

Gate 6 was accepted as a production characterization on the same date. The
matched uncapped RTX 3090 desktop ShippingFast pair measured meshlet versus
traditional render p50/p95 at `11.589/13.649` versus `6.829/7.686 ms`, Vulkan GPU
command-buffer p50/p95 at `11.464/15.311` versus `3.322/4.606 ms`, and frame-slot
wait p50/p95 at `4.102/5.196` versus `0.019/0.026 ms`. Synchronized utilization
was 97.3% mean / 98% p95 for meshlets versus 59.63% / 72% for traditional, while
submit/present remained sub-millisecond. This classifies the user's reproduced
system-wide mouse pressure as GPU execution/queue saturation; no cap is present.
The diagnostic supplement recorded 12,500 task groups, 570 cone and 361 Hi-Z
culls, 6,554,112 resident bytes, and zero capture-window rebuilds/retires. Both
ShippingFast variants retained zero generic readback/maps and CPU/forbidden
fallback. The current desktop is recorded separately from all laptop evidence,
and the materially slower meshlet result remains visible as an optimization gap.

The all-black scene seen when changing the settings UI from `CpuDirect` to
`GpuMeshletZeroReadback` was a material-table ABI mismatch: the CPU packed 13
logical words per row while a GLSL `std430` array of that struct requires a
16-word stride. Rows after material zero therefore read shifted/zero values.
The shared binding layout now computes and hashes the aligned stride, the CPU
entry is padded to 16 words, and construction asserts the generated layout and
native struct agree. Repeated CPU→meshlet→CPU→meshlet switching reproduced the
same nonblack albedo float hash with zero fallback/readback diagnostics.

The latest pulled implementation and final-binary acceptance prove:

- static cold: parser 1, builder 3, 551.296 ms builder time, 8,086,992 builder
  bytes, generated LODs 2, payloads 3, meshlets 80, GPU-written task records
  49, and 9,065 cumulative delayed task groups;
- static warm after the final conservative Hi-Z safeguard: parser 0, builder
  0, hydrations 3, GPU-written task records 24, and 3,456 cumulative delayed
  task groups;
- final mixed static plus skinned/morph cold: parser 2, builder 4, generated
  LODs 2, payloads 4, task records 49, requested draws 1,632 = consumed draws
  1,632, and explicit traditional routing for unsupported work;
- all accepted runs: zero generic GPU readback bytes, mapped buffers, forbidden
  fallbacks, render-path source hashing, render-path disk access, render-path
  cooker calls, and Vulkan validation VUIDs; and
- warm hydration also succeeds with the local `meshoptimizer.dll` removed from
  the validated runtime output and restored afterward.

The mixed warm probe intentionally remains open as a broader cache-closure
case: its three static LOD payloads hydrated, but the animated source still
parsed and built once. That does not invalidate the static exact-root warm
proof or cold mixed-routing proof, but it is not recorded as a warm-cache pass.

The stale-zero diagnostic bug was a Vulkan submission-order defect: a copy was
submitted before its deferred producer. Meshlet evidence now snapshots in the
ordered frame stream and submits host-visible copies only after the accepted
graphics submission. Asynchronous evidence is separately classified from
synchronous instrumented mappings, preserving the production zero-readback
gate. The expansion shader's task reservation and dispatch-count stores are
also race-free.

The Sponza production checkpoint adds the following evidence:

- a full cold import invoked the meshlet builder 393 times, attached 393 cooked
  payloads containing 12,707 meshlets, emitted 11,010 GPU task records, and
  produced nonzero delayed indirect dispatch evidence;
- the warm path hydrated the persisted Sponza payload set without source parsing
  or native meshlet building;
- payload-bearing cooked meshes now preserve the exact source streams needed to
  validate payload ownership instead of using the lossy SNORM16 position stream;
- the production meshlet draw now binds vertex-atlas buffers per populated
  static/dynamic/skinned tier. Task and mesh shaders filter by the same tier so
  each eligible row is submitted exactly once; and
- the legacy direct debug overlay no longer draws over an accepted production
  meshlet submission; and
- the small-scale transform eligibility bug is fixed, making all 393 opaque
  Sponza commands and 12,707 cooked meshlets eligible for production expansion.

The 2026-08-21 close-camera acceptance at `(-20.08, 0.055, 0.0)` looking at
`(-19.80, 0.055, 0.0)` shows clearly different neighboring meshlet colors in
both `AlbedoOpacity` and the final viewport. A consecutive frame, a nearby view,
and a warm DevParity restart retain the same deterministic palette. The accepted
frame reports one EXT indirect-count mesh-task operation and exact
`requested=3960`, `emitted=3960`, `consumed=3960` accounting, with zero
overflow, CPU/forbidden fallback, render-path hash/disk/cooker work,
maps/readbacks, descriptor failures, and VUIDs. Material/final-frame parity with
debug colors disabled was subsequently accepted at three views. The experimental
10 Hz render cap did not resolve the reported system-mouse jitter and remains
reverted; the later uncapped Gate 6 monitor attributes the pressure to near-
saturated mesh-task GPU execution and frame-slot queue waits.

Mesh-task Hi-Z now projects the complete meshlet sphere footprint, selects a
bounded conservative mip, and compares the conservative depth endpoint for the
active normal/reversed-Z convention. Unsupported or uncertain cases remain
visible, including sequential stereo/multiview. Frustum and cone culling remain
enabled, and the traditional GPU Hi-Z path is unchanged.

The later Gate 5 root-cause pass fixed the Vulkan device-loss regression in
worker-recorded command-chain secondaries. Parallel graphics and non-graphics
workers are enabled after the complete ownership/lifetime/submission matrix;
serial ownership remains a validated control, not a silent production fallback.

Standalone cache admission now hashes the full local source closure plus the
canonical import/LOD/meshlet/topology settings. Changed LOD settings, a changed
external glTF buffer, and a malformed Zstd payload were all rejected before
hydration/GPUScene. Independent cold generations produced identical semantic
payload hashes for all three LODs. The runtime identity deliberately excludes
local cooker provenance so compatible baked assets remain portable.

Unchecked boxes remain intentionally open until their own evidence exists. The
remaining direct Sponza blocker is debug-off useful-camera material/final-frame
parity against the traditional reference. Broader
boundaries include the inactive model/prefab binary cache and the
reimport/streaming/unload lifecycle matrix. RenderDoc now proves the Vulkan EXT
event, stages, and resident inputs, but not final-frame visual parity. The
diagnostics profile is intrusive and is not a shipping performance baseline.
Tests remain deferred under repository policy until the complete live
integration gate is cleared explicitly.

## Initial Implementation Snapshot (At Reopen)

| Area | Existing foundation | Reopened gap |
| --- | --- | --- |
| LOD and meshlet cooking | `MeshOptimizerIntegration.RegenerateAutoLods` and `BuildMeshlets` exist. | No import cook coordinator runs them in the required order for every base/manual/generated LOD. `BuildMeshlets` accepts LOD settings but does not generate LODs. |
| Import settings | `ModelCookSettings`, `MeshletGenerationSettings`, `MeshLodGenerationSettings`, and submesh overrides exist. | Effective settings are not resolved and executed per imported submesh; the generic meshlet setting defaults disabled. |
| Standalone cooked mesh | `XRMesh.CookedBinary.cs`, `XRMesh.CookedMeshlets.cs`, and `XRMeshYamlTypeConverter` already persist an attached `MeshletPayload`. | Imported meshes are externalized before a dedicated cook phase has guaranteed that payloads are attached. |
| Model binary container | The deterministic container reserves `Meshlets` and fingerprints model cook settings. | `ModelBinaryCacheCodec` still rejects hydration and skips publication; no model-owned meshlet section is composed or hydrated. |
| Repair cache | `MeshletPayloadDiskCache` reuses the standalone meshlet payload encoding. | GPUScene currently consults it from rendering with hard-coded Dense settings; its write replacement and concurrent-writer behavior are not the primary model-cache transaction. |
| GPUScene upload | Meshlet ranges, descriptors, vertex-reference streams, local triangle streams, bounds, and cones can be uploaded. | Runtime meshlet preparation scans every mesh under the scene lock, recomputes geometry hashes, and may build/write payloads before a GPU pass. Range reclamation across reimport/streaming/hot reload also needs an explicit contract. |
| GPU expansion/submission | GPU task-record expansion and indirect-count mesh-task submission exist. | Eligibility and production readiness are treated too globally; failure after strategy selection can skip geometry instead of routing an explicitly planned traditional GPU bin. |
| Shaders and passes | Static and skinned task/mesh shader sources exist. | The direct material-table path currently supports only opaque deferred, rejects override/depth-normal variants, and rejects a scene containing skinned commands. Deformation bounds/cones are not production-closed. |
| Vulkan capability | Extension query, feature enablement, command loading, shader compilation, and dispatch wrappers exist. | Both Phase 0 meshlet modes downgraded on this RTX 4070 laptop even though `vulkaninfo` reports `VK_EXT_mesh_shader`, `taskShader=true`, and `meshShader=true`; the exact failed rung was not retained in the profile evidence. |

## Locked Architecture Decisions

- Meshlets are derived asset data, not renderer-generated state.
- Cache portable CPU descriptors, vertex-reference indices, local triangle
  indices, bounds, cones, settings, and provenance. Do not cache Vulkan buffers,
  descriptor handles, command buffers, pipelines, or driver-specific objects.
- Finish all operations that can change topology before meshlet generation.
  Generate LODs first, then generate a payload for every renderable LOD.
- Imported triangle meshes use an import-specific meshlet-enabled default.
  Disabling generation is an explicit policy and is persisted as a real state.
- The standalone cooked `XRMesh` payload is the first usable persistence path.
  It must be populated before imported sub-assets are externalized.
- For an imported model binary cache, the model container's meshlet section is
  primary. `MeshletPayloadDiskCache` is secondary for repair, standalone meshes,
  procedural meshes, and legacy data only.
- The importer/cook service may perform file access and native meshoptimizer
  work. `XRMesh.GetOrCreateMeshletPayload` must not grow into a hidden general
  disk-cache API callable from rendering.
- Runtime format/shader compatibility is separate from cook provenance. A
  runtime without the meshoptimizer cooker must accept a compatible baked
  payload. A newer cooker version invalidates derived data during import/cache
  validation, not during every rendered pass.
- Imported mesh and LOD identity uses stable cache-local entity IDs. Absolute
  paths, display names, transient `Guid` fallbacks, process state, and GPU
  handles do not participate in deterministic section bytes.
- GPU submission policy and primitive-generation preference are independent:
  traditional and task/mesh bins may coexist in one sealed pass without CPU
  readback.
- Unsupported meshlet draws are classified before plan sealing. A selected
  zero-readback meshlet dispatch never retries on the CPU or silently falls
  through after submission begins.
- Mesh-shader device capability, shader/pipeline readiness, and per-pass draw
  eligibility are separate facts and are diagnosed separately.
- Current v1 meshlet payloads must obey one portable shader-compatible size
  profile. The existing shader contract and import defaults are 64 vertices and
  at most 124 output primitives until shader
  specialization and device-limit negotiation are deliberately implemented.
- Live feature validation precedes new automated test work. Add or run new tests
  for this integration only after the user explicitly clears test work under the
  repository testing policy.

## In Scope

- First-import generation and standalone cooked-mesh persistence.
- Deterministic cook identity, validation, and compatibility contracts.
- Shared meshlet section codecs used by standalone meshes and the model
  container.
- The model container's meshlet-section writer/reader, metadata, precedence,
  and hydration hook.
- Render-path removal of meshlet source hashing, disk I/O, native cooking, and
  cache writes.
- Explicit task/mesh versus traditional GPU binning per pass/material/draw.
- Vulkan EXT task/mesh capability activation and actionable diagnostics.
- Correct static opaque production dispatch plus explicit, lossless routing for
  every currently unsupported material/pass/deformation case.
- Skinned/morph bounds, cone-culling, LOD transition, stereo/multiview, reimport,
  streaming, and buffer-lifetime policy.
- Runtime/RenderDoc validation and, after explicit clearance, targeted tests.

## Out Of Scope

- Completing unrelated prefab component, texture, material, animation, or Unity
  cache features merely to finish this prerequisite.
- Caching API-specific GPU resources in model or mesh assets.
- Making every transparent/custom pass use mesh shaders when a planned
  traditional zero-readback bin is the clearer v1 architecture.
- Descriptor heap, descriptor buffer, device-generated commands, or
  buffer-device-address experiments not required by the current meshlet path.
- Silent CPU fallbacks or runtime generation as a substitute for cooked data.

## Success Criteria

- [x] A cold import builds meshlets exactly once for every enabled base/manual/
  generated LOD after final topology is known.
- [x] The imported `XRMesh` assets serialized during externalization contain the
  validated payloads and hydrate them on a second load.
- [x] A valid standalone warm mesh load invokes the meshlet builder zero times.
- [ ] **Conditional external dependency:** when broad model binary hydration is
  active, a valid warm hit invokes both the
  source parser and meshlet builder zero times.
- [x] Rendering performs zero meshlet source-geometry hashing, disk reads,
  disk writes, native meshoptimizer calls, or cache publication.
- [x] GPUScene registration consumes an immutable validated payload and an O(1)
  revision token.
- [x] Live reimport and unload validation proves that payload replacement and
  retirement invalidate/reclaim the correct ranges.
- [x] Static opaque meshlet bins coexist with skinned/morph and unsupported-pass
  traditional GPU bins without drops or duplicates in the mixed fixture.
- [x] Masked, override, transparent, missing-payload, and streaming coexistence
  cases complete the broader routing matrix.
- [x] `GpuMeshletZeroReadback` resolves to itself on the third-laptop RTX 4070
  for the supported static opaque fixture.
- [x] A RenderDoc capture contains the expected task and mesh stages and
  `vkCmdDrawMeshTasksIndirectCountEXT` with the correct indirect/count and
  meshlet buffers bound.
- [x] Steady-state `GpuMeshletZeroReadback` reports zero GPU readback bytes,
  maps, current-frame waits, forbidden fallbacks, and render-hot-path heap
  allocations attributable to meshlet preparation.
- [x] Every unavailable capability or ineligible draw reports one actionable
  primary reason rather than a generic meshlet-unavailable boolean.
- [x] Cold import, warm load, first render, steady state, reimport, streaming,
  and corruption-repair measurements are recorded in durable evidence.

## Primary Code Areas

- `XRENGINE/Core/Engine/AssetManager.ThirdPartyImport.cs`
- `XRENGINE/Core/Engine/ModelCaching/ModelBinaryCacheCodec.cs`
- `XREngine.Runtime.ModelAssetPipeline/Importing/`
- `XREngine.Runtime.ModelAssetPipeline/Importing/Caching/`
- `XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.Meshlets.cs`
- `XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.CookedBinary.cs`
- `XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.CookedMeshlets.cs`
- `XREngine.Runtime.Rendering/Objects/Meshes/MeshletPayloadDiskCache.cs`
- `XREngine.Runtime.Rendering/Serialization/XRMeshYamlTypeConverter.cs`
- `XREngine.Runtime.Rendering/Rendering/Meshlets/`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/`
- `XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs`
- `XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs`
- `XREngine.Runtime.Rendering/Runtime/RuntimeEngine.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/`
- `Build/CommonAssets/Shaders/Compute/Indirect/GPURenderExpandMeshlets.comp`
- `Build/CommonAssets/Shaders/Meshlets/`

## Phase 0 — Freeze The Reopened Contract And Baseline

- [x] Audit meshlet generation, cooked serialization, model-cache scaffolding,
  GPUScene registration, expansion, material routing, Vulkan capability, and
  task/mesh shader paths.
- [x] Confirm that standalone `XRMesh` serialization already carries an attached
  `MeshletPayload`.
- [x] Confirm that model-cache hydration/publication remains disabled.
- [x] Identify the render-path full scan, hashing, file access, native build, and
  cache-write path.
- [x] Identify current direct-pass, override/depth-normal, and skinned gaps.
- [x] Record the third-laptop requested/resolved meshlet downgrade and verify
  that the physical device advertises EXT task and mesh shader features.
- [x] Lock this tracker as the prerequisite for resident draw-stream Phase 1.
- [x] Choose one deterministic static imported model with multiple LODs, one
  skinned/morph model, and one mixed-pass scene for live acceptance.
- [x] Capture the third-laptop cold-import builder count/time/allocations and
  prove that the first meshlet pass performs zero runtime repair work.
- [x] Capture the current detailed Vulkan capability ladder in a diagnostic run,
  even if one or more fields require temporary local inspection.

Phase 0 exit gate:

- [x] Baseline evidence names exact source assets, import settings, cache state,
  source commit, selected GPU, driver, and output paths.
- [x] Every known gap maps to a phase and an owner in this tracker.

## Phase 1 — Import Cook Contract And API Cleanup

- [x] Add an explicit model-import cook coordinator in the modeling bridge rather
  than embedding topology work in the generic AssetManager wrapper.
- [x] Make import-specific model cook defaults enable meshlets for eligible
  triangle meshes without changing unrelated procedural-mesh defaults.
- [x] Resolve effective LOD and meshlet settings per submesh, honoring authored
  `MeshOptimizerSubMeshSettings` overrides.
- [x] Split or rename the current APIs so no caller can infer that
  `BuildMeshlets(..., lodSettings, ...)` generates LODs.
- [x] Define an explicit order: normalize topology, generate/reconcile LODs,
  then build meshlets per base/manual/generated LOD.
- [x] Define stable cache-local model, submesh, mesh, and LOD identities.
- [x] Define explicit generated-data states: `Present`, `Disabled`, `Empty`,
  `MissingRepairable`, `CorruptRepairable`, and `RepairFailed`.
- [x] Lock the v1 portable meshlet profile to shader-compatible limits or add a
  validated specialization contract before accepting non-default limits.
- [x] Keep cold cooking off the render thread through a bounded sequential import
  worker (`bound = 1`) with deterministic model/submesh/LOD output ordering.
- [x] Add cold-cook diagnostics for builder calls, LOD count, meshlet count,
  bytes, wall time, allocations, settings hash, and payload identity.

Phase 1 exit gate:

- [x] One orchestration API owns all import-time LOD/meshlet cooking.
- [x] Default and override resolution are deterministic and serializable.
- [x] Invalid meshlet limits fail during cooking with an actionable diagnostic.

## Phase 2 — First-Import Cooking And Standalone Mesh Persistence

- [x] Invoke the cook coordinator after successful source import and before
  embedded assets are externalized or the root asset is serialized.
- [x] Await all mesh processing and cooking in every import mode that can
  externalize or publish meshes; do not rely only on the current prefab-specific
  `ProcessMeshesAsynchronously = false` assignment.
- [x] Generate meshlets for the final base mesh and every renderable LOD.
- [x] Attach each validated payload to its owning `XRMesh` before
  `SaveAssetToPathCore`/`XRMeshYamlTypeConverter` serializes it.
- [x] Persist explicit disabled and empty payload states so warm loads never infer
  that they should build missing data.
- [x] Read the externalized mesh assets back and verify payload identity, counts,
  descriptor ranges, source geometry identity, and settings/provenance.
- [x] Prove a second standalone mesh load uses the serialized payload and performs
  zero builder calls.
- [x] Ensure import failure, cancellation, or payload-validation failure does not
  publish a partially cooked asset closure.
- [x] Report cache/persistence failure visibly while allowing the already parsed
  cold import to remain usable when policy permits.

Phase 2 exit gate:

- [x] First import builds and persists; second load hydrates and does not build.
- [x] Base and all LOD payloads survive the normal imported mesh asset lifecycle.
- [x] No first-render action is required to make imported meshlets available.

## Phase 3 — Cook Identity, Validation, And Runtime Compatibility

- [x] Replace the current positions/topology-only derived-data identity with a
  canonical hash of every input actually consumed by LOD or meshlet cooking,
  including selected normals, tangents, UVs, colors, seams/borders, and relevant
  skin-weight lock data.
- [x] Include effective settings, import/topology policy versions, shared section
  codec versions, payload version, and native/interop meshoptimizer provenance.
- [x] Separate `MeshletCookProvenance` from runtime payload compatibility so
  rendering never requires or queries the local cooker version.
- [x] Validate descriptor offsets/counts, vertex-reference ranges, local triangle
  indices, triangle padding, finite bounds/cones, source counts, total sizes,
  integer overflow, and configured read limits during hydration.
- [x] Reject or recook payloads whose baked limits exceed the active shader and
  selected Vulkan device limits before draw eligibility is sealed.
- [x] Ensure deterministic section bytes do not include absolute paths, transient
  object IDs, timestamps, process IDs, or unordered collection iteration.
- [x] Add an immutable payload revision/compatibility token computed once at
  cook/hydration time for O(1) runtime checks.
- [x] Harden secondary repair-cache publication with keyed writer arbitration,
  adjacent temporary files, post-write validation, and atomic replacement.

Phase 3 exit gate:

- [x] Equivalent cold imports produce equivalent semantic meshlet payload
  bytes/hashes for every LOD.
- [x] Changing any real builder input or setting invalidates derived data.
- [x] A runtime without meshoptimizer accepts a valid compatible baked payload.
- [x] Malformed payloads fail bounded validation before reaching GPUScene.

## Phase 4 — Shared Meshlet Sections And Model-Cache Integration

Coordinate every checked item with Phases 4–5 of the model import binary cache
tracker. This phase owns the meshlet slice, not unrelated prefab hydration.

- [ ] **Conditional external dependency:** extract the remaining reusable mesh-
  core codec needed by broad model/prefab hydration. The shared meshlet-section
  codec used by standalone and model-container paths is complete.
- [x] Make standalone `XRMesh` serialization compose the shared codecs.
- [x] Define model-owned mesh directory/LOD table references to one meshlet
  payload per renderable LOD.
- [x] Serialize the model container `Meshlets` chunk with settings, provenance,
  explicit state, stable local ownership, counts, and checksums.
- [x] Hydrate `XRMesh.MeshletPayload` before any mesh is registered with GPUScene.
- [x] Enforce model-container-primary and repair/standalone-disk-cache-secondary
  precedence.
- [x] Repair an optional missing/corrupt meshlet section from valid cached core
  geometry without opening the source model parser.
- [x] Keep repaired data in memory and warn when a read-only cache cannot be
  republished.
- [x] Add builder/parser counters to the model-cache diagnostics so a valid warm
  hit can prove both counts are zero when full model hydration becomes active.
- [x] Update the model import binary cache tracker with the completed standalone/
  meshlet-section work and the still-blocked broad hydration boundary.

Phase 4 exit gate:

- [x] Standalone and model-container formats share one meshlet codec authority.
- [x] The model meshlet chunk can round-trip every explicit payload state and LOD.
- [x] Model-cache precedence and optional repair are unambiguous and parser-free.

## Phase 5 — Remove Cooking, Hashing, And File Access From Rendering

- [x] Remove the pre-pass call to
  `GPUScene.EnsureRuntimeMeshletPayloadsForMeshletDispatch` from
  `RenderCommandCollection`.
- [x] Replace the scene-wide runtime repair scan with explicit registration of a
  prevalidated payload plus its immutable revision/compatibility token.
- [x] Remove per-pass `ComputeSourceMeshHash` work and any equivalent full mesh
  traversal from GPUScene render preparation.
- [x] Prohibit `MeshletPayloadDiskCache.TryLoad/TryStore`, `File.*`, and
  `MeshOptimizerIntegration.BuildMeshlets` on render-reachable call paths.
- [x] Move optional repair into asset loading or a bounded derived-data service
  that completes before a repaired draw becomes meshlet-eligible.
- [x] Mark a missing/incompatible payload ineligible without mutating it under the
  GPUScene lock.
- [x] Define reimport/hot-reload/streaming invalidation and upload ordering at a
  frame boundary.
- [x] Add meshlet range free/reuse/compaction or an equivalent bounded lifetime
  contract so churn cannot grow append-only GPU buffers indefinitely.
- [x] Add source guards/counters for render-path cooker calls, source hashes, disk
  reads/writes, repair requests, uploads, reclaimed bytes, and registration time.

Phase 5 exit gate:

- [x] A source trace shows no render-reachable meshlet builder or disk-cache path.
- [x] Steady-state meshlet preparation is O(changes), not O(all scene meshes).
- [x] Reimport, unload, streaming, and payload replacement reclaim or reuse the
  correct GPU ranges without stale descriptors.

## Phase 6 — Explicit Mixed GPU Routing And Readiness Contracts

- [x] Separate submission mode (`CpuDirect`, instrumented GPU indirect,
  zero-readback GPU indirect) from primitive path (`TraditionalOnly`,
  `MeshShaderPreferred`, `MeshShaderRequired`).
- [x] Resolve meshlet eligibility per pass/material/draw before the frame plan is
  sealed.
- [x] Partition eligible task/mesh bins and ineligible traditional GPU-indirect
  bins without CPU readback or duplicate scene ownership.
- [x] Preserve the strict required mode: `MeshShaderRequired` fails visibly before
  submission when any required capability or payload is unavailable.
- [x] Preserve the preferred mode: unsupported draws remain visible through an
  explicitly planned traditional zero-readback bin.
- [x] Remove the global “any skinned command rejects the meshlet path” behavior;
  classify individual draws/bins.
- [x] Replace dialect-only `SupportsProductionMeshletShaders()` semantics with
  device capability plus actual program/pipeline and per-pass readiness.
- [x] Record requested submission, primitive preference, resolved per-bin path,
  eligibility counts, and one primary reason for every ineligible bin.
- [x] Ensure a task/mesh program warmup/link/submission failure cannot silently
  drop geometry or retry a sealed zero-readback pass on the CPU.

Phase 6 exit gate:

- [x] The mixed static plus skinned/morph fixture renders every requested draw
  exactly once.
- [x] Masked and override draws complete the mixed material/state matrix.
- [x] Eligible opaque bins use mesh shaders while planned skinned/morph and
  unsupported-pass bins remain on traditional GPU indirect with zero readback.
- [x] Required-mode failure and preferred-mode routing are both explicit.

## Phase 7 — Vulkan EXT Capability Activation And Diagnostics

- [x] Publish a structured capability ladder containing selected GPU identity,
  extension advertised/requested/enabled, task/mesh features advertised/enabled,
  EXT command table loaded, device limits, shader compilation, program linking,
  graphics-pipeline readiness, renderer availability at resolution time, and
  per-pass readiness.
- [x] Retain the exact failed rung and expected/actual values in normal logs,
  profiler manifests, MCP render stats, and editor diagnostics.
- [x] Confirm the selected Phase 0 adapter is the RTX 4070 reported by
  `vulkaninfo`; do not infer logical-device enablement from physical exposure.
- [x] Diagnose and fix the current third-laptop downgrade without bypassing any
  required feature, command, limit, or pipeline gate.
- [x] Query EXT mesh shader properties and verify the selected cook profile fits
  task workgroup, mesh workgroup, output vertex, output primitive, and preferred
  invocation limits.
- [x] Verify task/mesh shader stage mapping, SPIR-V compilation, reflection,
  descriptor layout merging, dynamic-rendering attachment compatibility, and
  pipeline creation.
- [x] Verify compute-write to indirect-command/task-shader/mesh-shader barriers
  and buffer usages for expansion, dispatch count, task records, descriptors,
  vertex references, and local triangle indices.
- [x] Make `GpuMeshletZeroReadback` resolve to itself for the supported static
  opaque fixture on this machine.

Phase 7 exit gate:

- [x] Capability diagnostics distinguish physical support, device enablement,
  command availability, pipeline readiness, and draw eligibility.
- [x] The third-laptop static opaque run reaches real EXT indirect-count mesh-task
  submission without relaxing the zero-readback contract.

## Phase 8 — Pass, Material, Deformation, LOD, And View Correctness

- [x] Validate the existing opaque-deferred static material-table path first.
- [x] Route masked, depth-only, depth-normal, shadow, velocity, capture, forward,
  transparent, OIT, and override variants through either a real compatible
  meshlet program or an explicit traditional GPU bin.
- [x] Define custom-material eligibility from the same generated material binding
  layout used by traditional zero-readback rendering.
- [x] Wire skinned meshlet vertex inputs and current/previous bone data, or keep
  each ineligible skinned bin explicitly traditional until that wiring is ready.
- [x] Wire morph inputs or classify morph draws explicitly traditional.
- [x] Use conservative deformed object/meshlet bounds and disable cone culling
  where skinning/morph deformation makes baked cones unsafe; never permit false
  negative culling.
- [x] Handle negative-determinant transforms, winding, two-sided materials, and
  cone orientation consistently.
- [x] Validate base/manual/generated LOD range lookup and transition expansion.
- [x] Validate sequential stereo and Vulkan multiview culling/output semantics.
- [x] Validate capacity growth and overflow behavior without silent task loss.
- [x] Confirm cached cold-cook descriptors produce the same GPUScene bytes and
  visible result as the pre-cache reference generation path.

Phase 8 exit gate:

- [x] Every required pass/material/deformation/view case either has a correct
  meshlet path or a documented explicit traditional GPU route.
- [x] Three camera positions show no missing, duplicated, stale, or falsely culled
  geometry during LOD transition, animation, reimport, or streaming.

## Phase 9 — Live Validation, RenderDoc, Performance, And Closeout

Complete live/runtime validation before requesting clearance for new test work.

- [x] Reserve one bounded `Build/_AgentValidation/<run>/` root and record exact
  commands, settings, source commit, cache state, GPU, driver, and log paths.
- [x] Run cold import, second standalone mesh load, first render, and steady-state
  captures with builder/parser/hash/I/O/upload/allocation counters.
- [x] Run a full cold Sponza import and verify that every eligible imported mesh
  receives a validated payload before rendering: 393 builder calls, 393 cooked
  payloads, and 12,707 meshlets.
- [x] Load the persisted Sponza payload set without source parsing or native
  meshlet building and reach a real production Vulkan EXT mesh-task submission
  with the correct static vertex-atlas tier bound.
- [x] Implement and validate conservative mesh-task Hi-Z footprint/depth-range
  math. Accepted 2026-08-22 with controlled full/partial/near-plane,
  normal/reversed-Z, OpenXR/Monado stereo-fallback, and exact Hi-Z on/off
  `DepthView` parity at three useful Sponza camera positions.
- [x] Compare those three Sponza views with the traditional reference and close
  the separate debug-off material/final-frame parity gate.
- [x] Confirm the production meshlet debug mode assigns visibly distinct,
  stable colors to neighboring Sponza meshlets. Accepted 2026-08-21 in both
  `AlbedoOpacity` and the final viewport across consecutive frames, a nearby
  view, and a warm DevParity restart; exact evidence is recorded in the
  closeout guide and production-closeout investigation.
- [x] Run valid warm, disabled, empty, changed settings, changed source, changed
  cooker provenance, corrupt optional section, read-only repair, and runtime-
  without-cooker scenarios. The optional meshlet-section matrix is complete;
  broad prefab/model cache hydration remains a separate conditional row.
- [x] Run static, skinned, morph, mixed-pass, LOD transition, stereo/multiview,
  reimport, hot reload, streaming, unload/reload, and capacity-overflow scenarios.
- [x] Run `rdc doctor` before Vulkan capture.
- [x] Capture a real meshlet frame into the run root's `renderdoc/` directory.
- [x] Follow an open-work-close RenderDoc session; inspect `info`, passes, bounded
  draw/event lists, pipeline state, task/mesh shaders, and bindings, export the
  relevant render targets, visually inspect the PNGs, and run `rdc close`.
- [x] Prove the captured event uses `vkCmdDrawMeshTasksIndirectCountEXT`, the
  expected task/mesh stages, GPU-written indirect/count buffers, resident meshlet
  streams, material table, transforms, and correct attachment state.
- [x] Compare final framebuffer and suspicious intermediate outputs against the
  traditional zero-readback reference from at least three camera positions.
- [x] Run steady-state zero-readback, render-hot-path allocation, buffer-residency,
  churn/compaction, and dense-scene performance measurements.
- [x] Diagnose the worker-recorded command-chain device-loss root cause and
  re-enable parallel recording only after graphics/non-graphics lifetime and
  submission validation passes; the serial-owner quarantine remained in place
  until the complete matrix passed.
- [x] Record each available machine independently without combining unmatched
  results. The Gate 6 RTX 3090 desktop is separate from the prior RTX 4070
  laptop and any later original-laptop evidence.
- [x] After live validation succeeds, ask for explicit user clearance before
  adding or running new integration/regression tests.
- [x] After clearance, add targeted deterministic cache, validation, routing,
  lifetime, Vulkan capability, and parity coverage under `XREngine.UnitTests/`.
- [x] Update the model-cache tracker, historical meshlet tracker, production
  rendering roadmap, mesh-submission architecture, and resident-stream tracker
  with final status and evidence links.

Gate 7 was accepted on 2026-08-22. The focused Release suite passed 86/86 in
`Build/_AgentValidation/20260822-122200-meshlet-gates5-6/reports/gate7-tests/gate7-final-targeted.trx`.
It covers cache terminal states, payload validation, exact mixed routing,
generation-safe replacement, Vulkan capability selection, conservative Hi-Z,
debug-color stability, command-worker lifetime, and zero-readback contracts.
The suite found a real dense-compaction defect that removed terminal triangle-
index padding; the production compactor now retains the aligned payload range.
The final uncapped warm Sponza run at
`Build/_AgentValidation/20260822-122200-meshlet-gates5-6/reports/gate7-final-smoke/summary.json`
recorded real mesh-task frame operations, 7,074 requested/consumed Vulkan draws,
and zero generic readback, mappings, CPU/forbidden fallback, VUIDs, or capture-
window meshlet rebuilds/retires. The Release editor build and whitespace audit
also passed.

## Validation Matrix

| Scenario | Cold/import expectation | Warm/runtime expectation |
| --- | --- | --- |
| Static mesh with generated LODs | **Passed:** LODs then meshlets build once; all payloads persist. | **Passed:** zero parser/builder calls; payloads upload before eligibility. |
| Meshlets explicitly disabled | Persist `Disabled`; do not call builder. | Remain traditional GPU; do not infer repair. |
| Empty/non-triangle mesh | Persist `Empty` or ineligible state. | No build loop; no missing-geometry side effect. |
| Cook setting or real source-input change | **Passed for LOD settings and local glTF dependencies:** deterministically invalidate and recook. | **Passed:** never accept stale descriptors. |
| Cooker version change | Invalidate at asset/cache boundary. | Rendering accepts an already validated compatible payload. |
| Runtime without meshoptimizer | Not a cold-cook configuration. | **Passed:** valid baked payload works; no DLL load is required. |
| Corrupt optional meshlet section | Repair from cached core when policy allows. | Never open source parser merely for optional repair. |
| Skinned/morph mesh | Persist topology payload and deformation metadata/policy. | **Passed for explicit traditional routing;** native deformation path remains optional. |
| Mixed opaque/masked/override/transparent scene | Cook eligible geometry once. | **Passed:** Gate 4 proves exact-once meshlet/traditional routing for missing payload, masked, override, transparent/OIT, skinned/morph, and unsupported-pass work. |
| Full Sponza scene | **Passed:** 393 payloads containing 12,707 meshlets were generated and validated. | **Passed:** persisted payloads reach production Vulkan EXT submission, production debug colors and conservative Hi-Z are stable, three debug-off views match traditional output, and Gate 6 records the uncapped ShippingFast performance envelope. |
| Reimport/hot reload/streaming | Publish new revision atomically. | Frame-boundary swap; old GPU ranges reclaimed safely. |
| Third-laptop Vulkan static opaque | No runtime cooking dependency. | Strategy resolves to meshlet and RenderDoc proves EXT dispatch. |

## Resident Draw-Stream Resume Gate

Do not start Phase 1 of the Vulkan resident draw-stream tracker until all of the
following runtime gates are satisfied:

- [x] Imported base and LOD meshlets are generated before serialization and a
  second standalone load performs zero builder calls.
- [x] Rendering performs no meshlet cooking, source hashing, disk access, or
  cache publication.
- [x] The static plus skinned/morph mixed fixture keeps unsupported draws visible
  through explicit traditional zero-readback bins with exact requested/consumed
  accounting.
- [x] Missing-payload, masked, override, transparent, and streaming cases prove
  the same no-drop contract.
- [x] The third-laptop static opaque fixture resolves to real
  `GpuMeshletZeroReadback` and maintains the zero-readback/no-CPU-fallback
  contract.
- [x] RenderDoc proves EXT task/mesh stages and indirect-count submission with
  correct resident inputs.
- [x] RenderDoc/MCP comparison proves final visual output parity from useful
  camera positions.
- [x] Reimport, unload, streaming, and payload replacement have a bounded,
  generation-safe GPUScene range lifetime.
- [x] Capability and eligibility diagnostics retain one exact primary reason for
  every downgrade or ineligible bin.
- [x] Durable evidence is linked here and from the resident draw-stream tracker.

The evidence rows above and closeout Gates 1–7 are satisfied. Every
unconditional success criterion and the resident-stream resume gate is closed;
Vulkan resident draw-stream Phase 1 may now begin under its own tracker. The two
unchecked rows in this document are explicitly conditional on the separate
broad model/prefab binary-cache provider and do not reopen this meshlet tracker.
