# Vulkan Frame Loop Phase 3 Material Publication (2026-08-27)

## Objective

Continue the master frame-loop Phase 3.1 work by closing the retained material
publication boundary that Phase 2 reverse dependencies and later Vulkan SoA
lowering require. Preserve the existing generation-safe publication ring and do
not expose ring-owned spans beyond a valid publication lease.

## Findings

- `AdvancedMaterialDatabase` already owned fixed-slot constant words,
  texture/sampler references, material-to-layout handles, and an immutable
  `AdvancedMaterialPublicationSnapshot` capture primitive.
- `AdvancedGpuScenePublicationSnapshot` retained material, layout, kernel,
  texture, and sampler record-table snapshots, but omitted that material payload
  image. A retained scene generation therefore did not freeze the bytes needed
  to interpret its material rows.
- `BackendReadyFramePackage.PrepareCanonicalFromScene` holds a package lease only
  while copying projections and releases it before Vulkan consumption. Directly
  exposing ring-owned material spans from that package would permit the ring slot
  to be reclaimed and reused before a Vulkan GPU pin protects it.
- `AdvancedGpuMaterialPublisher` exists as the intended shared-material owner,
  but production scene registration still bypasses it and creates one shallow
  material header per draw. The draft publisher also used a linear handle scan
  for update/release and rebuilt its full hash table after the final release.

## Implemented

- Added one preallocated `AdvancedMaterialPublicationSnapshot` to every retained
  `AdvancedGpuScenePublicationSnapshot`.
- Sealed the packed material payload in the same transaction and sequence as the
  material/layout/kernel and texture/sampler tables. A capture failure or
  sequence mismatch rejects the complete publication before the ring tail is
  published.
- Preserved boundary growth behavior: recreating the scene publication snapshots
  also recreates payload storage at the new database capacities.
- Hardened `AdvancedGpuMaterialPublisher` with a preallocated generation-aware
  material-handle lookup, local open-addressing cluster repair on removal, and
  no release-time full-table clear/rebuild.
- Added exact publisher/material-table capacity rejection for new variants and
  made layout/coverage/render-state identity immutable during `TryUpdate`.
- Added database-owned `TryAddMaterialWithInternedSchema`: it resolves exact
  layout/kernel rows, preflights every missing table row and layout-member word,
  and performs no mutation until the complete schema/material operation can
  succeed. Failures after that preflight are invariant violations rather than
  partial-success results.
- Kept material-row ownership explicit: schema rows are interned, but every new
  shared publisher variant receives its own material row so the publisher's
  reference count remains the sole lifetime authority.
- Routed new publisher acquisition through that compound database operation.
  Updates retain the material row's current layout/kernel handles and cannot
  accidentally create schema rows.
- Restored the missing cache-codec registry, runtime asset bootstrap, and editor
  third-party watcher composition contracts that prevented the modularized
  editor launch path from reaching Vulkan validation.
- Corrected the canonical scene capacity profile so the three supported legacy
  layouts, all of their member declarations, fixed-stride material words and
  bindings, and the corresponding logical texture/sampler rows are reserved at
  initial creation and grow together at a publication boundary. The previous
  profile allowed only one empty layout/kernel and zero payload/resource rows,
  so the real publisher could never be wired safely.
- Added the scene-boundary-owned `AdvancedGpuResourcePublisher`. Texture
  identity is engine-object reference identity; immutable sampler rows are
  value-interned. Batch acquisition, release, and acquire-before-release
  replacement preflight peak registry usage, exact reference multiplicities,
  overflow/underflow, conflicting descriptions, and the complete add/replace/
  tombstone journal transaction before the first database write.
- Added a renderer-neutral source encoder for the exact full, non-rectangle,
  non-MSAA `XRTexture2D` shape used by the three current material layouts.
  Format classes and comparison operations use explicitly numbered advanced
  enums rather than reorderable API enums. Sampler identity preserves all six
  minification modes, magnification, address modes, effective LOD range, LOD
  bias, requested anisotropy, comparison state, and opaque-black border color.
