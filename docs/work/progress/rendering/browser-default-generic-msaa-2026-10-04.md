# Default and generic WebGPU multisampling

## Source contract

Default's browser command graph now preserves the requested CPU-direct,
GPU-indexed-indirect or compute-meshlet submission strategy. The depth/normal
prepass retains that exact strategy and the same viewport override as forward
raster. Default's cold requirements use the shared submission declaration to
require the matching cull, LOD, meshlet expansion/refit and ordering programs.
The existing shared collection still owns mixed-material ordering and explicit
CPU-only submissions; this change introduces no substitute submission algorithm.

The mono SDR Default resource profile admits AA None or exactly four MSAA
samples. The immutable resource profile already carries AA and sample count,
so changing either, resizing, or changing AO replaces the declared resource
generation. Framebuffer factories return their logical owners before physical
WebGPU preparation, allowing asynchronous preparation to retain and retry the
same pending-generation object.

For x4, forward opaque, masked, background and transparent draws use matching
RGBA16F color and Depth32Float attachments. With GTAO enabled, the prepass writes
x4 oct-encoded normals and the same raw x4 depth that forward raster later uses.
The cooked `depth-normal-msaa-resolve` fullscreen pass chooses the nearest
covered depth sample and copies that sample's encoded normal and depth into
single-sample GTAO sidecars. Companion normal alpha distinguishes covered samples
from the transparent clear; uncovered pixels retain far depth and zero normal.
The shader handles both depth directions, rejects nonfinite/out-of-range depth,
and never averages surface normals. The graph declares its depth write separately
from stencil and preserves dependencies on both multisample inputs.

After background and sorted transparency, the existing retained GPU color-only
resolve produces the single-sample HDR texture consumed by bloom, exposure and
tonemapping. Raw depth is not blitted or replaced by the AO sidecar.

The common material adapters admit matching one- or four-sample color/depth
targets for standard/color-coverage lit, lit textures, authored texture/alpha,
Unlit, Uber, sky and impostor raster, including exact depth/normal companions.
The generic draw descriptor continues to use the actual framebuffer sample
count, so authored modular pipelines can use the same adapters with their own
declared attachments and resolve commands. Existing directional and local shadow
outputs remain single-sample. The Advanced native per-sample shading path remains
independently implemented and qualified.

## Boundaries

- x2/x8 attachments, stereo/XR output, other Default AA modes and HDR presentation
  remain explicitly unsupported by this resource profile
- The initial x4 source milestone retained the generic reversed-depth rejection.
  The subsequent [reversed-depth integration](browser-generic-reversed-depth-2026-10-04.md)
  adds coordinated frozen-camera comparison/clear lowering, sky reconstruction
  and post-depth consumers. Its builds, cooks and managed checks pass; physical
  known-value output remains open. The resolve shader alone was not that support
- Arbitrary authored pipeline assets remain responsible for declaring compatible
  targets, exact programs and ordered resolve/post-process consumers
- No desktop GLSL, CPU readback, hardware task/mesh shader dispatch or alternative
  AA algorithm was introduced

## Validation

The initial source pass and independent review corrected multisample normal
binding shape, depth-output graph access, asynchronous framebuffer ownership,
prepass viewport-strategy parity and Default's submission-program requirements.
The combined Rendering and WebGPU build passed with zero warnings and errors
against source tree `75e3465d8e165aa447d896b45683eae007d60034`.
The native Browser build also passed with zero warnings and errors against that
same tree. Its successful rerun corrected a validation-command copy-local
override; the initial SDK core-library omission required no source change.
The focused production cook packaged the new resolve recipe successfully:
descriptor `083df46c9a6694cd2a3b8efbf38404810a6f02407fbe247d51b930be23a35958`,
WGSL `9bd28fa5ba905d7ebee97945f125d5b8363b46bb0ba2651b9282b4a0cae9c418`.

