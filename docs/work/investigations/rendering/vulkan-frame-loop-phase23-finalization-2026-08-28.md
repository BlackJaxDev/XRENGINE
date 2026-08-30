# Vulkan Core Frame Loop Phase 2/3 Finalization - 2026-08-28

Related tracker: [Vulkan Core Frame Loop, Resident Rendering, and High-Refresh Master TODO](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md)

## Problem Statement

Finish the implementation boundaries of master phases 2 and 3 without treating
contract scaffolding, a clean build, or placeholder advanced stages as live
production acceptance.

## Starting Boundary

- Phase 2 already owned tracked submission telemetry, reusable sealed contracts,
  compact resource generations, sampled full-path parity, and batched lifetime
  pins. Its stable image-state checks still consulted dictionary-owned
  subresource state, and reverse invalidation did not yet cover the production
  advanced consumer graph.
- Phase 3 already owned retained canonical scene/material/resource publications,
  exact set-2/set-3 Vulkan descriptors, direct-slot resident templates, and
  completion-owned native leases.
- `VPRC_AdvancedRenderStage` executed only `FrameBegin`; every production stage
  remained a no-op and Vulkan correctly advertised
  `EAdvancedShaderFamily.None`.
- No stable numeric bin implementation or live canonical diagnostic sidecar was
  present.

## Implementation In Progress

1. Publish exact retained draw, instance, geometry, transform, deformation,
   render-state, and editor-identity SoA images and handle-lookup segments into
   the existing frame-slot `AdvancedSceneStorage` lane. Bind these slices at the
   canonical set-3 ABI rather than the prior valid-zero fallback table.
2. Add exact-variant stable bins with topology-only intrusive membership,
   immutable template/bin native resource manifests, ordered exceptions, and
   sealed five-strategy plans.
3. Replace sealed stable-hit image dictionary scans with ABA-safe flat image
   subresource slots while retaining dictionary validation on cold/dirty/full
   paths.
4. Add a live instrumented-only diagnostic sidecar with fixed staging storage,
   submission-completion receipts, nonblocking retirement polling, and
   general-domain decode. Zero-readback and disabled paths must create no work.
5. Require the real set-1 visibility closure in advanced program ABI validation.
   Do not promote the shader family until production stage execution exists.

## Implemented During This Investigation

- Sealed submission image entry/exit validation now uses ABA-safe flat
  subresource slots on the stable path. Dictionary-owned image state remains
  confined to recording, cold sealing, and sampled full validation.
- The backend-native reverse graph now owns independent slot/generation and
  topology/content domains for pipeline layouts, pipelines, descriptor layouts,
  descriptor tables, render passes, and shaders. Exact dirty records replace
  broad invalidation for those connected domains.
- Resident templates now publish exact-variant stable-bin membership. Hole
  lookup, transactional replacement, and eviction remove the exact member;
  queue-family and image-layout manifest conflicts reject with typed reasons.
- A post-coalescing immutable bin stream intersects current visibility with
  resident membership, carries exact view/order keys and late resource uses,
  and preserves excluded work as typed ordered exceptions. Strategy resolution
  is frozen before sealing and strict GPU requests no longer silently become
  `CpuDirect`.
- Diagnostic readback plans are immutable allocation-free nodes stored in the
  reusable backend package. Instrumented Vulkan work uses a fixed host-visible
  ring, producer-complete copies, fence-status polling during retirement, and
  general-domain decoding. Zero-readback lanes create no sidecar resource,
  copy, map, wait, or decode work.
- The advanced stage command now reaches Vulkan through a renderer-neutral
  capability, a sealed `AdvancedVisibility` frame operation, logical resource
  declarations, and late frame-slot set-1 admission. The operation remains
  fail-closed until its real compute/raster native pipeline closure is complete.
- Canonical set 3 now uploads exact draw, instance, geometry, transform,
  deformation, render-state, and editor-identity record images and lookup
  segments instead of binding the generic zero table for those domains.
- Set 1 now owns a separately allocated 16 MiB persistent visibility-state
  buffer for the logical-device generation. Frame-slot candidates, payloads,
  ranges, counters, and indirect arguments remain arena-owned; descriptor
  binding 21 no longer aliases a frame-local slice that is cleared every frame.
- Prepared secondary recording now snapshots the exact backend-ready package's
  canonical frame, scene identity, views, passes, and diagnostic requests into
  reusable prepared-frame arrays before freeze, rejecting a frame-plan/package
  identity mismatch.
