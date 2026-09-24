# Advanced Vulkan TSR jitter and aliasing

## Report and scope

The user still observes jitter and aliasing with TSR on Vulkan using the
Advanced render pipeline. The current unit-world settings select Vulkan,
AdvancedRenderPipeline, desktop presentation, and the Tsr camera AA override.
The stable-grid, Vulkan displacement, and history-depth fixes are implemented
for Advanced TSR. No test files were changed. Existing uncommitted
scene-publication work is preserved. User confirmation remains outstanding.

## Original source findings

### Resolve output remains aligned with the jittered current image

`Build/CommonAssets/Shaders/Scene3D/TemporalSuperResolution.fs` samples current
color at framebuffer `uv`. It reprojects history with
`uv - velocity * 0.5 + PreviousJitterUv - CurrentJitterUv`, but does not offset
current-frame reconstruction onto a stable output grid. The stereo shader uses
the same convention.

For a static continuous signal with a positive projected displacement `j`, the
current raster represents `C(x - j_current)`. Sampling the preceding jittered
history at `x + j_previous - j_current` also produces `C(x - j_current)`.
Blending these aligned samples therefore retains the current jitter. History
readiness alone cannot establish stable output. This is a mathematical
source-level finding; its contribution to the reported image requires live
comparison.

### Vulkan framebuffer Y mapping is absent from TSR displacement conversion

`Runtime/RenderClipSpaceSettings.cs` in the rendering runtime defines Vulkan
framebuffer-texture Y as opposite the logical clip-space Y convention. The
default is Y-up clip space with a negative-height Vulkan viewport and Y-down
framebuffer-texture coordinates.

Advanced reconstruction writes `surface.motionNdc` from unjittered clip-space
current-minus-previous positions; native shading stores it directly in
`Velocity`. Advanced post-processing supplies jitter in positive clip-axis
texels divided by internal extent. TSR obtains framebuffer UV from
`gl_FragCoord` and applies both offsets directly, without the required Y-axis
conversion. Inspect actual GPU constants and descriptors before treating this
as a complete diagnosis of a particular live frame.

### TSR reads current-frame history depth alongside previous-frame color

`AdvancedRenderPipeline.LateAndPostCommands.cs` schedules temporal accumulation
before post-AA. `VPRC_TemporalAccumulationPass.ExecuteAccumulation` runs the
internal resolve only for TAA; TSR takes the passthrough branch that copies
both current color and depth into the history framebuffer before TSR executes.

`AdvancedRenderPipeline.FBOs.cs` attaches `HistoryDepthStencil` to the history
capture framebuffer and binds its depth view as TSR's `HistoryDepth` input.
`AdvancedRenderPipeline.Textures.cs` confirms that view references the same
storage. Full-resolution TSR color history is updated only after TSR runs.
Thus successful ordered execution supplies frame N depth with frame N-1 color
to the history rejection step. This can reject or accept the wrong history at
moving silhouettes and newly exposed surfaces.

## Additional quality constraints and ruled-out assumptions

- TSR uses a fixed eight-sample sequence scaled by `0.20`; offsets span only
  approximately +/-0.175 internal pixels. This limits subpixel coverage and is
  a secondary quality concern, not evidence that increasing jitter alone fixes
  the defects above.
- Desktop `RenderFrameViewSetCapture` constructs view rectangles from internal
  viewport dimensions. The suspected full-resolution versus internal-resolution
  jitter normalization mismatch is not established for this desktop path.
- Earlier TSR acceptance in `vulkan-phase67-implementation.md` exercised native
  OpenGL Advanced. It explicitly did not certify Vulkan layered rendering and
  does not establish aliasing or jitter quality for this Vulkan desktop path.
- A bounded independent broker review completed with requested and actual model
  `gpt-6-astra`, confirming the history-copy ordering finding. Its binding-timing
  concern remains a hypothesis, not a measured Vulkan upload defect.

## Baseline validation

- `rdc doctor` passes for the installed Windows replay/capture tooling.
- The named isolated session is `tsr-vulkan-jitter`.
- The first build was interrupted when validation cleanup treated the building
  session as inactive and removed intermediate files. Restarted serially; the
  isolated editor build passed with zero warnings and zero errors.