A narrow witness linked the frozen production assemblies and invoked Default's
real resource declarations, command graph metadata and cold requirements. It
passed 214 checks across twelve combinations of x1/x4, GTAO on/off, and CPU-direct,
indexed-indirect or compute-meshlet strategy. Checks covered matching forward
attachments, single-sample post-process/AO sidecars, optional resolve presence,
explicit depth writes and raw sampled inputs, resolve ordering after transparency,
preserved prepass strategy, selected compute/ordering program closures, resize
profile replacement identity, and rejection of unsupported x1/x2/x8 MSAA
selections. The witness compiled with zero warnings and errors. Viewport override
precedence and asynchronous generation ownership were independently source-reviewed;
the witness did not execute GPU commands or certify physical generation retirement.

Disposable evidence is under the active validation run's
`scratch/default-msaa-validation/` and `logs/default-msaa-{cook-0658,probe-0705}.log`.
Browser acceptance remains pending: edge coverage and sample preservation, masked
materials, normal and depth resolve, transparent ordering, GTAO on/off,
CPU-direct/indexed-indirect/compute-meshlet variants, resize/profile replacement,
and repeated generation retirement still need rendered evidence.

## Default factory browser cohort

The existing ordinary-Unlit diagnostic now exposes explicit `cpu-x1`, `cpu-x4`
and `cpu-x4-ao` startup profiles. These select the real Default camera and
resource profile before initialization. The original nine material centers are
unchanged; x4 adds a gutter overlap with known front/rear colors, depths and
oct-encoded normals. Quarter-step resolved coverage identifies partially
covered pixels without assuming hardware sample positions. The AO profile
requires nearest-covered depth and the normal from that same sample, plus the
actual resolve/GTAO operation order and native attachment sample counts.
The disabled-AO profile requires those unused sidecars to be absent.

The harness retains source/camera/committed-generation identity, successful
submitted packets, bounded canonical readbacks and failure evidence. It pauses
only its own diagnostic frame pump during grouped samples and restores it with
session/renderer ownership checks. Both x4 profiles repeat non-square resize,
restoration and startup. The Linux cook adds the existing canonical resolve
recipe and verifies the exact ten selected pipeline keys instead of only a
catalog count. Source and JavaScript checks pass; C# compilation and the first
rendered x4 result remain pending. This fixture does not qualify custom graphs,
GPU-indirect/meshlet modes, reversed depth or transparent coverage.

The fixture now compiles and reaches real x4 output on `930d1be4` in
[run 37214531333](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37214531333).
Its first saved image visibly contains the nine tiles and sloping overlap, but
both Unlit checks stop at screenshot extent: a 512×512 target is captured as
512×513 after the added profile control changes page layout. This is not a
completed x4 qualification; subsequent AO, resize and restart checks did not
run. The diagnostic now uses integral layout metrics and retains CSS bounds,
bitmap extent and pixel ratio before each capture. The original exact extent
and pixel assertions remain unchanged for the rerun.

The `5e42251` rerun in
[run 37219100537](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37219100537)
passes all six no-AO x4 captures: two fresh sessions, each at 512×512,
640×384 and restored 512×512. All 54 material-center comparisons pass with
maximum HDR component error 0.00022978 and zero display-byte error. The overlap
has 319 fractional-coverage pixels at 512×512 and 402 at 640×384, with zero
coverage/color mismatches. Native modules/pipelines remain stable through each
resize (six modules, 24 cached pipeline entries); sampled live resources remain
143, with no retiring resources or retained readback tickets. The non-square
restart image was also inspected. The disabled-AO stage correctly executes no
depth/normal resolve or GTAO operations.

The next AO profile rejects `DepthNormalMsaaResolve.wgsl` during real browser
parsing: adjacent less-than and greater-than comparison arguments to `select`
are interpreted as a template list. Parenthesizing both comparisons removes
that ambiguity without changing their operands or selection. The same syntax
was corrected in Advanced visibility resolve and authored source ranking.
Their rendered acceptance, and Default x4 depth/normal/GTAO acceptance, remain
pending. This partial no-AO result does not close the broad MSAA acceptance row.

