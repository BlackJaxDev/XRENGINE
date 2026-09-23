# Advanced pipeline antialiasing investigation

## AA-B1b visual revalidation: 2026-09-22 evening

The second isolated Vulkan/Advanced/Sponza run used the same private world
settings and named session `aa-b1b-0922`. All 469 meshes imported, the shared
geometry cache again admitted 465 resident draws without the old frame-scene
capacity or visibility-lease failure, and TSR was active at 1286x723 internal
resolution with a 1920x1080 display. A grouped MCP readback contained nonzero
visibility/depth and HDR color plus a populated full-resolution
`TsrOutputTexture`. Moving the camera close to `(0.35, 0.12, 0)` exposed a large,
dark Sponza facade in two inspected viewport PNGs. The earlier distant
`(7, 2, 0)` viewpoint made the model occupy only a tiny central area, so its
"background-only" description was too strong. The close captures establish
mesh output, but the PNG tool does not expose the producing frame receipt;
they do not establish temporal image quality or motion-vector correctness.

The previous game-setting TAA request was masked by the Unit Testing World
camera's `CameraAntiAliasingModeOverride = Tsr`. Setting the active camera's
override to `Taa` triggered and committed a new resource generation at
21:12:56: `aa:Tsr->Taa`, internal 1286x723 to 1920x1080. The first grouped TAA
readback at 21:13:13 was black in visibility, HDR, scene-color and velocity;
this is not a valid TAA quality sample. At frame 3265 the native stage rejected
an outdated 8x8 `stone_trims_01_BaseColor` source after it had streamed to
64x64. The rejection persisted through later sampled frames; by frame 3622
the live source had reached 4096x4096 and the retained publication still had
8x8 images. The canonical scene publication was frozen at sequence 207 with
`The canonical material tables cannot accept the complete ownership
transition.` The latest snapshot showed minimum acknowledged sequence 207,
minimum reclaimable sequence 132, and a GPU pin on retained sequence 133.
This is a material-publication capacity/retention blocker, not merely a
transient texture-streaming mismatch. The exact table limit and pin owner need
diagnosis before changing a capacity or lifetime rule.

The editor then closed through `Editor.ClosePrompt.CloseWindow` at 21:16:11;
the trigger was not established. The named session manager confirmed it
stopped. The transient `XRFrameBuffer` wrapper-before-CPU-publication exception
also recurred once at 21:05:25, then rendering resumed; it is a separate open
resource-construction race. MSAA and DLAA were not run in this revalidation
because a stale canonical scene could not supply a meaningful new-mode image.
The broader still/motion/cut comparison, TSR jitter and velocity checks, and MSAA
per-sample inspection remain open. Evidence is under
`Build/_AgentValidation/20260922-202000-aa-b1b/` (`mcp-output/revalidation-*`,
`mcp-captures/RenderPipeline_HDRSceneTex_20260922_210558.png`, the `210917`
and `211036` viewport captures, and the `211313` TAA readbacks). This run's
rendering and Vulkan logs were copied into the same evidence root's `logs/`
folder. No tests were added or modified.

## AA-B1b shared geometry residency: 2026-09-22

The diagnostic Sponza run `aa-b1b-0922` reproduced a compact request of
645,072,880 bytes at frame 3524. Its compact allocation breakdown, including
the existing 16-byte stream alignment, was:

| Scene data | Bytes |
| --- | ---: |
| Static canonical vertices | 543,568,256 |
| Indices | 44,866,592 |
| Meshlet descriptors | 10,709,360 |
| Meshlet vertex indices | 33,983,024 |
| Meshlet triangle words | 11,280,928 |
| Two empty preskinned streams | 32 |
| Frame tables, lookups and transient records | 664,688 |

The geometry snapshots expose exact used byte counts, not reserved CPU
capacity. The error was lifetime ownership: immutable scene geometry occupied
every frame slot's packed scene lane. That lane reserves all eight supported
slots, so increasing its 128 MiB ceiling to fit Sponza would exceed the frame
arena's 1 GiB aggregate mapped-memory guard.

`VulkanAdvancedGeometryResidentCache` now owns the seven geometry buffers
across slots. The key is database epoch plus arena handle/generation. Appends
write only an unpublished suffix; a growing bank preserves earlier buffers
until all referencing slots complete. Empty streams retain a zero sentinel.
The cache uses the existing tracked native buffer allocation, mapped-write,
and retirement services. Publication failures roll back only newly acquired
pins, preserving earlier accepted publications in the same slot.

The explicit policy permits at most 1 GiB per image and 2 GiB across live and
retiring allocations, checks storage-buffer descriptor limits, and rounds
nonempty banks to 4 MiB with 12.5% growth spare. Intrinsically oversized
seven-stream sets fail explicitly; temporary pin/retirement pressure retries.
Native allocation failures remain visible. Frame-varying records keep the
existing 128 MiB slot budget and 1 GiB frame-arena guard. Capacity diagnostics
include exact remaining table owners; cold geometry allocations log requested,
allocated and aggregate resident bytes.

