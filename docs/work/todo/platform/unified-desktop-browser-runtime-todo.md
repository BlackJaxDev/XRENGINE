# Unified Desktop And Browser Runtime TODO

[<- Work docs index](../../README.md) · Design: [Unified desktop and browser runtime](../../design/platform/unified-desktop-browser-runtime-design.md) · Prerequisite: [Native subsystem integration debugging and validation](native-subsystem-project-split-todo.md) · Backend detail: [Browser renderer module design](../../design/rendering/browser-wasm-renderer-design.md) · Device and delivery validation: [Mobile WebGPU runtime TODO](../rendering/mobile-webgpu-runtime-todo.md)

Status: **121 of 163 named items are checked; 42 remain open**. Completed source includes ordered native GPU-palette copies, shared offscreen canvas rendering/input, ordinary authored GPU-indirect submission, reviewed native authored decals, real authored instance publication, retained buffer/texture transfers, resident GPU transparent ordering, asynchronous scene capture with completed-result HLOD/impostor publication, and desktop-owned file backends for Core network transfers and shader hot-reload reads. The broader runtime file inventory remains open. Shared source implementation remains separate from browser acceptance. Physical Intel hardware renders the preceding `71684f00` static Advanced material sample through two startups and both resize checks. The `2b97bda4` software-Chromium meshlet run now passes one bounded static Default cohort; broader meshlet mode, material, deformation and unsupported-profile acceptance remains open. The `feca3bcc` visible-shadow fixture establishes bounded directional receiver darkening but exposed point sizing/caster defects; the `d660296f` corrections await their physical retest. The `6cbf90ba` software run now renders the static Advanced sample in two fresh sessions and both resizes through the proven modifier-free native family; full shadow/decal families and broader acceptance remain open. Coherent source groups use narrow compile/cook checks and complete browser/regression qualification at end-to-end milestones.

Created: 2026-09-29. Updated: 2026-10-06.

Owner: Runtime architecture / rendering / platform.

## Goal

Run the same engine, worlds, and C# game code in the browser (WebGPU, .NET 10 WebAssembly) as on desktop, following the engine model used by Unity and Godot web builds:

- **Same assets:** one serialized world, prefab, and component format.
- **Same code:** one set of engine and game assemblies.
- **Per-platform differences are limited to** platform leaves (renderer backend, audio, input, windowing, physics native build, transports) and cooked GPU/format variants (shaders, textures, audio codecs).
- **Modular pipelines:** support the same authored `RenderPipeline` assets, including Default, Advanced and custom command graphs. Admission follows concrete operation/resource/program capabilities, not a pipeline-type whitelist or silent replacement. The owner's 2026-10-02 clarification and implementation order are recorded in the [modular pipeline plan](../../design/platform/modular-browser-render-pipelines-2026-10-02.md).

The editor stays a desktop application and gains an honest browser publish target on this path. The separate browser runtime on the `codex/webgpu-readiness-audit` branch is stabilized as a reference harness and retired once this path reaches parity.

## Current State (2026-10-06)

The local source now contains a bounded mono Advanced
family: canonical scene residency, CPU-direct and GPU indirect/compute-meshlet
visibility, classification/reconstruction, native PBR shading, exact texture
cohorts, depth/AO stages and selected output/post/exposure paths, plus native
skin/morph production and faithful per-sample x4 MSAA. Material vertex displacement
has an implemented exact authored companion profile. The generic Default/custom compute-meshlet family has authored shaders, distinct-submesh deformation, exact multiple-instance publication and completion-retained resources. Resident and mixed CPU-direct/GPU transparent ordering have compiled implementations. Dynamic multi-LOD registration remains open under the separately held desktop compatibility correction.
The split below counts source implementation and
live acceptance independently, with each requirement owned by one named row.