- Canonicalized finite sampler float keys so equality and hashing agree for
  signed zero. Logical records reject native descriptor indices, realized
  residency, backend generations, invalid dimensions/formats, and non-normalized
  sampler state.
- Attached publisher registry growth to the shared scene publication boundary.
- Routed production canonical scene publication through a preallocated
  whole-scene transition plan. The plan captures command, mesh, material,
  transform, geometry, bounds, and render-state inputs once; deduplicates shared
  material variants and command identities; and aggregate-preflights exact scene,
  schema, material, texture, sampler, journal, and reference-count capacity before
  `BeginPublication` makes any database mutable.
- Committed successful plans in dependency order: acquire logical resources,
  create/update/retain shared material variants, add or replace draw ownership,
  tombstone retired/unsupported draws, release old material references, and then
  release variant-owned texture/sampler references.
- Split publication visibility into reserve, prepare, and commit stages. Table
  snapshots remain ring-invisible while the producer publishes renderer-facing
  identities; the handle lookup image is copied under the publication lock
  immediately before the prepared ring slot becomes visible. Any exception
  after `BeginPublication` permanently faults and quarantines that database
  instance, so partially mutated live tables cannot be reused or projected.
- Preflighted every prior and current renderer identity source independently of
  material compatibility. Unsupported or removed primitive slots are republished
  as explicit invalid handles instead of retaining the previous frame's canonical
  identity.
- Added exact legacy-to-canonical translation for the three supported material
  layouts, GLSL scalar/vector/matrix value kinds, coverage and double-sided
  render state, texture feature flags, and native opaque/masked eligibility.
- Preserved unsupported material/pass/resource combinations on the ordered
  compatibility path with typed reasons instead of rejecting the whole
  publication. Resource failures now distinguish texture type, shape, empty
  content, non-finite sampler state, format, address mode, comparison operation,
  and comparison-on-nondepth failures rather than collapsing them into one
  generic binding reason.
- Published texture/sampler dirty owners through the scene snapshot and mapped
  them into backend-ready dirty ranges, so logical resource mutations no longer
  masquerade as material-only changes.
- Added retained texture and sampler deltas to backend template projection, and
  made backend package projection fail closed for rejected or faulted scene
  publications.
- Made every retained scene publication a complete immutable replay source for
  the records needed by Vulkan lowering. Draw, material, kernel, layout,
  texture, and sampler table snapshots now own their physical record/handle
  images, the authoritative logical handle lookup image, exact logical record
  count, and physical high-water mark. Material publications also retain the
  exact used layout-member, constant-word, and texture-binding ranges.
- Added a preallocated resource-source image to each retained publication. It
  holds a strong `XRTexture` reference for every logically resident canonical
  texture handle and validates the matching texture/sampler records before the
  publication becomes visible. Backend-ready packages remain compact identity
  and delta projections; they do not duplicate source objects or payload bytes.
- Kept logical lookup capture authoritative instead of rebuilding it from the
  physical occupancy image. Tombstoned rows remain physically retained for old
  publication leases, so treating occupancy as current logical residency would
  resurrect a retired handle in the new publication.
- Reworked `VulkanResidentDrawDependencyManifest` to resolve draw, material,
  kernel, layout, binding, texture, sampler, and source dependencies exclusively
  from the exact retained publication. It no longer consults the mutable live
  scene/material/resource databases while constructing a resident template.
  Any missing image, sequence mismatch, stale logical handle, or absent strong
  source reference rejects the template dependency manifest explicitly.
- Added `VulkanAdvancedSceneResourceRuntime` as the first native realization of
  an exact retained publication. Each frame slot owns a fixed 8 MiB
  `AdvancedSceneStorage` arena lane, immutable material/kernel/layout/payload/
  resource/lookup slices, and fixed-capacity descriptor-indexing sets. The
  normal frame path cannot grow any of these structures.