The final isolated Debug build completed with zero warnings/errors. Its live
run imported 469 Sponza meshes. At frame 3726, the cache uploaded the exact
543,568,256-byte vertex stream to one 612,368,384-byte allocation, plus indices
and meshlet streams. Recorded resident allocations peaked at 738,197,616 bytes
(704 MiB plus the earlier zero sentinels), with no further geometry allocations
through frame 3770. The canonical publication admitted 465 draw records.
Between frames 3726 and 3770, sampled frame-tree logs reported 31 Completed,
5 Deferred and 1 Failed outcomes. None reported the prior compact-scene ceiling,
geometry-cache capacity failure, terminal renderer latch or visibility lease
exhaustion. This validates admission of the original oversized scene and
cross-frame reuse of the shared geometry allocations.

The visual exit gate remains open. Captures from `(7,2,0)` and `(-7,2,0)`, both
looking toward `(0,3,0)`, were inspected and remained background-only. Frame
3732 rejected `VisibilityPreparation` with `SourceMismatch` while a texture
changed from 8x8 to 64x64. Frame 3763 later accepted the native stage sequence;
enqueue acceptance and a Completed frame-tree outcome still do not certify
fresh mesh output. Frame 3732 also logged an `XRFrameBuffer` wrapper request
before CPU construction was published; subsequent frames resumed. A TAA request
read back as TAA in game settings, but both later state samples still described
the active TSR generation at 1286x723 internal resolution and 1920x1080 output.
The named editor exited before a successful TAA transition or MSAA attempt;
its exit cause was not established from the available logs. The session manager
confirmed it stopped. These remaining checks are tracked as AA-B1c, and neither
AA-B1b's original visual gate nor the wider AA quality audit is marked passed.

Evidence lives under `Build/_AgentValidation/20260922-202000-aa-b1b/`, including
`logs/final-build.log`, `logs/final-vulkan.log`, saved render-state responses
and the inspected viewport captures. No tests were added or modified. The user
has not reported whether the fix works in their normal editor session.
This change does not close S12's multi-world/deformation/performance gates or
the AA motion-quality work. Canonical CPU geometry currently appends replacement
geometry without a `ResetAtBoundary` caller; long-running structural/material
churn needs a separate bounded compaction policy.

## AA-B1 lease-retirement fix: 2026-09-22

The repeated Vulkan 16-family visibility lease exhaustion was downstream of a
terminal PresentNow pause. In the original Sponza run, frame 3157 failed
because its compact advanced-scene image needed 645,072,032 bytes per frame
slot against the explicit 134,217,728-byte ceiling. Earlier texture-source
mismatch rejections returned before `TryAcquireAdvancedVisibilityInput` and
did not consume leases. The terminal latch then skipped accepted-plan capture
and lowering, while each later CPU frame authored a distinct render-frame
visibility family. The ordinary skipped-frame drain preserved receipt-bound
operations for an output that could no longer be accepted; those operations
kept their lease references until the 16-slot arena filled.

`VulkanFrameOperationQueue.DiscardPausedSceneOperations` now settles all
pending scene operation snapshots, submission markers, output-completion
fences, and frame-view candidates at the pause boundary. It retains
independent texture uploads. `VulkanFrameLoop` invokes it when entering a
terminal or recoverable PresentNow pause and on subsequent rejected ticks,
and discards queued mesh requests so recovery starts with fresh scene work.
The arena capacity and immutable snapshot model are unchanged.

The isolated Debug session `aa-b1-0922` used a private Vulkan/Advanced/TSR
settings copy with Sponza enabled. The editor and Vulkan project builds had
zero warnings/errors. Sponza import completed; frame 3565 reproduced the
645,070,944-byte scene-image ceiling and terminal pause. Through 37 further
rejected frames, the new path logged 7–19 discarded scene operations per tick
and no lease-exhaustion warning. Two viewport captures at different camera
positions were inspected and remained background-only. This validates the
paused-path ownership fix, not fresh Sponza rendering or AA image quality.
Evidence is under `Build/_AgentValidation/20260922-200000-aa-b1/`; the named
session was stopped. No tests were added or run while the AA feature remains
under live validation. The user has not reported whether the result works.

The 128 MiB scene-publication ceiling is tracked separately as AA-B1b in the
todo. Its allocation-plan breakdown and proper bounded capacity policy need
investigation before Sponza and mode-transition quality verification can
resume.

## Follow-up quality verification: 2026-09-22

The user requested a fresh verification of Advanced TAA/TSR, DLAA, and MSAA,
with particular concern about TSR upscale resolution, velocity, jitter, and
smearing during motion. Prior execution evidence does not establish motion
quality or parity with Unreal Engine.