Fresh post-reconstruction evidence includes Rendering/WebGPU/Editor and native
Browser builds, the original 75 shader cooks and new deformation/MSAA companion
cooks, 48 saved-camera/identity assertions, 25 admission/reservation assertions,
24 production MSAA profile checks and source-boundary reviews. Genuine Editor Prepare/Export and
separate-process cooked hydration/BeginPlay also pass for the single-camera static
Advanced sample, preserving its pipeline/settings, four texture identities and
pawn-camera alias. The [fresh integration record](../../progress/rendering/browser-advanced-static-integration-2026-10-03.md)
records that evidence. The real Windows Editor publishes all three saved samples,
and the existing Linux browser, RollingBall and RenderingParity lanes pass at
`48fc5778`; its Advanced lane fails first-frame acceptance. The subsequent `6c91220f` fix changes the exact cooked mesh's canonical admission
from `InvalidGeometrySource` to accepted without changing its buffers or indices.
A physical Intel run then renders the saved panel in two fresh startups and both
resize checks, with native visibility/shading and no pending draw or resource
failure. The later `6cbf90ba` software CI path now passes the static modifier-free native application. Exact
PBR, selected shadow/decal families, native deformation/MSAA and broader acceptance remain open.
**121 of 163 named items are checked; 42 remain open.** The [quality/accessibility record](../../progress/platform/browser-quality-accessibility-2026-10-03.md) documents the preceding source and remaining live acceptance. The [meshlet ownership and acceptance record](../../progress/rendering/browser-authored-meshlet-indexed-2026-10-03.md) records 337 managed ownership checks and the bounded static Default browser cohort; the broader UR06.11b2b matrix remains open. The [GPU palette-copy contract](../../../architecture/rendering/webgpu-indirect-submission.md#native-aggregate-gpu-palettes) preserves exact resident inputs without reading CPU seed mirrors. The [point-shadow correction](../../progress/rendering/browser-point-shadow-restoration-2026-10-03.md) now passes 139 restoration, camera and managed-lifetime checks, including six teardown cycles with no surviving registered objects. The [UI ownership record](../../progress/rendering/browser-ui-lifetime-2026-10-03.md) separately records 91 UI checks and 104 managed-reuse checks. Physical point-shadow and whole-browser memory acceptance remain open.

The published `e8e381a2` checkpoint retains its historical **65/114** count and
the preceding Default/RenderingParity evidence: metadata-bearing RollingBall
headless lifecycle, genuine Editor cooking, and the physical Edge/Intel Arc
textured skeletal/morph run. Broader lighting semantics, numeric CPU/GPU
deformation parity and the newly implemented Advanced family still need their
own qualification.

The preceding published commit `0078867c7327e2a50605a655207854319045e2bb` passes
all three existing jobs in
[run 37024792567](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37024792567).
The genuine Windows Editor bundle renders the real RollingBall course, ball,
obstacles and authored HUD in Chromium/SwiftShader. Inspected captures verify
pause/resume and both resizes; the earlier CSS-outline false positive is absent.
All six offline queued-audio cases have zero maximum sample error. These bounded
results do not establish full gameplay, audible-device, performance or desktop
parity. The [current checkpoint](../../progress/platform/unified-browser-checkpoint-2026-10-01.md)
records exact captures, corrections, source groups and their separate limits.

The following area inventory retains the published checkpoint evidence and its
remaining boundaries; it is not a refreshed qualification of reconstructed source.

| Area | Published evidence and remaining boundary |
| --- | --- |
| Shared host/platform | Real `Engine`, `RuntimeWorld`, game assemblies, caller-thread jobs/frame callbacks and the browser platform leaf compose successfully. Surface generations, timing resets and scoped pipeline selection are implemented; desktop pacing and the full blocking-site closure remain open. |
| Game and publishing | The unchanged authored RollingBall and RenderingParity projects pass genuine Windows Editor CLI publication and render through the shipping browser player. Physical browser input, pause/reset, resize and two-start checks pass; broader gameplay/physics, desktop comparison and device acceptance remain open. |
| Rendering | Bounded shared lit/HDR/tonemap, directional shadow and debug-overlay profiles have known-value Chromium evidence. Physical RenderingParity now shows textured/shaded surfaces and combined skeletal/morph animation. Sky/local shadows, coverage, engine UI/text and bounded post effects are implemented and compiled/cooked; full known-value, compute parity and production-profile qualification remains open. |
| Assets/input/audio | Hash-verified shared cooked assets, font atlases, mapped transient input, touch controls, spatial/queued audio and composed activation are implemented. Default RollingBall cache v5 bytes remain identical; explicit required audio uses the backward-readable v6 extension. Queued-stream automatic looping and offline sample correctness pass; complete I/O closure, broader codec/IME/accessibility and device checks remain open. |
| Physics/networking | Native browser Jolt lifecycle/contact/query checks pass; a bounded matched native/WASM micro-scene has tolerance evidence. Canonical-game physics traces, physical-device budgets and real-server throttling/disconnect qualification remain open. |
| Toolchain/cleanup | The approved SDK/workload and Slang pins are retained, path casing is corrected, and clean Linux/Windows publication works. The separate browser reference runtime stays frozen until genuine parity permits retirement. |

Software WebGPU proves API/shader behavior for the recorded cases, not hardware
performance. The earlier host's local Chromium attempt was blocked by its
Unix-socket policy; that is historical environment evidence, not a current
browser-access determination. Historical build and
unit-test results below apply only to their recorded snapshots and do not imply
that the remaining verification rows have passed.

The Linux job of [exact-commit run 37222890374](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37222890374) on `e2c47497` passes the full browser diagnostic suite, including both Default CPU x4 profiles. Their twelve captures retain exact known-value colors, coherent depth/normal resolve, GTAO stages and resize identities. The preceding `5e42251` run passes all three existing Windows Editor publications, all eight ownership tests, RollingBall and RenderingParity; the reviewed receipt admission fix allows both RollingBall resize sessions to finish. Its first shared-UI cook exposed incorrect canvas CLR names, now corrected together with unattached text/button loading and cooked input-text replay. The `e2c47497` publisher then gets past world loading and stops at the fixture’s inherited Roboto Medium default. The explicit Roboto Regular project setting and staged Config now pass genuine UI cooking/publication on `f628183b`. Actual browser startup then exposes the unsupported .NET Brotli decoder used by font payload v2. The owner-approved v3 codec now writes bounded raw/LZ4 mip blocks, retains desktop v2 reading and gives browsers a named v2 recook diagnostic; fresh cooking and browser loading remain pending, so UI rendering/input acceptance stays open. The native sampling-loop restructuring showed no software startup benefit and was restored to its prior source. The bounded compile-only comparison runs both actual published shader families in separate fresh processes with matched backends and unchanged limits. Both native creations time out; module validation and cleanup pass, and the comparison does not turn the failed application into accepted rendering. The first Default GPU-indirect run on `f628183b` reaches real engine startup and exposes discarded pre-publication command snapshots and a missing diagnostic renderer scope. The reviewed fixes retain accepted row images and install the owner scope without changing strategy or guards; actual indirect submission/raster acceptance remains pending. The checklist split below records only the completed CPU x4 cohort.

## Rules

- **No second runtime.** New browser functionality goes through engine components, the engine renderer contract, and engine pipelines. Do not add features to the separate browser scene, component, animation, or pipeline types. Do not reimplement the engine facade or host services inside `XREngine.Browser`.
- **Compiled before complete.** An item is complete only when its code builds under the [build gate](#build-gate). At the owner's 2026-10-02 direction, implement coherent groups with narrow compile/cook checks and run the full gate at end-to-end milestones; do not serialize every small feature behind the broad suite. Source that has not been compiled is not a completed deliverable.
- **Item kinds.** Every item carries one tag:
  - `impl`: source work that an implementation pass can finish and compile.
  - `verify`: needs a live run, capture, measurement, or physical device.
  - `owner`: needs an owner decision or approval before work proceeds.

  An implementation-only pass completes `impl` items, leaves `verify` items unchecked with a note of what is ready to validate, and stops at `owner` items. It never resolves an [owner decision](#owner-decisions) by guessing.
- **Counting.** Count only named checkbox rows with an `impl`, `verify`, or `owner` tag. Non-counted parent summaries preserve earlier IDs and map their requirements to concrete leaves or existing owners; unnumbered explanatory sublists are not extra items. A completed implementation leaf does not close its live acceptance or the full source-group build gate.
- **Named failures.** A required service, pass, or physics feature that a platform lacks fails with a named diagnostic. No silent CPU fallback for a requested GPU path, no WebGPU-to-WebGL2 switch, no dropped components.
- **Hot paths.** One managed-to-JavaScript crossing per frame for rendering; no per-draw interop; no per-frame heap allocations in recording, visibility, simulation, or submission.
- **No blocking on the browser path.** Asynchronous I/O and readback only; the browser runs frame-stepped on one thread.
- **Desktop preservation.** Validate every shared-contract change on OpenGL and Vulkan per the [pipeline invariants](../../../architecture/rendering/default-render-pipeline-notes.md) and [mesh submission contracts](../../../architecture/rendering/mesh-submission-strategies.md).
- **Validation order.** Validate each feature through its live path (desktop editor, browser page) before adding regression tests, following repository policy. Record browser evidence under `Build/_AgentValidation/<run>/` and durable findings in `docs/work/investigations/<subsystem>/`.
- **Approvals.** Toolchain pins, new dependencies (repository-built `joltc`, headless-browser test tooling), and supply-path changes need owner approval and license review.
- **No todo IDs in code.** Keep task IDs out of code, comments, type names, and diagnostics.

## Qualified implementation snapshot

The exact published commit `047bb7f1126f9fa6272325ace84446b3c9f7e1b9` passed
[real Chromium CI](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36947665282)
on 2026-10-02. Actual Chromium on Google SwiftShader passed the fourteen-case
`StandardLitColor`/HDR/tonemap diagnostic and five resizes without console errors.
The supported profile is shared `DefaultRenderPipeline`, direct GGX/Fresnel
lighting with at most four directional, eight point and eight spot lights,
RGBA16F scene HDR plus depth32, and Mobius/gamma output to a non-sRGB canvas.
Known HDR captures retained values above one and authored opacity. The 30 live
GPU resources stayed stable through each case and resize; retiring resources
drained to zero. Depth and texture/sRGB replacement, engine world play/stop,
asset lifetime, native Jolt and offline audio also passed. See the [lit-profile
acceptance record](../../progress/rendering/unified-webgpu-shader-cooking.md#lit-profile-live-acceptance)
for exact samples and artifacts. This is software-WebGPU correctness evidence,
not hardware performance or complete production/gameplay acceptance.

The same current checkpoint records five-cycle canonical RollingBall lifecycle
in both headless and canvas-composed published WASM: 35 registered objects at
each cycle boundary and 120 warm frames plus 600 measured frames per cycle. A
separate injected BeginPlay failure rolled back cleanly and an immediate retry
passed. Canvas composition initializes no GPU. A separate
published-WASM versus native Linux Jolt probe compared a bounded single-ball,
tilting-course micro-scene at 18 sampled checkpoints and found zero sampled
position, velocity, contact/onset, or ray deltas. Quaternion normalization noise
was at most `4.3e-8` radians. This is not a full canonical-game physics trace,
rendered browser play, or a universal determinism guarantee; UR07.03 and U3
acceptance remain open.

At the earlier lit-only checkpoint, the checked implementation/approved-decision rows were `UR01.01`, `UR01.04`, `UR01.06`, `UR02.02`, `UR03.01`, `UR05.01`, `UR06.03`, `UR06.04`, `UR07.01`, `UR07.02`, `UR07.06`, `UR07.07`, `UR08.04`, `UR10.01`, `UR10.04`, `UR10.06`, `UR10.07`, `UR11.03`, `UR15.01`, `UR15.02`; the newer partial runtime and renderer evidence does not itself check additional rows.
Static registrations are supplied by the unified C# path; frozen reference
Python scripts remain for the separately gated retirement work. Canonical
RollingBall world/gameplay/Jolt proof uses published WASM under Node, not a
rendered game: five-cycle callbacks, teardown and late BeginPlay failure/retry
pass, but the diagnostic package does not exercise Editor BrowserWebGPU
publishing and initializes no GPU.

The remaining rows at that historical checkpoint required further implementation or their stated
acceptance evidence. Full production shader coverage, authored textured/deformed
world parity, memory/performance bounds, physical-device checks, recovery and
broader desktop/browser/device verification remain open.
The detailed [checkpoint record](../../progress/platform/unified-browser-checkpoint-2026-10-01.md)
preserves evidence and limitations.

### Compiled implementation closure (2026-10-02)

The coherent compute/texture/coverage/networking source group passes the desktop
WebGPU, Editor, Server and VRClient Release builds, all eighteen portable browser
compile rows, and a fresh browser interpreter/Jolt publication. ShaderCooker and
BrowserContentCooker also build cleanly; twenty-four raster recipes and the
canonical compute kernel cook successfully. Compiler results contain zero
warnings/errors. This closes the authored unsupported-physics cook audit
(UR07.04), transport/gateway and bounded browser networking composition
(UR12.01–UR12.02), and stale-completion rejection (UR14.02). That source snapshot reached
**45 checked and 65 open**.

Networking consumes a fresh trusted in-memory handoff and independently verified
loaded package identity; suspension discards admission and requires a new manager
and baseline. Optional voice remains explicitly unavailable. The opt-in shared
package has a conservative self-contained base-world profile. Its unmodified
server-generated world now passes the compiled Editor publication chain and the
browser package validator with byte-identical native world bytes. Real-server
play, new GPU deformation and
coverage pixels, layered textures, UI/IME behavior, custom-font cooking, full
recovery and production/device budgets remain open. Focused text accessibility
and a finite blocked-API list do not by themselves close broader UI or game-API
coverage rows.

### Compiled font and touch implementation (2026-10-02)

Authored FreeType bitmap-font cooking and shared virtual stick/button controls
now satisfy UR09.03 and UR09.05. Editor, native-Jolt Browser, Server, VRClient,
desktop WebGPU, all eighteen portable compile rows and fresh browser publication
pass with zero warnings/errors for the coherent source group. Glyph coverage is
cooked offline; browser runtime binds hash-verified owned atlases before world
activation. Virtual controls are ordinary engine UI feeding existing player
mappings with bounded multi-touch, physical-input preservation and ownership
cancellation. That source snapshot reached **47 checked and 63 open**. UI/IME pixels, device
input acceptance, broader accessibility and the complete quality profile remain
separate open work. The new opaque textured material projection exposed a normal
YAML alias-reconstruction issue during its targeted real cook. The corrected
saved-world path now passes production cooking, post-hydration capability audit
and packaging with strict runtime aliases and unchanged authored-file bytes.

### Compiled browser platform lifecycle (2026-10-02)

The platform-leaf extraction and surface/lifecycle timing work now satisfy
UR02.04 and UR02.05. The integrated Editor, Server, VRClient and desktop WebGPU
builds, all nineteen portable rows and fresh native-Jolt browser publication pass
with zero warnings/errors. The relocated host script is byte-identical at its
unchanged published URL. The same source group repairs canvas startup's missing
AA/exposure defaults and preserves explicit authored rejection diagnostics.
That source snapshot reached **49 checked and 61 open**. Physical lifecycle, desktop pacing,
and actual rendered RollingBall acceptance remain open; these implementation
closures do not imply those runtime checks passed.

### Compiled audio activation and hosting contract (2026-10-02)

The reviewed audio activation/lifecycle implementation now satisfies UR08.02:
page and surface blockers compose, stale output completions are rejected, and
only an explicitly audio-required world gates simulation while presentation
continues. Canonical optional-audio RollingBall cache bytes remain unchanged;
required audio uses a backward-readable extension. The shared managed ownership
probe passes eleven cases. UR15.03 is covered by the new
[static hosting guide](../../../developer-guides/runtime/browser-static-hosting.md),
without applying a deployment or claiming production-header/device validation.
Editor, Server, VRClient, desktop WebGPU, all nineteen portable compile rows and
fresh native-Jolt Browser publication pass with zero warnings/errors for this
coherent source group. The count is **51 checked and 59 open**. Queued-stream
automatic looping, real new-audio output/gesture qualification and broader audio
coverage remain open. The same group repairs resize readiness and rejects the
CSS-border-only image false positive; its fresh runtime acceptance is pending.

### Reconciled shader-cook and output implementation (2026-10-02)

A row-by-row review of the same compiled `4a801dba` snapshot closes UR05.02
and UR04.05, bringing the current count to **53/110**. The pinned Slang cooker
produces and validates schema-3 WGSL engine artifacts; full shader-profile
coverage and semantic pixel checks remain separate rows. Canvas presentation
uses `RenderFrameOutputDescription`, reacquires the current output each frame,
and rejects obsolete generations/properties in both recording and submission.
The current-output readiness correction is built, but the fresh game run stops
before resize, so this implementation closure does not claim live resize
acceptance. Browser composition still needs every unavailable host-service slot
explicitly accounted for and remains open.

### Compiled service and queued-audio completion (2026-10-02)

Explicit browser host-service composition and the complete bounded queued-audio
transport now satisfy UR01.02 and UR08.01, bringing the current count to
**55 checked and 55 open**. The isolated coherent source snapshot passes Editor,
Server, VRClient, desktop WebGPU, all nineteen portable compile rows and fresh
native-Jolt Browser publication with zero compiler warnings/errors. Its actual
published WebAssembly also passes three XRWorld/Jolt cycles and 360 shared frames
under Node with file-backed content fetches and no GPU. The audio implementation
has independent ownership/scheduling review and three production-JavaScript
boundary probes; exact-commit offline audio sample checks and real-device audio
acceptance remain pending. Browser startup still takes Development-mode assembly
scans for replication and type redirects; the separate published-metadata audit
and implementation remains open under UR01.03.

### Reconciled immutable render caches (2026-10-02)

A source/evidence audit of compiled commit `0078867c` closes UR04.02: the engine
tracks admitted raster state in C#, keys retained draws by material/state/target
revision, and bounds draw variants, resource layouts and bind-group/pipeline
caches. The JavaScript cache executes the resolved immutable descriptors; it does
not select the engine's material or raster policy. The current count is
**56 checked and 54 open**. This does not close remaining abstract renderer
operations, shader-generation coverage, actual batching, error-scope coverage or
whole-frame allocation verification.

### Published startup and authored-runtime milestone (2026-10-02)

The startup audit and verified metadata install now satisfy UR01.03, bringing
the count to **57 checked and 53 open**. Actual browser-output assemblies supply
the type table before engine assets, game registration or world hydration.
Premature Development discovery, mismatched active game types and replacement
metadata fail explicitly. Desktop service boundaries remain composed through
leaves. The genuine publisher and three exact-bundle headless WASM lifecycles
pass, followed by the coherent full build gate with zero warnings/errors. See
[the metadata record](../../progress/platform/browser-published-metadata-2026-10-02.md).
The same compiled group adds the authored textured/deformed world and repairs
its derived transform identity cache, bounded final-canvas/depth readbacks,
GPU luminance reduction and conservative shadow sizing/reuse. These partial
renderer changes do not close their broader rows before all contract and live
acceptance requirements are satisfied.

### Compiled asynchronous GPU services and input (2026-10-02)

The reviewed non-blocking renderer contract, in-place device replacement and
complete browser input leaf satisfy UR04.04, UR04.06 and UR09.01. The count is
**60 checked and 50 open**. The coherent source passes Editor, Server, VRClient,
desktop WebGPU, RenderingParity, all nineteen portable compile rows and fresh
native-Jolt WASM publication with zero compiler warnings/errors. The current
checkpoint records the source and boundary evidence. Full device-loss acceptance,
known-value new luminance/UI pixels, complete UI accessibility and performance
remain separate open work.

The same source group adds true GPU mip generation and exact sRGB-encoded
luminance input, authored PBR material recipes from shared engine material
parameters, UI viewport/clip packets, focused DOM button/text bridges and
catalog-aware prefab loading. Those features remain partial against their
broader coverage rows. The source group does not claim a first RenderingParity
browser frame before the reviewed standalone-mode correction is rerun.

### Authored browser load acceptance (2026-10-02)

The physical browser run of `7ba776cf` satisfies UR10.05, bringing the count to
**61 checked and 49 open**. The saved RenderingParity world now visibly runs its
authored game mode, pawn/camera, textured model and combined skeletal/morph
animation after real Editor cooking and publication. Pause/resume, bind-pose
reset, resize and two fresh starts pass on the reported non-fallback Intel Arc
Edge run. This row requires loading shared authored assets; desktop image
comparison remains separately open under UR11.05 and UR06.07. Full numeric
CPU/GPU deformation parity and the wider device matrix are not implied.

### Compiled asset delivery, scene streaming and API admission (2026-10-02)

The coherent asset/UI source passes Editor, Server, VRClient, desktop WebGPU,
RenderingParity, all nineteen portable compile rows and fresh native-Jolt WASM
publication with zero compiler warnings/errors. Full-wording source review closes
UR03.04, UR03.05 and UR10.02, bringing the checklist to **64 checked and 46 open**.
The [delivery record](../../progress/platform/browser-authored-asset-delivery-2026-10-02.md)
contains genuine saved-scene cooking/hydration, alias preservation, compatible
manifest partition and real scene-host lifetime evidence. Actual browser
streaming, throttle/cancel/restart and device memory checks remain open.

The same group adds a bounded display-space RGBA8 image-quad UI profile with
canonical desktop source, exact browser projection and shared batching. Its
compile/cook checks do not establish known-value browser image rendering or close
general UI coverage. Rotated glyphs, MSDF, sRGB image handling and wider UI
accessibility remain explicit gaps.

### Expanded modular-pipeline scope (2026-10-02)

Decision D15 adds four explicitly open named items, UR06.08–UR06.11, for generic
asset contracts, real Advanced backend operations/shaders and live acceptance.
The total at that scope checkpoint became **114**, with **64 completed and 50 open**. The preceding
64/110 milestone remains its historical published result. Completed initial
safety and CPU-direct gates do not count the newly required Advanced support as
finished; there is no new completion from this scope update.

The subsequent generic-contract implementation and full build gate close
UR06.08, bringing the subsequently published `e8e381a2` milestone to **65/114 complete, 49 open**. This closes
the shared authoring/cooking contract, not Advanced stages or browser pixels.

The owner's accompanying submission clarification permits CPU-direct,
GPU-driven indirect and GPU meshlet zero-readback paths. Preserve the selected
mode and implement supported compute/indirect algorithms without steady-state
CPU count/visibility readback or silent CPU fallback. Hardware task/mesh shader
extensions remain a separate concrete capability limit.

### Concrete source and acceptance leaves (2026-10-03)

The former mixed UR06.01, UR06.02, UR06.05, UR06.09, UR06.10 and UR06.11
checkboxes are now non-counted summaries. Their implementation and acceptance
requirements map to the named leaves below; reused UR04, UR05, UR06.08, UR09 and
UR14 requirements are not counted again. UR05.04 is now complete for bounded
logical references, qualifying arrays and native material cohorts. The fresh
compile/cook, focused-probe and genuine Editor export/hydration evidence above
supports the checked local source leaves, not native Browser publication,
rendered output or device recovery. The change
in denominator is decomposition, not a claim that the published milestone ran
additional tests. That first split checked 77/130. The material group checked 83/137 and the published native shadow/cold-admission group checked 85/137. The published quality/accessibility group checks **87/138, with 51 open**: UR06.06 and UR09.04 have compiled, reviewed implementations; UR09.06 owns the still-unverified live text/accessibility behavior. The published submesh ownership group checks **88/139, with 51 open**: UR06.09f4 is implemented, and UR06.10e3 exposed the missing ordered GPU-input copy into native packed deformation. The frozen UI/palette source group checks **90/139, with 49 open** after implementing that ordered producer, its completion-protected recurring-plan cache, and the shared offscreen canvas rendering/input route. Source builds and lifetime review pass; live GPU-copy and UI rendering acceptance remain separate. UR06.10e separates skin/morph from material displacement; UR06.09f separates the admitted generic meshlet route from four remaining producers; UR06.10b separates native material interpretation from shadow publication/depth sampling and authored decals. Summary parents are not counted. UR10.08 corrects an already-published serializer status. The current source adds the previously uncovered ordinary Default/custom GPU-indirect producer as UR06.09g, bringing that source group to **91/140, with 49 open**. The reviewed authored-decal producer and coherent graph invalidation now close UR06.10b3, bringing that source group to **92/140, with 48 open**; its rendered appearance remains under UR06.11c. This separates existing requested submission coverage from native Advanced and generic compute-meshlet work; UR06.11b retains its browser acceptance. The three broad UR04.01/UR04.03/UR04.07 rows are now non-counted summaries with nine concrete leaves: eight already implemented pieces and one remaining dynamic-upload producer. That source-grounded reconciliation brought the checklist to **100/146, with 46 open**. The reviewed per-instance producer now closes UR06.09f3 after the combined native Browser build, bringing current source to **101/146, with 45 open**; neither change adds a browser validation claim. The reviewed transfer and resident-ordering groups now split UR04.03b into three leaves and UR06.09f5 into two leaves: completed buffer transfers, completed texture transfers and completed resident GPU ordering are separate from pending physical creation and mixed direct/GPU ordering. The combined native Browser build passes with zero warnings/errors, bringing current source to **104/149, with 45 open**. The denominator increases by three to expose the existing unfinished producers; no work is counted twice and no browser execution is newly claimed. The later reviewed mixed-ordering and native material vertex groups pass the combined native Browser build and close UR06.09f5b/UR06.10e2, bringing current source to **106/149, with 43 open** without changing the denominator. The reviewed pending physical-creation group and shared retained generation materializer then close UR04.03b3 after the final native Browser build, bringing current source to **107/149, with 42 open**. Source closures do not close browser execution acceptance. The existing UR02.03 executor and whole-closure blocking inventory are now two non-overlapping leaves. Fresh scheduling evidence closes the executor leaf, bringing the current source to **108/150, with 42 open**; the remaining inventory stays open. The frozen upload still describes its preceding 107/149 source set. The offscreen scene-capture audit then exposed two separate unfinished requirements, renderer implementation and live HLOD/impostor acceptance. UR06.12a/b make those requirements explicit, bringing current source to **108/152, with 44 open**. This adds no completed item and leaves the reviewed 108/150 source snapshots unchanged. The completed asynchronous capture producer then closes UR06.12a after its combined builds and independent lifetime checks, bringing the current source to **109/152, with 43 open**. Its GPU pixel and end-to-end browser acceptance remains open in UR06.12b. The reviewed compiler-context completion now closes UR05.05 after genuine negative/positive cooks and the combined native Browser build, bringing current source to **110/152, with 42 open**. This adds no device-rendering claim. A subsequent source audit identifies the previously unlisted Default/custom x4 producer and its browser acceptance as UR06.10g/UR06.11f. Both are open, bringing current source to **110/154, with 44 open**; the completed Advanced-only MSAA item retains its original scope and evidence. The same audit found the previously checked generic GPU-indirect producer was not connected to Default's Web command chain. Reopening UR06.09g corrects the current ledger to **109/154, with 45 open** while preserving the narrower producer evidence. The reviewed Default strategy connection and complete x4 producer then close UR06.09g/UR06.10g after the combined native Browser build, focused resolve cook and production graph/profile checks. The authored and runtime-factory Unlit routes close UR05.03 after genuine export/original-metadata hydration and shared raster/native admission. This brings current source to **112/154, with 42 open**; none of these source closures adds a device-rendering claim.

The verified Default CPU x4 cohort on `e2c47497` now closes UR06.11f1. The original UR06.11f acceptance row becomes a non-counted summary with three nonduplicated leaves: completed CPU opaque/masked coverage, open Default GPU coverage, and open custom/blended/capability-rejection coverage. The denominator increases by two, giving **113/156 with 43 open**. This decomposes existing acceptance scope; it adds no implementation credit or claim that GPU, custom or blended profiles passed.

The verified saved clear/quad cohort on `918b91af` closes UR06.11a1. The former UR06.11a row becomes a non-counted summary with two nonduplicated leaves: this bounded custom asset/camera/output result and the remaining wider Default/Advanced, schema/AA, alias, rejection and simultaneous-output coverage. The denominator increases by one, giving **114/157 with 43 open**. This records an executed acceptance result without closing the broader pipeline matrix.

The verified Default GPU x4 ordinary/depth/AO cohort on `c9139dd6` closes UR06.11f2 without adding or splitting any item, bringing the current ledger to **115/157 with 42 open**. Its separate blended result remains bounded evidence under the still-open custom/blended/rejection leaf.

The verified static Default meshlet cohort on `2b97bda4075563efc9f2558832c6fbcf57611f31` closes UR06.11b1. Splitting the former UR06.11b row into a non-counted summary, the completed bounded cohort and its still-open broader acceptance leaf adds one named item and one verification result, bringing the current ledger to **116/158 with 42 open**. This records software-Chromium pixel and zero-readback evidence for one static mapped panel only; it does not close other submission modes, material/deformation profiles, unsupported-profile diagnostics or physical-device behavior.

The reviewed diagnostic-output and file-mapping move separates UR03.02 into a non-counted summary, the completed bounded placement leaf UR03.02a, and the open remaining inventory leaf UR03.02b. This brings current source to **117/159 with 42 open**. The extra item exposes the remaining physical I/O work without counting the moved operations twice. Source review and the desktop/shared build support the completed leaf; no new browser, external-backend or performance result is claimed.

The bounded static Advanced acceptance in UR06.11b2a brought the ledger to **118/160 with 42 open**. The existing Default CPU/GPU x4 blended result and authored custom x4 color/blended result now have separate completed leaves, UR06.11f3a/b. The former open UR06.11f3 becomes a non-counted summary with those two completed leaves and one open remainder, UR06.11f3c. This gives **120/162 with 42 open**. The denominator increases by two to expose already recorded acceptance; this edit runs no new test and grants no new implementation credit. Custom depth/normal sidecars, unavailable-profile rejection, broader submission and zero-readback behavior, repeated-resize memory stability and physical-device acceptance remain with their named open rows.

The Core network path transfers and shader source refresh now use separate desktop file backends. UR03.02b becomes a non-counted summary with the completed bounded placement leaf UR03.02b1 and the open full-inventory leaf UR03.02b2. This brings the current ledger to **121/163 with 42 open**. The denominator rises by one because the former broad row is split into two leaves. The published network change and the reviewed shader hot-reload change close only these source-placement requirements; no browser or live shader hot-reload acceptance is claimed.

| Kind | Checked | Open | Total |
| --- | ---: | ---: | ---: |
| `impl` | 102 | 16 | 118 |
| `verify` | 13 | 25 | 38 |
| `owner` | 6 | 1 | 7 |
| **Total** | **121** | **42** | **163** |

## Build Gate

Run narrow compile/cook checks before checking a coherent `impl` leaf, and the full gate at end-to-end milestones and before publication. Record which checks are fresh for the source being discussed; a narrow source completion does not imply the full gate passed. Fix failures caused by the change; list unrelated failures separately instead of working around them.

| Check | Command (repository root) |
| --- | --- |
| Shared closure, desktop | `dotnet build XREngine.Runtime.Rendering.WebGPU/XREngine.Runtime.Rendering.WebGPU.csproj` |
| Editor | `dotnet build XREngine.Editor/XREngine.Editor.csproj` |
| Server | `dotnet build XREngine.Server/XREngine.Server.csproj` |
| VRClient | `dotnet build XREngine.VRClient/XREngine.VRClient.csproj` |
| Shared closure, `browser-wasm` | `pwsh Tools/Test-PortableBrowserCompile.ps1 -Configuration Release` |
| Browser publish | `dotnet publish XREngine.Browser/XREngine.Browser.csproj -c Release -m:1` |

The two browser checks need the `wasm-tools` workload. A workstream that touches only desktop leaves may skip them; a workstream that touches any project in [PortableProjects.tsv](../../../../Build/Portable/PortableProjects.tsv) may not. Run repository PowerShell tools with PowerShell 7; Windows builds do not need it.

Historical result: all six checks passed with no warnings on 2026-09-30 for that source snapshot. On 2026-10-01, the current-tree portable compile lane (18 rows) and fresh browser publish also passed using the supported in-process MSBuild task-host override. The exact published 2026-10-02 commit additionally passed the actual Chromium CI build/publish and smoke path linked above. The [build stabilization record](../../progress/platform/unified-runtime-build-stabilization.md) has historical toolchain versions and payload sizes; the [current checkpoint](../../progress/platform/unified-browser-checkpoint-2026-10-01.md) records resumed-build and live qualification evidence.

## Owner Decisions

These block the listed items. Record each decision here with its date when it is made.

| ID | Decision | Blocks | Notes |
| --- | --- | --- | --- |
| D1 | Where the portable engine host lives. | UR17, UR01.06, UR02, UR10 | Approved 2026-09-30: `XREngine.Runtime.Host`, a portable `net10.0` project owning the facade, timer, tick lists, world host, settings and shared host services. Bootstrap retains desktop composition. |
| D2 | The U3 parity target: port the existing rolling-ball sample to portable code. | UR10.06–UR10.08, UR11.05 | Approved 2026-10-01: retain the existing gameplay as the parity target, isolate the VR-specific host, and rebrand the active project to **Rolling Ball** (`Samples/RollingBall`). The descriptive working name and source-only asset audit do not establish legal clearance. |
| D3 | Jolt as the browser physics backend and later desktop default promotion. | UR07 | Approved 2026-10-01: implement browser Jolt and tolerance-based parity; preserve the current desktop physics default until parity is proven. No default promotion is approved by this decision. |
| D4 | Jolt native supply and toolchain plan. | UR07.01 | Approved 2026-09-30: browser-only static archives from the recorded `joltc`/Jolt commits, pinned Emscripten 3.1.56 and the single-threaded wrapper; qualify linkage, callbacks, stepping and teardown. Existing desktop NuGet supply is preserved. See the [native supply proposal](../../design/platform/jolt-browser-native-supply.md). |
| D5 | Pin the .NET SDK and `wasm-tools` workload (`global.json` and recorded workload version). | UR00.02, UR15.01 | Approved 2026-09-30 and implemented: SDK 10.0.401, workload set 10.0.401.1. Emscripten 3.1.56 matches the separately approved browser Jolt supply. |
| D6 | Static registration mechanism for the browser host: the existing script-based generator or the C# source generator the design names. | UR01.04, UR17.05 | Approved 2026-09-30: extend [Generate-AotFactoryRegistrations.ps1](../../../../Tools/Generate-AotFactoryRegistrations.ps1) for both hosts with separate portable/desktop input sets, retiring the browser Python generator. The requested 2026-10-05 master merge retains the upstream C# runtime-contract generator and the browser metadata contract; see the [integration record](../../progress/platform/unified-runtime-master-integration-2026-10-05.md). |
| D7 | Authoring route for web-tier shaders. | UR05.02, UR05.07 | Approved 2026-10-01: additive Slang sources for the browser-compatible engine raster passes, reuse the existing WGSL compute kernels, and initially preserve desktop GLSL unchanged. See the [source inventory](../../progress/rendering/unified-webgpu-shader-inventory.md). |
| D8 | Normalize the Core project's directory casing in git. | UR00.08, UR15.01 | Approved and implemented 2026-09-30: 244 case-only index renames, preserving file contents and modes. |
| D9 | Headless-browser smoke tooling. | UR15.02 | Approved 2026-10-01: official Playwright local smoke tooling (1.63.0, Apache-2.0) for the build machine. The installed Chromium cannot start under this executor's Unix-socket policy; no browser or GPU result is implied. |
| D10 | Cross-platform physics determinism. | UR07.03 | Approved 2026-10-01: use tolerance-based parity first. Strict determinism and repository-built `joltc` on every platform remain a measurement-driven later decision. |
| D11 | Shipping runtime mode (interpreter or AOT). | UR13.02 | Approved 2026-10-01: continue the untrimmed interpreter path; shipping AOT/trimming selection remains dependent on measured performance and serialization qualification. |
| D12 | Retire the separate browser runtime after shared-engine parity. | UR16 | Approved 2026-10-01: retire it after the shared engine reaches parity. Preserve the frozen reference harness until then. |
| D13 | Whether Server and VRClient may reference the model asset pipeline, and whether Bootstrap's registration generator may scan it. | UR00.12 | Approved 2026-09-30: retain the application-root references and Bootstrap model-pipeline scan; update the graph and documentation. Approved 2026-10-01: also retain the existing desktop ModelingIntegration factory scan and correct the stale dependency-boundary assertion; this adds no native dependency. |
| D14 | Browser managed Jolt binding supply after the unchanged package fails static linking. | Browser native proof, UR01.06, UR07.01/UR07.02 | Approved 2026-10-01: a reviewed browser-only source build correcting `JoltPhysicsSharp` 2.22.0's conflicting `JPH_ContactListener_SetProcs` overload (`void` is the pinned native signature). Keep desktop package supply unchanged. Source pin/license review and the exact native/managed spike publish passed on 2026-10-01. Browser execution proof remains required. The owner separately approved Python only as an internal Emscripten dependency; implementation and editing helpers remain C#/PowerShell/JavaScript. See the [native supply record](../../design/platform/jolt-browser-native-supply.md#managed-linkage-findings). |
| D15 | Browser support for modular render-pipeline assets. | UR04, UR05, UR06, UR11 | Clarified 2026-10-02: support Default, Advanced and other authored pipeline assets through the shared modular contracts. Do not whitelist concrete pipeline types or substitute Default. Missing GPU operations retain precise capability diagnostics. Existing Default-based evidence and pipeline defaults remain unchanged; see the [implementation plan](../../design/platform/modular-browser-render-pipelines-2026-10-02.md). |

## Remaining Work

The historical 2026-10-02 modular-contract gate checked 65 of 114 items. The current source checklist checks 121 of 163, with 42 open: 102 of 118 implementation items, 13 of 38 verification items and 6 of 7 owner decisions. These are source-document counts, not a claim that the same source has been published or qualified in a browser. The [prerequisite checklist](native-subsystem-project-split-todo.md) has 35 of 36 items open. The reference/runtime-host checks, Editor/Server/VRClient builds and smokes, browser CI and bounded live qualification are recorded in their dated reports. New source implementation and partial profile evidence do not close an item without its own required build or acceptance evidence. The [unified browser checkpoint](../../progress/platform/unified-browser-checkpoint-2026-10-01.md) distinguishes what now runs from the remaining full production/game, physical-device, performance, recovery and networking qualification.

Sizes are rough planning estimates for one engineer: **S** is days, **M** is one to two weeks, **L** is several weeks, and **XL** is a month or more. Revise them once U1 is reached.

| Stage | Workstreams | Open items | Size | Blocked by |
| --- | --- | --- | --- | --- |
| U0 reference checks | UR00 | 0 | Complete locally | Historical evidence and limits recorded |
| Prerequisite integration | [Native subsystem checklist](native-subsystem-project-split-todo.md) | 35 | L | Remaining native subsystem acceptance; broader physics parity before any default-promotion consideration |
| U1: engine boots | UR17, UR01, UR02, UR03 | 7 | L each; UR01 M | Complete full U1 world/component, asset and frame acceptance beyond the passing lifecycle probes; D1 and D6 are approved |
| U2: engine renders | UR04, UR05, UR06 | 14 | UR04 XL, UR05 XL, UR06 L | Complete renderer/material coverage, generic meshlet producers, native decals and vertex displacement; qualify supported worlds and preserve desktop rendering; D7 is approved |
| U3: project plays | UR07, UR08, UR09, UR10, UR11 | 6 | UR07 L, UR08 M, UR09 L, UR10 M, UR11 M | Complete packaged-editor publication and browser physics/audio/UI and authored-project comparison evidence; D2, D3 and D4 are approved |
| U4: production | UR13, UR14, UR15, UR16 | 15 | M each | Complete measurements, recovery, CI/hosting and device evidence; D9, D11 and D12 are approved |
| U5: networked client | UR12 | 1 | L | U3 |

The current critical path extends the shared material and submission contracts beyond the passing RollingBall, RenderingParity and bounded Advanced runs. Implement coherent remaining groups, use narrow compile/cook checks, and qualify complete browser milestones with exact source provenance. D1, D6, D7 and D14 are approved. Broad numerical, device, lifecycle and project-comparison acceptance stays explicit; it does not block independent implementation work.

## Historical Pause Record (2026-09-30; implementation resumed 2026-10-01)

The following records the state and decisions as of the 2026-09-30 pause. It is retained as historical evidence; use the [2026-10-01 checkpoint](../../progress/platform/unified-browser-checkpoint-2026-10-01.md) and Current State above for the resumed implementation.

**Completed workstreams:** UR00's 12 reference checks and UR17's seven portable-host items. Host owns the real `Engine` facade, timer, settings and world services; Bootstrap owns desktop composition. The final full solution, WebGPU closure, all 15 portable browser projects, fresh browser publish, and the Development Debug build of the sample then named `MonkeyBall` (now `RollingBall`) passed with zero warnings or errors. OpenGL/Vulkan Play→Edit camera/UI restore and Server/VRClient local unit-world startup have recorded live evidence. At the pause, the [host validation record](../../investigations/platform/portable-engine-host-validation.md) left VRClient's shutdown queue stall, physical HMD/proxy operation and broader unit-suite acceptance open. Update 2026-10-05: the stall's root cause is fixed in the collapsed window host and validated in the editor on Vulkan only. OpenGL editor close now ends with a separate shared-context worker exception, and the recorded VRClient run used OpenGL. VRClient still needs a fresh shutdown run.

**Partial preparation; no additional checklist items completed:**

- UR01 references/audit: Browser references Host and evaluates to 13 full portable projects, including animation/audio/input adapters. The reviewed compile manifest has 15 projects. Its entry point still initializes the frozen reference scene host; real engine composition, complete component/serializer registration and startup audit are unfinished.
- UR01.05 live interpreter checks: a disposable page fetched the real authored `XRWorld` YAML. `XRPrefabSource`/camera component YAML and cooked-binary payloads in a MemoryPack envelope passed. The representative world failed by name on the external shader reference `Shaders/Common/UnlitColoredForward.fs`; its YAML and binary round-trips are not qualified. A fetch-backed real asset-reference owner is still required. The [browser boot qualification record](../../progress/platform/portable-browser-engine-boot.md) describes the exact scope.
- Approved D4 native prerequisite: clean exact Jolt/joltc pins build both archives with Emscripten 3.1.56. The initial spike publish passed, but live `Foundation.Init` failed on `JPH_Init` because archive basename admission created an empty `libjoltc` table. Staging the byte-identical `joltc.a` fixes that naming issue and exposes the managed signature conflict recorded in D14. Browser linkage, callbacks, 120 steps, raycast, teardown and absence of worker creation are **not qualified**. Jolt default promotion and determinism remain separate decisions.

**Resume in this order:** resolve D14 and complete the approved native spike; finish UR01.01–UR01.05 with real composition/registrations and supported external asset loading; boot an actual fetched/cooked `XRWorld` through `RuntimeWorld` for UR01.06; then implement and validate UR02 and UR03 before rendering work. Repeat the build gate after each workstream. Keep the separate runtime frozen and preserve every unchecked verification/owner item until its evidence or decision exists.

Remaining workstreams are UR01–UR16: live browser-world boot; shared frame stepping, caller-thread scheduling and production surface lifecycle; asynchronous assets/cooking; the engine WebGPU renderer, shaders and web pipeline; browser physics/audio/input/UI/text and portable game qualification; editor publishing; performance, recovery, CI/hosting/device qualification; networking; and retirement of the separate browser runtime. Decisions D1–D14 are recorded above; browser behavior or production evidence remains open where specified. No physical mobile-device, shipping AOT, Linux CI or production-performance qualification has been completed.

## Execution Order

1. **UR00** until the build gate passes and the harness has recorded evidence.
2. **UR17** and **UR01.01–UR01.05**. These are independent and may proceed together.
3. **UR01.06**, then **UR02** and **UR03**.
4. **UR04**, **UR05**, **UR06** in that order. The UR05.01 shader inventory can start as soon as UR00 is done.
5. **UR07**, **UR08**, **UR09**, **UR10**. These do not depend on each other; UR07 and UR10 do not depend on rendering.
6. **UR11**.
7. **UR13–UR15**, **UR12**, then **UR16**. Start the UR13.01 interpreter measurement as soon as U1 boots.

## Gates

| Gate | Result | Main workstreams |
| --- | --- | --- |
| U0 — Reference harness | The build gate passes. The branch's browser app and editor browser target build, run, and have recorded evidence. The separate runtime is frozen. | UR00 |
| U1 — Engine boots in the browser | The real portable assemblies load a real `XRWorld` asset through fetch, construct scenes, the game mode, and components, and tick fixed/variable updates without rendering. | Native subsystem integration acceptance; UR17; UR01–UR03 |
| U2 — Engine renders in the browser | Engine cameras, `ModelComponent`, and engine materials render through the shared WebGPU backend using Default, Advanced and authored modular pipeline assets, with precise operation capabilities, preserved submission modes, resize and device-loss reporting. | UR04–UR06 |
| U3 — Published project plays | Editor Build Project with the browser target produces a site that runs the startup world with the project's game code, Jolt physics, audio, input, and UI. The parity target chosen in D2 plays. | UR07–UR11 |
| U4 — Production qualification | Physical mobile devices, budgets, runtime mode (interpreter or AOT), recovery, hosting, and CI pass. The separate runtime is retired. | UR13–UR16 |
| U5 — Networked browser client | The browser client joins the real server path. | UR12 |

## UR00 — Stabilize The Branch As A Reference Harness

- [x] **UR00.01** `impl` Repair the portable source guard so it compiles as an inline build task, then validate the implemented source policy: permit `System.Drawing.Primitives` value types and deny Windows bitmap APIs. Resolve any remaining guard failures in the shared closure. Done 2026-09-30: deny rules narrowed to actual vendor, registry, and socket API use; native callback entry points moved out of Rendering into the desktop platform module.
- [x] **UR00.02** `owner` Pin the .NET SDK and install the `wasm-tools` workload per [the browser README](../../../../XREngine.Browser/README.md) (decision D5). Done 2026-09-30 after owner approval: SDK 10.0.401, workload set 10.0.401.1, `wasm-tools` 10.0.112, WebAssembly pack 10.0.12 and Emscripten 3.1.56; normal browser publish passes.
- [x] **UR00.03** `verify` Serve the published output locally. Done 2026-09-30: WebGPU reference fixture rendered in split and single-camera views, both PNGs viewed, counters captured and audio activation observed. Results, newline/hash failure and limits are in the [harness investigation](../../investigations/rendering/desktop-browser-reference-harness.md).
- [x] **UR00.04** `verify` Build the editor and run Build Project with `BrowserWebGPU` on a minimal supported world. Done 2026-09-30: the actual editor CLI publishes a saved camera/indexed-unlit-quad world; served output renders that world with no demo controls. PNG viewed and console errors checked; the settings-save reference expansion is recorded separately.
- [x] **UR00.05** `impl` Freeze feature work in the separate runtime (`Browser*` scene, component, animation, collision, and pipeline types, and `BrowserWorldPublishExporter`); allow fixes only to keep the harness running. Done 2026-09-30: direction recorded in the browser README and mobile tracker; no separate-runtime feature additions made.
- [x] **UR00.06** `impl` Reconcile the [mobile TODO](../rendering/mobile-webgpu-runtime-todo.md) code-completion rows with build/run evidence. Done 2026-09-30: compiled project closure and shader-cooker build distinguished from recipe, feature, device and performance qualification. No row is labelled never compiled without exclusion evidence.
- [x] **UR00.07** `impl` Stop shipping developer harness controls in editor-published output. Done 2026-09-30: staged player entrypoint requires a cooked startup world, hides the fixture overlay and contains no demo/diagnostic controls. Missing world fails by name; stale compressed entrypoint/descriptor variants are removed.
- [x] **UR00.08** `owner` Normalize the Core project's directory casing (decision D8). Done 2026-09-30 after approval: all 759 index paths use `XREngine.Runtime.Core`; 244 case-only renames preserve modes and blobs. The focused production calibration closure builds on Linux with zero warnings/errors. Full Linux CI/browser qualification remains open under UR15.01.
- [x] **UR00.09** `impl` Make the [build gate](#build-gate) pass: the shared closure, Editor, Server, VRClient, the unit-test project, the `browser-wasm` compile lane, and the browser publish. Fix failures that come from the extraction and retargeting; record unrelated failures separately. Done 2026-09-30, with the full solution also building; unit-test execution failures are recorded in the [build stabilization record](../../progress/platform/unified-runtime-build-stabilization.md).
- [x] **UR00.10** `verify` Run the full unit-test suite and triage with the [prerequisite checklist](native-subsystem-project-split-todo.md#build-dependency-and-publish-boundaries). Done 2026-09-30: repeat run completes without host abort, with 4,804 passed, 669 failed and seven runner skips. The [investigation](../../investigations/rendering/desktop-browser-reference-harness.md) separates source contracts, fixtures, behavioral failures and the CUDA requirement, compares baseline names, and records the TRX skip-accounting discrepancy. Broader suite acceptance remains unqualified.
- [x] **UR00.11** `verify` Exercise the relocated native callback entry points. Done 2026-09-30: live Vulkan ImGui creates/resizes/removes a detached viewport and completes native window quarantine; installed clipboard get/set callbacks pass null, sentinel and throwing-provider smokes without changing the OS clipboard. Native debug-utils marker and Streamline messages reach the relocated callbacks. A separate production RenderBench host process with entry points deliberately absent emits the named startup error. Implicit desktop composition and independent GPU-fixture scheduler limits are recorded in the [investigation](../../investigations/rendering/desktop-browser-reference-harness.md).
- [x] **UR00.12** `owner` Decide D13. Approved 2026-09-30: retain Server/VRClient model-pipeline references and Bootstrap's model-pipeline generator scan. Application reference checks and organization docs are updated; the separate ModelingIntegration scan is still a pending decision.

**Acceptance (U0):** the build gate passes, and there is reproducible build, publish, and run evidence for the branch as it stands, with its limits recorded.

## UR17 — Portable Engine Host And Facade

Runs after UR00 and before UR01.06, UR02, and UR10. Blocked on decision D1.

**Starting point.** `XREngine.Runtime.Bootstrap` targets `net10.0-windows7.0`, references every desktop leaf, and owns:

- the `Engine` static facade: about 40 source files under `Engine/`, `SubsystemHost/`, and `RenderingHost/`, including `Engine.Time`, `Engine.TickList`, `Engine.Worlds`, `Engine.State`, `Engine.Lifecycle`, `Engine.PlayMode`, `Engine.Settings`, and `Engine.Threading`;
- `EngineTimer` (`Core/Time/`), which owns the update, collect-visible, and fixed-update workers and the render-thread wait;
- `RuntimeWorldHost` and the world-host composition services (`WorldHost/`);
- `GameStartupSettings` and the other settings types (`Settings/`);
- the implementations installed into about 30 host-service slots declared by the shared projects: `RuntimeTimingServices`, `RuntimeWorldHostServices`, `RuntimeWorldRegistryServices`, `RuntimeWorldObjectServices`, `RuntimeGameModeHostServices`, `RuntimePawnHostServices`, `RuntimePlayerControllerServices`, `RuntimeInputServices`, `RuntimeInputCaptureServices`, `RuntimeAnimationHostServices`, `RuntimeAudioIntegrationServices`, `RuntimeRenderingHostServices`, `RuntimeRenderObjectServices`, `RuntimeShaderServices`, `RuntimePhysicsServices`, `RuntimeThreadServices`, `RuntimeTransformServices`, `RuntimeSceneNodeServices`, `RuntimeSceneStreamingHostServices`, `RuntimeNetworkingHostServices`, `RuntimeNetworkDiscoveryHostServices`, `RuntimeMaintenanceServices`, `RuntimeDebugHostServices`, `RuntimeWindowApplicationServices`, `RuntimeVideoStreamingServices`, `RuntimeVrRenderingServices`, `RuntimeVrStateServices`, `RuntimeVrInputServices`, `RuntimeCharacterMovementVisualizationServices`, `RuntimeStaticColliderAuthoringServices`, and `RuntimeApplicationCapabilityServices`.

`XREngine.Browser` installs none of these slots. Game code calls the facade directly (`Engine.Delta`, `Engine.FixedDelta`, `Engine.Assets`, `Engine.ShutDown`), so the same game assembly cannot load in the browser until the facade is portable.

**Constraint.** A C# partial type cannot span assemblies. When `Engine` moves, its desktop-only members (windows, VR lifecycle, network discovery, profiler transport) cannot stay behind as Bootstrap partials; they move behind host-service contracts or onto a separate desktop type, and their call sites are updated.

- [x] **UR17.01** `owner` Decide the project boundary (decision D1) and the portable project's name. Approved 2026-09-30: `XREngine.Runtime.Host`; the preceding local reference gate is complete.
- [x] **UR17.02** `impl` Inventory Bootstrap file by file as portable as-is, portable once a leaf reference is replaced by an existing contract, or desktop-only. Done 2026-09-30: the [ownership inventory](../../progress/platform/portable-engine-host-ownership.md) contains all 165 source files and 37 service rows, including the desktop provider and lifetime cuts. Inventory changed no production source; existing build evidence applies.
- [x] **UR17.03** `impl` Move the portable set into the portable project, keeping namespaces and type names (`XREngine.Engine`, `XREngine.Timers.EngineTimer`). Done 2026-09-30: 121 reviewed source files moved to `XREngine.Runtime.Host`, registered in the portable manifests, properties and solution. The [compiled identity audit](../../progress/platform/native-subsystem-type-identities.md#shared-engine-host-identities) preserves all 152 baseline public names: 117 moved, 35 stayed, none missing. Desktop builds, full browser compile lane and publish pass; live qualification remains below.
- [x] **UR17.04** `impl` Move desktop-only facade members behind host-service contracts or a desktop type. Done 2026-09-30: platform initialization belongs to desktop composition; startup policy, display extent, renderer/pipeline catalogs and headless physics factories are explicit contracts. VR providers and their leases remain desktop-owned. Missing startup policy and explicit unavailable VR operations fail by name; shared networking admission remains before transport startup.
- [x] **UR17.05** `impl` Give static factory generation an owner that both hosts can use (decision D6). The original 2026-09-30 PowerShell/MSBuild implementation preserved 67 shared factories and Rendering's 124 commands. The requested master integration now uses the shared C# runtime-contract generator and retains the browser bridge and verified metadata. Shared Host/WebGPU compilation and production metadata cook/hydration pass; browser CI for the merged source remains separate. Browser Python and checked-in generated source remain retired.
- [x] **UR17.06** `impl` Reduce Bootstrap to desktop composition: leaf installation, window and VR startup, and desktop launch profiles. Editor, Server, VRClient, benchmarks, and samples build against the new boundary. Done 2026-09-30: the final full solution, WebGPU closure, 15-project Release browser compile, fresh browser publish, and the Development Debug build of the sample then named `MonkeyBall` (now `RollingBall`) passed with zero warnings or errors. Native VMA configuration follows the managed Debug/Release suffix; the sample manifest uses shared OpenVR contract types.
- [x] **UR17.07** `verify` Launch the Editor, Server, and VRClient and load the unit-testing world. Confirm startup, play mode, and frame pacing match the pre-move baseline. Done 2026-09-30: fresh OpenGL/Vulkan Editor Before→Play→Edit→After runs preserve canonical/active camera identity and visible ImGui UI; Server runs a playing headless world; VRClient's local unit-world entry advances fixed updates and measures 89.808 Hz variable updates across 30 ready samples. All three timer source files match their pre-move contents; Editor timing and inherited rendering diagnostics are compared in the [validation record](../../investigations/platform/portable-engine-host-validation.md). VRClient's unrestricted render-dispatch counter is not physical FPS, and its shutdown queue stall remained open. Update 2026-10-05: the root cause is fixed in the collapsed window host and validated in the editor on Vulkan only; OpenGL close has a separate shared-context defect; a fresh VRClient shutdown run is still required. Physical HMD/proxy operation and broader suite acceptance are not established by these smokes.

**Acceptance:** the facade, timer, and host services compile for `browser-wasm` from the same assembly desktop uses, and Bootstrap contains only desktop composition.

## UR01 — Portable Engine Assemblies In The Browser Host

Shared projects already target `net10.0` with whole-project checks. Depends on their [integration acceptance](native-subsystem-project-split-todo.md#build-dependency-and-publish-boundaries); source completion alone does not establish browser startup.

- [x] **UR01.01** `impl` Verify the browser host references the full portable assemblies and include the integration adapters needed for real-world boot. The host currently references Core, Rendering, the WebGPU module, and Animation only. Source-subset profiles and the old portable build property are already removed; qualify the evaluated closure and full API surface.
- [x] **UR01.02** `impl` Make `XREngine.Browser` a composition root that installs, explicitly and as they land:
  - the host services from UR17, each either implemented or installed as a named unsupported service;
  - the browser leaves: WebGPU renderer, browser platform, Jolt, Web Audio, browser input, fetch asset source, WebSocket transport.

  Unavailable required services fail by name.
- [x] **UR01.03** `impl` Audit static constructors, module initializers, and reflection scans reachable at browser startup. Qualify the implemented published-metadata lookup and desktop service boundaries, then move any remaining browser-reachable desktop initialization into leaves.
- [x] **UR01.04** `impl` Generate static registrations for browser component, transform, serializer, and module registration with the mechanism chosen in D6. Retire the branch's Python registration generator.
- [ ] **UR01.05** `verify` Load real YAML/MemoryPack assets in the interpreter and round-trip representative worlds, prefabs, and components.
- [x] **UR01.06** `impl` Boot a real `XRWorld` fetched from a cooked bundle through `RuntimeWorld` and the world host, replacing the host's use of `RuntimeSceneHost`. Construct scenes, the game mode, pawns, and components; run fixed and variable updates; report lifecycle state to the page. Depends on UR17.

**Acceptance:** the browser runs the engine's own world, scene, and component lifecycle from the same binaries desktop uses.

## UR02 — Platform Host, Frame Stepping, And Scheduling

Depends on UR17.

- [ ] **UR02.01** `impl` Extract a single-frame engine step (fixed-step simulation with bounded catch-up, variable update, visibility, render recording, submission) that desktop and browser hosts both call. The loop lives in `EngineTimer`: `RunGameLoop`, `BlockForRendering`, the `DispatchUpdate`/`DispatchCollectVisible`/`DispatchSwapBuffers`/`DispatchRender` methods, and the fixed-update worker. `RuntimeRenderThreadHost` in Rendering only wraps the render-thread side. Start from `EngineTimer.BeginExplicitFrame`, which already runs one deterministic frame on the calling thread with the workers stopped, and extend it to real elapsed time.
- [x] **UR02.02** `impl` Support rendering on the calling thread: update, swap, collect, and render run in sequence within one step. Verify the engine's double-buffered render state works without a dedicated render thread.
**UR02.03 — Caller-thread execution and remaining blocking sites (non-counted summary).** The historical row combined the executor with the whole shared-closure inventory; its two responsibilities are now separate leaves.

- [x] **UR02.03a** `impl` Implement the caller-thread `JobManager` executor and shared scheduling admission. `RuntimeWorkScheduler.ConfigureCallerThread` owns threadless execution before startup; the browser pumps queued jobs through the same manager. Shared physics-chain preparation, CPU ranges and compatibility solving now execute their selected algorithms inline without creating workers or completion events. Native worker topology remains intact. The fresh Core build and 810-check managed caller-world run pass; browser scheduling and whole-world allocation acceptance remain separately scoped. [Scheduling evidence](../../progress/platform/browser-physics-chain-scheduling-2026-10-03.md).
- [ ] **UR02.03b** `impl` Inventory every remaining blocking and thread-creating site in the shared closure and make each asynchronous, move it to a desktop leaf, or confine it to cook and editor code. The [current closure inventory](../../progress/platform/browser-caller-blocking-inventory-2026-10-03.md) records implemented caller-job, transform, event, Uber, image-resize and HLOD/impostor paths, plus explicit media/host-I/O admission. Guarded desktop compatibility implementations remain physically present in shared assemblies; the literal placement requirement stays open. Regenerate the inventory with:

  ```sh
  git grep -l -E "\.Wait\(|\.WaitOne\(|\.Result\b|GetAwaiter\(\)\.GetResult\(\)|Thread\.Sleep|\.WaitAll\(|\.WaitAny\(|\.Join\(" -- ":(icase)<project>/*.cs"
  git grep -l -E "new Thread\(" -- ":(icase)<project>/*.cs"
  git grep -l -E "Task\.Run\(|Parallel\.(For|ForEach|Invoke)|ThreadPool\." -- ":(icase)<project>/*.cs"
  ```

  File counts on 2026-09-30 (lexical matches, so they include false positives such as `string.Join`):

  | Project | Blocking waits | `new Thread` | Task/parallel/pool |
  | --- | --- | --- | --- |
  | `XREngine.Runtime.Core` | 29 | 4 | 8 |
  | `XREngine.Runtime.Rendering` | 39 | 2 | 9 |
  | `XREngine.Data` | 9 | 0 | 7 |
  | `XREngine.Extensions` | 2 | 0 | 4 |
  | `XREngine.Animation` | 1 | 0 | 0 |
  | `XREngine.Runtime.AudioIntegration` | 3 | 0 | 2 |
  | `XREngine.Runtime.AnimationIntegration` | 3 | 0 | 0 |
  | `XREngine.Runtime.InputIntegration` | 1 | 0 | 1 |
  | `XREngine.Browser` | 1 | 0 | 0 |

  The project that UR17 creates adds `EngineTimer` and `Engine.Threading` to this list.
- [x] **UR02.04** `impl` Create `XREngine.Runtime.Platform.Browser` from the branch's canvas host and surface contracts: `browser-canvas-host.js`, `IRuntimeSurfaceHost`, `RuntimeSurfaceState`, `BrowserCanvasRenderTarget`. It covers CSS/backing size, DPR caps, orientation, safe area, detach/reattach, and output generations.
- [x] **UR02.05** `impl` Handle page visibility, freeze/resume, and `pagehide`/`pageshow`; reset timing and invalidate temporal history after suspension or large gaps.
- [ ] **UR02.06** `verify` Confirm desktop editor and VRClient frame pacing is unchanged.

**Acceptance:** one engine frame step drives desktop and browser, and no browser-reachable code blocks.

## UR03 — Asset I/O And Per-Platform Cooking

- [x] **UR03.01** `impl` Route `AssetManager` loading through the asynchronous members of the asset-source contract and implement the browser source with same-origin, credential-free, hash-verified fetches, reusing `content-loader.js` and `content-manifest.js`. The contract is `IRuntimeAssetSource` with `IAssetReadBatch` (`XREngine.Runtime.Core/Assets/IO/`), installed through `DirectStorageIO.Source`; its only implementation is the DirectStorage leaf. It also exposes synchronous members (`Exists`, `ReadAllBytes`, `ReadRange`, `TryReadInto`, `TryReadFileInto`, `IAssetReadBatch.Execute`). Either remove those from the contract or make the browser source fail them by name; do not block on a fetch.
**UR03.02 — Runtime synchronous loading and host-file separation** (summary; count only its leaves). The former broad UR03.02b requirement is now split between the bounded desktop-placement work in UR03.02b1 and the full runtime inventory in UR03.02b2.

- [x] **UR03.02a** `impl` Move diagnostic capture PNG/metrics file output and both FileMap facades' file-open/create/copy operations into the existing desktop platform leaf. Keep shared capture commands, public FileMap signatures, callback timing and stream ownership. Source review and the desktop/shared Release build pass with zero warnings/errors. External `IFileMappingBackend` implementations must add the two host-opening methods and rebuild. This is bounded source-placement evidence, not browser or external-backend runtime acceptance; see the [I/O boundary record](../../progress/platform/browser-runtime-asset-io-boundaries-2026-10-03.md).
**UR03.02b — Remaining runtime host-file separation** (non-counted summary). The original requirement is divided between the completed operations below and the open full inventory.

- [x] **UR03.02b1** `impl` Move the Core network path transfer file operations and shader source refresh file reads to desktop host backends. `DesktopHostFileTransferBackend` owns `FileInfo.Length`, `File.OpenRead`, and `File.OpenWrite`; the published change is `1eb898c7`, with details in the [network host I/O record](../../progress/networking/network-file-transfer-host-io-2026-10-06.md). `DesktopShaderSourceFileBackend` owns shader hot-reload `FileInfo` checks, readability probes, and encoded asynchronous root reads. The six-file source change passed independent source and lifetime review. The combined Rendering and Desktop Release build passed with zero warnings and errors. See the [shader backend record](../../progress/platform/browser-shader-source-file-backend-2026-10-06.md). This closes only these source-placement requirements. It does not claim browser or live shader hot-reload acceptance.
- [ ] **UR03.02b2** `impl` Remove the remaining synchronous load wrappers and physical host-file operations from runtime-reachable paths. On 2026-09-30 the asset manager had 10 sync-over-async sites, and Core and Rendering had 136 direct `File`/`Directory`/`FileStream` call sites across 39 files that bypass the asset source. The [runtime I/O record](../../progress/platform/browser-runtime-asset-io-boundaries-2026-10-03.md) distinguishes implemented catalog/cache-only loading, identity/handoff boundaries, nonblocking shader preloads and named source-admission guards from the remaining physical separation of desktop APIs. The [asset-manager boundary record](../../progress/platform/browser-asset-manager-source-boundaries-2026-10-03.md) adds constructor/load/cache/metadata admission, sticky catalog ownership, retired watcher rejection and remote-response rollback; published browser delivery requires a registered cooked target or a feature-specific async reader. The targeted review found no new bypass in those paths and does not close the literal whole-inventory removal requirement.
  The [host asset output record](../../progress/platform/browser-host-asset-file-output-2026-10-06.md) records the later DDGI and encoded TextFile path-write extraction in `a6e4973`. The extraction preserves stream ownership, encoding and successful-write revision updates. Source review and the Desktop Release build pass. This removes only those physical operations; generic asset serialization and the wider inventory remain open. The checklist count is unchanged.
- [ ] **UR03.03** `impl` Add a platform target to cooking (`CookContent` in `XREngine.Editor/ProjectBuilder.cs`). Preserve all admitted texture interpretation and sampler state through the cooked carrier: raw `XRTexture2D` currently omits `ImportedColorSpace` and `MaxAnisotropy`; material-specific color-space metadata does not close that shared carrier gap. A compatibility-preserving solution must retain non-default anisotropy rather than silently replace authored sampling. Web cooking produces:
  - WGSL shader artifacts;
  - ASTC 4×4 and ETC2 texture variants with RGBA8 fallbacks, reconciled with the [texture compression TODO](../texturing/texture-compression-and-cooked-cache-todo.md);
  - web-decodable audio;
  - per-asset capability requirements.
- [x] **UR03.04** `impl` Generalize the branch's `BrowserContentPackageBuilder` rules (immutable SHA-256 payload URLs, revalidated manifest, dependency closures, essential and streamed splits, strict limits) to the real asset graph instead of browser-only DTOs. Done 2026-10-02: the serializer-owned XRAsset graph now includes authored lazy XRScene roots and compatible validated root partitions; native shared-package authentication remains intact. See the [delivery record](../../progress/platform/browser-authored-asset-delivery-2026-10-02.md).
- [x] **UR03.05** `impl` Keep bounded download concurrency, cancellation, retry, progress, and per-frame integration budgets, reusing the branch's delivery code. Track retained, staging, and estimated GPU bytes. Done 2026-10-02: one bounded cross-source FIFO admits actual synchronous hydration; ownership, staging, transfer/retry and qualified allocation estimates are exposed on demand. Indivisible hydration can overrun its time target and is reported; native package preflight remains eager and separately counted. [Delivery](../../progress/platform/browser-authored-asset-delivery-2026-10-02.md) and [GPU estimate](../../progress/platform/browser-gpu-memory-2026-10-02.md) records do not claim exact live-heap or driver-resident measurements.
- [ ] **UR03.06** `verify` Validate cold and warm cache, throttled, failed, corrupt, and missing payloads, and cancel/restart. This carries over the mobile TODO's MW07 acceptance.

**Acceptance:** the browser loads the same world assets as desktop, with only cooked platform variants differing.

## UR04 — WebGPU Renderer Backend For The Engine

**UR04.01 — Shared renderer wrappers (non-counted summary).** The original abstract-contract inventory is now split into the compiled source leaves below. They cover the admitted engine profiles and retain named diagnostics for unsupported operations. Actual output/convention comparisons remain UR05.06, UR06.11a/c and UR04.08; missing submission/material producers keep their existing named owners.

- [x] **UR04.01a** `impl` Register the WebGPU `AbstractRenderer` backend, implement its abstract entry points or named capability diagnostics, and record clear/presentation through the engine output contract.
- [x] **UR04.01b** `impl` Implement data buffers/views, cooked programs, mesh renderers and validated vertex streams for the bounded engine-camera mesh route, including unlit draws.
- [x] **UR04.01c** `impl` Implement 2D, array and cube texture storage/views plus authored sampler state for admitted textured materials; validate formats, extents, usage and resource ownership.
- [x] **UR04.01d** `impl` Implement attachment-backed framebuffers and render buffers, offscreen targets and exact same-format color resolves. This does not imply scene-capture factory, stereo or XR composition support.
- [x] **UR04.01e** `impl` Publish the bounded engine lit-material factors, uniform blocks and light bindings through exact cooked resource layouts.
- [x] **UR04.01f** `impl` Record cooked compute dispatch in the ordered engine frame, retaining its declared resources and usage transitions.

These leaves have clean shared Rendering/WebGPU/Editor and native Browser build evidence, most recently the 2026-10-03 authored-source closure. The [engine-frame record](../../progress/rendering/browser-engine-frame-diagnostics-2026-10-02.md) and dated material/resource records distinguish implementation from their bounded GPU evidence. Native shader-language extensions absent from WebGPU, synchronous waits and unsupported selected profiles remain specific diagnostics, not claims of equivalent implementations.

- [x] **UR04.02** `impl` Track GL-shaped state in C# and resolve it into cached immutable render pipelines, layouts, and bind groups with complete keys and bounded caches, following the Vulkan backend's approach.
**UR04.03 — Frame recording and bridge traffic (non-counted summary).** Shared engine policy and the remaining hot upload paths have separate leaves. The retained reference harness's `browser-render-pipeline.js` is retired only by UR16.03; it is not counted twice here.

- [x] **UR04.03a** `impl` Record raster/compute commands and uniform/storage snapshots in reusable managed arenas and submit one `Engine.Frame` packet through the generic JavaScript executor, reusing resource, readback, usage-scope and pipeline-cache services.
**UR04.03b — Dynamic transfers and frame-time preparation (non-counted summary).** Physical allocation and transfer acceptance have separate owners; eliminating one frame-packet call does not establish that all hot bridge traffic is gone.

- [x] **UR04.03b1** `impl` Snapshot dynamic buffer writes and fresh/resize/oversized buffer initialization into retained ordered frame preparation, including non-storage destinations. Preserve exact source bytes, retry headroom, generation retirement and accepted-prefix ownership. One frame acceptance call also settles preparation-only attempts and returns the completion watermark without hot polling. The reviewed managed journal/lifetime probes, actual JavaScript executor and combined native Browser build pass. [Transfer ownership](../../progress/rendering/browser-engine-resource-acceptance-2026-10-03.md).
- [x] **UR04.03b2** `impl` Preserve CPU texture upload, array-layer copy and frame-produced copy order through the same acceptance owner. Retain committed sources, reject stale generations and conflicting standalone writes, rebuild rejected frame-dependent array generations, and keep rejected GPU producers out of retryable copies. Independent review passes 45 ownership, 291 journal and 12 texture retry checks; the extended generation-specific texture/luminance predicate probe passes 16 checks, the JavaScript executor passes 17 checks, and the combined native Browser build passes. Live copy pixels, pressure and recovery remain separate. [Texture acceptance](../../progress/rendering/browser-engine-resource-acceptance-2026-10-03.md).
- [x] **UR04.03b3** `impl` Move lazy physical resource, view, sampler, bind-group and command-plan creation onto retained preparation requests and readiness receipts, or establish that a particular call occurs only outside rendering. Keep pending request identities distinct from physical generation-table handles; preserve dependency order, atomic replacement, cancellation and late-result retirement while maintaining one frame acceptance crossing. Done 2026-10-03: bounded owner-generation/descriptor requests receive actual physical handles through the same acceptance import. Dependency preparation, initial snapshots, animated content/clears, atomic replacement and late cancellation retain exact ownership; shared factory and generation cursors yield without discarding active resources. Independent reviews, 25 actual wrapper checks, 12 managed receipt and five clear-record checks, nine JavaScript transport scenarios, two scope-failure cases, 16 dynamic-clear checks, 113 shared-generation checks and the final native Browser build pass. Raw standalone APIs and asynchronous shader/pipeline compilation retain their separate contracts; rendered recovery/pressure acceptance remains open.

- [x] **UR04.04** `impl` Clean up the renderer contract for non-blocking backends:
  - neutral `RuntimeImage` readbacks (implemented shared contract; browser renderer support still required);
  - asynchronous-only screenshots, pixel reads, and luminance;
  - `WaitForGpu` rejected with a named error on non-blocking hosts;
  - truthful capability probes: no indirect-count draw, no mesh shaders, no bindless textures, bounded bind groups.

  Done 2026-10-02: neutral async canvas/image, pixel, depth and luminance paths use bounded accepted-production readbacks; synchronous waits/readbacks reject by name and capabilities/physical bindings enforce browser limits. See the [readback](../../progress/platform/browser-webgpu-readback-2026-10-02.md) and [luminance](../../progress/platform/browser-webgpu-luminance-2026-10-02.md) records. New known-value GPU acceptance remains separate.
- [x] **UR04.05** `impl` Present to the canvas through `RenderFrameOutputDescription` with surface generations, re-acquiring the output each frame and rejecting obsolete plans after resize.
- [x] **UR04.06** `impl` Handle pending, ready, failed, and lost states. Recovery reconstructs device resources from CPU-side and cooked sources, carrying over the mobile TODO's MW10.01–MW10.05. Done 2026-10-02: reviewed finite replacement retires the old GPU owner, reconstructs from retained engine sources, preserves world/player/physics identity, and requires current validated output before resuming. The [recovery record](../../progress/platform/browser-webgpu-device-recovery-2026-10-02.md) distinguishes implementation/boundary checks from open live loss/reconstruction acceptance.
**UR04.07 — Diagnostics and allocation acceptance (non-counted summary).**

- [x] **UR04.07a** `impl` Scope engine-frame GPU validation/out-of-memory errors, retain resource/pass debug labels, and expose JavaScript and managed bridge counters. UR13.04 owns measured warm-world allocation acceptance across simulation, visibility, recording and submission; narrow zero-allocation probes do not close that verification.
- [ ] **UR04.08** `verify` Validate every shared renderer-contract change on OpenGL and Vulkan with editor captures from multiple positions.

**Acceptance:** the engine's renderer runs on WebGPU through the same contract as OpenGL and Vulkan, with one JavaScript crossing per frame.

## UR05 — Shaders And Materials

**Starting point.** `Build/CommonAssets/Shaders` holds 552 GLSL sources: 222 fragment, 148 compute, 109 includes, 40 vertex, 20 geometry, 9 mesh/task, and 4 tessellation. Three of them (`Common/MaterialTable.glsl` and the two `Graphics/BindlessMesh` stages) require bindless-texture or 64-bit integer extensions. The same tree has 2 Slang pilots under `FrontendPilots/`. The only other Slang source and all 10 WGSL sources belong to the separate runtime. Slang does not ingest desktop GLSL 4.6 unchanged, so the web tier's shaders must be ported, not only cooked.

- [x] **UR05.01** `impl` Inventory the shaders the web tier of `DefaultRenderPipeline` needs. Classify each as portable through Slang, needing a WGSL rewrite, or desktop-only. Record the list, grouped by pass, in a progress doc; it is the work list for UR05.07.
- [x] **UR05.02** `impl` Cook engine shaders to WGSL with the pinned Slang route and the shader artifact format (reusing `Tools/ShaderCooker`, the `ShaderCompileTarget.WebGPUWgsl` target, and artifact schema checks). Follow the [Slang cross-compile plan](../../design/scripting/slang-shader-cross-compile-plan.md). Depends on decision D7.
- [x] **UR05.03** `impl` Extend the engine's material shader generation with a WGSL target, replacing the separate browser material generator. Support the engine's lit material model, not only unlit and Lambert. The [authored color-coverage record](../../progress/platform/browser-authored-color-coverage-2026-10-03.md) documents the implemented versioned uniform-alpha modes and exact normal/shadow companions, including real Editor roundtrips. The [two-image alpha record](../../progress/platform/browser-authored-textured-alpha-2026-10-03.md) additionally covers exact diffuse-alpha/red-mask lowering, additive authored/cooked carriers and matching auxiliary/native programs. The [canonical Uber base contract](../../progress/rendering/browser-uber-base-material-contract-2026-10-04.md) records source-exact feature specialization, generic/native companions, retained probe ownership and genuine cook/publish/hydration checks. The [ordinary Unlit record](../../progress/rendering/browser-engine-unlit-materials-2026-10-04.md) completes the five canonical families for both authored per-material cooks and runtime factory variants, including genuine exporter/original-metadata hydration and exact native admission. Shared generation now covers the inventoried required material families; optional inventory-D features retain named exclusions. Combined managed/native builds and focused cooks pass; rendered material interpretation remains separate acceptance.
- [x] **UR05.04** `impl` Implement the WebGPU encoding of logical material and texture references with bounded bind groups, texture arrays for qualifying content, and material batching. No desktop bindless handle reaches WGSL. Done in the reconstructed source: exact logical texture/sampler generation pairs lower to finite native cohorts, qualifying authored 2D arrays retain their layer/mip identity, and material rows select cohorts. `WebGpuAdvancedShadingOutput.Cohorts`, `WebGpuTexture2DArray` and `AdvancedShadeTextures.slang` implement this encoding; generic material-generator coverage remains UR05.03 and live native material interpretation remains UR06.11c. See the [native admission record](../../progress/rendering/browser-advanced-admission-2026-10-02.md).
- [x] **UR05.05** `impl` Report web-unsupported shaders and material features at cook time with material, pass, source location, and reason. The [material-cook record](../../progress/platform/browser-authored-lit-material-cooking-2026-10-02.md) covers shared material projection. The [compiler-diagnostic record](../../progress/platform/browser-shader-cook-diagnostics-2026-10-04.md) completes standalone, generated, per-material and native-vertex compiler paths: genuine negative cooks retain exact recipe context and compiler coordinates when available, reject without publishing a manifest, and preserve valid artifact bytes. Bounded fallback diagnostics do not invent coordinates or expose absolute paths. The combined native Browser build passes; rendered interpretation remains separate acceptance.
- [ ] **UR05.06** `verify` Verify coordinate conventions (clip depth range, texture Y, winding, matrix layout, reversed-Z where the engine uses it) with known-value renders. The [generic reversed-depth record](../../progress/rendering/browser-generic-reversed-depth-2026-10-04.md) now documents implemented frozen-camera comparison/clear lowering, independent shadow scopes, sky and post-depth consumers. Six exact cooks, 33 production scope/callback checks and combined managed/native builds pass; known-value browser renders remain required for this verification.
- [ ] **UR05.07** `impl` Port the hand-written web-tier shaders from the UR05.01 list by the route chosen in D7: depth and shadow casters, forward lit surfaces, sky and environment, tonemapping and the bounded post-process set, then UI and text. Implement coherent groups with narrow cook/compile checks and run known-value rendering at end-to-end milestones; each group's known-value result must be recorded before this coverage is closed, not before work on another group begins. Desktop GLSL behavior is unchanged.

**Acceptance:** engine materials cook to WGSL and render with correct interpretation; unsupported ones fail by name.

## UR06 — Modular Render Pipeline Web Support

Decision D15 requires the same modular asset model for Default, Advanced and
other authored pipelines. Existing Default-based evidence remains valid; neither
Advanced nor arbitrary authored-pipeline support is implied by it. Do not close
this work by allowing only a concrete pipeline class or substituting another
pipeline when a required operation is missing.

**UR06.01 — Modular pipeline coverage (non-counted summary).** The shared
operation/resource/program declarations are owned by UR06.08; concrete authored
persistence/admission work is below under UR06.02. Default's depth, lit surfaces,
directional/spot/point shadows, sky/environment, coverage/transparency, HDR,
tonemapping and bounded effects remain in the shader/material coverage owners
UR05.03, UR05.05 and UR05.07. Engine UI/text completion remains UR09.02–UR09.04.
Advanced backend contracts and actual native/output programs are owned by the
UR06.09 and UR06.10 leaves. These requirements apply to authored assets without
pipeline substitution; this summary adds no duplicate checkbox.

**UR06.02 — Authored selection, persistence and admission (non-counted summary).**
UR06.08 owns the common publication/runtime declaration and scoped-program
infrastructure. The following leaves own its camera-persistence integration and
remaining cold-admission/reference-identity gaps.

- [x] **UR06.02a** `impl` Persist and restore authored camera pipeline sources, nested executable commands, branch lifetime, custom postprocess settings and camera AA/MSAA/HDR overrides; preserve unassigned-camera defaults across fresh factory IDs and synchronize already bound viewports. Fresh 35-assertion camera checks and genuine single-camera Advanced Editor export/separate-process cooked hydration preserve the source/settings, four texture identities and pawn-camera alias. This does not establish shared multi-camera reference equality. See the [camera contract record](../../progress/rendering/browser-modular-pipeline-contracts-2026-10-02.md) and [fresh static integration record](../../progress/rendering/browser-advanced-static-integration-2026-10-03.md).
- [x] **UR06.02b** `impl` Preserve one shared authored pipeline object's reference identity across multiple cameras through generic cooked binary hydration and standalone camera-array serialization. Generic BinaryV2 definitions/uses and document-scoped YAML anchors retain exact aliases; distinct objects with equal IDs stay distinct. The 48-check managed probe includes two cameras sharing a custom pipeline in an XRWorld, independent camera settings, standalone arrays, repeated YAML documents, cycles and explicit old-cache recook diagnostics. See the [shared graph contract](../../../architecture/assets/cooked-asset-aot-and-io.md); browser multi-camera acceptance remains UR06.11a.
- [x] **UR06.02c** `impl` Complete cold publication admission for the implemented native scene profiles, using shared geometry/deformation/material and texture/sampler/bank contracts with asset/pass/resource diagnostics. Immutable startup/streamed metadata includes future standalone shadow requirements; native scene routes distinguish opaque consumers from marker/late/output commands and validate exact cooked meshlets when their selected strategy is declared. Unimplemented vertex displacement, decal publication and unsupported probe generations reject explicitly. Later runtime/environment strategy or quality changes still undergo runtime admission. Fresh builds and genuine shadow-fixture export/hydration pass; see the [native shadow and cold-admission record](../../progress/rendering/browser-native-standalone-shadow-2026-10-03.md).
- [x] **UR06.03** `impl` Historical safety gate: make unavailable `AdvancedRenderPipeline` requests report a reason through `Available` and fail under `Required`, rather than silently substituting a pipeline. The initial WebGPU rejection was implemented. D15 supersedes keeping Advanced permanently unsupported; this completed guard is not Advanced rendering support. Its replacement with concrete capability checks and real execution is explicitly open in UR06.08–UR06.11.
- [x] **UR06.04** `impl` Baseline mesh submission: resolve `CpuDirect` for WebGPU. This implemented baseline remains valid and does not establish Advanced GPU-scene submission. D15 requires the selected pipeline's real GPU-driven paths, tracked in UR06.09; measurement still governs performance claims and default promotion, rather than blocking required source implementation.

**UR06.05 — Mesh deformation (non-counted summary).** The ordinary engine mesh
deformation implementation and numeric acceptance are distinct. Advanced's
current/previous native producer integration is owned only by UR06.10e.

- [x] **UR06.05a** `impl` Run the admitted ordinary engine mesh skinning/blendshape profile through its selected CPU or canonical packed WebGPU compute route, publishing position/normal/tangent streams with exact palette and morph ownership and no silent GPU-to-CPU substitution. `WebGpuMeshDeformation` and the cooked packed-skinning family are included in the fresh renderer/Editor builds and 75-recipe cook.
- [ ] **UR06.05b** `verify` Compare CPU and GPU positions, normals and tangents for matched skeletal, morph-only and combined deformation inputs, including palette bases, spill influences and sparse morphs. Record numeric tolerances and known-value rendered results; historical visible animation alone is insufficient.
- [x] **UR06.06** `impl` Add mobile quality tiers to engine settings: backing resolution and DPR caps, shadow sizes and cadence, light counts, texture tiers, and post effects. Disabled effects must not allocate resources. Done 2026-10-03: shared profiles control both pipeline families; disabled GTAO/bloom omit their targets, stages and programs. Native AO uses a schema-validated neutral binding, with the selection frozen into resource generation and reservation order. Combined WebGPU/native-WASM Browser builds and independent source review pass; live resource/pixel acceptance remains in UR06.11c. See the [implementation record](../../progress/platform/browser-quality-accessibility-2026-10-03.md).
- [ ] **UR06.07** `verify` Render the same test worlds on OpenGL, Vulkan, and WebGPU using Default, Advanced and an unrelated authored modular pipeline; compare tolerant captures and document deliberate differences and concrete capability limits.
- [x] **UR06.08** `impl` Replace browser-specific concrete-pipeline and global pass-name gates with shared pipeline/command requirement and cooked-program dependency contracts, generic metadata, scoped program references and backward-readable existing manifests. Publication and runtime consume the same declarations without a concrete-type whitelist, Default artifact requirement or implicit Default postprocess schema. A shader-free custom clear pipeline and an unrelated quad pipeline require only their own operations/programs. The historical 2026-10-02 full compile/native-WASM gate established this foundation; fresh camera/admission probes recheck its reconstructed integration. Camera persistence is counted only in UR06.02a–b and remaining native cold eligibility in UR06.02c. See the [implementation record](../../progress/rendering/browser-modular-pipeline-contracts-2026-10-02.md); actual GPU output remains UR06.11.

**UR06.09 — Advanced backend operations and submission (non-counted summary).**
These leaves cover the Advanced-specific contracts layered on the general
renderer wrappers in UR04.01, cached state in UR04.02 and asynchronous API in
UR04.04. They do not count those general requirements again. Fresh source and
probe evidence is in the [static integration record](../../progress/rendering/browser-advanced-static-integration-2026-10-03.md)
and [native admission record](../../progress/rendering/browser-advanced-admission-2026-10-02.md).

- [x] **UR06.09a** `impl` Materialize the native physical visibility layout with three integer MRTs, exact integer/float/depth formats, storage-image outputs and exact mip/layer/aspect texture views. Check attachment extents, sample counts, numeric types and negotiated device limits; reject unsupported physical forms without format substitution.
- [x] **UR06.09b** `impl` Retain the canonical GPU-scene publication and geometry/material/global arenas in bounded frame slots. Pin the exact publication until non-blocking queue completion, preserve resource generations, and retry GPU-owned slots without overwriting them or mapping counts/visibility.
- [x] **UR06.09c** `impl` Lower indexed-indirect/count and indirect-compute dispatch through ordered WebGPU passes and copies. GPU-count lowering zeros inactive bounded slots on the GPU; pass-boundary visibility and completion receipts replace blocking waits. Require optional indexed first-instance support only for the routes that consume it.
- [x] **UR06.09d** `impl` Preserve Advanced's authored CPU-direct, GPU-driven indirect and GPU meshlet zero-readback submission. Emit direct draws for the direct producer and vertex-pulled indirect draws for GPU-produced indexed/meshlet streams; reject missing resident meshlets and strategy downgrades. Hardware task/mesh extensions remain a distinct unavailable capability.
- [x] **UR06.09e** `impl` Reserve bounded native output families against exact device/program contracts, owner identity, publication, resource generation and stage order. Retain retiring reservations until queue completion and prevent partial/post-only native frames from submitting. The fresh 25-assertion admission probe covers these source boundaries; live lifetime behavior remains UR14.01 with the native cases listed under UR06.11.
**UR06.09f — Generic authored meshlet submission (non-counted summary).** The material-independent route preserves authored Default/custom graphs and shaders. Its initial executable profile and remaining producers have separate owners below; none may substitute indexed or CPU submission.

- [x] **UR06.09f1** `impl` Implement the single-LOD, single-instance generic compute/indirect meshlet route, exact cooked cull/refit/finalize companions, original primitive/corner ordering, frozen CPU ownership and completion-retained publication/work slots. Unknown vertex behavior uses conservative unbounded GPU expansion; canonical GPU deformation refits its final position stream. Zero-instance commands remain no-ops. Combined WebGPU/Editor/native Browser builds and all three companion cooks pass; runtime strategy acceptance remains UR06.11b. See the [authored meshlet record](../../progress/rendering/browser-authored-meshlet-indexed-2026-10-03.md).
- [ ] **UR06.09f2** `impl` Publish GPU-selected mesh/LOD decisions and use them in generic meshlet expansion, preserving authored LOD policy without CPU visibility/count readback. The initial route explicitly rejects dynamic multi-LOD sources.
- [x] **UR06.09f3** `impl` Publish per-instance transforms and bounds for generic meshlet submission with more than one logical instance; consume them in culling, deformation and raster addressing without substituting a single instance. The typed runtime publisher supplies exact current/previous matrix and bounds rows to raster and GPU union visibility; command instance count and native addressing are preserved across direct, indirect and meshlet routes. Shared current skin/morph output is admitted through its explicit source contract; separate instance palettes and missing previous deformed vertices retain precise diagnostics. Independent review, 58 production checks, 228 resident regression checks, 17 JavaScript checks, seven shader cooks and the combined native Browser build pass. [Implementation and acceptance limits](../../progress/rendering/browser-authored-meshlet-indexed-2026-10-03.md#authored-instance-publication-and-conservative-gpu-visibility).
- [x] **UR06.09f4** `impl` Publish exact deformation streams and ownership for distinct authored submeshes sharing a renderer, so each generic meshlet source refits and draws its own selected current geometry. Done 2026-10-03: shared mesh-keyed palette/morph ownership preserves exact bone order, bind root, controls and source generations; CPU-direct and generic meshlet paths retain separate draw/deformation caches and retire removed sources. The WebGPU build and 337 managed ownership checks pass, including zero allocations across 200 warmed preparations; independent review cleared lifetime and desktop compatibility. External GPU palettes are consumed directly by the generic route. Native packed GPU-input copying is separately implemented in UR06.10e3, and live browser acceptance remains UR06.11b/d. See the [ownership record](../../progress/rendering/browser-authored-meshlet-indexed-2026-10-03.md).
**UR06.09f5 — Authored transparent ordering (non-counted summary).** GPU-resident sources and mixed direct/GPU sources have distinct replay producers; neither may alter the requested draw route or read GPU order back to the CPU.

- [x] **UR06.09f5a** `impl` Rank admitted resident transparent mesh sources on the GPU from the selected frozen collection's authored priority, view-dependent bounds distance and exact insertion token. Retain per-candidate GPU buffer snapshots and original primitive/instance order, then replay exact material/pipeline commands in rank order through GPU-written indirect arguments. Independent source review, 26 production-method checks, nine JavaScript replay checks, both compute cooks and the combined native Browser build pass. Bounded rank/candidate budgets and unsupported comparers are explicit. Live sorted pixels and zero-readback traces remain UR06.11b. [Resident ordering contract](../../progress/rendering/browser-authored-meshlet-indexed-2026-10-03.md).
- [x] **UR06.09f5b** `impl` Interleave explicit CPU-direct and GPU-resident transparent mesh sources through shared frozen ordering and exact opt-in raster rank gates, preserving each requested direct/indirect route, callbacks, binding generations, coverage, depth and blend semantics. Done 2026-10-03: explicit direct sources keep direct indexed draws through proven vertex rank gates, while resident sources keep their indirect route. Exact candidate-owned inputs and one callback capture preserve source order and coverage. Independent source review, 33 producer/replay/ABI checks, 66 gate checks, 15 JavaScript checks, seven real cooked artifacts, 51 actual cooker/browser-catalog checks and the combined native Browser build pass. Arbitrary callbacks or shaders without an executable gate retain precise contract diagnostics; live mixed output remains UR06.11b.

- [x] **UR06.09g** `impl` Implement ordinary authored Default/custom GPU-indirect submission with its original raster programs and index buffers, without requiring a meshlet payload. Preserve instrumented and zero-readback strategy identity, frozen source/LOD/submesh bindings, GPU-derived current-position bounds, exact padded index ownership and completion/abort lifetimes. The whole-primitive culler writes original-order indexed draw arguments without CPU visibility/count readback. The generic producer's managed build and 228 production checks pass. The subsequent Default integration now preserves the authored strategy and viewport override in both scene and normal passes, declares exact GPU operation/program requirements, and passes the combined native Browser build plus production graph/profile checks. See the [Default integration record](../../progress/rendering/browser-default-generic-msaa-2026-10-04.md); multiple instances and transparent ordering remain with their existing producers, and live browser acceptance remains UR06.11b. See the [authored submission record](../../progress/rendering/browser-authored-meshlet-indexed-2026-10-03.md).

**UR06.10 — Advanced cooked stages and output (non-counted summary).**
The initial family was static, mono and single-sample; the current group adds native skin/morph and mono x4 MSAA. All 87 canonical recipes cook; the [fresh integration record](../../progress/rendering/browser-advanced-static-integration-2026-10-03.md)
also records genuine Editor export and cooked source reload, not browser GPU
execution. Generic logical texture encoding is counted only in UR05.04.

- [x] **UR06.10a** `impl` Cook and connect static visibility compaction/finalization/raster, integer reconstruction, work classification, depth-pyramid and GTAO stages to the canonical frame contract. Preserve static current/previous transforms and primitive/material identities; no synthetic scene or alternate pipeline stands in for these producers.
**UR06.10b — Native material and global sampling (non-counted summary).** Material interpretation and missing physical depth sampling are separate implementation leaves. Selected-global cold admission remains solely UR06.02c; known-value pixels remain UR06.11c.

- [x] **UR06.10b1** `impl` Cook and bind native standard-material PBR/background shading, admitted float-texture cohorts, retained light/global resources and declared surface exports. The versioned engine-surface companion preserves independent metallic/roughness red-channel maps, BaseColor/Opacity/Specular/Emission factors and the frontend normal convention without changing desktop records. Runtime global closure retains probe, shadow and decal references that satisfy the existing float-bank contract. Four exact native/MSAA/export companions cook; fresh builds and genuine Editor export/hydration pass. Old artifacts missing the schema witness reject with an explicit recook diagnostic. Depth-texture sampling remains the next leaf.
- [x] **UR06.10b2** `impl` Connect standalone browser directional/point/spot outputs to canonical native shadow publications and exact typed depth/comparison companions. The bounded one-of-each, normal-Z, non-atlas PCSS profile preserves original depth/R16Float producers, exact frozen receipts and complete point faces. Typed banks stay within baseline limits, allocate depth padding only when needed and reject stale/tombstoned sources. Shared projection policy and full biased-coordinate derivatives preserve selected receiver semantics. Fresh builds and all 91 recipes cook; actual shadow pixels remain UR06.11c. See the [native shadow record](../../progress/rendering/browser-native-standalone-shadow-2026-10-03.md).
- [x] **UR06.10b3** `impl` Publish ordinary authored decals into canonical native global rows and consume their exact admitted material semantics. Done 2026-10-03: the canonical deferred decal preserves its XZ box projection and texture-alpha albedo blend, frozen per-view order, selected receiver pass and borrowed image identity. Generic engine-surface decals retain independent maps and normal/factor interpretation. Current graph ownership, branch replacement and in-place Switch edits invalidate declaration and submission demand coherently; unsupported custom/OIT draw contracts fail by name. Independent review, 274 production checks, 28 projection checks, 10 genuine export checks, eight native cooks and clean Rendering/Editor/Browser native builds establish source closure with zero warmed allocation and component registry drift. Existing cross-pipeline child reparenting and inline-YAML raw-image metadata limitations are recorded, not changed by this producer. Live decal output remains UR06.11c. See the [authored-decal record](../../progress/platform/browser-native-authored-decals-2026-10-03.md).
- [x] **UR06.10c** `impl` Connect Advanced scene-copy/presentation and selected bounded postprocess programs, including bloom, motion blur, depth of field, FXAA/SMAA and GPU auto-exposure history. Resource declarations and cooked dependencies follow authored settings; disabled effects do not own execution targets/history. Unsupported temporal reconstruction, atmosphere/fog and other unavailable profiles retain explicit diagnostics.
- [x] **UR06.10d** `impl` Install and validate exact scoped Advanced program ABIs/dependencies at initial output creation and device replacement, and re-evaluate the native capability/reservation contract for the replacement device. This is native-family integration with the recovery owner UR04.06, not a second implementation of general device recovery.
**UR06.10e — Native deformation families (non-counted summary).** Ordinary mesh compute from UR06.05a does not satisfy these producers; live numeric and rendered evidence remains separately owned.

- [x] **UR06.10e1** `impl` Implement canonical current/previous native skinning and sparse-morph production, authored controls, ordered geometry copies and matching visibility/reconstruction/shading consumers. Retain deformation generations and prepared temporal relations. Exact aggregate/copy companions cook and the integrated native Browser build passes; desktop GLSL/bindings and authored rich-morph policy remain unchanged. Numeric parity remains UR06.05b and native motion execution remains UR06.11d.
- [x] **UR06.10e2** `impl` Implement selected material vertex-displacement producers and exact visibility/reconstruction/shading companions with conservative geometry bounds and retained current/previous relations. Done 2026-10-03: the same authored position/normal function produces an exact PBR raster/native-compute pair plus depth-normal, three shadow casters and two receiver companions. Up to four explicit float4 inputs, canonical native skin/morph composition, conservative culling and accepted-frame history are retained without altering desktop GLSL. Independent reviews, 90 production runtime checks, 16 dispatch checks, 62 auxiliary checks, 23 real packaging/loader checks, eight shader cooks and Editor/WebGPU/native Browser builds pass; warm captures and auxiliary resolution allocate zero bytes. Generic raster/caster skin/morph composition remains a named source-equivalence limit because its existing normal equations differ. Live displacement/motion output remains UR06.11d. See the [material vertex contract](../../progress/rendering/browser-native-material-vertices-2026-10-03.md).
- [x] **UR06.10e3** `impl` Copy explicitly GPU-owned pose inputs into the native Advanced packed deformation arena through an ordered GPU producer, preserving exact palette order, source generation and current/previous ownership without CPU readback. Done 2026-10-03: exact resident source captures exclude GPU-owned palette spans from CPU uploads and record retained GPU copies before aggregate deformation. Stale captures reject before recording and can be recaptured without poisoning the slot. A bounded recurring-shape cache handles alternating GPU palettes across three output slots without warm plan churn. The actual Rendering/WebGPU build, independent lifetime review, actual JS executor witness and managed helper/arena checks pass; GPU-produced active morph inputs remain explicitly unsupported. Live producer and motion acceptance remains UR06.11d. See the [copy contract](../../../architecture/rendering/webgpu-indirect-submission.md#native-aggregate-gpu-palettes).
- [x] **UR06.10f** `impl` Implement faithful per-sample Advanced MSAA visibility, reconstruction, shading and resolve with declared storage/format/sample contracts and exact cooked companions. The mono x4 profile preserves all covered samples through native shading and resolves canonical sidecars coherently; x2/x8/x16, stereo and unavailable profiles reject explicitly. Seven new companions plus the original visibility recipe cook; integrated builds and 24 production profile checks pass. See the [MSAA integration record](../../progress/rendering/browser-advanced-msaa-2026-10-03.md). Actual per-sample pixels remain UR06.11e.
- [x] **UR06.10g** `impl` Implement shared generic and Default mono x4 MSAA raster targets, matching depth/normal prepass and closest-sample sidecar resolution, preserved forward depth, and final HDR color resolve before single-sample postprocessing. Admit supported custom authored x4 framebuffer graphs through the same material and draw contracts. Default and generic material adapters now admit exact x1/x4 targets through retained resource generations. The new depth/normal resolve artifact cooks, the combined managed/native builds pass, and 214 production resource/graph/requirements checks cover twelve sample/AO/submission profiles. See the [Default integration record](../../progress/rendering/browser-default-generic-msaa-2026-10-04.md). Unavailable depth/stencil blits, sample counts and output profiles remain explicit; separate browser profiles are tracked in UR06.11f1–f3.

**UR06.11 — Browser execution acceptance (non-counted summary).**
A source build, recipe cook, admission probe or Editor export does not pass these
leaves. Desktop image comparison remains solely UR06.07. General loss/restart,
hide/show and lifecycle acceptance remains solely UR14.01: include Advanced and
custom pipelines, scoped-catalog reinstallation, resize, pending/retiring native
outputs, stale generations and preserved world/asset/settings identity in that
existing matrix. UR04.06 and UR14.02 own the underlying recovery/stale-completion
implementation. This cross-reference adds no recovery checkbox.

**UR06.11a — Saved pipeline assets and output identity (non-counted summary).** The completed clear/quad cohort and the remaining broader asset/profile/isolation requirements are separate leaves; the original acceptance scope remains.

- [x] **UR06.11a1** `verify` Publish and run saved game-defined clear-only and unrelated quad pipeline assets with AA None. Verify authored source/settings identity, known-value output, sequential selection of two cameras sharing one clear-pipeline source, a distinct quad source, two fresh starts and resize. Done on `918b91af` in [run 37238768014](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37238768014/job/111548627207): the genuine Editor bundle passes all twelve sampled states, actual canvas acquisition/submission and quad-draw checks. Initial and resized screenshots were inspected. See the [modular sample record](../../progress/rendering/browser-modular-sample-2026-10-04.md). This is software-adapter evidence for sequential use of one viewport.
- [ ] **UR06.11a2** `verify` Complete saved Default/Advanced pipeline output and authored asset/settings identity coverage, remaining custom schema/AA-setting preservation, scoped-program alias isolation and precise operation-level capability rejection. Exercise simultaneous output isolation for cameras sharing one source after UR06.02b; the sequential clear/quad result in UR06.11a1 does not prove this. Reuse the existing MSAA execution results under UR06.11e/f instead of counting their pixel comparisons again.
**UR06.11b — Advanced and authored Default/custom submission acceptance (non-counted summary).** The bounded static Default meshlet cohort and all broader acceptance requirements have separate leaves below.

- [x] **UR06.11b1** `verify` Run the freshly Editor-published static Default meshlet world in Chromium's software WebGPU path through two fresh GPU contexts. Verify actual select-LOD, cull-expand and finalize dispatches, indexed-indirect draws, authored raster program identity, CPU/GPU pixels at initial and resized extents, zero GPU read maps or mapped-scene direct draws, and clean CPU/GPU teardown. Done on `2b97bda4075563efc9f2558832c6fbcf57611f31` in [run 37290314776, job 111708082004](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37290314776/job/111708082004): at 977×550 and resized 813×457/893×502, all four CPU/GPU comparisons have mean RGB error 0, mismatched pixels 0 and silhouette overlap 1. This is one static, single-LOD/single-instance cohort on the software adapter; see the [acceptance record](../../progress/rendering/browser-authored-meshlet-indexed-2026-10-03.md#browser-acceptance-bounded-static-default-meshlet-cohort).
**UR06.11b2 — Native Advanced and broader submission acceptance (non-counted summary).** The static modifier-free application result and the remaining submission matrix have separate leaves. This replaces one open leaf with one completed leaf and one open leaf; the total increases from 159 to 160 without closing the broader matrix.

- [x] **UR06.11b2a** `verify` Publish and run the saved static Advanced sample through two fresh software-WebGPU contexts and resize. Verify the actual selected modifier-free native program/descriptor, native visibility/shading and output stages, authored surface presence and zero GPU read mappings. Done on `6cbf90ba` in [run 37409898930, job 112103952918](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37409898930/job/112103952918): initial/resized captures were inspected, and selected native pipeline creation completes in 10.90 and 11.55 seconds. See the [bounded acceptance record](../../progress/rendering/browser-advanced-static-integration-2026-10-03.md#software-browser-acceptance-with-proven-modifier-absence). This does not establish numeric material parity, all submission strategies or hardware performance.
- [ ] **UR06.11b2b** `verify` Beyond UR06.11b1 and UR06.11b2a, run native Advanced and authored Default/custom worlds in the implemented CPU-direct, GPU-driven indirect and compute/indirect meshlet modes. Verify each selected mode, original primitive/corner order, authored material/deformation output and explicit unsupported-profile diagnostics. Demonstrate that each zero-readback mode keeps count/visibility data on the GPU with no CPU fallback. The initial Advanced stage counters do not distinguish all draw methods or establish this complete matrix.
- [ ] **UR06.11c** `verify` Capture known-value native PBR/background, masked coverage, texture/sampler/array cohorts, lighting/shadow/probe/decal, depth/AO and selected output/post/exposure results. Exercise disabled effects and resource replacement; shader cooking alone does not establish material interpretation or produced pixels.
- [ ] **UR06.11d** `verify` Exercise each implemented native deformation family from UR06.10e1–e3 through visibility/reconstruction and current/previous motion output in the selected submission modes. Skin/morph acceptance need not wait for displacement implementation. Reuse numeric deformation results from UR06.05b rather than counting the same parity comparison twice.
- [ ] **UR06.11e** `verify` After UR06.10f, render each implemented Advanced MSAA sample profile and verify per-sample visibility/coverage/shading and final resolve, including selected-profile identity and explicit rejection of unsupported forms.
**UR06.11f — Default and custom x4 execution (non-counted summary).** Ordinary, blended and authored custom color profiles have separate evidence. The remaining sidecar and capability-rejection requirements retain the original scope.

- [x] **UR06.11f1** `verify` Render Default CPU-direct x4 with opaque/masked ordinary materials, silhouette coverage, coherent closest-sample depth/normal sidecars, enabled/disabled GTAO, final HDR color resolve, resize, restart and source/profile identity. Done on `e2c47497` in [run 37222890374](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37222890374): two starts per profile and three extents produce twelve captures, 108 known-value centers and zero coverage mismatches. Native shader/pipeline identities stay stable through each resize, required resolve/GTAO operations execute in order, and readback tickets drain. This bounded factory cohort is documented in the [MSAA record](../../progress/rendering/browser-default-generic-msaa-2026-10-04.md).
- [x] **UR06.11f2** `verify` Render Default x4 ordinary materials through GPU-indirect submission with the same silhouette, coherent depth/normal, enabled/disabled GTAO, final resolve, resize and source/profile checks. Verify selected strategy, real indirect work and zero visibility/count readback during ordinary frames, accounting separately for diagnostic pixel copies. Done on `c9139dd6` in [run 37261633781](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37261633781/job/111609908659): two starts per x4 profile and three extents yield twelve ordinary/AO captures, 108 known-value centers and zero coverage/sidecar mismatches. The AO profile executes twenty-four GPU selection/cull pairs, twelve depth/normal and twelve color indexed calls, closest-sample resolve, three GTAO stages and final color resolve. Ordinary intervals add no READ maps; paused AO captures account for twenty-two diagnostic maps. See the [exact GPU acceptance record](../../progress/rendering/browser-default-generic-msaa-2026-10-04.md#gpu-depth-ao-and-blended-execution).
**UR06.11f3 — Blended and custom x4 execution (non-counted summary).** The Default blended and custom color results are complete below. Custom sidecars and unavailable-profile diagnostics remain open; these leaves do not duplicate the opaque/masked and depth/AO cohorts in UR06.11f1/f2.

- [x] **UR06.11f3a** `verify` Qualify Default CPU-direct and GPU-indirect x4 straight-alpha blending, fractional coverage, ordering, color resolve and presentation through two starts and three extents per route. The CPU result on `08822920` in [run 37256129926](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37256129926) and GPU result on `c9139dd6` in [run 37261633781](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37261633781/job/111609908659) each supply six captures with zero blended coverage/composition mismatches. The [blended execution record](../../progress/rendering/browser-default-generic-msaa-2026-10-04.md#cpu-blended-execution-and-remaining-gpu-admission) retains the known-value colors and the GPU follow-on evidence. Exact `48f92bd0` [Linux qualification](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37424561132/job/112147026765) repeats both blended profiles successfully; their final live/retiring resource and logical-byte counts are zero. This does not establish long-duration resize stability or driver-memory budgets.
- [x] **UR06.11f3b** `verify` Render the unrelated authored custom x4 color graph through CPU-direct and GPU-indirect submission, preserving distinct source IDs/settings, four-sample color/depth attachments, authored straight-alpha composition, color resolve and presentation before and after resize. Exact `c9139dd6` [Windows-published custom browser qualification](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37261633781/job/111616155761) supplies eight x4 captures across two fresh contexts. Actual direct and indexed-indirect scene calls match the selected source; all overlap centers are `(83, 128, 77, 255)`, and background/fractional-edge checks pass. See the [authored custom graph record](../../progress/rendering/browser-modular-sample-2026-10-04.md). This color-only graph does not author resolved depth/normal sidecars and does not prove the broader zero-readback contract.
- [ ] **UR06.11f3c** `verify` Qualify an authored custom x4 graph that uses coherent resolved depth/normal sidecars through CPU-direct/GPU-indirect rendering and resize. Verify precise rejection of unavailable sample/format combinations in Default and custom graphs. UR06.11b2b retains the broader submission-mode and zero-visibility/count-readback requirements; UR13.05 and UR15.04 retain wider memory and physical-device acceptance.

**UR06.12 — Offscreen scene capture and impostor rendering (non-counted summary).** Shared UI framebuffer rendering does not establish scene-capture or asynchronous HLOD baking. These requirements preserve the authored pipeline and submission strategy.

- [x] **UR06.12a** `impl` Implement asynchronous, output-owned offscreen scene capture through the ordinary shared renderer frame. Retain isolated capture scene publications, exact accepted target generations, bounded layered float readback, cancellation and teardown. Connect HLOD/impostor baking with atomic completed-result publication and the cooked billboard shader/array-texture path, preserving source assets and desktop behavior. Done 2026-10-04: the exact combined source passes managed WebGPU, native-WASM Browser and Editor builds with zero warnings/errors. Independent production-method checks cover accepted layer/content receipts, cancellation across scheduler replacement, retired-viewport finalization, source topology/material invalidation, generated-resource ownership and rollback. See the [capture boundary record](../../progress/platform/browser-hlod-impostor-capture-boundary-2026-10-03.md); rendered acceptance remains in UR06.12b.
- [ ] **UR06.12b** `verify` Render the implemented offscreen capture and cooked impostor paths in the browser with Default, Advanced and an authored custom pipeline where their concrete operations qualify. Verify requested submission mode, layer/float values, final billboard appearance, capture isolation, asynchronous replacement/cancellation, resize/device replacement and repeated teardown; pending work must retain the previous completed result or original source rendering without publishing a partial atlas.

**Acceptance (U2):** a representative engine world renders in the browser through engine objects and the web tier, with documented differences from desktop.

## UR07 — Jolt Physics In The Browser

Depends on the [Jolt browser proof and default-promotion gates](native-subsystem-project-split-todo.md#jolt-browser-proof-and-default-promotion-gates) and decisions D3 and D4. The desktop Jolt module already exists.

- [x] **UR07.01** `owner` Productize the `joltc` Emscripten archive build, pinned to the runtime pack's Emscripten version. Ship it as the Jolt leaf's `browser-wasm` native asset, with a `.props` file that adds the `NativeFileReference`.
- [x] **UR07.02** `impl` Install the Jolt module in the browser composition with single-threaded job execution and a truthful capability report.
- [ ] **UR07.03** `verify` Compare desktop and browser results on matched scenes. If the approved native supply enables cross-platform determinism (decision D10), match state hashes; otherwise match within documented tolerances.
- [x] **UR07.04** `impl` Fail browser publishing of worlds that require PhysX-only features with the component path and feature name.
- [ ] **UR07.05** `verify` Measure physics step time on the reference devices and set budgets.
- [x] **UR07.06** `impl` Make the Jolt leaf buildable for the browser. `XREngine.Runtime.Physics.Jolt` targets `net10.0-windows7.0` and references `JoltPhysicsSharp` 2.22.0, whose native package has no `browser-wasm` asset. Retarget it to `net10.0`, keep desktop native resolution working, and admit the project and package to the portable policy files.
- [x] **UR07.07** `impl` Add a reviewed native-asset allowance to the portability guard. [PortableRuntime.targets](../../../../Build/Portable/PortableRuntime.targets) rejects every `NativeFileReference`, native copy item, and resolved native runtime asset in a portable project, and the browser host is one. Allow named files per project and runtime identifier with a recorded reason, as the package policy does; do not disable the check. Update the [portable project rules](../../../developer-guides/runtime/portable-projects.md).

**Acceptance:** engine physics components behave the same on desktop and web through Jolt.

## UR08 — Audio In The Browser

- [x] **UR08.01** `impl` Implement `XREngine.Audio.WebAudio` against the audio contracts (sources, listener, spatialization, gain, looping, streaming), reusing `browser-audio.js`. The name follows the existing audio leaves (`XREngine.Audio.OpenAL`, `XREngine.Audio.NAudio`, `XREngine.Audio.SteamAudio`). Engine audio components are unchanged.
- [x] **UR08.02** `impl` Add gesture-driven activation, suspension, and resume. Gate simulation only when a world declares audio as required.
- [ ] **UR08.03** `verify` Validate cooked audio codecs on the browser matrix, including Safari.
- [x] **UR08.04** `impl` Report Steam Audio features as unsupported on web unless a WebAssembly build is separately evaluated and approved.

**Acceptance:** engine audio components play in the browser with the same authored data.

## UR09 — Input, UI, And Text

- [x] **UR09.01** `impl` Implement a browser input leaf that feeds `XREngine.Input` devices from pointer, touch, keyboard, IME, wheel, and Gamepad API events, reusing `browser-input.js`. Player controllers and input mappings are unchanged. Done 2026-10-02: `engine-input.js` adapts the reference event approach into shared device/contact snapshots and the existing viewport/player mapping path, rather than importing the frozen page-owned gameplay controller. Focus/content generations protect IME and DOM edits. See the [input record](../../progress/platform/browser-ui-clip-input-2026-10-02.md); device/IME and full accessibility acceptance remain open.
- [x] **UR09.02** `impl` Render engine UI through WebGPU with hit testing in the same coordinate convention as rendering. Done 2026-10-03: shared batches support screen UI and owned linear premultiplied offscreen canvases, composed through camera/world surfaces. Hit testing and spatial DOM bounds reuse the displayed canvas placement; clipped controls preserve local/cross-canvas ownership. Retained sRGB views decode before filtering; the versioned UI image carrier preserves sampler/import settings and per-role encoding without changing raw texture, authored YAML or lit carrier bytes. Native Browser/Editor builds, four production shader cooks and managed/JS ownership/ABI witnesses pass. Unsupported direct/unbatched canvas forms retain named diagnostics. Known-value UI rendering remains in UR05.07 and live editing/IME/accessibility acceptance in UR09.06. See the [UI implementation record](../../progress/platform/browser-ui-clip-input-2026-10-02.md).
- [x] **UR09.03** `impl` Cook glyph atlases with FreeType at cook time and render cooked fonts at runtime. Desktop shipping builds may use the same path.
- [x] **UR09.04** `impl` Bridge text entry, IME, and accessibility to DOM elements. Done 2026-10-03: bounded shared-control projection supplies DOM button, checkbox and textbox semantics; clipped geometry, local canvas ownership, cross-canvas focus, native editing/IME and generation-based stale-event rejection share engine state. The native-WASM build and independent lifetime review pass. See the [implementation record](../../progress/platform/browser-quality-accessibility-2026-10-03.md); live acceptance is owned by UR09.06.
- [x] **UR09.05** `impl` Express mobile touch controls (virtual sticks, buttons) as engine input mappings and UI, not page-specific script.
- [ ] **UR09.06** `verify` Exercise browser Tab/Shift+Tab traversal, activation, readonly/multiline editing, selection, IME composition and assistive-technology names/roles/states. Verify clipped/hidden controls, cross-canvas focus, reordered and removed/recreated controls, interrupted editing and world teardown against the shared engine state.

**Acceptance:** a user can play with touch, keyboard/mouse, or gamepad through the engine's own input and UI.

## UR10 — Game Code And Components

Depends on UR17: game code reaches the engine through the `Engine` facade.

- [x] **UR10.01** `impl` Make project templates and game projects target `net10.0` and reference only portable engine assemblies (plus leaf contracts where needed). The generated target framework is `net10.0-windows7.0` today, set in `XREngine.Editor/CodeManager.cs` and `XREngine.Editor/EditorProjectInitializer.cs`.
- [x] **UR10.02** `impl` Report at build time which game-assembly references block browser publishing (desktop-only leaves or APIs), with type and member names. Done 2026-10-02: exact built/loaded module identity gates precede world cooking and browser publication; bounded metadata diagnostics name direct desktop leaves, nested/generic members, native imports and available browser-platform annotations. The [audit record](../../progress/platform/browser-game-assembly-audit-2026-10-02.md) keeps unresolved third-party declarations and dynamic/transitive behavior outside static compatibility claims.
- [x] **UR10.03** `impl` Link the project's game assemblies into the browser publish, with generated static registrations. Editor hot reload remains desktop-only. Done 2026-10-02: the complete production publisher generated the typed game bootstrap/static registrations and activated a browser bundle containing the canonical game WebCIL and bootstrap. See the [publisher evidence](../../progress/rendering/browser-project-publishing.md#same-assets-and-game-code); runtime gameplay acceptance remains separate.
- [x] **UR10.04** `impl` Keep editor-only code out of game builds, as today.
- [x] **UR10.05** `verify` Load serialized game components, game modes, and pawns in the browser from the same assets as desktop. Done 2026-10-02: the real Editor-published RenderingParity asset at `7ba776cf` renders and responds through its authored mode/pawn/animation components in physical Edge/Intel Arc, including two fresh starts. Production native YAML/cooked standalone checks establish exact saved mode/controller/pawn/camera identities. The [startup record](../../investigations/platform/browser-authored-mode-startup-2026-10-02.md) distinguishes this shared-asset load evidence from the separate desktop pixel comparison.

The following items record RollingBall's portability work under decision D2. Its starting point targeted `net10.0-windows7.0`, referenced `XREngine.Runtime.Bootstrap`, ran on PhysX (`XREngine.Scene.Physics.Physx`), used VR components and the OpenVR action manifest, and shipped its own cooked-world serializer. The checked rows describe the implemented shared-runtime replacement.

- [x] **UR10.06** `impl` Move the sample's PhysX-specific calls onto the backend-neutral physics contracts so it runs on Jolt. Otherwise UR07.04 rejects it at publish.
- [x] **UR10.07** `impl` Separate the sample's VR rig, OpenVR manifest, and startup-settings generation from its gameplay code, so the gameplay assembly references only portable projects and the desktop build adds the VR part.
- [x] **UR10.08** `impl` Bring the sample's cooked-world serializer under the platform cook target from UR03.03 so desktop and web load the same world asset. This corrects the status of source already published at `2ce09a0d`: RollingBall registers the same explicit v5/v6 serializer/dependencies for both hosts, and the platform publisher uses that registered RuntimeBinaryV1 route before generic cooking. Existing v5 bytes remain unchanged; v6 carries the explicit audio requirement.

**Acceptance:** the same compiled game code runs on desktop and in the browser.

## UR11 — Editor Browser Publishing On The Unified Path

- [x] **UR11.01** `impl` Replace `BrowserWorldPublishExporter` with this flow. Done 2026-10-02: the compiled production `BuildCurrentProjectSynchronously` chain completed all steps below and activated the canonical RollingBall bundle atomically through a portable Linux invocation. This is complete publisher execution, not Windows CLI or gameplay acceptance; see the [publisher evidence](../../progress/rendering/browser-project-publishing.md#same-assets-and-game-code).
  1. Cook the startup world's web closure.
  2. Build the game assemblies.
  3. Publish the browser host with the engine assemblies, game assemblies, and selected leaves.
  4. Write the launch descriptor.
  5. Activate the output atomically, reusing the branch's staging and rollback.
- [x] **UR11.02** `impl` Persist the bounded authored-world capability audits before publication, with scene/node/component/material/pass paths and reasons. Required findings block activation; optional findings remain listed; an incomplete report survives interrupted preflight. Genuine Editor Prepare/Export yields zero required/two optional findings for unchanged RollingBall and three required/two optional findings for the copied negative world. See the [report contract and evidence](../../progress/platform/browser-capability-report-2026-10-03.md). Additional native cold eligibility is owned only by UR06.02c; arbitrary runtime-created behavior is not statically certified.
- [x] **UR11.03** `impl` Ship a player shell page (canvas, loading progress, errors, audio unlock) separate from the developer harness page.
- [ ] **UR11.04** `impl` Keep the CLI entry point (`--build-project <project> --build-platform BrowserWebGPU`) and the editor Build Project action stable. Browser publishing must also work from a packaged editor, not only a source checkout.
- [ ] **UR11.05** `verify` Publish the parity target and a lit, textured, animated test world; play them in the browser and compare with desktop.

**Acceptance (U3):** a published project plays in the browser from the editor's normal build action.

## UR12 — Networked Browser Client

- [x] **UR12.01** `impl` Implement a WebSocket transport leaf over the transport contract (`INetworkTransportBackend` and `IDatagramTransport`, already separated from the desktop socket implementation in `XREngine.Runtime.Net.Sockets`), plus the matching server gateway.
- [x] **UR12.02** `impl` Carry over the mobile TODO's MW11 requirements: bounded queues, backpressure, reconnect/resync, suspension, authentication, origin and credential policy, and optional voice.
- [ ] **UR12.03** `verify` Validate against the real server path under throttling, disconnects, and app switching.

**Acceptance (U5):** the browser client joins and plays through the production server path.

## UR13 — Performance, Runtime Mode, And Size

- [ ] **UR13.01** `verify` Measure the interpreter with representative worlds (CPU per frame, startup, memory) early. Do not wait until feature completion.
- [ ] **UR13.02** `owner` Qualify AOT: generated serialization and registration metadata, trimming roots, build time, download size, runtime cost. Choose the shipping mode from measurements (decision D11).
- [ ] **UR13.03** `impl` Reduce download size through trimming, lazy assembly loading, and streamed content, against the [readiness budgets](../../progress/rendering/mobile-browser-readiness.md#devices-and-measurable-budgets). The [download-size boundary](../../progress/platform/browser-download-size-boundary-2026-10-04.md) records the current 25.46 MiB gzip framework build-resource subtotal, implemented scene streaming and deferred scene shader delivery. Genuine authored export and production-loader checks establish bounded shader deferral; managed-assembly loading, trimming, measured published transfer and browser attachment remain open.
- [ ] **UR13.04** `verify` Demonstrate steady-state frames with no recurring managed allocations in simulation, visibility, recording, and submission; record unavoidable browser/API allocations separately.
- [ ] **UR13.05** `verify` Bound linear-memory use across the .NET heap, native Jolt heap, staging, and retained content; test repeated world load and unload. The [UI ownership record](../../progress/rendering/browser-ui-lifetime-2026-10-03.md) and [point-light record](../../progress/rendering/browser-point-shadow-restoration-2026-10-03.md) document exact managed-object teardown and reuse checks. Those bounded probes do not close browser heap, staging or in-flight GPU lifetime acceptance.

## UR14 — Lifecycle And Recovery

- [ ] **UR14.01** `verify` Carry over the mobile TODO's MW10 recovery and lifecycle matrix to the unified runtime: device loss and reconstruction, hide/show, lock/unlock, orientation during loading, canvas removal, repeated load/unload, and explicit restart.
- [x] **UR14.02** `impl` Reject stale asynchronous completions after teardown or device replacement.

## UR15 — CI, Hosting, And Evidence

- [x] **UR15.01** `impl` Extend the browser compile lane into a build and publish lane next to the Windows desktop lane. [portable-browser-compile.yml](../../../../.github/workflows/portable-browser-compile.yml) already runs [Test-PortableBrowserCompile.ps1](../../../../Tools/Test-PortableBrowserCompile.ps1) on `ubuntu-latest`; the directory casing prerequisite is fixed, and the lane uses the SDK pin from D5. Exact-commit results are recorded above.
- [x] **UR15.02** `owner` Propose a headless-browser smoke harness (decision D9) that boots a cooked world and checks startup, rendering, and diagnostics.
- [x] **UR15.03** `impl` Document production hosting: HTTPS, MIME types, compression, immutable caching, bootstrap revalidation, and CSP. Add cross-origin isolation only if threads are adopted.
- [ ] **UR15.04** `verify` Run the physical-device matrix from the mobile TODO (MW00.06, MW12.07) with the evidence template in its completion section.
- [ ] **UR15.05** `impl` Publish user-facing build, publish, hosting, support-matrix, and troubleshooting docs after validation.

**Acceptance (U4, together with UR13, UR14, and UR16):** reproducible clean publish and recorded device evidence.

## UR16 — Retire The Separate Browser Runtime

After U3, subject to decision D12:

- [ ] **UR16.01** `impl` Remove `BrowserMeshComponent`, `BrowserSpinComponent`, `SceneBootComponent`, and the browser registration manifest and generator.
- [ ] **UR16.02** `impl` Remove `BrowserCooked*` scene and instance DTOs and the `BrowserSceneSession` content, motion, collision, and animation paths.
- [ ] **UR16.03** `impl` Remove `BrowserRenderPipeline`, its packet types, and `browser-render-pipeline.js` once the engine pipeline covers their cases.
- [ ] **UR16.04** `impl` Remove `BrowserCpuAnimator`, `BrowserCookedAnimationPlayer`, and `BrowserKinematicCharacter`.
- [ ] **UR16.05** `impl` Remove `BrowserWorldPublishExporter`, `Tools/BrowserContentCooker`'s browser-only recipe format (keeping the generalized packager), and the Python scripts (`Tools/Generate-BrowserRegistrations.py`, `Tools/Reports/audit_browser_dependencies.py`, `Tools/Shaders/cook_browser_shaders.py`).
- [ ] **UR16.06** `impl` Keep the developer harness page only if it still exercises the unified runtime; otherwise remove it.
- [ ] **UR16.07** `impl` Close or rewrite superseded mobile TODO rows and progress docs; move durable content into stable docs.

## Mobile TODO Carry-Over

| Mobile TODO item | Disposition on the unified path |
| --- | --- |
| MW00 (scope, budgets, devices) | Still applies; budgets and device matrix are reused. |
| MW01 (same-identity portable profiles) | Superseded by the implemented whole-project layout; remaining qualification is in the native subsystem integration checklist and UR01. |
| MW02 (browser host) | Host contracts and canvas code carry into UR02. |
| MW03 (batched bridge) | Carries into UR04; its acceptance evidence still applies. |
| MW04 (WGSL artifacts) | Artifact format and Slang route carry into UR05; the separate browser material generator is superseded. |
| MW05 (WebGPU resources and submission) | Carries into UR04; acceptance still applies. |
| MW06 (focused browser pipeline) | Superseded by UR06; the branch pipeline is reference only. |
| MW07 (cooked content) | Delivery code carries into UR03; browser-only DTOs are superseded. |
| MW08 (interaction and services) | JavaScript services carry into UR08 and UR09; bounded animation and collision profiles are superseded by engine components. |
| MW09 (compute/indirect) | WGSL kernels become engine pipeline ports in UR06. |
| MW10 (recovery, budgets) | Carries into UR13 and UR14. |
| MW11 (multiplayer) | Carries into UR12. |
| MW12 (publish, hosting, validation) | Carries into UR11 and UR15. |
| MW-D01–MW-D06 (deferred) | Still deferred. |

## Deferred

- **Browser-hosted editor:** a separate design after the runtime ships. It needs ImGui for WebAssembly with a WebGPU backend, a compilation service, server-side import, and a virtual file system.
- **WebXR:** a separate presentation and input leaf.
- **WebGL2:** a separate backend under the [browser renderer design](../../design/rendering/browser-wasm-renderer-design.md).
- **Multithreaded browser runtime:** after .NET WebAssembly threading with native relinking is reliable and measurements justify it.
- **PWA/offline packaging and native mobile applications.**

## Validation Matrix

| Area | Required evidence |
| --- | --- |
| Build | The build gate after every workstream; clean publish |
| Boot | Engine world lifecycle in the browser through the portable engine host; absent desktop services; named failures for missing required services |
| Rendering | OpenGL/Vulkan/WebGPU captures of the same worlds; orientation, depth, linear/sRGB, alpha, transparency, shadows, tonemapping; resize and device loss |
| Content | Same world assets; cooked variant selection; cold/warm cache; failure and cancellation cases |
| Physics | Desktop/browser Jolt parity on matched scenes; PhysX-only rejection |
| Audio/input/UI | Gesture activation, spatial audio, touch/keyboard/gamepad, IME, UI hit testing |
| Game code | Same game assemblies on desktop and web; blocked-reference report |
| Performance | Interpreter/AOT comparison; frame-time percentiles; allocations; download size; memory peaks on reference devices |
| Desktop preservation | OpenGL/Vulkan editor smokes and targeted tests after each shared-contract change |

## Definition Of Done

- The browser runs the engine's own worlds, components, renderer, pipelines, physics, audio, input, and UI from the same assemblies as desktop, through the same engine facade and host services.
- Editor browser publishing produces a playable site from the project's real assets and game code, with named reports for unsupported features.
- Only platform leaves and cooked GPU/format variants differ between desktop and browser.
- The separate browser runtime is removed, and superseded mobile TODO rows are closed.
- Device, performance, recovery, hosting, and CI evidence is recorded.