- Split sampled-image and sampler realization into independent descriptor
  arrays. Dense-plus-one encoded texture and sampler references reserve zero as
  invalid/fallback, and the shader-side resolver follows the logical sampler
  handle through its own lookup rather than deriving sampler identity from the
  image.
- Made lowering a complete preflighted transaction over the exact pinned
  publication. It revalidates every strong source image against the retained
  logical record, requires an already-ready Vulkan texture descriptor, creates
  no wrappers or uploads synchronously, and publishes no partial native state
  on capacity, source, descriptor, or sampler failure.
- Added typed, shared-state native publication receipts with at-most-once
  release. Prepared-frame transfer and resident frame-slot retention now carry
  those receipts beside the existing canonical GPU publication lease; native
  receipt retirement therefore uses the same frame-slot completion authority
  and is ordered before release of the canonical lease.
- Finalized descriptor-backend publication after the memory allocator is live.
  Logical-device bootstrap previously published descriptor-indexing capability
  facts but the live descriptor manager never selected the requested backend,
  leaving it incorrectly active as `DescriptorSets`. Startup now resolves and
  reports the exact requested/active backend before advanced-scene resource
  initialization.
- Kept the integration fail-closed and compatibility preserving: exact typed
  failures leave the existing ordered draw path intact, descriptor-heap mode is
  explicitly unsupported, and no advanced shader family is promoted to
  production by this slice.

## Ownership and lifetime decisions

- The retained scene publication ring owns packed payload bytes.
- A frame package may copy derived compact projections while holding a package
  lease, but it must not retain direct payload spans after releasing that lease.
- The eventual Vulkan consumer must acquire a GPU publication lease before it
  resolves the retained material payload and must release that lease only after
  the matching GPU completion authority retires.
- Production material integration acquires new texture/sampler ownership and a
  new shared material variant before publishing a draw replacement. It releases
  old material/resource ownership only after the replacement is fully accepted.
  One draw registration owns one material reference; one shared material variant
  owns one reference to each of its logical resource-binding slots.

## Validation

- `rdc doctor` passes for RenderDoc 1.44, including replay support and the Vulkan
  implicit layer.
- Release builds pass with zero warnings/errors for
  `XREngine.Runtime.Rendering`, `XREngine.Runtime.Rendering.Vulkan`,
  `XREngine.Animation`, `XREngine.Runtime.ModelAssetPipeline`, and
  `XREngine.Editor`. `XREngine.Runtime.Bootstrap` also builds successfully.
- Isolated Vulkan editor session
  `vulkan-phase31-material5-20260827` reached a playing Unit Testing World and
  published frames through the normal desktop frame transaction. The retained
  publication/material searches found no rejection, sequence mismatch, or
  invariant failure, and the Vulkan logs contained no validation VUID, device
  loss, acquire failure, submit failure, or present failure.
- Vulkan readback captured two 1920x1080 `R16G16B16A16Sfloat` viewport images
  after an immediate camera move. The images changed with the camera, proving
  that the screenshots were fresh render/readback results rather than stale or
  uninitialized data:
  - `Build/_AgentValidation/20260827-185318-vulkan-phase31-material/mcp-captures/Screenshot_20260827_191056_580_414315489bd743ada17b67b4aa24dc0f.png`
  - `Build/_AgentValidation/20260827-185318-vulkan-phase31-material/mcp-captures/Screenshot_20260827_191107_672_7ffb2097984742aa8e97e6fc33dd2d01.png`
- Startup produced one recoverable frame-97 `CompletionMaintenance` failure
  while a newly requested `XRTexture2DArray` framebuffer attachment had no
  Vulkan backing yet. Frame 98 completed, and ready/completed publications then
  continued past frame 1936. This is a distinct target-preparation/bootstrap
  lifetime issue; it did not corrupt or stop canonical publication and remains
  visible in the retained validation logs rather than being waived.
