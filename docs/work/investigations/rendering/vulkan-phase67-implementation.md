# Vulkan Phases 6, 7, and 7R implementation

Last updated: 2026-09-08

**Current status: paused at the user's request. Five implementation tasks and 67 runtime validation tasks remain open; all nine source audits are checked.** The
[active XR/Advanced TODO](../../todo/rendering/vulkan-xr-and-advanced-rendering-todo.md)
now owns the implementation, audit, and validation checkboxes. The
[September 8 validation wrap-up](#2026-09-08-validation-wrap-up) records the
current stopping point, final build and failed mirror experiment. Later dated
entries supersede earlier findings only when they record an explicit closure.
The current checkpoint supersedes chronological "in progress" and "not yet rebuilt"
statements. Work resumed from pulled commit
`f1318da77c07db362ffdbc71298b6f8082b4cb1a`; the older review baseline below is
retained as history. The wrap-up source/docs were subsequently committed in
`8b104bf7a` (`More work`, 2026-09-04). No new runtime evidence is claimed by the
September 6 documentation reorganization.

## 2026-09-07 OpenGL implementation checkpoint

This checkpoint supersedes Build 9's statement that OpenGL pair residency is
unfinished. The native GL executor now implements early/late visibility,
indexed opaque/masked raster, GTAO, tile classification/indirect dispatch,
froxels, native material shading and background. Canonical globals share one
std430 arena so the largest shader fits the RTX 3090's 16-block compute limit
(15 active blocks). Texture/sampler pairs and immutable source publications
are retained by fenced output slots. No CPU fallback is introduced.

The single-pass stereo implementation requires `GL_OVR_multiview2`, two
hardware views and linked stereo programs. GPU compaction unions the two
independently culled draw streams, retains a two-bit mask per payload, and
submits one multiview indexed stream. Culling/history, depth reduction,
classification, AO and shading remain per eye. The driver linked all 12 mono
programs and all three stereo programs; the four stereo shader stages also
passed glslang compilation. The stereo vertex stage uses nine storage blocks.

Builds 23–32 passed with zero warnings/errors; final Build 32 took 27.68 seconds.
The static three-draw OpenGL
fixture produced finite, camera-dependent native HDR with authored red/green
panels and gray ground. Inspected captures are
`mcp-captures/RenderPipeline_HDRSceneTex_20260907_192139.png` and
`mcp-captures/RenderPipeline_HDRSceneTex_20260907_192228.png` under the existing
`20260906-123802-vulkan-xr-advanced` task run. This proves the explicitly selected
CPU-direct indexed producer feeding GPU native shading. Setting
`GPURenderDispatch=true` alone did not override the existing CPU strategy;
the stereo rerun explicitly requests `XRE_FORCE_MESH_SUBMISSION_STRATEGY=GpuIndirectZeroReadback`.

Runtime investigation also corrected integer attachment readback, invalid
mip counts on the reduced depth grid, and completed-slot/capture-package
retention. Picking publication leases are now limited to Advanced pipelines;
completed one-shot captures release their exact package/source leases.
The emulated stereo route now participates in viewport enumeration, output
binding, and canonical package preparation. MCP accepts `vr_eye="stereo"`;
`layer_index=0/1` selects each texture layer. `get_render_state` also accepts
the optional VR selector.

The final integration also corrected these concrete gaps:

- Headset view information now reaches the lifecycle setter that calculates
  the shared stereo frustum. Shared-eye collection uses the normal viewport
  package-production boundary. Exact authored views can contain two eyes even
  when the earlier collection package contains a mono view.
- The visibility candidate was 80 bytes overall, but .NET placed its first
  Vector4 at byte 8 while both shaders read byte 16. Explicit field offsets
  are now 0/16/32/48/64/72/76. Reflection verified the old and new offsets;
  correcting this layout changed the GPU-indirect output from empty to visible
  geometry (ARP-I48). This is shared Vulkan/OpenGL code.
- Instance object-layer/render-pass masks are named according to their actual
  contents. They are no longer misinterpreted as eye membership. Independent
  GPU culling and the multiview payload mask determine eye eligibility.
- Explicit emulation has a 64 mm baseline, scaled through the existing avatar
  and IPD controls. Its previous identity eye poses produced identical views.
- Two instance-owned ledgers resolve native GL stereo history independently.
  They commit after both eyes' full native background/shading operations have
  been accepted into the ordered GL context. This is native HDR authoring
  history, not a GPU-completion or headset-presentation receipt. Failed frames,
  camera cuts, changed projection/extent/output, and gaps invalidate reuse.
- MCP eye diagnostics retain the exact frozen views accepted by preparation,
  with their source frame/output/generation. Split-stage diagnostics retire
  old phases together so successful late compute/raster no longer coexist
  with an obsolete startup rejection.

Final runtime: task-owned session `xr-advanced-0906`, PID41360, explicit
OpenGL Advanced + `VR.Mode=Emulated` +
`XRE_FORCE_MESH_SUBMISSION_STRATEGY=GpuIndirectZeroReadback`, RTX 3090,
1920×1080 per eye, three static opaque draws. The final cohort ran about
87 seconds before the last snapshot (render frame 14694); two retained HDR
eye images are in `mcp-captures/stereo22-eye0/` and `stereo22-eye1/`, named
`RenderPipeline_HDRSceneTex_20260907_195533.png` and
`RenderPipeline_HDRSceneTex_20260907_195534.png`. Both contain the expected
red/green panels and gray ground with visible stereo separation. Each has
2,073,600 pixels, zero nonfinite samples, RGB range 0–0.17871094; means are
0.027614968 (left) and 0.027613005 (right). Their raw RGBA hashes match the
previous visually inspected eye pair from Build 31, and differ between eyes.
The separate camera-position capture from Build 30
`stereo20-camera2/RenderPipeline_HDRSceneTex_20260907_194815.png` was inspected
and shows the corresponding scene-perspective change.

`mcp-output/gl-state-22-final.json` reports accepted native phases, two
distinct eye/history keys with `TemporalHistoryValid`, no canonical publication
rejection, one retained publication, four package pins and three GPU pins.
This is bounded snapshot evidence, not an allocation-pressure proof. Final
shader checks cover four GL stereo stages, 24 GL mono stages, and 16 Vulkan
stages without compiler failures. The final build contains no added tests.

The independent Sol C# review found no blocker in the exercised fixture. Its
two capability questions are answered by the outer
`GetAdvancedVisibilityFamilyAdmission` gate, which requires bindless textures,
indirect count and at least 90 SSBO bindings before `_advancedAdmissionReady`
allows stereo selection. The review service rejected shader context under
`Build`; the successful review used six C# files only. Shader semantics were
checked locally, not certified by that review. All broker runs are terminal.

**Remaining acceptance:** ARP-I26 and ARP-I48 are closed as implementation;
ARP-V45/V52/V54/V03 and the other open V rows stay open. The logs still contain
initialization/compatibility texture-format, completeness and bindless-sampler
errors outside the demonstrated native HDR result. The full post/preview
output, masked/textured/skinned scenes, resets/failure injection, lifetime
pressure and real-headset behavior have not passed their required cohorts.
The new shared candidate ABI still needs a Vulkan runtime capture. Next work
is to triage those GL texture errors and run ARP-V45's complete history/reset
matrix, then the listed backend/profile cohorts; add precise I rows for any
new defects that investigation identifies. Do not repeat completed native
executor implementation or mark runtime acceptance from these source checks.

The task-owned editor is stopped. No other editor was stopped, and no commit
was made. The current checklist is 60/60 implementation, 5/9 audits and 6/81
runtime validation items complete.

## 2026-09-07 Build 9 source and runtime checkpoint (historical)

Build 9 of `XREngine.Editor` completed with zero warnings/errors in 48.42
seconds. No tests were added or run. The isolated desktop-plus-probe runtime
was PID43924; its current log is
`Build/Logs/XREngine.Editor_debug/windows_x64/xrengine_2026-09-07_13-52-54_pid43924`,
and the exact MCP diagnostic snapshot is
the task-run artifact `mcp-output/code-completion-diag-3-probe.json`.

The runtime admitted `DesktopAdvanced` and activated two independent banks.
At capture version 50 / render frame 1092, diagnostics reported desktop bank
output 10, identity 1, incarnation 2, managed 237,267,072 bytes; probe bank
output 9, identity 2, incarnation 1, managed 274,081,560 bytes; total managed
511,348,632 bytes. The prior late-planner generation error did not recur.

This is not ARP-V48 acceptance: the editor exited at 13:54:53 with
`ErrorNativeWindowInUseKhr` during swapchain recreation. No visual cubemap
proof, owner churn/delayed-completion proof, capacity recovery proof, or
practical-memory validation was obtained. It is retained as bounded runtime
diagnostic evidence only.

Source boundaries closed at this checkpoint are ARP-I21 (per-view Vulkan),
ARP-I22 (RVC exact per-eye history), ARP-I23 (owned offscreen consumers and
completion), ARP-I25 (executable diagnostics), ARP-I27 (928-byte foveation ABI
and GPU use), and ARP-I45 (bank lifetime/reuse diagnostics). ARP-I26 remains
active: its source executor and slot storage exist, but OpenGL SPS pair
residency integration is still unfinished. The September 6 and Build 5/6
snapshots below are historical; they do not describe this current boundary.

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

### 2026-09-06 tracker reorganization and source audit

The user requested a completed foundation document and precise checkboxes
that can close as work progresses. The master now preserves 29 unfinished
foundation requirements under stable F IDs; 121 previously checked Phase 0–5
items and their dated narratives moved to the completed implementation record.
No open item was promoted to complete by that move. Legacy 6/7/7R now have one
active checklist with distinct implementation (I), source-audit (A), and runtime
validation (V) rows. The master retains integrated Phase 8/9 requirements.

Read-only source checks at `8b104bf7a` confirmed these earlier defect
descriptions were obsolete:

| Implemented behavior | Current source |
|---|---|
| Froxel allocation derives from extent, tile dimensions, slices, and view/layer count; 262144 is only a historical minimum | `AdvancedRenderPipeline.NativeShading.cs`, resource declaration/allocation path |
| Classifier histogram covers 128 kernel slots; indirect counts clamp to initialized capacity | `ClassifyTiles.comp`, `BuildClassificationIndirect.comp` |
| Canonical material/light headers and accessors use 64/128-byte records | `AdvancedMaterialRecord.cs`, `AdvancedLightRecord.cs`, `AdvancedLayout.glslinc`, `AdvancedMaterialAccess.glslinc`, `AdvancedLightAccess.glslinc` |
| Native PBR resolves material constants/textures and convention-aware shadows | `StandardMaterial.glslinc`, `StandardPBR.glslinc`, `StandardShadow.glslinc` |
| Exact transparency and late/temporal/post/output command stages are wired | `AdvancedRenderPipeline.CommandChain.cs`, `AdvancedRenderPipeline.LateAndPostCommands.cs` |

These are implementation observations, not full visual or capacity acceptance.
The active checklist marks their source slices implemented and leaves their
specific runtime scenarios open. The 128-slot fix does not establish the full
layout/coverage/derivative/view classifier key or subgroup ballot/scan path.
Those remain explicit classifier audits and implementation work (`ARP-A05`,
`ARP-A06`, `ARP-I28`), including bounded shared-memory fallback. Desktop accepted-submission history, XR
post-detach replacement recovery/runtime quirk policy, actual AO/GI contribution,
decals, and admitted stereo/offscreen/editor integration remain explicit work.
The known failed AO readback and validation-contaminated Monado cohort remain
failed/incomplete evidence. No engine code, test, editor session, or GPU run
was changed or started for this documentation task.

### 2026-09-06 implementation resume

The user authorized completing the entire active XR/Advanced checklist after
the documentation cleanup. The new run root is
`Build/_AgentValidation/20260906-123802-vulkan-xr-advanced/`; the owned isolated
editor session is `xr-advanced-0906`. User settings and other editor processes
remain separate through an explicit per-session settings file.

Source tracing corrected the September 4 history handoff: at `8b104bf7a`,
`RenderFrameViewHistoryLedger.Commit` has no caller. `XRViewport.Render` discards
failed candidates but does not advance successful history. This is missing
accepted-history advancement, not an existing authoring-success commit.
ARP-I10–ARP-I13 still require exact immutable candidate/record/submit ownership.

XR-A01 closed with the [complete submit ownership inventory](../../progress/rendering/openxr-submit-ownership-audit-2026-09-06.md).
This is a source audit, not live route acceptance. The bounded broker review
(`493f45057d664572a3c83e4a164a238b`, requested/actual `gpt-5.6-sol`) confirmed
post-detach/partial-creation and full-refresh failure-state hazards for XR-I09/I10;
the implementation repair and its runtime checks remain in progress.

XR-I09/I10 implementation closure (working tree on `8b104bf7a`): replacement
now returns typed pre-detach/replaced/post-detach outcomes. Any failure after
cleanup starts enters child-safe SessionStopping recovery; incomplete initial
or running swapchain sets are rejected/recovered. Deferred full refresh retains
instance destruction and service-refresh intent. RuntimeRecommended refresh is
an explicit host capability configured for MonadoOpenXR, not inferred from a
manifest filename. The isolated editor build at 12:50 passed with zero warnings
and errors (`logs/build-xr-readback.log` in the current run). XR runtime acceptance
remains open; the existing teardown/end-session behavior needs lifecycle evidence.

The desktop baseline at 12:45 produced a black inspected screenshot, repeated
`final presentation source epoch 1 is incomplete`, and empty PresentNow clears.
The desktop ledger has no commit caller and can fill its three pending slots;
submission-owned history implementation is the next blocker. No AO acceptance
is claimed. R8Unorm readback conversion and byte-size support were added and
passed the same build. On the 12:51 restart with validation/sync validation
explicitly enabled, capture instead rejected the missing matching submitted
planner generation; it must be repeated after history/output recovery. Neither
an unwritten target nor successful format compilation is an AO validation pass.

ARP-I28 implementation: cold program creation queries compute BASIC+BALLOT
subgroup properties, emits the subgroup macro/extensions only when supported
and subgroup size exceeds one, and logs the selected path. Every lane reaches
the ballot and exclusive scans; shared histogram aggregation, barriers, ordered
membership emission, and the unsupported-device fallback preserve the same
contract. Index guards bound multiplication before membership arithmetic.
The Khronos subgroup specification informed capability and uniform-call rules:
https://github.com/KhronosGroup/GLSL/blob/main/extensions/khr/GL_KHR_shader_subgroup.txt
Six Vulkan 1.3 variants compiled: ClassifyTiles shared/subgroup, each mono/array,
and BuildClassificationIndirect mono/array (`reports/shaders/classification-manifest.json`).
The integrated isolated editor build passed with zero warnings/errors
(`logs/build-history-second.log`). This build includes an unfinished history
bridge: build success does not close ARP-I10–I13. Runtime and performance
acceptance for classification remains open. The earlier leaf build failed only
because it used old core references while history types were being introduced;
the integrated build resolves that dependency.

### 2026-09-06 AO capture checkpoint

The runtime shader compiler initially targeted SPIR-V 1.0 for the new subgroup
compute path, despite its successful offline Vulkan 1.3 compilation. The
13:07 run therefore failed classification compilation and its black AO image
was not a valid output. `VulkanShaderCompiler` now selects Vulkan 1.1/SPIR-V 1.3
for explicitly subgroup-enabled sources and includes that target in artifact
identity. The integrated isolated build (`logs/build-history-review.log`) passed
with zero warnings/errors. Task/mesh target policy remains Vulkan 1.3/SPIR-V 1.6.

Owned session `xr-advanced-0906`, PID 22164, started 13:13:38 PDT with Vulkan
validation and synchronization validation enabled. Its log selected
`SubgroupBallotScan`, subgroup size 32. Two 1920×1080 R8 AO captures were retained
and visually inspected over 4m32s: `RenderPipeline_AdvancedShading.AmbientOcclusion_20260906_131409.png`
and `..._20260906_131841.png` under the current run's `mcp-captures/`. Camera
positions were (4,3,7) and (-3,2,5), both looking at (0,0.8,0). Each contains
2,073,600 pixels with zero nonfinite samples; ranges/means were 0.02745098–1 /
0.9628924 and 0–1 / 0.9182258. White background and contact occlusion remain
attached to the floor/panels as the camera changes. This closes only ARP-V22.

The 13:21 final screenshot is still incorrect: magenta floor and black panels.
The later R32UI shading-diagnostics readback reported a uniform 65280 (the
background/fully-visible-shadow value), inconsistent with the color result;
this needs native capture and resource/producer correlation. No final color,
material parity, AO on/off, or history acceptance is claimed. Cold shader
reflection warnings about macro binding indices also remain; no VUID or error
was found in the inspected live Vulkan log slice through 13:20. These two
retained AO images are not a clean all-feature runtime cohort.

### 2026-09-06 integrated history and classification checkpoint

The reviewed integrated build (`logs/build-history-reviewed.log`) passed with
zero warnings/errors in 25.80 seconds. The eight affected classification and
native-shading shader variants also compile; ARP-I30 records that source-level
closure. Runtime creation of the changed ClassifyTiles pipeline then failed
with `ErrorUnknown` / driver `failed to compile internal representation`.
This happens outside RenderDoc and with an empty isolated cache (PID 648,
13:34:44 startup), ruling out capture-only behavior and persistent cache state.
The actual emitted SPIR-V passes `spirv-val --target-env vulkan1.1`.

The RenderDoc capture `renderdoc/native-color.rdc`, frame 2635, was taken from
owned PID 5376 and the inspection session was closed. It contains only post/UI
work because the native family failed pipeline creation in that run; its black
final export is not a capture of the earlier magenta result. The 13:32 AO-disabled
capture is also black/unwritten because the native family was rejected. It does
not close ARP-V23. Normal root settings and user caches were not removed.

The first controlled shader revision moves the dense kernel derivative flag
read out of the fixed 128-entry membership emission loop. Per-pixel resolved
kernel flags now aggregate with the shared histogram; the final membership
writer consumes shared flags. All derivative/view and range checks remain.
Runtime retest is pending; no driver-root-cause claim is made yet.

### 2026-09-06 history counter validation

The next build (`logs/build-history-snapshot.log`) passed with zero warnings
and errors in 39.28 seconds, including the pre-pipeline descriptor/token capture
and allocation-free ledger snapshot exposed in MCP `get_render_state` under
`activeViewports[].viewHistoryLedger`. `list_render_pipeline_resources` now
includes the live pipeline asset GUID for precise property inspection/mutation.

The multiply-high overflow rewrite removed the ClassifyTiles pipeline-creation
failure in the next non-RenderDoc run (PID 6568). This does not prove the shader
or driver root cause beyond that controlled source change.

Live PID 22448, started 13:44:47, revealed a history failure: at render frame
2942, viewport 0 had zero commits, 2938 effective discards, no pending slots,
and invalid committed history. Later frames repeatedly failed with “recorded
no fresh swapchain terminal”; AO capture rejected a missing matching submitted
planner generation. The I10–I13 implementation boxes remain open. Investigation
now traces the pacing request FrameId versus actual render-loop source-frame
identity at pre-reservation admission, then per-output binding/attestation.
No successful history, disabled-AO, or final-color validation is claimed.

### 2026-09-06 AO neutral-output checkpoint

PID 32484 used the 22.48-second, zero-warning/error integrated build in
`logs/build-history-gi-second.log`. The revised scheduling frame identity lets
history commit, but sparsely: at render frame 3446 there were 28 commits,
three effective discards, committed source 3157/sequence 3158, pending zero.
At render frame 36784 there were 377 commits/36 discards, committed source
36777/sequence 36778. Provisional collection still used the other frame domain;
its source has since been aligned in source and needs the next build/live check.
Initial no-fresh-terminal failures (through frame 876) prevent clean cohort
acceptance. I10–I13 remain open pending integration of that correction.

AO validation retained and inspected three 1920×1080 mono/layer-0 images in
`mcp-captures/` over 2m49s, at the same (-3,2,5) camera looking at (0,0.8,0):

| Capture suffix | Mode | Raw R8 RGB range / mean | Result |
|---|---|---|---|
| `20260906_135325.png` | Built-in AO disabled | 1–1 / 1 | Entire target neutral white |
| `20260906_135515.png` | Built-in AO enabled | 0–1 / 0.9182258 | Panel/floor contact occlusion follows scene; neutral background |
| `20260906_135614.png` | Enable true, AmbientOcclusionProvider null | 1–1 / 1 | Entire target neutral white |

Each filename starts `RenderPipeline_AdvancedShading.AmbientOcclusion_` and has
2,073,600 pixels / 6,220,800 finite RGB samples, zero nonfinite values. These
results close ARP-V23. An earlier immediate post-camera-change capture still
showed the previous accepted view and was not used as the enabled reference.
The final color screenshot remains magenta floor/black panels; native color
acceptance and contributing GI/AO acceptance are separate and remain open.

## 2026-09-06 desktop history sequence checkpoint

`build-history-sequence.log` passed the isolated Debug editor build with zero
warnings/errors (26.40 seconds). In desktop Vulkan session `xr-advanced-0906`,
PID 29888, two live snapshots reported:

| Render frame | Effective commits | Effective discards | Committed sequence/source | Pending |
|---|---|---|---|---|
| 497 | 494 | 1 | 496 / 495 | 497–498 (2) |
| 1586 | 1583 | 1 | 1585 / 1584 | 1586–1587 (2) |

The 1,089 intervening render frames produced 1,089 effective commits. History
was valid in both snapshots. The session logs contained no matches for
`NoFresh`, `ErrorUnknown`, `FrameViewHistory`, or `VUID` during this inspection.
The retained screenshot at 14:11:20 still shows magenta ground and black
panels, so this is ownership evidence, not native-color or motion acceptance.

The preceding build exposed a sequence bug in both instrumented PID 34564 and
uninstrumented PID 5876: repeated collections reused a predicted history
sequence with changing scheduling source, causing rejection and zero commits.
Collection now allocates a monotonic history sequence, freezes its scheduling
request, and transfers that exact cohort through swap to authoring. Skipped or
superseded provisional candidates are explicitly discarded. Earlier attempts
that compared render-loop counters against scheduling frame IDs were also
incorrect; those counter domains are distinct.

Evidence is in the current run's `mcp-output/history-sequence-1.json`,
`history-sequence-2.json`, and `logs/build-history-sequence.log`. GPU captures
made while the earlier build rejected every frame contain no native passes
and are excluded from visual acceptance.

## 2026-09-06 native color input diagnosis

RenderDoc capture `native-dense-history.rdc`, PID 36544, frame 892, contains
three indexed visibility draws and the real classification/native compute
stages. At native dispatch EID 178 the shaded floor is approximately
`(0.25, 0, 0.25)` and shading diagnostics are `65280` (success, full shadow
visibility). This is shaded material color, not the magenta error sentinel.
The GPU material table contains three valid 64-byte rows; all three constant
rows have base color `(1,0,1,1)`, RMSE `(1,0,1,0)`, and texture flags zero.
MCP `get_material_uniforms` confirms those same magenta CPU-side values.

The default Assimp material factory discarded the parsed property dictionary
and supplied only textures/name to the standard material constructor. Its
untextured branch deliberately uses magenta, so the fixture's MTL `Kd` values
never reached canonical publication. A scoped factory fix now applies a finite
authored diffuse RGB factor to `BaseColor`; fresh-import build/runtime validation
is pending. This change does not claim full Assimp material-property fidelity.

On the live editor, setting red `(0.8,0.08,0.04)`, green `(0.04,0.7,0.1)`, and
gray `(0.35,0.35,0.35)` through `set_material_uniform` changed the shaded floor
to gray. The panels initially remained black because their +Z normals faced
away from the only directional light and no GI/emission was present. Rotating
that light to pitch -45°, yaw 0° produced visible red/green panels and gray
ground in inspected `Screenshot_20260906_141818_796_325811a903fc44769ce13685cdf32cc1.png`.
No renderer color workaround was used.

Motion acceptance is still open: stationary velocity is exactly zero and
finite, but captures during camera interpolation also remained zero. The
`native-camera-motion.rdc` capture contains post/bloom/UI and shadow work but
no Advanced native stages. That capture cannot prove native velocity behavior;
the missing stage execution is being investigated before ARP-V17 closure.

Fresh-import follow-up, PID 14656: importing the distinct
`static-native-authored.obj` fixture through the fixed factory produces exactly
the MTL red/green/gray BaseColor values in MCP inspection. With the camera at
(-3,2,5) looking at (0,0.8,0) and the directional light at pitch -45°, yaw 0°,
the inspected 1920×1080 output at 14:29:59 shows both colored panels and gray
ground. No material uniforms were overridden in this session. The earlier
zero-warning build `build-gi-lifetime-corrected.log` includes the importer fix;
the newer `build-gi-retirement.log` has one unrelated GI nullable warning that
is fixed in source and awaiting the next build. Evidence:
`mcp-output/fresh-import-red-material.json`, `fresh-import-native-output.json`,
and `Screenshot_20260906_142959_071_08858214e38945bbad74d6e9960f12ce.png`.

## 2026-09-06 canonical history and probe checkpoint

Working tree on `8b104bf7a`. `build-canonical-history-gi.log` and
`build-gi-restart.log` each pass with zero warnings/errors. The latter includes
terminal publication gating and a new GPUScene publisher/database epoch after
Destroy/Initialize; retained old leases still address their old database.

The desktop collection package previously used raw-camera fallback view rows,
which set previous VP equal to current VP despite successful history commits.
Vulkan now realizes the frozen authoring view descriptors into canonical GPU
rows. Advanced primary recording is refreshed so cached set-3 bindings cannot
reuse stale camera history. PID36228 camera tweens produced finite velocity:
14:35:13 EXR range -0.00006568432 to 0.0022201538; reverse tween 14:36:40 range
-0.00038170815 to 0.000051617622, both zero nonfinite samples. The normalized
14:36:40 PNG was inspected and shows spatially varying panel/ground motion.
This closes ARP-I37 implementation, not the full motion fixture matrix. The
RenderDoc capture selected an auxiliary post-only frame and is not native
same-frame binding proof.

The GI implementation review found no remaining P0/P1 lifetime issues: each
immutable IBL output pair is fenced before promotion, retained by publication
snapshots, and retired after producer/publication ownership ends using backend
resource retirement. The shader consumes the selected probe mode only and
attenuates indirect diffuse/specular with AO; direct and emissive terms remain
separate. Live acceptance remains open.

PID18980 Single-probe run (Vulkan Advanced, 64px capture, realtime every 3s,
no timeout) allocated capture resources but still reported CaptureVersion=0,
null published IBL textures and HasUsableIblTextures=false after two minutes.
Two startup VUID-VkImageMemoryBarrier-oldLayout-01197 errors identify a depth
image reopening from DEPTH_STENCIL_READ_ONLY_OPTIMAL while its tracked/native
layout was SHADER_READ_ONLY_OPTIMAL. TransitionFboAttachmentsForDynamicRendering
was normalizing the recorded old layout as if it were a desired attachment
layout. The correction preserves exact recorded layout and producer scopes;
build and a clean probe rerun are pending. No GI validation box is closed by
this failing run.

Probe follow-up: PID18400 reran for more than two minutes without Vulkan VUID
or ERROR entries, but retained a pending generation with CaptureVersion=0.
The initial assumption that validation aborted capture finalization was wrong:
private-state inspection shows a pending IBL output had been generated. Source
tracing found no registration in CaptureComponents; LightProbeComponent instead
registers in Lights3DCollection.LightProbes. Its new SwapBuffers override was
therefore unreachable. Publication now runs over registered probes immediately
before RuntimeWorldRenderer captures canonical global resources.

`build-probe-publication.log` passes with zero warnings/errors. PID38616 reaches
CaptureVersion=1 and publishes both usable IBL textures. This exposes a separate
missing layout declaration in existing Advanced forward probe bindings:
LightProbeIrradianceArray is imported but undeclared, causing repeated desktop
CompletionMaintenance failures. ARP-I41 adds the forward texture/buffer import
contracts. The session was stopped; contribution/updates are not yet accepted.
The barrier source also now conservatively downconverts synchronization2 high
stage/access bits instead of truncating write dependencies into legacy masks.

Further probe results: PID30632 reached generations26 and62 with usable
textures; its inspected Vulkan log had no ERROR/VUID entries. RenderDoc
probe-ibl.rdc frame547 (PID8348) proves native dispatch183 bound a valid probe
record and the two actual octahedral images, but both images contain zero RGB.
Their lifetime/generation validity is not evidence of nonzero capture content.
The GI=None image comparison was also invalid: None rejected native stages,
leaving stale output. ARP-I43 now admits None/null-provider zero-indirect work;
its image comparison remains pending.

A targeted draw trace in PID35348 found both convolution meshes enqueued with
pass=-2147483648 (unset), valid FBOs, instances=1, expanded=1 and shadow=false.
The ordered fence separately resolved unset to PreRender. ARP-I42 now gives
all convolution draws and their receipt an explicit common PreRender scope
when finalization/retry executes outside the command chain. Build zero
warnings/errors (`build-probe-pass-scope.log`); runtime rerun pending.

PID28028 disproved the pass-only fix: both convolutions used pass=-1, but
RenderDoc still contained no convolution draws and the published images were
black. Deferred Vulkan mesh materialization drops requests whose Pipeline is
null. Finalization runs after the capture viewport's pipeline scope has ended,
while the separate ordered fence can still be accepted.

The first explicit convolution owner passes the isolated editor build with
zero warnings/errors (`build-probe-pipeline-owner.log`), but PID26696 exposed
the next required contract: that owner has no frozen render-graph publication.
The planner correctly rejects its LightProbeCapture SubmissionMarker instead
of silently borrowing the main-view publication. This run was stopped. The
implementation must publish a complete auxiliary convolution scope and retain
its exact writers before ARP-I42 or GI runtime acceptance can be closed.

## 2026-09-06 clean Monado SPS rerun

Working tree on `8b104bf7a`; zero-warning/error isolated editor build
`build-probe-pipeline-owner.log`. PID8256 ran Vulkan DynamicRendering,
Default/RVC strict SinglePassStereo, stationary simulated Monado, foveation
off, independent desktop output, and Vulkan synchronization validation.
The isolated fixture disables probes so the incomplete convolution changes
are not exercised by this submission-only acceptance run.

Durable observed results: 360 retained frames, 351 successful strict SPS
submissions, nine cold no-layer frames, zero EndFrame failures, zero sequential
fallback attempts, zero summed validation errors, empty warnings/failures,
and completed teardown. Final swapchain and resource retirement counts were
zero. The last retained frame reports both eye publish deltas=1 and accepted
desktop present. Vulkan logs contain no VUID or ERROR; the initial desktop
readiness retry presented its initialization clear. A Monado bootstrap client
disconnect occurred before engine session startup, with no subsequent engine
submission failure. This closes XR-V01 only; pressure, fault, exact per-route
receipt, timing/allocation, visual eye, Advanced stereo, and physical hardware
matrices remain unvalidated.

Evidence: `Build/_AgentValidation/20260906-123802-vulkan-xr-advanced/reports/xr-sps-summary.json`
and named-session logs `xrengine_2026-09-06_15-23-10_pid8256`. The fixture and
environment are `scratch/xr-world.jsonc` and `scratch/xr-environment.json` in
the same run root. An initial launch failed before runtime because the scratch
fixture used `LightProbe=None`; correcting the actual enum to `Off` resolved it.

The same binary/fixture with ParallelCommandBufferRecording (PID40132) failed:
zero submissions, two no-layer frames, then the 90-second smoke timeout and
incomplete teardown. Vulkan repeatedly threw `parallel-paired-plan` at
VulkanFrameLoop.OpenXR.EyeRecordWorkers.cs:37, starting at frame12. The compound
condition currently hides whether first-eye preparation, second-eye preparation,
or paired-plan creation rejected. XR-I11 tracks that concrete gap; XR-V03
remains open. Evidence: `reports/xr-parallel-summary.json` and
`xrengine_2026-09-06_15-26-14_pid40132/log_vulkan.log`.

## 2026-09-06 late transparency safety checkpoint

ARP-I34 source is implemented on `8b104bf7a`: the shared PPLL producer uses
checked host sizing capped at 128 MiB, clamps allocation to actual node-buffer
capacity, saturates overflow reporting, and exports emitted/dropped/rejected/
status GPU words. Advanced resets all head texels and counters on the GPU,
declares producer/resolve storage dependencies, validates each link against
actual/emitted bounds, limits traversal to 256 and resolution to 16 fragments,
and rejects overflow/corrupt/over-limit pixels transparently. Its isolated
premultiplied resolve preserves earlier HDR instead of replaying a stale scene
snapshot. Default's shared producer ABI is updated; its resolve is unchanged.

`build-probe-motion-ppll.log` passed with zero warnings/errors. GLSL/SPIR-V
compilation passed for Advanced PPLL reset/resolve, lit/unlit shared producers,
and the colored-alpha velocity/reactive pair. No pressure or composition
runtime acceptance is claimed by these compiler checks.

ARP-I32 has concrete per-material temporal variants and an explicit built-in
colored-alpha factory. Water remains blocked pending a displacement-correct
variant. Initial live runs exposed reactive FBO resource/depth dependency
errors; the declaration now includes color-attachment usage and dependency
edges, and its factory uses the declared native depth resource. The latest
`build-motion-depth-dependency.log` passes zero warnings/errors; runtime
resource realization and ARP-V26 coverage/motion acceptance remain pending.

## 2026-09-06 paced parallel-eye recovery

The parallel failure was empty eye operations after cold RVC resource/package
generation mismatch. `VulkanPresentNowReadinessException` inherits
InvalidOperationException, so the Vulkan binding's existing rethrow escaped
the normal no-layer xrEndFrame/pacing path. A specific preceding catch now
returns handled/false with no sequential fallback; the existing finally still
releases acquired images. The next normal collection then captures realized
resource generations without accepting stale packages or adding a second
collection protocol.

`build-required-probe-writers.log` passes with zero warnings/errors (25.51s).
PID30284, same simulated Monado/Default-RVC parallel fixture, retained360:
357 successful submissions, three cold no-layer frames, zero EndFrame failures,
and zero final reported pending retirement. The initial diagnostic now names
`parallel-first-eye-preparation`, followed by paced recovery and sustained
eye publication. This closes XR-I11 implementation/recovery only.

The run still failed teardown: repeated GPU-quiescence-incomplete warnings
retained OpenXR parents, and shutdown timed out waiting for XRE-Update.
`teardownCompleted=false`; XR-I12 and XR-V03 remain open. Evidence:
`reports/xr-parallel-paced-summary.json` and named-session logs
`xrengine_2026-09-06_15-46-34_pid30284`.

## 2026-09-06 required probe producer work in progress

The IBL scope now includes an explicit pipeline owner, PreRender pass, and
frozen renderer resource scope. Required producer authoring is atomically
captured, strictly materialized, and rolled back on incomplete work. The
recording layer still needs an exact member receipt so a skipped native draw
cannot be certified by its later fence; that work is in progress.

Per-mip output FBOs and roughness materials are now stable during deferred
materialization. Irradiance is drawn into every mip directly, removing the
synchronous GenerateMipmapsGPU call that previously ran before the queued
writer. The captured environment's final-face/mipmap ordering still requires
completion staging. Unwritten Vulkan rollback discards outputs explicitly;
an immediate backend's unfenced partial failure disables probe production and
retains one terminal closure per renderer for safe native teardown.

No contributing GI output is yet accepted. PID38588's inspected RenderDoc
capture `probe-convolution-owned.rdc` was UI-only; persistent main-pipeline
DescriptorGenerationMismatch explains why a no-VUID run did not prove scene
rendering. Exact descriptor mutation/publication diagnostics are being added.

## 2026-09-06 exact capture receipts and XR child retirement (in progress)

The required IBL batch now requires every sealed producer operation to have actually recorded. An incomplete required marker aborts the primary command buffer, so partial writes cannot submit under a failed/unregistered fence. The latest reviewed receipt build (`build-required-recording-atomic.log`) passed with zero warnings/errors. A subsequent clean build (`build-probe-mutation-trace.log`, 25.78 seconds) includes bounded required-cohort preparation and the XR retirement diagnostics.

PID41580 exposed permanent required-cohort starvation: every new output generation changed preparation signatures, so the ordinary cold preparation time slice repeatedly omitted 2–10 of 15 draws. Required cohorts now attempt each bounded member once without the ordinary slice; genuinely unavailable resources still reject the complete batch. Descriptor provenance traced Advanced resource mutations to forward probe resource rebuilding during deferred material binding. The temporary stack trace instrumentation was removed after identifying the owner.

PID8056 cannot certify Advanced runtime behavior: shader hot reload picked up the decal worker's new 16-byte lighting counters while the running assembly still expected eight bytes, producing a native closure rejection. The named editor was stopped. Future Advanced runs require one coherent shader/assembly checkpoint.

Source capture changes compile but remain runtime-unaccepted: six immutable face FBOs, explicit capture scheduling, per-face completion polling, source-epoch restart, and delayed cubemap/octahedral mip generation are integrated with a new exact multi-pass output receipt. A terminal clear alone cannot attest a face; every receipt-tagged sealed operation plus the exact terminal target must record and its accepted submission must signal. Runtime RGB contribution is still unproven; ARP-I42/V16/V24 remain open.

The decal implementation's `BuildFroxels.comp` and `ShadeNativeOpaque.comp` both compile with the official engine source resolver/preamble and `glslc --target-env=vulkan1.3`. C# integration and live decal/list/overflow behavior remain unvalidated; this compiler result does not close ARP-I08 by itself.

A further parallel Monado cohort, PID3836 (`xr-parallel-retirement-summary.json`), narrowed XR-I12. At retirement all of the following were true: exact XR timeline complete, resource lifetime ticket ready, runtime images released, external lifetimes detached, and detached slots ready. The sole failing parent predicate was `childrenDestroyed=False` for six native child generations. Child pin/queue provenance diagnostics are now being added. The parent guard remains intact; no global-idle or completion bypass was introduced. This run used the already-built Default/RVC eye path and does not validate the unbuilt capture/decal changes.

## 2026-09-06 compiled decal and executable late-pass integration

`build-capture-receipt-decals-late-3.log` passed the isolated editor build with zero warnings/errors in 29.95 seconds (working tree on `8b104bf7a`). This is implementation evidence, not runtime acceptance.

- ARP-I08: per-froxel decal offset/count lists and indices now feed native shading before lighting. Conservative bounds cull the lists; exact projected-volume/material/texture/view/layer checks govern application. Base/mask alpha controls coverage; base color, projected normal maps, and RMSE roughness/metallic affect the surface. Allocation overflow is counted and repaired by an exact GPU scan. Zero layer mask means all layers; newly published decals default enabled. Both changed compute shaders also pass Vulkan 1.3 compilation. Live overlap, list bounds, overflow, and material output still need acceptance.
- ARP-I32: concrete material velocity/reactive variants now drive transparent replay into shared native motion/reactive outputs with opaque depth ownership. The colored-alpha factory shares source shader parameters with its replay variants, including opacity coverage; unsupported displaced temporal variants carry a separate temporal reason. This does not certify the motion/reset matrix or displaced water motion.
- ARP-I36: actual Advanced late submission now checks material metadata, concrete lane kind, opaque/masked bypass, and profile capability. Unsupported visible lanes report reasons. Exact transparency currently uses CPU draw submission into the requested GPU shaders until indirect submission has equivalent per-draw eligibility. On-top editor/debug overlays retain their explicit lane contract. Invalid enum kinds are rejected, and blocked-path logging no longer constructs a dynamic key per draw.

The same build includes exact multi-pass output completion receipts and capture lifetime changes, but ARP-I42 remains open until runtime face/convolution RGB and on/off contribution are verified.

## 2026-09-06 exact capture generation and XR child queue evidence

The isolated editor build `build-exact-local-generation.log` passed in 28.04 seconds with zero warnings/errors. PID34000's inspected desktop screenshot retained the red/green panels and gray ground. Its probe attempts failed before backend reservation because a manually collected capture package N was compared with the global desktop consumed generation N-1. The completion invocation now carries the exact swapped package generation; ordinary desktop validation is unchanged. The rebuilt capture path still requires a runtime pass. Review also identified release-coroutine fence settlement, pre-authoring rejection classification, cross-world shared-viewport admission, and originating-renderer retention fixes; these are being integrated before GI acceptance.

Monado Vulkan parallel-eye PID41204 retained 360 frames: 357 submissions, three cold no-layer frames, and zero EndFrame failures. Teardown remained false. All six undestroyed OpenXR child image-view generations reported ready retirement tickets and zero descriptor/template/recorded/queued pins; their last graphics sequences were below the observed completed sequence. The parent receipt was valid. At production frames 856/910/964, the image/view backlog remained 42 with zero admissions/completions. This narrows the blocker to queue-entry/dependency/admission handling; it does not yet prove meter starvation. The reported Phase 5.25 final pending count was zero despite incomplete parent teardown, so that counter cannot serve as complete lifecycle acceptance. XR-I12 and XR-V03 remain open.

Evidence: `Build/_AgentValidation/20260906-123802-vulkan-xr-advanced/reports/xr-parallel-child-summary.json`; session `xr-advanced-0906`, log folder `xrengine_2026-09-06_16-39-32_pid41204`. A bounded diagnostic now correlates each exact child generation with its image retirement entry, merged ticket, dependency result, and remaining scan allowance for the next run. No lifetime predicate or retirement budget was relaxed.

## 2026-09-06 parallel-eye teardown native view ownership fixed

XR-I12 is implemented and its previously failing teardown path now passes. PID29920's queue diagnostics proved that all six ready, dependency-free image-view entries were dequeued while their exact lifetime generations remained undestroyed. `VulkanTargetOutputServices.TrackLiveImageView` registered only the lifetime ledger/backing-image relationship, leaving `VulkanImageResourceService.LiveHandles` empty. `TryTakeImageViewGeneration` therefore skipped native destruction after dequeue. This was not scan-credit starvation.

Target views now also register through `Images.RegisterView`; every application-created VkImageView is engine-owned even when its backing image is OpenXR/WSI-owned. Immediate target-view destruction goes through `Images.TryBeginDestroy` so live-handle/descriptor metadata cannot remain stale. Deferred image bundles preflight live view ownership and verify actual view-generation destruction before clearing deduplication. An unexpected post-admission failure retains the dedup reservation and enters the existing quarantine path.

Validation: isolated build `build-xr-view-owner-complete.log`, 7.77 seconds, zero warnings/errors. Monado Vulkan parallel-eye PID6948, validation plus synchronization validation enabled, retained 360 frames and submitted 356, with four cold no-layer frames, zero EndFrame failures, and `teardownCompleted=true`. The initial bounded drain deferred ready children, then normal subsequent draining completed parent teardown around 16:49:46. No VUID/Validation Error matched the session Vulkan log. Named editor shutdown completed. No forced retirement or scan/budget relaxation was used. This closes the specific implementation defect; delayed-completion pressure, accepted-owner lifetime inspection, hardware eye output, and the rest of XR-V03 remain separate validation work.

Evidence: `Build/_AgentValidation/20260906-123802-vulkan-xr-advanced/reports/xr-parallel-owner-summary.json`; session log folder `xrengine_2026-09-06_16-49-17_pid6948`. The earlier registry-only PID21288 was stopped before producing a smoke summary and is not an acceptance run.

## 2026-09-06 Advanced profile diagnostics and explicit offscreen request checkpoint

The combined build `build-capture-multipass-bind.log` passed with zero warnings/errors (6.98 seconds). The new read-only `get_advanced_profile_diagnostics` command exposes actual backend admission/reservation/binding, raw capability discovery separately, declared/realized resource entries with descriptor revision/signature, canonical per-view package identity, and a bounded ledger of actual stage command/enqueue/rejection observations. GPU counter observations preserve their existing source frame/pass and are not automatically attributed to a stage or called GPU completion. PID43748 MCP read-back returned Advanced execution admitted with no execution blocker and 13 stage observations. The generic capability resolver's incomplete shader-family discovery is labeled separately instead of contradicting admitted native execution. Capture failures in this run caused real stage enqueue rejections, which the ledger reported with their frame identity. ARP-I25 remains open for the remaining capture-stable counter/per-eye attribution and completeness review; ARP-V38 is not closed.

The initial ARP-I23 explicit-request slice also compiles: an immutable offscreen intent reaches capability-gated Advanced construction, unsupported ownerless portal/mirror requests fail explicitly, and a raw export command is present for HDR/depth/visibility. Existing capture callers remain on their current pipeline. Caller integration, compatible-target verification, typed output capture, and offscreen resource/command review remain necessary before ARP-I23 or its validation rows can close. See the [stereo/profile audit](../../progress/rendering/advanced-stereo-and-profile-audit-2026-09-06.md) for remaining execution gaps; array declarations do not establish stereo support.

Probe receipt validation is still failing before writer completion. PID34024 isolated unbound receipts, and PID43748's field-level diagnostic identified `producer output id`. The light-probe pass context rejects a generic SceneCapture scheduling identity during lowering; a capture-kind correction is in progress. ARP-I42/V16/V24 remain open.

## 2026-09-06 probe writer RGB and disabled-provider proof

ARP-I42 and ARP-I43 are implemented and their specific failing paths now pass. Capture's explicit output identity now matches the LightProbeCapture context. Its shared viewport had remained internally 1x1 because automatic internal resizing was disabled; setup now passes explicit 64x64 internal dimensions. The completion binder accepts a complete multipass producer cohort while selecting exactly one frozen terminal, and the required convolution batch attests actual recorded writers. Face/encoding/convolution fences retain resources through completion, and uncertain submitted work quarantines its originating renderer closure.

Isolated build `build-probe-explicit-extent.log`: 25.88 seconds, zero warnings/errors. Vulkan PID31764 ran the static red/green/gray fixture, validation and synchronization validation enabled, Advanced desktop with LightProbesAndIbl and built-in AO. MCP observed CaptureVersion=39, HasUsableIblTextures=true, and continued completed captures. No VUID/Validation Error or output-binding rejection matched the run's Vulkan/rendering logs at this checkpoint.

RenderDoc `probe-exact-writers.rdc`, frame1026, at final event841: the 64x64 irradiance texture ResourceId30641 had RGB maximum 0.185546875 and mean 0.08176315824; the 128x128 prefilter texture ResourceId30673 had maximum 0.7998046875 and mean 0.07963695568. Both PNG exports were actually inspected: blurred color in irradiance and scene-colored octahedral prefilter content. The in-progress source cube also contained nonzero face RGB. These replace the prior black-RGB failure evidence; neither mere publication nor a terminal clear was used as writer proof.

After fixing the camera/light view and disabling further realtime capture, MCP HDRSceneTex comparisons at 1920x1080 were finite (6,220,800 finite RGB samples, zero nonfinite samples; alpha exactly1):

| Provider state | RGB max | RGB mean | Raw RGBA SHA256 |
|---|---:|---:|---|
| LightProbesAndIbl enabled | 0.20788574 | 0.019079195 | D25E1A1CCFFD3351D619663D70B15E109E05FB52F2C9952BFE4A6136F60C3860 |
| None | 0.17565918 | 0.018686175 | CD66B5E0CAC86E3D34B1F03804E360872B1258FC0406ED99DF5BDDA642C8D278 |
| LightProbesAndIbl with null provider | 0.17565918 | 0.018686175 | CD66B5E0CAC86E3D34B1F03804E360872B1258FC0406ED99DF5BDDA642C8D278 |

NativeOpaqueShading was BackendEnqueueAccepted in the None and null observations (frames3854/4530); completed texture readbacks establish fresh GPU output. None/null equality and the GI-on difference establish the disabled contribution path instead of stale output. This is a bounded static-scene comparison, not full ARP-V16/V24 closure: probe movement/update contribution, other provider modes, direct/emissive isolation, and combined AO behavior still need their own captures.

Evidence under `Build/_AgentValidation/20260906-123802-vulkan-xr-advanced`: `reports/probe-exact-rgb.json`, `renderdoc/probe-exact-irradiance.png`, `renderdoc/probe-exact-prefilter.png`, and `mcp-output/gi-on-hdr.json`, `gi-none-hdr.json`, `gi-null-hdr.json` plus their diagnostic snapshots. RenderDoc session closed after inspection. The named editor remains owned by this task for subsequent validation; live state currently has realtime probe refresh disabled and GI provider null.

## 2026-09-06 EXR export correction in progress

ARP-I44 tracks a diagnostic export defect discovered while comparing GI/AO per pixel. Different raw readbacks/hashes produced byte-identical EXR files because `WriteExr` imported normalized radiance/alpha through the typed Q16 quantum overload and assigned RGB color space after import. The old EXR artifacts are not valid pixel-level evidence. Raw MCP statistics and SHA256 values are computed before export and remain valid; RenderDoc texture reads/exports and directly inspected viewport PNGs are independent of this defect.

The correction scales the copied readback by `Quantum.Max` (the typed float overload requires Quantum storage) and establishes linear RGB before importing pixels. A bounded Magick.NET round-trip in the task scratch analysis tool now reads normalized [0.020004272, 0.19995117, 2, 1] from source [0.02, 0.2, 2, 1], within EXR half precision and preserving HDR >1. Direct StorageType.Float with the typed float-array overload was rejected by the library and was not retained. The combined editor build awaits unrelated strict-blit compile fixes; runtime EXR recapture remains required.

PID32888 raw AO/GI comparison: enabled AO RGB mean0.019079195/max0.20788574; disabled AO mean0.019178944/max0.2088623. Disabled AO texture is uniformly1. With GI null, toggling AO left raw HDR hash CD66B5E0CAC86E3D34B1F03804E360872B1258FC0406ED99DF5BDDA642C8D278 unchanged. These are raw readback observations, not accepted EXR file comparisons. ARP-V24 remains open pending corrected captures and the remaining contribution checks.

## 2026-09-06 EXR export and AO/IBL contribution acceptance

ARP-I44 and ARP-V24 are closed for the Vulkan mono built-in AO + LightProbesAndIbl combination. Revision is `8b104bf7a` plus the current working changes. `build-strict-offscreen-exr-2.log` passes in 31.75 seconds with zero warnings/errors. Named editor `xr-advanced-0906`, PID17396, runs the authored static red/green/gray fixture at 1920x1080 with Vulkan validation and synchronization validation enabled. The probe had CaptureVersion47 and usable IBL before realtime refresh was disabled for the comparisons. The shader adds diffuse indirect multiplied by AO and specular indirect multiplied by its roughness-aware specular-occlusion function exactly once; direct and emissive terms are outside those operations.

Corrected EXRs decode to the raw readback extrema and means: AO on max0.20788574/mean0.0190791960267, AO off max0.2088623/mean0.0191789443622, alpha1. A pixel comparison finds 215,849 changed pixels, 641,363 RGB samples darker by more than1e-6, zero brightened samples, zero nonfinite samples, and min difference-0.0152587890625. This is the expected indirect occlusion behavior; the diffuse and specular models do not require a uniform linear AO ratio. The earlier incorrectly exported EXR files remain excluded from pixel-level evidence.

With GI None and Red.Emission=2, AO on and off both yield raw RGBA SHA256 `6C66660F53D2BF6D2AD9C83929E9C59E8F52BBF74AB2342E21A4F4244EDF0DC1`, RGB max1.7753906/mean0.03258633, alpha1, and all6,220,800 RGB samples finite. This isolates nonzero direct plus emissive output and confirms AO does not attenuate it. The viewport screenshot was inspected and shows the bright emissive red panel, green panel, and gray ground. The earlier scratch EXR round-trip also preserves source radiance2 exactly and source0.02/0.2 within half precision.

Evidence under `Build/_AgentValidation/20260906-123802-vulkan-xr-advanced`: `mcp-output/exr-corrected-ao-on.json`, `exr-corrected-ao-off.json`, `exr-emissive-direct-ao-on.json`, `exr-emissive-direct-ao-off.json`; `reports/exr-corrected-ao-comparison.jsonl`, `exr-emissive-direct-ao-comparison.jsonl`, `exr-roundtrip.txt`; corrected EXRs stamped173235/173252/173401/173418; inspected screenshot `Screenshot_20260906_173441_153_b06c2fc34d6a4ea882814ed6c3fb6482.png`. This acceptance does not close probe-update switching ARP-V16, other GI-provider profiles, or stereo acceptance.

## 2026-09-06 probe position refresh checkpoint

PID17396 exercised runtime probe refresh with the selected LightProbesAndIbl provider. After the completed probe at(-0.5,2,1.5) reached CaptureVersion89, it moved to(1.5,1,0.5) and realtime refresh advanced to CaptureVersion108 with usable IBL. The static camera and direct/emissive scene remained unchanged. GI-on output changed from raw hash7EF5FD187C3D0D57D511263E73C50C416FB0CD8781376658CFA72F8CB02E6909 (mean0.032977544/max1.8076172) to EEA89CF7C28ABB4427EE2127BC9F5806FD8C8D7534DE0E294589A522EB7DD535 (mean0.033123497/max1.7998047). Switching back to None recovered exactly the earlier direct/emissive hash6C66660F53D2BF6D2AD9C83929E9C59E8F52BBF74AB2342E21A4F4244EDF0DC1. All captured RGB samples are finite, alpha1. No VUID/Validation Error matched this runtime's Vulkan log. Evidence: `mcp-output/probe-before-emissive-refresh.json`, `probe-after-position-refresh.json`, `probe-position-refresh-gi-none.json` under the existing task run. This demonstrates completed refresh and provider isolation, but moving the probe also changes its spatial influence; ARP-V16 remains open until updated source-texture contribution is isolated at a fixed final probe transform.

Changing only the authored Emission scalar increased desktop native HDR but did not change the Default capture-derived IBL after repeated refresh. The explicit Advanced probe capture path is being connected to render native direct/emissive radiance; it deliberately disables capture-time GI to prevent recursively sampling its own previous IBL. This is implementation in progress under ARP-I23, not a validation closure.

## 2026-09-06 multiple Advanced output owners in progress

ARP-I45 is a prerequisite discovered by executing the explicit Advanced probe owner, not a runtime acceptance. PID47472 refused output9 because Vulkan admitted one sticky mono output per renderer generation. Safe expansion requires more than changing the reservation guard: each camera/output needs independent persistent occlusion state, immutable frame-slot descriptors, retained input family, and stable-bin geometry/submission closure. The implementation now has a fixed eight-bank bound, independent 16MiB persistent buffers per initialized bank, per-bank 2MiB transient allocation limits backed by one aggregate16MiB lane per frame slot, full reservation identity on visibility state, per-family inputs/bins, and primary preparation grouped by the exact reservation. Shared lane allocation/rollback, quarantine and publication scratch remain serialized. Programs/pipelines and immutable multi-publication scene resources remain shared. Required capacity failures remain visible. Safe owner release/recycling is still unfinished; this is currently a bounded renderer-generation lifetime reservation.

Review caught a second loop that associated every output with the current bank; exact reservation filtering and association checks now cover both preparation loops. Review also caught repeated lane reservation in each new bank: the arena rejects a second reservation even with equal capacity. PID19668 confirmed that failure and was stopped. Shared initialization now records the successful arena identity/generation and checks every active frame-slot group, native buffer and reserved capacity before subsequent bank initialization. `build-multi-output-banks-4.log` was a clean11.64-second build before that last reservation-proof correction; the corrected combined build/runtime is pending. No multi-output validation box is closed by these builds.

The explicit SceneCapture/LightProbe opt-in is `UseAdvancedCapturePipeline`. Its resource/profile transition uses an Advanced HDR offscreen intent and the viewport's own output reservation; raw per-component/face identity remains in exact completion receipts. Capture-time GI is disabled to prevent recursive IBL accumulation. A separate history fix makes depth-pyramid reuse depend on the immutable view descriptor's accepted temporal history, so the shared progressive cube viewport cannot certify the previous face as temporal history. ARP-I46 records the build-validated fix; ARP-I23/I45 and runtime probe-profile acceptance remain open.

## 2026-09-06 final wrap-up checkpoint

Work paused at the user's request. This checkpoint supersedes earlier present-tense “in progress”, “pending build”, and “editor remains running” statements above; those remain chronological evidence. HEAD is still `8b104bf7a`; current implementation is uncommitted. Final isolated editor build `build-wrap-up-final.log` succeeded in31.97 seconds with zero warnings/errors. It includes the latest multi-output, picking, RVC profile and MCP fixes. No final-build runtime was launched; no automated tests were added or run. Named editor `xr-advanced-0906` is stopped (stop result has State=Stopped, ProcessId=null, McpReady=false); RenderDoc is closed. Other user sessions and unrelated changes were preserved.

### Last multi-output failure and subsequent source corrections

The storage-proof build passed with zero warnings/errors, but desktop plus Advanced probe PID41680 paused at frame212: `Advanced visibility operation is Unsupported: stable-bin sealing failed: capture outputs require an independent capture family`. CaptureVersion14/HasUsableIbl was retained Default output and does not establish Advanced capture. The renderer then repeatedly failed without useful progress; the owned session was stopped. Evidence is `mcp-output/multi-output-proof-properties.json`, `multi-output-proof-render-state.json` and `logs/stop-multi-output-storage-proof.log` under `Build/_AgentValidation/20260906-123802-vulkan-xr-advanced`. The render-state request itself failed with concurrent collection enumeration. Approximate process memory before stop: working set6,261,276,672 bytes, private9,398,697,984 bytes.

The final source fixes the capture guard using an exact `AdvancedVisibilityFamilyReservation` in `VulkanSubmissionOutputPolicy`, and requires equality with the recorded request. Ordinary unreserved special outputs retain their restrictions. Review also found an inverted reservation/storage lock order: reservation now takes the shared storage gate before its own gate and reads the arena generation inside. Both family preparation loops filter the exact reservation. Repeated storage initialization validates the already-reserved arena/generation and every frame-slot group instead of attempting to reserve the lane twice.

The initial eight-bank CPU implementation eagerly allocated eight worst-case input/bin workspaces in every normal and retired plan slot. The final source keeps eight logical reference slots but constructs each bank's CPU storage only at output activation. A cached narrow activation callback provisions all normal/retired slots before publishing the reservation; references use volatile publication/read, non-null workspaces are never replaced, and existing sealed contents remain untouched. Repeated reservation does not allocate another workspace. This removes the unconditional eight-family startup multiplication; actual active-bank memory and failure handling still require measurement. Each active bank still has a substantial worst-case budget.

The compiled independent-bank portion is ARP-I47. ARP-I45 now owns the remaining completion-safe release/reuse, reassignment identity, capacity/allocation diagnostics and memory verification. Reservations currently remain sticky until renderer-generation retirement. No successful multi-output runtime is claimed for these corrections. Resume with the exact desktop-plus-probe cohort before attempting to certify RVC eyes or another offscreen profile.

### Other final source boundaries

ARP-I24 now contains asynchronous GL PBO/fence and Vulkan bounded staging/fence picking, retained canonical publication/identity resolution, exact accepted visibility-source publication, frozen managed render-owner sidecars, Advanced editor hover/click and read-only MCP `query_advanced_pick`. Stale generation/world delivery is rejected. The missing MCP attribute namespace was fixed in the final build. No live pick/selection result was collected; ARP-V37 remains open. OpenXR picking remains explicitly unavailable without an accepted XR source receipt. `get_render_state` now dispatches its complete collection snapshot through MCP Main thread affinity; this build fix has not been runtime rechecked.

ARP-I22 has a built RVC-owned two-pass Advanced family on the same outer pipeline instance, with exactly one cached preparation-acquire command. The minimal eye resource/command profile retains native HDR/depth-stencil and post/final color composition plus cleared1x1 neutral optional inputs. It excludes late transparency, temporal/history, AA/TSR, bloom, motion blur, DoF, atmosphere/fog and UI. This is partial profile integration, not full Advanced XR parity. Separate-eye motion/occlusion, pose/late-latch/deadline behavior and resource use remain unobserved under ARP-V35. Layered execution, OpenGL SPS and foveation still require implementation under ARP-I21/I26/I27.

ARP-I23 has strict offscreen intent/admission, typed exact exports and required-completion propagation plus explicit SceneCapture/LightProbe opt-in. Advanced probe execution still needs the above runtime rerun. Owned mirror/portal/thumbnail consumers and profile completeness remain unfinished; ownerless mirror/portal requests are explicitly rejected. Depth/visibility-only requests still run the full native family, so output-specific minimal work is not complete. ARP-I25 has real diagnostics and working typed captures, but the full counter inventory and per-eye timing/capture attribution remain open.

The latest completed runtime result remains ARP-V24's mono AO/IBL/direct/emissive comparison documented above. ARP-V16 still needs a fixed-transform updated source-texture comparison; probe movement also changes influence and cannot substitute for it. All unchecked visibility/material/shadow, motion/reset, late/post, XR pressure/lifecycle/timing, profile and hardware rows in the active TODO remain unfinished. Do not infer closure from this clean build.

Automatic approval review blocked cleanup of the mistyped scratch build directory `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260906-124103-xr-advanced0906` with reason “blocked by policy”; it remains in place. No cleanup retry was made.

## 2026-09-07 resumption and launch prerequisite

User requested continued implementation to completion. HEAD remains8b104bf7a with the previous working changes preserved. `Manage-McpEditorSession.ps1 Start -Name xr-advanced-0906 -NoBuild -SessionEnvironmentFile .../scratch/renderdoc-environment.json` was refused by `Assert-SessionFreeSpace` before process creation: only4.66GiB available on D: after the script's automatic retention cleanup, versus its required10GiB. Later readbacks fell to about2GiB. The user was asked to free at least6GiB while independent source work continues. No runtime result or new completion checkbox follows from this attempted launch. Evidence: `logs/start-resume-0907.log` under the existing task run. The previously blocked mistyped directory cleanup was not retried.

Source work now covers minimal depth/visibility-only command/resource/native-closure profiles, full-family AO/IBL request consistency, output-bank diagnostics, and completion-safe reservation reuse. The reuse implementation must retain retiring owners until sealed plans, recorded command buffers and actual GPU submission watermarks all settle. Merely dropping a viewport binding or observing zero plan pins is insufficient. The independent broker design review used requested and actual `gpt-5.6-sol`, run41d775b12e2f426db7634d4c803be7e8, completed successfully with no editor/repository tools; its selected source evidence does not substitute for local build/runtime validation. It identified ABA incarnation requirements, generation-reset hazards and lock-order constraints; these are being integrated into the actual submission/completion authorities.

### Historical September 7 source checkpoint — before Build 5

At that time D: subsequently fell below 1 GiB free (805,244,928 bytes
observed), so the named editor remained stopped and the clean September 6 build
predated the source changes in this section. This historical blocker was
resolved before the Build 5 checkpoint below; it is not a current build or
runtime constraint.

- ARP-I45 source now contains bank incarnations and Active/Retiring/Free states,
  sealed-plan ownership, a preallocated 128-entry recorded-command lease ledger,
  completion watermarks from both actual submission publishers, and full-token
  resource reassignment resets. Viewport binding clear/replacement releases the
  bank through its original renderer host. Review corrections cover identical
  tokens on different renderer hosts, partial activation allocation accounting,
  command-pool reset, deferred command-buffer retirement and native-free handle
  clearing before fallible bookkeeping. Consequential review and build/runtime
  acceptance remain required before closing the task.
- ARP-I23 now selects a visibility-only stage/resource profile for explicit
  depth/visibility outputs and avoids the native shading closure. It rejects
  incompatible post/temporal options. Phase flags remain consistent across the
  admitted family. This source has not been built or exercised. Existing mirror
  capture still owns a Default pipeline; portal and GPU thumbnail consumers
  still need actual owners and completion contracts.
- ARP-I25 adds a Main-thread profile snapshot, RVC family resolution through the
  real renderer host, current-generation stage attribution and bank diagnostics.
  Allocation bytes measure managed allocations on the activation thread, not
  VRAM or whole-process memory. Full counter/per-eye coverage remains open.
- The primary visibility recorder no longer demands a desktop picking receipt
  for OpenXR eye/mirror recording without an accepted desktop frame plan.
  OpenXR picking remains explicitly unavailable until an accepted XR source
  receipt exists. This guard correction is unbuilt.
- ARP-A07 source tracing is complete in
  [the visibility execution inventory](../../progress/rendering/advanced-visibility-execution-audit-2026-09-07.md).
  Full mono Vulkan has actual early/late indirect-count consumers and
  depth-pyramid/retest dispatches. ARP-V55 remains open. The separate
  [special-effects/upscaler inventory](../../progress/rendering/advanced-special-effects-and-upscaler-audit-2026-09-07.md)
  records remaining admission/consumer gaps; ARP-A03/A04 remain open.

The next runtime pass is a rebuild with the sealed-planner generation fix, then
a desktop-plus-Advanced-probe rerun. Follow it with bank owner churn and
delayed-completion/capacity recovery, inspecting diagnostics, captures and
validation logs before layered/XR/profile acceptance. No tests were added or
run, and no commit was created.

Lifetime review remains incomplete after the recorded corrections. In particular,
verify the policy for an already leased, sealed plan that begins recording after
its viewport owner retires: recorded-bank acquisition currently requires an
Active bank. It must either consume its protected Retiring incarnation safely or
use an explicit, coordinated cancellation path; a plan lease alone does not
establish that behavior. Also retain the delayed-submission/reset and device-loss
cases in the runtime acceptance cohort.

## Historical 2026-09-07 Build 5 and runtime checkpoint

**Superseded by the Build 9 checkpoint at the start of this document.** The
open source boundaries and pending rerun described below were resolved or
reclassified there; retain this section only as the record of the earlier
planner-generation failure.

The earlier disk-space blocker is resolved (approximately 141 GiB free). Build
5 completed with zero warnings and zero errors. It includes the current output
bank lifetime work, profile-specific offscreen assembly, diagnostic readback
work, and per-view Vulkan work. Fifteen Vulkan shader variants, including true
multiview paths, validated in that build. Build 6 has OpenGL scaffolding errors
under active repair and is not evidence against the completed Build 5 check.

ARP-I21 has compiled per-view native dispatch/target closures, view-addressed
counters, and the 928-byte foveation ABI. This is substantial source evidence,
but the row remains open until the remaining full per-eye source contract is
reviewed and the layered runtime matrix is observed. ARP-I22 has compiled full
eye-feature source under review. ARP-I26 remains active executor work; ARP-I27
still needs the foveated derivative/LOD consumer path.

ARP-I23 now routes explicit depth/visibility-only intents through the minimal
stage/resource profile and omits the native shading closure. Incompatible
dependencies are rejected. SceneCapture and LightProbe remain the built
Advanced opt-in owners; mirror/portal/thumbnail ownership and completion
integration are still absent, so the row remains open.

ARP-I25 now has completion-gated Vulkan GPU receipts with identity sealed at
producer copy scheduling: visibility is copied by the existing
`AdvancedVisibilityDiagnosticCopy` after late compute at `viewIndex * 152`
bytes and published as a raw 38-word per-view row; native classification is an
8-word aggregate across views; native lighting is a 4-word per-view row.
The sidecar reserves bounded staging capacity, waits for the primary completion
timeline before mapping, and drops saturated diagnostic requests without
changing rendering. Shading and reconstruction do not yet expose actual
backend-produced counter buffers, so they are not reported as completed
counters. Receipt-to-capture/timing correlation remains ARP-V38.

ARP-I45 compiled its Active/Retiring/Free bank states, incarnation identities,
plan/recorded-command leases, submission watermarks and reassignment resets.
A cold activation diagnostic reported one active bank, incarnation 2, and
237,267,072 allocated bytes. This is a bounded allocation observation only;
practical memory use and owner churn remain open under ARP-V48.

The current `xr-advanced-0906` session (PID37632) is stopped. Desktop Advanced
was admitted, but enabling the Advanced probe failed at frame 587 with a sealed
late-closure generation mismatch. The explicit sealed-planner generation fix
is pending rebuild and rerun. This failure does not prove probe completion or
close any profile/runtime validation row.

## 2026-09-07 validation resumption: OpenGL final stereo output

Baseline is `8b104bf7a` plus the working changes. Build 42 of the isolated
editor passed with zero warnings/errors (4.60 seconds). Session
`xr-advanced-0906`, PID33532, ran from 20:55:44 to approximately 20:58 local
time on OpenGL/RTX 3090, Advanced, emulated SinglePassStereo, explicitly
`GpuIndirectZeroReadback`, at 1920x1080 per eye. It is stopped. This is a
two-minute interactive cohort, not a retained-frame timing benchmark.

ARP-I49 fixed output RGB8 storage/view agreement, mip bounds and array color
attachment metadata, including bloom intermediates. ARP-I50 fixed the second
eye being absent from fullscreen bloom/post/FXAA output: explicitly Advanced
OVR framebuffers now select an OVR fullscreen vertex shader and mesh version,
including emulated VR where `IsInVR` is false. Legacy NVIDIA/default selection
is preserved. Intermediate builds exposed the FXAA-owned quad separately;
the final cohort includes its fix.

The fresh 288-line OpenGL log contains zero High/Error/INVALID/incomplete
messages, including teardown. Six raw PNG captures inspect final post/FXAA
layers at two camera positions with auto exposure disabled, exposure 1 and
ACES selected through the camera's actual pipeline stage state. All have
6,220,800 finite RGB samples, zero nonfinite samples, and alpha exactly 1.
The first pose has maximum RGB 0.2619629; FXAA eye means are 0.027987820 and
0.027985621. The second pose changes them to 0.015981723 and 0.016009994.
Inspected images contain the expected red/green panels and gray ground with
distinct eye parallax and changed perspective. Final and FXAA layers are
populated for both eyes. At frozen frame 21608, output 11 generation 3 carries
view/layer 0 and 1 with independent history keys 6360580617985672788 and
6360580618086467668, both `TemporalHistoryValid` and matching the output.

This closes ARP-I49/I50 and ARP-V45 for this admitted two-eye emulated OpenGL
profile. It does not close headset/XR timing, Vulkan layered execution,
textured/skinned reconstruction, every post effect, foveation or resets.
Supporting artifacts live under the existing ignored run
`Build/_AgentValidation/20260906-123802-vulkan-xr-advanced/`: `logs/gl29-opengl.log`,
`logs/build-validation-42.log`, `mcp-output/gl29-*.json`, and
`mcp-captures/gl29-*`. The observations above are the durable closure evidence.

Validation also found ARP-I51: standard material layout/kernel/material
required-attribute masks were zero, suppressing UV/normal/tangent/color and
analytic derivative reconstruction. Its source fix and textured runtime
validation are in progress; solid-color stereo evidence cannot certify it.

The earlier Build 34 strict-SPS six-stage fault matrix did not reach its
successful-submission injection threshold during 12 cold frames. No injection
occurred; that cohort fails XR-V07 even though individual smoke runners exited
successfully. A warmed 128+180-frame rerun was refused before launch because
Monado PID13580 already exists without ownership in this runner's marker.
It was not stopped or adopted. XR-V07 remains open pending an owned service.

## 2026-09-07 grouped motion captures and temporal copy failure

Build 51 passed with zero warnings/errors (11.44 seconds). PID43824 ran the
OpenGL Advanced emulated stereo checker fixture from 21:42:29 to approximately
21:48 local time, at 1920x1080 per eye. The stopped cohort's OpenGL log has
zero High/API Error/GL_INVALID messages. This is a six-minute interactive
cohort, not a performance benchmark.

The existing MCP texture capture now accepts up to seven companion resources
and reads all of them inside one post-render callback. This prevents camera
motion from invalidating input/output comparisons across separate requests.
Four camera-motion pairs and three object-motion pairs captured native HDR,
the pre-filter `MotionBlur` copy, `Velocity` and final FXAA for each selected
eye. The existing `ProfileCameraMotionComponent` drove the VR playspace, then
the imported model root while the camera was stationary. No tests were added
or modified. The authored blur settings were shutter 2, threshold 0, maximum
64 samples and 32 pixels; DoF, bloom and AO were disabled.

Camera-motion frames change 40,428–53,209 pixels above 0.0002 relative to the
same-frame input when velocity is nonzero, with maximum HDR delta 0.03790283.
Two sampled zero-velocity frames preserve the entire input exactly. Across
every capture, zero-velocity pixels are unchanged. Object motion changes
28,547–48,472 pixels, maximum HDR delta 0.03344727, with finite nonzero
velocity in both eyes (maximum component magnitude 0.00596237).
Zero shutter restores the input hash exactly in each eye while the model
continues moving. Enabled/disabled final images were captured and inspected;
all raw samples are finite and final alpha is 1. This closes ARP-V40 for this
OpenGL stereo profile; it does not close the complete native history/reset
matrix or another backend's motion validation.

Readback of `DepthView` exposed an existing diagnostic gap: OpenGL accepted
only concrete 2D/array textures and requested RGBA for depth. Build 52 now
queries actual view mip extents and copies depth/stencil with scalar transfer
formats before expanding to diagnostic RGBA. In the fresh stereo run,
both depth views are finite in [0.91379094,1] with distinct eye hashes;
both stencil views are exactly zero. Alpha is 1. Build 52 passes with zero
warnings/errors (3.95 seconds). These results establish ARP-I54's source and
readback behavior, not a full diagnostic-mode acceptance matrix.

Enabling session-only `AntiAliasingModeOverride.Value=Taa` in that next run
then exposed ARP-I55: temporal current-color and history-color/depth/exposure
copies still call framebuffer blits on multiview attachments. Those calls are
invalid, and an ordinary layered blit also cannot certify every eye. The
later TAA failure is excluded from the earlier motion/readback acceptance.
V31/V33 remain open pending the layer-copy fix and a fresh runtime.

Artifacts under the existing ignored run: `logs/gl35-opengl.log`,
`reports/gl35-motion-comparison.json`, `reports/gl35-object-motion-comparison.json`,
`mcp-output/gl35-*`, `mcp-output/gl36-depth-*`, `mcp-output/gl36-stencil-*`.

## 2026-09-07 stereo scene filters and executable post bindings

ARP-I52 is implemented on `8b104bf7a` plus working changes. Advanced scene
filters now copy through the existing layered scene-copy quad, provision the
actual view count for DoF/motion-blur intermediates, preserve the DoF
`ColorSource` sampler ABI, select mono/array filter shaders and connect the
existing settings-upload callbacks. The shaders share their algorithm bodies;
literal sampler declarations remain in the wrappers because Vulkan's legacy
uniform rewriter must identify sampler types before macro expansion.

Build 50 passed with zero warnings/errors (28.25 seconds). All eight checks
passed for mono/stereo DoF and motion blur: OpenGL GLSL plus Vulkan 1.3 SPIR-V
using the engine's actual multiview and auto-uniform rewrites. This establishes
shader compilation, not Vulkan runtime acceptance.

ARP-V41 passes for the OpenGL/RTX 3090, Advanced GPU-indirect, emulated OVR
1920x1080-per-eye checker fixture. The Build 49 session compared focus distance
4/range 10 with distance 50/range 0.05, MaxCoC 32, bloom disabled, AO disabled,
fixed exposure 2, contrast 0 and ACES. In the checker ROI, left/right mean
edge magnitude changes from 0.0043665/0.0044966 unfiltered to
0.0027425/0.0027810 out of focus. Focused output remains within mean absolute
error 0.00004160/0.00004115 of the input. Inspected final images show sharp
versus blurred cells. Disabling DoF exactly restores the original raw HDR for
both eyes. All captured samples are finite and alpha is 1.

Build 50's fresh PID39376 cohort ran from 21:33:21 until approximately 21:41
local time. Its final shader wrappers reproduce both earlier blurred HDR
hashes exactly. This is an approximately eight-minute interactive cohort,
not a retained-frame benchmark. OpenGL reports no High/API Error/INVALID
messages during the DoF recheck. Motion-blur investigation also ran during
this cohort and is not certified by the DoF result.

ARP-I53 connects nine additional existing callbacks to their executable quads:
atmosphere scatter/reproject/upscale, volumetric fog scatter/reproject/upscale,
temporal accumulation, TSR and weighted-transparent resolve. Build 50 verifies
this source change. Their runtime rows V28/V30/V31/V33 remain open.

Durable limits: this closes I52, I53 and V41 only. V40 still needs camera and
object motion comparisons. Separate readbacks observed both finite nonzero
velocity and exact-zero frames; update/render sampling is a possible cause,
not yet a proven history defect. Same-frame grouped input/output readback is
being added to avoid comparing different camera poses. Supporting artifacts
under the existing validation run are `reports/gl33-dof-comparison.json`,
`mcp-output/gl33-*`, `mcp-output/gl34-*`, and the Build 50/compiler logs.

## 2026-09-07 textured stereo and post-process acceptance

Build 44 passes with zero warnings/errors (4.65 seconds); source is
`8b104bf7a` plus working changes. The isolated OpenGL/RTX 3090 session
PID38032 ran the two-eye 1920x1080 Advanced GPU-indirect checker fixture for
approximately nine minutes. It is stopped. Runtime material schemas were
recreated on restart with ARP-I51's nonzero common attribute requirements.
The two inspected checker captures contain the authored 8x8 pattern, separate
eye parallax, zero nonfinite samples and alpha 1. FXAA maxima are 0.67822266
and 0.6777344. Frozen frame 10444 has separate valid left/right histories.

**ARP-V42 — selected tone operators:** With bloom strength 0, AO disabled,
fixed exposure 10, neutral tint/HSV and contrast 0, linear HDR input has maximum
0.22387695. Contrast's authoring value 0 is neutral; it maps to multiplier
`((100 + Contrast) / 100)^2`. The initial comparison incorrectly assumed 1
was neutral; it was rerun with the correct setting. Raw EXR readback compared
Linear, Reinhard and ACES on 786,336 left / 786,274 right interior pixels
against independent numeric evaluation of the captured HDR input. Maximum
absolute errors are 0.00390625 (Linear) and less than 0.002449
(Reinhard/ACES), consistent with +/-0.5/255 shader dither plus half-float
quantization. Linear preserves exposed values above 1; the selected Reinhard
and ACES outputs remain in their expected range. All data is finite. This
closes these operators on OpenGL stereo, not every operator/backend.

**ARP-V43 — authored grading:** On the same ACES output, tint (1, 0.5, 0.25)
changes 800,962 / 800,901 pixels. Maximum channel-scaling error is 0.001586915,
within dither/half precision. Restoring tint (1,1,1) reproduces the full baseline
raw image exactly for each eye. The tinted output was visually inspected.

**ARP-V32 — bloom:** Threshold 5 produces exact zero RGB in both eyes at mip 1
(960x540) and mip 4 (120x67); lowering the threshold to 0.1 produces finite
nonzero output in both levels/eyes. Mip 1 maxima are 0.19238281 / 0.19250488;
mip 4 maxima are 0.06329346 / 0.06347656. The inspected mip image shows spread
around the bright checker cells. Enabling combine strength 1 brightens
818,824 / 818,431 pixels with zero darkened pixels at a 0.003 threshold;
17,910 / 17,584 brightened samples lie outside native geometry. HDR source
readback stays identical. Intermediate bloom alpha is 2 at accumulated mip 1
and 1 at mip 4; combine consumes RGB, and final alpha remains 1.

Logs contained zero OpenGL errors through these captures. Enabling DoF at
21:12:41 then exposed ARP-I52: an illegal multiview framebuffer blit plus mono
filter samplers. That later failed interval is excluded from the above
acceptance and remains a separate implementation/validation obligation.
Supporting artifacts are `mcp-output/gl30-*.json`, `mcp-captures/gl30-*`,
`reports/gl30-neutral-tonemap-comparison.json`, `gl30-grading-comparison.json`
and `gl30-bloom-comparison.json` under the existing ignored validation run.

## 2026-09-07 layered temporal copies, picking and editor gizmos

Revision: `8b104bf7a` plus working changes. Configuration: RTX 3090, OpenGL,
Advanced GPUIndirectZeroReadback, emulated OVR two-view output, 1920×1080 per
eye, textured checker fixture. No new unit tests were added or changed.

**I55 complete:** Builds 53–55 added layer-aware framebuffer copies. The backend
uses temporary single-layer read/draw framebuffers, validates actual attachment
names/mips/view offsets and framebuffer completeness, preserves destination draw
routing, checks transfer errors, and detaches temporary attachments after each
operation. Strict copies reject missing color routes; ordinary temporal copies
fail visibly before history publication. An independent Sol review identified
three routing/error-attribution concerns; Build 54 addressed all three.
Build 55 completed with zero warnings/errors. PID16264 (Start 38, approximately
22:05–22:09 local) produced identical same-callback HDR/history-color hashes and
depth/history-depth hashes in each eye. The approximately four-minute cohort,
including shutdown, had zero OpenGL high-severity/API errors. Evidence:
`gl38-taa-static-eye0/1.json`, `logs/gl38-opengl.log`, under the current validation
run. This establishes the copy behavior; it does not certify every temporal mode.

**I56 complete:** unavailable picking storage now fails before transfer without
generating an uninitialized texture name. The transfer uses validated native
names, checks actual extents, issues the texture-update barrier, checks GL errors,
and preserves the prior pixel-pack binding. Polling uses named-buffer readback.
The viewport no longer consults a thread-local/transient current renderer from
an off-render-thread request. Build 56 passed with zero warnings/errors.
PID46260 returned Ground, RedPanel and GreenPanel hits and background misses
for both views (eight requests, publications 1415–1417); view 7 was rejected
before transfer. This is bounded transfer/identity evidence, not all of V37.

**TAA reset evidence, V31 still open:** Build 56's camera cut changed both eye
reset generations from 1 to 2; both seeded generations then matched 2. A cut
capture contained zero history weight and subsequent captures returned weights
0.90527344–0.95996094. State snapshots are `gl39-taa-before-cut.json` and
`gl39-taa-after-cut.json`. Build 57 repeated the generation transition, but its
capture arrived after the zero-weight frame, so that capture cannot independently
prove the cut's transient weight. Complete stable/moving/reset acceptance still
requires the clean final cohort after the editor issue below is resolved.

**I57 remains open:** selecting an editor object in Build 56 exposed a mono/NV
vertex program drawing into the OVR two-view ForwardPassFBO. Build 57 makes
late/generated meshes select OVR from that target. It then exposed the next
stage mismatch: the gizmo's geometry shader lacks the matching view count.
PID41000 (Start 40) repeatedly logged `num_views` link failures after selecting
RedPanel. The final scene capture remained finite but did not show a working
gizmo. This failed run is not editor acceptance. The owned session was stopped
before further source work. Ordinary mono and selected two-eye gizmo output
must be checked after fixing complete stage compatibility.

Builds 58–59 tried authored multiview geometry variants. The compiler-stage
mismatch was addressed, but runtime capability inspection proved this RTX 3090
configuration does not expose `GL_EXT_multiview_tessellation_geometry_shader`.
The attempt was removed; no shared shader-source rewriting or unsupported
geometry fallback remains. Builds 60–62 replace the editor's line/arrow geometry
stage with static segment triangles expanded in the vertex shader. Mono, OVR and
NV variants share clipping, pixel-width and arrow-shape code. Six OpenGL and four
Vulkan vertex shader compiler checks passed; Build 62 passed with zero warnings
and errors in 22.20 seconds. The source change is not yet I57 acceptance: the
selected-object captures remain missing the expected visible gizmo.

I58 was found once the geometry-stage exception no longer prevented late replay.
The global opaque `GpuIndirectZeroReadback` override replaced the explicitly
filtered direct Advanced temporal lane and caused a repeated strategy exception.
The pass now preserves its declared filtering strategy and still rejects an
explicit incompatible GPU-dispatch request. Build 62 PID47724/Start 43
returned ready TAA histories for both
eyes with no repeated late strategy exception. Final retained-cohort verification
is still in progress; no broader late-motion acceptance is claimed.

RenderDoc investigation: `rdc doctor` passed (RenderDoc 1.44, Python 3.10.6).
An injected named session, PID46960, exposed unavailable bindless/Advanced shader
capabilities and rejected cached program binaries under the capture layer. Its
output was almost black and no usable frame capture was received. This is a
capture-tool limitation for this profile, not native acceptance or a substitute
renderer pass. The named injected editor was stopped. MCP matrix inspection is
being used to continue the gizmo investigation with the original native profile.

## 2026-09-07 material includes and mono-stereo gizmo acceptance

Build 64's read-only `get_transform_tool_state` showed a valid reference camera
at (0,1,4), root identity and billboard scale 0.23804761. A temporary one-shot
GL uniform readback in Build 65 then showed zero `MatColor` and `LineWidth`,
despite valid model/eye matrices and viewport dimensions. The material's raw
shader regex had removed explicitly authored parameters declared only in an
include. Build 66 resolved includes before discovery: the actual program then
contained red (1,0,0,1) and width 1.45, and both OVR eyes displayed the gizmo.

Independent Sol review `8f83a97190bd4cf1a3e0f07055a7ea88` completed with matching
requested/actual model. Its partial-load finding was integrated: parameter and
engine-requirement discovery now share one complete resolved stage snapshot,
preserve state if any configured stage is absent/unresolved, exclude ordinary
and vertex engine uniform names, and retain additive authored requirements.
This remains the existing declaration parser; it is not a GLSL preprocessor.

Mono validation exposed the split-program uniform interface: material values
were uploaded to the fragment program while screen-space expansion needs them
in the vertex stage. Gizmo renderers now explicitly use a combined program for
every variant. Build 67 passed with zero warnings/errors in 32.73 seconds;
PID48308 (Start 48, RTX3090, native OpenGL Advanced, forced
`GpuIndirectZeroReadback`, OVR emulated stereo) displayed axes and arrows in
mono and both 1920x1080 eye images. The temporary GL inspection file and call
were removed before this build. Ten prior GLSL checks cover the six GL and four
Vulkan-compatible gizmo vertex variants; Vulkan/NV runtime remains unverified.
Retained captures: `gl48-gizmo-mono`, `gl48-gizmo-eye0`, `gl48-gizmo-eye1` under
the existing `20260906-123802-vulkan-xr-advanced` validation root. Both eye images
and the mono image were visually inspected. ARP-I57 is closed for this profile.

Selected-object TAA also completes under the forced opaque GPU strategy,
with both history layers ready, proving the explicit filtered late lane is no
longer overridden (ARP-I58). During the final approximately four-minute
PID48308 cohort, OpenGL and rendering logs contain no API, shader-link or caught
render-command errors, including shutdown. Logs were copied as
`logs/gl48-log_opengl.log` and `logs/gl48-log_rendering.log`.

ARP-V31 evidence from the same cohort:

- Grouped `gl48-taa-final-eye0/1` captures have exact HDR/history-color hashes
  and exact current/history-depth hashes. Depth is finite, 0.9757976–1;
  stationary velocity is zero in both eyes. Input and accumulated HDR differ.
- Stable history-weight debug captures are finite with positive weight, up to
  0.95996094. The early cut requests already captured recovered frames; these
  alone are not evidence of the transient reset output.
- A subsequent AA-mode transition/cut sequence at a temporary requested 2 FPS
  captured exactly zero RGB history weight for each eye at the transitional
  1286x723 extent (`gl48-taa-slowcut-eye0/1`). Settled 1920x1080 captures recover
  weights 0.90527344–0.95996094. Final profile generation is 3, both reset and
  seeded generations are 6, and both histories/exposure are ready. The frame
  limit was restored to 60. This demonstrates reset versus accumulation;
  it does not isolate the transition's resize from its camera cut.
- Camera/object moving native velocity and filtering are separately retained
  in ARP-V40. V31 acceptance covers this OpenGL TAA profile only; the full
  motion/reset matrix and other backends retain their own unchecked rows.

Build 68 adds bounded MCP control/read-back of the camera TSR render scale,
with finite 0.5–1 validation and null to clear. It passes with zero warnings and
errors in 22.37 seconds. TSR acceptance is still in progress; source/settings
success alone is not an accepted resource generation or upscaled output.

## 2026-09-07 TSR history and transparent temporal producers

Continuation of the native OpenGL Advanced validation on `8b104bf7a` plus the working tree, RTX 3090, forced `GpuIndirectZeroReadback`, emulated OVR stereo. The owned session remains `xr-advanced-0906`; the user's editor and Monado PID13580 were not stopped.

- Build 68 first exercised TSR at 0.5 scale. Output and copied history were finite, but both histories stayed invalid: completion coverage reported color/depth `3` and TSR `0`. The Advanced chain copied TSR history without recording its coverage.
- Build 69 replaces the separate copy/mark operations with `CaptureTsrHistoryColor`: execute the actual blit first, record coverage only after it succeeds. Default uses the same command. This avoids certifying a failed copy after a command exception.
- Build 70 / PID51484 exercised scales 0.5 → 1.0 → 0.75. Internal extents were 960×540 → 1920×1080 → 1440×810; output stayed 1920×1080. Profile, reset and seeded generations advanced 2 → 3 → 4 for both eyes and both histories became ready. Same-callback native-scale TSR output/history hashes matched. The approximately three-minute cohort and shutdown logs have no GL API, shader/link, caught-command or recurring history-coverage error. `historyExposureReady=false` is expected for TSR: the TAA exposure-variance producer is not used. This closes I59, not the entire V33 matrix.
- Source tracing found that neither TAA nor TSR sampled the canonical Advanced reactive mask. Build 70 adds an explicit Advanced shader define, dependency and sampler binding for each resolve; shared Default variants retain their prior interface. Stereo debug modes now implement reactive/motion/confidence and history acceptance consistently with mono. All sixteen GL/Vulkan Default/Advanced temporal shader inputs compile.
- A positive colored-alpha producer exposed two more gaps: `CreateLitColorMaterial(deferred:false)` supplied deferred parameter names to a forward shader, and OpenGL's separate material resolver ignored `AdvancedLateTemporalOutput`. Build 73 corrects those paths, disables the conflicting generic motion-variant flag for reactive replay and combines reactive writes with MAX. Native background/opaque shading initializes the canonical mask before late producers.
- MCP `create_primitive_shape` now supports an explicit transparent fixture. Its previously documented color object had silently failed reflection-based conversion of unsafe `ColorF4`; a dedicated converter handles RGB(A) objects and #RRGGBB[AA], and invalid input returns an error before scene mutation. Live material read-back confirms `(1,0.2,0.05,0.5)`.
- Build 73 / PID38872: visually inspected transparent orange cube over the checker panels, with no selected gizmo. TAA marked 72,423/71,536 pixels, TSR 72,420/71,536. Every mask=1 pixel has debug red=1 exactly, in both eyes and both resolves. All output samples are finite. Setting the shared MatColor alpha to zero produces an exact-zero mask in both eyes; restoring alpha restores coverage. The stationary fixture's velocity is exactly zero. These close I60 for the exercised profile.
- The moving fixture uses the existing `ProfileCameraMotionComponent` attached to the cube; changing coverage confirms motion. **Its entire velocity image remains zero in both eyes**, including warmed captures. I61 and V26/V33 therefore remain open. Build 74 adds temporary bounded GL uniform inspection solely to diagnose this failure; remove it before a production checkpoint.
- Sol broker review `9699551bd5b04bc4af5e9fbe8777aeb1` completed with requested=actual `gpt-5.6-sol`. Reactive flag/MAX findings were implemented. Native Advanced opaque velocity is computed directly, so there is no earlier generic transparent replay to duplicate. Model history already keeps a read-only previous snapshot per viewport render sequence; reset behavior and material/deformation coverage still need validation. Ambient temporal lookup derives the active pipeline/viewport/camera key; other-profile deferred recording is not certified by this run.
- RenderDoc 1.44 doctor passes. Late injection into PID38872 and an instrumented manager restart (PID57420) attach but expose no capturable GL API context; capture requests return no capture. No GPU replay acceptance is claimed from those attempts. Continue with bounded in-process diagnostics.

Evidence under the existing ignored run root: `mcp-output/gl51-tsr-*-state.json`, `gl51-tsr-native-eye*.json`, `gl54-taa-reactive-eye*.json`, `gl54-tsr-reactive-eye*.json`, `gl54-taa-alpha0-eye*.json`, `gl54-tsr-moving*-eye*.json`, `reports/reactive54.json`, `mcp-captures/gl54-color-eye*/`, `logs/gl51-log_*.log`, `logs/gl54-log_*.log`, `logs/shader73-*.log`. Build 73: zero warnings/errors, 39.53 seconds; four colored-alpha GL/Vulkan shader checks pass. No unit tests were added or modified.

## 2026-09-08 transparent motion and render-thread ownership

This checkpoint supersedes the zero-velocity failure above. The source baseline
is still `8b104bf7a` plus working changes. Builds 77 and 80 run native OpenGL
Advanced on RTX 3090, forced opaque `GpuIndirectZeroReadback`, emulated OVR
stereo at 1920×1080, with an explicitly CPU-direct rigid transparent late lane.
Hardware XR, Vulkan layered rendering and NV stereo are not certified here.

**Transparent motion:** Build 75's temporary native uniform inspection confirmed
different current/previous model matrices, correct per-eye camera matrices and
the authored alpha. Current canonical and aliased depth hashes also matched.
The actual failure was the absent `VelocityFBO` resource declaration: binding
silently returned null, leaving the previous target active. Builds 76/77 declare
the framebuffer against canonical velocity/depth and include color-attachment
usage on the velocity texture. Replay now verifies its target before drawing.
Temporary inspection code was removed before Build 76.

- Build 77 / PID59868: both warmed moving-eye captures contain nonzero velocity
  on every marked pixel (72,478/71,420); pixels outside the mask are zero. The
  reactive debug channel remains exactly one on coverage. TSR debug mode 2
  agrees with `velocity.xy * 0.25 + 0.5`: maximum channel errors are
  0.00049019/0.00039911 over 68,832/67,922 interior pixels, within half-precision
  quantization. All interior pixels have non-neutral encoded motion.
- After waiting for accepted history extents, scale 0.5 → 0.75 → 1 produces
  960×540 → 1440×810 → 1920×1080 internal resources, constant 1920×1080 output,
  and paired profile/reset/seeded generations 6 → 7 → 8. Both histories recover.
  The first immediate scale read-backs can still describe the previous accepted
  frame; only the converged captures establish this result. Both half-scale
  output images were viewed and contain finite, distinct checker-panel views.
- The attempted slow-frame reset captures already contain recovered history
  weights (0.1599–0.95996). They do **not** prove the first invalid-history frame.
  The new per-eye history-ready shader guard suppresses both stale model and
  camera motion in source; direct moving-object reset evidence remains open.
- Build 80 / PID50868 rejects billboarding with a rate-limited diagnostic and
  zero mask in both eyes. Restoring rigid mode restores coverage and nonzero
  motion on every marked pixel (65,848/64,507). A source shader revision change
  also rejects replay and clears coverage. Replacing the shared parameter array
  reports stale coverage through `get_material_uniforms.advancedTemporal`.
  Positive RGB(A) and hexadecimal color creation/read-back succeed. One early
  right-eye moving capture was zero before warmup; only the warmed pairs above
  establish motion acceptance. Skin/morph, multiple instances and render-option
  overrides are explicitly outside this rigid factory's contract.

**Render-thread ownership:** Build 74 / PID60184 crashed with native access
violation in `GL.ClientWaitSync` while an HTTP settings request polled visibility
publication during viewport rebinding. `IsRendererActive` describes the calling
context; false on the worker was mistaken for permission to mutate GL resources.
Viewport rebinding, pipeline changes and instance transitions now use render
thread identity, with a zero-ID bootstrap allowance. Source tracing places the
only zero-ID assignment before window creation during dedicated-thread handoff;
normal render-loop shutdown does not clear it. Settings/preference/material
mutation tools declare main-thread affinity. Sol review
`72ed2eb03024492898dc3115927b0649` completed with requested=actual `gpt-5.6-sol`.

Build 80 survived six settings reapplications invoked from Caller/HTTP affinity,
then continued rendering, rejected the negative material cases above and shut
down cleanly. Build 77's roughly seven-minute scale/AA cohort also shut down
cleanly. Both copied GL/rendering logs contain zero API, shader/link,
caught-render-command or repeated history-coverage errors; expected coverage
rejection warnings are retained. This closes I62's observed failure, not the
separate delayed-submission or XR lifecycle matrices. Build 80 is clean with
zero warnings/errors (4.23 seconds); colored-alpha GL and Vulkan shader checks
also pass. No unit tests were added or modified.

Evidence remains under `Build/_AgentValidation/20260906-123802-vulkan-xr-advanced`:
`reports/gl58-reactive.json`, `reports/gl58-velocity-debug.json`,
`reports/gl59-reactive.json`, `reports/gl59-worker-refresh.jsonl`,
`mcp-output/gl58-tsr-scale-*-state.json`, `gl59-*-material.json`,
`gl59-replaced-parameters.json`, grouped `gl58-*`/`gl59-*` capture responses,
`logs/gl56-settings-crash.log`, `gl57-log_opengl.log`, `gl58-log_*.log`,
`gl59-log_*.log` and `shader77-*.log`. Owned session Start 59 was stopped;
Monado PID13580 and the user's editor were not stopped or adopted.

Start 60 / PID54248 (Build 80) subsequently captured the actual first cut frame.
VSync Off plus target render frequency 1 Hz was read back from `get_time_state`
with render delta exactly one second; the earlier requested FPS alone had not
established this timing. After a 2.2m playspace teleport, grouped captures show
TSR history weight exactly zero in both eyes. Every one of 15,892/16,316
transparent coverage pixels has zero velocity despite active object motion.
Recovered frames have nonzero velocity on all 13,676/13,805 transparent pixels
and history weight up to 0.95996094. The cap was restored to 60 and normal
output from both eyes was visually inspected. This closes I61 and bounded V33.
Evidence: `reports/gl60-reset-motion.json`, `mcp-output/gl60-time.json`,
`gl60-tsr-cut-eye*.json`, `gl60-tsr-recovered-moving-eye*.json`,
`gl60-recovered-state.json`, `mcp-captures/gl60-color-eye*/`.

That same reset capture exposes a separate I63 defect: 75,510/75,524 native
opaque pixels outside the transparent mask retain camera motion, up to
1.6796875 absolute NDC. Stationary opaque velocity recovers to exact zero.
Sol review `d4f7ca3d950a4c35929fd665fe467fe2` completed with requested=actual
`gpt-5.6-sol` and confirmed the cause: accepted native view ledgers recognize
explicit camera epochs but no geometric cut; post temporal accumulation detects
the 2m/55° discontinuity only after native views have been frozen. The fix in
progress shares the cut predicate and retains per-output accepted poses in the
desktop/GL stereo ledger and OpenXR committed view history. It does not mutate
camera epochs or advance rejected candidates. Direct runtime acceptance of this
fix remains pending. The broker's first source-packet request was rejected
before starting because `Build/` is excluded; the successful packet omits that
file. No provider or model substitution occurred.

## 2026-09-08 native cut and sorted alpha acceptance

Build 81 is clean (zero warnings/errors, 63.86 seconds). Its shared
`RenderFrameViewHistoryPolicy` compares frozen position/forward against the
history owner's last accepted pose. Desktop and GL stereo use the existing
ledger; OpenXR stores fixed-size committed pose arrays and updates them only
at successful history commit. Invalid pose data fails history closed. The post
temporal pass uses the same 2m/55° predicate; no camera epoch or backend flag is
patched after capture. Other reasons for native/post history disagreement still
belong to V18/V19; this change addresses geometric cuts.

Start 61 / PID48200, RTX3090 native OpenGL OVR, Build 81:

- Separate 2.2m translation and 60° yaw cuts at confirmed 1Hz/VSync Off produce
  exact-zero whole-image velocity and TSR history weight in both eyes. Positive
  transparent coverage is 91,828/92,589 pixels on translation and
  320,558/350,926 on rotation; this is not an empty-scene zero check.
- Recovered frames contain nonzero motion on all 32,219/31,543 transparent
  pixels, exact-zero stationary opaque/background velocity and history weight
  up to 0.95996094. I63 is closed for this observed OpenGL profile. Mono/OpenXR
  execution, threshold boundaries and rejected-candidate matrices remain open.
- The later TSR→FXAA capture attempt produced GL_INVALID_OPERATION/ENUM in
  `TryReadTextureMipRgbaFloat` before reporting unavailable mip/layer storage.
  It queried an API wrapper's name without checking that it was a live GL
  texture. This does not invalidate the earlier cut images, but the complete
  cohort is **not** a clean GL run. I65 adds a liveness guard before mip queries.

The same session tested two overlapping 50%-alpha rigid cubes against individual
red-only, green-only and background HDR references. With red nearer the camera,
the combined image matched the *reversed* blend within 0.00006104, while correct
composition differed by up to 0.02002. Advanced's numeric stage registration had
left the transparent material bucket unsorted. Build 82 assigns its existing
far-to-near snapshot sorter; stage execution remains command-chain controlled.

Build 82 is clean (zero warnings/errors, 31.12 seconds). Start 62 / PID58000 uses
the same native OpenGL OVR configuration. Across 63,931 interior overlap pixels
per eye, both red-near and green-near cases now match
`frontOnly + 0.5 * (backOnly - background)` within 0.00006104; the wrong order
differs by 0.02002. Reversing depth preserves this result without recreating the
materials. I64 is closed. This covers a common ordering for both eyes; exact
transparency profiles requiring contradictory per-eye order are not certified.

With camera motion plus independent rigid-object motion and TSR restored, every
covered transparent pixel has nonzero velocity (128,694/124,365), as does every
uncovered native opaque pixel (653,975/652,326). Outside geometry velocity is
exact zero. All samples are finite, with maximum absolute motion
0.00088835/0.00158405. The canonical reactive mask and velocity coexist, and
both final eye PNGs were exported; the left image was visually inspected.
Together with the prior reactive-consumption and reset proof this closes V26
for the built-in rigid colored-alpha lane.

Retained evidence: `reports/gl61-cut-motion.json`, `gl61-alpha-order.json`,
`gl62-order.json`, `gl62-reverse.json`, `gl62-merged-motion.json`, grouped
`mcp-output/gl61-*-cut-eye*.json`, `gl62-order-*.json`, `gl62-reverse-*.json`,
`gl62-merged-motion-eye*.json`, `gl62-merged-state.json`, output PNGs under
`mcp-captures/gl62-*color-eye*/`, and `logs/gl61-log_*.log`.

## 2026-09-08 capture liveness and mono scene filters

Build 83 passes with zero warnings/errors (6.26 seconds). The OpenGL capture
path now checks `gl.IsTexture` before querying mip storage. Start 63 / PID62164,
native OpenGL Advanced, completes eight HDR/TSR-history exports across four
TSR/FXAA transitions. Four unallocated `AtmosphereHalfTemporal` exports fail
explicitly with `Texture storage is not live in the current OpenGL context.`
No invalid GL query is issued. `reports/gl63-capture-matrix.jsonl` records every
outcome. Copied GL/rendering logs through owned-session shutdown have no
API/link/caught-command errors. The prior Start 62 cohort also has no such
errors. I65 is closed; export success for retained inactive TSR history does
not claim that FXAA updates that history.

Scene-filter validation moved to the admitted **mono** Advanced viewport because
the current atmosphere/fog profile excludes stereo. Adding an atmosphere
component yields finite aerial perspective (RGB up to 0.00034475,
transmittance 0.99902344–1 at this short range); disabling it gives zero
contribution. The normal mono post image was viewed. Full sky and atmosphere
history/reset acceptance remain open.

The initial fog fixture appeared neutral. Debug output established that scatter,
upscale and composite executed, but every ray missed the volume. MCP component
mutation had accepted `{X:5,Y:3,Z:5}` through property-only JSON deserialization,
silently producing a zero vector; the component then clamped it to minimum
extent. The getter also serialized the vector as `{}`. This is I66, a validation
tool defect, not accepted negative fog evidence. Dedicated converters for
Vector2/3/4, Quaternion and Matrix4x4 now read finite complete float components
and emit those fields in responses. Build/runtime validation is pending.
Evidence: `mcp-output/gl63-atmosphere-*.json`, `gl63-fog-*.json`,
`gl63-fog-debug*.json`, `gl63-mono-state.json`, and `logs/gl63-log_*.log`.

Build 84 passes with zero warnings/errors (12.32 seconds). Start 64 / PID47908
reads fog half-extents back as exactly (5,3,5). A missing-Z object is rejected
and read-back proves no mutation. Correct geometry produces finite fog, closing
I66's observed Vector3 failure; the shared converters also define complete
Vector2/4, Quaternion and Matrix4x4 serialization without claiming a live case
for each type. No automated tests were added or modified.

V30 acceptance is native OpenGL **mono**, RTX3090, approximately eleven-minute
owned cohort. Fog uses half-extents (5,3,5), density 0.2 and noise amount 0.
Enabled versus disabled post output differs by mean 0.05194, while native HDR
is exactly unchanged. Full-resolution fog transmittance spans 0.1918–0.8267.
At 960×540, temporal/history copies match exactly; settled temporal versus
current scatter differs by mean 0.004935. On a 2.2m camera cut at 1Hz/VSync Off,
temporal equals current scatter exactly and history equals temporal exactly.
Viewed images at z=4 and z=6.2 show fog following the scene; the frame cap was
restored to 60. Evidence: `reports/gl64-fog-history.json`,
`mcp-output/gl64-fog-settled-*.json`, `gl64-fog-cut.json`, `gl64-half-extents.json`,
`gl64-invalid-vector.json`, and `mcp-captures/gl64-fog-*-color/`.

Atmospheric aerial perspective uses the default ground atmosphere with sun
intensity 2000 to make its short-range contribution measurable. It is finite
(RGB up to 0.04260, transmittance 0.9990–1) and changes post output by mean
0.004115 while leaving native HDR exactly unchanged. The cut capture has exact
temporal/current/history equality. Three captures during a three-second camera
move have identical temporal/history copies but differ from current scatter
by maxima 0.0234–0.0318, proving active history accumulation. Images before and
after the view change were viewed. Evidence: `reports/gl64-atmo-history.json`,
`gl64-atmo-moving.json`, `mcp-output/gl64-atmo-*.json`, and viewed PNGs under
`mcp-captures/gl64-atmo-color/` and `gl64-atmo-camera2/`.

This closes V30 for the admitted mono aerial-perspective/fog profile. It does
not certify stereo (explicitly excluded), Vulkan, or sky output. The latter
exposes I67/V25: the atmosphere component authors a Background mesh, but the
Advanced chain has no draw command for that bucket. Native background compute
initializes sentinel color/sidecars but does not execute the authored sky.
The authored sky lane is the next implementation/validation task.

## 2026-09-08 authored background admission and depth preservation

ARP-I67 now has an executable authored Background mesh lane immediately after
native opaque compute, before scene snapshots and late/post work. It loads the
canonical HDR/native-depth framebuffer. Explicit shader receipts, per-submesh
checks, alpha/depth/stencil/blend state validation and override rejection preserve
native identity. Built-in gradient/procedural/texture sky shaders have mono/OVR
profiles; texture mode requires a real texture. Atmosphere sky currently admits
mono only. Unsupported stereo atmosphere and stale shader receipts report their
reasons, also exposed by `get_material_uniforms.advancedBackground`.

The native sentinel initializer now writes alpha zero, independently of window
clear alpha. The final GL opaque-compute barrier includes framebuffer access.
Separate GL vertex programs now receive depth mode and clip-space policy: the
first reversed-depth capture revealed that camera matrices alone left the sky
vertex shader at normal far depth and allowed it over opaque color.

Build 88 passes with zero warnings/errors (29.48 s). Owned Start66/PID50596 ran
from 01:51 to approximately 01:57 PDT, using native OpenGL Advanced on RTX3090,
1920x1080 mono plus two emulated OVR eyes and forced opaque GPU zero-readback.
The final cohort has no OpenGL API, shader-binding/link or caught-command errors
through shutdown. Expected unsupported-profile warnings remain visible.

The `gl66-clear-*` versus gradient, HDR-gradient and custom-geometry captures
preserve alpha, native depth, velocity/reactive and **all** non-background color
exactly. Mono has 917,762 background and 1,155,838 non-background pixels; the two
eyes have 1,272,638/1,272,699 background and 800,962/800,901 non-background pixels.
The custom triangle varies local Z from -10 to +10 but uses the certified far-depth
shader; its changed shape affects only background coverage. HDR sky values reach
2.6992 mono and 3.15625 stereo without clamping in EXR. Enabling alpha writes or
invalidating the shader receipt yields output identical to the clear baseline.
Mono atmosphere sky contributes visible RGB while the unadmitted stereo variant
is rejected. Mono and both custom-eye images were inspected, as was the fixed
reversed-depth image.

For reversed mono depth, `gl66-reverse-clear-*` versus
`gl66-reverse-gradient-*` again preserves opaque color and every checked sidecar
exactly. Do not interpret this as normal/reversed reconstruction parity: the
separate `gl66-restored-*` versus `gl66-reversed-*` comparison retains an opaque
maximum RGB difference of 0.06676. That needs the reconstruction/depth-convention
matrix under V56, rather than being hidden by background acceptance.

Evidence lives under the existing run root: `reports/gl66-clear-*.json`,
`gl66-reverse-preservation.json`, `gl66-reversed-summary.json`, correlated
`mcp-output/gl66-*.json`/EXRs, and `logs/gl66-log_{opengl,rendering}.log`.
Build85/Start65 found the bugs above and is not the final clean cohort; its default
texture-less skybox produced a binding error before explicit admission was added.
Broker reviews `b0ea5e46f0e84b529eff038196a74316` and
`7324f6ffd88c439caef05faea0fd9e18` completed with requested/actual Sol. The latter
identified the final barrier and submesh gaps. Its canonical-Lequal concern is
resolved by the existing backend `MapDepthComparison` and the runtime check above.

I67 is implemented. V25 retains Vulkan/output-profile validation; this GL evidence
does not certify Vulkan, OpenXR two-pass, captures, or a physical headset.

## 2026-09-08 Vulkan capture provisioning and readback

Current status: I69 is fixed; I70 depth exports pass but stencil acceptance is
pending; I71 GPU-produced probe-array assembly is missing. V48 remains open.
All builds and captures below use `8b104bf7a` plus working changes in the owned
`xr-advanced-0906` session. No tests were added or modified.

Build90/PID22568 failed from frame10 with bounded Advanced input-family capacity
exhaustion. Build91/PID47372 added a failure-only bank diagnostic: MainScene,
fixed capacity 65536, all eight input banks unprovisioned. `FramePlan` accepted
the static slot operation stream but did not assign it to `_operations` until
publication. Activation consequently provisioned an unused placeholder stream.
Assigning the supplied stream in the constructor fixes that mismatch without
raising the bank limit or changing completion ownership. Build92 passed with
zero warnings/errors. PID43424 ran beyond 1,000 frames without that exception;
an explicitly opted-in Advanced probe completed CaptureVersion224 with two
active output banks and no retiring banks.

Completion flags did not prove correct probe output: both imported irradiance
(64x64) and prefilter (128x128) array layer0 exports were entirely zero, including
alpha. The normalized irradiance PNG was inspected and was black. Layer0 is the
first real probe. `VkTexture2DArray.PushTextureData` only uploads CPU mip data,
whereas the probe produces GPU textures. The OpenGL implementation copies live
GPU images. I71 tracks the missing Vulkan copy and complete prefilter mip chain.
Source IBL images have not yet been inspected. PID43424 private memory reached
11,154,960,384 bytes; bounded refresh/churn and memory acceptance remain open.

The same run exposed a readback defect: D32_SFLOAT_S8_UINT depth-aspect exports
advanced five bytes per pixel. Vulkan buffer copies separate depth/stencil
planes: depth is four-byte D32_SFLOAT and stencil is one-byte S8_UINT.
The shared decoder now uses those aspect sizes and supports D16/S8 and X8/D24
depth formats. See the official [Vulkan copy specification](https://docs.vulkan.org/spec/latest/chapters/copies.html).
Build93 passed with zero warnings/errors. PID47784 exported finite full-image
depth and passed a frozen-probe clear/gradient sky comparison: 917,762 background
pixels changed; all 1,155,838 opaque pixels, alpha, depth, velocity and reactive
data remained exactly identical. The PNG was inspected. This proves the tested
mono background preservation case, not full V25 or V56 depth-mode parity.

Start70 enabled RenderDoc's implicit Vulkan layer. `rdc doctor` passed and target
38920 matched PID47784, but trigger/list produced no capture. RenderDoc's own
application log contains startup presentation-image layout and loadOp read
hazards not present in the engine's filtered log; this cohort is therefore not
a clean validation-layer pass. Retained log: `logs/vk70-renderdoc.log`.
Build95 adds read-only MCP live texture-ID capture and optional stencil probing
to inspect source generations directly; zero warnings/errors. Build94's misplaced
parameter caused one compiler error and was corrected before Build95.

Supporting artifacts use the existing run root
`Build/_AgentValidation/20260906-123802-vulkan-xr-advanced`: `logs/build-validation-90`
through `95`, `mcp-output/vk69-*`, `mcp-output/vk70-*`, and
`reports/vk70-clear-gradient.json`. Broker copy review
`f3d26da9cf0a43678c19d71ddf2b7c61` completed with requested/actual Sol. An initial
request was rejected for a nonexistent allowed root, then corrected before the
run. The review confirms the missing GPU copy; source validity and exact command
port/lease integration still require local verification.

## 2026-09-08 Probe GPU array ownership and mip production

Current status: I71–I73 are implemented and build-validated. Build97 proves
source-to-array copying; Build99's producer corrections still require the live
V48 cohort. This supersedes the earlier missing-array and pending-stencil
statements. V25/V48 and broader acceptance remain open.

Build96 added explicit GPU source copying for Vulkan probe arrays. Each source
mip is validated and pinned by native generation, transitioned for a graphics
queue copy, restored to its previous layout, and retained through the completed
copy fence. Copying alone did not fix named probe arrays: Build96/PID38408 read
nonzero source images but blank array images. Renaming the same live array to a
diagnostic name made its pixels visible. The allocator was creating blank
images for External descriptors and name-based lookup selected those instead
of the bound producer image. Build97 excludes External images from allocation
and preserves declared mip metadata; bound external wrappers own storage.

Advanced probe publication now stages both arrays, retains immutable source
publications, verifies actual backend readiness, and publishes only after the
copies succeed. Pre-commit failures retain the previous publication and retry.
Unbinding imported arrays does not destroy them; Advanced and Default cleanup
now explicitly destroy their pipeline-owned arrays after unbinding. OpenGL's
explicit GPU-source copy path rejects incomplete/incompatible inputs and no
longer regenerates authored roughness mips from the CPU mip-record count.

Build97: zero warnings/errors, 47.66 seconds. PID57872, frozen capture version41:
all 15 source/array float hashes match (irradiance mips0–6, prefilter mips0–7),
all samples finite. Mip0 irradiance max0.18554688 and prefilter max0.7998047;
higher source mips themselves are black. The graph now reports 7/8 levels.
A gradient-sky Advanced capture (version42) produces nonzero irradiance mip0,
but prefilter mip1 retains RGB0/alpha1 clear values. This is producer evidence,
not a successful complete IBL result. Ignored report:
`Build/_AgentValidation/20260906-123802-vulkan-xr-advanced/reports/vk73-stable-probe-mips.json`.

Two producer defects were then isolated. `CaptureIndirectProducerSnapshot`
used base framebuffer height while converting the mip-sized viewport to
Vulkan Y coordinates. The render pass used the correct mip attachment, so the
higher-mip draws rasterized outside it. Build99 uses the existing mip-aware
extent resolver, including the view-uniform fallback. Separately, captured
cubemaps requested explicit GPU mip generation after six faces but Vulkan
allocated only the one CPU mip record. Scene capture now declares the full
range and the cube wrapper honors it. Face/mip readback supports live inspection.
Build98: zero warnings/errors, 36.08 seconds; Build99: zero warnings/errors,
6.48 seconds. Runtime acceptance of these changes is pending below.

The bounded broker copy review used requested=actual `gpt-5.6-sol`; its staging
and readiness findings informed Build97. The later mip review also completed
with the exact model, but could not read XRQuadFrameBuffer outside its roots.
Its suggested missing snapshot was a hypothesis: local inspection showed a
snapshot already exists and isolated its incorrect base extent. No worker
result substitutes for the builds and captures above.

PID57872 native Vulkan mono clear-to-gradient comparison: 917,762 background
pixels change; 1,155,838 opaque pixels have exact RGB preservation. Alpha,
depth, velocity and reactive mask are byte-for-byte unchanged; all output is
finite. V25 still needs Vulkan HDR/custom/reversed/capture profile coverage.
Report: `reports/vk73-clear-gradient.json` beneath the same ignored run root.
No tests were added or modified.

Build99/PID59540 (version76 Default, version78 Advanced) validates I72: all
15 convolution mips are finite and nonzero, and every array copy is exact.
The irradiance image was viewed. Higher irradiance maxima decrease from
0.18554688 to 0.07470703 in the Default capture; prefilter from 0.7998047 to
0.07543945. Advanced likewise passes all15 with distinct roughness results.
The last snapshot at frame4489 reports 2 active banks, 0 retiring, 6 free and
0 activation failures, but 764,580,848 managed activation bytes and process
private memory 8,667,561,984 bytes. This is not bounded-refresh proof.

Build100 also declares the mip range in LightProbeComponent's specialized
cubemap factory (the base SceneCapture factory alone does not cover it).
Zero warnings/errors, 19.99 seconds. PID34236 retained face0 mip6 is finite
and nonzero (max0.11566162), proving generation of the allocated environment
chain. Sky-only face1/2 remain black, so V48 still fails complete face content.

Enabling the existing runtime frame-op trace isolated the export defect.
All six faces had their blit at pass100000, before native visibility/shading
passes100002–100008 and background100009. The export command described a
transfer pass but never pushed its index during authoring. Build101 fixes that
scope and rejects missing pass metadata (zero warnings/errors, 25.91 seconds).
PID37636 shows native stages, background100009, then export100033. All six
faces now contain finite sky RGB. However sky-on side faces appear to lose
geometry; a sky-off recapture restores nonzero geometry. V25/V48 remain open
while native depth/opaque preservation is investigated. Do not treat the
previous pre-render copies as correct capture-layer evidence.

Supporting files under the ignored run root include `mcp-output/vk74-*`,
`vk75-*`, `vk76-*`, `reports/vk74-advanced-probe-mips.json`, and per-session
Vulkan logs under the owned session path. The corrected export ordering
applies to the shared HDR/depth/visibility export command; the other profiles
still require their own output checks.

Build104 (zero warnings/errors, 4.47 seconds) enables exact submitted offscreen
readback after the transient caller-FBO scope ends. It matches the newest
submitted receipt only for an Advanced offscreen pipeline, while retaining
pipeline/viewport identity, registry, resource/descriptor generations, extent,
queue and pass signatures. It does not instantiate replacement images.
MCP `capture_owner_id` selects the assigned capture viewport. The public
`CaptureViewport` is runtime-only, and capture inspection remains read-only.

PID56652 versions54/55 prove sky preservation in all six exported faces:
opaque RGB/alpha are exact and last-face depth, velocity and reactive mask are
exact. Both actual background attachments have LOAD/STORE semantics, refuting
the load-clear hypothesis from the bounded broker review (requested=actual
Sol). The exported face5 and its own HDR source hash match exactly. The
apparent missing geometry was not a background overwrite. However all faces
had precisely the same200-pixel native geometry mask, while sky directions
changed. That was a separate per-view canonical-global cache defect.

I75 root cause: `VulkanAdvancedSceneResourceSlot.Find` matched only database and
scene publication. A capture of the same scene reused the main view's set-0
view/frame/pass buffers. Each preallocated entry now retains exact copied view,
frame, pass, coverage and diagnostic inputs; differing inputs produce distinct
immutable native states. Pass snapshot high-water arrays survive completed slot
reuse, and view storage is preallocated. The caller preparation resets before
each visibility request; downstream global descriptor sets are indexed by
entry and view ranges are uploaded separately. The bounded requested=actual
Sol review found no blocker in its supplied excerpt, but did not inspect all
downstream bodies; local source inspection and runtime are the evidence.

Build105: zero warnings/errors, 19.03 seconds. Start79 versions40/41 now produce
native opaque masks of711/323/0/3584/0/1504 pixels across faces0–5, consistent
with distinct side/up/down views. Sky toggles preserve all opaque RGB and
alpha exactly, and last-face depth/velocity/reactive exports match exactly.
Side-face PNG was viewed. Report `reports/vk79-cube-background.json` in the
ignored run root. No per-view aliasing acceptance is claimed for other output
families that have not run.

V48 remains open for I76. PID56652's20 explicitly completed refreshes advance
versions55–75 over frames4577–5937. Image count stays211 and image bytes stay
734,941,184; output banks remain2 active/0 retiring/6 free, with zero activation
failures and764,580,848 managed activation bytes. Total live Vulkan allocations
increase1,878→2,026 and allocated bytes2,301,707,952→2,313,104,048. Counts also
rise at intermediate five-refresh checkpoints. This establishes growth during
the cohort, not yet its owner or causation versus elapsed idle rendering.
Buffer-owner diagnostics and an idle control are the next investigation.
Report `reports/vk78-refresh.json`. No new tests were added or modified.

I76 resolution, September 8: Start80's idle control stayed at2,361 native
allocations /2,318,166,192 bytes. Five refreshes then added35 buffers and
2,627,840 bytes. Build107's native owner labels identify the exact increments:
two `LightProbeGridCells`, two `LightProbeGridIndices`, and one each
`LightProbePositions`, `LightProbeParameters`, `LightProbeTetrahedra` per refresh.
These allocations remained CPU-owned, with submitted grid cells; they never
entered retirement. The probe owner already calls `Destroy(true)` and `Dispose`.

`VkDataBuffer.PushData` could allocate storage before `Generate` assigned a
binding ID, while `VkObjectBase.Destroy` skips inactive wrappers. Build108
establishes activation before entering the upload body. The outer call returns
because `PostGenerated` performs the upload; continuing would duplicate uploads
and potentially recreate immutable storage. A bounded broker review with
requested=actual Sol independently identified the same invariant. Local source
inspection confirms the binding allocator rejects zero and generation failure;
subsequent hardening also prevents a queued upload from regenerating destroyed
data. No base-wide teardown relaxation or forced device idle was added.

Build108: zero warnings/errors, 5.25 seconds. Start82/PID13684 completes20
Advanced refreshes, versions57–77, over frames1127–2504; measured capture waits
sum to47.454 seconds, excluding MCP diagnostics. Counts at refresh10/15/20
are identical:1,560 allocations /2,275,043,248 bytes. Temporary in-flight peaks
(1,564 at refresh8;1,563 at14) retire. Final probe roles have one buffer each:
grid cells262,144 bytes and four256-byte buffers. Images remain211 /
734,941,184 bytes. Banks remain2 active/0 retiring/6 free, no activation failures,
with764,580,848 managed activation bytes. This is a bounded native-allocation
plateau, not a zero-allocation claim for structural refresh CPU work.

At version77, all7 irradiance and8 prefilter mip readbacks are finite, nonzero,
and hash-identical to their copied array layers. The irradiance PNG was viewed.
Together with Start79's six distinct geometry masks and exact sky preservation,
this closes the single-probe Vulkan V48 fixture. OpenGL copy regression remains
separate V68. Start82's Vulkan log had zero VUID/SYNC-HAZARD/validation-error
matches before the independent thumbnail experiment. Artifacts in the ignored
run root: `reports/vk82-refresh.json`, `reports/vk82-probe-mips.json`, and matching
`mcp-output/vk82-refresh-alloc-*.json`. Durable numeric results are recorded here.

### Standalone capture scheduling follow-up

Start82's six synchronous `ThumbnailCaptureComponent.TryCapture` calls through
MCP never completed. Current `McpDispatchMode.MainThread` dispatches to the
application thread (`EnqueueAppThreadTask`), not the render thread. The standalone
owner lacked a queued entry point or synchronous thread guard. Presentation
descriptor rejections appeared after this experiment; their causation is not
yet isolated. This does not invalidate the preceding completed probe cohort.

I77 adds coalesced render-thread capture/completion scheduling, explicit
wrong-thread rejection, capture version/failure diagnostics, and diagnostic
completed-texture identity. It also prevents a signaled fence for unsuccessfully
authored work from publishing a successful output. Live thumbnail/portal
acceptance remains open until the new path is run.

Build109 (zero warnings/errors,41.63 seconds) and Start83 did not complete the
queued thumbnail. Moving authoring from a general render-thread coroutine into
the owning window's active-frame callback in Build110 (zero warnings/errors,
20.60 seconds) also did not resolve it. The initial hypothesis that pre-frame
authoring alone explained the failure was not established. Start84 recorded
repeated viewport render attempts, no completed capture and no required-stage
failure. Source inspection identifies the exact pre-authoring rejection:
standalone `CreateOutputRequest` used `BackgroundCapture`, while
`XRRenderPipelineInstance` requires `RequiredDependency` to reserve an exact
output-completion receipt. I77 corrects that contract and retains active-window
scheduling for explicit renderer/submission ownership. Validation continues.

Build111 (zero warnings/errors,20.17 seconds) reaches completion reservation but
Start85 quarantines a rejected command stream. Build112 (zero warnings/errors,
20.28 seconds) distinguishes pre-submission rejection from submitted completion
failure and releases the exact captured canonical/picking package on settlement,
matching the probe owner. Start86 then exposes a separate identity mismatch:
Vulkan compares the explicit registered Advanced output ID against the viewport
history ID. Build113 (zero warnings/errors,8.44 seconds) freezes those identities
separately and uses the Advanced ID for both enqueue and deferred preparation.
The bounded requested=actual Sol review agrees they must remain distinct and
that compatibility comparisons must retain Advanced ownership. Local inspection
found fingerprint-only consumers, so the recording fingerprint also includes
the new identity. Binding capture precedes context completion/fingerprinting.

Start87 gets past identity admission but exact completion reports missing
producer/terminal work. Its trace contains two main-view chains including
bloom/FXAA/presentation; the supposed thumbnail has no offscreen export.
`EnsureResources` assigned the camera while `SetRenderPipelineFromCamera` was
still true, replacing the explicit thumbnail pipeline with the camera's main
pipeline. Set that flag before assigning the camera. This is a source-supported
profile replacement, not evidence that thumbnail post-processing was intended.
Validation of the corrected profile remains pending.

Build114 (zero warnings/errors,23.09 seconds) and Start88 confirm the real
`Thumbnail` profile with post/temporal/bloom/DoF/late transparency all disabled.
Capture still does not complete. Build115 adds cold `GetCaptureDiagnostics`
without GPU polling; Build116 applies the same scene-capture scope as probes
(zero warnings/errors,26.80/19.10 seconds respectively). Start89 diagnostics
show no pending GPU writer, no quarantine and clean retry state. Those snapshots
alone do not distinguish pre-authoring rejection from a rejected submission
between retries. Build117 adds explicit precondition
reasons (zero warnings/errors,26.15 seconds). Start90 reports history-generation
invalidation during capture resource realization. The viewport incorrectly
classified mono offscreen products as desktop-facing, acquiring a desktop
history ledger despite their own exact output receipts and disabled temporal
profile. Exclude the offscreen purpose from `IsDesktopFacingOutput`; validate
the resulting capture and probe regression before claiming acceptance.

Build118 passed (zero warnings/errors,19.61 seconds), but Start91/PID42680 still
failed. The full Vulkan warning log, rather than a between-attempt owner snapshot,
identifies two backend failures: exact output binding rejects `producer output id`
(nine authored operations), and native-compute closure lacks a required resource.
`OutputRequest.ResolveOutputIdentity` did not accept `Thumbnail` under the
scene-capture context, replacing its dedicated owner ID. The native reactive-mask
image was declared only with late transparency, although every shaded output
writes it. Move that image into native-shading resources and preserve the explicit
thumbnail contract when lowering. A bounded read-only broker review
(requested=actual `gpt-5.6-sol`) confirmed the resource declaration mismatch.
Build119 passes with zero warnings/errors in21.79 seconds; Start92 validates both
fixes. No acceptance is inferred from the build.

Start92/PID47800 proves successful standalone output (V49 thumbnail and V47
portal). Each owner completes versions1–8: initial256x256, a translated/rotated
camera, resize to320x192, then five refreshes. All four inspected states per
owner have zero nonfinite samples and exact HDR/export float-hash equality.
Both initial hashes are `83279B55…57DFD31C`, moved hashes `C234B9F0…271404F1`, and
resized/repeated hashes `253A968C…54549E4D`. The portal retains its initial hash
through all thumbnail changes, then reproduces the same pixels at corresponding
poses; neither owner writes the other's texture. The thumbnail PNG was viewed:
red/green panels, ground and gradient sky are present. Its pipeline15 log records
native passes100002–100008, background100009 and export100031, without temporal
or main-view post work. The later MCP trace falls back to the desktop once the
capture stops; it is not used as thumbnail proof.

Build119 also preserves all15 probe source/array mip hashes (seven irradiance,
eight prefilter) at completed version2, with nonzero finite output. Vulkan logs
have zero VUID, SYNC-HAZARD, validation errors, recovery-pending or output-receipt
binding rejections in this cohort. Opt-in `Presentation descriptor binding
rejected` diagnostics remain: the descriptor observer probes every binding
named `SourceTexture` and rejects images other than the published final source.
Those messages are not failed capture receipts. Retirement/reactivation and
consumer-lease review remains I77; no full-document acceptance is claimed.

The requested=actual Sol lifetime review found real source races despite that
successful simple activation cycle: retirement was sticky and cleanup did not
recheck activation; destruction was not atomic with acquiring a consumer lease;
queued attach/cancel jobs could outlive their request; live desired dimensions
could disagree with the allocation being authored. Builds120/121 (zero warnings
and errors; final incremental check1.90 seconds) serialize retirement, creation,
completion publication and lease admission, use allocated extents, and tag queued
requests with generations. Deactivation and destruction schedule render-thread
callback detachment. A prior writer settles without satisfying a newly queued
request. Quarantined submitted work retains its fence, package and resources.

Start94/PID57764 exercises the actual public lease API using a scratch runtime
adapter (no unit-test files changed). A retained256x256 thumbnail survives
deactivation, then20 rapid queue/deactivate/resize/reactivate cycles. During the
lease, version remains1, references1, the texture remains alive, and the new
refresh waits with dirty resources. Retirement is cancelled and its cleanup
queue clears. Releasing the lease produces version2 at320x256, with finite
nonzero output and no pending writer/package/quarantine. A second
acquire/deactivate/release clears the viewport and cleanup queue. Destroying a
portal while its output lease is retained likewise keeps the texture alive;
release returns banks to2 active/0 retiring/6 free with zero activation failures.
This cohort has zero Vulkan VUID/SYNC-HAZARD/validation errors, recovery-pending,
or exact-output binding rejections. Final requested=actual Sol review found one
remaining stale-cancellation interleaving: retirement released its gate before
invalidating the request, allowing a reactivation request to be cancelled. Keep
both steps under the same publication lock. Build123 includes that correction
and passes with zero warnings/errors in25.59 seconds; the review found no other
unsafe lease/writer/retirement interleaving within the supplied contract.

The separate OpenGL Start93/PID53240 attempt did not publish probe irradiance or
prefilter output (CaptureVersion0). V68 remains open; this is not a passed copy
comparison. Its startup log reports the visibility shader family still compiling
and an unavailable Advanced family. The next GL run must inspect the live
capture stage/admission state and retry after warmup before attributing the
failure to GPU array copy.

Start95/PID39292 isolates the GL block: generation1 has a submitted IBL writer
but `OutputValid=false`; publication polling at world buffer swap never settles
it. The desktop Advanced profile is actually Bound/Admitted after warmup, so the
startup capability message is not the ongoing cause. A scratch one-shot callback
on the actual window render boundary invokes normal publication settlement and
immediately permits generation2; a second permits generation3. These incomplete
convolution batches must settle before retrying. Move normal probe completion
polling into `GlobalPreRender`, where the world's renderer context is current,
instead of buffer swap. The next global-resource snapshot observes the promoted
generation. Build123 also adds precise required-probe-pass preparation diagnostics
to identify any remaining convolution rejection. Build122 failed because the
diagnostic helper initially assumed a quad FBO for cube passes; Build123 fixes
that helper to accept the actual mesh. Start96 validates the resulting GL path.

Start96/PID11692 reaches completed generation4 after temporary buffer-readiness
deferrals. All15 source/array mip pairs are finite, nonzero and exactly hash-equal;
the64x64 irradiance array PNG was viewed. However, the first subsequent required
Advanced refresh does not complete: pipeline10 reports seven rejected stages,
with `VisibilityPreparation` returning the misleading reason `Ready`. The existing
completed output remains valid, with no pending writer or IBL generation.
`OpenGLAdvancedSceneTableUploader.TryLowerTextureReferences` can preserve a prior
successful `Ready` reason when a later logical texture/source/sampler lookup fails.
Split those checks into precise failure reasons before diagnosing the refresh.
No bounded-refresh or V68 acceptance is claimed. Baseline tracked GL allocation
was730,719,776 bytes (textures729,318,235, buffers1,385,157, renderbuffers16,384).

Build124's precise diagnostic isolates an occupied texture row whose logical
lookup is invalid. `AdvancedGpuRecordTable` intentionally retains a tombstoned
physical row until older publications are acknowledged, while the current
lookup is exactly `AdvancedGpuHandleLookup.Invalid`. Build125 handles that
case in both source validation and bindless lowering; holes/tombstones retain
the zero reference, inconsistent live rows fail, and material references must
still resolve live sources/samplers. Requested=actual Sol review
`3ff8b457e9bd41e38031b2ccd156c835` accepts this distinction. Its conditional
disposal concern is satisfied by the actual sole disposal chain:
`DisposeAdvancedRuntimeForShutdown(false)` calls `RawGL.Finish()` before registry,
slot and uploader disposal. Normal reuse polls the slot fence before releasing
its publication/resident handles; old publications retain their own residency.

Start98/PID3224, Build125 (zero warnings/errors,5.35 seconds), completes20
OpenGL probe refreshes, versions4–24, frames7995–12152,13.375 seconds. Tracked
VRAM increases from730,719,904 to731,702,920 bytes during initial reuse, then
is identical at iterations10/15/20; buffers stay1,385,285 bytes and renderbuffers
16,384. All7 irradiance and8 prefilter source/array mip pairs are exactly
hash-equal at version24, with no nonfinite samples and nonzero RGB in every mip.
The irradiance PNG was inspected. This closes I79/V68 for the single-probe
OpenGL fixture. Stale frame-package descriptor-generation mismatches are observed
during probe global updates; they defer packages and all refreshes finish. This
is not a claim that logs contain no warnings. Evidence: `reports/gl98-refresh.json`,
`reports/gl98-probe-mips.json` under the current ignored run root.

Cube inspection initially fails because GL tooling only admits2D textures.
Build126 (zero warnings/errors,2.28 seconds) admits cube/array/view readback and
treats one cube's six faces as z slices, following the
[Khronos subimage contract](https://registry.khronos.org/OpenGL-Refpages/gl4/html/glGetTextureSubImage.xhtml).
Start99/PID36780 captures all six64x64 faces at versions5/6 with sky off/on.
Opaque counts711/323/0/3584/0/1504 match the Vulkan fixture; opaque RGB and alpha
remain exactly equal, while background RGB changes. Final-face depth, velocity
and reactive mask are bit-equal. All six1x1 mip6 exports are finite/nonzero;
face6 is rejected as out of range. Side-face PNG inspected. This closes I80's
cube readback requirement; cube arrays/views have no separate live claim.
Evidence: `reports/gl99-cube-background.json`, `gl99-cube-mip6.json`,
`gl99-invalid-face.txt`. Vulkan regression after the shared completion-polling
change remains required for I78, and other background profiles remain V25.

Vulkan regression Start100/PID51648, Build126, completes 20 refreshes,
versions2–22 and frames515–2079. Native allocations plateau at 761 allocations /
2,933,732,336 bytes at iterations10/15/20; images remain255 /1,196,240,896 bytes,
and banks remain2 active/0 retiring/6 free with zero activation failures. All15
source/array mip comparisons at version22 are finite, nonzero and hash-identical;
irradiance PNG inspected. Versions23/24 repeat the six-face clear/sky comparison:
opaque RGB/alpha are exactly preserved in all faces, with final-face depth,
velocity and reactive mask unchanged. Vulkan/rendering logs have zero VUID,
SYNC-HAZARD, validation-error, RecoveryPending or exact-output-rejection matches
through this cohort. This closes I78's Vulkan regression requirement. The new
Vulkan desktop mono clear/sky comparison also preserves 1,155,838 opaque pixels
exactly and changes only 917,762 background pixels; depth/velocity/reactive and
alpha match. Compatible custom geometry and the other capture profiles remain
under V25. Evidence: `reports/vk100-refresh.json`, `vk100-probe-mips.json`,
`vk100-cube-background.json`, `vk100-main-clear-gradient.json`.

### Standalone data capture validation

The existing minimal profile/export implementation had no standalone owner
that allocated its required attachment. Build127 adds `DepthCaptureComponent`
and `VisibilityCaptureComponent`, using the existing completion/lease-gated
owner and a focused output-target partial. Depth exports D32F/S8's depth aspect;
visibility exports RG32_UINT. Neither allocates an auxiliary color/post target;
filtering is nearest and the completion request specifies the actual aspect.
Build127 passes with zero warnings/errors in40.82 seconds. Start101 validates
eight depth writers, movement,256x256→320x192 resize and retirement; visibility
authoring completes but its diagnostic readback fails.

Build128 adds Vulkan's missing eight-byte RG32_UINT numeric decoder and adds
`capture_owner_id` to resource/profile diagnostics. It passes with zero
warnings/errors in24.06 seconds. Start102/PID56644 validates both owners, eight
completed writers each. Visibility covers frames526–1078; depth covers1404–2024.
Initial/moved/resized/repeated exports have identical float hashes to their
native sources. Camera movement changes both products; final repeat equals the
resized state. Both PNGs were inspected: surface/background identity silhouette
and finite depth across the panels/ground. Depth values lie within[0.91378355,1]
initially and[0.9237763,1] after resize; no nonfinite samples occur. RG32_UINT
numeric captures include the background sentinel; they are not lossless proof
of every32-bit handle bit because float conversion rounds large integers.

The actual inventories contain only depth pyramid current/previous, depth/stencil,
identity, metadata and selection textures plus the visibility framebuffer.
Observed stages are frame begin, deformation, visibility preparation/raster,
depth pyramid/late visibility and output. No classification, native shading,
lighting, temporal, post or UI stage is observed. After deactivation both owners
have null viewport/profile/format, zero publication references/package generation,
no writer, no quarantine and no queued cleanup. Vulkan/rendering logs have zero
VUID/SYNC-HAZARD/validation-error/recovery/exact-output-rejection matches before
shutdown. I83 is closed. OpenGL comparison remains required before V50 closure.
Evidence: `reports/vk102-Depth-data.json`, `vk102-Visibility-data.json` and matching
resource/profile/retirement snapshots under `mcp-output`.

Mirror source review (`73dddfc1516d43c28e91e4cb9e35e3ca`, requested=actual Sol,
completed) finds incomplete ownership beyond the data-capture work: buffer-swap
writer polling, missing reader-aware retirement/destruction, missing writer
package release, repeated request registration/viewport replacement, an RGBA8
target incompatible with the Advanced HDR export, and missing mirror callbacks
in the Advanced main command chain. I81 remains open; V36 cannot be claimed from
the portal/thumbnail results. Preserve reflected camera/oblique clipping and
explicit writer/reader completion when addressing this owner; do not enable
arbitrary legacy callback lanes in Advanced as a workaround.

OpenGL Starts103/104 completed writers and changed native depth after moving
the visibility camera, but identity stayed at its initial hash. Owning all
three indexed color-write masks (Build129) was necessary state hygiene but did
not resolve this failure. The retained mono-array alias cache could reuse a
view after native GL names and local pipeline generations were recycled by a
different source owner. Build130 made source identity part of the key;
Build131 further restricts eviction to fenced slot reacquisition, bounds the
cache by resource role (16 per slot), and rejects replacing a role already used
within the current family. The current family retains its source objects and
views until its completion boundary. Sol review
`08a3f31d708d4dda909dd80ef5549ae2` (requested=actual, completed) accepts this
boundary, conditional on the existing render-thread disposal invariant.

Build131 passes with zero warnings/errors in 5.23 seconds. Start106/PID54100
completes four owners (depth, visibility, depth, visibility), eight writers
each. Initial/moved/resized/repeated hashes match the native sources; both
visibility owners reproduce the same changed-camera and resized hashes.
The normalized visibility PNG was inspected. Every owner retires to null
viewport/profile/format, zero references/package generation and no pending
writer/quarantine/cleanup. The session logs contain no GL-invalid, recovery,
output-rejection or quarantine matches. I84 is closed. Evidence:
`reports/gl106-a-Depth-data.json`, `gl106-a-Visibility-data.json`,
`gl106-b-Depth-data.json`, `gl106-b-Visibility-data.json`, and corresponding
retirement snapshots under `mcp-output`.

Independent decoding then exposed a distinct diagnostic-file defect: the
ImageMagick EXR writer stores HALF channels even when its image depth is 32.
Numeric RG32_UINT sentinel values became infinity in saved files. Earlier
finite statistics and matching hashes describe the runtime float readback,
not those saved EXR pixels. Those visibility EXRs are excluded from saved-file
acceptance; their PNGs and runtime comparisons remain useful evidence.
Build132 replaces that path with a dependency-free uncompressed scanline
FLOAT32 RGBA writer in `XREngine.Data.Core.Files.OpenExrWriter`. It preserves
all source float bits, including negative and nonfinite values, and applies
only the requested row flip. No quantum scaling or half conversion remains.
The required channel/header/chunk layout follows the
[OpenEXR file specification](https://openexr.com/en/latest/OpenEXRFileLayout.html).
The library limitation is visible in ImageMagick's
[EXR writer](https://github.com/ImageMagick/ImageMagick/blob/main/coders/exr.c).
Terra review `c4c332bb67df4b7c8ceaef506f4cc432` (requested=actual, completed)
accepts the encoding; Build132 passes with zero warnings/errors in 29.89 seconds.

Start107 repeats both OpenGL owners. Independent OpenCV decoding of all 16
native/exported EXR files across initial, moved, resized and repeated states
is finite and reproduces every original readback SHA-256 after accounting for
BGRA ordering and the recorded row flip. The numeric visibility sentinel is
4,294,967,296, rather than infinity. This still does not make float conversion
a lossless representation of arbitrary uint handles. I82/I85 are closed;
V50 awaits the corresponding Vulkan saved-file verification. Evidence:
`reports/gl107-Depth-data.json`, `gl107-Visibility-data.json`,
`gl107-data-file-validation.json`.

Start108/PID58608 repeats the Vulkan owners on Build132, eight writers per
owner, initial/moved/resized/repeated inspection and complete retirement.
Its 16 decoded EXRs also exactly match the original readback hashes and are
finite. Across the two backends all four identity images are bit-equal; depth
differs by at most 1.1920928955078125e-7 (floating-point raster precision).
The older HALF depth exports hid this small difference; they must not be
interpreted as proof of FLOAT32 bit equality. Both Vulkan PNGs were inspected.
The exact session logs contain no VUID, SYNC-HAZARD, validation error, recovery,
output-rejection or quarantine matches. Every owner retires with null viewport,
zero publication references and package generation, and no queued release or
quarantine. V50 is closed for this two-backend data-only fixture. Evidence:
`reports/vk108-Depth-data.json`, `vk108-Visibility-data.json`,
`vk108-data-file-validation.json`, `vk108-gl107-data-parity.json` and matching
profile/resource/retirement snapshots.

The follow-up mirror design review `e03a401a7ff141f4ae7b0f10d351e958`
(requested=actual Sol, completed) recommends reusing the standalone owner
through protected camera/lifecycle hooks for Advanced mode, preserving legacy
mode separately, and adding canonical opaque mirror material admission rather
than disguising an opaque mirror as transparency. Outer consumers need exact
texture-generation ownership through GPU completion; the old mirror shader
and legacy callbacks do not establish that contract. A single 2D output cannot
claim independently projected stereo or multiple cameras. Before coding this
integration, resolve publication retention across prepared frame packages and
refresh continuity: a post-window fence alone cannot justify rewriting a
texture still referenced by another prepared/recorded generation. I81/V36 stay
open; no mirror display implementation or live pass is claimed by this review.

The subsequent ownership review `97c72ed3a23f4a8ea1484ad884ea65f2`
(requested=actual Sol, completed) identifies the existing reuse point:
`AdvancedGpuResourceBindingSource.Lifetime` and
`AdvancedGpuResourcePublicationSnapshot.TryAddTextureSource`. The snapshot
retains source lifetimes through prepared/recorded/GPU ownership and releases
them before free-entry reuse. Local follow-up confirms fully drained terminal
release in `AdvancedSharedGpuSceneDatabase.TryReleaseTerminalSnapshotsCore`,
which waits for all active leases and package/GPU pin counts before clearing
every source snapshot. Material texture encoding currently supplies no lifetime
for ordinary XRTexture2D sources, so a standalone capture requires explicit
integration at that encoder and owner boundary. I86 tracks that prerequisite.

### 2026-09-08 GI provider switching and probe updates

Build132, Vulkan Start108/PID58608 and OpenGL Start109/PID57480 each run six
grouped HDR/depth captures with the built-in AO disabled and FXAA selected:
provider on, off, restored, changed sky before refresh, after refresh, and off
after refresh. The scratch reflection adapter only selects the existing
`GlobalIlluminationProvider`; production shader/provider code is unchanged.
Each capture contains 1,155,838 opaque pixels. All samples are finite, and
depth plus alpha are exactly unchanged across all six states on each backend.

Vulkan's provider-on contribution changes 1,150,539 opaque pixels, with positive
RGB deltas up to 0.27886963. OpenGL changes all opaque pixels, with deltas from
0.01039886 to 0.24157715. Restoring the provider reproduces the earlier opaque
output exactly on both backends. Setting the sky to blue HDR leaves opaque
lighting identical until the capture completes. Probe versions advance 2→3 on
Vulkan and 4→5 on OpenGL, after which the opaque contribution changes. Disabling
the provider after that update exactly reproduces the original disabled opaque
output. This rules out stale switching and cumulative contribution from repeated
enabling in this fixture. The independently generated probe cohorts differ;
their absolute cross-backend radiance is not asserted identical.

Both final PNGs were inspected. Vulkan logs have no VUID/SYNC-HAZARD/validation
error/recovery/output-rejection/quarantine matches; OpenGL has no GL-invalid,
recovery/output-rejection/quarantine matches. V16 is closed for the admitted
LightProbesAndIbl provider. Other GI algorithms remain outside Advanced's
currently admitted provider contract. Evidence:
`reports/vk108-gi-switch.json`, `gl109-gi-switch.json`, all six corresponding
`mcp-output/<tag>-gi-<case>.json` captures and profile snapshots.

Data-only final cohort frame spans, recorded from exact owner stages:
OpenGL Start107 depth6648–8359 and visibility9224–10706; Vulkan Start108
depth880–1559 and visibility1897–2692. Each includes eight completed writers.

### Canonical capture publication ownership

Build133 adds `AdvancedMutableTexturePublicationLifetime`: one atomic token per
physical standalone capture texture, with generation-checked reader admission,
writer reservation, explicit publication/withdrawal and terminal retirement.
The token never calls the producer lock from the scene database lock. Published
textures remain unwritable even between readers; withdrawal prevents new readers
but preserves existing counts. Writer admission, resource recreation and owner
retirement all wait for those counts as well as existing direct leases.
`TryPublishCompletedOutput` and `WithdrawPublishedOutput` require the render
thread. The material source encoder attaches the token, and resource snapshots
retain the exact source-content generation. Existing immutable source lifetimes
retain the default interface implementation. The writer advances the texture
content generation only after its GPU completion.

Sol review `8296316de1924379a2f3973d6d8ce859` (requested=actual, completed)
found a pre-existing one-based-index defect in the resource snapshot's source
generation array: it had Capacity entries while accepted handles range through
Capacity. A retain at that highest handle could throw before recording its
lifetime, leaking the reader reference. Build134 changes the array to Capacity+1,
matching the source/lifetime arrays. The review found no other concrete race
under the single render-thread producer contract. Build133 passes in 42.14 seconds
and Build134 in 20.81 seconds, both with zero warnings/errors. Live validation is
in progress under I86; no mirror display acceptance is implied.

Start110/PID40812 retains six canonical source references after binding the
completed thumbnail to a real native textured cube. Its queued resize correctly
stays at version1/256x256 while published. Removing the consumer and withdrawing
the source leaves four references, however, because logically retired ring
snapshots retained source lifetimes until eventual reuse. Build135 releases
those retains when an acknowledged entry with zero package/GPU pins is removed
from the ring. Review `5cae2637d1504dd1a55460630d67d53a` (requested=actual Sol,
completed) accepts that ring boundary: snapshot lookup, lease acquisition and
coalescing can no longer find a removed entry. Existing native consumers retain
their own GPU leases: the GL scene-table uploader holds one through its submitted
fence, and Vulkan prepared recording/pin sets retain and move them into accepted
frame ownership. Neither source can legally depend on a retired unpinned entry.

Start111/PID62216 on Build135 now drains canonical readers to zero and recreates
the 320x192 target. Its next authoring attempt exposes another independent gap:
`GL material texture binding 15 does not resolve its retained texture/sampler
publication`. The removed material's fixed-stride arena still contained its
old valid texture handle after that resource was tombstoned. Build136 clears a
removed material's payload and unused tails on replacement, and marks the full
erased arena range dirty. Previously sealed snapshots keep their copied payloads.
This is I87, pending runtime verification. Builds135/136 pass with zero
warnings/errors in 20.26/19.90 seconds.

The inspected Start111 consumer also contains 4,742 magenta pixels, exactly
matching ShadingDiagnostics bit16. This is reconstruction rejection, not proof
of sampled capture correctness. I88 tracks it separately. Build137 preserves
`EAdvancedReconstructionInvalidReason` in diagnostic bits20–27 while retaining
the existing rejection bits. It initializes the surface even when material
resolution short-circuits. Capture/readback of that reason is the next isolation
step. No mirror completion is claimed from the reference-count work.

Start112/PID47508, Build137, passes the live OpenGL publication lifecycle:
one or more real native scene readers retain the completed capture; an explicit
refresh/resize stays queued at version1/256x256 while canonical; removing that
consumer and withdrawing publication allows version2 at320x192. A second
publication is sampled by the reactivated consumer, then owner/consumer
deactivation drains both direct/canonical counts to zero and retires viewport,
pipeline, profile and texture. No writer/quarantine remains. The I87 payload
cleanup removes the stale binding15 failure. Vulkan regression remains required
before I86/I87 closure. Evidence: `reports/gl112-publication-lifecycle.json` and
`mcp-output/gl112-publication-*`.

The same grouped diagnostic capture isolates I88 to reconstruction reason12
(NonFinite), draw4, primitive IDs32/33, with 4,742 rejected pixels. It is not a
missing texture or material generation. Build138 adds the first nonfinite
attribute to bits28–30: 1=world position, 2=normal, 3=tangent, 4=bitangent,
5=UV, 6=UV dx, 7=UV dy. This is diagnostic isolation only; no surface fix is
claimed yet.

Start113 identifies all 4,742 failures as attribute4 (bitangent). The prior
Gram–Schmidt subtraction can leave a tiny parallel residual when authored
normal/tangent vectors are nearly parallel; normalizing it makes the subsequent
cross product zero/nonfinite. Build139 uses a cross-product projection with
finite checks and a relative squared-angle threshold of1e-12. Degenerate input
uses the deterministic perpendicular tangent. The CPU reference uses the same
rule; determinant/UV handedness remains unchanged. Sol review
`e663f728aa3f4304bfc5b03bed907384` (requested=actual, completed) accepts the C#
math and finite fallback. The broker cannot read Build shader paths, so GPU
correctness is established locally, not attributed to that review.

OpenGL Start114/Build139 passes both the publication lifecycle and a grouped
1920×1080 shading capture with **zero** reconstruction-invalid pixels. The
consumer PNG was inspected; the prior magenta rejection is gone. Vulkan
Start115/Build139 stalls immediately after creating a selected primitive:
`GizmoLine` contains an inactive NV stereo branch in its shared include, which
Vulkan validates before GLSL conditional preprocessing. This is I89, separate
from source publication. Build140 puts mono/OVR/NV position writes into their
six line/arrow entry shaders and leaves shared corner expansion API-independent.
A Luna source inventory `063e93a94fc446fcbcaeeff9c756bd4f` (requested=actual,
completed) confirms compiler ordering and rewritten-source cache hashing; it
is not a GPU review. Builds139/140 pass with zero warnings/errors in20.25/2.03s.

Vulkan Start116/PID62700, Build140, passes the complete real-consumer lifecycle:
initial canonical references1, published capture version1/256×256 remains
unwritable despite queued resize, withdrawal/removal drains readers and permits
version2/320×192, and a second publication then retires with zero direct and
canonical references, zero writer package, no quarantine, and null viewport,
profile and texture. Its grouped1920×1080 capture also has zero reconstruction
failures, and its PNG was inspected. No VUID/SYNC hazard or gizmo compiler/link
failure occurs. There are two cold-start primary-recording first-chance failures
and the first history candidate is rejected before the validation cohort;
this run is not a claim of completely clean initialization. I86/I87/I88 close
for their bounded behavior. Mirror display and broader material/attribute
validation remain open. Evidence: `reports/gl114-publication-lifecycle.json`,
`reports/vk116-publication-lifecycle.json` and corresponding grouped capture
responses under `mcp-output/`.

The existing tangent test was attempted through the normal UnitTests project.
Unity reference-exporter fixture sources were unintentionally in the C# compile
glob; the project now excludes that Unity-owned subtree. The next attempt
reveals stale Advanced/OpenXR test APIs (TemporalReason/retirement constructor
arguments, removed AttributeReconstruction stage and old picking members).
No tests were changed. An ignored runner links the unchanged
`AdvancedAttributeReconstructionNumericalTests.cs` and runs only
`TangentContract_PreservesHardSmoothUvSeamAndMirroredIslandSemantics`: **1 passed,
0 failed**,143ms. This is a targeted result, not a full UnitTests build pass.
Evidence: `logs/tangent-existing-test*.log`.

### 2026-09-08 gizmo entry-point admission

Build140 separates the API-specific position writes from shared screen-space
corner expansion. Vulkan Start116/PID62700 now compiles and executes selected
primitive gizmos without the inactive NV semantic rejection from Start115.
The final output PNG is inspected, but shows an extra gizmo image above the
cube; this is an open placement/consumer issue, not full V37 acceptance.
OpenGL Start117/PID65532 uses the same Build140 shaders with the forced native
GPU-indirect strategy, mono and emulated OVR outputs. Both1920×1080 eye PNGs
show correct line/arrow expansion and distinct eye positions; their readbacks
are entirely finite and logs have no GL-invalid or shader compile/link failures.
Mono was recaptured after disabling its independent auto-exposure setting;
the initial mono image was saturated and is not accepted visual evidence.
Evidence: `mcp-output/vk116-gizmo.json`, `gl117-gizmo-left.json`,
`gl117-gizmo-right.json`, `gl117-gizmo-mono-fixed-exposure.json`.

The mirror integration architecture review `4e07cb81ed5f4678bc38379985cd0389`
(requested=actual Sol, completed) recommends two owner-private SceneNode(world)
capture components reusing the existing owner, an explicit reflected-camera
configuration hook, a bounded native opaque projective material layout, and
exact camera admission. It calls out pre-publication material snapshot tearing
and pending-plan retention as a boundary to resolve before implementation.
Its suggested two-slot camera assignment does not itself provide continuous
double buffering for multiple cameras; the owner must make that resource/profile
contract explicit. No mirror code or runtime completion is implied by this review.

### 2026-09-08 existing mirror lifecycle work in progress

Build141/142 correct render-thread writer polling, paired writer-package/picking
release, bounded16 consumer-fence storage, unconditional deactivation/destruction
retirement requests, complete viewport/FBO/color/depth cleanup, and unregistering
the actually registered Advanced intent. Replacement reapplies reflected camera
and oblique clipping. Default keepsRGBA8; the explicitly Advanced branch uses
RGBA16F. ForMirror no longer requests unrelated post work. A protected camera
configuration hook prepares reuse of the standalone owner by reflected slots.
The latest builds pass with zero warnings/errors (43.47s and26.59s).

Review `9642905c0c5549bf875b18ea7404c3f5` (requested=actual Sol, completed)
finds a lost wake-up between retirement's queued/requested flags during rapid
activation changes. The next source change serializes that state transition.
It also calls out the accepted-command interval before a legacy PostRender
consumer fence exists: that pending-reader boundary still needs a reservation
or a proven scheduler/native-resource ordering contract. I81 remains open.
No consumer package is released without a corresponding acquired lease.

Default OpenGL Start118/PID40740 allocates the256×256 mirror but reports
captureVersion0 and no active writer; inspecting its source exposes uninitialized
pixels, so this is a failed capture run. The exact output was still classified
VisibleMirror, unlike the proven capture owners' RequiredDependency contract.
The next build changes this classification and records pre-render/authoring
attempt/disposition diagnostics. No mirror runtime pass or Advanced display
implementation is claimed. Evidence: `mcp-output/gl118-Mirror-*`,
`gl118-mirror-source.json`, `gl118-mirror-visible.json`.

### 2026-09-08 validation wrap-up

Paused at the user's request on `8b104bf7a` plus the existing working changes.
**Final Build145 passes with zero warnings/errors in 19.46 seconds.** It removes
only the unsuccessful Build144 camera-math experiment; no other work was
reverted. The resulting camera file has no diff against the source baseline.
The final build was not launched. The task-owned editor `xr-advanced-0906`
is stopped, no RenderDoc session remains open, and no commit was created.
Other editor/Monado processes were not stopped or adopted. Evidence root:
`Build/_AgentValidation/20260906-123802-vulkan-xr-advanced/`;
final build `logs/build-validation-145.log`, stop receipt
`logs/stop-validation-120.json`. Ignored artifacts are disposable; the outcomes
and resume contract are recorded here.

The active checklist now has **101 checked / 5 open implementation tasks**,
**9 checked / 0 open audits**, and **22 checked / 67 open runtime tasks**.
This is not an effort estimate or a statement that only validation remains.
I86/I87/I88/I89 retain their bounded backend evidence from the publication and
gizmo checkpoints above. New I92 records the completed existing mirror writer
and serial retirement plumbing separately from the remaining I81 integration.
The unchanged tangent test passed 1/1 through the isolated runner; the normal
UnitTests project still has the stale API compilation failures listed above.
No test methods were added or modified.

**Mirror writer/retirement result (I92):** Build143 passes with zero
warnings/errors in 20.69 seconds. It serializes the retirement flags under
`_mirrorLifetimeSync`, fixing review `9642905c0c5549bf875b18ea7404c3f5`'s lost
wake-up, and uses `RequiredDependency` for the exact writer-completion request.
Default OpenGL Start119/PID51180 completes **12,147 captures**. Its recorded
256×256 viewport12 is replaced by viewport14 at320×192 with a new texture
identity. Deactivation leaves writer/package/consumer counts zero, no
quarantine, no registered Advanced owner, and null viewport/texture/dimensions.
This is a serial lifecycle cohort; it does not prove pending collected-reader
ownership or delayed-submission pressure. Evidence:
`mcp-output/gl119-Mirror-before.json`, `gl119-Mirror-resized.json`,
`gl119-Mirror-retired.json`.

**Mirror image result (I91/V36): failed.** Start119's inspected source PNG is
black and its EXR has RGB min=max0, alpha1. Successful writer retirement does
not establish a valid reflected image. Camera parameters use a zero-to-one
System.Numerics projection; the current `CalculateObliqueProjectionMatrix`
uses `2/dot` and `c.Z+1`, which implement the negative-one near boundary. This
convention mismatch is supported by the engine's projection/depth-policy source
and the original [OpenGL oblique-projection construction](https://terathon.com/blog/oblique-clipping.html).
The mismatch is a concrete defect; it is not yet proven to be the only cause
of the black image.

Build144 (zero warnings/errors, 19.56 seconds) experimentally replaced the
third column with `plane / dot(plane, inverseProjection * farCorner)` under the
engine's row-vector convention, mapping the plane to zero depth before the
existing backend/reversed-depth remap. Sol review
`dc2bf4c5675f49658ba5f4d958baac39` completed with requested=actual
`gpt-5.6-sol` and accepted the mathematics for perspective, off-center and
orthographic lenses. It identified missing nondegenerate/scale-robust plane
validation, denominator conditioning, projection error attribution and
transactional setters. This review is mathematical evidence only.

Default OpenGL Start120/PID67528 then failed before authoring: two PreRender
calls, zero authoring attempts/completed captures, and `ArgumentException`
from the oblique far-side guard. `MirrorCaptureComponent.PreRender` currently
sets the plane in `UpdateRenderTransform` before configuring the reflected
camera. The camera setter stores the plane before projection computation can
throw, leaving a partially updated state. Texture inspection subsequently
failed because storage was not live. Logs also contain a cold-start
`VPRC_RenderQuadToFBO` null reference; no clean-start claim is made.
The experiment was removed before Build145 to avoid retaining this new
initialization regression. The original clipping defect is still present.
Evidence: `mcp-output/gl120-Mirror-diagnostics.json` and session logs under
`xrengine_2026-09-08_08-27-29_pid67528`.

Resume from these concrete boundaries:

1. **I91:** apply reflected camera state before the plane; compute and validate
   the candidate oblique projection before committing plane/matrix state.
   Correct the zero-to-one construction with finite/nondegenerate, scale-robust
   plane handling. Validate the reflected scene and both clipping halfspaces,
   then camera/depth variants. Do not suppress failures by silently disabling
   clipping. Preserve the failed experiment evidence instead of rerunning it.
2. **I93/I94:** reserve legacy mirror readers from collection through rejection
   or GPU completion; a PostRender fence alone misses pending commands. Gate
   visible output until a generation has actually completed, while keeping its
   capture scheduling active. Start118 exposed unwritten pixels; this remains
   unresolved.
3. **I81:** implement Advanced mirror scheduling and native opaque projective
   display. The standalone camera hook, `ForMirror` profile and publication
   lifetime are prerequisites already implemented. Persistent slots must pair
   texture generation with reflected projection and preserve canonical readers.
   Resolve pending material-plan retention/snapshot tearing and explicit
   per-camera/eye capacity. No two-slot native mirror consumer exists yet;
   adding legacy callbacks to the Advanced chain is not that implementation.
4. **I90/V37:** isolate Vulkan primitive placement/extra gizmo submission from
   Start116; OpenGL Start117's correct placement does not close Vulkan parity.
5. Continue every remaining unchecked V row independently. XR fault injection
   awaits an owned Monado service; PID13580 remains unowned. Hardware-runtime,
   vendor SDK/device and OpenGL RenderDoc bindless limitations remain explicit
   prerequisites, not accepted results.