- Stable-bin lowering now produces a typed native submission with exact set-1
  argument/count offsets. The family recorder owns one batched
  producer-to-indirect barrier and `vkCmdDrawIndexedIndirectCount` consumes the
  exact range capacity only for the sealed
  `GpuIndirectZeroReadback` lane; unsupported or unavailable lanes fail closed.
  Its production caller remains gated on the linked compute/raster pipeline
  closure rather than recording partial work.
- The indirect producer ABI now allocates indexed and mesh argument arrays by
  payload capacity rather than range count, publishes the exact payload-to-range
  and range-prefix tables, and lowers each draw-count call from the planner's
  immutable `AdvancedIndirectRange`. Multi-command ranges can no longer overlap
  or address undersized storage.
- The advanced request carries the exact four-attachment framebuffer and
  immutable frame view set. Late preparation captures dynamic-rendering or
  legacy-render-pass compatibility plus the native framebuffer generation;
  compute-only preparation declares no attachment writes and raster alone owns
  the target uses.
- World light identities and numeric directional/point/spot records are captured
  at the renderer boundary and applied in the same canonical publication
  transaction as draw/material/resource changes. Exact source-reference updates
  and tombstones preserve stable light ordinals without a late mutable recapture.
- `VisibilityPreparation` records `EarlyVisibility`, one shader-storage barrier,
  then `BuildVisibilityIndirect`, binding exact sets 1, 2, and 3 with bounded
  dispatch sizing and no CPU count. The baseline compute shaders intentionally
  own no set 0: they self-size from the bound storage arrays and conservatively
  publish candidates. Previous-depth refinement remains a separate later stage
  rather than an unbound sampler dependency.
- The visibility pipeline runtime now owns distinct opaque and masked
  vertex/fragment programs built with the same advanced ABI preamble; transparent
  and refractive coverage reject before linking. Exact resident vertex-input and
  target-compatible graphics-pipeline preparation are the remaining raster
  integration boundary.
- Visibility raster sealing is now canonical-atlas-only even when ordinary mesh
  ingress is nonempty. Every sealed record retains the exact atlas tier/version,
  logical buffers, native handles/generations, index format, and mesh range and
  revalidates that immutable closure immediately before tracked native binds.
  Skinned/deformed records reject until a matching deformation-arena geometry
  closure exists instead of pairing deformed offsets with static atlas buffers.
- Canonical atlas native state and resource manifests use fixed inline/preallocated
  storage. Header pipeline preparation no longer rescans the whole stream, payload
  range lookup is direct, and sealed plans share a fixed exception snapshot; the
  former per-draw arrays and per-plan `ToArray()` allocation are gone.
- Canonical set 3 now also uploads exact Lights, Shadows, Probes, Environments,
  Decals, and GI records. Frame-slot view rows and contiguous frame/pass metadata
  replace the final global fallback bindings, while set 1 retains the concrete
  visibility and indexed/mesh indirect streams.
- Native dependency invalidation is transitive and resident variants link to
  their concrete pipelines. Broad migration telemetry now records an exact reason,
  owner, slot, generation, and version domain rather than an unqualified count.
- Output sealing derives the exact target view mask and shadow/OpenXR/mirror/
  capture policy from the accepted `RenderOutputRequest`; stereo desktop output
  is no longer mislabeled as OpenXR.
- `BuildVisibilityIndirect.comp` now checks each bin's sealed prefix range, not
  only the aggregate argument-buffer capacity. A defective producer therefore
  cannot spill arguments into the following bin; indexed and mesh outputs use
  their respective native capacities and retain asynchronous overflow counting.
- Early visibility and per-range indirect reservations now use saturating atomic
  compare/exchange loops. Published counts cannot exceed their writable slices,
  invalid or zero-meshlet payloads are rejected before reserving an argument,
  and overflow remains a sticky asynchronous counter rather than corrupting the
  next range or provoking a same-frame retry.
- Stable-bin plans now retain both the requested family strategy and the exact
  range-local execution strategy derived from
  `AdvancedIndirectRange.Key.Producer`. A meshlet-family indexed fallback owns
  an indexed plan and indexed diagnostic decoder; lowering rechecks the producer
  against the immutable execution lane. Actual meshlet ranges still reject until
  canonical GPU geometry residency and the mesh visibility pipeline are sealed.