- No tracked automated test was added or modified. The existing Phase 3 filter
  was run only after production material/resource wiring passed live/runtime
  validation, in accordance with the phase-local test sequencing policy.
- The new logical-resource slice builds with zero warnings/errors in an isolated
  `XREngine.Runtime.Rendering` compile that excludes only five unrelated files
  currently broken by the concurrent Runtime.Core/facade ownership migration.
  `XREngine.Runtime.Rendering.Vulkan` then builds with zero warnings/errors
  against that output.
- A disposable runtime smoke under
  `Build/_AgentValidation/20260827-230500-vulkan-resource-publisher/temp-build/`
  passed acquire, sampler-state replacement, same-handle texture metadata
  refresh, acquire-before-release retirement, and final balanced release.
- A second disposable public-scene smoke under
  `Build/_AgentValidation/20260827-230500-vulkan-resource-publisher/temp-build/scene-transition-smoke/`
  passed first publication, two-draw material sharing, same-variant numeric
  update without handle churn, one texture acquisition per variant, nested
  texture/sampler metadata refresh without a leak, one-owner retention,
  last-owner retirement, exact unsupported-pass and resource-shape compatibility
  reporting, stale identity invalidation, and recovery back into the canonical
  stream.
- The normal Debug dependency build now succeeds through Runtime.Core,
  Runtime.Rendering, Vulkan, Editor, and UnitTests. Isolated Release builds of
  Runtime.Rendering and Runtime.Rendering.Vulkan pass with zero warnings/errors.
- The existing Phase 3 regression filter completed with 104 passing and 6
  failing tests. All six failures are pre-existing source-string assertions for
  culling shader constants, Hi-Z constants, occlusion binding spelling, LOD
  command identity, a Vulkan profiler toggle, and an ImGui overlay handoff; none
  cover or touch the material/resource transition implementation. No tracked
  test was added or modified.
- Fresh named session `phase31-material-transition-20260827` built and ran the
  Vulkan Unit Testing World to frame 2644. Two camera positions produced distinct
  1920x1080 `R16G16B16A16Sfloat` readbacks:
  - `Build/_AgentValidation/20260827-230500-vulkan-resource-publisher/mcp-captures/live-a/Screenshot_20260827_214300_446_8b48636d42f94f7184512017b68261db.png`
  - `Build/_AgentValidation/20260827-230500-vulkan-resource-publisher/mcp-captures/live-b/Screenshot_20260827_214303_706_fc6eb5c2a3b844d3a1a9a6e8d4dde46c.png`
- That session stopped cleanly with no canonical publication rejection,
  exception, Vulkan VUID, validation error, device loss, or OOM. The earlier
  recoverable startup framebuffer-backing race did not reproduce. The live log
  instead exposes the intended next boundary: advanced rendering remains on the
  legacy pipeline because Vulkan does not yet provide GPU-addressable texture
  indirection. Repeated `ResourceVector` full-validator fallbacks for a changing
  UI buffer remain separate Phase 2 fast-path work.
- A max-effort read-only architecture re-review of the rejection/fault gate,
  texture/sampler template deltas, staged lookup/ring visibility, unsupported
  identity clearing, and granular resource reasons found no remaining blocking
  correctness issue.
- Fresh named session `phase31-staged-commit-20260827` rebuilt the complete Debug
  editor graph with zero warnings/errors and exercised the staged publication
  path through frame 520 with 227 live rendering commands. Two camera positions
  produced distinct, visually inspected 1920x1080 Vulkan readbacks that match
  the earlier baseline:
  - `Build/_AgentValidation/20260827-230500-vulkan-resource-publisher/mcp-captures/staged-a/Screenshot_20260827_220924_953_56b7a2aae69e4e1ba5fa41e85dbf22b5.png`
  - `Build/_AgentValidation/20260827-230500-vulkan-resource-publisher/mcp-captures/staged-b/Screenshot_20260827_220933_543_a6d03f9486bb40208025771bfa7e96d7.png`
