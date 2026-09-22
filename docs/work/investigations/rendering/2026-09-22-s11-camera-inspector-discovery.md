# S11 Camera Inspector Discovery And Metadata

Date: 2026-09-22
Status: Validated (cold-path performance, live picker behavior and script lifetime gates passed)
Owner: ImGui editor / camera inspector
Source revision at entry: `ee9808725`

## Problem Statement

The September 17 Vulkan probe observed
`UI.ComponentEditor.CameraComponent.Settings` as the active leaf for 109.272 ms
during a 503.605 ms active render frame. That observation is not exclusive timing,
but source inspection found a falsifiable cold-path candidate: passive camera
settings drawing asks each asset field whether a creatable type exists, and a
cache miss synchronously scans every loaded assembly on the render/UI thread.

The same camera settings path also performs smaller recurring reflection work for
pipeline setting categories, display names, enum arrays, enum labels and tooltip
descriptions. This is a separate child candidate and must not be changed unless
warmed measurements show material cost after cold discovery is removed.

## S11a Entry Gate: Create/Replace Discovery

- Owning path:
  `CameraComponentEditor.DrawCameraSettings` ->
  `ImGuiAssetUtilities.DrawAssetField<TAsset>` ->
  `EditorImGuiUI.HasCreatablePropertyTypes` ->
  `GetPropertyTypeDescriptors` -> loaded-assembly enumeration.
- Hypothesis: the first passive draw of the render-pipeline and framebuffer asset
  fields can synchronously build the shared loadable-type catalog and account for
  a material part of the observed cold camera-settings leaf.
- Rejection check: if a matched cold run shows the type catalog was already warm,
  or removing render-thread discovery does not remove the cold child timing, the
  observation must be reassigned to native waits, JIT, GC, or another child.
- Affected workloads: first Player Cameras/camera inspector settings draw,
  create/replace menu opening, assembly/script load and unload, and repeated panel
  opening on both Vulkan and OpenGL.
- Smallest coherent change: keep passive drawing to a constant-time discovery
  state lookup; run cold type enumeration on a bounded worker only after the user
  requests create/replace; publish immutable results while drawing the popup;
  discard stale results across assembly/script generations.

### Ownership And Lifetime

- ImGui/render owner: request, state publication, popup rendering and asset
  assignment.
- Worker: reflection/type enumeration and immutable descriptor construction only.
- Generation: increment on any AppDomain assembly load and on dynamic game
  assembly unload. Results from a previous generation are never published.
- Cancellation/supersession: generation invalidation drops cache ownership;
  already-running reflection may finish, but its result becomes unreachable and
  cannot republish into the new generation.
- Collectible lifetime: cache invalidation clears both type keys and descriptor
  values before the dynamic load context requests collection.
- Lock order: the discovery cache has one private lock and does not invoke ImGui,
  asset assignment, logging or user callbacks while holding it.

### Predeclared Acceptance

- Correctness: passive camera settings draw performs no loaded-assembly/type
  enumeration; create/replace remains available; pending, empty and failure states
  are visible; selection still creates and assigns the requested type; script
  generation changes cannot publish stale types.
- UI: first panel open, repeated open, create/replace popup, pipeline settings,
  enum choices, tooltips and camera asset assignment remain visibly correct.
- Performance: after publication, `UI.ComponentEditor.CameraComponent.Settings`
  is below 1.0 ms p95 over at least 300 visible warmed frames. Cold discovery is
  absent from the render/UI-thread scope. Worker discovery may exceed 1 ms but must
  not block presentation.
- Retention: after invalidation and teardown, no completed old-generation cache
  entry remains and pending work cannot restore one. No unbounded retry loop or
  per-frame task allocation is allowed.
- Observer overhead: use the existing profiler scope and focused discovery
  counters/timings only; compare like-for-like Release Vulkan sessions.

## S11b Entry Gate: Warmed Metadata

Status: Deferred after S11a measurement; see disposition below.

Only proceed if warmed measurement shows material camera pipeline metadata cost.
If needed, cache complete immutable property/enum descriptors and read tooltip
text only after hover. Treat this as a separate edit and validation gate.

## Evidence Log

- Historical entry observation: 109.272 ms active camera-settings leaf in a
  503.605 ms active render frame. This is wall-clock active-scope evidence, not
  exclusive method timing.
- Source entry evidence: `ImGuiAssetUtilities.DrawAssetField<TAsset>` calls
  assembly-wide `HasCreatablePropertyTypes` during passive layout on a cache miss.
- Baseline and changed session evidence is recorded below. The final follow-up
  completes the live UI and script-reload gates.

## Baseline Attribution

The Release Vulkan baseline isolated the first-use cost from warmed drawing:

- The first recovered `UI.ComponentEditor.CameraComponent.Settings` leaf took
  401.871 ms. Later visible samples were 0.160-0.189 ms.
