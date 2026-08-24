# Vulkan Resident Draw Stream Phase 3

Date: 2026-08-23

## Objective

Implement the Vulkan resident template table and native lifetime contract on
top of the Phase 2 canonical publication. Stable draws must resolve through a
generation-validated direct slot without a template-table hash lookup,
structural comparison, or program-artifact cache reacquisition.

## Final Design

- A render-thread-owned primary-slot table is indexed directly by canonical
  draw-handle index. Each primary owns one active sealed
  pass/strategy/instrumentation/dialect/output variant. A resolved
  `VulkanResidentDrawTemplateHandle` carries the canonical epoch and generation,
  variant ordinal, and resident-entry generation. The mesh producer publishes
  that handle with its next request, so a stable recording performs one primary
  access and one exact variant access without rebuilding structural or artifact
  fingerprints.
- Full canonical/template structural equality runs only while creating or
  replacing the active variant. Data-content changes advance independently;
  resource-table, layout/topology, and recording generations invalidate only
  the domains that affect native or recorded state.
- The Vulkan projection consumes each canonical publication's ordered mutation
  journal once by database epoch and sequence. Draw-owner updates, tombstones,
  and dense remaps evict the exact primary slot before lookup. Data-only changes
  remain generation-driven. Until Phase 4 adds reverse dependency manifests,
  structural updates, tombstones, and dense remaps in other owner domains clear
  the resident table conservatively instead of risking a stale template.
- Transactional typed dependency acquisition pins exact generations of pipeline
  layouts, pipelines, vertex/index buffers, and legacy render passes. Partial
  acquisition rolls back. Table eviction detaches visibility immediately, but
  dependency release waits until all prepared/submitted uses retire.
- Canonical publication ownership is handed off only after the destination has
  retained it. Prepared recording transfers canonical leases and resident-use
  pins to preallocated per-frame-slot lifetime storage before command execution;
  desktop, explicit, and OpenXR completion authorities release the slot only
  after their timeline wait proves prior GPU use is complete. Shutdown clears
  the table and retires slot lifetimes before render-pass and other native-owner
  teardown begins.
- Persistent program-binding artifacts are sampled from immutable typed
  publisher snapshots and exact material/program/engine generations. Stable
  hits do not reacquire the artifact; publisher deltas or generation changes
  make the resident entry cold.
- Strategy and instrumentation remain explicit key fields. Resident artifacts
  currently encode direct vertex-input commands, so their command dialect is
  reported truthfully as `VertexInput`.
- Shared and thread-captured request cohorts have fixed CPU-known capacity.
  Any queue/publication-retention failure rejects and clears the whole cohort
  before materialization. No sealed zero-readback pass retries through readback
  or submits a partial cohort.

## Issues Found And Resolved

1. Releasing the producer publication bridge before prepared recording retained
   it left a publication lifetime gap. The bridge now releases only after the
   destination pin succeeds.
2. Prepared-recording reset happened before queue submission, so template/native
   pins could retire while recorded commands were still in flight. Ownership
   now transfers to the desktop frame slot and retires after timeline completion.
3. A bounded variant array still required stable scans and could accumulate
   resize-dependent keys. Each canonical primary now has one exact active
   variant, and output identity excludes transient FBO IDs and dimensions.
4. Canonical mutation journals previously had no Vulkan consumer. Projection
   deltas are now applied before resident lookup and evict exact primary slots.
5. Queue overflow previously risked accepting a prefix. Shared and captured
   cohorts now fail atomically and defer/reject the complete frame outcome.
6. Including the global descriptor-write counter in resource identity caused
   continuous cold misses. Only persistent registry/owner generations
   participate; transient descriptor sets are not retained by templates.
7. Stable staging rebuilt full fingerprints before consulting a direct handle.
   The producer now carries its published resident handle, stable lookup checks
   only its exact variant and dependency generations, and full fingerprints are
   built only on the cold create/replace path.
8. Draw-owner deltas were exact, but structural mutation in another owner domain
   could leave a stale dependent template. Non-draw structural mutation now
   clears the table conservatively; Phase 4 reverse manifests can narrow this.
9. Desktop completion retired frame-slot lifetimes, while explicit waits,
   explicit-target slot acquisition, and OpenXR retirement did not. Every
   completion authority now releases the same frame-slot lifetime storage after
   its own completion proof.
10. Logical-device cleanup originally destroyed legacy render passes before
    clearing resident owners. Resident entries and submitted-use pins now retire
    before any native dependency owner is destroyed.

## Live Evidence

Evidence root:
`Build/_AgentValidation/20260823-222800-vulkan-resident-phase3/`.

Final normal validation session: `resident-phase3h-20260824`, PID 24300.

- A clean Sponza load created 125 resident templates. After warmup and a camera
  move, telemetry reported 220,348 direct resident hits, 210 cold misses, one
  replacement, one cold-path structural comparison, zero dependency rejects,
  and zero capacity failures. Program-binding telemetry reported three artifact
  builds, 21 reuses, and zero fallbacks.
- Screenshots from two materially different camera positions rendered the
  Sponza brick wall and exterior/roof correctly; the camera-dependent image
  change ruled out stale output sampling.
- Exact Vulkan log scans found no `VUID`, validation error, resident-use
  underflow, stale dependency release, or frame-slot lifetime-capacity
  exhaustion. Renderer teardown reached `reason=force-flush-completed`.
- Earlier iterations in the same evidence root exercised reload of 77 shader
  sources, 16 exact dependency-generation rejects and cold repopulation,
  Sponza `leaf.Roughness` mutation from `0.9` to `0.4` and restoration, import
  of 128 meshlet geometry payloads, startup resize from `1x1` to `1920x1080`,
  native-generation eviction, and clean shutdown.
- Startup could exceed the fixed 4,096-request cohort. The renderer rejected
  those cohorts atomically before materialization, then converged and rendered;
  no accepted prefix was submitted.

Device-loss validation reused the stopped named session with
`XRE_VULKAN_RESIDENT_TEMPLATE_DEVICE_LOSS_INJECT=1`, PID 22860.

- The one-shot injector waited for resident publication and marked the device
  lost at frame 920 through `ResidentTemplateLifetimeFaultInjection`.
- Repeated terminal `InvalidOperationException` reports while the window
  remained alive were the expected renderer policy after device loss. The
  explicit close destroyed the renderer and reached
  `reason=device-loss-force-destroy`; one terminal in-flight submission remained
  tracked, as expected when Vulkan cannot prove completion after device loss.
- Post-shutdown scans found no `VUID`, validation failure, resident-use
  underflow, stale dependency release, capacity exhaustion, or cleanup failure.

The final normal session also exposed an unrelated pre-existing
`ShadowAtlasManager.RemoveKeysForAtlasKind` dictionary-enumeration race during
startup reset. It was subsequently fixed by deferring atomic reset requests to
the shadow-atlas planning-thread boundary; see
`shadow-atlas-reset-thread-ownership-2026-08-24.md`.

RenderDoc 1.44 and its Vulkan layer passed `rdc doctor`. A capture was not
needed because the two MCP images and exact generation/lifetime logs identified
and validated the relevant path.

## Build And Test Status

- `dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj -c Release --no-restore`
  completed with zero warnings and zero errors.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -c Release --no-restore`
  completed with zero warnings and zero errors.
- No unit or regression tests were added or run. Repository policy requires
  explicit user clearance before test work for this integration after live
  feature validation.