- Result: quality verification failed. Isolated session `aa-quality-vk-0922`
  ran Vulkan, then OpenGL with private settings, alongside independent temporal
  and MSAA/vendor source audits. RenderDoc prerequisites passed, but no new GPU
  frame was captured: stage rejection and resource publication already blocked
  the requested comparison.
- Disposable evidence: `Build/_AgentValidation/20260922-170000-aa-quality/`.
- The optional broker's five tools are unavailable in this session; no broker
  request was made. Native read-only rendering reviewers are assisting.
- No tests have been added or modified. No user verdict on this follow-up's
  visual result has been received.

### Confirmed source defects in the follow-up

1. **Vulkan temporal coordinate conversion.** Native reconstruction writes
   current-minus-previous unjittered NDC to `Velocity`
   (`Advanced/Reconstruction/ReconstructSurface.glslinc`,
   `Advanced/Shading/ShadeNativeOpaqueEvaluator.glslinc`). Vulkan's default
   clip Y-up / framebuffer texture Y-down policy uses a negative-height
   viewport. `AdvancedViewAccess.glslinc` explicitly converts between those
   spaces, but `TemporalAccumulation.fs` and `TemporalSuperResolution.fs`
   subtract `velocity * 0.5` directly from framebuffer UV. The uploaded current
   and previous jitter UVs likewise lack the framebuffer Y conversion.
   Convert both displacement inputs consistently before history lookup;
   preserve the native NDC velocity ABI for other consumers.
2. **TSR depth history overwritten before resolve.** Advanced invokes temporal
   accumulation before its TSR output pass. The non-TAA branch in
   `VPRC_TemporalAccumulationPass.Accumulate` copies current color **and depth**
   into the history framebuffer. TSR then samples that depth at reprojected
   history UV as though it belonged to the previous frame. TSR color history
   is captured later, but there is no corresponding delayed depth capture.
   Retain prior depth through TSR resolve and commit current depth afterward,
   keeping depth, color, and jitter generation ownership aligned.