- The sealed stable-hit contract now captures secondary command-buffer lifetime
  records and render-target resource slots at the recording boundary. Stable
  validation resolves those ABA-safe flat slots directly instead of returning to
  command-buffer or render-target resource dictionaries.
- Desktop swapchain output and its primary/dynamic-UI/ImGui command artifacts
  now publish exact native dependency identities and `Output -> CommandArtifact`
  edges, with fail-fast recreate-before-retirement and ordered artifact/output
  retirement.
- The CPU direct/indirect parity artifact is preallocated and diagnostic-only. It
  compares template identity, indexed arguments, material/object identity, and
  order without participating in production strategy selection or recording.
- Strict readback accounting now records mapped GPU-buffer operations and exact
  mapped byte totals from the instrumented sidecar, so a zero-readback lane can
  prove both counters remain zero instead of relying only on the absence of a
  queued diagnostic node.
- Vulkan normal recording no longer consumes `PreparedGpuScene` or a live
  renderer visibility snapshot. The retained canonical package, exact scene
  publication, stable-bin stream, and immutable frame operation payloads are
  now the only advanced Vulkan input.
- Set 1, set 2, and set 3 now carry the complete mono view ABI. View-indexed
  counters, payload/range segments, depth-pyramid descriptors, and persistent
  visibility history use bounded preallocated storage; the family rejects
  stereo until layer-specific raster scopes or a true multiview indirect ABI
  is implemented.
- Late visibility now owns per-operation/per-view/per-mip immutable descriptor
  families. Same-generation retries are idempotent only for an identical image
  closure; a differing closure rejects rather than overwriting a descriptor set
  captured by a command buffer.
- Late depth/pyramid capture validates distinct images, exact depth and R32F
  formats, matching extents/layers, and bounded view counts. Interner
  acquisitions are balanced on success and every partial failure, and closure
  arrays/descriptors are frame-operation-owned rather than allocated during
  preparation.
- The preparation/raster/late trio now seals one exact immutable set-1 family.
  Raster-local native arguments are materialized before the sole upload; later
  stages associate the same state without clearing or rewriting it. Equal
  counts are not an identity proof: reuse also requires the exact frame-plan,
  extractor, preparation/indirect publication, scene native generation, lookup
  segments, geometry slices, and view count.
- Visibility allocations now preflight every individually aligned range and
  restore the reserved-lane cursor on allocation, initialization, or descriptor
  failure. Same-generation reuse is read-only and cannot reset GPU-produced
  counters or outputs before recording.
- Persistent visibility history is indexed by dense candidate within each view
  segment, so sparse stable draw handles cannot overlap eye histories. Loaded
  draw identity is compared before replacement. The provisional center-sample
  Hi-Z test is disabled because it was not conservative; late recovery remains
  correct while conservative projected-rectangle occlusion is still pending.

## Review Corrections

- Stable-bin membership keyed only by a canonical draw slot is invalid because
  one draw may own multiple resident variants. Membership and eviction must use
  the exact resident template slot/generation.
- Topology replacement must preflight capacity and linkability before unlinking
  the old member.
- A bin manifest containing only logical canonical dependencies cannot replace
  `FrameOpResourceUseList`; native access, layout, queue-family, and lifetime
  declarations are also required.
- Diagnostic contract objects without construction, staging buffers, producer
  copies, completion receipts, and decode scheduling do not complete Phase 3.6.
- Per-frame `ToArray()`/node-array construction is forbidden on this hot path.
- A frame-slot resident image may skip several canonical publications. Applying
  only the newest publication's delta range is incorrect even when table shape
  matches; resident reuse must carry exact source identity and a cumulative
  bounded delta chain (or use full copy-on-write when that proof is absent).
- Rendering each eye repeatedly inside one multiview scope broadcasts both
  draws to both layers unless the shader/indirect ABI is based on
  `gl_ViewIndex`. Stereo capability therefore remains disabled rather than
  advertising the partially wired array resources.
- Descriptor-set updates are lifetime publication, not merely Vulkan API calls.
  Set-1 and late descriptor batches now use the shared descriptor lifetime
  authority so recorded work retains exact buffers and image views after local
  interner acquisitions are released.
- Resident images are not canonical publication pins. Delta reuse now requires
  the same database epoch, exact applied sequence, and retained-journal floor;
  otherwise the destination is initialized from the complete snapshot.
