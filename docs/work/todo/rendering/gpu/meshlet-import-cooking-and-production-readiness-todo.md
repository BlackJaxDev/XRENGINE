# Meshlet Import Cooking And Production Readiness TODO

Last Updated: 2026-08-20
Owner: Assets / Rendering / Vulkan
Status: Implementation substantially landed; production acceptance is incomplete
Priority: Immediate; do not resume Vulkan resident draw-stream Phase 1 until the exit gate is satisfied

Related docs:

- [Vulkan resident draw stream and render task pool TODO](../optimization/vulkan-resident-draw-stream-and-render-task-pool-todo.md)
- [Model import binary cache TODO](../../assets/model-import-binary-cache-todo.md)
- [Model import binary cache design](../../../design/assets/model-import-binary-cache-design.md)
- [GPU meshlet zero-readback rendering design](../../../design/rendering/gpu-meshlet-zero-readback-rendering-design.md)
- [Historical GPU meshlet zero-readback implementation tracker](../../COMPLETED/gpu-meshlet-zero-readback-rendering-todo.md)
- [Mesh-submission strategy contract](../../../../architecture/rendering/mesh-submission-strategies.md)
- [Third-laptop Phase 0 resident-stream evidence](../../../investigations/rendering/vulkan-resident-draw-stream-phase0-2026-08-17.md)

## Ordering And Ownership

Implement this tracker now. Do not begin Phase 1 implementation in the Vulkan
resident draw-stream and render-task-pool tracker until the exit gate in this
document is satisfied.

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

## Wrap-Up Status — 2026-08-20

This implementation pass is wrapped up for now. The asset, runtime, Vulkan,
shader, planner, and measurement foundations are substantially implemented and
build cleanly, but the production acceptance gate is **not closed**. The latest
third-laptop run reaches and records real Vulkan EXT mesh-task indirect-count
commands without validation errors, readback, mapped buffers, or fallback. The
remaining blocker is acceptance evidence: delayed GPU-written task-record/group
diagnostics are still zero, the final strict cold-to-warm pair has not run on the
latest code, and the mixed/lifetime/RenderDoc matrices remain open.

Checkboxes now distinguish completed implementation/evidence from remaining
acceptance work. Checked implementation items are build-validated or directly
observed; unchecked exit gates remain deliberately open where the requested live
matrix, deterministic comparison, or capture proof has not been completed. A
feature listed as implemented here is not production-accepted until its related
live evidence and resume-gate items are also complete.

### Completed And Build-Validated