3. **Vendor motion direction.** `VPRC_VendorUpscale` sends a positive 0.5
   normalization scale for both bridge and native Vulkan dispatch, while
   native reconstruction writes current-minus-previous NDC. NVIDIA's
   [DLSS programming guide](https://raw.githubusercontent.com/NVIDIA/DLSS/main/doc/DLSS_Programming_Guide_Release.pdf)
   requires displacement from the current pixel to its previous-frame
   position. Correct direction and backend Y convention at the vendor boundary;
   successful NGX evaluation alone does not prove valid temporal inputs.
4. **Biased jitter cycle.** The eight-entry `TemporalJitterSequence` contains
   X=+0.375 twice and omits X=-0.375; its X mean is +0.09375 before scaling.
   Y contains all eight distinct odd-eighth strata with zero mean. Restore a
   balanced sequence and check its coverage over a full cycle. Current TSR
   jitter is additionally limited to +/-0.175 input pixels by its 0.20 scale;
   whether that range provides adequate reconstruction quality needs measured
   edge comparisons, rather than an arbitrary sharpening increase.
5. **Camera history invalidation is split across owners.** The frame-view
   ledger invalidates for projection changes and camera history epochs, but
   the temporal accumulator's separate history readiness resets for its own
   dimensions/profile/pose checks and does not inspect the frozen descriptor's
   history status. FOV changes or explicit camera invalidation can therefore
   leave TAA/TSR blending history while native geometry reports invalid history.
   Propagate the ledger's invalidation into accumulation and reseeding.
6. **Reversed-depth velocity dilation chooses the farthest sample.** The mono
   and stereo temporal shaders select the minimum raw depth when searching for
   the closest velocity. Reversed depth requires the maximum. This is a
   conditional defect; the default normal-depth run does not exercise it.

Additional vendor/MSAA audit observations: vendor reconstruction currently
runs after motion blur/DoF and other post effects, pairing their spatially
mixed color with raw scene depth/motion. That ordering needs review for those
effects. Ordinary MSAA source paths use coherent nearest-sample sidecars and
per-sample shading; only the malformed-depth path disagrees on coverage (the
canonical resolve rejects invalid depth, while shading counts valid identities
before its depth check). Neither observation is a new runtime quality proof.

These findings establish incorrect temporal inputs from source. Their visual
severity has not yet been measured by controlled GPU captures in this run.

### Live setup and Vulkan blocker

The original local settings had Sponza disabled. The first run therefore
rendered only background and is excluded from mesh-quality evidence. A private
settings copy enables only the existing Sponza model; the original settings
are unchanged. The isolated build passed with zero warnings and errors.

Vulkan reported TSR internal size 1286x723 and output/history size 1920x1080,
with history ready and finite output. After Sponza loaded (368 resident draws),
native stages repeatedly rejected execution with:

> The bounded advanced visibility authoring lease arena exhausted its 16 concurrent families.

Captures remained background-only despite profile admission. Consequently this
run does **not** verify Vulkan mesh AA or motion quality. Startup imports and
shader/resource work also caused a readback timeout; that is separate from
the repeated native-stage rejection. The same named session was stopped and
restarted with an OpenGL private settings copy, reusing the validated binaries.

### OpenGL result and verification limits

OpenGL loaded 465 resident draws, but the sampled native shading stage reported
`BackendEnqueueRejected` with `The OpenGL Advanced stage does not match its
sealed family or required per-view order.` Startup shader work caused capture
timeouts. A later successful capture showed background/editor gizmos rather
than Sponza. Switching None then MSAA did not produce an active MSAA generation
before closeout: frame 15137 still reported active TSR at 1286x723 with pending
MSAA at 1920x1080. The `gl-msaa-capture` label records the **requested** mode;
its resource/image data still belongs to TSR and must not be counted as MSAA.

The session's OpenGL launch explicitly enabled the installed vendor bridge in
its process environment. DLAA evaluation was not reached in this follow-up.
The prior successful NGX evaluation remains historical execution evidence only.

| Requested check | Follow-up result |
| --- | --- |
| TSR reduced-resolution allocation | Observed 1286x723 color/velocity and 1920x1080 output/history on both backends. |
| TAA/TSR temporal correctness | Failed source audit: coordinate conversion, depth history ordering, history invalidation, jitter cycle; reversed-depth dilation additionally affected. |
| TAA/TSR still/moving/cut quality and scale sweep | Blocked by native execution/resource startup; no valid comparison obtained. |
| MSAA mesh coverage and raw sidecars | Not reverified; OpenGL request remained pending, Vulkan mesh execution failed. Prior coverage captures remain historical evidence. |
| DLAA temporal correctness | Failed source audit of vendor motion direction; no fresh vendor evaluation obtained. |
| Stereo/quad-view | Not exercised. |

Useful current evidence is `vk-admit`, `gl-steady-check`, `gl-msaa-active`,
`gl-msaa-pending`, and `gl-final-state` JSON in the run's `mcp-output/` folder.
`gl-steady-view` and `gl-msaa-capture` record the inspected background-only
outputs. Build and backend logs are copied to `logs/`. Only investigation/TODO
documentation was changed; no renderer fixes or tests were added in this
verification. Both backend runs used the same owned logical session, stopped
through the session manager at closeout. The original local settings were not
written. The user has not yet assessed a follow-up visual result.

## Problem

The user reports that Sponza and other meshes show little or no antialiasing
under Advanced except with FXAA or SMAA. The initial diagnosis was followed by
an explicit request to fix TAA, TSR, MSAA, and DLAA. The user then resumed the
todo work on 2026-09-22. Mono OpenGL and Vulkan TAA/TSR/MSAA live paths now
execute with nonblack output, history, and four-sample MSAA coverage. DLAA
executed on OpenGL/NVIDIA. Stereo, Vulkan DLAA availability, direct per-sample
capture, quality assessment, and regression tests remain open.

## Root causes

### TAA and TSR: jitter is applied after the native view was frozen

Camera/global AA selection reaches the per-frame profile. Temporal Begin,
accumulation, and commit commands execute, and history can become ready. The
missing connection is between the current temporal sample and native geometry:

1. `XRViewport.cs:2217-2268` captures the desktop view before command execution.
   `RenderingState.cs:322-345` assigns the immutable `FrameViewSet`.
   `XRRenderPipelineInstance.cs:853-864` executes the command chain afterward.
2. `AdvancedRenderPipeline.CommandChain.cs:56-64` places temporal Begin before
   native stages, but this is already after view freezing.
3. `VPRC_TemporalAccumulationPass.cs:1197-1215` generates jitter and pushes it
   onto the live camera. Its separate temporal snapshot captures the resulting
   projection at lines 1276-1306.
4. `VPRC_AdvancedRenderStage.cs:167-175` passes the original `state.FrameViewSet`
   to native stages. `BackendReadyFramePackage.Canonical.cs:320-340` and
   `AdvancedViewRecordFactory.cs:11-38` preserve the frozen projection values.
5. Vulkan rebuilds canonical authoring rows from `request.Views` in
   `VulkanPreparedFrameRecording.cs:195-203`, called by
   `VulkanRenderer.CommandBufferRecording.Primary.Preparation.cs:1395-1400`.
   OpenGL uploads the same request views through
   `OpenGLRenderer.AdvancedPipelineStages.cs:154-156` and
   `OpenGLAdvancedSceneTableUploader.cs:183-192`.
6. `Build/CommonAssets/Shaders/Advanced/Visibility/VisibilityRaster.vert:250-256`
   rasterizes with that record's `viewProjectionJittered`. The mesh shader uses
   the same record.

Normally the preceding frame's jitter handle was removed by Pop/Commit, so the
frozen desktop descriptor has zero jitter and identical jittered/unjittered
projections. A pre-existing external jitter would instead be stale relative to
the current temporal sample. Moving Begin ahead of the native stage markers
alone does not fix this ordering.

This is a renderer-neutral source-level defect affecting Vulkan and OpenGL.
It explains active history weighting without the changing subpixel geometry
coverage needed for temporal AA. TSR additionally reprojects using
`PreviousJitterUv - CurrentJitterUv` even though native geometry did not use
those samples. Spatial filtering/upscaling can still change the picture; the
modes are not literal no-op shaders.

### MSAA: the native Advanced path remains single-sampled

`AdvancedRenderPipeline.VisibilityBuffer.cs:508-618` creates native visibility
targets without the selected MSAA count. The native scene color is declared by
`AdvancedRenderPipeline.NativeShading.cs:155-172`. Advanced retains older MSAA
texture/FBO factories, but its current native command chain contains no MSAA
geometry branch or `VPRC_ResolveMsaaGBuffer` execution. The Default pipeline has
those explicit branches.

The live accepted profile reported `aa=Msaa msaa=4`, while resource inspection
reported `samples=1, multisample=false` for visibility identity, metadata,
selection, depth/stencil, and `HDRSceneTex`. Selecting MSAA therefore does not
multisample the rendered native mesh coverage.

### DLAA/vendor paths: selection exists without a vendor resolve command

`AdvancedRenderPipeline.cs:417-456` treats DLAA as a DLSS vendor request and
suppresses the engine post-AA branches when vendor upscaling is selected.
`VPRC_TemporalAccumulationPass.cs:739-740` runs internal accumulation only for
TAA, so DLAA does not fall through to that resolve either.

`AdvancedRenderPipeline.LateAndPostCommands.cs:216-250,415-480` dispatches and
presents TSR, FXAA, SMAA, or the direct final post-process result. It never adds
`VPRC_VendorUpscale`. Compare `DefaultRenderPipeline.cs:2045`, which actually
adds the vendor command. The runtime accepted DLAA without a resource-generation
failure, but selecting it does not execute DLAA in this Advanced output chain.

### Why FXAA and SMAA visibly work

Their explicit commands process the completed scene color and the output
branch presents their results. They require neither geometry jitter nor
multisampled visibility buffers, so they avoid the missing paths above.

## Suggested repair boundaries

- Publish an immutable authoring view set after temporal Begin, deriving its
  current per-view jitter from temporal data while preserving frozen pose,
  unjittered projection, history identity, and XR projection policy. Advanced
  stages should submit that set. Preserve the original logical `FrameViewSet`
  for history-ledger ownership; avoid late mutation or backend-specific patches.
- Implement an explicit native visibility/shading MSAA strategy and resolve,
  or reject MSAA for Advanced until that exists. Merely allocating the unused
  deferred MSAA textures cannot fix it.
- Add the vendor command/output integration, including its existing capability
  failure diagnostics, or reject DLAA/vendor requests in Advanced.
- Validate stillness, camera motion, cuts, and mode transitions on Vulkan and
  OpenGL before treating temporal AA quality as fixed.

## Validation

- Local settings select Vulkan, Advanced, and a TSR camera override. The enabled
  Sponza import is translated to X=-20. Settings files were not changed.
- `rdc doctor` passed the desktop capture/replay checks.
- Named isolated session `advanced-aa-0922` built successfully with zero
  warnings/errors and was stopped after capture. No other editor was stopped.
- Visually inspected captures from two camera positions, including a Sponza
  interior at position (-14,2,0), looking toward (-23,3,0).
- TSR ran at 1920x1080 output and 1286x723 internal resolution. History was
  ready. A direct `TsrOutputTexture` HistoryWeight capture reported minimum
  0.15991211, maximum 0.95996094, and average 0.93771684. This rules out an
  always-disabled history resolve in the sampled scene.
- TAA ran at 1920x1080 with history and exposure history ready. Its debug
  resolve produced scene-shaped history-weight output. This proves resolve
  execution, not correct current-color ownership or spatial sampling.
- Verified live accepted MSAA=4 and DLAA profiles, including the single-sample
  MSAA resource results above.
- Evidence root: `Build/_AgentValidation/20260922-111614-advanced-aa/`.
- Build output, rendering/Vulkan logs, accepted mode/resource snapshots, and
  PNG captures are retained there. No RenderDoc frame was needed to identify
  the frozen-view source boundary; uploaded GPU matrix bytes were not captured.
- Logs contain startup and camera-cut/profile-change reseeding, followed by
  ready history in MCP snapshots. They also contain a startup framebuffer
  construction/publication error and retired-generation fence warnings on AA
  transitions; these are separate from the proven missing jitter connection.
- A broker evidence request was rejected before launch because its repository
  policy excludes `Build/`, which contains the authored shader sources. No
  worker result was accepted; shader inspection continues locally.
- The standard scratch retention command failed while removing an older
  session directory. A subsequent bounded cleanup of an old task evidence root
  was blocked by automatic approval review. Retention cleanup is incomplete.

## Historical runtime anomaly: resolved in the resumed investigation

The TSR-to-TAA transition produced black scene color before diagnostic mode.
After enabling and disabling TAA HistoryWeight, grayscale diagnostic content
persisted and slowly changed instead of being replaced by fresh scene color.
Same-boundary `HDRSceneTex` and `TemporalColorInput` exports had identical hashes
and contained the diagnostic image. Advanced still reported Admitted/Bound.
Switching to MSAA restored the ordinary scene.

The subsequent Vulkan run confirmed a pass-metadata mismatch on the mode
transition. `VPRC_TemporalAccumulationPass.DescribeRenderPass` had selected its
branch from mutable pipeline settings instead of the active resource profile,
and temporal pass-index lookup could fall back to pipeline-wide metadata rather
than the active generation. Both boundaries were corrected. Live TSR-to-TAA
now produces fresh, nonblack HDR color, color input, and history in Vulkan and
OpenGL, including after a camera move.

## Status

The prior pause is recorded below as a historical checkpoint. The resumed
mono-view validation has since confirmed TAA/TSR fresh output and history,
four-sample MSAA mesh-edge coverage on OpenGL and Vulkan, and NGX DLAA dispatch
with valid final color on the available OpenGL/NVIDIA path. Stereo/quad-view
behavior, direct raw-sample inspection, final AA image-quality comparison, and
regression tests remain open in the linked todo.

## Authorized implementation progress

- Temporal Begin now publishes a separate immutable native authoring view set.
  It retains frozen poses, per-view projections, and logical history identity,
  while incorporating the current temporal jitter before either backend uploads
  its canonical view records. The logical history-ledger view set stays separate.
- Corrected perspective jitter to multiply the requested NDC offset by projection
  M34. Adding it directly to M31/M32 reversed the actual shift with M34=-1.
  Mono and stereo TAA history lookup now adds previous-minus-current jitter,
  matching the unjittered motion-vector contract.
- Temporal accumulation is declared as an actual framebuffer with HDR and
  exposure-variance color attachments, matching its factory and the Default
  pipeline. Its prior QuadMaterial declaration omitted those physical outputs.
  Whether this fixes the observed stale-color anomaly still requires capture.
- Advanced desktop output now includes the vendor command for DLAA, with the
  final graded color, native depth, and native motion inputs. Existing explicit
  requested-vendor failure diagnostics remain authoritative.
- Native MSAA retains raw multisample identity, metadata, selection, depth, and
  sample position separately from the canonical single-sample downstream ABI.
  The intended sequence is multisample early/late raster, coherent nearest-sample
  visibility/depth resolve, AO, then full-screen per-sample material shading and
  HDR coverage resolve. MSAA temporarily disables only HZB occlusion rejection;
  late compute still emits all deferred candidates. This is conservative and
  trades culling efficiency for correctness until a multisample reducer exists.
- The per-sample shading evaluator is factored into one shared routine.
  HDR resolves every sample; velocity, material exports, and packed diagnostics
  use the same nearest covered sample as canonical visibility. Reactive coverage
  includes mixed identities and partial coverage.
- Authored background rendering in MSAA fills the uncovered fraction through
  destination-alpha blending. Its original admitted material remains unchanged
  for cameras using other AA modes.
- OpenGL array allocation now uses multisample storage for multisample targets,
  accounts for all samples in VRAM size, and avoids illegal filter/wrap/mip state
  on those images. Native MSAA verifies the realized sample count before drawing.

### Intermediate validation

- Renderer and isolated editor builds reached zero-warning/zero-error boundaries.
  A later OpenGL build including the MSAA dispatch plumbing also passed cleanly.
- One isolated run returned an entirely black viewport because Advanced admission
  prewarmed `ShadeNativeOpaqueMsaa.comp` before that new shader file existed.
  MCP reported the missing asset explicitly; this run is not evidence about the
  temporal fixes. The owned session was stopped pending completion of the shader.
- An independent broker inventory completed with requested and actual model both
  `gpt-5.6-luna`. Its useful framebuffer-declaration observation was checked in
  source; its speculative physical-alias inference was not accepted as proof.

## Pause checkpoint: 2026-09-22

### Implemented and checked

- TAA/TSR: immutable temporal authoring views, corrected perspective jitter sign,
  and TAA previous-minus-current jitter reprojection. Source review found the
  mono and ordinary stereo ownership consistent. Quad-view temporal post state
  remains unproven because it still has only left/right eye slots.
- TAA: corrected the accumulation framebuffer resource declaration. This is a
  concrete declaration/factory mismatch fix, not proof that the stale-color
  symptom is resolved.
- DLAA: connected Advanced output to the existing vendor dispatch command and
  its color/depth/motion inputs. Actual vendor dispatch and output remain to be
  verified on the live supported hardware path.
- MSAA: raw multisample visibility/depth/sample-position resources, per-sample
  material/lighting evaluation, coverage-aware HDR resolve, coherent nearest
  sample sidecars, and OpenGL/Vulkan raster and graphics resolve plumbing.
- OpenGL: multisample array storage allocation and native realized-sample checks.
- Vulkan: four-color raw raster validation, sample-rate shading, descriptor ABI
  bindings 55-59, per-view graphics resolve, and a conservative late-visibility
  path that skips pre-resolve HZB rejection while retaining deferred candidates.
- Targeted OpenGL and Vulkan project builds passed with zero warnings/errors.
  An intermediate isolated editor build also passed, but it predates the final
  Vulkan resolve and descriptor validation changes.
- Both native shading drivers passed OpenGL engine-preprocessed GLSL syntax
  validation and Vulkan SPIR-V compilation in frozen 2D and array variants.
  Evidence is in `reports/shader-gl-compile.log` and
  `reports/shader-vulkan-compile.log` under the task evidence root.

### Remaining work at pause (historical)

1. Correct profile-dependent render-graph descriptions. Raw resources are only
   declared for MSAA, but `VPRC_AdvancedRenderStage.DescribeRenderPass` currently
   declares raw accesses and a synthetic multisample resolve for every profile.
   Vulkan filters absent resource uses but retains the phantom resolve's writes
   to canonical resources. Select conditional pass/usage groups at the active
   resource-layout/generation boundary, for example by presence of the raw
   multisample framebuffer. A static flag on the shared command is insufficient:
   AA changes invalidate resources without rebuilding the command metadata.
   Describe only passes/resources that execute; non-MSAA AO/classification must
   depend on late raster directly. MSAA raster must not claim canonical writes.
2. Diagnose the OpenGL MSAA stage-order rejection. The last live run admitted
   `aa=Msaa msaa=4`, with `executionAdmitted=true`, `bindingState=Bound`, and no
   resource-generation failure, but logged that `MultisampleResolve` did not
   match its sealed family or required per-view order. Determine whether an
   earlier asynchronous shader/admission failure or an ordering defect caused
   it; successful profile admission alone is not a successful rendered frame.
3. Fix background coverage contracts. The new destination-alpha blend requires
   explicit opaque source alpha, but the inspected skybox shaders output `vec3`.
   Make admitted sky output alpha one and encode that guarantee in admission,
   or render backgrounds separately and composite once. Multiple backgrounds
   also require a deliberate composition policy. Background validation currently
   checks submaterials while cached render state comes from the primary material;
   reject multi-material background draws or resolve state per actual subdraw.
4. Make GL array allocation identity include sample count and fixed-sample
   locations. Current tracking covers format/size/mips only; the new immutable
   resource generations usually avoid reuse, but changing sample count on a
   surviving object would retain incompatible storage.
5. Rebuild the isolated editor with the final integrated changes. A Vulkan run
   before the final fixes failed ABI validation at binding 55; that source rule
   is now fixed, but the corrected Vulkan path has not been rerun.
6. Validate all four modes on OpenGL and Vulkan using Sponza still views, camera
   motion, cuts, and mode transitions. Verify actual multisample attachments and
   coverage changes, current/previous jitter, fresh TAA color, and DLAA vendor
   execution. Capture and inspect GPU resources with RenderDoc if the TAA stale
   or black color anomaly persists.
7. After live feature validation, obtain the user's explicit clearance before
   adding/modifying regression tests. Existing alpha-to-coverage phase tests
   include expectations for the old TAA jitter omission and will need review.

### Session and evidence state

- Owned session `advanced-aa-0922` was stopped successfully at wrap-up. No other
  editor was stopped. All implementation edits are preserved and uncommitted.
- The final live OpenGL process used a scratch settings copy selecting MSAA;
  the root Unit Testing World settings were left unchanged.
- Final OpenGL session logs are under
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260922-111628-advanced-aa-0922/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-09-22_12-19-02_pid38848/`.
- The last observed resolve rejection was at 12:20:59.625. Native opaque shader
  linking took approximately 55 seconds during startup, so the next run must
  distinguish startup admission from steady-state frame execution.
- No final before/after visual result or user confirmation exists yet for any
  of these four AA fixes. No new tests were created or modified.

## Resumed implementation and live validation: 2026-09-22

### Additional fixes

- The Advanced render-stage graph now selects declarations from the active
  resource generation. In MSAA it describes raw early/late raster, a coherent
  visibility/depth resolve, then canonical AO/classification. In non-MSAA it
  describes direct canonical raster and no raw resources or phantom resolve.
  The editor's `get_render_state` now exposes `activeViewportResourcePasses` so
  the active generation can be inspected separately from the command chain.
- Vulkan's Advanced resolve request now matches the sealed stage family, and
  OpenGL no longer rejects the correctly admitted multisample resolve after
  startup shader linking. The GPU-scene publisher reclaims acknowledged
  tombstones before capacity preflight.
- A Streamline sidecar remains reusable when DLAA is switched off and back on.
  The previous second initialization crashed the native process; two live
  off/on transitions completed without that crash. No dependency was upgraded.
- Temporal accumulation now chooses its pass declaration using the active
  resource profile. Its pass-index lookup consults active-generation metadata.
  Both changes were required to remove the Vulkan TSR-to-TAA black frame.

### Observed mono-view results

| Path | OpenGL | Vulkan |
| --- | --- | --- |
| MSAA 4x | Raw visibility/depth descriptors report four samples; an HDR mesh-edge crop contains 14 intermediate pixels where AA-off is black. | Four-sample raw descriptors and active resolve graph; an HDR edge crop contains 12 intermediate pixels where AA-off is black. |
| TAA | Fresh nonblack color input, accumulated HDR, and history after settling and camera motion; TSR-to-TAA stays live. | Fresh nonblack color input, accumulated HDR, and history after settling and camera motion; TSR-to-TAA stays live. |
| TSR | Populated upscale output at still and moved camera positions; history ready. | Populated upscale output and history; TSR-to-TAA transition stays live. |
| DLAA | NVIDIA Streamline NGX DLAA instance creation and evaluation observed. Two DLAA off/on transitions survived and Sponza produced nonblack final output. | Vendor dispatch was not exercised in the Vulkan session; do not infer Vulkan DLAA support from OpenGL results. |

For OpenGL MSAA, the representative edge pixel at (1252, 879) is RGB
`(60, 56, 49)` with MSAA versus `(0, 0, 0)` with AA off; (1257, 880) is
`(59, 56, 50)` versus black. The corresponding Vulkan edge crop had 12
partially covered pixels versus none without AA. These comparisons prove a
real change in mesh-edge coverage, not full image-quality or per-sample
sidecar correctness. Both backends reported active frame completion after
shader and resource startup. OpenGL DLAA final post-process color was
populated, and the viewport screenshot showed Sponza, although the scene was
overexposed.

Evidence remains under `Build/_AgentValidation/20260922-111614-advanced-aa/`:

- `mcp-captures/vulkan-msaa-vs-none-edge.png` compares the Vulkan edge.
  Vulkan TAA and TSR examples are
  `RenderPipeline_HDRSceneTex_20260922_154029.png`,
  `RenderPipeline_HDRSceneTex_20260922_154129.png`,
  `RenderPipeline_TsrOutputTexture_20260922_154143.png`, and
  `RenderPipeline_HDRSceneTex_20260922_154157.png` in `mcp-captures/`.
- OpenGL MSAA/None examples are
  `mcp-captures/RenderPipeline_HDRSceneTex_20260922_155117.png` and
  `mcp-captures/RenderPipeline_HDRSceneTex_20260922_155136.png`.
  TAA/TSR examples are the `155239`, `155251`, `155309`, `155318`, and
  `155331` captures with their corresponding full filenames in that folder.
  The DLAA final image is
  `mcp-captures/Screenshot_20260922_154802_488_9b03e1dea4ef4a6cb7ca5cd7a9252a18.png`.
- The final named Vulkan session `advanced-aa-vulkan-0922` was built with zero
  warnings/errors. Its active generation changed from MSAA to None to TAA
  and back to MSAA without a recurring metadata warning. At frame 3596 it
  reported `aa=Msaa msaa=4` with 74 active resource passes, including the raw
  resolve and AO. It was stopped through the named session manager. The named
  OpenGL session `advanced-aa-gl-final-0922` likewise completed steady-state
  frames after initial shader linking and was stopped. The ignored local world
  settings were restored from their pre-validation backup, with matching hash.

### Limits and follow-up

- MCP readback rejects raw multisample textures, so the raw sample contents,
  canonical nearest-sample sidecars, and exact coverage/depth choice were not
  independently inspected. A direct RenderDoc launch completed engine frames,
  but its manual trigger disconnected before writing an `.rdc` file. That run
  also emitted `without metadata: 100065`; the later named Vulkan session did
  not reproduce it after MSAA/None/TAA/MSAA switches. Reproduce the warning
  under capture conditions before attributing it to the AA changes.
- One Vulkan startup frame in the final named session reported `XRFrameBuffer`
  wrapping before CPU construction publication. It did not recur during the
  settled AA mode transitions. Keep it separate from the fixed black TAA frame.
- The measured captures establish execution, fresh output, and MSAA edge
  coverage. A controlled AA-quality comparison for still/moving/cut scenes,
  including ghosting and partial background coverage, remains. Ordinary stereo
  and quad-view need XR validation; the desktop sessions were mono only, and
  quad-view temporal post state still has left/right eye slots only.
- No tests were added or changed. Repository policy requires live feature
  validation and the user's explicit clearance before regression test work.
