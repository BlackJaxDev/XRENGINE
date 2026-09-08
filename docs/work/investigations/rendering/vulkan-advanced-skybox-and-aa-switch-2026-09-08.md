# Vulkan Advanced pipeline: Default World skybox and AA freeze

Opened: 2026-09-08  
Status: Fixed and validated in an isolated normal Default World launch with AdvancedRenderPipeline; user acceptance pending

## Report and scope

The normal **Editor (Default World)** launch shows the grid floor without the
expected skybox. Bloom and TSR appear to work. Switching anti-aliasing from TSR
to FXAA froze the user's last run. The user confirmed the normal launch, not the
Unit Testing World.

Focus on these regressions before resuming the Vulkan XR/Advanced rendering
backlog. Preserve the existing TODO edits. No tests are added or modified;
regression test work requires completed runtime validation and user clearance.

## Verified outcome

The final editor build succeeds with **0 warnings and 0 errors**. The normal
Default World can switch from DefaultRenderPipeline to a fresh Advanced camera
asset, render its gradient skybox and grid, switch TSR/FXAA repeatedly, and
replace Advanced with another fresh Advanced asset without a terminal freeze.
No global Advanced-enabling environment override or local settings change is
needed for explicit camera asset selection.

Confirmed causes and fixes:

- **Advanced asset-switch freeze:** the transparent, CPU-authored infinite grid
  was classified as opaque. Advanced's package required that draw, but its
  command chain correctly omitted legacy opaque replay. Vulkan rejected the
  unmatched package exception and paused presentation. The grid now declares
  the existing sorted-alpha late stage. Exact package matching stays enforced.
- **AA freeze:** stale frozen-history dimensions prevented the pending resource
  generation from advancing. Current FXAA commands could also see old TSR
  resources. Advance generation work before rejecting old history, and require
  the current profile's exact generation before command execution. Interactive
  resize reuse also checks all non-size profile fields.
- **Advanced black sky:** output binding incorrectly treated the policy for
  selecting the default pipeline as a gate on an explicitly assigned Advanced
  asset. After correcting binding, zero opaque draws still suppressed native
  initialization, leaving sky depth tests against depth 0. A valid empty native
  family now clears and initializes its output with zero logical draws and
  valid GPU backing. RenderDoc confirms the sky passes at far depth 1.
- **Cold UI readiness:** descriptor/layout materialization could change the UI
  pipeline key after pre-acquire preparation. Secondary recording now honors
  exact PresentNow foreground readiness for that final key instead of returning
  a terminal failure while its compilation completes.

The separate DefaultRenderPipeline clear/load defect found before the pipeline
scope correction is also fixed and recorded below.

Final runtime evidence:

- Twelve alternating Advanced AA transitions passed: six with a native cube,
  four with no opaque geometry, and two on the final source under
  `StandardValidation`. The final two took 5.180 seconds to FXAA and 3.286
  seconds back to TSR; resource generations 5 and 6 committed and history
  continued advancing.
- The final StandardValidation run admitted native stages with `drawCount: 0`,
  a bound current reservation, and no pending generation failure. A second
  Advanced asset assignment committed revision 3/generation 8. Horizon and
  raised-view PNGs were both inspected and show the expected changing sky.
- An earlier final-feature run passed the empty -> cube -> empty transition:
  the cube appeared, then disappeared without stale geometry.
- The final pre-shutdown Vulkan log has **zero terminal transitions** and only
  the two baseline startup errors for unknown pNext structure types 1000135008
  and 1000135009 in Vulkan SDK validation 1.4.328.1. The earlier depth-only grid
  interface error is absent. This is not a claim of globally clean validation.
- The owned `skybox-aa-debug` session is stopped after preserving evidence.
  Builds and runtime checks used isolated outputs; the normal editor needs
  rebuilding/relaunching to consume the changes. No tests were added, modified,
  or run during this feature-debugging task.