The ordinary authored indexed producer is now connected to this same diagnostic
through explicit `gpu-indirect-x1` and `gpu-indirect-x4` startup selections. Its
real `GpuIndirectZeroReadback` setting is selected before Default construction
and retained through camera setup; original material and index sources remain
the producer inputs. The verified catalog binds the production indexed route,
with exact scoped primitive-cull, LOD-select and source-order dependencies added
to the diagnostic cook and package. State reports the renderer's actual indexed
strategy and rejection reason. No meshlet payload or substitute argument buffer
is manufactured. The separate legacy browser packet strategy is unchanged.
These connections have source and JavaScript checks; managed compilation and
browser indirect output remain pending. Existing automated CPU cases continue
to select their original profiles, and no additional acceptance row is closed.

The Linux job of
[run 37222890374](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37222890374)
on `e2c47497` now passes the complete diagnostic suite, including both CPU x4
profiles. Twelve captures across two starts per profile and initial/non-square/
restored extents pass all 108 known-value center comparisons. Maximum HDR
component error is 0.00022978 and display-byte error is zero. The overlap has
319 or 402 fractional pixels according to extent, with zero coverage mismatches.
The AO profile verifies twelve prepass draws, one coherent depth/normal resolve,
and all three ordered GTAO operations. Its sampled AO spans approximately
0.7803–0.9429 at 512×512 and 0.7891–0.9424 at 640×384. Its fifteen native modules
and 59 cached pipeline entries retain identity through resize; sampled live
resources remain 274 with zero retiring resources and drained readback tickets.
The no-AO profile retains six modules, 24 pipeline entries and 143 live resources,
with no unused sidecar/AO operations. The non-square AO restart image was viewed.

This closes only the bounded Default CPU x4 opaque/masked acceptance leaf.
GPU-indirect x4, custom framebuffer graphs, blended transparent coverage and
unavailable-profile rejection remain explicit acceptance requirements.

The next separately labelled `engine-unlit-indirect` check reuses the existing
sample/lifetime helpers for GPU x1/x4, with independent managed selection,
LOD/cull dispatch and indexed-indirect issuance observations. It permits direct
presentation but rejects any direct scene draw mixed into the indirect result.
Actual GPU argument counts remain unknown; the nine material centers and x4
overlap supply output evidence. A monotonic READ-map counter starts before page
navigation and records zero increases in ordinary frame/resize intervals, with
exactly nine or ten maps attributed to each paused diagnostic-copy interval.
The current opaque/masked source does not select transparent rank/mask work,
so no source/primitive-order result is inferred. Independent source review,
JavaScript syntax and diff checks pass; live execution of this added check is
still pending.

## GPU-indirect color and lifetime acceptance

The Linux qualification job `111572327221` of
[run 37248918555](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37248918555)
passes on exact commit `6fea26f99f5066950642215fbfd998d0cd1db969`.
Artifact `11320856846` has archive SHA-256
`995986b21afec9d87bb1de7e184d2b86bef617ce7703740dcc7d0cfde6d7ee4f`.
Chromium 153.0.8010.12 uses the explicitly selected SwiftShader software adapter.
The genuine Default source selects `GpuIndirectZeroReadback` throughout x1 and
x4, with matching authored camera, committed sample count and resource extents.

Both profiles complete two fresh starts, each at 512 x 512, then 640 x 384,
then restored 512 x 512. The twelve captures pass 108 HDR/display center
comparisons: maximum HDR component error is 0.0002297794 and display-byte error
is zero. The x4 diagonal contains 319 or 402 fractional pixels, including all
quarter-coverage steps, with zero mismatches. The initial x4 image and the
non-square second-start image were inspected. Captured command records include
real GPU LOD selection and culling, ten x1 or twelve x4 indexed-indirect color
draw calls, and x4 color resolve before presentation. Geometric rejection is
conservatively disabled for these undeclared vertex bounds; this does not
establish frustum-culling efficacy, GPU-selected count values or primitive
ordering for transparent sources.