- Live Vulkan Advanced mono execution was admitted at 1920x1080 output and
  1286x723 internal resolution (the global 0.67 TSR scale). History readiness
  was true, with seeded/reset generation 1/1. The exposure-history flag was
  false, which is expected for TSR.
- Captured and viewed viewport PNGs from the initial camera and from position
  (5, 3, 8), looking at the origin. The images changed with the camera. The
  scene contains a large black foreground silhouette over the environment;
  this session is not an acceptance fixture for general material shading.
- An eight-frame sequence completed for render frames 1635 through 1642 with
  no failed or dropped captures. Its contact sheet was viewed. Auto-exposure
  was active and luminance increased through this sequence, so whole-image
  differences do not isolate jitter.
- Texture capture reports `render_backend=Vulkan`, `clip_space_y_direction=YUp`,
  and `framebuffer_texture_y_direction=YDown`. Stationary velocity and the
  reactive mask were exactly zero. TSR output was finite. Current and history
  depth had identical float hashes at the post-render capture boundary; this
  alone cannot prove the earlier in-frame copy order.
- The viewed TSR HistoryWeight debug capture ranged from 0.8671875 to
  0.95996094, with average 0.9595829. This rules out globally disabled history
  as the explanation in this stationary session. The debug setting was
  restored to Disabled before stopping the session.
- The session was stopped through its named manager. Rendering and Vulkan
  logs contained no VUID reports, error/exception matches, or incomplete
  history-coverage warnings. Two history-unavailable warnings corresponded
  to startup and the explicit camera cut. This is not a claim that validation
  layers were enabled.
- Evidence is under
  `Build/_AgentValidation/20260924-120600-tsr-vulkan-jitter/`; session logs are
  under `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260924-120317-tsr-vulkan-jitter/logs/`.
  Automatic approval review blocked manual pruning of an older ignored run;
  that folder was left intact, temporarily exceeding the immediate-directory
  retention limit by one. No alternate deletion was attempted.
- Per-draw RenderDoc constants/image-writer inspection and controlled fix
  comparisons were not performed in the baseline investigation.

## Implemented contract

For stable output UV `U`, clip-axis jitter `j`, and current-minus-previous NDC
motion `v`, let `T` flip Y when framebuffer textures are Y-down:

- Current source UV: `U + T(j_current)`.
- Previous stable color UV: `H = U - T(v / 2)`.
- Previous raw depth UV: `H + T(j_previous)`.

Both mono and stereo shaders convert the offsets, validate color/depth UVs
independently, and clamp against the corresponding texture dimensions. The
conversion does not change jitter amplitude or history/sharpening settings.
Advanced material creation and refresh both select `XR_TSR_STABLE_OUTPUT`.
The deferred-depth gate also includes RVC's admitted Advanced two-pass eye
family, whose commands are rebound to the RVC host while using Advanced shaders.
Advanced draws its overlays before popping jitter, so stencil-tagged overlays
use the same shifted source UV; their mask disables history blending and
spatial reconstruction rather than selecting a different coordinate grid.

Advanced TSR's early passthrough now copies only internal history color.
The post-TSR command copies full-resolution color and then raw current depth,
checking backend acceptance before recording layer coverage. Rendergraph
transfer declarations match that ordering. A rejected copy leaves history
coverage incomplete instead of admitting mismatched history.

Default retains its existing color grid and early depth-capture timing. Its
unjittered overlays are already composited before TSR, and some deliberately
disable stencil tagging. Its transform-tool rotation sphere can also write
depth after jitter is popped. Applying Advanced's full-image de-jitter or late
depth copy there would introduce a separate regression. Default still uses the
shared framebuffer-axis displacement conversion, but a complete stable-grid
conversion requires separating its overlay composition.

An independent source review identified these pipeline differences; a bounded
broker review confirmed the stable-color/raw-depth coordinate formulas. Neither
review substitutes for live image validation.

## Fix validation

- All eight shader combinations compile: mono/stereo, Advanced/Default, and
  OpenGL/Vulkan. Vulkan variants pass Vulkan 1.3 SPIR-V generation. This is
  compilation coverage; stereo and OpenGL were not run live.
- The named Vulkan Advanced editor ran with Khronos validation enabled, fixed
  exposure, and both 0.67 and 1.0 render scale. Stationary velocity/reactive
  inputs were zero, TSR output was finite, and history became ready at both
  resolutions, including after scale invalidation/reseeding.
