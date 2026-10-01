# Browser runtime smoke harness

This is production-path qualification tooling. It serves an already published
browser application and cooked shader artifacts, launches real Chromium, and
checks the actual engine-rendered pixels. It neither mocks WebGPU nor replaces
physics or engine scene objects. A source/build success is not a smoke pass.

The texture diagnostic uses the same engine camera, ModelComponents and frame
path with canonical UV streams and an authored `XRTexture2D`. It checks four
colored texture corners, the linear result of sampling an sRGB midpoint, and
five alternating resource replacements. Retired resources must drain without
growing the live GPU-resource count; reference-scene packets remain forbidden.
Array/cube/depth sampling, HDR, and production lit materials are not implied.

The audio check imports the published Web Audio streaming scheduler and renders
two adjacent PCM buffers with a real `OfflineAudioContext`, checking every output
sample at rates 1 and 2, processed-buffer order, and disposal. It requires no
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
  --shader-artifacts <depth-shader-cook-output> \
  --output Build/_AgentValidation/<run>/reports/browser-smoke
```

Cook `Build/CommonAssets/Shaders/WebGPU/engine-depth.recipe.json`,
`engine-depth-probe.recipe.json`, and `engine-texture-probe.recipe.json` with
`Tools/ShaderCooker`; supply the directory
containing its schema 3 `manifest.json`, hashed descriptors, and hashed WGSL. The
shader artifacts are explicit runtime inputs to the smoke, not dependencies on
an ignored prior agent run. The browser publish must include
`diagnostics/engine-mesh.html` and the production engine/runtime modules.

`XRE_BROWSER_EXECUTABLE` optionally selects an already installed Chromium. With
that variable absent the harness uses Playwright's managed Chromium and its new
headless channel. `--headed` is available for local inspection. The harness does
not download or install a browser as part of a run.

Default `--gpu-mode native` does not force a software implementation and rejects
reported fallback/SwiftShader/llvmpipe adapters. An unavailable WebGPU adapter is
a failing `WebGPUUnsupported` result, never a skipped/passed rendering check.
For a deliberately software-only trusted CI runner, use `--gpu-mode software`.
That explicitly enables developer WebGPU/SwiftShader launch switches, records
the actual adapter and flags, and labels the result software API/shader
correctness only. It is not physical-device, native GPU, or performance evidence.
Do not use software developer switches with untrusted web content.

The harness binds an ephemeral port on `127.0.0.1`. Only the supplied publish,
shader, and optional Jolt directories are mounted. Path/symlink escapes, hidden
path segments, non-read requests, and unexpected Host headers are rejected.
Browser requests to other origins are blocked and fail qualification. No server
is exposed on a LAN interface.

## Checks and evidence

- The real engine diagnostic must report all three mesh submissions. A clear-only
  page cannot satisfy readiness
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

The CI lane packages `Content/EngineSmokeWorld.asset` with the shared content
packager and requires two real engine-world start/stop cycles, each completing
at least twelve caller-thread frames. This minimal canonical YAML world contains
one scene/root transform, not a separate scene DTO. It qualifies the headless
world lifecycle and hash-verified asset path; it is not a representative game,
cooked-binary roundtrip, renderer, or physical-device parity result.

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

The diagnostic's static Core scene host creates no production `RuntimeWorld`,
never begins play, and requests no physics backend. It validates engine rendering
and shader interpretation only. Full `DefaultRenderPipeline`, textures, shadows,
skinning, desktop preservation, mobile lifecycle, and performance remain separate
gates. The harness never promotes the diagnostic pipeline as a production fallback.

Primary tool references: [Playwright browser selection](https://playwright.dev/docs/browsers),
[Playwright browser launch](https://playwright.dev/docs/api/class-browsertype),
[Chromium SwiftShader modes](https://github.com/chromium/chromium/blob/main/docs/gpu/swiftshader.md),
and [Chrome headless WebGPU qualification](https://developer.chrome.com/blog/supercharge-web-ai-testing).