Ordinary frame and resize intervals add no READ maps. Each paused diagnostic
interval accounts for exactly nine x1 or ten x4 pixel-copy maps. Native module
and pipeline identities remain stable, with eight module cache entries and
thirty pipeline cache entries. The exact managed slot roots and retained
command dependency graph account for partial spare-slot warming: non-slot
inventories remain 105 resources for x1 and 125 for x4 across every resize and
restart. No stale output attachment, unowned pipeline or unexplained resource
is accepted. All four teardown records report zero live/retiring resources,
readback tickets and estimated logical GPU memory. This is bounded descriptor
and ownership evidence, not a driver-memory or long-duration allocation budget.

These GPU profiles have GTAO disabled. They qualify the ordinary x1/x4 material,
color resolve, selected-mode, pixel-copy accounting and retained-resource subset.
The GPU x4 depth/normal sidecars and enabled GTAO path still require execution;
the complete Default GPU x4 acceptance leaf remains open. Custom x4 graphs,
blended transparent coverage and unavailable-profile rejection also remain open.

## Additional authored AO and blended profiles

The same diagnostic now includes `gpu-indirect-x4-ao`. Its accepted-frame
contract requires twelve indexed depth/normal draws and twelve indexed color
draws, each with its own GPU selection/cull work. The exact cold owner map
therefore admits twenty-four selection/work entries in a completed slot, within the unchanged
production capacities. Closest-covered depth/normal resolution, three ordered
GTAO operations and the existing independent numeric witnesses are mandatory.
Twenty-two READ maps belong only to paused diagnostic copies; ordinary frames
retain the zero-readback requirement.

The separate CPU and GPU x4 blended profiles preserve the original nine tile
cases. Only the sloped gutter pair changes: the rear has alpha 0.75 and the
front alpha 0.5, over the opaque clear/background color. The fixture inserts
front before rear, so the expected rear-over-background then front-over-rear
result cannot pass by retaining insertion order. RGB uses SrcAlpha and
OneMinusSrcAlpha; alpha uses One and OneMinusSrcAlpha. The independent HDR
oracle includes fractional geometry coverage in quarter steps. GPU evidence
must include source ranking, argument masking, retained raster input copies
and indexed-indirect replay; direct scene substitution and ordinary READ maps
remain failures.

Connecting this authored state exposed two production admission gaps: the
canonical unlit surface reader and WebGPU coverage check previously required
SrcAlpha for the alpha channel as well as RGB. Both now also admit One for the
alpha source of unlit AlphaBlend. Existing SrcAlpha, premultiplied and additive
behavior is preserved, and the native pipeline still lowers the actual authored
factors. No desktop GLSL, default material state or serialized format changes.

Independent source reviews found no remaining static blocker after those gate
fixes. JavaScript syntax, diff checks and replay of the previously accepted
opaque/AO CPU and ordinary indexed captures pass. A synthetic blended witness
checks the oracle only. New profile pixels, C# compilation and browser behavior
remain pending on a fresh exact-commit run. These source and replay checks do
not close either wider MSAA acceptance leaf.

The first `03c19bf4` run (`37252729733`) compiled the managed changes and
published the browser host, but the added profiles stopped at the JavaScript
host's older profile allowlist before managed creation. The CPU loop completed
its ordinary x4 captures before reaching the rejected blended profile; the GPU
loop completed ordinary x1/x4 before reaching its rejected blended profile.
Neither new blended profile nor the subsequent GPU AO profile executed. The
host now admits the same eight explicit names as the managed parser and page
selector, while invalid names and non-Unlit use remain rejected before changing
the running host. Ten bounded checks execute the actual host start method with
mocked managed creation and verify that dispatch boundary; they do not qualify
rendering. The same run's normal custom shader cook rejected the new recipes'
`xrengine.sample.*` schema prefix. Both now use the existing versioned
`xrengine.engine.*` ABI namespace while retaining their `custom` pipeline scope.
JSON/schema-shape and syntax checks pass; genuine cooking and new pixels still
require the repaired exact-commit run.

## CPU blended execution and remaining GPU admission

