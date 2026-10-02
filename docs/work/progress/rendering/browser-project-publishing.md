# Browser project publishing

**Status (2026-10-02):** shared-engine integration is in progress. The Editor
cross-build, Server and control-plane builds pass with zero warnings/errors.
Actual Chromium CI qualifies bounded depth, texture/sRGB, lit/HDR, standalone
directional PCSS, native Jolt, asset/world lifecycle and offline-audio paths.
The [shader acceptance record](unified-webgpu-shader-cooking.md) links exact
commits and captures. Local Chromium still cannot create its required Unix
socket. These results do not establish an editor-published playable project,
physical-device acceptance or performance parity.

## Shared desktop entry point

Select `BrowserWebGPU` in the existing project `BuildSettings.Platform`, then use
Build Project. The headless entry point remains:

```text
XREngine.Editor.exe --build-project <project-file> --build-platform BrowserWebGPU --build-configuration Release --output-subfolder Browser
```

The existing builder retains settings, save-before-build, jobs, cancellation,
diagnostics, output-directory ownership, sibling staging, and rollback. It builds
the portable game assembly, loads the saved startup world, cooks that engine
asset with the existing registered codec, publishes the shared engine/game
assemblies and platform leaves, packages immutable content, and activates the
complete site atomically. It no longer calls `BrowserWorldPublishExporter` or
translates gameplay into the frozen browser scene/component DTOs.
When saved startup settings have no window target, the project may select a
saved `.asset` beneath its Assets directory with `StartupScenePath`; existing
single-window selection and conflicting-world checks remain in force.

The current publisher requires a source checkout containing `XREngine.Browser`.
Publishing from a packaged editor is still open. Use the pinned SDK/workload in
`global.json` and prepare the approved Jolt source before publishing:

```powershell
dotnet workload install wasm-tools
pwsh Tools/Dependencies/Prepare-JoltBrowserManaged.ps1 -OutputDirectory Build/Dependencies/JoltBrowser/managed
pwsh Tools/Dependencies/Build-JoltBrowser.ps1 -OutputDirectory Build/Dependencies/JoltBrowser/native
```

The native command uses Emscripten's internal Python tooling, CMake and Ninja.
It verifies exact source/compiler pins and ships notices. See the
[Jolt supply record](../../design/platform/jolt-browser-native-supply.md).
Agent validation uses its reserved output directory and explicit MSBuild path
properties instead. Desktop Jolt package supply and the desktop physics default
are unchanged. Browser restore/publish sets `XREngineJoltBrowser=true` globally
so NuGet resolves the reviewed source binding throughout the graph.

## Same assets and game code

Game libraries target `net10.0`, retain portable engine references, exclude only
their own `Editor` source subfolders, and remain untrimmed. Desktop launchers add
desktop composition independently. Browser publishing audits the game assembly
for blocked references/APIs, then statically references its project and generates
a typed bootstrap invocation using the same concrete-bootstrap resolver as the
desktop launcher. Game module/serializer registration occurs after the browser
asset source is installed, before world deserialization.

The canonical content manifest uses `schema: 1`, `format: xrengine-assets`,
virtual `/game` and `/engine` paths, exact registered type names, SHA-256 payloads,
explicit dependencies, startup world/settings, and shader sidecars. The shared
content packager owns hashes, bounds and manifest-last writes. Runtime
`AssetManager` asynchronously preloads dependencies, enforces catalog identity
and type checks, rejects stale owners and duplicate IDs, and uses the existing
cooked/YAML decoding paths. It does not fall back to desktop files or manufacture
missing asset placeholders. Custom game codecs retain their own format and need
their own allocation and platform-variant review.

Browser capability admission inspects the world after its registered serializer
has cooked and hydrated it under a thread-local WebGPU material-construction
target. The temporary graph has scoped object ownership and cannot create API
wrappers on an active desktop editor renderer. This lets an explicit game codec
reconstruct source-free built-in materials without editing its desktop YAML or
persisted binary format. Such materials require an exact semantic/profile-to-
descriptor variant in the project-relative shader manifest; custom shader
stages still require explicit WGSL companion identities. The cooker/resolver
serves verified descriptors and sources through the same asset source, and the
pipeline mappings are explicit hash-bound artifacts. No arbitrary
desktop GLSL translation or shader-name inference is performed.
See [engine shader cooking](unified-webgpu-shader-cooking.md).