- Viewed color captures and contact sheets at camera positions (5, 3, 8) and
  (5, 4, 8), plus an eight-frame vertical-camera-motion sequence. The first
  stationary 0.67 capture requested 16 frames and stopped explicitly on
  readback backpressure after nine consecutive frames; no silent drops occurred.
  An eight-frame native sequence completed. In both stationary sequences the
  capture tool reported maximum channel differences of one 8-bit step in its
  64x64 analysis thumbnails. This does not bound full-resolution edge changes.
- The intermediate history-weight capture averaged 0.9594882, with rejection
  confined to thin edge regions. Debug mode was restored before motion checks.
  A separate sequence overlapped a debug-mode change and is excluded from
  stationary quality evidence.
- Validation reported two device-initialization errors for unknown structure
  types 1000135008/1000135009. They precede the rendering work and are outside
  this change. No TSR copy rejection or incomplete-history warning appeared.
- The scoped-variant editor rebuild passed with zero warnings and zero errors.
  The final RVC depth-gate adjustment also passed a targeted rendering-project
  build with zero warnings/errors; that stereo-host path was not run live.
  The rebuilt Vulkan session again reached ready history with finite output;
  two eight-frame sequences completed, and captures from both camera positions
  were viewed. The final session had a different, brighter environment, so it
  is not used as an image-quality comparison against the earlier environment.
  The named session was stopped and its logs inspected. The same two startup
  validation errors appeared, with no TSR history-copy/coverage failure.
- Full-resolution PNG analysis found remaining localized variation: at 0.67
  scale, mean RGB deltas were 0.090-0.182 on the 0-255 scale, with
  0.014-0.318% of pixels changing by more than four channel steps; native
  resolution measured 0.045-0.088 mean delta and 0.010-0.042% of pixels above
  four steps. Isolated maximum deltas reached 122. In the final bright
  stationary sequence, 0.015-0.038% of pixels exceeded four steps, with a
  maximum of 123. These figures establish that thin-edge variation remains;
  thumbnail stability must not be reported as elimination of shimmer.
  Evidence: `reports/full-resolution-differences.json` under the run root.

## Initial correctness-validation limits

The initial baseline used auto-exposure, so those results do not establish a
numeric before/after improvement. That live fixture verified a static silhouette
and camera motion; independent object motion, layered stereo, and broad material
quality were not verified in that run. The controlled follow-up below adds
matched edge comparisons and independent object-motion checks. No per-draw RenderDoc capture was taken, and
post-render depth equality is not proof of the earlier in-frame writer.
Existing source-contract tests that require the old literal jitter expression
need updating separately after explicit clearance under the runtime-first
test policy. The user has not yet reported whether the fix resolves their view.
Future quality work should isolate residual edge rejection, sample coverage,
and sharpening using matched exposure/scene captures. No jitter-amplitude or
quality-weight tuning was included in this correctness fix.

## Thin-edge stabilization follow-up

The user requested sequential implementation of surface-aware rejection,
thin-detail history protection, unstable-edge sharpening suppression, and
broader jitter coverage. A controlled scene now uses three black diagonal
bars on a constant gray sky, exposure 1, bloom/AO/atmosphere disabled, camera
at (0, 0, 5), and TSR scale 0.67. The imported scene and probe grid are disabled
only in the owned session. Each stationary capture contains 32 full-resolution
1920x1080 PNGs, sampled every seven rendered frames with no failed/dropped
readbacks. Analysis uses the final 16 images, full-resolution ROI
`x=[710,1050), y=[320,760)`, excluding the selected bar's transform gizmo.
Edge pixels have baseline mean-image horizontal luminance gradient above two
8-bit steps. This same 7,937-pixel mask is used for every variant.

1. Surface-aware rejection selects paired current depth/motion in the bilinear
   color footprint, reconstructs previous-camera depth with each eye's actual
   projection convention, and validates the actual bilinear history contributors.
   Historical surface selection uses the same footprint. A limited local
   projected-depth slope handles grazing planes; opposing slopes at thin-strip
   extrema are flattened so a plane is not fitted through finite background.
   Invalid/extrapolated depths reject history. Independent object motion that
   disagrees with the camera correspondence by more than half an internal pixel
   currently rejects history; two-channel motion does not supply previous object Z.
   Eight shader variants compiled and the isolated Vulkan editor built with zero
   warnings/errors. Viewed captures from (0,0,5) and (0.2,0.15,5).
   Mean edge frame delta fell from 4.778 to 2.950 8-bit steps; temporal standard
   deviation fell from 5.239 to 3.255. ROI integrated darkness changed +0.16%.
   P95 delta was 9 then 10, and maxima 168 then 160: localized spikes remain.
   Evidence: `reports/edge-baseline-sequence.json`, `reports/edge-depth-sequence.json`,
   and `reports/thin-edge-metrics.json` in the existing run root.