Remaining scope: observed resource rebuild pauses are roughly 2–6 seconds.
Existing immutable temporal-snapshot warnings mean these checks establish
switching stability and visible output, not TSR reconstruction quality. The
tested profile is desktop, single-view, `hdr=False`; HDR-specific and XR/stereo
behavior were not certified. Serialized custom command-chain round trips and
raw ingress reason-flag categorization remain separate unvalidated follow-ups.
The user has not yet evaluated the final fixes.

To reproduce through the normal editor: launch Default World with Vulkan,
assign a fresh AdvancedRenderPipeline asset to the camera, inspect the horizon
and look upward, alternate TSR and FXAA, add/remove one cube, then assign
another fresh Advanced asset. Each step should resume frame presentation with
the expected scene and no terminal renderer error.

The remaining sections retain the investigation chronology, including failed
hypotheses and earlier partial results; this section is the final status.

## Corrected pipeline scope and live Advanced failure

The user clarified during validation that the intended camera asset is
**AdvancedRenderPipeline**. Default World identifies the scene, not the render
pipeline. Initial diagnosis mistakenly used DefaultRenderPipeline. Its two
confirmed fixes below are useful separate defects, but are not evidence that
Advanced's skybox and AA problems are resolved.

The user switched the investigation camera to Advanced at approximately 12:14
and reported another freeze. The saved Vulkan log identifies a terminal
submission failure at frame 10850, 12:14:16.076:

`Prepared mesh ingress exceeded its fixed resource-use capacity.`

The initial error text combines several different failures and does not prove
capacity exhaustion. A diagnostic-only rebuild split finalization, stable-bin
construction, and manifest resolution. A clean Default-to-fresh-Advanced switch
then failed specifically in **stable-bin stream creation**, before manifest
resolution, with 14 ingress entries. At this point capacity, ordering, and exact
package exception matching still needed to be distinguished at the failing
entry; the grid diagnosis below
resolves that ambiguity.

Reproduction used a fresh Advanced asset, matching the inspector. A serialized
asset attempt also rejected the Advanced output reservation; fresh assets later
reproduced that rejection too, identifying output binding as its cause.
Serialized custom command-chain round trips remain unvalidated. A fresh
Advanced-to-Advanced transition also exposed a distinct cold UI
secondary-recording readiness failure, fixed and validated later below.

The failure occurs at `FramePlanSeal`, ticket `prepared-mesh-ingress`, while
lowering `DesktopScene` dependencies. Vulkan marks the renderer paused/terminal.
The active resource generation subsequently commits successfully (including
FXAA), but no frame after history sequence 10849 commits; three pending history
records accumulate. The terminal submission error is the primary cause of this
Advanced freeze, not evidence that its resource transaction remains stalled.

Evidence: `advanced-switch-frozen-state.json`, `advanced-switch-log_vulkan.log`,
and `advanced-switch-log_rendering.log` under the disposable evidence root.
The owned editor was stopped after preserving that state. Subsequent sections
record the ingress fix and Advanced skybox, AA, and asset-switch validation.

## Separate Default pipeline skybox defect: inactive branch clears the active scene

`BootstrapWorldFactory.CreateDefaultEmptyWorld` always calls
`BootstrapModelBuilder.AddSkybox(rootNode, null)`, which creates a gradient
skybox. The Unit Testing settings' `Skybox: false` does not control this world.

Two baseline camera poses reproduced the problem: a level view showed grid and
black sky, and a raised view showed black throughout. Pipeline texture captures
also showed black `HDRSceneTex`, so the error precedes bloom and TSR.

RenderDoc capture `baseline-explicit141_frame648.rdc` identifies the failure:

- Event 169 draws `Skybox.Gradient` into the forward color target. Pixel history
  at (640, 180) records a successful blue output of approximately
  (0.3501, 0.4941, 0.6675), with depth 1. The skybox scope loads color and depth.
