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

An Editor installation that supplies its normal engine assets and the
`BrowserPublishing` sidecar can build the browser
target outside a source checkout. The Editor publish copies the portable source
project closure, build policies and generators, browser web assets, and the
reviewed browser Jolt managed source and native archives with their pins and
license notices. Browser builds select that payload beside the Editor; source
builds continue to use the checkout. The Editor publish builds the sidecar in a
sibling staging directory and replaces the previous manifest-owned payload only
after every file is copied and hashed. A prior payload with added or changed
files is left in place and raises `BrowserPublisher.PayloadModified`. The
packaged Editor checks the complete manifest before browser builds, and writes
browser graph outputs under the project's `Intermediate/BrowserPublishing`
directory so installed source files can remain read-only. The game project's
existing compiled assembly path is preserved. The sidecar excludes build
caches, launch profiles, and desktop native binaries. It uses the Editor's
ordinary `Build/CommonAssets` asset tree rather than carrying another copy.
The sidecar target does not yet copy that normal asset tree; standalone Editor
packaging must supply it separately before the installation is complete.

To prepare an Editor package that supports browser publishing, use the pinned
SDK in `global.json`, install its `wasm-tools` workload, and prepare the approved
Jolt source before publishing:

```powershell
dotnet workload install wasm-tools
pwsh Tools/Dependencies/Prepare-JoltBrowserManaged.ps1 -OutputDirectory Build/Dependencies/JoltBrowser/managed
pwsh Tools/Dependencies/Build-JoltBrowser.ps1 -OutputDirectory Build/Dependencies/JoltBrowser/native
dotnet publish XREngine.Editor/XREngine.Editor.csproj -c Release -p:JoltBrowserManagedSourceDirectory=<prepared-jolt-root>/managed/staged -p:JoltBrowserArchiveDirectory=<prepared-jolt-root>/native/archives
```

The native command uses Emscripten's internal Python tooling, CMake and Ninja.
The Editor publish checks the native source/compiler pin and required
source/notices, then records content hashes for the copied source and archives.
The native pin does not certify byte-identical archives across platforms. A
packaged Editor still needs the pinned .NET SDK,
`wasm-tools`, and PowerShell on the machine that publishes a browser project;
it does not install them. See the
[Jolt supply record](../../design/platform/jolt-browser-native-supply.md).
Agent validation uses its reserved output directory and explicit MSBuild path
properties instead. Desktop Jolt package supply and the desktop physics default
are unchanged. Browser restore/publish sets `XREngineJoltBrowser=true` globally
so NuGet resolves the reviewed source binding throughout the graph.

### Packaged build-graph execution (2026-10-03)

The genuine `BuildCurrentProjectSynchronously` browser chain completes through
the compiled Editor methods with the installed sidecar selected: portable game
build/load, authored-world export, native-WASM publication, exact linked-game
MVID and managed-closure checks, content packaging, launch configuration and
atomic activation. The authored world remains unchanged. Browser compilation
uses a frozen portable source payload and project-owned artifacts, and the
installed-source manifest remains valid afterward.

This portable Linux invocation exercises the production publisher methods; it
is not the Windows Editor CLI or a browser-render acceptance run. Normal engine
assets are supplied explicitly for this check. Standalone Editor packaging still
needs to include its ordinary `Build/CommonAssets` tree. The pinned browser SDK
uses the configuration-only managed output pivot, such as
`Artifacts/bin/XREngine.Browser/release`; assuming a `_browser-wasm` suffix was
rejected by the actual closure check and corrected.

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

Projects may explicitly select `BrowserSharedWorldPackageManifestPath` to extend
a verified self-contained native package with the cooked browser catalog and
payloads. Both representations then use the same recomputed immutable package
identity for managed admission. Ordinary local publishing does not opt in.
See [shared world package publishing](../../../developer-guides/networking/browser-shared-world-package.md)
for the bounded native profile, output handoff, integrity checks and remaining
runtime qualification.

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

Before activating content, rendering admission checks the packaged startup
output, effective project/default-user AA and GI overrides, camera output and
pipeline selections, material pass routing and canvas raster requirements.
Failures identify the startup setting or scene path, material/source pass when
applicable, and the unsupported requirement. An opaque deferred material remains
admitted through the web command chain's explicit deferred-to-forward route.
Stencil, multisample coverage, per-target blending, omitted scene pass families,
and scene materials on the display-debug callback pass are rejected.