- Publication sequence advance alone is not a dirty owner. Retained table owner
  generations allow unchanged topology/content owners to retain their native
  frame-slot image and stamp the newer sequence without COW. Logical lookups use
  twelve fixed resident owner segments and patch only changed lookup generations.
  The mapped Vulkan slices are now the sole data authority; there is no CPU mirror
  capacity that can be mistaken for native capacity. Table and byte predicates
  prove required length against the retained slice (including element-size
  divisibility), and larger current images trigger the packed rebuild path.
- Cursor restore failure means allocator state is unknown. Both advanced resource
  runtimes now quarantine that frame slot and return `TransactionIntegrityFailure`
  rather than clearing resident metadata and misreporting an ordinary capacity/native error.
- A preparation publication's structural generation does not freeze its mutable
  view masks/plans. Visibility content owns a separate monotonic generation,
  which is captured by the request/family seal and checked before and after the
  set-1 copy.
- Late raster cannot reuse a pipeline keyed by another legacy render-pass handle.
  The baseline now requires one exact dynamic-rendering target closure for both
  raster stages and rejects the family before command-buffer recording.
- Device support alone cannot advertise a one-family realization when capability
  selection may independently choose it for multiple mono outputs. Global
  `VisibilityBuffer` promotion remains disabled until output-family cardinality
  is represented or multiple bounded families are implemented.
- Ordinary scheduled mesh secondaries must not depend on an advanced publication
  while that family is not production-promoted. Requiring it caused the first
  PresentNow frame to pause when the initial backend package legitimately carried
  no canonical scene image; the publication retain/association path is now entered
  only for a promoted plan containing advanced visibility operations.
- A fixed-capacity stream's current entry span is not its capacity. Stable-bin
  acceptance compared incoming ordered exceptions with the cleared destination
  count, so any ordered exception produced a contradictory 0-of-4096 overflow.
  Copy admission now uses the fixed exception capacity and reports all three
  bounded dimensions correctly.

## Validation Record

- `rdc doctor`: passed; RenderDoc replay, CLI, and Vulkan layer are ready.
- `dotnet build XREngine.Runtime.Rendering.csproj --no-restore --no-dependencies`:
  passed with 0 warnings and 0 errors after the first canonical/diagnostic slice.
- `dotnet build XREngine.Runtime.Rendering.Vulkan.csproj --no-restore --no-dependencies`:
  passed repeatedly with 0 warnings and 0 errors after the flat image index,
  native reverse graph, diagnostic sidecar, stable-bin corrections, complete
  set-1 binding declaration, and sealed advanced frame-operation integration.
- The renderer-neutral and Vulkan targeted builds also pass with 0 warnings and
  0 errors after global-light publication, exact target/view threading, indirect
  range correction, compute-family recording, and conservative set-0 removal.
- The targeted Vulkan Debug build continues to pass with 0 warnings and 0 errors
  after exact per-range execution strategies, saturating GPU reservations,
  output/artifact dependency publication, CPU parity scaffolding, and the final
  sealed secondary/render-target flat-slot removal.
- Final renderer-neutral Core/Rendering/Vulkan Debug builds pass with 0 warnings
  and 0 errors after descriptor-authority routing, database-epoch/journal-floor
  resident proof, visibility-content sealing, mono request admission, and the
  raster/late dynamic-closure preflight.
- The same targeted builds remain clean after unchanged-owner sequence stamping,
  fixed-segment resident lookup patching, mapped-slice capacity proof,
  rollback quarantine, canonical frame-field correction, and the two live-path
  fixes above.
- All six advanced shaders (`EarlyVisibility`, `BuildVisibilityIndirect`,
  `LateVisibility`, `BuildDepthPyramid`, indexed visibility raster, and mesh
  visibility raster) compile through the runtime-equivalent validation preamble.
- A direct Vulkan Release build with `--no-dependencies` is not a valid source
  check in the current workspace: its retained Release dependency binaries are
  older than the new renderer-neutral contracts and report missing types. A
  dependency rebuild reaches the unrelated shared-source blockers below, so no
  Release percentile claim is made from stale references.
- Full dependency builds are currently contaminated by ignored legacy files under
  `XREngine.Data/Core/Assets/Caching/` that duplicate the tracked
  `Core/Files/Caching/` cache authority/registry types. Those unrelated files are
  preserved and are not counted as Vulkan validation.
- A disposable isolated-editor build initially failed because the same unrelated
  shared work also lacks `FbxImportBackend`/`GltfImportBackend`, contains an
  ignored duplicate `RuntimeAssetBootstrap`, and has an ambiguous serialization
  registration. Validation-only compile inputs/exclusions under the task run root
  isolate those sources without modifying tracked code; the full isolated editor
  build then passes with 0 warnings and 0 errors.