| Area | Completed work | Remaining acceptance |
| --- | --- | --- |
| Import cook | Added a single import cook coordinator; resolves explicit defaults/overrides and stable identities; generates LODs before meshlets for every renderable LOD; cooks Unity imports once after final composition. Fixed the native meshoptimizer x64 `size_t` ABI and validates all returned ranges before optimize/bounds calls. | Prove cold and warm counts with the strict harness, including manual/generated LOD coverage. |
| Payload and standalone cache | Added immutable payload v3 state, provenance, portable runtime-profile token, full range/finite validation, source-geometry revision binding, atomic standalone cooked-mesh publication, read-back validation, and warm hydration. | Complete a successful warm run proving parser=0, builder=0, and hydration>0. |
| Model meshlet section | Added deterministic model/submesh/LOD keys, a bounded shared section codec, container reader/writer support, and transactional primary/secondary/repair hydration that attaches only a complete validated closure. | General model/prefab binary-cache hydration remains intentionally disabled because the mesh-core/prefab-graph serializer/provider does not yet exist. Do not claim a model-cache warm hit until that broader cache layer lands. |
| Render hot path | Removed scene-wide source hashing, disk access, native cooking, and cache writes. GPUScene now accepts only immutable payloads validated for the mesh's O(1) geometry revision. | Confirm all prohibited-work counters remain zero during successful cold/warm production captures. |
| GPU lifetime | Added dense immutable meshlet buffer generations published at the frame boundary, fence-backed retirement, and live/retired byte telemetry. | Exercise reimport, unload, streaming, and repeated generation retirement under live rendering. |
| Mixed routing | Added selected-LOD-aware inverse meshlet/traditional predicates, rigid-static eligibility, skinned/morph/pass/material/view rejection, overflow-safe recovery, and exact-once traditional GPU routing for unsupported draws. | Run the mixed static+skinned/morph, transition, override, multiview, and unsupported-pass matrix and prove no drop or double draw. |
| Vulkan activation | Added a durable EXT capability ladder; exact portable 64-vertex/124-primitive gates; correct sparse output-location and effective-scalar accounting; task/mesh SPIR-V target identity; and actionable downgrade reasons. | Capture the successful ladder and live task/mesh stages together in the final evidence. |
| Vulkan deferred state | Mesh-task operations now freeze the explicit program, link generation, descriptor snapshot, bindless material table, target/fixed state, viewport/scissor, and exact target-compatible graphics pipeline. Primary admission associates every payload, mesh-task chains require a fresh primary, descriptor resources participate in frame semantics, and compute-to-mesh barriers occur outside rendering scopes. | Complete a clean live Vulkan dispatch and RenderDoc inspection. |
| Planner/capability correctness | Primary recording centrally consumes `EndsRendering`; pipeline-bound operations use the command-workspace runtime scope and frozen pipeline context; SceneCapture publication remains strict; off-frame profiling no longer poisons active capability/strategy state; pipeline compilation uses a non-starved normal-priority worker. | Exercise nested capture and auxiliary passes in the final matrix. |
| Vulkan descriptors and shaders | Generic descriptor binding excludes the renderer-owned bindless set, binds dynamic descriptors correctly, and validates the program/global-set contract. Task Hi-Z moved to view frequency. All mesh variants use a strided 32-lane/64-vertex loop, transformed bounds, finite-safe normalization, rigid-route normal transforms, and bounds-checked EXT stream access. | Prove delayed GPU task/group output and inspect the final bindings and mesh output in RenderDoc. |
| Diagnostics and harness | Added actual source-parser/native-builder/warm-hydration counters, render-path prohibited-work counters, buffer-generation gauges, dispatch accounting only after Vulkan command emission, delayed GPU-written task/group proof, cold/warm cache modes, and deterministic static/mixed fixtures. Fixed-camera control is deterministic with locomotion disabled. | Rerun after the final diagnostic-readback enablement, then obtain the strict cold and warm reports. |

Latest narrow/integrated validation:

- `dotnet build XREngine.Editor/XREngine.Editor.csproj -c Release --no-restore -v:q`
  passed with 0 warnings and 0 errors after the final renderer fixes.
- The focused Runtime.Rendering, ModelingBridge, and Vulkan Release builds also
  passed with 0 warnings and 0 errors during their implementation lanes.
- `git diff --check` reported no whitespace errors; repository line-ending
  advisories remain.
- `rdc doctor` passed. Two capture attempts did not produce an `.rdc` because
  they preceded the final mesh-shader fixes and the target device was lost.
- No tests were added or run. Repository policy requires successful live feature
  validation and explicit user clearance before test work for this integration.

### Live Evidence Obtained On The Third Laptop

The earlier cold smoke report at
`Build/_AgentValidation/20260817-210826-meshlet-production/reports/smoke-static-postscope-r2/summary.json`
proves the import portion is active on this machine:

- source parser calls: 1;
- native meshlet builder calls: 3;
- generated LODs: 2;
- validated payloads: 3;
- cooked meshlets: 80;
- render-path source-hash, disk, and cooker calls: 0;
- Vulkan dialect: `VulkanEXT`; meshlet dispatch readiness: true.

The latest stable fixed-camera warm report is
`Build/_AgentValidation/20260819-235811-meshlet-readiness/reports/static-r33-full-safe/summary.json`.
It reached one stable workload identity with the following relevant evidence:

- effective strategy: `GpuMeshletZeroReadback`;
- actual Vulkan mesh-task indirect command emissions: 456;
- requested draws: 1,664; consumed draws: 1,664;
- warm payload hydrations: 3; parser calls: 0; builder calls: 0;
- render-path source-hash, disk, and cooker calls: 0;
- GPU readback bytes, mapped buffers, fallback events, and forbidden fallback
  events: 0;
- Vulkan validation VUIDs: 0; capability failed rung: `ready`.