2. Dedicated R16F history-age output/history resources are implemented.
   They preserve presentation alpha and are copied after resolve; readiness
   requires accepted color, age, and depth copies for every required layer.
   Mature compatible history may use the observed color-neighborhood extrema,
   preventing a rare foreground sample from being erased by variance clipping.
   Rejection/reactivity resets age; motion and the canonical reactive mask gate
   protection. The Vulkan build passed with zero warnings/errors; all eight
   shader variants compile. A 32-frame stationary capture measured 2.904 mean
   edge delta, 3.248 temporal standard deviation, P95 10, maximum 160. This is
   a small improvement over rejection alone. Viewed the age texture (finite,
   range 1..32) and scene captures from both positions. The age texture showed
   mature flat regions and resets localized to the silhouettes.
3. Advanced sharpening now requires mature, compatible history and suppresses
   the current-frame high-frequency term for geometric/reactive instability,
   motion, and unclipped luminance disagreement. A matched 32-frame capture
   measured mean edge delta 2.465, standard deviation 2.894, P95 8, maximum 134.
   Integrated darkness changed +0.33% from baseline. The first capture attempt
   ran before a matching Vulkan resource snapshot was submitted and failed
   explicitly; a later warmed capture completed with no failures/drops.
   Viewed the stationary and second-position images. All eight shaders compile.
4. Evaluated Advanced-only centered Halton(2,3) cycles. The candidate used
   `ceil(8 * displayPixels / internalPixels)`, bounded to 8..32: 8 native,
   18 at 0.67 scale, and 32 at half resolution. Cycles were precomputed once,
   individually centered, and read without per-frame allocation. Default TSR,
   TAA, and DLAA retained their existing pattern. The first candidate widened the
   offset to half an internal texel. A 64-frame capture regressed mean edge delta
   to 11.586 and P95 to 49; the age image showed frequent silhouette resets.
   This amplitude was rejected. Depth selection remains bounded to contributing
   color texels: expanding it to unrelated neighbors would trade rejection for
   incorrect surface ownership/edge smearing. The revised sequence retained the
   previous maximum offset of 0.175 input texels. It also regressed: mean edge
   delta 6.016, standard deviation 4.565, P95 31, maximum 146. Both candidates
   were removed; the original eight-sample pattern remains. This item is
   addressed by rejecting a measured regression, not by claiming wider sample
   coverage is solved. Coverage-aware history remains necessary before widening
   jitter. The retained implementation consists of the first three changes.

### Final retained implementation validation

The final rebuild includes surface-aware rejection, history age/detail protection,
sharpening suppression, and the original eight-sample jitter pattern. It passed
with zero warnings and zero errors. All eight mono/stereo, Advanced/Default,
OpenGL/Vulkan shader combinations compile; Vulkan variants generate Vulkan 1.3
SPIR-V. Stereo and OpenGL have compilation coverage only.

The final matched 0.67-scale capture completed 32 frames with no failed or dropped
readbacks. Its last 16 frames cover two complete jitter cycles:

| Full-resolution edge metric (8-bit steps) | Baseline | Retained implementation |
| --- | ---: | ---: |
| Mean frame-to-frame delta | 4.778 | 2.644 |
| Mean temporal standard deviation | 5.239 | 2.893 |
| P95 frame-to-frame delta | 9 | 9 |
| Maximum frame-to-frame delta | 168 | 134 |

Mean delta and temporal standard deviation decreased about 45%. Integrated ROI
darkness changed +0.35%, with all three bars still visible. Isolated edge spikes
remain: this is a measured reduction in shimmer, not its elimination. The small
mean-delta difference from the earlier sharpening capture depends on which
transition falls outside the 15 frame differences; temporal standard deviation
is effectively unchanged. Evidence: `reports/thin-edge-metrics.json` and
`reports/final-retained-sequence.json` under the run root.