Exact commit `08822920aca51af3a26328ede27471a69a669370` advances the new
profiles in [run 37256129926](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37256129926).
The Linux artifact `11322779048` has SHA-256
`1734f371a5f90fc4ac2f69e950ab06551402c734bd75fd374823e6c01709162d`.
The CPU x4 blended profile completes two fresh lifecycles, each at 512 x 512,
640 x 384 and restored 512 x 512. All 54 original material-center comparisons
pass, with maximum HDR component error 0.0002297794 and zero display-byte
error. The independent blended gutter witness contains 319 or 402 fractional
pixels with zero coverage/composition mismatches. Its full-coverage far color
is (0.21875, 0.4375, 0.65625, 1) and near-over-far color is
(0.734375, 0.34375, 0.390625, 1), as required by the authored straight-alpha
factors. Recorded color commands place both transparent draws after opaque
geometry and resolve before presentation. Initial and non-square second-start
screenshots were inspected. Both teardown records report zero live/retiring
resources, readback tickets and estimated logical GPU memory.

The same run passes CPU x4 opaque and GTAO profiles and ordinary GPU x1/x4
profiles. GPU blended startup reaches the authored ranking module and fails
WGSL validation because its constant NaN sentinel is not a representable f32
constant. The later GPU AO profile is therefore not reached. This is a shader
admission defect, not a failed pixel comparison or permission failure. The
ranking repair retains explicit invalid-distance ordering without manufacturing
a NaN constant; live GPU blended and AO results remain pending.

The Windows Editor now cooks all three custom modular recipes. The subsequent
game assembly build identifies the missing `XREngine` namespace import for
`EAntiAliasingMode` in `ModularMsaaRenderPipeline`; that import is restored.
Custom x4 publication and pixels remain unqualified. These bounded results do
not close the wider custom, blended and unsupported-profile acceptance item.

## GPU depth, AO and blended execution

Exact commit `c9139dd69679c2872d956323971df46fb5292926` passes the complete
[Linux browser job in run 37261633781](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37261633781/job/111609908659).
Artifact `11325318497` has SHA-256
`b5bbdb1496eafd5809a706e6fae1cf545f5c64230bc1abbf22d0974f70758b03`.
The software adapter executes all four GPU profiles: x1, x4, x4 blended and
x4 with GTAO. Each completes two fresh starts and the same three extents.
The x4 ordinary and AO profiles together supply twelve captures and 108
material-center comparisons. Maximum HDR component error is 0.0002297794;
display-byte error and silhouette/sidecar coverage mismatches are zero.

The AO profile records twenty-four GPU selection/cull pairs, twelve indexed
depth/normal calls and twelve indexed color calls. Closest-covered depth/normal
resolve precedes all three GTAO stages; HDR color resolve precedes presentation.
The witness observes the authored near/far depth and normal values at every
quarter-coverage step. Its final AO ranges from 0.7802734375 to 0.94287109375
at 512 x 512 and from 0.7890625 to 0.9423828125 at 640 x 384. Ordinary frames
and resize add zero READ maps; each paused AO capture accounts for exactly
twenty-two diagnostic pixel maps. Native module/pipeline identities remain
stable at seventeen modules and sixty-five pipelines, and the exact non-slot
inventory remains 232 entries across all six AO captures. Both AO teardowns,
and all eight GPU-profile teardowns, report zero live/retiring resources,
readback tickets and estimated logical GPU memory.

The GPU blended profile now progresses through the repaired draw cache. Its
six captures pass the straight-alpha and fractional-coverage witness with zero
mismatches. Actual commands include source ranking, two argument-mask
dispatches, six raster-input copies and fourteen issued indexed color calls,
followed by color resolve and presentation. Effective GPU-visible counts remain
unread. Each paused capture uses ten diagnostic maps and ordinary intervals use
none. Initial blended and initial/resized AO screenshots were inspected.
Native program identities stay stable; the blended retained owner inventory
warms from 275 to 295 to 313 entries over the three extents, unlike the stable
ordinary/AO non-slot inventory. It returns to zero on teardown, but repeated
resize budget/stability is not established by this bounded blended result.

This closes Default GPU x4 ordinary/depth/AO acceptance. Custom x4 graphs,
their blended coverage and precise unavailable-profile rejection remain open,
as do physical-device, long-duration memory and wider renderer acceptance.