This is meaningful live progress, but it is **not final production acceptance**.
The delayed GPU-written task-record and indirect group counters remained zero.
The diagnostic profile was subsequently changed to request the existing
fence-owned dispatch diagnostics, but that exact build has not completed a new
strict capture. The run also used a warm cache populated by earlier work rather
than being the warm half of a final same-code cold-to-warm pair.

### Still Open

- Actual Vulkan EXT indirect-count command emission is proven, but delayed
  GPU-written task-record and group proof remains zero and must be rerun after
  the final diagnostic-readback enablement.
- Warm parser=0, builder=0, hydration>0 is proven independently; the final strict
  cold and exact-same-root warm pair on the latest code is still outstanding.
- The mixed-draw, LOD-transition, reimport/unload/streaming, corrupt/disabled
  payload, stereo/multiview, and unsupported-pass live matrix is outstanding.
- No real RenderDoc capture has been collected or inspected. `rdc doctor`
  passed, but that is tooling readiness only.
- No meshlet performance baseline or resident draw-stream Phase 0 promotion
  measurement is valid yet.
- Full model binary cache hydration/publication remains blocked on the general
  mesh-core/prefab-graph cache provider described above.
- No final performance baseline is valid yet; the diagnostics configuration is
  intentionally expensive and the resident-stream promotion measurement must
  come from a successful production-profile capture.

### Next Execution Order

1. Rerun the fixed-camera static diagnostics fixture after the final diagnostic-
   readback enablement and prove the supported `OpaqueDeferred` pass emits
   real Vulkan task records and a nonzero delayed GPU-written indirect group
   count. Keep planned unsupported passes traditional.
2. Run the strict cold capture, then the strict warm capture against the exact
   same standalone cache root and camera. Cold must show parser=1, builder=3,
   generated LODs=2, payloads=3; warm must show parser=0, builder=0, hydration>0;
   both must show zero prohibited render-path work and nonzero production GPU
   task/group evidence.
3. Run the mixed and correctness matrix: static plus skinned/morph, selected-LOD
   transitions, overrides/unsupported passes, multiview, streaming/reimport,
   unload, disabled/empty/corrupt payloads, and buffer-generation retirement.
4. Collect a real RenderDoc capture; inspect task/mesh events, pipeline state,
   descriptors, selected LOD geometry, and exported render targets.
5. Record performance and third-laptop baselines only from successful stable
   captures, update the resident draw-stream Phase 0 investigation, and perform
   the remaining P1 architecture review.
6. After the live gate passes, request explicit clearance for targeted automated
   tests. Resume resident draw-stream Phase 1 only after this document's resume
   gate is fully checked.

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
- [ ] When model binary hydration is active, a valid warm hit invokes both the
  source parser and meshlet builder zero times.
- [x] Rendering performs zero meshlet source-geometry hashing, disk reads,
  disk writes, native meshoptimizer calls, or cache publication.
- [ ] GPUScene registration consumes an immutable validated payload and an O(1)
  revision token; reimport and unload invalidate/reclaim the correct ranges.
- [ ] Meshlet and traditional GPU bins coexist without dropping unsupported
  pass, material, skinned, morph, streaming, or override draws.
- [x] `GpuMeshletZeroReadback` resolves to itself on the third-laptop RTX 4070
  for the supported static opaque fixture.
- [ ] A RenderDoc capture contains the expected task and mesh stages and
  `vkCmdDrawMeshTasksIndirectCountEXT` with the correct indirect/count and
  meshlet buffers bound.
- [x] Steady-state `GpuMeshletZeroReadback` reports zero GPU readback bytes,
  maps, current-frame waits, forbidden fallbacks, and render-hot-path heap
  allocations attributable to meshlet preparation.
- [x] Every unavailable capability or ineligible draw reports one actionable
  primary reason rather than a generic meshlet-unavailable boolean.
- [ ] Cold import, warm load, first render, steady state, reimport, streaming,
  and corruption-repair measurements are recorded in durable evidence.

## Primary Code Areas