- The staged session contained zero canonical publication rejection/fault and
  zero Vulkan validation VUID. Startup frame 73 reproduced the separately known
  `XRTexture2DArray` framebuffer-backing race, recovered on frame 74, and then
  continued normally. Independent session `p68-vulkan-20260827` recorded the
  same failure on frame 70 before these staged-publication changes, confirming
  that it is a pre-existing target-preparation/bootstrap lifetime issue rather
  than a regression in this slice.
- Renderer-neutral and Vulkan Debug builds passed after the retained-image work
  with zero warnings/errors. A full isolated editor build for named session
  `phase31-publication-images2-20260827` also completed with zero
  warnings/errors.
- A disposable publication-image smoke under
  `Build/_AgentValidation/20260827-224758-vulkan-publication-images/scratch/`
  retains three simultaneous publications across add, replace, and tombstone.
  It first exposed the physical-occupancy resurrection bug, then passed after
  capture switched to the authoritative logical lookup image:
  `retained-images-ok handle=1:1 sequences=1,2,3 lookupRows=2,2,2`.
- Fresh named Vulkan session `phase31-publication-images2-20260827` exercised
  the corrected build through frame 853 with 219 live commands. Profiler state
  reported 19 resident-template creations, 9 exact dependency invalidations,
  zero dependency rejects, zero Vulkan validation messages/errors, and zero
  dropped frame or draw operations. A visually inspected 1920x1080
  `R16G16B16A16Sfloat` readback came from the live Vulkan viewport:
  `Build/_AgentValidation/20260827-224758-vulkan-publication-images/mcp-captures/Screenshot_20260827_230449_240_63fd2860a4234beab99bdf7d49f3545c.png`.
- The final log scan found no canonical rejection/fault, source-capture failure,
  dependency rejection, VUID, device loss, or OOM. One recoverable startup
  `XRTexture2DArray -1` backing warning reproduced the known independent
  framebuffer-preparation race. A prior run of the same slice also observed an
  unrelated one-shot mesh-cache initialization `NullReferenceException`; it did
  not recur in the corrected run. RenderDoc 1.44 remained capture-ready, but no
  GPU capture was needed because the camera-dependent readbacks and logs were
  conclusive for this publication-boundary change.
- The native-resource continuation builds in Debug with zero warnings/errors.
  Named isolated editor session `phase31-native-validation-20260827` rebuilt the
  full editor graph and ran with the `StandardValidation` diagnostic preset;
  validation layers and debug utils were both active. Startup reported
  `requested=DescriptorIndexing active=DescriptorIndexing`, followed by an
  advanced-scene runtime with two frame slots, 1024 descriptor entries per
  sampled-image/sampler array, resource set 2, and 8 MiB of fixed storage per
  slot.
- Progressive Sponza streaming first exercised the exact rejection path: old
  retained publications were rejected with named width/height/mip mismatches or
  `TextureDescriptorNotReady` while the current source was changing. No partial
  native publication escaped and ordered rendering continued. After streaming
  queues reached zero, a reversible scene-membership refresh produced the first
  successful native lowering at canonical sequence 717 / native generation 1,
  with 31 textures and 5 samplers in frame slot 1.
- At frame 998 the live profiler reported zero frame-package rejections, zero
  validation messages/errors, zero dropped frame/draw/compute operations, zero
  resident dependency rejects, zero native capacity failures, and zero broad
  fallback invalidations. The active strategy remained `CpuDirect`, as expected
  before the Phase 3.1 shader/pipeline cutover.