- The final named session `phase23-final-0828` reached MCP readiness. Its initial
  attempts exposed the canonical-publication and exception-capacity defects above;
  after correction, PresentNow frames completed past frame 438 without Vulkan
  errors, renderer pause, frame rejection, transaction quarantine, or capacity
  failure. Profiler evidence reported two packages prepared/published/consumed,
  zero rejected packages, zero overflow, zero forbidden fallback, and zero
  submission managed bytes in the sampled interval.
- Camera-separated readbacks were visually inspected at `(0,6,18)` and `(12,6,0)`:
  `Screenshot_20260828_160246_377_94db37ebe09c4063b763ab36476b601e.png`
  and `Screenshot_20260828_160308_521_83ff7763c88f470ba2acadf30870f6b1.png`
  show different textured Sponza geometry. This closes the ordinary Vulkan smoke;
  it does not claim execution of the deliberately unpromoted advanced family.
- After the final native-capacity proof correction, the rebuilt named session
  completed beyond frame 337 with the same zero-error/zero-rejection log filter.
- Final resident closeout removed the managed comparison mirrors entirely. The
  retained mapped Vulkan slice is now the data authority; table ABI sizing uses
  `Unsafe.SizeOf<T>()`, empty owners retain sentinel storage, and all writes map
  exact row/range/segment/tail sub-slices.
- Resident allocation is now ordered as one owner/lookup prefix followed by
  fallback, view, frame/pass, and encoded-reference transients. A completed-slot
  publication either retains the entire prefix or rebuilds every owner from the
  immutable canonical snapshot. Epoch/journal gaps with sufficient native
  capacity use a full in-place owner rewrite rather than unsafe deltas or COW.
  The preflight charges an unaligned retained tail and rejects any mismatch
  between its planned aligned end and the mapped arena's aligned final cursor.
- The final rebuilt `phase23-final-0828` editor passed its isolated build with
  zero warnings/errors and ran through publication 3629. The sampled profiler
  reported 3 prepared packages, 2 published/consumed packages, 0 rejected
  packages, 0 forbidden CPU fallback, 0 submission managed bytes, 0 resident
  capacity/dependency rejection, and 0 Vulkan validation messages. The final
  log filter found no transaction/storage-capacity failure, quarantine,
  frame-plan capacity failure, renderer pause, VUID, device loss, OOM, or
  exception. The inspected readbacks were:
  `Screenshot_20260828_162600_261_23a34932ee7a42c2839286b6f8d1d33a.png`
  and
  `Screenshot_20260828_162623_410_2e58dac37a97433385fa0e5c6968c83d.png`.

## Wrap-Up Boundary And Remaining Issues

The implementation work in this slice is wrapped and the master TODO boxes now
distinguish implemented substrate from unproven promotion work. The remaining
known issues are explicit rather than hidden behind the clean ordinary-frame
smoke:

1. Retain-all selection can still reject a publication that would fit after an
   exact compact rebuild when historical resident/lookup capacities are much
   larger than the current snapshot. Preflight must compare both footprints and
   choose compact rebuild before mutating the arena.
2. `VulkanFrameDataDirtyRanges` retains eight disjoint ranges. A ninth exact
   write conservatively collapses to one broad range, so memory safety holds but
   proportional dirty-flush promotion still needs a larger bounded store or
   explicit collapse telemetry plus a zero-collapse gate.
3. Native table replay should skip journal deltas already covered by
   `AppliedPublicationSequence` before writing, matching the metadata commit
   filter and avoiding repeated dirty rows for slow retained journals.
4. Async texture loading still leaves a stale canonical texture record in the
   dual-feed path: `sponza_thorn_diff` was revalidated at 256x256/nine mips while
   the retained row remained 64x64/one mip. Vulkan correctly selected the ordered
   legacy draw and reported `SourceMismatch`; resource republishing/parity must
   close this before canonical production cutover.
5. Output-aware family reservation, stereo/multiview ABI, five-lane rendered
   parity, mesh-task production completion, strict zero-readback/diagnostic
   saturation evidence, exact mutation/zero-broad-fallback evidence, Release
   percentile gates, and hardware/OpenXR coverage remain open in the master TODO.

## Live Acceptance Required

