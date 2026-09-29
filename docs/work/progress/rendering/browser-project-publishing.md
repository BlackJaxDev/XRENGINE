# Browser project publishing

**Status:** Source implementation. Builds, native export/cooker execution, browser
and GPU runs, and physical-device qualification remain deferred by request.

## Shared desktop entry point

Select `BrowserWebGPU` in the existing project `BuildSettings.Platform`, then use
the editor's normal Build Project action. The existing headless entry point also
accepts the target:

```text
XREngine.Editor.exe --build-project <project-file> --build-platform BrowserWebGPU --build-configuration Release --output-subfolder Browser
```

Use an editor built from this source checkout with the repository's compatible
.NET SDK and WebAssembly workload available. Browser publishing locates
`XREngine.Browser/XREngine.Browser.csproj` from the working directory or editor
location. Qualification and workload pinning are still open; this is an
implementation workflow, not an executed release recipe.

The existing project builder retains settings, save-before-build, jobs, progress,
logging and output-directory ownership. Its browser steps deserialize the saved
startup world, extract portable content, publish the browser application, invoke
the shared content packager, and write the launch descriptor. Publishing the
portable application does not build or copy the project's native game assemblies,
desktop launcher, native content archives or desktop renderer binaries.

`CookContent`, `BuildManagedAssemblies`, `BuildLauncherExecutable` and
`CleanOutputDirectory` must be enabled. NativeAOT launcher options, launcher
compile constants and desktop-only renderer selections are rejected. Desktop
copy/archive defaults are inapplicable and do not require manual disabling.
Use a dedicated output subfolder below the project's Build directory.

The complete site is prepared in a sibling staging directory. A successful build
swaps that site into the selected output folder, restoring the previous folder
if activation fails. Failure or cancellation cleans staging. This protects local
build output; remote deployment and retention of older cached payloads remain
hosting responsibilities.

## Reuse boundaries

| Responsibility | Implementation reused |
| --- | --- |
| Settings, jobs, progress and CLI | Existing `ProjectBuilder`, `BuildSettings` and editor command path |
| Child build and diagnostics | Existing `CodeManager` MSBuild process helper, now cancellable |
| Saved world loading | Native `AssetManager` deserialization of the selected startup `.asset` |
| Mesh and camera extraction | Existing browser asset/camera adapters and native transform/projection APIs |
| Texture decoding and missing mip generation | Existing native texture/image APIs; no browser importer |
| Skin weights | Canonical native packed skin buffers with palette-index remapping |
| Curve evaluation | Native curve evaluators and animation setters on detached transforms |
| Payload rules, hashes and manifest | One `BrowserContentPackageBuilder`, compiled into editor and standalone cooker |
| Wire contracts | Same cooked DTO source files in editor export and browser loading |
| Runtime playback and rendering | Existing cooked loader, animation player and registered WebGPU renderer |

There is no second runtime scene database, weight compressor, animation curve
evaluator or content packager. Native interpretation stays in the editor; the
portable runtime receives the already admitted payloads.

## Admitted authored content

The startup windows must select the same saved project world. The exporter reads
that asset afresh, rather than exporting a potentially dirty live editor world.
Visible non-editor scenes are traversed with bounded hierarchy and payload sizes.
The selected profile requires one active standard perspective or orthographic
camera. Backend clip-depth encoding, reversed depth and temporal jitter are not
serialized into the authored browser projection; oblique/custom camera policies
are rejected.

Supported static models use indexed triangles, UV0 and one LOD. Canonical native
forward unlit-color and unlit-texture shaders are recognized by their resolved
source; arbitrary shader translation is not inferred. Admitted depth/cull/color
policy, tint and ordinary resident 2D RGBA8/sRGB textures are preserved. Texture
export retains complete mip chains or uses native generation when enabled.
Repeat, mirror and clamp addressing, nearest/linear filters and supported
anisotropy share the browser sampler descriptor. Comparison/border sampling and
LOD bias are outside this profile.

The initial native animation adapter admits one automatically started generic
transform clip at normal speed and full weight on an identity-based skinned
model. It samples supported native local TRS channels, includes the final sample
to retain authored clip duration, and remaps canonical packed influences to a
parent-before-child palette. The existing bone/frame/asset byte limits apply.
Native morph export, nonidentity skin root-space policies, humanoid/IK/root motion,
import adapters, animation events and state machines remain excluded.

Unknown active components and unsupported authored material/camera/animation
policies fail with the affected scene path. Native physics, UI, audio, gameplay
scripts and arbitrary game modes are not silently removed or presented as web
support. Their browser services may already exist independently; mapping their
desktop authoring contracts is separate remaining work.

## Static bundle startup

The build output contains the WebAssembly application, module-owned JavaScript
and shader assets, `content/manifest.json` with immutable payloads, and
`browser-publish.json`. The browser loads the descriptor before starting the
runtime, so opening the deployed site loads the published world without a
`?world=` argument. Query parameters can still override the world, submission
strategy and skinning mode for investigation.

The descriptor is bounded, schema checked and revalidated. A missing or malformed
descriptor fails startup instead of launching the development demo. Repository
development content ships a descriptor with a null world. Published defaults are
balanced quality, automatic CPU-direct submission and CPU deformation; advanced
GPU paths remain explicit opt-ins pending qualification.

Serve the output over same-origin HTTPS (localhost HTTP for development) with
the WebAssembly runtime's required MIME/encoding rules. Revalidate launch and
content manifests; immutable hashed payloads may be cached. See
[cooked content delivery](browser-cooked-content.md) for cache and payload rules.
Production hosting configuration, deployment automation, full desktop content
coverage and all deferred execution evidence remain open.