- A focused child scope around the synchronous descriptor scan measured
  `UI.Inspector.CreatableTypeDiscovery.Sync` at 356.009 ms inside that first
  camera-settings draw. This confirmed assembly-wide create/replace discovery,
  rather than recurring enum, attribute or tooltip metadata, as the S11 owner.
- A composited capture showed the Player Cameras settings UI visible during the
  measured workload.

## Implemented Ownership Model

- Passive `DrawAssetField` layout no longer queries creatable types. Discovery
  begins only after the user opens Create/Replace, runs on one serialized worker,
  and publishes immutable descriptors from the ImGui owner while the popup is
  open. Pending, empty, failure and retry states remain explicit.
- Each type-catalog generation owns separate descriptor, camera metadata and
  fallback-pipeline cache state. A generation change atomically detaches old
  state; in-flight work can finish but cannot publish into the replacement.
  Superseded queued scans are cancelled and all retry loops are bounded.
- The generation service observes every assembly load and registers each
  collectible `AssemblyLoadContext` once. An unloading context stays marked in a
  weak registration because its assemblies can remain visible through
  `AppDomain.GetAssemblies()` after `Unload()`; all S11 scans reject those
  assemblies before reading the shared type catalog and recheck generation after
  enumeration.
- Loader callbacks never mutate ImGui-owned dictionaries. They atomically swap
  thread-safe generation state or request owner-thread cleanup for the next
  frame.
- Editor-created fallback render pipelines use per-camera transient
  post-processing state with weak camera keys. Superseded pipelines and backing
  states are retired and destroyed on the ImGui owner under a fixed per-frame
  budget, preventing their object registrations, events, schemas or reflected
  types from pinning collectible assemblies.

## Changed Runtime Evidence

The first changed Release Vulkan pass captured 5,865 profiler samples with the
Player Cameras settings visible. Normal camera-settings leaves were
0.120-0.213 ms. One 3.156 ms sample followed an intentional UI mutation; a
separate 395.070 ms root stall contained only 0.137 ms of camera-settings work,
confirming the remaining stall belonged elsewhere. No synchronous
`CreatableTypeDiscovery` scope appeared during passive drawing.

Composited captures showed the camera settings, pipeline assignment and render
target controls present, including the Create/Replace affordances.

## Earlier Post-Review Validation

- The isolated Release Vulkan editor build completed with 0 errors and one
  unrelated nullable warning in `OpenGLRenderer.AdvancedMultisample.cs`. Its
  editor assembly was written after the final S11 source changes. The editor
  launched, answered MCP ping, and produced a composited Vulkan screenshot with
  Player Cameras settings visible. A later current-checkout Debug editor build
  passed with 0 warnings and 0 errors. The independent lifetime/concurrency
  review found no remaining blocking source issues.
- A live game-script reload was attempted through `compile_game_scripts` and
  failed before loading an assembly. The default generated `GeneratedProject`
  includes ten third-party scripts under `Assets/Imported/Aryia_By_Mimiiu_V1.1`;
  compiling them produces 77 missing Unity, UnityEditor, VRC and `nadena` type
  errors. These are imported avatar-package scripts, not XRENGINE game code.
  This is an unrelated input/project setup failure, not evidence that generation
  invalidation works or fails at runtime.
- To isolate that input, a disposable XRENGINE `S11Smoke.xrproj` was created
  under the ignored `Build/_AgentValidation/20260922-104224-s11-camera-inspector/scratch/`
  run. Its single `XRAsset` script compiled successfully through the editor's
  `--build-project-code` path. A separate named Release Vulkan editor session
  also built with zero warnings or errors and ran with the camera settings
  visible. The project-open dialog did not complete under UI automation, so a
  fresh isolated editor loaded the same project through its existing
  `invoke_method` MCP action. The initial load request exceeded the 120-second
  HTTP timeout, but a later `get_game_project_info` read-back confirmed
  `projectName: S11Smoke` with one game script. Two live `compile_game_scripts`
  calls then succeeded, first loading `S11ProbeAsset`, then replacing it with
  `S11ProbeAssetV2` and `S11ProbeAssetV3`. The editor type-catalog generation
  advanced from 44 to 46 across the final reload. A generic type enumeration
  briefly saw both V2 and V3 after reload; after GC it saw only V3. This
  confirms the old script type was collectible, but does not by itself prove
  that the S11 Create/Replace popup filtered the unloading type before GC.
- A structural Vulkan `build_and_reload_renderer` request also timed out while
  the editor remained alive. Repository guidance in
  `docs/architecture/rendering/renderer-backend-hot-reload.md` explicitly says
  this path is unsupported after NVIDIA Streamline initialization; it must not
  be used as the S11 script-reload substitute. The named session was stopped.