The named isolated Unit Testing World Vulkan/MCP smoke is complete. Because
production capability promotion is deliberately fail-closed until output-family
cardinality is represented, this smoke validates the ordinary Vulkan frame loop
but does not claim advanced-stage execution, five-lane stable-bin parity, or
strict zero-readback promotion. Those remain explicit gates rather than being
inferred from a clean ordinary frame.

## Promoted-Family Continuation

The final continuation promoted one output-aware mono visibility family far
enough to record and submit its real physical backend operations. It also closed
the four implementation defects from the prior wrap boundary:

- Resident preflight compares retain and exact compact footprints, selects a
  cold compact rebuild before mutation when only that footprint fits, and
  compacts lookup segment capacities with the table owners.
- Dirty publication owns 32 exact ranges and publishes typed collapse telemetry;
  mapped replay ignores deltas already covered by the resident generation.
- Async texture streaming increments a content generation after the complete
  source/mip commit. Canonical encoding retries torn reads and performs an
  ABA-safe row update rather than retaining the import placeholder metadata.
- Mono family reservation includes the exact output identity and arena
  generation. Conflicting outputs and stereo remain fail-closed instead of
  sharing one mutable family realization.

Late visibility is now physically split into `LateCompute` and `LateRaster`.
The compute half builds the depth pyramid and late-cull indirect stream; the
raster half has a graphics rendering scope and consumes that stream. Enqueued
requests capture the exact published backend package, canonical frame, and scene
publication. Preparation and secondaries validate that snapshot without
equating collection-frame IDs with render-frame IDs.

The first promoted-family live attempts exposed and fixed, in order:

1. a backend-package identity check spanning different frame-ID domains;
2. command-index-aligned invalid draw holes being treated as missing canonical
   geometry;
3. late resource lookup applying the render-graph texture namespace twice;
4. compute-only push-constant flags against a common graphics/compute range;
5. unsubmitted recording attempts mutating global image-layout authority;
6. combined depth/stencil images transitioning only the depth aspect; and
7. late raster being classified as a compute operation without a rendering
   scope.

The rebuilt named session `phase23-promoted-20260828` reached MCP readiness and
submitted the promoted nine-operation family, including four compute operations.
Sampled frame-tree records completed with zero VUIDs, Vulkan error records,
desktop frame failures, source mismatches, transaction-integrity failures,
quarantines, or frame-plan-capacity failures. The device-lost text in the log is
the startup device-fault capability report (`requested=False`), not a device-loss
event.

This scene's advanced graph currently has no terminal shaded-output producer.
It therefore deliberately publishes the deterministic red/transparent
`EmptyPresentNowClear` diagnostic after the promoted phase work. MCP ping and
tool discovery remained responsive, but `set_editor_camera_view` and
`capture_viewport_screenshot` timed out on editor dispatch; no camera-separated
image is claimed for this session. The live evidence closes native phase
recording, synchronization, lifetime, and validation correctness only. Rendered
five-lane parity, mesh-task production, strict zero-readback evidence, stereo,
and hardware/OpenXR promotion remain open.

## 2026-08-29 Phase 2/3 Source Closeout

The remaining Phase 2 and Phase 3 implementation rows are now closed. The
master TODO deliberately retains promotion-only measurements and matrices in
Phase 8 rather than treating source completion as proof of performance or
shaded-output parity.

### Final implementation

- `AdvancedDrawSubmissionRecord` and its retained publication snapshot now own
  normal-frame Vulkan membership, pass, order, instance, material, geometry,
  stable-query, and compatibility identity. `BackendReadyMeshSelection` was
  deleted, and Vulkan no longer reconstructs or consults mutable legacy
  selection arrays.
- Canonical packages consume exact publication-owned mutation ranges rather
  than live scene ranges. Publications also retain compact material/geometry/
  texture/kernel/layout reverse manifests, while the Vulkan resident template
  table and native dependency graph execute exact transitive invalidation for
  material/resource and pipeline/layout/descriptor/shader/output artifacts.
- Global shadow/probe state now has one immutable coverage record per canonical
  pass. Package preparation derives the exact used-owner generations from the
  retained submission rows, mixes only used owners into the pass dependency
  signature, and copies the coverage vector into prepared frame-slot storage.
  Native realization rejects count, pass-index, sequence, generation, dirty-
  range, or submission-use disagreement as
  `DependencyManifestInconsistent`.