Native-resolution validation also completed 32 frames without failures/drops.
History was ready at both 1286x723 internal / 1920x1080 output and native 1920x1080,
including reseeding after the scale change. Native mean edge delta was 1.832 and
temporal standard deviation 1.982; no matched native baseline was captured, so
these are not before/after improvement figures.

Three 64-frame motion sequences completed without failed or dropped readbacks:
camera motion over the sky background, independent horizontal motion of the
thinnest bar, and a camera pan with a finite tilted background plane. Viewed
frames at multiple camera/object positions showed the bars and no obvious
persistent trails. These spot checks do not establish broad motion quality;
independent object motion still rejects history when its correspondence differs
from the camera prediction, and arbitrary previous object Z is unavailable.
Sequence reports are `reports/final-camera-motion-sequence.json`,
`reports/final-object-motion-sequence.json`, and
`reports/final-finite-camera-motion-sequence.json`.

The owned session was stopped and final logs inspected at
`logs/XREngine.Editor_debug/windows_x64/xrengine_2026-09-24_13-22-13_pid67688/`
under the named session root. The two known startup validation errors for
unknown structure types 1000135008/1000135009 remain. One premature capture
failed explicitly because the matching Vulkan resource generation had not yet
rendered; the warmed retry succeeded. History-unavailable messages corresponded
to startup and render-scale reseeding. No TSR shader failure, rejected history
copy, or incomplete history-coverage failure was found.

The RenderDoc target-control connection was available, but triggered captures
did not return an RDC file. No draw-level RenderDoc inspection is claimed.
Shader reload was used for the final source-only correspondence corrections
before the first comparison; logs confirmed recompilation. C# resource changes
use a rebuilt named editor session. No test files were changed under the
runtime-first regression policy. User confirmation of visual improvement is pending.

## Published-technique comparison and remaining algorithm gaps

The user requested an internet comparison after the retained fixes. This section
records source inspection and research, not a new runtime validation or shader
change. The approximately 45% improvement remains specific to the controlled
stationary fixture; it does not identify every remaining per-pixel cause.

The strongest remaining hypothesis is intermittent subpixel coverage being
treated as a surface change. At a thin silhouette, jitter can alternate a source
sample between foreground and background. The resolve assigns one surface depth
to a footprint, whereas its accumulated color can contain contributions from both.
More accurate depth rejection alone cannot describe that mixture.

Concrete implementation gaps:

- `TsrComputeDetailProtection` requires accepted history and age greater than two;
  `TsrAdvanceHistoryAge` resets rejected history to one. Repeatedly rejected edge
  pixels cannot acquire protection. Even mature protection only extends clipping
  bounds to the current neighborhood's observed extrema, so it cannot preserve
  detail absent from that whole neighborhood this frame. Age is not coverage or
  a persistent thin-feature lock.
- `TsrSelectSurface` and `TsrHistorySurfaceDepth` classify a 2x2 source footprint.
  At reduced resolution, `SampleCurrentReconstruction` also contributes a wider
  nine-tap filtered color at 25% weight. History additionally contains older
  accumulated samples. The snippet's comment claiming the historical color came
  from the same bilinear footprint is therefore incomplete. This mismatch needs
  explicit reconstruction/coverage handling, not blind expansion of depth
  selection to unrelated neighbors.
- No multi-frame luminance oscillation detector or coverage statistics distinguish
  periodic sampling flicker from an actual scene change.
- The resolve applies sharpening before `OutColor`, and that output is copied to
  color history. Sharpening is gated now, but it remains inside the feedback loop.
  Its remaining contribution requires a controlled comparison with unsharpened
  accumulation and presentation-only sharpening.
- Independent object motion still uses the conservative camera-correspondence
  gate. That is a separate motion-quality limitation, not an explanation for
  stationary fixture flicker.

Relevant primary sources:

- [Epic: Thin Geometry Detection](https://dev.epicgames.com/documentation/unreal-engine/thin-geometry-detection-with-temporal-super-resolution)
  describes jitter-dependent foreground/background coverage and neighbor-clamping
  failure. Its solution tracks temporal coverage, detects coherent edges/lines,
  and selectively relaxes rejection while checking scene changes and translucency.
- [AMD: FSR2 algorithm](https://gpuopen.com/manuals/fidelityfx_sdk/techniques/super-resolution-temporal/)
  documents thin-feature locks with lifetime and luminance-based invalidation.
  It also keeps the accumulated history separate from the RCAS-sharpened
  presentation image.
- [Epic: TSR temporal analysis](https://dev.epicgames.com/documentation/en-us/unreal-engine/temporal-super-resolution-in-unreal-engine)
  describes multi-frame flicker analysis and the difficulty of accumulating detail
  that history rejection continually removes. Motion, parallax, and animated
  materials require safeguards against retaining stale content.

The next investigation should record rejection reasons, clipping displacement,
and age at the same worst-edge pixels over a full jitter cycle. Then compare
unsharpened history and a coherent thin-edge coverage/lock mechanism, preserving
real disocclusion rejection and reactive-material invalidation. Revisit wider
jitter only after those comparisons improve both stationary and moving edges.
Higher-resolution history and older-frame resurrection are additional options;
neither is the first justified change for this stationary coverage failure.

## Coverage implementation and shader-input diagnosis

The coverage implementation uses a positive 2x2 current-color footprint and a
separate 3x3 witness neighborhood. RGBA16F metadata carries age, accumulated
foreground coverage, previous raw compressed luminance, and signed flicker
confidence. An additional HDR accumulation target keeps presentation sharpening
and diagnostic colors out of feedback. Both mono and stereo entry points share
the coverage helper; Default keeps its existing reconstruction path.

Initial controlled comparisons exposed regressions that were not accepted:

| Variant | Mean edge delta | Temporal standard deviation |
| --- | ---: | ---: |
| Prior retained implementation | 2.644 | 2.894 |
| Bilinear current color and unsharpened history only | 3.203 | 3.401 |
| Initial coverage experiment, before stricter validation | 2.445 | 2.557 |
| Strict mixed-history validation and cold seeding | 11.455 | 11.354 |
| Local projected-depth witnesses | 11.478 | 11.098 |

Values use the original fixed 7,937-pixel edge mask and final 16 frames of
32-frame stationary captures. The two latter regressions required further
input diagnosis; relaxing rejection solely to recover the earlier score would
hide invalid continuity. Capture stride 15 preserves the same reverse ordering
of the eight jitter phases as stride 7 while reducing readback queue pressure.

Single-plane validation was inappropriate for a cube rod's front and side faces.
The revised check matches each previous depth witness to nearby projected current
witnesses, within 1.5 internal pixels and the existing bounded view-depth
tolerance. It does not interpolate across an unobserved depth interval. Both
clusters need support; their motion and reprojection must agree. Failed current
validation leaves coverage invalid instead of continually cold-seeding an
unsupported pair.

This removed the current-geometry failures, but significant luminance changes
still repeatedly cleared history on stationary geometry. Temporary shader views
established that current raw color and its depth-derived coverage agreed within
approximately 1/510 in compressed luminance, whereas reconstructed previous
coverage disagreed with the stored previous raw luminance by up to about 0.18 at
inspected pixels. Stored luminance followed the preceding frame correctly.

A direct GPU-visible jitter diagnostic then confirmed that current and previous
jitter were identical in every captured frame, despite changing across frames.
For example, encoded current/previous X pairs were 159/159, 121/121, 134/134,
and 172/172; the Y difference encoded zero throughout. This explains why previous
raw depth was sampled with current-frame fractional weights. The stationary
camera check also accepted the collapsed current-to-previous transform.
Evidence is preserved in `reports/coverage-jitter-trace.json`,
`reports/coverage-phase-trace.json`, and `reports/coverage-residual-trace.json`.

Advanced TSR now has a draw-facing temporal snapshot captured at Begin. Commit
can advance general history state without replacing the preceding jitter and
matrices used by the pending TSR resolve. Explicit reset and missing-snapshot
invalidation also invalidate this resolve snapshot. GPU verification and final
quality measurements follow.

### Final coverage validation

After the rebuilt snapshot fix, the GPU diagnostic showed distinct current and
previous X values in the expected cycle: 121/134, 134/172, 172/147, 147/83,
83/96, 96/147, 147/159, and 159/121. Previous X matched the adjacent captured
phase, as expected for stride 15. Y differences were also nonzero. The false
unexplained-luminance rejection count fell from 21,284 to two pixel-frames in the
inspected ROI over eight captured frames. This confirms a real input-lifetime
defect, rather than a need to weaken the lighting-change safeguard.

The remaining overly strict geometry check was narrowed to the color footprint.
Every positive previous 2x2 contributor must match exactly one current projected
cluster, with the same foreground/background meaning as the previous raw pair.
Outside-footprint 3x3 context probes may fail matching but then provide no support;
two matched witnesses of each class remain required. Temporary jitter/luminance
instrumentation was removed, leaving the four documented diagnostic views.

| Final comparison | Prior mean delta | New mean delta | Prior temporal std | New temporal std |
| --- | ---: | ---: | ---: | ---: |
| 0.67 scale, 1286x723 to 1920x1080 | 2.644 | 2.513 | 2.894 | 2.919 |
| Native 1920x1080 | 1.832 | 1.067 | 1.982 | 1.245 |

Both final stationary sequences completed 32 frames without failed or dropped
readbacks. These comparisons use the same original fixed edge mask and final
16 frames. Upscaled average edge delta improves about 4.9%, but variance is
effectively unchanged and the 95th percentile increases from 9 to 13. Maximum
upscaled delta remains 137 versus 134 previously. This is not elimination of
localized shimmer. Native average delta improves about 41.7% and standard
deviation about 37.2%. Upscaled integrated darkness changes about -1.1%; the
viewed images retain all three rods. Evidence: `coverage-footprint-sequence.json`,
`coverage-native-sequence.json`, and `thin-edge-metrics.json` under `reports/`.

Three final 64-frame motion sequences completed with no failed or dropped
readbacks: camera motion over sky, independent thin-rod motion, and camera motion
over a finite tilted emissive background. PNGs were viewed at multiple positions;
no obvious persistent trails were seen. The first finite-background attempt was
uninformative because the unlit scene made its non-emissive material black; it
was excluded and repeated with visible emission. Accepted sequence reports are
`coverage-camera-motion-sequence.json`, `coverage-object-motion-sequence.json`,
and `coverage-finite-lit-camera-motion-sequence.json`.

A separate 64-frame transition sequence hid the thinnest rod, then changed the
sky from 0.6 to 0.2 gray. Viewed intermediate and final frames showed the rod
absent and the new background without obvious persistent afterimages. These are
spot checks sampled every 15 frames, not a proof of zero single-frame ghosting.
The report and action times are `coverage-transitions-sequence.json` and
`coverage-transitions-events.json`.

With the flicker diagnostic enabled, a same-boundary texture capture proved
`TsrAccumulationTexture` and `TsrHistoryColor` had identical float-image hashes,
while diagnostic `TsrOutputTexture` differed. All four targets, including
metadata, reported zero nonfinite samples. Metadata age reached 32 and signed
flicker confidence spanned -1 to 1. This verifies that diagnostic/presentation
color does not enter history. Evidence: `coverage-final-targets.json` and viewed
`coverage-final-flicker` PNGs.

The isolated editor build passed with zero warnings/errors. All eight shader
variants passed: Default/Advanced, mono/stereo, OpenGL/Vulkan. Final source review
found no blocking issue. No tests were added or modified under the runtime-first
regression policy. OpenGL and stereo remain compile-only validation; no new RDC
capture is claimed. The attempted broker inventory was rejected before a run
because its context included excluded `Build` shader paths; native agents
performed the bounded implementation/review work.

The owned session was stopped after validation. Its final logs are under
`logs/XREngine.Editor_debug/windows_x64/xrengine_2026-09-24_14-29-22_pid21568/`
inside the named MCP session root. No final-session TSR shader or history-copy
failure was found. Startup resource-generation mismatch warnings and existing
exposure-update fallback messages remain outside this change. An earlier running
session saw a transient cached-snippet compile error while temporary diagnostics
were removed; a fresh session and all final variant compiles passed afterward.

GPU command timing was disabled, so no GPU speed claim is made. The coverage path
can perform up to 324 local witness comparisons and 124 historical depth fetches
per qualifying pixel; flat, reactive, moving, and invalid-metadata paths exit
earlier. Dense thin geometry needs profiling. Metadata plus the accumulation
target occupy about 47.5 MiB at 1080p mono, about 39.6 MiB more than the previous
age-only pair. Features absent from the entire witness neighborhood, independent
motion without valid previous object depth, and sky mixtures during camera
motion still use conservative rejection. User-scene confirmation remains pending.
