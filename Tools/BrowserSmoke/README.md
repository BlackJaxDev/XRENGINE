# Browser runtime smoke harness

The CI game lane publishes through the genuine Windows Editor CLI, verifies its
activated canonical content, and uploads the exact static bundle. A dependent
Linux Chromium job downloads that artifact and runs the existing game checks
with explicit SwiftShader. RollingBall and RenderingParity use separate matrix
entries and artifacts, so a second-world failure does not replace the first
game result. This keeps Editor CLI execution and GPU qualification
on runners that provide their required capabilities; the game is not republished
or rewritten on Linux. The separate Linux engine-regression job remains intact.

Each ref retains one active normal browser workflow run. A later source push
does not cancel that run. The default concurrency queue keeps at most one
pending run; a newly queued run replaces the previous pending run. This
[GitHub concurrency policy](https://docs.github.com/en/actions/concepts/workflows-and-actions/concurrency)
preserves an active qualification result while further implementation continues.
It can delay qualification of a newer commit, so results must stay associated
with their exact source SHA. All existing source/helper triggers, job time
limits, and assertions remain. The isolated networking workflow keeps its
separate admission and one-run activation checks.

After saving the three browser bundles, the Windows job runs targeted
RenderingState, FBO-binding, thread-isolation, and existing VulkanP1
ownership checks, with their TRX results retained separately. The Linux game
pixel jobs still consume the unchanged saved bundles.

This is production-path qualification tooling. It serves an already published
browser application and cooked shader artifacts, launches real Chromium, and
checks the actual engine-rendered pixels. It neither mocks WebGPU nor replaces
physics or engine scene objects. A source/build success is not a smoke pass.

The texture diagnostic uses the same engine camera, ModelComponents and frame
path with canonical UV streams and an authored `XRTexture2D`. It checks four
colored texture corners, the linear result of sampling an sRGB midpoint, and
five alternating resource replacements. Retired resources must drain without
growing the live GPU-resource count; reference-scene packets remain forbidden.
Array/cube/depth sampling is not implied.

The separate lit diagnostic uses the built-in `StandardLitColorV1` material
factory, a normal-bearing `ModelComponent` quad, actual directional/point/spot
components and their engine light collections. The shared `DefaultRenderPipeline`
writes RGBA16F scene color, then presents it with its cooked Mobius tonemap pass.
Fourteen cases check ambient, each light type, combined lighting, BaseColor,
Roughness, Metallic, Specular, Opacity, Emission, exposure and light intensity,
then restore the baseline. The same material, shaders and GPU pipelines must
survive every numeric update. An explicit diagnostic-only 5x5 HDR readback checks
linear color and alpha (the opaque browser canvas cannot expose material alpha);
separate canvas screenshots check display-encoded pixels. Above-one emission
must survive in HDR. No replacement unlit fixture is admitted. All lights have
shadows disabled. Five surface resizes rebuild the declared resource generation,
recheck HDR/display pixels, and require retired GPU resources to drain without
live-count growth before teardown. This establishes the bounded static rendering path, separately
from RuntimeWorld/gameplay, shadows, probes, transparency and broader effects.

The `engine-unlit-materials` check starts a separate nine-tile board through the
packaged material and pipeline catalogs. Factory-created V1 color, V2 linear and
sRGB texture, V3 forced-opaque texture, V4 cutoff-boundary texture, and V5
two-layer array materials all render together over a discard background. The
check compares independent known values with real RGBA16F readbacks and canvas
captures, then repeats after non-square resize and fresh session startup. Case
selection changes metadata only; the check requires stable package identities,
native shader modules and render pipelines across resize, bounded device-local
program caches, CPU-direct mesh submission, and drained resources. Engine draw
handles may rebuild with the surface generation. This qualifies
the static, single-sample default-pipeline cohort; it does not qualify custom
graphs, MSAA, GPU-driven submission, or authored serialized materials.

The separate `engine-unlit-msaa` check selects `cpu-x4` and `cpu-x4-ao` before
the same fixture initializes. The nine original center values stay unchanged.
A sloping overlap in the gutter adds independently known colors, depths and
normals: fractional HDR coverage must occur in quarter steps, and the enabled
AO profile must resolve depth and normal from the same nearest covered sample.
This avoids assuming fixed hardware sample positions. Submitted packet and
native texture observations check the actual x4 attachments, x1 color resolve,
enabled-only depth/normal resolve and GTAO stages. Grouped diagnostic copies
pause that fixture's frame pump, then resume it before the next change. Both
profiles exercise non-square resize, restoration and fresh-session restart.
This is Default CPU-direct coverage; custom pipelines and GPU submission modes
retain separate checks. On `e2c47497`, both x4 profiles pass their initial,
resized and restored captures across two start/stop cycles per profile, including
coherent depth/normal resolution and GTAO. The earlier comparison-argument parse
error is fixed without changing the selected depth sample.

The same diagnostic page also exposes `gpu-indirect-x1`, `gpu-indirect-x4` and
`gpu-indirect-x4-ao`
for the ordinary authored indexed route. These select managed
`GpuIndirectZeroReadback` before pipeline construction and bind the verified
primitive-cull, LOD-select and source-order companions. Select the profile before
starting **Unlit materials**; `unlitState()` reports the requested strategy and
the actual indexed submission strategy/reason. The JavaScript renderer's legacy
packet strategy is separate from these engine-frame commands. GPU qualification
must inspect actual indirect work, rendered values and visibility/count readback
activity; selecting the profile alone does not establish acceptance.

The separate `engine-unlit-indirect` check reuses the known-value centers and x4
coverage witness with two fresh contexts per GPU profile and the same three
extents. Managed strategy, actual LOD/cull compute operations, indirect scene
draw issuance and direct canvas presentation are checked independently. Native
READ-map counters remain cumulative: ordinary startup/render/resize intervals
must add zero maps, while paused diagnostic sampling must account for exactly
nine maps at x1, ten at x4 without AO, or twenty-two for the x4 AO profile.
The latter requires indexed depth/normal and color draws, matching GPU producers,
coherent closest-sample sidecar resolution and three ordered GTAO stages. The
ordinary x1/x4 profiles pass twelve captures on `6fea26f9`; the new GPU AO profile
still awaits live execution. GPU-visible effective argument counts remain opaque.

The additional `cpu-x4-blended` and `gpu-indirect-x4-blended` profiles keep the
nine original tile cases and give the two gutter surfaces explicit source-over
blending. Front-before-rear insertion makes the expected rear-then-front result
depend on correct sorting. Independent HDR expectations include quarter-sample
coverage, RGB SrcAlpha/OneMinusSrcAlpha and alpha One/OneMinusSrcAlpha over the
opaque background. The GPU check requires source ranks, argument masking,
retained raster-input copies and real indexed-indirect replay. Ordinary frames
must still add no READ maps. These new profiles require fresh runtime acceptance;
synthetic oracle checks and replay of older captures do not establish their pixels.

The `engine-shared-gtao-bloom` check runs the static effects diagnostic through
the real WebGPU default pipeline. It reads prepass depth and normal, GTAO stages,
raw HDR, bloom mips and the combined target, then changes camera effects and
authored mesh face coverage. Tiny and odd resizes plus stop/restart exercise
resource generations. Once two accepted ready frames have been observed and
retired GPU resources have drained, the diagnostic holds its own frame pump
during each effects pixel sample, including the canvas capture, then resumes it
before the next change. The report records duration and frame and submit deltas.
This check requires a package manifest containing the exact cooked pipeline
artifacts and reports failures separately from the
authored RollingBall world-play check.

The separate `rollingball-editor-published-game` check mounts the unmodified
output of `Tools/BrowserSmoke/Publisher/RollingBallPublisher.csproj`. That driver
copies the canonical RollingBall project, Assets, Config, and Metadata into an
owned validation directory, hashes those authored bytes before and after, and
calls the compiled production Editor's browser build method. The normal game
compile, world cook, WebAssembly publish, content pack, launch configuration,
and atomic activation must all finish. No prebuilt replacement page or mock
world is used. Chromium opens that shipping index page, requires the real
world/Jolt startup and nontrivial canvas pixels, exercises focused keyboard
tilt and reset, checks the pause HUD color transition and its reversal on
resume, resizes the canvas, and starts a second fresh lifecycle. Saved game
screenshots and pixel summaries are bounded visual/input evidence; they do not
establish numeric rigid-body parity or native-GPU performance.

The separate `rendering-parity-editor-published-game` check opens the shipping
player for the saved RenderingParity world. Its static mapped panel and animated
skeletal/morph ribbon must both occupy the canvas interior. Captures check
right-side motion, animation pause, bind-pose reset, resume, actual canvas resize,
and two fresh page contexts without console errors. CI cooks canonical shaders
with the pinned Slang release and verifies the packed-skinning and tangent-material
artifacts in the genuine Editor bundle. These broad pixel/artifact checks do not
by themselves prove GPU dispatch or known-value mapped-lighting semantics; those
remain separate acceptance evidence.

The `static-meshlet-parity-editor-published-world` check uses the saved single
mapped panel from RenderingParity with meshlets enabled. Windows publishes two
copies of that same saved world through the Editor: GPU meshlet startup and a
CPU-direct startup reference. Linux compares matching settled panel captures
at both initial and resized extents, then requires an explicit startup marker,
CPU direct indexed draws, actual select/cull/finalize compute dispatches,
`drawIndexedIndirect`, and the same original mapped vertex/fragment module
identity on both paths. The expected program comes from each published
StandardLitTexture material variant, with descriptor and WGSL hashes checked
against the active draw. It requires zero GPU READ maps of any label, two fresh
GPU contexts, and zero retained resources or tickets after owned host disposal.
The saved-world bootstrap rejects missing or non-owner-validated cooked
payloads. The smoke report does not infer GPU-visible meshlet counts from CPU
commands.

The directional-shadow diagnostic is a separate static fixture. It cooks the
`StandardLitColorV1` HDR shadow receiver and `OpaqueShadowDepthV1` writer as
distinct variants, then uses one registered `DirectionalLightComponent` and its
real `ShadowRenderPipeline` viewport. The light has one normal-Z orthographic
depth24 map, no atlas/cascades/contact shadows, and the authored 8/8 PCSS,
bias, filter and source settings. The 256² diagnostic map can be rebuilt at
512²; the normal engine default remains 2048². An angled narrow caster projects
outside its camera-visible silhouette onto a wider receiver. Actual HDR
readbacks and canvas captures verify contrast, caster/light movement, near/far
penumbra, disabled shadow binding, map resize and stop/restart. The diagnostic
shadow camera uses a 4×4×100 world-unit volume so the near blocker does not
already saturate the authored PCSS maximum radius; the far blocker does.
Producer pass
and caster counters plus the dedicated depth target must agree with those
pixels; a mock shadow or reference-scene packet cannot pass. This qualifies the
bounded shadow profile, not cascades, atlas, contact shadows or gameplay.

The shared debug-overlay diagnostic uses a registered `DebugDrawComponent` and
the `DefaultRenderPipeline` callback after tonemapping. Three cooked debug
variants expand packed point, line, and triangle storage on the GPU. It checks
colored and alpha-blended pixels, same-count value changes, count and capacity
changes (including 256 to 384), repeated resource retirement, a non-square
resize, warm single-frame submission, and stop/restart. Offscreen surplus shapes
exercise buffer capacities without changing the visible reference primitives.
The separate published-WASM recording probe covers ordered frame uploads and
failure atomicity; browser smoke remains the pixel qualification.

The audio check imports the published Web Audio streaming scheduler and renders
two adjacent PCM buffers with a real `OfflineAudioContext`, checking every output
sample at rates 1 and 2, processed-buffer order, and disposal. It also checks
whole-queue native looping and disabling near the end of a traversal at both
rates, including the absence of an extra traversal. All six cases pass with zero
maximum PCM error at commit `0078867c` in
[run 37024792567](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37024792567).
The check requires no
microphone or output-device permission. This establishes scheduling/sample
correctness only, not audible playback, gesture unlock, spatialization, or codec
support across the device matrix.

## Prerequisites and execution

Use Node 20 or newer, the repository's pinned .NET/browser toolchain, and the
already pinned Playwright dependency:

```sh
npm ci --prefix Tools/BrowserSmoke
cd Tools/BrowserSmoke
npx playwright install chromium
cd ../..
node Tools/BrowserSmoke/run.mjs \
  --browser-publish <browser-publish>/wwwroot \
  --game-publish <editor-published-rollingball-root> \
  --shader-artifacts <depth-shader-cook-output> \
  --output Build/_AgentValidation/<run>/reports/browser-smoke
```

Cook `Build/CommonAssets/Shaders/WebGPU/engine-depth.recipe.json`,
`engine-depth-probe.recipe.json`, `engine-texture-probe.recipe.json`,
`engine-standard-lit-color.recipe.json`,
`engine-standard-lit-color-directional-shadow.recipe.json`,
`engine-shadow-depth.recipe.json`, `engine-tonemap.recipe.json`,
`engine-debug-point.recipe.json`, `engine-debug-line.recipe.json`, and
`engine-debug-triangle.recipe.json` with `Tools/ShaderCooker`; supply the directory
containing its schema 3 `manifest.json`, hashed descriptors, and hashed WGSL. The
shader artifacts are explicit runtime inputs to the smoke, not dependencies on
an ignored prior agent run. The browser publish must include
`diagnostics/engine-mesh.html` and the production engine/runtime modules.

`XRE_BROWSER_EXECUTABLE` optionally selects an already installed Chromium. With
that variable absent the harness uses Playwright's managed Chromium and its new
headless channel. `--headed` is available for local inspection. The harness does
not download or install a browser as part of a run.

`--game-publish` points to an activated browser-game directory rather than the
bare browser host. It is optional for the full diagnostic harness and required
by `--game-only`. `--game-kind rollingball` is the default; use
`--game-kind rendering-parity` for the authored textured/deformed world. The portable publisher driver can invoke the compiled Editor
browser method on Linux with `EnableWindowsTargeting=true`; the dedicated
Windows CI lane uses the actual Editor CLI instead. The Linux CI lane retains
the shared renderer and runtime diagnostics, avoiding a duplicate game publish.

The Windows CI lane invokes the production Editor CLI with the canonical
RollingBall project: `--build-project <project.xrproj> --build-configuration
Release --build-platform BrowserWebGPU --output-subfolder browser-game`. It
stages authored inputs under the validation root and uses the pinned Jolt
source/build scripts. Explicit browser target selection treats saved desktop
NativeAOT options as inapplicable defaults; explicit incompatible AOT CLI
options still fail. The CLI override is not saved into project settings. This
lane runs `run.mjs --game-only --game-publish <editor-output> --gpu-mode software
--output <evidence>` to check the activated shipping game in Chromium without
repeating the full engine-diagnostic suite. Its artifact retains the Editor
CLI log, descriptor, manifest, browser report/logs, and canvas screenshots.

Default `--gpu-mode native` does not force a software implementation and rejects
reported fallback/SwiftShader/llvmpipe adapters. An unavailable WebGPU adapter is
a failing `WebGPUUnsupported` result, never a skipped/passed rendering check.
For a deliberately software-only trusted CI runner, use `--gpu-mode software`.
That explicitly enables developer WebGPU/SwiftShader launch switches, records
the actual adapter and flags, and labels the result software API/shader
correctness only. It is not physical-device, native GPU, or performance evidence.
Do not use software developer switches with untrusted web content.

The harness binds an ephemeral port on `127.0.0.1`. Only the supplied publish,
shader, and optional Jolt/game directories are mounted. Path/symlink escapes, hidden
path segments, non-read requests, and unexpected Host headers are rejected.
Browser requests to other origins are blocked and fail qualification. No server
is exposed on a LAN interface.

## Checks and evidence

- The depth/texture engine diagnostics must report all three mesh submissions; the
  lit path must report its normal-bearing surface and tonemap submissions. A clear-only
  page cannot satisfy readiness
- The shadow path must render its real standalone depth writer before the HDR
  receiver and tonemap frame is presented. Changes wait for a new producer pass;
  pending preparation cannot count a partial frame as ready
- A 512x512 screenshot of the composited WebGPU canvas is decoded and sampled in
  5x5 interior patches. Near-left depth 0.25 must produce RGB approximately
  `(64, 0, 191)`; far-right depth 0.75 approximately `(191, 0, 64)`, each within
  eight byte values. Two background patches must match the authored clear color.
  Additional above-edge/below-edge patches reject an upside-down triangle. The
  far-left third triangle must remain behind the near-left triangle
- Distinct transformed instances exercise per-draw uniform snapshots. The fixture
  is real `ModelComponent`, `XRMeshRenderer`, `XRCamera`, `XRViewport`,
  `RuntimeWorldRenderer`, and the explicitly authored diagnostic engine pipeline
- Stop must remove the diagnostic session. Console errors and unhandled page
  errors fail the check
- Renderer counters must show real mesh submission and zero reference-pipeline
  frames/legacy mesh packets
- The separate engine-diagnostic page must boot its actual .NET JS exports and
  reach the stopped/ready state. This alone is not world-play or rendered-world proof
- When an authored browser launch descriptor is present, the shipping index page
  must auto-start its canonical engine asset manifest without diagnostic URL controls.
  Its running status is world-play evidence only; it reports rendering unavailable
  until the production web-tier pipeline passes its own gate

Optional `--engine-manifest /relative/path/manifest.json` supplies the real browser
asset catalog required by the engine-depth diagnostic; without it, that check is
skipped. The diagnostic page's manual controls require both the schema 3 shader
manifest and this cooked engine asset manifest. It reads a payload through
the real `engineAssetImports` source twice, verifies its size/hash, and checks
released, canceled and disposed owner/ticket behavior. The manifest and payloads
must be inside the supplied browser publish root. This byte-delivery check does
not claim decoded-world acceptance. Add `--require-world-play` only for a publish
with a real browser physics module and cooked startup world: it requires two
actual start/stop cycles on engine-diagnostic and fails named if the backend is absent.

The CI lane cooks `Content/EngineSmokeWorld.asset` into a published binary world
and generates its type metadata with `Tools/BrowserRuntimeMetadataCooker`, both
under the ignored validation root. The effects-recipe preparer copies those
generated inputs into its staging directory before the shared content packager
runs. No generated fixture binary is checked in. The lane requires two real
engine-world start/stop cycles, each completing at least twelve caller-thread
frames. This minimal canonical authored world contains one scene/root transform,
not a separate scene DTO. It qualifies the headless world lifecycle, published
binary hydration, and hash-verified asset path; it is not a representative game,
renderer, or physical-device parity result.

Optional `--jolt-spike <jolt-browser-spike-wwwroot>` mounts the separately published
native spike, runs its existing native assertions twice, requires native teardown
completion, and rejects any worker starts in this single-threaded profile. Omitted
optional checks are recorded as skipped, not as successful qualification.

Outputs are `smoke-report.json`, `browser-console.json`, actual canvas/page PNGs,
and any failure screenshot. The JSON includes browser/Playwright/Node/platform
versions, selected GPU mode, adapter metadata, launch arguments, per-check results,
request failures, console/page errors, pixel ranges, and renderer statistics.
Nonzero exit status means failure, including browser launch, unsupported GPU,
missing files, timeouts, validation errors, or wrong pixels. Always inspect the
saved screenshot when accepting rendering evidence.

### Native GPU loss diagnostics

Opt-in `--gpu-diagnostics` adds Chromium's stderr logging switches, CDP GPU and
process snapshots before/after the unchanged engine-depth check, and the retained
first engine failure to the report. With `DEBUG=pw:browser`, Playwright forwards
native Chromium process stdout/stderr to the runner. The CI lane captures both
streams from process startup in the qualification artifact. These streams may
contain the local qualification URLs and should be reviewed before external sharing.

After engine-depth, a separate page runs independent raw WebGPU clear and tiny
triangle submissions, checks their actual canvas pixels, and compiles the same
hash-verified cooked depth-probe WGSL and its known pipeline ABI without loading
the engine or .NET runtime. `gpuCanary`, `gpuCanaryPixels`, and the explicitly named
diagnostic-only check distinguish these stages. The canary uses the same browser
process and exact launch settings; it cannot satisfy, skip, replace, or change the
engine-depth pixel/submission requirements. CDP GPU-process IDs can be compared
with the native log to identify process replacement; a missing CDP snapshot is
reported as diagnostic evidence failure, never a successful rendering claim.

Diagnostic flags only enable logging; they do not select another GPU backend or
relax WebGPU validation. See [Chromium logging](https://www.chromium.org/for-testers/enable-logging/).

The software lane enables the Vulkan feature as well as selecting the SwiftShader
Vulkan driver. In [run 36931696715](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36931696715),
the driver switches alone left Skia on GaneshGL: both engine-depth and the separate
raw canary lost their device while creating the canvas shared image, with no
shader draws and no GPU-process restart. Native stderr identified the missing
WebGPU swapchain backing factory. Chromium's [shared-image factory selection](https://chromium.googlesource.com/chromium/src/+/ae6b8f6dca19521b784f8695c9d5203e977aee54/gpu/command_buffer/service/shared_image/shared_image_factory.cc)
requires a Vulkan context or enabled GL/Vulkan interop for its Linux Vulkan backing.
The single feature switch is a software-runner configuration correction to qualify
through the unchanged raw-canary and engine-depth checks; it is not itself evidence
of successful rendering. Native GPU mode is unchanged.

Agent runs must place `--output` under the active `Build/_AgentValidation/<run>`
root and honor `Tools/Limit-AgentValidation.ps1`. CI may supply its own artifact
directory. No completed run is claimed by adding this harness.

## Scope and references

The `advanced-rendering-parity` game kind consumes the ordinary Editor-published
`Samples/AdvancedRenderingParity` player. It requires the saved static textured
surface to reach the canvas across two startups and resize. Read-only wrappers
record the actual WebGPU pipeline labels used by compute dispatches and raster
draws, requiring native visibility, depth, AO, classification, shading and final
presentation. GPU read mappings are counted separately and must remain zero.
This initial sample selects CPU-direct submission; it does not qualify meshlet,
deformation, MSAA, desktop comparison or performance by association. The wrappers
add diagnostic allocations, so this run is not steady-state allocation evidence.

Native shading can select one of four optional companions when the frozen scene
proves that selected shadows and decals are absent. The harness records the
actual selected program, including its label, pass, entry point, descriptor and
WGSL identity. CI requires the exact sixteen full companions and four optional
companions. It checks every descriptor/source hash and length, then checks each
optional schema, physical layout, limits and dependencies against its full
counterpart. Missing optional companions leave the runtime on the full family;
the current source cook is expected to include all twenty. These checks do not
qualify shadow/decal rendering or dynamic selection by association.

The `ui-parity` kind consumes the Editor-published
`Samples/BrowserUiParity` world through the shipping player. It checks the
shared control callbacks through their readonly status label, authored
screen/offscreen color and glyph witnesses, clipping and native focus/text
semantics. Mouse and touch hit the actual canvas; transparent accessibility
proxies are used for semantics and keyboard focus. A temporary observational
input wrapper reads existing exported state, preserving and restoring the
original method. Synthetic composition checks event routing only. Two contexts
and resize exercise this bounded fixture; physical IME, assistive technology,
Move/Rotate actions and full GPU retirement remain separate acceptance.
Normal publication uses the existing Roboto font cook and includes its license.
Adding the fixture and harness does not establish a successful cook or browser run.

One separately approved `--ui-frame-trace` capture is limited to the first UI
iteration on an exact non-forced branch push and workflow run, attempt one. The
source policy, immutable activation record and private exclusive runner claim
must agree before a browser opens. The workflow resolves the activation to a
specific commit and verifies its bytes and complete run identity; missing or
mismatched activation fails closed. Later pushes, reruns, other games and local
command-line flags cannot activate this capture. The helper receives the existing
read-only GitHub token only while preparing the activation; the browser command
does not receive that token.

The trace uses an owned Playwright browser server bound to loopback, with the
same browser launch settings. The approved replacement requires `DEBUG=''`
before Node imports Playwright. This suppresses browser debug logging that could
retain the temporary loopback control URL. The pinned Playwright dependency
deletes an empty `DEBUG` value during import, so the later claim accepts an empty
or absent value. Every nonempty value remains rejected. Launch and connection errors use
fixed diagnostic codes. If launch fails before Playwright returns the owned
process handle, the report marks cleanup as unverified. A five-second Node exit
deadline invokes Playwright's existing cleanup hook for its own child process
group; it does not establish an observed child exit.
Connection failure uses the same five-second deadline for owned process cleanup
and forced Node exit. Verified child exit clears this guard. Failed cleanup
retains the guard and reports a fixed cleanup-unverified code.
Recording starts only after the shipping player is running, exactly 17 controls
and native proxies are ready, and the shared checkbox is checked in both the
engine and native DOM. It starts immediately before the initial checkpoint's
existing two-frame witness and stops when that witness returns or fails. A
25-second stop timer and independent 29-second owned-process
watchdog keep the capture within its 30-second limit; uncertain shutdown aborts
the diagnostic. Normal runs keep their existing launch path. The five-second
frame check, 180-second Playwright setting and all app assertions remain.

`ReportEvents` delivers trace batches in memory. The recorder immediately
reduces them to fixed event classes, categories, phases, timing values and process/
thread IDs. It does not write raw trace data, URLs, arguments or shader labels.
The browser buffer is four MiB, cumulative input is capped at 16 MiB, and the
sanitized summary is limited to one MiB and 4,096 records. Stop/cleanup has a
five-second bound. Missing data, caps and cleanup uncertainty remain explicit.
The evidence artifact retains only the sanitized summary from this trace. No privileged
profiler or user computer is used. Source checks and mocked protocol results do
not establish a successful capture or UI acceptance.

The original capture is consumed by [run 37412087888, job 112112789417](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37412087888/job/112112789417).
It reproduced the unchanged initial animation-frame failure. During drain, the
16 MiB input limit stopped collection; Chromium also reported data loss. The
summary retains 18,074 event counts and 4,096 sanitized records, so it is
incomplete and does not establish the cause of the stall. Stop and completion
were acknowledged, the tracing session detached, and the owned browser exited.
Recorded drain time is 743.8 ms. No raw trace stream or raw trace arguments were
written. The ordinary process log retains the temporary loopback control URL
emitted by Playwright DEBUG. That browser endpoint is closed; the earlier
no-URL claim was incorrect for those process logs.

The original request `7c47e15a-8186-430c-bc15-56736d2d8a6f` is retired. Do not
reuse its activation record or its consumed one-run approval. The separately
approved replacement uses request `3e875a33-496f-4054-a609-af4970a51fe1` and
`.github/diagnostic-activations/ui-frame-trace-loaded-20261006.json`. This request
is also consumed. Its source authorization is disabled and its activation record
is retired. Do not rearm or rerun either consumed request. No further capture is
authorized. The original UI assertions and deadlines remain active.

The first activation for this replacement reached preparation in
[run 37427839671, job 112166466744](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37427839671/job/112166466744).
The empty `DEBUG` import check failed before the persistent claim, browser launch
or trace. That activation record was retired and left the capture allowance unused
at that point. Its failure artifact
is `editor-ui-parity-browser-qualification`, ID `11397631830`, SHA-256
`a2ccba410c6c931d7ce1656488853c57520b7cba670fce9d0aff43d862b2983a`.

The next activation selected source `1eb898c77020a15012fdb918b2cfdac2db4577c6`,
which preceded the required native DLL staging correction. GitHub superseded
[run 37437468558](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37437468558)
before any job started. The completed run is cancelled and has zero jobs. It
created no claim, browser or trace. Its activation record was retired, and the
capture allowance remained unused at that point.

The replacement capture was consumed by
[run 37439311782, job 112216652475](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37439311782/job/112216652475).
The player reached the loaded initial checkpoint. `Tracing.start` did not
acknowledge within its three-second start bound, so the recorder aborted with
`stage: start` and `reason: Budget`. It retained 32 sanitized events and 23,304
accounted input bytes. Stop and completion were acknowledged. The session
detached and the owned browser exited; no shutdown uncertainty was reported.
Drain time was 41.4348 ms. The four-file report/log artifact contains no raw trace,
browser debug log or WebSocket control URL.

The trace is incomplete. Its abort assertion stopped the UI diagnostic before
the ordinary initial two-frame check, so it does not establish the cause of the
original frame stall or UI acceptance. All recording, drain, size and assertion
limits remain unchanged. The replacement artifact is
`editor-ui-parity-browser-qualification`, ID `11403577899`, SHA-256
`a421ef20f51e44b09cd7dcfa03b6d5751145a5b58890e60c44f9b13ab73f705b`.

The original capture artifact is `editor-ui-parity-browser-qualification`, ID
`11390519203`, SHA-256
`ca089afa7d6d86916ba5939f9960427fbfca90cc83638c869769157c84d618b5`.

After a failed Advanced application check and successful closure of its browser,
the harness starts a separate Chromium process with the same launch options.
It verifies the actual selected native shader against the recorded module hash and
replays the actual entry point, constants, explicit binding layouts and device
requirements. This compile-only diagnostic has its own 45-second native compile
bound and reports identity/timing without retaining shader source. It helps
distinguish compilation cost from application activity; its result cannot change
the failed application result or qualify a rendered frame.
After that control process fully closes, one further fresh process compiles the
existing published `advanced::shade-uber-native` consumer. It verifies the same
manifest's control identity and the comparison's real schema, complete 40-entry
binding contract, compiler and capability requirements. Both arms keep the same
device requests and 45-second bounds. This compares a combined helper/body and
texture-bank difference. When the selected control is the optional single-sample
native companion, the harness first verifies its full counterpart's descriptor
and WGSL, then checks the Uber contract against that full descriptor. The timing
comparison therefore includes both modifier specialization and the Uber body/
texture-bank difference. It does not execute a third GPU arm or substitute an
application material. Profiles without the matching single-sample native ABI
report the comparison as unavailable. Two timeouts
are censored observations, and any missing artifact or contract mismatch leaves
the comparison unavailable rather than changing the failed application check.

For `--game-only --game-kind advanced-rendering-parity`, the opt-in
`--native-compile-trace` records browser-wide `gpu.dawn` events in the isolated
native control process. It leaves the application and both compile deadlines
unchanged and does not trace the Uber comparison. The existing artifact folder
receives a raw JSON trace capped at 16 MiB; `nativeTrace` in the smoke report
records browser revision, supported categories, verified module identity,
backend, data loss, transport/setup completeness and cleanup outcomes.
The trace buffer is 4 MiB; stop/flush/drain has a separate five-second bound.
Summaries inspect at most 100,000 events and retain at most 256 known stage
events, omitting arbitrary trace arguments. Argument filtering is requested
for the raw capture; capture errors are reported without protocol error text.

Clock-sync markers are best effort and never block the compile callback or its
watchdog. Matched IDs provide request/acknowledgement bounds, not GPU barriers;
missing or filtered markers leave cross-clock correlation unavailable.
The page retains its existing device disposal before Node cleanup, and the
trace is drained before context/browser closure, so evidence can include cleanup.
The summary does not derive pre-deadline durations or pair begin/end events.
Native event presence and unfinished-event JSON preservation require actual
capture inspection. There is no verified span around `vkCreateComputePipelines`;
residual native initialization time cannot be attributed to SwiftShader alone.
Trace success never changes rendering acceptance.

The same opt-in makes two direct `SystemInfo.getProcessInfo` reads through the
existing CDP session: near the compile callback and at 44 seconds of the unchanged
45-second watchdog. Each read has a 500-ms bound and neither blocks the callback
or watchdog. A delayed second read is skipped if less than its query budget
remains; deadline, early completion and cleanup cancel future reads. Pending
reads settle within the existing trace cleanup budget. `nativeTrace.processCpu`
retains only the unique GPU PID and cumulative CPU seconds, request/response
timestamps, read failures, PID replacement, decreasing counters and Node cleanup
overlap. A delta requires two valid reads of the same PID; its elapsed interval
is bounded by their request/response times. The counter covers every thread in
the GPU process. It cannot identify worker CPU, compiler stages, or the cause of
low activity. Page disposal can precede the Node cleanup marker, so page cleanup
overlap cannot be excluded. This observation does not activate CPU sampling.

The diagnostic's static Core scene host creates no production `RuntimeWorld`,
never begins play, and requests no physics backend. It validates engine rendering
and shader interpretation only. Full `DefaultRenderPipeline`, textures, shadows,
skinning, desktop preservation, mobile lifecycle, and performance remain separate
gates. The harness never promotes the diagnostic pipeline as a production fallback.

Primary tool references: [Playwright browser selection](https://playwright.dev/docs/browsers),
[Playwright browser launch](https://playwright.dev/docs/api/class-browsertype),
[Chromium SwiftShader modes](https://github.com/chromium/chromium/blob/main/docs/gpu/swiftshader.md),
and [Chrome headless WebGPU qualification](https://developer.chrome.com/blog/supercharge-web-ai-testing).

## Consumed native GPU capture

The single attempt on October 5 is complete. Run
[`37283428635`](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37283428635)
reported `NativeCommandUnavailable` before collector launch and produced no
samples. Its authorization marker was consumed and raw deletion was verified.
The request, activation and one-shot workflow are removed. The
[investigation record](../../docs/work/investigations/rendering/browser-native-compiler-stall-2026-10-05.md)
preserves the exact provenance and limits; this result does not diagnose a
compiler region or qualify Advanced rendering.

Normal Linux CI records bounded, unprivileged package-query and FIFO-creation
outcomes in `native-profile-prerequisites.json`. This informational check runs
no collector, privileged command or browser attachment and does not reactivate
the consumed attempt.

`--native-owned-profile-once` is an inactive-by-default diagnostic for the
Advanced software game-only route. It does not enable profiling in ordinary
push CI or on a developer's computer. The former one-shot workflow first
verified its immutable request and existing published bundle. A separate
activation must bind that request to the exact checked-out commit and Actions
run ID; a different run, rerun or changed request is rejected. Only the first
attempt can consume the exclusive runner marker.

The approved collector uses the already-installed perf binary through one
fixed sudo/timeout command. It targets about eight seconds of user-space
sampling on the isolated browser's verified GPU threads; the root supervisor
sends INT at nine seconds and KILL one second later. It changes no security
settings and does not install a profiler. Process identity is rechecked after
disabled attachment and before enable. Incidental kernel-symbol metadata is
discarded before analysis input is written. Unknown layouts, ownership changes,
missing tools and incomplete cleanup fail closed without another capture.

Raw data and control pipes stay outside upload paths and must be deleted before
summary admission. The workflow suppresses ordinary harness logs and uploads
only the allowlisted `owned-native-profile-summary.json`, capped at 128 KiB.
An unverified privileged exit or cleanup interrupts the diagnostic, skips the
comparison arm and forces process exit 86. The original application verdict and
45-second compile budget remain unchanged; profiled timing is diagnostic
evidence, not performance acceptance. Stripped binaries may identify only
modules or code regions.

Any later capture requires a new explicit scope decision; editing or re-adding
the request is not an automatic retry.

## Work-note-only pushes

The portable browser workflow skips a push when its only changes are Markdown
work notes under `docs/work/` or the existing diagnostic request/activation
records. Pull-request and manual triggers remain unfiltered. A mixed push with
source, helper, project or workflow changes still runs within
[GitHub's path-filter evaluation rules](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#git-diff-comparisons).
The new pattern matches only Markdown names. JSON, TSV and other file extensions
in the work-note tree do not match it.
GitHub currently evaluates only the first 3,000 files in a generated diff, so
this path filter is not an absolute guarantee for arbitrarily large pushes.
Required workflow checks can remain pending when a path filter skips their
event; the unchanged pull-request trigger avoids adding that filter to PR checks.

This scheduling change does not alter qualification assertions, time limits,
runner permissions, or the independent one-run diagnostic activation gates.