The canonical Rolling Ball world now passes the complete compiled Editor
`BuildCurrentProjectSynchronously` chain through a portable Linux reflection
runner: generated portable game compilation, authored world cooking, actual
`CodeManager.PublishBrowserApplication`, content packaging, player-shell
installation, and atomic output activation. This is the real publisher and its
failure checks, not a replacement packaging implementation. It is not execution
of the Windows-only Editor CLI process.

That run exposed a startup-load defect missed by the earlier method-only cook
probe: requesting the concrete `XRWorld` base ignored the saved derived world
type. The publisher now requests abstract `XRAsset` through the existing
polymorphic deserializer and still requires an `XRWorld` result. The saved
`RollingBallWorldAsset` type and its registered version-five codec are preserved.
The source project, Assets and Config copies remain byte-identical to the
canonical sample. An initial WebAssembly task-host socket failure was resolved
with the same in-process MSBuild override already used by this restricted
validation host; the production publisher was not bypassed.

The activated static bundle contains the game WebCIL/bootstrap, 32 hash-verified
assets, fifteen shader artifacts, six material variants, and nine exact pipeline
mappings for tonemap, depth-normal, GTAO and bloom. Its published JavaScript
admits the manifest. The opt-in desktop diagnostics I/O remains behind the
desktop host, and the game passes the production metadata audit. Editor Release
builds have zero warnings/errors. Actual browser gameplay and the Windows CLI
entry point remain separate acceptance checks.

## Capability and startup policy

The source audit rejects known desktop-only components, VR-dependent components
and transforms, absent shader companions or built-in variants, and unsupported
output requirements
with contextual diagnostics. It is not yet a complete feature classifier.
The initial startup profile accepts one local mono output; VR, remote-client
startup, HDR, transparent surfaces and multiple/split outputs fail explicitly.
The selected world is loaded through `RuntimeWorld`, caller-thread engine
stepping and the Jolt leaf. The interpreter is intentional; trimming/AOT remain
measurement-dependent decisions.

Rolling Ball is the approved parity sample. Its gameplay and desktop VR host are
separate, but complete browser sample parity is not yet established. Its custom
cooked material reconstruction needs the same explicit web shader identities.
The descriptive rebrand and source asset audit are not legal clearance.

## Player and diagnostics

The output contains the WebAssembly application, module-owned JavaScript,
`content/manifest.json`, immutable payloads and `browser-publish.json`. The launch
descriptor is exactly schema 2 / `xrengine-engine-launch`, with manifest
`./content/manifest.json`. The shipping `engine-player.html` is copied to
`index.html`; it validates a same-origin, no-redirect descriptor and starts
without URL-entry controls or diagnostic query overrides. It shows loading,
errors and gesture-driven audio activation. `engine-diagnostic.html` retains the
manual development flow separately.

**Current rendering limit:** production browser startup connects the real world
to an engine viewport and canvas renderer. The shared `DefaultRenderPipeline`
has qualified bounded lit/HDR/tonemap, standalone directional-shadow and shared
debug-primitive routes. GTAO and bloom also compile and execute their actual-WASM
command path; GPU effect pixels are being qualified separately.
Authored features outside that route still fail by name; a blank input canvas
is not success. Engine-mesh diagnostics use real scene/camera/model objects and
that shared pipeline, but do not establish full authored-world gameplay.
Skinning, screen-space UI, broader feature profiles and complete gameplay
acceptance remain open.

The [browser smoke harness](../../../../Tools/BrowserSmoke/README.md) records
actual captured pixels, export startup, optional asset lifecycle and native
physics teardown. Explicit software WebGPU on CI is shader/API correctness
evidence only. Source success, skipped optional checks and hardware acceptance
are reported separately. The frozen reference runtime remains until shared
engine parity permits its approved retirement.

Serve published output over same-origin HTTPS (loopback HTTP for development)
with correct WebAssembly MIME/compression and manifest revalidation. Immutable
payload caching may be reused from [content delivery](browser-cooked-content.md).
Production hosting, complete capability reporting, packaged-editor delivery,
audio/input/UI coverage, desktop comparisons and physical-device qualification
remain tracked in the [active runtime plan](../../todo/platform/unified-desktop-browser-runtime-todo.md).