- The remaining gate at that point was to exercise an actual Create/Replace popup through pending,
  ready, selection and failure/retry states, then load/unload a changed script
  type in a project whose game scripts compile. The final follow-up below covers
  stale-type filtering, visible controls, assignment and undo. No regression
  tests were added: repository policy requires explicit user clearance after
  live validation.

## Final UI Validation Follow-Up

- The Create/Replace popup was observed in both pending and populated states in
  the disposable XRENGINE project. Holding the serialized discovery worker made
  the pending state deterministic while the Vulkan editor continued rendering;
  releasing it populated the picker.
- A follow-up source review found a captured selection callback allocated on
  every passive asset-field draw. The popup now returns the selected `Type?`,
  and the caller creates/assigns only on selection. This removes that additional
  per-frame closure without changing the existing assignment or undo callback.
  The isolated Release build passed with zero warnings and zero errors.
- Generic inspector asset assignment retains its undo transaction and `XRBase`
  tracking. Camera-specific pipeline/render-target callbacks retain their
  pre-existing direct-assignment behavior; S11 does not add a new undo contract
  to those fields.
- The final live reload check exposed a loader lifetime bug: every current
  `S11Smoke` assembly was already marked unloading, so the picker correctly
  excluded even the newly compiled assets. `GameCSProjLoader` held its logical
  load context only through a weak reference. Its registry now strongly owns the
  context until explicit `Unload`, removes the entry, then requests unload.
  This prevents context finalization from silently retiring an active project.

### Final Gate Results

The named `s11-smoke-0922` Release Vulkan session was rebuilt after the loader
fix with **zero warnings and zero errors** (1:27.32). A separate read-only
lifetime review accepted the strong-context ownership fix. The session used
only the disposable XRENGINE project; imported avatar scripts were not required.

- **Creation and undo:** clicking the generic inspector Create picker assigned
  `AAS11ProbeAssetV4`. The fixture's `SetField` notification counter advanced
  from 0 to 1. MCP undo restored null and advanced it to 2; redo restored the
  visible V4 asset and advanced it to 3. The populated-asset MCP serialization
  request timed out; the redo state was verified visually and the counter was
  separately read back as 3.
- **Failure/retry:** selecting `AAS11ThrowingAsset` closed the popup without
  changing the existing V4 asset or its counter. A disposable reflection probe
  injected a failed discovery result; the actual popup displayed
  `Type discovery failed.`, its diagnostic and Retry. Clicking Retry restored
  the populated list. Release core exception logging is compiled out by the
  existing `DEBUG || EDITOR` guard, so constructor log text is not claimed as
  observed evidence; the catch/log call is unchanged and was source-reviewed.
- **Reload before reclamation:** while Replace was open, compiling V6 retired
  V4. Both assemblies remained enumerable because the scene/undo still held
  V4, but the old context was undiscoverable and the new context discoverable.
  Generation 50 published 87 XRAsset descriptors, containing V6 and the throwing
  fixture, not V4. The visible list and type/assembly hover tooltip agreed.
- **Lifetime:** forced collection and finalizer completion before reload left
  the logically loaded game context discoverable. Explicit `Unload("GAME")`
  later detached all discovery entries; a read-back returned an empty cache.
  Retaining the old asset in a scene/undo is intentional ownership, not a stale
  discovery entry. No old generation republished.
- **Camera UI:** the actual camera Pipeline Assignment Replace popup published
  nine render-pipeline choices. Searching for `z` displayed
  `No matching types found.`; Backspace restored the choices. Repeated opening,
  camera visibility/pipeline/image-quality sections, labels and tooltips were
  inspected. The anti-aliasing enum displayed Use Global, None, MSAA, FXAA,
  SMAA, TAA, TSR and DLAA without changing the active setting.
- **Performance/allocation scope:** the earlier 5,865-sample Release run remains
  the matched performance evidence. The final review removed the additional
  passive selection closure; closed popups neither start tasks nor enumerate
  assemblies. Existing assignment delegates and open-popup rendering allocations
  are unchanged; no claim of a zero-allocation whole inspector is made.

Composited evidence is under the run's `mcp-captures/final-pending`,
`final-assignment` and `final-camera-picker` folders. The exact final runtime log
session is under the named session's
`logs/XREngine.Editor_release/windows_x64/xrengine_2026-09-22_16-27-37_pid42924/`.
Only the named validation editor was stopped. S11 is **Validated**, not Closed;
any new regression tests still require the repository's explicit post-validation
clearance. S11b remains deferred, not silently implemented.

## S11b Disposition

Status: Deferred as not material.

Warmed settings drawing remained roughly 0.12-0.21 ms in the changed Vulkan
workload, well below the 1.0 ms investigation threshold. The evidence therefore
does not justify caching more enum, attribute or tooltip metadata in this item.
Tooltip metadata remains hover-gated. Revisit S11b only if a later matched trace
attributes material warmed cost to those children.