- Event 180 begins the following grid scope with **Clear** for both color and
  depth/stencil. This erases the skybox before grid event 186, which discards
  that sky pixel. The shader and its far-depth policy are functioning.
- `VPRC_IfElse.DescribeRenderPass` describes both scene-workload branches. The
  callback-only branch declares an initial clear on `ForwardPassFBO` using the
  same `OpaqueForward` graph pass index as the full scene. Its later attachment
  declarations overwrite the full scene's load behavior when Vulkan resolves
  that pass's attachment signature.

Attempted fix: assign the callback-only opaque command its own
`CallbackOnlyOpaqueForward` graph identity. It still executes the same mesh
bucket callbacks, but its initial clears cannot replace the normal forward
pass's attachment loads. Keep intentional clears and Vulkan pass reentry rules.

## Shared AA defect reproduced on Default: resource generation starved by history rejection

The user's log session is
`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-09-08_11-26-48_pid44896/`.
At the AA change, the active generation is TSR at 1286x723, while pending FXAA
needs 1920x1080 and a different resource set. The logs report missing
`FxaaOutputTexture`, frozen-history extent mismatches, and repeated Vulkan
preflight rejection. The pending build stops progressing; three pending history
records and the last committed history sequence remain unchanged.

An isolated Default World run reproduced this with the camera's
`AntiAliasingModeOverride` changed to `Fxaa` through MCP, the same property used
by the editor. The process still answered MCP; presentation was stalled rather
than the entire application becoming unresponsive. More than 65,000 blocked
iterations left the pending generation in `Created` after 22 seconds.

`XRRenderPipelineInstance.TryRenderCore` rejected stale frozen history before
calling `EnsureResourceGenerationForCurrentFrame`. The resource rebuild needed
to resolve that mismatch therefore stopped receiving work. A separate permissive
check allowed current-profile commands to run against an older generation when
AA/features changed within the same pipeline revision.

Attempted fix:

- Advance the resource transaction inside the installed pipeline/camera/render
  state, before rejecting or reserving frozen history and output work.
- Require the active resource generation to match the current frame profile
  exactly before executing the command chain. This prevents an FXAA command
  path from looking up resources in the previous TSR registry.
- Retain exact history/output identity guards and ownership cleanup. Do not
  publish a generation from Vulkan preflight after commands have been authored.

## Reproduction and evidence

Use the isolated named editor session `skybox-aa-debug`, with `-NoUnitTesting`
and `XRE_WORLD_MODE=Default`, matching the reported launch. Only stop this
investigation's named session.

