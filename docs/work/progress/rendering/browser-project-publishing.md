# Browser project publishing

**Status (2026-10-01):** shared-engine source integration and build qualification
are in progress. The Editor cross-build, Server and control-plane builds pass
with zero warnings/errors. The browser native physics diagnostic publishes.
The local Chromium process cannot start because its Unix socket creation is
denied; the approved Actions lane will provide live browser evidence. These
results do not establish playable-project, GPU, device, or performance parity.

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

Every selected shader needs an explicitly verified WGSL artifact identity.
The cooker/resolver preserves that identity and serves its descriptor and source
through the same asset source. No arbitrary desktop GLSL translation is inferred.
See [engine shader cooking](unified-webgpu-shader-cooking.md).

## Capability and startup policy

The source audit rejects known desktop-only components, VR-dependent components
and transforms, absent shader companions, and unsupported output requirements
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

**Current rendering limit:** production browser world startup still owns a
headless engine world. The shipping page reports rendered output unavailable;
a blank input canvas is not success. The separate engine-mesh diagnostic uses
real engine scene/camera/model objects to qualify depth and per-draw uniforms.
It does not substitute for the web tier of `DefaultRenderPipeline`. Textures,
lighting, shadows, skinning, UI and full authored-world rendering remain open.

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