Camera effect admission uses a detached WebGPU schema without constructing a
desktop pipeline or rebinding the authored camera. Missing values select manual
artist exposure and the schema's enabled GTAO/bloom defaults; explicit saved
automatic exposure, alternate tonemapping, unsupported effects or AO methods
remain publish errors. Selected effects require their exact manifest programs.
Pipeline descriptors are checked by the same immutable catalog as runtime.
The startup AA default is None only when both packaged overrides are absent.
Packaged GPU-dispatch selection follows the same user-over-project cascade and
rejects an effective GPU-driven request before publishing.
The default probe/IBL mode still admits global ambient without actual probes.
Runtime-created game features, launch-time quality selections, and dynamically
changed settings remain subject to the runtime backend checks; this bounded
preflight does not certify arbitrary game code or all renderer features.

The 2026-10-02 bounded cook replay passed the Editor Release build with zero
warnings/errors and reused the ordinary authored textured world unchanged
(`08f7f9c9ce25523570a537e62b98f5190daf70b8605b94e789aabc0a87842589`).
The existing production-cook driver retained its source fragment stage, produced
the source-free textured carrier with all shared texture aliases, and packaged
80 assets and 39 shaders. Copied startup fixtures rejected explicit MSAA and
GPU dispatch with setting/pass/reason diagnostics; explicit default-user AA None
over project MSAA remained admitted. These are cook-path checks, not new browser
rendering acceptance.

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
Production hosting, complete capability reporting, packaged-editor end-to-end acceptance,
audio/input/UI coverage, desktop comparisons and physical-device qualification
remain tracked in the [active runtime plan](../../todo/platform/unified-desktop-browser-runtime-todo.md).

### Browser physics capability admission

The source audit now examines configured backend-neutral rigid bodies and
character controllers as well as desktop-only component assemblies. It rejects
ignored PhysX body flags, dominance/owner metadata, incompatible packed collision
masks, contact/sleep and custom COM/inertia settings, distinct per-shape dynamic
friction/restitution/damping, unsupported geometry adapters, and native runtime
material objects. Both character-controller component families currently fail
with `BrowserCook.ControllerFilteringUnsupported`: PhysX movement uses unfiltered
manager-wide sweeps while Jolt applies the controller group/mask. Neither default
settings nor lower-sixteen-bit representability guarantee equivalent selection
against all admitted rigid bodies. Desktop behavior is unchanged; a future
portable controller contract must cover sweep queries and simulation filtering,
not only encode a different group number. Diagnostics identify the scene path, component and
feature. Defaults that the Jolt adapter intentionally replaces remain admitted;
this does not claim numerical equality between physics backends. Collision
geometry that needs native runtime generation must be baked before publishing.
Game code can still request a later unsupported runtime service, which retains
its named runtime failure rather than being predicted by this authored-data
audit. Integrated Editor compilation passes with zero warnings/errors. Representative
negative-world cook acceptance remains for the end-to-end milestone.

The real Windows Editor CLI subsequently reached canonical RollingBall cooking.
Its all-ones collision mask exposed an overly strict high-word check. The audit
now compares exact effective lower-sixteen-bit masks after each backend's empty
sentinel, within Jolt's sixteen-group packed profile. Neutral high words no
longer reject, while non-equivalent masks still do. Distinct neutral static and
dynamic friction remains the existing approved Jolt single-dynamic-coefficient
mapping, with a named cook warning; no physics runtime behavior changed.

The corrected compiled Editor then passed `BrowserBuildState.Prepare`,
`ExportAuthoredWorld`, and the real content packager against the unchanged
canonical RollingBall snapshot containing those masks and friction values. Its
existing fifteen-shader catalog produced 32 assets and 31 deduplicated payloads.
This is a cook/package check, not another full WASM publication or rendered-game
claim. The current expanded sample catalog separately contains 33 immutable
artifacts, 23 material variants, nine pipeline passes and one compute kernel.

### Native shared-package positive publication

The corrected publisher also accepts the unmodified native server-generated
base-world package. Its UTF-8 BOM is removed only for text inspection, and the
typed inline game mode and owned ID-only transform retain their native meaning.
The original package world, project world and emitted native world remain
byte-identical: 1,406 bytes, SHA-256
`18d9b1e092920c76fb92db098e66884fca67b4e2b2ce35bd4273f1c24b130e09`.
The compiled Editor preparation, preflight/cook, content packaging and shared
manifest publication all pass. Native manifest/file verification and the actual
browser `readSharedWorldPackage`/`validateSharedWorldPackage` functions agree on
`sha256:5f8c50a3a9b717f621ddcab2cd8eeb03c55dcff92ca3280fb855c5281789f9d0`.
The browser validator checks four declared files with five read-only local fetches
under Node; this does not claim a real server join or browser GPU execution.