Disposable evidence root:
`Build/_AgentValidation/20260908-113838-vulkan-skybox-aa/`.
Session builds/logs:
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260908-113911-skybox-aa-debug/`.

Relevant evidence includes `baseline-horizon`, `baseline-up`, and
`baseline-inputs` in `mcp-captures/`; AA state snapshots and transition replies
in `mcp-output/`; and `skybox-erased-pixel-history.json` plus
`grid-pass-clear.json` in `renderdoc/`. Saved PNGs were visually inspected.

RenderDoc tooling: the installed capture layer is 1.44 but `rdc`'s Python replay
runtime is 1.41; a 1.44 capture cannot be opened by this replay runtime. For this
session only, `VK_IMPLICIT_LAYER_PATH` points at the existing matching 1.41
runtime, and `ENABLE_VULKAN_RENDERDOC_CAPTURE=1` enables it. No installation or
registry change was made. `RenderDocFriendly` is used for capture; a separate
standard-validation run subsequently passed the targeted checks recorded above.

## Initial validation before the Advanced fixes

- Baseline isolated editor build: succeeded, 0 warnings and 0 errors.
- Both reported regressions: reproduced on the normal Default World path.
- Fixed editor build: succeeded, 0 warnings and 0 errors. Default pipeline horizon and raised-view PNGs now show the gradient; grid remains visible.
- Default pipeline: five TSR/FXAA transitions passed, with generations committed, no pending failures, and history commits advancing. RenderDoc confirms grid color/depth load operations changed from Clear to Load.
- At this stage Advanced skybox and AA validation were pending; the user-triggered asset switch exposed the terminal ingress failure above. Final Advanced results are recorded at the top of this document.
- Standard-validation startup warnings also occur in the pre-fix baseline (unknown pNext extensions and depth-only shader interface locations); no clean global Vulkan validation claim is made.
- HDR-specific validation is unconfirmed; the reproduced profile has hdr=False. MCP does not expose the camera HDR override.

User acceptance remains pending for the final changes.

## Advanced switch: exact unmatched grid publication

Freshly constructing and assigning an Advanced asset reproduces the first-frame
terminal failure consistently. The expanded diagnostic rules out capacity and
resource-manifest exhaustion: `PackageExceptionUnmatchedHandle`, one package
exception, fourteen prepared mesh entries, and zero stable-bin records before
failure. The selected package belongs to Advanced and its active resource
generation has already committed.

At 12:50:29, frame 637, the rejected record is draw handle index 1/generation 1,
view 0, pass 3 (`OpaqueForward`), order key 17179869184, reason flags 3146002,
compatibility reason `None`. Default World has exactly one mesh in this bucket:
`InfiniteGrid.FullscreenTriangle`. Its command is forced CPU and its material is
excluded from GPU indirect, alpha blended, and depth-read-only, but it was
authored as opaque forward. Advanced intentionally has no legacy opaque-forward
CPU replay stage. The scene package therefore advertises a compatibility draw
that the executed Advanced chain never prepares; Vulkan treats that contract
mismatch as terminal, leaving the last frame displayed.

Attempt underway: author the grid as `TransparentForward` with explicit
`AlphaBlend` and `AdvancedLatePassMetadata(SortedAlpha)`. This uses Advanced's
existing admitted late stage and preserves native opaque ownership. It also
stops the fullscreen grid from masquerading as opaque native/shadow input.
No exception matching guard is bypassed. Runtime validation follows below.

A separate asset roundtrip issue was found during reproduction: serialized
Advanced command trees lose runtime delegates when reloaded. Fresh constructor
reproduction is used above to isolate the grid failure. Asset reload validation
and a suitable generated-command restoration fix remain open.
### Grid fix runtime result and remaining black output

Build 12:52 succeeded with zero warnings/errors. Fresh Advanced assignment no
longer terminates the renderer. History commits progress from sequence 250 to
2121 and onward; TSR to FXAA commits resource generation 5 without starvation.
The inspected viewport remains black, including after adding a native cube.

RenderDoc `advanced-grid-late_frame2082.rdc` contains authored sky/grid and
post-processing, but no native Advanced stages. Sky event 28 computes valid blue
RGB (approximately 0.172, 0.236, 0.318), yet fails depth testing against depth 0.
The capture explains the black sky: the native stage never initializes the
shared depth surface. The sky shader is not the source of this failure.

Runtime profile diagnostics report admission `Admitted`, binding `Disabled`,
reservation current false, renderer backend generation 0, all 8 output banks
free, and no activation attempts/failures. Native stages log that the Advanced
output reservation is not current. This also happens with a freshly constructed
asset, so the earlier suspected serialization issue does not explain it.
Investigation is focused on binding initialization after camera pipeline asset
replacement. Evidence is in `advanced-grid-fixed-black-log_*.log`,
`advanced-grid-fxaa-state.json`, and `advanced-black-sky-pixel.json` under the
current disposable run root. No new tests were added or modified.
### Output binding cause: default selection policy reused as an execution gate

`BootstrapWorldFactory.CreateDefaultEmptyWorld` calls the shared
`BootstrapRenderSettings.Apply`, including pipeline selection. Thus the local
`UnitTestingWorldSettings.Rendering.UseAdvancedRenderPipeline: false` selects
`EAdvancedRenderPipelineMode.Disabled` even in the reported normal launch.

The selection factory correctly creates Default under that policy. The later
`ApplyRenderPipelineOutputBinding` path, however, applies the same policy after
a camera explicitly receives an Advanced source asset: it sets the binding to
`Disabled` without reserving native resources, while the Advanced command chain
continues running background/late/post commands. The diagnostic then incorrectly
calls that disabled state a stale renderer reservation.

Chosen fix: keep Disabled/Diagnostic factory-selection behavior, but realize an
explicitly configured Advanced camera source under Available (Required remains
Required, as do explicit offscreen outputs). Camera asset selection is already
the source authority; output binding must validate and bind that selected source.
No global preference or local settings file is changed. OpenXR's independently
owned eye policy is unchanged. Correct policy-disabled diagnostics separately.
This is a deliberate behavior correction for manual camera pipeline selection;
runtime validation follows in the normal launch without an enabling env override.
### Bound Advanced: empty-native-scene rejection and AA validation

The explicit camera binding fix builds with zero warnings/errors. Without any
Advanced-enabling environment override, the same normal launch now reports
`Bound`, reservation current true, backend generation 1, one active output bank.
A second blocker then becomes visible: all native stages reject the otherwise
valid publication because `DrawCount == 0`. The normal world contains only the
background and transparent grid, so it needs a valid empty native pass that
clears depth/identities and initializes HDR/temporal sidecars.

Adding one temporary cube makes native visibility and shading enqueue correctly.
Actual captures then show the cube, blue gradient and grid, with distinct horizon
and raised-camera views. This isolates zero-native-draw handling from binding.

Six Advanced AA changes with that native cube passed (FXAA/TSR alternating):
resource generations 5 through 10 committed, native admission remained true,
frame history continued committing, and the Vulkan log contained no terminal
transition/error. Observed MCP transition times were 6.103, 3.045, 4.119, 2.938,
5.254 and 2.896 seconds. These resource rebuild stalls remain a performance
limitation; they are distinct from the former permanent freeze. The final image
and both camera views were inspected. This validates switching and current-frame
output, not temporal reconstruction quality: existing immutable temporal
snapshot warnings still occur.

Separately, the earlier cold Advanced-to-Advanced run exposed dynamic UI
compiling a second pipeline key after descriptor/layout materialization. The
pre-acquire key was ready; secondary recording queued the new key and returned
false, which was escalated to terminal despite compilation succeeding 15 ms
later. A targeted fix makes this secondary path honor exact PresentNow
foreground readiness after materializing frame data, before beginning the
secondary command buffer. Nonretryable failure handling is unchanged.
### Empty-family runtime acceptance

Build 13:23 succeeded with zero warnings/errors. The unmodified normal launch
still creates Default first; assigning a fresh Advanced asset activates native
output. With `drawCount: 0`, visibility preparation/raster and native shading
are accepted, and the inspected image shows the gradient and grid.

Four additional AA changes on the actual empty Default World passed, alternating
FXAA/TSR at generations 5–8 (5.245, 2.444, 2.471, 2.364 seconds as observed by MCP).
Adding a temporary cube produces draw count 1 and visible geometry. Deleting
that exact temporary node returns to draw count 0 and removes the cube from the
image, confirming native clears are current. Replacing Advanced with another
fresh Advanced asset succeeds at assignment revision 3/resource generation 10.
No terminal renderer transition occurs in this run.

RenderDoc `advanced-empty-fixed_frame1793.rdc` independently confirms the sky
pixel now passes: event 422, pixel (640,180), depth 1, RGB approximately
(0.3501,0.4944,0.6675). Before empty-family support, the analogous sky draw failed
against depth 0. The capture includes the native initialization preceding sky.

Final review tightens two boundaries: every realized visibility family must
validate its geometry/overlay closure even when empty; interactive window resize
may reuse only an exact or dimension-only resource key, including output kind
and color/depth formats. An AA or feature change cannot use that resize shortcut.
The final StandardValidation run passed the targeted checks; its results are recorded at the top of this document.