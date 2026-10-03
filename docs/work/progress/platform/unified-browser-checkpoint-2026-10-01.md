# Unified browser implementation checkpoint

Updated: 2026-10-02. This is a partial implementation checkpoint, not completed
desktop/browser parity qualification. The sections below preserve the sequence
of implementation and runtime findings; later exact-commit evidence supersedes
earlier limitations.

## Current checkpoint

The reviewed authored-runtime/metadata/readback/shadow implementation passes the integrated
Editor, Server, VRClient, desktop WebGPU, nineteen portable compile rows and
fresh interpreter/native-Jolt browser publication with zero compiler warnings
or errors. **57 of 110 named checklist items are complete.** The preceding
published implementation at `0078867c7327e2a50605a655207854319045e2bb` has now
passed genuine Windows Editor CLI publication and exact-bundle RollingBall
Chromium qualification in
[run 37024792567](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37024792567).
Its inspected pause/resume and both resized captures show real game/HUD output;
the prior border-only false positive is absent. All three jobs for this exact
commit are now successful, including the baseline browser/audio checks.

The preceding exact-bundle run already displays the real RollingBall course,
ball, obstacles and authored HUD, including the mapped pause/resume transition.
Inspection also exposed a blank resize capture despite a green check. The
current source corrects output-generation readiness and requires real interior
game/HUD pixels; both stricter resize cases now pass with inspected game output. See
[the evidence correction](#game-input-acceptance-and-resize-evidence-correction-2026-10-02).

Current source also includes lit textured surfaces, bounded sky/local-shadow/
post-process profiles, GPU deformation, engine UI and authored font cooking,
shared touch controls, composed audio activation and the browser platform leaf.
These implementations have different acceptance boundaries; compiled source
does not imply that every profile has rendered correctly. Full textured and
animated authored-world play, audible-device audio acceptance, broader recovery/networking,
allocation/size budgets, desktop capture parity and physical-device acceptance
remain open. The frozen reference runtime remains until its parity gate is met.

## Initial implemented and compiled snapshot (2026-10-01)

- The browser composes the shared `Engine`, `RuntimeWorld`, registrations,
  caller-thread frame loop, asset catalog, Jolt leaf, Web Audio leaf, input
  viewport, and WebSocket transport instead of a second gameplay runtime
- The caller-thread scheduler reuses the engine's fixed/update/visibility/swap/
  render callbacks. A production-assembly CPU smoke completed 2,048 warmed
  empty frames with zero managed caller-thread allocations; this is not a
  rendered game allocation measurement
- Runtime assets use the shared serializers, hash-verified catalog entries,
  bounded reads, stale-completion rejection, and explicit teardown ownership
- The WebGPU renderer now adapts engine buffers, programs, materials, meshes,
  and batched frame commands. The implemented raster shader group is the
  explicitly authored depth/depth-probe diagnostic
- `RollingBall` gameplay is portable, with desktop VR composition in
  `RollingBall.DesktopVR`. The desktop Jolt/PhysX default is unchanged
- The editor publisher packages canonical engine assets and statically linked
  game code, with named capability failures and staged output activation
- The server gateway and browser transport share the versioned realtime
  protocol; the quaternion/wire compatibility break is intentional for this
  undeployed application

Browser interpreter/native-Jolt publishing, Editor, Server, VRClient, and the
RollingBall desktop host have successful local build logs. The shader cooker
compiled both engine depth recipes with pinned Slang 2026.8 and validated their
explicit ABI. The codec budget smoke exercised 21 valid/invalid payload cases.
These focused checks do not replace the full build/test/device acceptance matrix.

The resumed current-tree check built all 18 portable projects and completed a
fresh browser publish. Publishing emitted zero warnings/errors. The compile
sweep emitted no compiler warnings/errors, but retained `NU1900` package-audit
cache warnings caused by this host's read-only home. The host also requires the
supported in-process MSBuild task override because its separate task-host
Unix sockets are unavailable. CI uses the ordinary toolchain path and must
independently qualify clean restore, build, and publish.

## Initial depth-only limitations (historical)

At the initial depth-only checkpoint, the browser player advanced the real world but reported that rendering
is unavailable. `DefaultRenderPipeline` rejects WebGPU output until the required
forward lighting, attachment, material, and tonemap routes exist. Engine texture
and framebuffer wrappers, lit material generation, the remaining raster shader
groups, shadows/environment/post processing, engine UI/text, and rendered sample
parity are not implemented by the depth diagnostic.

The separate browser reference runtime remains frozen and present. Its removal
is gated on genuine published-project parity. Physical-device budgets, desktop
render preservation, AOT measurements, tolerance-based cross-platform physics,
full recovery, and production networking qualification remain open.

## Browser CI history

The browser workflow builds the pinned managed/native Jolt supply, compiles the
portable projects, publishes the host and native diagnostic, cooks the engine
depth shaders, and runs the Playwright harness. The harness checks actual depth
pixels, two real engine-world lifecycle iterations, fetched asset lifetime, and
native physics startup/teardown. It records screenshots, adapter details,
console output, and check outcomes.

CI deliberately selects software WebGPU and labels its evidence as API/shader
correctness only. It does not claim hardware performance or full sample parity.
Local Chromium execution is blocked by the current host's Unix-socket policy;
no local browser execution pass is claimed. The CI result must be attached to
the exact published commit before accepting its live checks.

The first real Chromium run for commit
`36c4bde1b7575c6b9887d61aab1b9e27c0c1bf09`
([Actions run 36912281747](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36912281747))
completed clean build/publish, Slang cooking, and local delivery. Its production
engine-world check completed two start/stop cycles with twelve caller-thread
frames each; the asset delivery/lifetime check also passed. This is a minimal
world with one scene/root transform, not RollingBall gameplay acceptance.

The same run failed the engine depth diagnostic at `Engine` static
initialization and the standalone Jolt query diagnostic in the Mono runtime.
No rendered pixel or full native-query acceptance is claimed. The bare runtime
bundle has no authored launch descriptor, so published-player auto-start was
explicitly skipped. The failures are being repaired using the preserved console
logs, screenshots, and exact published WebAssembly module.

The Jolt repair preserves typed managed APIs while moving callback import
parameters to pointer-sized ABI tokens supported by the interpreter. Its exact
published module now completes sixteen native world lifecycles, 1,920 steps,
two foundation initialization/shutdown passes, contact callbacks, raycast hits,
and disposal checks under Node. This is native WASM evidence; Chromium remains
the browser gate. The correction is published as
`2fc658ca641aad91fd419268bdabb77a91bb2352`.

The engine initializer failure was a missing catalog in diagnostic composition:
settings tracking reached the asset manager before a browser asset source was
installed. The corrected diagnostic uses the same genuine fetch-backed catalog
as production before first engine access. Published WASM checks now pass repeated
create/stop, overlapping-start supersession, cancellation, and restart. Node's
content fetch was mapped to the actual cooked files for this managed-boot check;
it is not browser transport or rendering evidence.

The resource/canvas slice adds exact-format engine texture/framebuffer
wrappers and a real-world canvas owner. It remains unqualified by that run.
The production pipeline gate stays closed until its lighting, materials,
attachments, and presentation route are genuinely available.

An explicit built-in material semantic and immutable exact-hash variant catalog
also compile. They do not yet make desktop-GLSL material factories browser-ready;
target-aware construction and real lit shader variants remain required. The
RollingBall binary format is unchanged.

The combined checkpoint `fe3a11974100a7946b6d9f768eb28646dadc1621`
([Actions run 36918408996](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36918408996))
completed clean Linux build/publish, all 18 portable compile rows, native Jolt
build/publish, Slang cooking, and canonical asset packaging. Chromium passed
the two real-world start/stop cycles, asset delivery/lifetime, native Jolt
startup/queries/callbacks/teardown, and local delivery. This is the first
Chromium acceptance of the repaired native callback ABI. No worker threads
were created by the Jolt check.

The depth render remained the only failed check. It reached the shared frame
loop, where GPU-scene mirroring eagerly generated its LOD-transition buffer
before the buffer's construction was published. The cache publication guard
correctly rejected it. A focused repair removes the premature generation and
leaves native allocation to the existing backend-binding lifecycle; the guard
is unchanged. The same frame path also eagerly constructed an unused indirect
manager and loaded desktop GLSL for CpuDirect submission; that manager is now
created at its actual GPU-use boundary. The static diagnostic now steps the
real caller-thread timer and standard viewport collection/swap callbacks,
preserving output and visibility generations rather than inventing them.

The repaired published WebAssembly reaches clear, shader-module, vertex/index
buffer, pipeline, and mesh-command imports in two start/stop cycles under Node.
Eight frames per cycle complete without a publication, asset catalog, pipeline,
or generation-decline error. The fixture's orthographic camera now explicitly
uses the centered origin its three test triangles require. Both cycles prepare
and submit all three draw commands by frame three. This is boundary evidence
only: the Node imports do not execute WebGPU. Actual depth pixels, production
lit rendering, and RollingBall browser play remain unqualified.

The run used Google SwiftShader software WebGPU. Its screenshots, console,
adapter metadata, and smoke outcomes are in the
[qualification artifact](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36918408996/artifacts/11190823487).
The bare host's published-player auto-start remains explicitly skipped until
an authored startup-world descriptor is supplied. Physical hardware budgets
and the broader recovery matrix remain open.

The depth repair was published as `52a0a0331b5f8ec139e67a1444b1249b3ece3763`
([Actions run 36924823536](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36924823536)).
Build, publish, shader cook, world lifecycle, asset lifetime, Jolt, and delivery
checks passed again. Actual Chromium depth rendering failed after approximately
three seconds with a destroyed-device notification. The console artifact did
not retain an earlier GPU error, so the original cause remains under diagnosis;
the Node boundary result does not establish GPU execution. The
[qualification artifact](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36924823536/artifacts/11194011923)
preserves the failure screenshot and smoke outcomes.

The next source slice adds serializer-owned external dependency declarations
and recursive canonical cooking, plus hash-owned material-variant metadata
from cooker to browser catalog. Local Editor/Rendering/Browser/cooker builds
and focused runtime probes pass. Built-in lit-color construction now retains
the shared semantic/parameters without trying to load desktop GLSL in the
browser. The actual lit shaders and render-profile integration remain required.

Browser input now binds its snapshot source to the player's real `XRViewport`
instead of substituting an input-only object for rendered sessions. Focus,
capture lifetime and CSS-to-backing-pixel conversion have focused probe
evidence; rendered UI/hit-testing acceptance remains open.

See the [browser smoke instructions](../../../../Tools/BrowserSmoke/README.md),
[shader integration record](../rendering/unified-webgpu-shader-cooking.md), and
[caller-thread validation](caller-thread-frame-stepping.md) for reproducible
commands and detailed boundaries.

## Canonical game lifecycle and browser qualification updates

The authored `RollingBallWorld.asset`, its version-5 cooked world serializer, and
statically linked portable gameplay assembly now run through the real browser
`RuntimeWorld`/Jolt lifecycle under published .NET WebAssembly. Repeated headless
and canvas-composed runs each execute 120 warm frames and 600 measured frames,
with 600 variable and 1,200 fixed callbacks per measured cycle. The canvas run
in this local probe does not initialize WebGPU and is not a pixel, playable
browser, or device-performance acceptance claim.

The runtime proof exposed and repaired browser-native body ABI mismatches and
native lifetime defects. Physics initialization now precedes scene attachment;
settings are applied after initialization. Five canonical cycles release all
native bodies, clear component body links, invalidate retained actor IDs, and
release the physics system. Removing and re-adding an actor preserves its ID;
destroying a detached actor releases it and rejects subsequent re-add. Exact
browser-only patch hashes and native API evidence are recorded in the
[Jolt supply record](../../design/platform/jolt-browser-native-supply.md).

Repeated game runs also exposed strong-registry retention. Source-owned cooked
allocation batches and explicit session ownership now release all authored
nodes, components, and worlds after each of five headless and canvas-composed
cycles. Failed manifest loading and a separately injected failure after world
hydration both permit a later successful retry. Smaller session-container and
lazy default-resource ownership defects remain under repair and shared-runtime
review; these observations do not yet establish leak-free lifecycle acceptance.

The final reviewed ownership slice makes the headless registry flat at 35 objects
after each of five cycles. Canvas-composed teardown still retains eight objects
per cycle, traced to a 128-byte LOD-transition buffer and a generated empty
fallback material with their owned containers. Forced-GC heap growth remains
unqualified. A separate live probe injects a component failure during actual
`OnBeginPlay`, after native world activation: startup reports the intended error,
rollback leaves zero authored graph objects, and an immediate clean retry plays
and stops successfully. Ownership commits may omit fully destroyed temporary
allocations; ordinary construction publication remains strict, and destruction
exceptions abort the owning transaction even if the caller catches them.

[Run 36928282822](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36928282822)
retained the first GPU failure: the external Dawn Instance reference ceased to
exist before explicit renderer destruction, with no preceding validation error.
The subsequent diagnostic checkpoint `e8ef0ceafac6196b0c237bfbcaac9e73c0c01370`
([run 36931696715](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36931696715))
proved that a separate raw WebGPU page also failed on its first canvas clear,
before any engine, managed runtime, or shader pipeline. Native Chromium stderr
reported a missing shared-image backing factory; its GPU process did not crash.
This narrows the failure to the runner's canvas presentation configuration.

Checkpoint `e787fa4dddad043e5f0403fbe4eef357b9bbdb45`
([run 36933343571](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36933343571))
adds one explicit software-mode Vulkan feature switch, preserving native mode,
validation, and depth assertions. The run completed successfully. Every sampled
5×5 engine pixel patch exactly matched its expected bytes: left `(64,0,191)`,
right `(191,0,64)`, and background `(13,13,13)`. It recorded 30 real engine mesh
draws, 16 submissions, zero reference-scene packets, and no focused reference
pipeline. The raw clear, triangle, and identical cooked-WGSL checks also passed;
there were no device losses or browser console errors. CDP changed from
GaneshGL to GaneshVulkan while retaining SwiftShader, with no GPU-process crash.
This is software-WebGPU correctness evidence for the admitted depth profile,
not the complete production render pipeline or physical hardware qualification.

The same checkpoint implements bounded Web Audio PCM queues, ordered processed
buffer retrieval, clock-based scheduling, pause/resume, stop/rewind, rate changes,
and source/context teardown. Local schedule/ownership probes and actual .NET
WASM memory-view queue/unqueue calls pass; the leaf build has zero warnings and
errors. The same CI run rendered known PCM samples with a real
`OfflineAudioContext` at both playback rates with maximum sample error zero.
Audible output, gesture activation, spatialization, codecs, and device budgets
remain separate acceptance. Whole-stream automatic looping is explicitly
unsupported; static looping remains supported. See the
[audio leaf contract](../../../../XREngine.Audio.WebAudio/README.md).

## Reviewed lifetime repair and lit renderer continuation

The canvas-composed ownership leak is now repaired at the actual owners:
`GPUScene` releases its lazy LOD-transition buffer and pending state, and each
render-pipeline asset retains one fallback material with ownership limited to its
factory allocations. Shared shader-cache imports have an independent publication
boundary so destroying a generated fallback cannot destroy a cached shader.
Borrowed custom fallback materials remain with their original owners.

Early scene activation exposed a separate rollback hole: a root was world-bound
before the root collection tracked it. Attachment and visible-scene registration
are now transactional. An intentional activation failure preserves the original
exception, clears root/component world links and bookkeeping, and permits a
successful retry on the same world followed by clean graph destruction. The
probe also covers a preceding successfully attached root in the same scene.

Freshly published .NET WebAssembly executes the canonical RollingBall game through
five headless cycles and five canvas-composed cycles. Both routes finish every
cycle with exactly the same 35 registered objects and identical registered IDs,
zero authored graph survivors, no remaining native actors, cleared component body
links and a released physics system. Each cycle executes 120 warm frames and 600
measured frames, with exactly 600 variable and 1,200 fixed callbacks. The canvas
probe deliberately leaves graphics uninitialized; it does not establish pixels,
playable browser input, forced-GC heap budgets or physical-device performance.

The next renderer slice implements exact-hash built-in lit material selection,
bounded engine directional/point/spot light publication, RGBA16F attachments and
the shared default pipeline's HDR-to-Mobius output route. Actual Slang cooking and
narrow managed builds pass. The [shader record](../rendering/unified-webgpu-shader-cooking.md#engine-lit-forward-and-hdr-output)
lists the supported cohort and explicit rejection boundaries. The corresponding
live browser pixel qualification is still pending; full shadows, UI and rendered
RollingBall acceptance remain open.

The renderer slice was then published as
`047bb7f1126f9fa6272325ace84446b3c9f7e1b9` and passed
[run 36947665282](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36947665282).
Real Chromium/SwiftShader verifies fourteen lit material/light/HDR/tonemap cases
and five resizes, with 30 live GPU resources after every case/resize, zero retiring
resources after drain, and no browser console errors. Known HDR samples preserve
values above one and authored opacity; inspected captures and exact values are
recorded in the [lit shader acceptance](../rendering/unified-webgpu-shader-cooking.md#lit-profile-live-acceptance).
Full shadows, engine UI and rendered RollingBall remain the next required work.

The subsequent standalone directional-shadow implementation and restart repair
are qualified at `3d18468ebdf1115b431743c56ce0f345d8c3235d` by
[run 36954020671](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36954020671).
Actual Chromium executes the shared shadow viewport, PCSS receiver and HDR output
for moving casters/lights, near/far penumbra, disabled/re-enabled shadows, 256/512
map resize, repeated resource retirement and session restart. The
[shader record](../rendering/unified-webgpu-shader-cooking.md#standalone-directional-shadow-integration)
preserves exact pixels, the first failed restart check, and the final complete
evidence. This does not qualify cascades, atlases, spot/point-light shadows,
contact shadows, UI, or the rendered canonical game. Shared DebugDraw primitives,
the game's authored GTAO/bloom and the full project publish/play route remain open.

The following shared-debug slice now passes real Chromium at exact commit
`a5762484b8c763fa59f8edb6000d0b61a455bf00`:
real registered callbacks feed the engine point/line/triangle visualizer, whose
cooked vertex shaders consume ordered frame-packet storage snapshots. Zero to
1,024-instance cohorts, capacity growth, same-count mutations, alpha blending,
resize and restart pass, with 68 stable live resources and zero retiring after
warmup. See [the exact run](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36961007160).
A fresh Editor build has zero warnings/errors, and the complete compiled
`BuildCurrentProjectSynchronously` chain has executed game compilation, canonical
world cook, actual browser publication, content packaging, player-shell setup
and atomic activation. Its bundle contains 32 verified assets, fifteen shader
artifacts, six material variants and nine pipeline mappings. This supersedes
the earlier method-only cook probe. The portable Linux invocation is not the
Windows Editor CLI process or rendered browser-game acceptance. The complete
publisher and static game-linking evidence closes two implementation rows,
bringing the checklist to 41 checked of 110, with 69 open. See the
[publisher record](../rendering/browser-project-publishing.md) and
[debug overlay record](../rendering/unified-webgpu-shader-cooking.md#shared-debug-primitive-overlays).

## Bounded native and WebAssembly physics comparison

On 2026-10-02, the unchanged desktop Linux Jolt packages and the reviewed browser
Jolt build ran the same shared ball-and-tilting-course probe for 240 fixed steps
at 120 Hz. Eighteen paired checkpoints across two independent WASM cycles matched
all sampled position, linear/angular velocity, contact-added/contact-persisted
counts and ray-hit/body/fraction values. First contact occurred on step 35 in
both runners; equivalent quaternion-angle noise was at most 4.3e-8 radians.
The tolerance specification preceded both traces. No fallback solver or desktop
physics-default change was involved.

The real cooked RollingBall world remained active around the browser measurement,
but the measured scene was an independently owned physics probe rather than the
canonical game's actors. Consequently this is bounded backend compatibility
evidence, not full-game parity, universal deterministic replay, Chromium gameplay
input, or hardware performance. The shared source, predeclared tolerances,
native/WASM supply hashes, traces and commands are retained under
`Build/_AgentValidation/20261001-225000-lit-surface/physics-parity/`.

## Compiled shared rendering and networking group (2026-10-02)

The current coherent source group passes Release builds of the desktop WebGPU
closure, Editor, Server and VRClient; all eighteen portable browser compile rows;
and a fresh browser interpreter/Jolt publish, with zero compiler warnings/errors.
ShaderCooker and BrowserContentCooker also build cleanly. Twenty-four raster
recipes, including the new explicit V2 alpha coverage and bitmap UI variants,
and the canonical packed deformation kernel pass cooking. The sample catalog
retains twenty-two hash-verified artifacts, twelve material variants, nine
pipeline passes and one lazy compute kernel.

Implementation now includes numeric shared compute dispatch, canonical GPU
skinning/morph lowering, cube/array textures, explicit lit masked/sorted coverage,
production browser WebSocket composition, conservative opt-in shared world
identity, and authored Jolt-unsupported feature rejection at cook time. Reviews
repaired native buffer disposal, producer ordering, cached-range retirement,
value-only auxiliary lifetime, stale networking callbacks and cancellation races,
and native package reference/snapshot admission. The TODO closes only four full
implementation rows and now reads **45/110**, leaving live acceptance explicit.

The latest published pre-group commit `b78c8101259d6707e2677a4356a63c40b0f42901`
passes its Linux browser job in
[run 36972866407](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36972866407).
The Windows job reached the real Editor compile and exposed missing pinned OSC
and OpenVR submodules in the clean runner. The reviewed workflow fix initializes
only those existing gitlinks and verifies their exact recorded commits. No
submodule revision, dependency supply or desktop/HMD validation scope changed.
Actual Editor-published RollingBall pixels/gameplay and the newly implemented
profiles remain unqualified until the next end-to-end run completes.

## Sky, local shadows and authored fonts (2026-10-02)

The subsequent integrated source group adds five exact built-in sky modes,
RGBA16F sky texture uploads, bounded standalone point/spot shadows alongside the
directional map, and project-authored FreeType bitmap-font cooking with explicit
license notices and preactivation font binding. Desktop shader behavior is
preserved. All 35 raster recipes plus the canonical compute recipe cook; the
sample's production catalog contains 33 artifacts, 23 material variants, nine
pipeline passes and one compute kernel. Editor and native-Jolt Browser Release
builds pass with zero warnings/errors. Visual acceptance of these new profiles
remains open; this source progress does not change the 45/110 checked count.

Published commit `766b5e87468001be3e41bdd165696cc9048fbb54` passes the complete
Linux browser job in
[run 36977206462](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36977206462).
The real Windows Editor CLI now passes its dependencies and game compilation,
then reaches canonical world cooking. The new physics audit rejected the
sample's compatible all-ones mask; the corrected exact effective-mask comparison
and named existing friction mapping subsequently pass the unchanged canonical
world through the compiled Editor's real cook and content packager.

An unmodified native server-generated base world also passes the real compiled
Editor shared-package path and the browser identity validator, preserving all
1,406 world bytes. The [publisher evidence](../rendering/browser-project-publishing.md#native-shared-package-positive-publication)
records hashes and boundaries. Windows full publication and rendered RollingBall
remain end-to-end acceptance checks, while shared touch controls and remaining
material coverage continue as code-first implementation work.

## Shared input, textured surfaces and output policy (2026-10-02)

Authored FreeType bitmap-font cooking and engine virtual sticks/buttons now close
two complete implementation rows, bringing the checklist to **47/110**, with 63
open. The coherent group passed Editor, native-Jolt Browser, Server, VRClient,
desktop WebGPU, all eighteen portable compile rows and a fresh interpreter
publication with zero compiler warnings/errors. Subsequent targeted Editor and
Browser builds also pass after the material serialization and output-factory
repairs below. Runtime UI/IME/device acceptance is not implied by these builds.

Virtual controls use the owning player's existing gamepad mappings, a bounded
ten-contact stream, locked span-based engine UI hit tests and per-control input
delivery state. Focus, visibility, viewport/pawn replacement, overflow and callback
reentrancy cancel the correct contribution without advancing withheld physical
input. Ordinary touch widgets resolve fresh hit targets rather than stale hover.

The opaque deferred texture profile preserves base/normal/metallic/roughness
behavior through strict typed semantics, seven cooked shader variants, a
source-free material carrier, and exact canonical shader/snippet verification.
Cook-only alias reconciliation compares complete CPU image payloads and omitted
sampler/import metadata before unifying equal persistent identities. Runtime
bindings still require exact references. The ordinary-world probe exposed and
repaired a separate YAML converter bug: explicit `ShaderFloat` values written as
integer-looking scalars had incorrectly reloaded as `ShaderInt`. The converter
now honors only its existing closed type registry; untagged inference remains.
The sample catalog has 40 artifacts, 30 material variants, nine pipeline passes
and one compute kernel. New textured pixels remain unqualified.

Explicit browser quality settings now control canvas resolution/DPR, light and
resource admission caps, and optional GTAO/bloom targets. Lower presets fail
oversized authored content rather than silently dropping it; default behavior is
preserved. Settings restoration is session-owned and startup selections are
captured per request. True shadow cadence/downsize and cooked texture tiers are
still open, so the quality-profile row remains unchecked.

Exact commit `ff215b9673b5a3885ea5a101624cdccb97d7cb08` passed the complete Linux
regression job and genuine Windows Editor CLI publisher in
[run 36986080740](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36986080740).
The Windows artifact contains 68 assets, 33 shaders, 23 material variants, nine
pipeline passes and one compute kernel. Its SHA-256 is
`0632b4610020724b5de6228f2c62929600334ec117a014d8c6e70ebe04e45a33`.
The exact bundle is transferred to Linux Chromium because the Windows runner
does not provide the required WebGPU adapter. Game startup reaches WebGPU but
exposed an independent factory entry point selecting desktop Advanced resources.
The source repair scopes explicit and lazy caller-thread pipeline creation to
the same browser recipe, preserves Required-Advanced rejection, and rejects
unsupported capture/stereo/XR requests. It does not package desktop GLSL as a
workaround. Actual rendered/playable game acceptance awaits the repaired run.

Cook admission also now rejects both character-controller families by name:
existing PhysX unfiltered sweeps and Jolt filtered movement do not preserve the
same authored collision selection. Rigid-body/RollingBall filtering is unchanged.

The fixed ordinary textured-world probe now passes the real Editor preparation,
world cook, post-hydration capability audit and content packager. The saved
12,367-byte input retains SHA-256
`08f7f9c9ce25523570a537e62b98f5190daf70b8605b94e789aabc0a87842589`.
The produced source-free material has one image shared by legacy slots 0/2/3
and all three semantic roles, and the strict runtime reader passes. Its raster
fixture catalog produces 80 assets and 39 shader artifacts. The targeted driver
uses the genuine CPU headless rendering-host services; it does not render or
claim GPU acceptance. Both final Editor and native-Jolt Browser builds pass with
zero warnings/errors after the alias, YAML type and canonical-expansion fixes.

### Browser platform ownership and startup profile repair (2026-10-02)

`XREngine.Runtime.Platform.Browser` now owns the concrete canvas presentation
target and production canvas host. Neutral surface contracts stay in Rendering;
WebGPU consumes the shared interface rather than depending on the platform leaf.
The script remains linked at the same published URL. The portable closure and
solution include the new leaf (19 portable projects). CSS/backing extents, DPR
caps, orientation, safe-area layout, attachment and output generations remain
owned by the page host. Visibility, freeze and page-cache transitions preserve
session ownership and reset elapsed time and camera/pipeline temporal history.
Focus-only and unchanged-size events no longer reset frame cadence; gaps over
250 ms discard simulation debt without rebuilding physical resources.

Exact commit `9dd4b065c98847cf9ad85368a42fe6cbec25d75d` passed the full baseline
Linux browser qualification and genuine Windows Editor CLI publisher in
[run 36990713888](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36990713888).
The separate exact-bundle game check now selects `DefaultRenderPipeline`, but
could not prepare its first frame: absent game/user AA overrides inherited the
desktop FXAA default. Web resource layout rejected that profile, and the layout
exception was only logged, hiding the cause behind the startup timeout. The
canvas startup projection now supplies AA None only for absent overrides. The
browser-targeted Default schema supplies manual exposure only for missing camera
values, independent of ambient renderer binding. Authored AA/exposure selections
remain authoritative and desktop defaults are unchanged. Layout-description
failures retain their named diagnostic and existing per-key retry backoff.
Independent review cleared the corrected schema-lifetime choice. No smoke
assertions or tests changed; actual rendered game acceptance awaits this fix's
approved browser run.

The integrated Editor, Server, VRClient and desktop WebGPU builds, all nineteen
portable compile rows and fresh native-Jolt browser publication pass with zero
compiler warnings/errors. Editor was rerun after the final process-stable schema
correction. The published canvas host matches its relocated source byte-for-byte.
This closes the two platform surface/lifecycle implementation rows, bringing the
checklist to 49/110. Live lifecycle, desktop pacing and rendered-game acceptance
remain open. Logs are under the active validation run’s `browser-platform/` folder.

The first clean-checkout run of `69d693c` exposed a publication omission: the
repository-wide `Assets/` ignore rule had excluded the relocated canvas script,
although the local builds and publication used it. The corrected source adds an
explicit browser-platform authored-asset exception and tracks that exact checked
script. The browser output URL, code behavior and earlier local evidence are
unchanged; the clean-checkout run is repeated for the corrected commit.

### First Editor-published RollingBall frames (2026-10-02)

Exact commit `adb0e9ef90ea9a88a7318e022567795ece7368fb` passed both the baseline
Linux browser/rendering/physics job and the genuine Windows Editor CLI publisher
in [run 36995649490](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36995649490).
The exact Editor bundle now starts and presents the real lit course, ball,
obstacles and authored HUD in Chromium/SwiftShader; the captures were inspected.
The separate gameplay check advances through initial rendering and focused input
but fails at the pause HUD transition, whose status bar remains green. Its
[capture bundle](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36995649490/artifacts/11222715073)
preserves playing, tilt, reset and attempted-pause images. Generic image changes
alone do not prove a particular gameplay callback executed.

Source inspection confirms a shared snapshot-input defect: down/up transitions
are retained, but mapped keyboard button events consumed only the final held
mask, losing a complete tap between frames. Mouse button transitions were also
ignored by its state adapter. The implementation work preserves ordered edges
and input-ownership boundaries rather than inserting smoke-test delays. Full
pause/resume, reset/tilt semantics, resize, repeated start and broader game parity
remain open until the corrected path runs. The checklist remains 49/110.

The shared transient-input correction now passes independent read-back and a
20-case production-assembly reproduction. Rapid Escape/R and physical mouse
pulses dispatch ordered mapped edges exactly once; capture, mapping/source
changes, nested fresh snapshots and touch/physical union preserve their declared
ownership. A real state-change consumer receives its one ordinary release after
same-owner capture. The InputIntegration Release build has zero warnings/errors,
and 2,048 warmed keyboard/mouse ticks allocate zero managed bytes. This narrow
CPU/input evidence does not replace the pending exact-commit Chromium gameplay
check. Cook admission is also source-complete for the bounded startup/camera,
effect, raster/pass and shadow cases recorded in the publisher progress report;
its positive authored texture fixture and explicit unsupported-setting checks
pass through the real compiled Editor methods. The larger audio ownership work
remains a separate source group and is not part of this publication.

### Game input acceptance and resize evidence correction (2026-10-02)

Exact commit `45c3a5a00806c8c31253ccb3b16b9a473cc2c81c` passed all three jobs in
[run 37000846498](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37000846498):
baseline Linux browser/rendering/physics, genuine Windows Editor publication,
and the exact-bundle game smoke. The inspected game captures now show the pause
bar changing green to yellow and back. Tilt/reset image differences and two
fresh game contexts also pass the existing checks; generic image differences
are not an independent state trace of each gameplay callback.

Capture inspection exposed a false positive in the first resize check despite
the green job: its image contained only the CSS outline (828 colorful pixels,
exactly its width). The second resize capture shows the actual game. Thus resize
recovery is not fully qualified by that run. The source correction tracks the
last complete engine submission's output generation and properties, invalidates
page readiness on a new generation, and keeps preparation time independent of
simulation-debt resets. The strengthened existing check excludes outline pixels
and requires the authored green HUD for playing/resized frames. Independent
review confirms the old blank capture now has zero accepted colorful/green
pixels, while the actual game captures still pass. A fresh runtime run is
required; submission readiness does not claim GPU completion or browser paint.

### Reviewed audio and hosting source (2026-10-02)

The audio source group adds authored distance/cone/Doppler/relative controls,
stereo preservation, queue-relative seek semantics, composed page/surface
activation blockers, and required-world-only simulation gating while rendering
continues. Required worlds retain a session-long activation owner. Pose changes
coalesce once per source per frame, with explicit Play/queue flush boundaries.
Changed playback rates may still allocate bounded browser one-shot nodes;
queued-stream automatic looping remains explicitly unsupported.

Shared-buffer retirement now accounts for direct requeue, another source's
live queue/static use, duplicate retirement, callback throws and listener
teardown. Eleven production AudioManager/ListenerContext/AudioSource cases pass
against an accounting transport. Final Browser compilation passes with zero
warnings/errors. The canonical optional-audio v5 payload remains byte-identical
(2,669 bytes, SHA-256
`80b3e1847a6d242fcef63208f699c8b432869270f885cfdd78d45d2fb520d107`);
required audio alone emits v6, and both versions hydrate/round-trip through the
registered codec. No authored asset migration is performed. Audible browser,
gesture/device-matrix and new spatial/streaming pixel/audio acceptance remain
separate from these source/managed-boundary checks.

The new [static hosting guide](../../../developer-guides/runtime/browser-static-hosting.md)
records HTTPS, MIME/compression, immutable versus revalidated URLs, a conditional
CSP example, and why the current single-threaded profile does not require
cross-origin isolation. No deployment or security header was applied. The
combined audio/hosting/resize group passes the integrated Editor, Server,
VRClient, desktop WebGPU, all nineteen portable compile rows and fresh native-Jolt
Browser publication with zero compiler warnings/errors. Logs are under the
active run’s `audio-output-gate/` folder. This closes the audio activation and
static-hosting documentation rows, bringing the checklist to 51/110. The broader
audio and live resize/device acceptance rows remain open.

### Shader cooking and output-contract reconciliation (2026-10-02)

A review against exact row wording closes the pinned schema-3 Slang-to-WGSL
engine cook and current-generation canvas output implementations on the already
compiled `4a801dba` source. The current checklist count is **53/110**. These two
implementation closures do not imply that every shader profile or the corrected
resize path passed live acceptance. Browser service composition remains open
until every required provider is implemented or explicitly rejects unsupported
operations; nullable/no-op defaults do not establish that closure.

### Zero-delta gameplay status and shared frame operations (2026-10-02)

The next exact-bundle game run exposes a pause-capture failure before its resize
checks. The last element capture is green; the later full-page failure capture
is yellow. Archive times differ by approximately ten seconds, so those captures
do not isolate GPU/compositor delay from application refresh timing. The run's
failure remains recorded rather than retried away or accepted by a weaker
predicate.

Source review identifies a deterministic status-refresh defect: RollingBall's
HUD refresh countdown uses simulation delta, while the browser previously discarded delta
for active rAF gaps above 250 ms. Input and rendering still execute, so pause
can take effect while its old HUD persists. Explicit pause/resume now refreshes
the real HUD immediately; other round-state changes bypass the cosmetic refresh
throttle. An actual component/DebugDrawComponent probe at zero delta and positive
countdown passes pause/resume from Playing and Falling, preserving 36 shapes,
score/time/lives and queued physics enablement. The actual RollingBall project
and probe compile with zero warnings/errors. This proves the source defect's
repair, not the pending browser capture outcome. Logs are
`logs/hud-state-probe-build.log` and `logs/hud-state-probe.log` in the active run.

Shared timer collection/publication operations now serve both desktop's existing
collect worker and caller StepFrame, preserving independently scheduled desktop
simulation. A production timer probe passes generation publication, failure and
restart, configured-caller capability guards, native worker start/dispose and
2,048 warmed empty frames with zero caller-thread managed allocation. Callback
ordering was initially confirmed from shared source; the final probe now records
and checks `fixed, fixed, update, collect, world swap, viewport swap, render`.
It also checks pre-start/post-stop caller guards, preservation of a 0.75-second
fixed interval across two 0.5-second frames, and the one-second/four-fixed-tick
cap for a ten-second request. The full Host graph compiles cleanly, and the
final helper/probe rebuilds contain zero warnings/errors. Final probe evidence
is in `logs/caller-frame-final-probe.log`.
Broader blocking-site closure, literal all-phases desktop frame-step semantics,
physical pacing and the documented inherited concurrency cases remain open.

### Active browser time and explicit host-service composition (2026-10-02)

Valid slow active rAF intervals now reach the existing bounded engine clock
instead of producing `Step(0)` indefinitely. Gaps over 250 ms invalidate camera
and pipeline history without clearing fixed-step fractions. Suspension, output
replacement and invalid timestamps still reset timing; the first restored frame
has zero elapsed time. Required-audio activation similarly consumes a zero-delta
simulation frame when its gate clears. Existing parameterless desktop history
invalidation retains property notifications; the repeated browser timing path
can update the same temporal epoch silently to avoid event allocations.
Independent source review clears that contract. A probe executes the production
canvas-host body with controlled timestamps and confirms active progression,
suspension/output resets, unchanged-size cadence, invalid clocks and raw-time
preparation deadlines. Its imports are resolved to the built renderer module;
it does not create a browser or GPU. A production camera/viewport probe also
passes 2,048 silent history invalidations with zero managed bytes and zero
property events, advancing the same epoch. The existing parameterless API still
publishes its ordinary property notification. The probe build has zero warnings
or errors; the isolated integrated clock-policy build now passes.

Browser host composition now explicitly installs unavailable native services
instead of inheriting nullable/no-op desktop defaults. Optional VR input
registration correctly returns unavailable so the ordinary keyboard/gamepad
pawn still activates; required window/VR/video/import operations fail by name.
The session owns its lease before mutations, admits only default VR providers,
rejects foreign direct-provider overlays during teardown without losing retry
ownership, and uses retired-node import scopes that cannot revive dead providers.
Composition/replacement is serialized on the browser event thread.

The focused Browser Release build and native production-source composition
probe have zero warnings/errors; nineteen capability/ownership cases pass.
That actual built WebAssembly also completes three real XRWorld/Jolt cycles
with 120 shared frames each under Node. Fetches map to the hash-verified local
fixture package; no GPU is created. The first attempt selected a RollingBall
package for the bare host and correctly rejected its unlinked game type; the
rerun uses the existing EngineSmokeWorld fixture. This evidence is service
composition and interpreter lifecycle, not rendered game/device acceptance.
Logs are under `service-composition/` and
`logs/browser-composition-wasm-probe.log` in the active validation run.

Queued audio looping is now implemented and independently reviewed, including
native future-clock handoffs, finite traversal-tail stop, retained aggregate
leases, changed-rate/pause/seek handling and bounded memory. Three production-JS
boundary probes pass. The existing offline browser audio check now contains
sample-accurate loop and near-end-disable cases at rates one and two, accounting
for its explicit initial two-quantum lead. Their first exact-commit browser run,
audibility, spatial/gesture behavior and codec/device acceptance remain pending.

### Isolated service/audio/frame milestone (2026-10-02)

The reviewed 33-file source group was compiled from its exact staged Git tree,
with the unrelated animated-world repair excluded. Editor, Server, VRClient,
desktop WebGPU, all nineteen portable compile rows and fresh interpreter/native-
Jolt browser publication pass with zero compiler warnings/errors. The actual
published module repeats the three XRWorld/Jolt cycles and 360 shared frames
under Node, using file-backed content fetches and no GPU. Evidence is retained
under the active run's `cleared-runtime-gate/` folder. Provider composition and
queued audio implementation close two rows, bringing the count to 55/110.

This build includes immediate zero-delta RollingBall HUD updates and active slow-
frame clock progression; their fresh exact-bundle browser result remains pending.
The extended existing offline audio cases likewise await this exact-commit run.
A separate startup audit proves Development-mode assembly scans still execute
for replication metadata and type redirects. Reusing the existing published
metadata builder with a verified in-memory browser install is the next bounded
startup cut; no native constructor side effect was observed by that audit.

### Immutable render-cache reconciliation (2026-10-02)

The source/evidence audit of compiled commit `0078867c` confirms the admitted
C# raster state and complete retained draw keys, plus bounded draw/layout/
bind-group/pipeline caches. This closes the immutable render-state cache row
and brings the current count to 56/110. It does not claim missing image-readback,
material generation/batching, whole-frame error-scope or allocation acceptance.

### Exact-bundle RollingBall resize and pause acceptance (2026-10-02)

Commit `0078867c7327e2a50605a655207854319045e2bb` passes genuine Windows Editor
publication and the exact-bundle Linux Chromium game job in
[run 37024792567](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37024792567).
The [capture artifact](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37024792567/artifacts/11235184430)
was downloaded, its SHA-256 verified, and both resized plus pause/resume PNGs
inspected. Resized captures contain the actual lit course, ball, obstacles and
green authored HUD: 197,647 and 237,031 accepted interior colorful pixels, with
2,995 and 3,646 green pixels. They are not the earlier CSS-border-only images.
Pause shows the yellow status bar; resume restores green. Two fresh browser
contexts pass, with no accepted browser console errors or outside requests.
Tilt/reset image changes still are not independent state traces of each callback.
The software adapter establishes this bounded rendering/game result, not physical
performance, complete gameplay semantics, all device lifecycles or desktop parity.

### Queued-loop browser sample acceptance (2026-10-02)

The same `0078867c` run passes all three jobs. Its
[software-browser artifact](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37024792567/artifacts/11236861043)
was downloaded with verified archive SHA-256. The OfflineAudioContext report
records zero maximum PCM error in all six cases: the two retained non-looping
cases, continuous queue loops at rates one/two, and near-end loop disable at
rates one/two. Loop playback starts at frame 256; finite tails end at frames
5,056 and 2,656 respectively. This qualifies actual browser sample scheduling,
not audible device output, gestures, spatialization or codec support. All
existing renderer/asset/Jolt checks also pass. The software GTAO/bloom sweep
took 19.7 minutes; broad qualification continues at coherent milestones while
independent implementation proceeds, rather than gating each source edit.

### Authored-runtime, metadata and GPU services milestone (2026-10-02)

The coherent frozen source builds Editor, Server, VRClient, WebGPU, the portable
RenderingParity game, all nineteen browser compile rows, and a fresh native-Jolt
WASM publish with zero compiler warnings/errors. Logs are under the active run's
`authored-runtime-gate/` directory. This closes published startup metadata and
brings the checklist to 57/110. The remaining 53 rows retain their own full-wording
implementation or acceptance requirements.

- [Published metadata](browser-published-metadata-2026-10-02.md) is hash-owned by
  the activated bundle and installed before engine/game startup. The genuine
  Editor method chain publishes the canonical RollingBall project, and its exact
  output passes three headless WASM start/120-step/stop cycles. The local route
  calls compiled production methods; the separate approved Windows lane runs
  the actual Editor CLI
- The saved RenderingParity world now preserves its game mode, camera/pawn,
  exact scene skeleton/root aliases, shared geometry, named morph and four mapped
  material roles through real generic hydration and Editor save/cook/package.
  Authored YAML and RollingBall codec bytes are unchanged. The generic derived
  transform cache needs a matching-engine recook; see the
  [construction investigation](../../investigations/rendering/authored-rendering-world-construction-2026-10-02.md)
- [Async canvas and depth readbacks](browser-webgpu-readback-2026-10-02.md) use
  accepted producer tickets, bounded copies, async error/map completion and
  lifecycle cancellation. [GPU luminance](browser-webgpu-luminance-2026-10-02.md)
  adds a hash-owned reduction kernel; legacy mip-generation and sRGB cases
  remain explicit unsupported contracts
- [Shadow quality](browser-webgpu-shadow-quality-2026-10-02.md) preserves authored
  dimensions while capping browser allocation, and reuses only unchanged,
  supported static casters tied to exact accepted target production
- The shared shader preparation cooks 42 artifacts, nine pipeline entries and
  both packed-skinning and luminance compute entries. Packaging succeeds with
  84 assets. Source/ABI cooking is not actual GPU compilation or pixel proof

The milestone workflow retains RollingBall's checks and separately publishes
RenderingParity through the genuine Windows Editor CLI, then runs each exact
bundle in Linux Chromium. The new world's broad capture checks cover visible
surfaces, motion, pause/reset, resize and two fresh contexts. Manifest presence
and moving pixels alone cannot prove mapped-lighting semantics or GPU skinning
execution; those acceptance claims remain open. No new GPU readback, luminance,
shadow-cadence or authored-world rendering result is claimed before CI evidence.

### Metadata-bearing game acceptance and compute publisher correction (2026-10-02)

Commit `d986055df320ea626666e018d3ab53f5d4e90a70` passes the genuine Windows
Editor CLI RollingBall publisher and exact-bundle Chromium game job in
[run 37040914825](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37040914825).
The [game capture artifact](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37040914825/artifacts/11243401005)
was hash-verified (`dbcb9f0613b9320570482665f5e0a9f8d4c6350c5e9485b62d000909fe6d6c00`)
and both resized plus pause/resume captures were viewed: they show the real course,
ball, obstacles and green/yellow HUD. This extends the bounded game evidence to
published-metadata startup. Source/headless validation alone was not used to claim
that browser result.

The same Windows job successfully cooked 42 RenderingParity shader artifacts,
then its real publisher rejected the two compute entries at an obsolete
one-kernel guard. Therefore no RenderingParity bundle or browser run was produced
by that snapshot. Commit `266392a25a9c5c0d3ec9b1fe73646c6ba8c8bf8e` corrects only
that Editor admission: the shared supported-kernel set and per-kernel ABI validator
now control admission within a bounded catalog. Editor compilation passes 0/0;
the production constructor admits the actual 42-artifact/two-kernel and
44-artifact/four-kernel manifests, while unknown, duplicate and mismatched entries
still fail. This fix does not weaken the browser artifact or pixel assertions.
The fresh milestone run is
[37044552081](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37044552081).

### Authored-world startup diagnosis (2026-10-02)

The real Windows Editor publisher and RollingBall browser job pass in both
run 37044552081 and
[run 37048210271](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37048210271).
The reported physical Intel Arc/Edge RollingBall run also renders, responds to
tilt/reset/pause/resume and survives two fresh starts and resizes. This is bounded
desktop-browser evidence, not the complete mobile/device matrix.

RenderingParity exposed two separate startup faults. The first run failed to
resolve a standalone `System.Single` array element under published metadata.
The finite framework-data resolver in `d8e4c748` removes that exception. The
next exact bundle reaches a Ready renderer but times out at zero draws in both
software Chromium and the physical PC. Shared standalone play had discarded the
authored game mode, so its saved pawn was never possessed and the viewport had
no camera. The mode is now preserved without creating the unwanted editor
fallback. The reviewed correction and production native lifecycle evidence are
in the [startup investigation](../../investigations/platform/browser-authored-mode-startup-2026-10-02.md).
Commit `83de01a5e8bd8e4b490b0ff869fd39263b93f377` is published for the existing
browser acceptance rerun; native possession/camera evidence does not establish
the first rendered textured/deformed frame.

### Compiled GPU services, material recipes and UI input (2026-10-02)

The final coherent source passes Editor, Server, VRClient, desktop WebGPU,
RenderingParity, all nineteen portable compile rows and fresh native-Jolt WASM
publication with zero compiler warnings/errors. The refreshed gate includes the
final content-version/label input changes and standalone-mode lifecycle fix.
Evidence is under the active run's `gpu-services-gate/`; narrow runtime probes
and source reviews are recorded in each linked report.

Independent full-wording source review closes the non-blocking renderer,
device-state/reconstruction and complete input leaf implementation rows. The
checklist is now **60/110**, with 50 open. This does not close device/pixel,
desktop parity, complete UI or performance verification.

- [GPU luminance](browser-webgpu-luminance-2026-10-02.md) now generates real mip
  levels and preserves encoded sRGB sampling through a raw copy-compatible
  texture. The shared cook produces 44 artifacts, nine raster pipelines and four
  compute entries. Unsupported scalar-mip cases remain named contracts
- [Device replacement](browser-webgpu-device-recovery-2026-10-02.md) reconstructs
  only the retired GPU owner and pipeline resources, retaining the game session
  and rejecting stale callbacks. Controlled boundary probes pass; forced-loss
  actual GPU continuity still needs the milestone run
- [Authored PBR recipes](browser-authored-lit-material-cooking-2026-10-02.md)
  originate from shared engine material parameters, with exact cooked source/ABI
  identities. Real authored YAML projection, dynamic parameter changes and
  unchanged legacy cache bytes pass. Automatic Editor Slang invocation and
  arbitrary shader/profile coverage remain open
- [UI clip/input](browser-ui-clip-input-2026-10-02.md) carries engine viewport and
  scissor through version-3 reusable commands, retains clipped quad/text batches,
  and bridges focused text/button controls with IME and content-version guards.
  General UI profiles and the whole accessibility tree remain open
- [Catalog routing](browser-catalog-prefab-routing-2026-10-02.md) fixes virtual
  prefab loads, preserves cached fast paths, and rejects blocking runtime-source
  operations and writes. Full runtime I/O/thread closure is still incomplete

No new live GPU acceptance is inferred from the full build gate. The current
published-world run remains the required next check for RenderingParity pixels.

The physical `83de01a5` rerun confirms startup now reaches shader creation, where
the mapped-normal prepass exposes a real derivative-uniformity error. The final
source addition samples before varying fallback guards and updates the canonical
authored-material include pin. All four affected variants and the authored
normal recipe recook; the narrow ShaderCooker rebuild passes 0/0 after the full
gate. Independent emitted-WGSL review preserves implicit LOD and normal math.
The [startup investigation](../../investigations/platform/browser-authored-mode-startup-2026-10-02.md)
records exact failed/corrected hashes and keeps real first-frame acceptance open.

### Physical authored-world acceptance (2026-10-02)

The physical RenderingParity run at
`7ba776cf223f0bc1fcf4e1e6b74e2c6b56ec5e9c` passes real Editor build, fresh
shader cook and browser publication. Inspected captures show textured/shaded
panels and an animated ribbon; Space pause/resume, R bind-pose reset, resize and
two fresh startups pass. The non-fallback Intel Arc Xe-LPG adapter used driver
32.0.101.8132 and Edge 154.0.4258.48 with the sandbox enabled. There were no
console errors or HTTP failures; three canceled fetches are documented separately.
Test-owned processes were closed and the isolated worktree remained clean.

This is actual browser rendering of the saved authored game, including its game
mode, pawn/camera, textured material and combined skeletal/morph animation. It
closes the preceding first-frame failure, not desktop image parity, isolated
CPU/GPU deformation comparison, performance budgets or the full device matrix.
The shared-asset load acceptance row now closes, bringing the checklist to
61/110 while broader remaining requirements stay open. Further implementation continues with authored asset
streaming, bounded delivery/integration accounting and earlier game API admission.

The exact Editor-published RenderingParity bundle also passes Linux Chromium in
[run 37056066628](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37056066628).
Its [capture artifact](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37056066628/artifacts/11249716330)
has SHA-256 `eaf277fe54c275b0b6b557246dd8161f9948d3e0f8d5dc83f679e18676ae95e8`.
The downloaded archive matches that digest; the animated and reset captures were
viewed and show the checker-textured ribbon bending and returning to its straight
bind pose beside the rigid panel. The report records `passed: true`. All four
jobs are now green: the real Windows Editor publisher, both exact-game browser
lanes and the broad engine/native-physics baseline.

The [baseline artifact](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37056066628/artifacts/11249799018)
was also downloaded and its SHA-256 matches
`ac561182f0ae9bf52d54a2007ac84671617019925454103bdaef94989c20dd78`.
All requested baseline checks pass: depth, texture/lifetime, lit/HDR/tonemap,
GTAO/bloom, directional shadows, debug overlay, shared world lifecycle, queued
audio samples, asset lifetime and native Jolt. The bare-host autostart/game
checks are explicitly skipped there and covered by the separate real-game jobs.
The software GPU GTAO/bloom matrix takes about 1,254 seconds of the 1,398-second
baseline run; the separate RenderingParity game check takes about 59 seconds.
These are CI execution costs, not representative hardware frame-time budgets.

### Compiled streamed assets, delivery accounting and image UI (2026-10-02)

The next source snapshot passes Editor, Server, VRClient, desktop WebGPU,
RenderingParity, all nineteen portable compile rows and fresh native-Jolt WASM
publication with zero compiler warnings/errors. The source hashes are frozen in
the active run's `asset-ui-gate/`; the reused browser output is the latest
`gpu-services-gate/browser-publish/` directory. Full-wording source review closes
the generalized asset package, bounded delivery/integration and build-time game
API diagnostics rows. Including the physical shared-asset load acceptance above,
the checklist is **64/110**, with 46 open.

- [Authored delivery](browser-authored-asset-delivery-2026-10-02.md) uses the same
  XRAsset cooker for startup and declared lazy XRScene roots, with compatible
  essential/streamed root partitions. Genuine saved textured-scene export and
  hydration retain shared texture aliases and unchanged authored bytes. Real
  world/scene-host checks cover shared handles, external ownership, failure retry
  and disposal; incomplete root cleanup is no longer discarded prematurely
- Global FIFO admission wraps actual synchronous hydration, with finite queue,
  staging, per-frame starts/bytes and elapsed-time limits. An indivisible batch
  can overrun its time target and is reported. Source ownership remains the sole
  destruction owner; measurement uses weak allocation keys. Native shared-package
  preflight still authenticates all files eagerly and is counted separately
- [GPU estimates](browser-gpu-memory-2026-10-02.md) walk existing live and retiring
  resource owners on demand, deduplicate shared objects, and distinguish logical
  buffer size and texture descriptors from unmeasured driver residency. Shipping
  progress wakes only during active delivery and has no idle memory-poll timer
- [Game API admission](browser-game-assembly-audit-2026-10-02.md) verifies the exact
  built/loaded DLL before cooking and again before publishing. Metadata-only
  checks report bounded assembly/type/member findings, including available
  browser-platform annotations and nested/generic signatures. Dynamic behavior
  and unresolved third-party declarations are not claimed statically safe
- [Image UI](../rendering/browser-ui-image-profile-2026-10-02.md) adds a shared,
  batched display-space RGBA8 image quad with canonical desktop GLSL and exact
  browser semantic projection. UV data uses retained instance storage and the
  per-texture mesh cache is bounded. Cook, compile and independent source review
  pass; browser image pixels, sRGB image semantics, rotated glyphs and MSDF remain
  separate open coverage

These source closures do not close browser streamed-scene playback, cold/warm or
throttled delivery, cancel/restart, exact heap/device budgets, or general UI and
accessibility acceptance. The next renderer source slice addresses renderbuffer
ownership and explicit MSAA color resolve rather than enabling unsupported AA
profiles silently.

The published snapshot `4c91e14708bdbf053d17aea19feda08db7a4fbfb` now passes
all four jobs in [run 37063133078](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37063133078):
the portable/browser/native-physics baseline, genuine Windows Editor publication,
and separate Chromium runs of the exact RollingBall and RenderingParity bundles.
This preserves the preceding end-to-end baseline while the next source group is
implemented. It does not establish live streamed-scene, image-UI, or Advanced
coverage that those checks do not exercise.

### Modular pipeline scope clarification (2026-10-02)

The owner requires browser support for Default, Advanced and other authored
modular pipeline assets, preserving their selected commands, settings and
submission modes. CPU-direct, GPU-driven indirect and compute/indirect meshlet
zero-readback paths are in scope. The [shared implementation plan](../../design/platform/modular-browser-render-pipelines-2026-10-02.md)
distinguishes algorithm support from unavailable hardware task/mesh shader
extensions and prohibits silent CPU fallback or count readback in zero-readback
modes.

Four explicitly open acceptance/implementation rows extend the checklist to
**64/114 complete, 50 open**. The historical 64/110 published result remains
valid. The completed initial safety rejection of Advanced does not count as
Advanced rendering support, and removing a type gate alone cannot close the new
requirements.

### Shared modular contracts and backend recording (2026-10-02)

The frozen implementation passes Editor, Server, VRClient, desktop WebGPU, all
nineteen portable compile rows and fresh native-Jolt browser publication with
zero compiler warnings/errors. The [modular contract record](../rendering/browser-modular-pipeline-contracts-2026-10-02.md)
documents saved/reloaded clear and unrelated quad assets, exact scoped shader
dependencies and compute identities through the real publisher audit. This closes
the generic-contract implementation row: **65/114 complete, 49 open**. Browser
output for the new custom assets and the Advanced stage family remain open.

The same group implements [engine renderbuffers and retained color resolve](../rendering/browser-color-msaa-resolve-2026-10-02.md)
and [pipeline-independent frame diagnostics](../rendering/browser-engine-frame-diagnostics-2026-10-02.md).
Their narrow lifetime/allocation probes pass, including 271 JavaScript boundary
assertions, but do not establish new live MSAA pixels or whole-world allocation
budgets. The missing shared compute-program event connection is also repaired,
so declared buffer-only command dispatch reaches the existing ordered recorder
rather than silently doing no work. Storage-image operations remain explicit
until their subsequent resource implementation is installed.

### Recovered static Advanced integration (2026-10-03)

The [static integration record](../rendering/browser-advanced-static-integration-2026-10-03.md)
separates recovered source, reconstructed source, fresh managed/cook/export
checks, and the pending complete browser milestone. The current source adds
exact integer/storage texture operations, canonical scene retention and GPU
visibility, bounded native material cohorts, and the selected Advanced output
chain. The saved sample now passes genuine Editor export and a separate-process
cooked BeginPlay with its exact authored pipeline and settings.

The workspace reset lost local build outputs and an unpublished portion of
the shared source. Uploaded Git objects recovered the first portion exactly;
the remaining source was reconstructed and freshly compiled/reviewed. Earlier
pre-reset logs are historical evidence, not proof of the reconstructed bytes.
The last published baseline remains `e8e381a26d80d146a38eab972cdbda4c6c851614`,
whose four jobs passed in
[run 37070456974](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37070456974).
No Advanced browser image has passed yet. The checklist now separates concrete
implementation leaves from live validation instead of counting broad parent
summaries a second time.