- Broad resident invalidation remains an explicit migration-only correctness
  fallback. Telemetry now retains its exact reason, owner, mutation domain,
  affected-entry count, and publication sequence; MCP exposes the typed last
  fallback alongside the aggregate counters.
- The sealed submission path retains one flat batch receipt containing direct
  command-lifetime and tracking-batch references. Stable submission validates
  ABA-safe resource slots and image-state versions without repeating normal
  subresource dictionary or queue-ownership discovery, and publishes/releases
  the exact retained records through the existing submission-state lock.
- Meshlet diagnostic requests now identify their purpose explicitly. A strict
  zero-readback evidence copy can be scheduled without being classified as the
  generic instrumented readback path, while instrumented lanes retain their
  bounded asynchronous sidecar.
- Startup applies the requested strategy before first frame publication and
  synchronously prepares framebuffer backing with typed retry/terminal failure,
  removing the earlier first-frame readiness race.

### Build and runtime evidence

- The targeted Release editor build passed with zero warnings and zero errors:
  `dotnet build XREngine.Editor/XREngine.Editor.csproj -c Release --no-restore`
  with the task's validation-only shared-source exclusions.
- The five-lane report is under
  `Build/_AgentValidation/20260829-014400-phase23-closeout/reports/phase3-five-lane-closeout/`.
  All five requested strategies resolved exactly, shared workload hash
  `12941640762020391990`, and recorded zero fallback events and zero VUIDs.
  `GpuIndirectZeroReadback` requested/consumed 2403 draws per sample with zero
  generic readback bytes and maps. `GpuMeshletZeroReadback` requested/consumed
  the same 2403 draws, emitted 292 task records across two produced frame
  operations, and also reported zero generic readback bytes and maps.
  `GpuMeshletInstrumented` emitted 256 task records, 36,352 delayed dispatch
  groups, 2,288 diagnostic bytes, and one produced diagnostic frame operation.
- The corrected strict meshlet report is under `reports/meshlet-evidence-fixed/`.
  It emitted 146 task records, produced two requested frames and two frame
  operations, kept generic readback/maps/fallback/VUID counts at zero, and
  performed no render-path source hashing, disk access, or cooking. The one
  procedural cold-builder call is setup evidence and is not an imported-mesh
  warm-cache result.
- The post-coverage smoke is under `reports/phase23-coverage-final/`. It resolved
  `GpuIndirectZeroReadback`, requested/consumed 2403 draws per sample, and
  reported zero readback bytes, maps, fallback events, submission rejections,
  global fallback invalidations, and VUIDs. Log filtering found no dependency-
  manifest, global-pass-coverage, or advanced-publication rejection. The MCP
  snapshot reported zero resident broad invalidations and an empty typed last
  broad-fallback record.
- The final sealed-path report is under `reports/phase2-sealed-lock-reuse/`.
  It recorded 79 sealed hits, 36 `MissingContract` cold fallbacks, zero
  `ResourceVector` fallbacks, and zero resident broad fallback. The sealed-hit
  gateway histogram reported 0.4096 ms p50, p95 in the 0.8192–1.6384 ms bucket,
  and 6.5536 ms p99. This does not meet the `<0.25 ms` p95 promotion target, so
  that target remains unchecked in Phase 8.
- `rdc doctor` passed. The attempted RenderDoc launch did not produce an `.rdc`
  capture, so no RenderDoc evidence is claimed.
- At this Phase 2/3 checkpoint no automated tests had yet been added, modified,
  or run. The later Phase 4.4 closeout below records the post-live-validation
  regression run requested by the user.

### Remaining promotion work

Phase 2 and Phase 3 now have no unchecked implementation rows. Phase 8 retains
the unmet sealed-hit percentile, rendered five-strategy output parity, exact
local-mutation/zero-broad-fallback matrix, diagnostic saturation, cross-vendor
descriptor-tier comparison, hardware/OpenXR coverage, and full validation-mode
gates. The current advanced graph's explicit empty-output clear still prevents
an honest shaded-output parity claim.

## 2026-08-29 Phase 4.4 Hot-Path and Liveness Closeout

### What the red frame means

The active `AdvancedRenderPipeline` published a valid canonical scene package
(61 resident/canonical submissions in the final inspection), but all 13 active
pass definitions reported zero authored render commands. The solid red frame is
therefore the graph's deliberate empty-output diagnostic, not evidence that the
scene publication disappeared. Producing the shaded scene remains a Phase 8
output-parity task.