- `XRENGINE/Core/Engine/AssetManager.ThirdPartyImport.cs`
- `XRENGINE/Core/Engine/ModelCaching/ModelBinaryCacheCodec.cs`
- `XREngine.Runtime.ModelingBridge/Importing/`
- `XREngine.Runtime.ModelingBridge/Importing/Caching/`
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
- [ ] Capture current cold-import builder count/time/allocations and the current
  first-meshlet-pass repair count/time/allocations before changing behavior.
- [x] Capture the current detailed Vulkan capability ladder in a diagnostic run,
  even if one or more fields require temporary local inspection.

Phase 0 exit gate:

- [ ] Baseline evidence names exact source assets, import settings, cache state,
  source commit, selected GPU, driver, and output paths.
- [x] Every known gap maps to a phase and an owner in this tracker.

## Phase 1 — Import Cook Contract And API Cleanup

- [x] Add an explicit model-import cook coordinator in the modeling bridge rather
  than embedding topology work in the generic AssetManager wrapper.
- [x] Make import-specific model cook defaults enable meshlets for eligible
  triangle meshes without changing unrelated procedural-mesh defaults.
- [x] Resolve effective LOD and meshlet settings per submesh, honoring authored
  `MeshOptimizerSubMeshSettings` overrides.
- [ ] Split or rename the current APIs so no caller can infer that
  `BuildMeshlets(..., lodSettings, ...)` generates LODs.
- [x] Define an explicit order: normalize topology, generate/reconcile LODs,
  then build meshlets per base/manual/generated LOD.
- [x] Define stable cache-local model, submesh, mesh, and LOD identities.
- [x] Define explicit generated-data states: `Present`, `Disabled`, `Empty`,
  `MissingRepairable`, `CorruptRepairable`, and `RepairFailed`.
- [x] Lock the v1 portable meshlet profile to shader-compatible limits or add a
  validated specialization contract before accepting non-default limits.
- [ ] Keep cold cooking off the render thread and support bounded import-worker
  parallelism without nondeterministic output ordering.
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

- [ ] Equivalent cold imports produce equivalent meshlet section bytes/hashes.
- [ ] Changing any real builder input or setting invalidates derived data.
- [ ] A runtime without meshoptimizer accepts a valid compatible baked payload.
- [ ] Malformed payloads fail bounded validation before reaching GPUScene.

## Phase 4 — Shared Meshlet Sections And Model-Cache Integration

Coordinate every checked item with Phases 4–5 of the model import binary cache
tracker. This phase owns the meshlet slice, not unrelated prefab hydration.

- [ ] Extract reusable mesh core and meshlet section codecs from the monolithic
  standalone `XRMesh` implementation without duplicating wire-format authority.
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
- [ ] Update the model import binary cache tracker as each shared item lands.

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
- [ ] Reimport, unload, streaming, and payload replacement reclaim or reuse the
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

- [ ] A mixed static/skinned/masked/override scene renders every draw exactly once.
- [ ] Eligible opaque bins use mesh shaders while planned unsupported bins remain
  on traditional GPU indirect with zero readback.
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

- [ ] Validate the existing opaque-deferred static material-table path first.
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
- [ ] Confirm cached cold-cook descriptors produce the same GPUScene bytes and
  visible result as the pre-cache reference generation path.

Phase 8 exit gate:

- [x] Every required pass/material/deformation/view case either has a correct
  meshlet path or a documented explicit traditional GPU route.
- [ ] Three camera positions show no missing, duplicated, stale, or falsely culled
  geometry during LOD transition, animation, reimport, or streaming.

## Phase 9 — Live Validation, RenderDoc, Performance, And Closeout

Complete live/runtime validation before requesting clearance for new test work.

- [ ] Reserve one bounded `Build/_AgentValidation/<run>/` root and record exact
  commands, settings, source commit, cache state, GPU, driver, and log paths.
- [ ] Run cold import, second standalone mesh load, first render, and steady-state
  captures with builder/parser/hash/I/O/upload/allocation counters.
- [ ] Run valid warm, disabled, empty, changed settings, changed source, changed
  cooker provenance, corrupt optional section, read-only repair, and runtime-
  without-cooker scenarios.
- [ ] Run static, skinned, morph, mixed-pass, LOD transition, stereo/multiview,
  reimport, hot reload, streaming, unload/reload, and capacity-overflow scenarios.
