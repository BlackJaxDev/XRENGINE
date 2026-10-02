# Unified browser implementation checkpoint

Updated: 2026-10-01. This is a partial implementation checkpoint, not a playable
browser release or completed parity qualification.

## Implemented and compiled

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

## Deliberately unavailable

The production browser player advances the real world but reports that rendering
is unavailable. `DefaultRenderPipeline` rejects WebGPU output until the required
forward lighting, attachment, material, and tonemap routes exist. Engine texture
and framebuffer wrappers, lit material generation, the remaining raster shader
groups, shadows/environment/post processing, engine UI/text, and rendered sample
parity are not implemented by the depth diagnostic.

The separate browser reference runtime remains frozen and present. Its removal
is gated on genuine published-project parity. Physical-device budgets, desktop
render preservation, AOT measurements, tolerance-based cross-platform physics,
full recovery, and production networking qualification remain open.

## Browser CI scope

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