- Two post-success camera positions produced distinct, visually inspected
  1920x1080 Vulkan `R16G16B16A16Sfloat` readbacks:
  - `Build/_AgentValidation/20260827-234339-vulkan-advanced-scene/mcp-captures/Screenshot_20260828_000157_212_6a3da1186b734b1eaf808073a3845f4a.png`
  - `Build/_AgentValidation/20260827-234339-vulkan-advanced-scene/mcp-captures/Screenshot_20260828_000202_719_b23a5166f8274e60a78c5983acaa2292.png`
- Active rendering completed without a validation VUID, device loss, OOM, or
  canonical/native publication fault. Device teardown still reported the known
  `VUID-vkDestroyDevice-device-05137`, naming five image views and one pipeline
  layout from separate lifetime debt; no advanced-scene descriptor pool,
  descriptor-set layout, descriptor set, or sampler was listed. The advanced
  runtime nevertheless now retires its descriptor pool and uncached layout
  explicitly before the common retirement drain.
- Final named session `phase31-native-retirement-20260828` rebuilt the complete
  isolated editor graph with zero warnings/errors and revalidated the final
  ownership code under Standard Validation. After streaming settled, canonical
  sequence 595 lowered as native generation 1 with 31 textures and 5 samplers.
  At frame 843 the frame outcome was `Completed`, with zero frame-package
  rejections, validation messages/errors, dropped operations, resident
  dependency/capacity/broad-fallback failures, descriptor binding skips, or
  pending retirements. The named session manager stopped the owned process, but
  that run ended before reverse-order teardown diagnostics were flushed; the
  earlier typed leaked-object list remains the teardown evidence rather than a
  claim that the separate six-object debt was retested away. Final logs are
  copied under
  `Build/_AgentValidation/20260827-234339-vulkan-advanced-scene/logs/standard-validation-final/`.
- No automated tests were added or run for this continuation; live Vulkan
  feature validation remains the accepted gate until explicit phase-local test
  clearance.
- The final binding-ready slice reserves the production Vulkan ABI: advanced
  resource arrays are fixed-capacity set 2, canonical storage tables are set 3,
  visibility/pass resources remain set 1, and ordinary uniforms remain set 0.
  The runtime now owns compatible set-2/set-3 layouts and descriptor sets;
  prepared recording binds the exact retained publication state while legacy
  descriptor allocation, writes, and fingerprints exclude both sets. Missing
  canonical table domains receive a valid zero fallback slice until their SoA
  producers exist. Exact advanced programs are rejected at link time if types,
  counts, bindings, capacity, or runtime availability disagree.
- `dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj --no-restore`
  passed with zero warnings/errors after this slice. Named isolated session
  `phase31-advanced-binding-20260828` built through the Vulkan module cleanly,
  but the user-requested wrap stopped it before MCP readiness. It supplies no
  new live-frame, screenshot, parity, or teardown evidence. The named session
  manager reports it stopped and no editor process was launched.

## Remaining work

1. Instantiate and execute the real advanced shader/program/pipeline families
   using the now-implemented runtime-owned set-2/set-3 ABI. Keep
   `EAdvancedShaderFamily.None` until every required stage and resource exists.
2. Complete the remaining frequency-owned frame/view/pass/draw/object/instance/
   mesh/geometry/transform/deformation/state/visibility SoA streams and set-1
   visibility resources over the same frame-slot contract; replace the current
   set-3 zero fallback bindings rather than repacking per strategy.
3. Run live dual-feed material/resource/order/output parity across all five
   strategy lanes. Only then remove legacy arrays/maps and close the dependent
   Phase 2 reverse-dependency and exact-mutation gates.
4. Diagnose the reproduced startup framebuffer-attachment backing race as a
   separate Vulkan target-preparation/bootstrap lifetime slice.
5. Retire the five-image-view/one-pipeline-layout device-teardown debt under
   Standard Validation without conflating it with active-frame correctness.
6. After explicit phase-local clearance, add focused automated coverage for
   shared ownership, same-variant update, resource replacement, retirement,
   aggregate preflight rejection, and compatibility-reason stability.