- [x] Run `rdc doctor` before Vulkan capture.
- [ ] Capture a real meshlet frame into the run root's `renderdoc/` directory.
- [ ] Follow an open-work-close RenderDoc session; inspect `info`, passes, bounded
  draw/event lists, pipeline state, task/mesh shaders, and bindings, export the
  relevant render targets, visually inspect the PNGs, and run `rdc close`.
- [ ] Prove the captured event uses `vkCmdDrawMeshTasksIndirectCountEXT`, the
  expected task/mesh stages, GPU-written indirect/count buffers, resident meshlet
  streams, material table, transforms, and correct attachment state.
- [ ] Compare final framebuffer and suspicious intermediate outputs against the
  traditional zero-readback reference from at least three camera positions.
- [ ] Run steady-state zero-readback, render-hot-path allocation, buffer-residency,
  churn/compaction, and dense-scene performance measurements.
- [ ] Record current third-laptop results and later matched original-laptop/
  desktop evidence without combining unmatched runs.
- [ ] After live validation succeeds, ask for explicit user clearance before
  adding or running new integration/regression tests.
- [ ] After clearance, add targeted deterministic cache, validation, routing,
  lifetime, Vulkan capability, and parity coverage under `XREngine.UnitTests/`.
- [ ] Update the model-cache tracker, historical meshlet tracker, production
  rendering roadmap, mesh-submission architecture, and resident-stream tracker
  with final status and evidence links.

## Validation Matrix

| Scenario | Cold/import expectation | Warm/runtime expectation |
| --- | --- | --- |
| Static mesh with generated LODs | LODs then meshlets build once; all payloads persist. | Zero parser/builder calls; payloads upload before eligibility. |
| Meshlets explicitly disabled | Persist `Disabled`; do not call builder. | Remain traditional GPU; do not infer repair. |
| Empty/non-triangle mesh | Persist `Empty` or ineligible state. | No build loop; no missing-geometry side effect. |
| Cook setting or real source-input change | Deterministically invalidate and recook. | Never accept stale descriptors. |
| Cooker version change | Invalidate at asset/cache boundary. | Rendering accepts an already validated compatible payload. |
| Runtime without meshoptimizer | Not a cold-cook configuration. | Valid baked payload works; no DLL load is required. |
| Corrupt optional meshlet section | Repair from cached core when policy allows. | Never open source parser merely for optional repair. |
| Skinned/morph mesh | Persist topology payload and deformation metadata/policy. | Correct meshlet deformation or explicit traditional GPU bin. |
| Mixed opaque/masked/override/transparent scene | Cook eligible geometry once. | Every draw appears once through an explicit meshlet/traditional bin. |
| Reimport/hot reload/streaming | Publish new revision atomically. | Frame-boundary swap; old GPU ranges reclaimed safely. |
| Third-laptop Vulkan static opaque | No runtime cooking dependency. | Strategy resolves to meshlet and RenderDoc proves EXT dispatch. |

## Resident Draw-Stream Resume Gate

Do not start Phase 1 of the Vulkan resident draw-stream tracker until all of the
following runtime gates are satisfied:

- [x] Imported base and LOD meshlets are generated before serialization and a
  second standalone load performs zero builder calls.
- [x] Rendering performs no meshlet cooking, source hashing, disk access, or
  cache publication.
- [ ] Missing and unsupported meshlet draws remain visible through explicit
  traditional zero-readback bins; no selected meshlet pass silently drops work.
- [x] The third-laptop static opaque fixture resolves to real
  `GpuMeshletZeroReadback` and maintains the zero-readback/no-CPU-fallback
  contract.
- [ ] RenderDoc proves EXT task/mesh stages and indirect-count submission with
  correct resident inputs and visual output.
- [x] Reimport, unload, streaming, and payload replacement have a bounded,
  generation-safe GPUScene range lifetime.
- [x] Capability and eligibility diagnostics retain one exact primary reason for
  every downgrade or ineligible bin.
- [ ] Durable evidence is linked here and from the resident draw-stream tracker.

After this gate passes, update the resident tracker status from paused to active
and continue with its Phase 1 central execution topology work.
