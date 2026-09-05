# Vulkan Phases 6, 7, and 7R implementation

Last updated: 2026-09-04

**Current status: paused at the user's request; phases incomplete.** The
[wrap-up and resume boundary](#2026-09-04-wrap-up-and-resume-boundary) below
supersedes the earlier chronological "in progress" and "not yet rebuilt"
statements. Work resumed from pulled commit
`f1318da77c07db362ffdbc71298b6f8082b4cb1a`; the older review baseline below is
retained as history. Source changes remain uncommitted.

## Objective and baseline

Implement the master TODO's asynchronous OpenXR ownership/lifecycle and Advanced rendering requirements, including the review remediation in Phase 7R. The user authorized implementation on 2026-09-04. Baseline revision: `55f46a4e335a03b923883b600d308313cd3efa81`.

The source review found incomplete ownership of accepted asynchronous submissions, unenforced in-flight and retired-generation limits, unsafe session teardown, uncompilable shading shaders, incompatible GPU records, unbounded classification/froxel consumers, and unexecuted shading/late/post stages. Contract declarations and managed builds do not establish runtime completion.

## Work and evidence

- In progress: receipt-based XR ownership and enforced admission, followed by session-safe replacement and teardown.
- In progress: executable classification/native shading and integration with existing Advanced late/post command primitives.
- Compiler checks passed for the current mono native implementation; live validation is in progress. GPU captures, allocation/lifetime evidence, stereo/offscreen/editor acceptance, and supported runtime/hardware acceptance remain open.
- New tests remain subject to the repository's runtime-first and explicit-clearance policy. Existing diagnostic tests may be used when necessary to reproduce an active defect.

Local evidence root: `Build/_AgentValidation/20260904-124955-vulkan-phase67-implementation/`. Required findings and commands will also be recorded here so this document does not depend on disposable evidence.

## Validation baseline

The preceding review passed `dotnet build XREngine.Runtime.Rendering.Vulkan/XREngine.Runtime.Rendering.Vulkan.csproj --no-restore -v minimal -p:XREngineUseExistingNativeBridges=true` with zero warnings/errors. The native bridge build failed on FileTracker access, and the test runner failed before discovery because its results directory was inaccessible. Neither is a feature acceptance result.

Shader compilation with the engine-generated Vulkan preamble passed reconstruction/classification/froxel compilation, but failed `ShadeNativeOpaque.comp` and `ShadeBackground.comp`. No live rendering or headset result has been established for this implementation yet.

## Implementation checkpoint: native compute and XR ownership

The Vulkan managed build now passes with zero warnings/errors using the command above. Native compute preparation seals the exact graph generation, image views, descriptor family, resident scene and pipeline generations. Recording now dispatches classification, per-kernel indirect argument construction, froxel construction, background initialization, native opaque shading, and a GPU overflow repair dispatch with explicit buffer/image dependencies. The late/post command chain now invokes transparency, temporal accumulation, motion blur, DoF, bloom, atmosphere/fog, final composition, and AA/upscaling commands; their visual output still needs validation.

The material shader contract is generated from the actual CPU layout offsets. Visibility identities resolve through draw/material/kernel generation handles, classification supports all 128 admitted kernel slots with bounded independent memberships, and froxel storage derives from extent and view count. Froxel index exhaustion marks affected cells for a conservative GPU light-list repair. Masked coverage now reads the actual alpha cutoff and base alpha rather than unrelated constant words. Real shadow/AO/GI/decal consumption and layered view execution are not yet complete.

`glslc --target-env=vulkan1.3` passed all 14 permutations: mono and array variants of `ClassifyTiles.comp`, `BuildClassificationIndirect.comp`, `BuildFroxels.comp`, `ShadeNativeOpaque.comp`, `ShadeBackground.comp`, and `VisibilityRasterMasked.frag`, plus `ReconstructionReference.comp` as the control. Sources used the freshly generated `AdvancedShaderAccessLibrary.BuildPreamble` with set 3 descriptor indexing, canonical include resolution, and the engine layout defines. Array compilation establishes syntax/layout viability only; the Vulkan runtime still rejects unimplemented layered output.

XR ordinary/paired/parallel-eye submissions reserve bounded tracker capacity before recording, prepare ownership before native submit, and commit the exact accepted semaphore/value in the common submit authority. Prepared inputs and uploads transfer to tracker retirement even when a later outward operation fails. Temporary mirror commands now have a distinct retirement payload; remaining mirror/SPS callers and parent/child teardown are under implementation. These source changes have not yet passed the runtime lifecycle matrix.

## Live validation setup

Named isolated session: `phase67-native`, created under `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260904-132443-phase67-native/`. Process overrides select Vulkan, Advanced Required, Desktop, and Vulkan synchronization validation. `XRE_UNIT_TEST_USE_ADVANCED_RENDER_PIPELINE=1` was added so this selection does not require modifying saved world settings.

The first isolated build failed before launch: both configured NuGet audit endpoints were unavailable, and `OscCore` treats NU1900 as an error. The same isolated artifacts directory is being rebuilt with `-p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true -p:XREngineUseExistingNativeBridges=true`; this invocation-only adjustment does not change package versions or repository audit policy. No live image has been accepted at this checkpoint.

## Live validation findings and corrections

The isolated editor build subsequently passed with zero warnings/errors. Incremental validation uses `dotnet build XREngine.Editor/XREngine.Editor.csproj --configuration Debug --no-restore --artifacts-path Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260904-132443-phase67-native/artifacts -p:Platform=AnyCPU -p:XREngineUseExistingNativeBridges=true -p:UseSharedCompilation=false /nodeReuse:false`. The sandboxed launch could not create its HTTP listener; launching the same named session through the approved elevated execution path established MCP readiness. Only this named session is stopped/restarted.

Successive Vulkan runs exposed these concrete blockers before native scene submission:

1. Managed resource realization rejected `BloomBlurTexture`: five declared mips but one produced mip. Late/post factories now derive dimensions, layers and mip ranges from their immutable resource profile; the auto-exposure and depth-history formats were also aligned with their declarations.
2. Depth/stencil view aspect declarations disagreed with the actual views. The declarations now specify depth or stencil explicitly, and depth-peel depth images no longer request unsupported depth storage-image usage.
3. Advanced stage ordinals collided with the classic mesh-pass collection indices. The graph merged attribute reconstruction with late raster depth writes and correctly rejected the feedback hazard. Advanced graph nodes now use synthetic graph identities with named dependencies, independent of mesh collection numbers.
4. The graph then reached exact scene-descriptor publication, which rejected canonical texture `1:1` indefinitely. Cold image wrappers were inspected without triggering their existing readiness/upload path. Publication now prepares the exact image before rechecking readiness and reports the source name, dimension, generations and wrapper readiness if it remains unavailable.

The graph-fix and texture-fix editor builds both passed with zero warnings/errors. The next runtime attempt is in progress; recovery-background presentation is not accepted as a rendered scene. No screenshot or GPU frame has yet passed visual acceptance.

XR source integration now covers immutable generation-bearing admission tickets, full ordinary/paired/parallel/SPS/mirror ownership, real frame/predicted-display metadata, and profiler completion at proven timeline retirement. In-session resize retains the parent session; terminal teardown returns a failure/defer result while child GPU ownership remains. RuntimeRecommended dimensions retain the safe Monado refresh/reprobe policy. These changes pass narrow rendering/Vulkan builds and are undergoing independent lifetime review; Monado/hardware acceptance remains open.

Typed canonical texture descriptors now include 2D arrays and cubes, with independently validated default sampler handles. Shadow publication and native sampling are being integrated; the shadow ABI now carries explicit depth conventions, moment-filter parameters and rendered depth/cascade ranges rather than relying on live camera state.

## Native dispatch and ownership checkpoint

The original saved skinned world reached stable-bin sealing and failed because the prepared payload referenced GPU-deformed vertex offsets while the canonical geometry described immutable bind-pose vertices. The implementation must retain a frame-local deformation overlay and exact GPU output ranges; changing the canonical geometry or binding static vertices would be incorrect. This repair is in progress.

A controlled three-mesh static OBJ fixture, held only in the isolated evidence directory, reached actual native compute dispatch with Vulkan synchronization validation. That run reported VUID-VkComputePipelineCreateInfo-layout-10069 and VUID-vkCmdPushConstants-offset-01795 because the common pipeline layout exposed 16 push-constant bytes while native compute uses 64. It also reported VUID-vkCmdDispatchIndirect-buffer-02709 because the classification dispatch buffer lacked indirect-buffer usage. The common compatible push range is now 128 bytes and classification arguments use DispatchIndirectBuffer. These changes require a fresh GPU run; the static fixture is an isolation aid, not a production fallback.

Independent XR ownership review identified six further defects: accepted-submit publication occurred too late; rejected preregistered uploads lacked a single settlement authority; retirement could repeat already-released components; partial swapchain creation was published before complete enumeration; direct batch calls bypassed checked acquire/release accounting; and teardown discarded drain failures. The corresponding corrections now pass the narrow Vulkan build with zero warnings/errors. A NUL-filled worktree copy of OpenXRAPI.RuntimeStateMachine.cs was restored from its intact staged copy and the scoped teardown corrections were reapplied. No Monado or hardware runtime acceptance is inferred from these source/build results.

Native shadow records now have a canonical 272-byte ABI, including rendered depth/cascade ranges, moment parameters, depth conventions, and the rendered point-light origin/far distance. Contiguous shadow groups cannot be relocated by generic compaction. Point/spot atlas sampling uses completion-stamped snapshots matched against allocation identity, content generation, and rendered frame; missing or legacy-only sources remain explicitly nonresident. Native shading consumes the exact texture/default-sampler generations and supports atlas PCF, VSM, EVSM2/4, cascade selection/blending, and radial point depth. All 14 mono/array shader compiler permutations pass after these changes; visual correctness remains unverified.

A layered R32ui AdvancedShading.ShadingDiagnostics output is wired at native set 1 binding 18. Bits 0–7 encode EAdvancedShadowFallbackReason, bits 8–15 encode shadow visibility from 0 to 255, and bits 16/17/18 identify invalid reconstruction, invalid material layout, and required classification overflow. The immutable request captures the selected shading debug view. This replaces inference from a magenta image with a captureable reason, while retaining visible failure output.
## Steady-frame capture and sampler admission correction

The static fixture was captured from two camera positions; both saved images were solid magenta. HDRScene was zero and the shading-diagnostics image had no native writes. A RenderDoc 1.41 frame (19408) contained Bloom, exposure, post composition, TSR and editor overlay work, but no native visibility/classification/opaque-shading dispatch. This is a failed frame, not acceptance.

The native stages were rejected in log_general.log, rather than log_rendering.log: canonical sampler 2:1 had no material binding that could revalidate its source. The validator already checked each texture's strong source, content generation and default-sampler state, but discarded that ownership evidence and required every sampler to appear in a material. Global shadow textures legitimately retain samplers without a material. Validation now records each successfully revalidated texture's generation-resolved default sampler before adding material witnesses; unowned or changed sources still fail.

The sampler, native-deformation binding, temporal scheduling and diagnostic-readback changes pass the isolated editor build with zero warnings/errors (99.10 seconds). The next static GPU run is PID 10912, started 2026-09-04 16:26:52 PDT. This build result does not close visual acceptance.

Diagnostic image readback also produced VUID-VkImageMemoryBarrier-oldLayout-01208: a planner-owned storage image lost its usage metadata, so an undefined prior layout restored as ColorAttachmentOptimal without attachment usage. BlitImageInfo now carries the exact physical image usage, resolves its submitted layout, and chooses a legal restore layout. GPU revalidation is pending. RenderDoc additionally reported host-access stage VUIDs during capture; application versus capture-layer ownership still needs isolation. The Vulkan host pseudo-stage is distinct from all queue commands ([Vulkan pipeline-stage specification](https://docs.vulkan.org/refpages/latest/refpages/source/VkPipelineStageFlagBits.html)); these errors are not dismissed as harmless.

RenderDoc was loaded only into the named session with ENABLE_VULKAN_RENDERDOC_CAPTURE=1 and DISABLE_VULKAN_RENDERDOC_CAPTURE_1_44=1, avoiding the two installed capture layers. The target-control helper verified the owned process ID and retained the connection until capture completion. Capture, pass inventory and validation messages are saved under the current evidence root. The replay session and editor were closed before rebuilding.

Further XR review caught an accepted receipt capturing timeline value zero before reservation. The common native-submit gateway now marks acceptance immediately after successful queue submission and commits its patched exact timeline value before telemetry. Remaining findings involve duplicate cleanup after registration, failed child destruction/rollback retention, and teardown result propagation; those are still being corrected. A clean build is not evidence that these ownership paths or headset runtime scenarios pass.

## User-reported outcomes

No implementation attempt has yet been reported working or failing by the user.

## Resume from `f1318da77` — 2026-09-04

The continuation starts from `f1318da77c07db362ffdbc71298b6f8082b4cb1a`
("Vulkan master todo phase 6/7 reimpl"). Existing solution/submodule/dependency
worktree changes were preserved. Evidence for this host is under
`Build/_AgentValidation/20260904-183514-vulkan-phase67-resume/`; the prior host's
ignored evidence is not present here.

The named session `phase67-resume` built with zero warnings/errors in 72.99 s
using `Tools/Manage-McpEditorSession.ps1 Start`. Process overrides select Vulkan,
Advanced Required, Desktop, `StandardValidation`, and command-buffer labels.
The session uses its own artifacts, metadata, cache and logs under
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260904-183614-phase67-resume/`.
`rdc doctor` passes on RenderDoc 1.44. A separate evidence-only broker review
completed with requested and actual model `gpt-5.6-sol`; its initial shader
attachments were rejected by the broker's `Build` path exclusion, so the
accepted review contained only renderer C# source. Compiler validation remains
a separate local operation.

Two MCP viewport captures at camera positions `(0, 1.2, 3)` and `(2.5, 1.2, 1.8)`,
both looking at `(0, 1, 0)`, were visually inspected and are solid magenta.
This is failed visual acceptance. The steady log reported an exhausted
16-family authoring lease arena. Source inspection found that
`TryCopyVisibilityInputs` required a deformation allocation even for static
draws, whose slice is correctly empty. The queue then tried the same invalid
publication in all free slots and replaced the real source failure with an
arena-exhaustion diagnostic. Static draws now explicitly clear their retained
deformation slice, skinned draws still require their exact allocation, and a
capture failure is returned without misreporting capacity pressure. GPU
revalidation is in progress; no parent checklist item is closed by this fix
alone.

### Resumed shader, static geometry, and submission ownership repairs

The current host's engine-resolved Vulkan 1.3 shader harness initially passed
12 of 14 variants. Both `ReconstructionReference.comp` variants failed on
undefined reconstruction layout macros. `ReconstructionInterface.glslinc` now
defines the Vulkan set-1 and OpenGL binding forms, matching the existing
visibility/reconstruction binding ABI. The fresh reflected preamble, engine
include resolver, and `glslc` now compile all 14 mono/array variants without
warnings. Compiler evidence is `reports/shaders/validation-manifest.txt` below
the current run root; this does not prove GPU shading correctness.

Live skinned-scene diagnostics identified the first terminal failure as a
36,552,080-byte canonical scene image exceeding its fixed 33,554,432-byte
reservation. The later deformation `AwaitingSubmission` state was a secondary
ownership bug: paused-frame queue reset discarded pending submission markers
without failing them. Queue reset/disposal now settle only queue-owned pending
markers, and preparation does not execute stale deformation jobs after an
unpublished build. A rejected producer invalidates its output and remains
retained until exact native-consumer reuse is ready; query failures and device
loss do not grant reuse. Scene-capacity growth and live recovery remain under
validation.

An isolated three-material OBJ fixture progresses further than the saved
skinned scene. It exposed two additional admission defects: static-only draws
required unallocated deformation buffers, and bin manifests rejected distinct
vertex/index ranges in the same packed buffer. Visibility now uses the existing
prepared-vertex source contract for canonical or deformed ranges. Static-only
bindings retain canonical vertices; an invalid previous deformation history
binds current vertices while the overlay disables previous-history reads.
Manifests retain distinct ranges with the same native generation and still
reject inconsistent generation, layout, or queue ownership. Manifest failures
now report their actual reason instead of `Ready`.

The isolated editor builds after these changes pass with zero warnings/errors
(46.44 s and 49.02 s). The 19:00 and 19:07 fixture attempts still failed and
their magenta output is not acceptance. The 19:12 attempt uses the new manifest
repair and a RenderDoc target attached only to session `phase67-resume`.

XR source repairs also preserve per-view swapchain handles and native arrays
across failed destruction, use an 8 ms retirement-pressure wait, record
prepared-input release progress, avoid an aggregate paired-eye upload list,
and preserve pending instance teardown. The narrow Vulkan build passes.
Dependent Vulkan image-view/framebuffer retirement still requires a completion
receipt before runtime swapchain destruction; Monado/hardware acceptance and
the corresponding master rows remain open.

### 2026-09-04 checkpoint: shader closure and late/post path status

Evidence root: `Build/_AgentValidation/20260904-183514-vulkan-phase67-resume/`;
named editor session: `phase67-resume` under
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260904-183614-phase67-resume/`.
The isolated editor build `native-contract-and-xr-build.log` passed with zero
warnings/errors in 36.30 seconds. The shader manifest reports 14/14 engine-
preamble/include-resolved Vulkan 1.3 mono/array variants compiled with zero
warnings after the reconstruction-interface macro fix. This closes only the
first two 7R.5 rows; no GPU/profile/hardware acceptance is implied.

Source review also confirms the current command chain invokes classification,
native shading, late transparency, exact-transparency helpers, temporal/post,
and output stages. `VPRC_AdvancedRenderStage` still fails closed on missing
snapshot, binding, reservation, capability, or realized-resource prerequisites;
production readiness remains pending until runtime/output evidence exists.
The static-native-2 RenderDoc capture (PID 30912, frame 1449) recorded 3
visibility draws and 135 native-compute dispatches, but identity/metadata/depth
replay was empty and HDR was black with alpha 1; the output remained solid
magenta. This is failed visual acceptance. Root fixes for canonical-table
`includeRecordImage` and subsequent resource ownership/XR receipt repairs are
not yet rerun through GPU/profile acceptance. No Monado or hardware pass exists,
and no tests were added, run, or cleared.

### 2026-09-04 checkpoint: typed publication and runtime status

The focused `native-post-payload-xr-storage-build.log` build passed with zero
warnings/errors in 50.61 seconds. Canonical `PhysicalRecords` are materialized,
typed identity clears the first visibility scope, and SPIR-V payload stride is
corrected from 92 to 96 bytes. All 18 shader variants pass through the existing
manifests: `reports/shaders/glslc-manifest-20260904.json` and
`glslc-array-manifest-20260904.json`.

Post-sampler refresh produced three static draws with real geometry/color.
MCP red/green/gray material updates were visually inspected in the two saved
camera captures under `mcp-captures/`. The Assimp-only Kd-to-BaseColor loss
remains a nearby importer gap. Fresh RenderDoc captures are
`static-native-4.rdc` (frame 1185) and `static-native-4-camera2.rdc` (frame 9213).

Monado PID 6012 still reports zero submitted frames and 360 no-layer events in
`reports/monado-storage-prewarm-summary.json`; teardown is true. Strict SPS
MirrorPreview omits `ReadOnlyStorageAuthority` while ordinary eye rendering
has it; correction is in progress. The authoring-prewarm guard alone was
insufficient. The program mutation-lock fix removed the observed deadlock, but
XR acceptance remains open and motion-history changes are not built. No tests
were added or run.

### 2026-09-04 checkpoint: temporal sidecars and XR slot progression

The focused `view-history-xr-slot-build.log` build passed with zero
warnings/errors in 48.30 seconds. The temporal shader manifest records 20/20
passing variants across 10 mono/array families, including mesh variants.
Source now carries per-draw temporal ownership flags and relation checks for
static/skinned sidecars, rejects deformation jobs that cannot seed produced
history, uses an explicit camera epoch, and fails closed when GPU view history
is invalid. Desktop history resolves from the frozen current collection before
the authoring-history/output-local sequence. OpenXR remains pending across all
views, with tracking, exact layered EndFrame commit, and lifecycle clearing
implemented in source. Dense velocity is gated on valid view, payload, and
previous-vertex inputs.

The latest desktop-ledger second-phase fix has not been rebuilt. Monado PID
26748 (`reports/monado-slot-epoch-summary.json`, logs
`20-56-31`) reports 0/360 submitted/no-layer events, teardown true, strict SPS
0, and retirement pending 0. It passes storage but then fails
`SubmitTrackedOpenXrMirrorSubmission` validity before GPU dispatch because the
packed batch is `[render, null, publish]` with count 2. The compact
`[render, publish]` correction is present but not rebuilt. Earlier PID 5128
failed at canonical arena slot reset epoch 0; bounded non-relocating slot
growth and shared immutable preparation for ordinary/SPS mirror paths are now
source fixes under validation. All XR acceptance and checkboxes remain open.

The native AO GTAO worker has just started; no AO or runtime acceptance is
claimed. No tests were added, run, or cleared.

### 2026-09-04 checkpoint: successful Monado submissions and AO admission

`native-gtao-history-xr-submit-build.log` passed with zero warnings/errors in
47.93 seconds. Monado PID 31636, using the static fixture and strict SPS,
reported 352 submitted frames, eight cold-start no-layer frames, zero EndFrame
failures, teardown complete, and zero final pending retirements in
`reports/monado-compact-submit-summary.json`. This is the first successful
GPU-submission cohort in this investigation. The compact two-command batch
is `[render, publish]`; the three-command path remains `[left, right, publish]`.
Temporary copy commands resolve to no frame-data slot (-1), while recorded
render commands explicitly own XR slots, so they do not seize desktop slots.

This cohort is **not clean Vulkan acceptance**: the new AO sampled-image
binding 20 collided with an existing storage buffer. AO storage/sample
bindings moved to 49/50 across shader declarations, layouts, and writes.
`gtao-descriptor-depth-build.log` then passed with zero warnings/errors in
50.21 seconds. Shader compilation exposed a reserved GLSL local name in the
new GTAO pass; the other 24 mono/array variants passed. AO shader/compiler and
visual acceptance remain pending. Depth-pyramid and late-visibility shaders
also incorrectly read the inverse depth range (`depthParams.z`) as the
reversed-depth flag; both now use the canonical flag component (`w`).

Desktop temporal history still commits at successful command authoring in
this built cohort. A receipt bridge is being implemented to acknowledge only
the exact output histories carried by a successfully submitted primary,
including zero-draw/background outputs. No phase-wide acceptance or test
clearance is claimed; hardware, pressure/recreation, AO/indirect-lighting, and
the remaining advanced feature/profile gates stay open.

### 2026-09-04 wrap-up and resume boundary

The user requested a stop and an accurate master TODO, rather than continued
feature expansion. The final source boundary keeps the canonical record,
visibility clear/stride, post bindings, program lock order, XR immutable
storage/arena/ownership, temporal-validity, and native GTAO repairs. The
unvalidated desktop accepted-submission bridge was removed after review; none
of its token/manifest/attestation files or integration symbols remain.

Final checks:

- `logs/wrap-up-editor-build.log`: isolated Debug editor build, 41.74 seconds,
  zero warnings/errors, using existing native bridges. Command:
  `dotnet build XREngine.Editor/XREngine.Editor.csproj --configuration Debug --no-restore --artifacts-path Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260904-183614-phase67-resume/artifacts -p:Platform=AnyCPU -p:XREngineUseExistingNativeBridges=true -p:UseSharedCompilation=false /nodeReuse:false -v minimal`.
- `reports/shaders/wrap-up-26-variant-manifest.json`: all 26 Vulkan 1.3
  mono/array variants compiled without diagnostics through the engine
  preamble/include resolver. The final GTAO helpers/constants use the
  `XR_ADV_AO_` prefix. Standalone GLSL compilation previously missed a
  runtime source-optimization collision with `noise`; actual runtime
  admission and optimizer inspection now pass. Array compilation is not
  Advanced stereo acceptance.
- Monado `reports/monado-compact-submit-summary.json`: 352 strict SPS
  submissions, eight cold no-layer frames, zero EndFrame failures, clean
  teardown/final pending count. This cohort used the RVC/Default eye path and
  contained the subsequently corrected AO descriptor VUIDs. It is not a clean
  XR validation pass. No hardware-runtime acceptance was obtained.
- Desktop PID 48048 (`21-29-30` log directory) admitted Advanced rendering
  with the corrected runtime GTAO shader and logged zero Vulkan error/VUID
  matches through shutdown. `mcp-output/gtao-final-resources.json` lists the
  live `AdvancedShading.AmbientOcclusion` R8 target with sampled/storage/transfer
  usage. `gtao-final-capture.json` reports **Texture readback failed**. The
  inspected final viewport capture shows geometry with the fixture's default
  magenta materials/back-lit panels; it is not AO, lighting, or motion
  acceptance. Use the earlier red/green/gray two-camera captures for the
  narrow material-update evidence.
- No new/modified/run tests and no user test clearance. No user-reported
  visual pass/fail. No performance or phase-wide completion claim.
- Named editor `phase67-resume` stopped; owned Monado service PID 6672 was
  stopped after verifying its executable path and exact creation timestamp.
  RenderDoc was already closed. The user's existing solution/submodule and
  dependency-directory changes were preserved.

Known remaining correctness work, in implementation order:

1. **Desktop temporal acceptance:** the retained ledger freezes current
   collection state, resolves previous history at authoring, tracks output
   sequence/structure/camera epochs, and rejects stale/tombstoned sequences.
   It still commits on `XRRenderPipelineInstance.TryRender` authoring success.
   The removed bridge captured candidates too late (only after RecordPrimary)
   and missed synthetic `RequiresFreshEmptyTerminalWrite` output with no
   operation context. The next design must retain candidate tokens before
   every fallible readiness/record step, separately attest actually recorded
   output including synthetic empty writes, commit only exact accepted
   submission tokens, and discard every rejected candidate. Handle multiple
   outputs and pending next-frame collection without hot-path allocations.
2. **XR replacement after detachment:** ordinary busy-frame deferral already
   retries from `UpdateRuntimeState`'s current/applied resolution comparison.
   The unresolved defect is in `TryReplaceSwapchainsInSession`: after
   `CleanupSwapchains` succeeds, failed/empty replacement can leave
   `SessionRunning` without swapchains and stopped pacing; subsequent recreate
   exits early because no active swapchains exist. Distinguish pre-detach
   deferral from post-detach failure and route the latter through safe partial
   cleanup/session teardown/recreation while retaining requested dimensions.
   Successful replacement restarts pacing at the normal next-render callback;
   validate that boundary and failure recovery. Restrict full runtime/service
   dimension refresh to an explicit runtime/Monado quirk rather than every
   `RuntimeRecommended` preset.
3. **AO and motion runtime acceptance:** inspect the AO target through a
   working readback or RenderDoc, prove `EnableBuiltInAmbientOcclusion` true
   versus neutral false, inspect camera/object motion and invalidation masks,
   and integrate AO only with an actual indirect-light contribution. The
   current native shader has no contributing ambient/IBL term, so AO is
   sampled only in its diagnostic view. Custom providers remain rejected.
4. **XR matrix:** repeat clean strict SPS, ordinary/paired/parallel eyes,
   render-plus-publish/preview copies, delayed completion/image pressure,
   resolution replacement, repeated restart/loss, allocations/waits, and at
   least one hardware runtime. Retain the exact submission/child-retirement
   receipts throughout; do not replace them with global timeline guesses.
5. **Remaining advanced work:** original skinned fixture/arena reuse,
   textured/masked/mixed-kernel and capacity cases, correct local lights and
   shadows, real GI/IBL/probes and decals, late/OIT/refraction/fog/post behavior,
   and admitted stereo/offscreen/editor/picking profiles. Native mono opaque
   progress does not establish these features. The master retains all parent
   runtime, budget, production, and deletion gates.

Resume using the existing source repairs and evidence, closing one bounded
implementation/runtime slice at a time. Do not reopen fixed 96-byte payload,
typed visibility-clear, compact two-command SPS submission, or busy-frame
resolution-retry investigations without new contradictory evidence.