The debug overlay freeze was a separate defect. During the full-model startup
transition, an exact canonical texture descriptor could remain retryable after
the desktop image was acquired. The old `PresentNow` rejection policy neither
submitted nor presented that frame, which also stranded the pending upload
needed to make the descriptor ready. Subsequent frames repeated the same retry
and the otherwise valid UI snapshot never reached the screen.

Retryable, device-healthy acquired frames now admit a fresh initialization
clear plus the current ImGui/dynamic-text overlay and any pending texture upload.
This is not stale scene replay. Permanent terminal failures retain the strict
no-present policy. The same run moved from the expected finite startup retry
sequence back to completed native submit/present and continued advancing the
overlay.

### Runtime evidence

The named Release session was `phase44-hotpath`, rooted at
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260829-141051-phase44-hotpath/`.
After the transition, snapshots at frame-loop invocations 30,693 and 39,439
showed:

- submission managed bytes unchanged at 22,904;
- present managed bytes unchanged at 9,528;
- zero build/dispatch/execute/merge managed bytes;
- zero unexplained scheduler wakes;
- zero scheduler queue, Vulkan lifetime, or image-layout lock waits above
  0.1 ms;
- accepted native submission/presentation, an operational device, and zero
  Vulkan validation errors.

The two samples span 8,746 steady-state invocations. Cold startup and retirement
allocations before the measured interval are intentionally not claimed as zero.
The session stopped cleanly. Log filtering found no VUID, validation error,
device loss, unhandled exception, fatal error, or ordinary error record.
Vulkan viewport capture still lacks a transfer-readable live color image, so no
MCP screenshot is claimed; the editor window was inspected directly and showed
the expected red diagnostic with a changing FPS overlay.

### Regression evidence

- The Release editor build completed with zero warnings and zero errors.
- The Debug unit-test project completed with zero warnings and zero errors.
- The focused Phase 3/4 Vulkan regression filter passed all 110 tests. Existing
  source-contract tests were updated after live validation to follow the current
  canonical draw-ID buffers, render-domain lane workers, telemetry ownership,
  and split ImGui overlay recorder; no new tests were introduced.
- A second directly affected filter passed all 66 advanced-pipeline, geometry,
  visibility, backend-package, and lane-arena contract tests.

## 2026-08-29 Pipeline Source Authority Correction

The render-path overlay exposed a split source of truth: the camera inspector
reported its configured `DefaultRenderPipeline`, while the post-window
`ApplyRenderPipelinePreference()` pass had replaced only the live viewport with
an `AdvancedRenderPipeline`. A later camera synchronization could therefore
silently restore the legacy pipeline.

The camera source is now authoritative. Assigning a pipeline to a
camera-synchronized viewport updates the camera asset and synchronizes every
bound viewport, and new desktop cameras use `AdvancedRenderPipeline` as their
source under the default `Available` policy. The Vulkan visibility reservation
was moved off the shareable pipeline asset and onto each physical
`XRRenderPipelineInstance`; binding uses the viewport window's renderer rather
than the process-global current renderer. `OverrideProtected` prevents source
replacement but no longer suppresses required output binding. An unavailable
additional output is left explicitly unbound instead of downgrading a shared
camera source.

### Validation

- The final Release editor build completed with zero warnings and zero errors.
- The named isolated Release session was
  `pipeline-source-authority-final-0829`, rooted at
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260829-184734-pipeline-source-authority-final-0829/`.
- `list_ui_viewport_diagnostics` reported both the configured camera source and
  live viewport as `XREngine.Rendering.AdvancedRenderPipeline`, with
  `configuredSourceMatchesLiveViewport=true`. The desktop output retained ID 9
  and a `Bound` advanced binding with reservation 1 on Vulkan generation 1.
- A later profiler snapshot still named the rendered desktop pipeline
  `AdvancedRenderPipeline`, resolved `CpuDirect` on Vulkan, and reported zero
  Vulkan validation errors. A finite earlier texture-publication retry was
  retained in diagnostics, but the sampled frame completed and presented; it
  did not alter source identity or output binding.
- Viewport capture failed explicitly because the advanced diagnostic path does
  not expose a transfer-readable live color image. No CPU or OS-window fallback
  was used, so no screenshot is claimed. This remains part of the Phase 8 shaded-
  output/capture work rather than a source-authority failure.
- The named session stopped through the session manager. No automated tests
  were added, modified, or run for this correction because live feature
  validation precedes test clearance under repository policy.
