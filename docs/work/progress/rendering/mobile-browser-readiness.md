# Mobile browser readiness

**Reviewed source:** `9fee4b983efda6f3f79ac107eba2137a5ab8c5fa` (2026-09-28 review).
**State:** Historical source audit and delivery contract. A later [portable browser scene boot](portable-browser-scene-boot.md) implements and validates the minimal scene path; device qualification remains open.

## Scope and reconciliation

The first delivery is a .NET 10 browser application presenting to one WebGPU
canvas. It uses the engine scene/component/transform lifecycle and renderer
contracts. It excludes the editor, native XR, multiplayer and voice initially.
The package contains WebGPU only: a forced WebGL2 request must report that the
module is absent; `Auto` must report that no fallback is packaged if WebGPU
initialization fails. Do not report an unavailable fallback as success.
Independent WebGL2 support remains required for the full
[companion design](../../design/rendering/browser-wasm-renderer-design.md).

Compared with the TODO's `4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63` baseline:

- The mobile runtime TODO is now in the repository. Its checklist has not been
  qualified in a browser.
- Project files, shared build props/targets, shader compilation contracts,
  backend creation contracts and the browser design are unchanged. No browser,
  shared-browser or WebGPU renderer project has appeared.
- Runtime source has changed, including bootstrap initialization, engine timing,
  visibility-generation coordination, native XR, texture streaming and GPU-scene
  publication. Preserve these changes when extracting code; use the current
  source rather than applying a patch against the old review.
- `Engine` initialization now explicitly assigns render/window threads and
  suppresses settings cascades while constructing settings. That addresses a
  desktop initialization concern; it does not establish browser scheduling.

Reproduce the bounded reconciliation with:

```sh
git diff 4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63 9fee4b983efda6f3f79ac107eba2137a5ab8c5fa -- '*.csproj' Directory.Build.props Directory.Build.targets '*ShaderCompile*' '*RendererBackendCreateContext*' docs/work/design/rendering/browser-wasm-renderer-design.md
git diff --name-only 4a0d4a2f6a815040b6ab3a4847f9aff995c3ab63 9fee4b983efda6f3f79ac107eba2137a5ab8c5fa -- XREngine.Runtime.Core XRENGINE.Runtime.Core XREngine.Runtime.Rendering XREngine.Runtime.Bootstrap XREngine.Data
```

## Reproducible dependency inventory

Run the standard-library-only Python 3.10+ tool from the repository root:

```sh
python3 Tools/Reports/audit_browser_dependencies.py
```

It reports the union of source-declared project references starting at Core,
Rendering and Bootstrap, direct package declarations, conditions, frameworks,
imports, build targets and copied output items. This deliberately exposes the
desktop composition's extra dependencies. It is **not** the selected browser
publish graph. Use repeated `--root` arguments to inspect an extracted boundary.
Use `--output <report.json>` to retain evidence under the repository's approved
validation directory; stdout is sufficient for review.

The report is deterministic for unchanged inputs, retains source hashes, and
records missing submodule projects and unresolved MSBuild expressions. Conditions
are preserved, not evaluated. It does not restore NuGet, enumerate transitive
NuGet runtime assets, discover SDK-injected references, establish source
reachability, or certify WASM compatibility. None of those omissions may be
treated as an empty dependency set. The committed
[source inventory](mobile-browser-dependencies.json) captures this review.

Before accepting the portable graph, evaluate each selected project with the
actual .NET SDK/configuration and browser RID. MSBuild's `-getProperty` and
`-getItem` support evaluation without executing targets:

```sh
dotnet msbuild <browser-project.csproj> -p:Configuration=Release -p:RuntimeIdentifier=browser-wasm -getProperty:TargetFramework,RuntimeIdentifier -getItem:ProjectReference,PackageReference,Compile,Content,None
```

Then restore/publish that graph and inspect `project.assets.json` and published
files for native/runtime assets. Record SDK/workload versions and all imports.
Reference: [MSBuild evaluation output](https://learn.microsoft.com/en-us/visualstudio/msbuild/evaluate-items-and-properties).
No SDK/workload or dependency version is changed by this work.

## Extraction ownership and hazards

Owners below are implementation responsibilities, not assigned people. A
portable candidate still needs trimming/AOT and live-browser validation.

| Source boundary | Classification and browser action | Owner |
| --- | --- | --- |
| `XREngine.Runtime.Core` | Browser-replaceable boundary: extract existing scene nodes, transforms, components, scheduling and service contracts. Current Windows target and native packages cannot be imported whole. | Runtime/scene |
| `XREngine.Runtime.Rendering` | Browser-replaceable boundary: extract generic resources, catalog, presentation, output and capability contracts; keep API wrappers, windows, media and XR in native leaves. | Rendering |
| `XREngine.Data` | Mixed: retain portable data/identities; split DirectStorage, drawing and audio implementations. `net10.0` alone is insufficient. | Data/assets |
| `XREngine.Extensions` | Mixed: retain portable helpers; isolate Meshoptimizer native use in cook tooling. | Data/cook |
| `XREngine.Runtime.Bootstrap` | Desktop-only composition; reuse its lease/static-registration patterns, build a separate explicit browser composition root. | Application host |
| Animation and animation integration | Portable candidates after selected animation path and registrations are audited; include at the representative-world gate. | Animation |
| Audio, input and their integration projects | Browser-replaceable: DOM input and browser audio services; do not import native device providers. | Browser services |
| OpenGL, Vulkan and OpenVR projects | Desktop-only; exclude from browser publication. Native OpenXR is also excluded. | Native rendering/XR |
| ControlPlane | Excluded from local scene/frame sample; do not inherit it through desktop bootstrap. | Application host |
| Model import, modeling, image/font/mesh processing | Offline-cook-only for the selected browser sample; browser loads cooked bytes. | Asset cooker |

Declared-package disposition (covers all 99 unique names in the snapshot):
`*` below matches the declared family members in the JSON inventory, not an
approval for future packages. Classification describes intended use in this
sample, not a package's universal platform support. Portable candidates require
usage, transitive runtime-asset and browser verification before admission.

| Declared packages | Classification / action | Owner |
| --- | --- | --- |
| BitsKit, DotnetNoise, K4os.Compression.LZ4, LZMA-SDK, MemoryPack, Newtonsoft.Json, SharpZipLib, System.IO.Hashing, YamlDotNet, ZstdSharp.Port | Portable candidates; retain only used data, math and serialization paths. Validate reflection/generator and codec reachability. | Data/serialization |
| MathNet.Numerics, Silk.NET.Core | Portable candidates; retain needed math/data types only, audit API/native loaders. | Rendering/data |
| ImmediateReflection | Browser-replaceable discovery; prefer existing static registrations for included types. | Composition |
| AssimpNetter, Magick.NET-Q16-HDRI-x64, Meshoptimizer.NET, MIConvexHull, SharpFont.Dependencies, SharpFont.NetStandard, SkiaSharp, Svg.Skia, System.Drawing.Common | Offline-cook-only for sample; replace runtime image/font/import needs with cooked data. | Asset cooker |
| GraphQL, Google.Cloud.Speech.V1, Google.Cloud.TextToSpeech.V1 | Excluded from selected local/network sample; no browser cloud credentials. | Application services |
| JoltPhysicsSharp, MagicPhysX | Browser-replaceable collision service; isolate native implementation. | Physics |
| Jitter2 | Excluded from initial scene/frame; assess managed physics separately before selecting playable collision implementation. | Physics |
| NAudio, NAudio.Lame, NAudio.Sdl2, NAudio.Vorbis, NVorbis, Silk.NET.OpenAL* | Browser-replaceable audio service; cooked audio and browser playback initially. Managed codec reuse remains separately auditable. | Audio |
| DXNET.XInput, Silk.NET.GLFW, Silk.NET.Input*, Silk.NET.SDL, Silk.NET.Windowing*, Silk.NET.XInput | Browser-replaceable DOM input/canvas host. | Browser host/input |
| Silk.NET.DirectStorage, Silk.NET.DirectStorage.Native | Browser-replaceable async asset byte/stream loading. | Assets |
| Silk.NET, Silk.NET.Core.Win32Extras, Silk.NET.Direct3D*, Silk.NET.OpenGL*, Silk.NET.OpenGLES*, Silk.NET.OpenXR*, Silk.NET.Vulkan*, Silk.NET.WGL*, Raylib-cs | Desktop/native API leaves excluded from WebGPU package. WebGL2 JS integration is a separate workstream. | Native rendering/XR |
| Silk.NET.Shaderc | Offline-cook-only compiler; browser consumes artifacts. | Shader cooker |
| FFmpeg.AutoGen, ImGui.NET, MathNet.Numerics.Providers.CUDA, System.Management, UltralightNet, UltralightNet.AppCore | Desktop-only integrations excluded from this sample; browser UI uses explicit browser services. | Media/editor/native host |

Specific source hazards requiring extraction review:

- `XREngine.Runtime.Core/Imaging/MagickRuntimePolicy.cs` has a module initializer
  that applies ImageMagick limits. Moving only call sites leaves assembly-load
  behavior; move the initializer with the native imaging implementation.
- `XREngine.Runtime.Bootstrap/Engine/Engine.cs` installs concrete services from a
  static constructor. Browser scene construction must not touch this facade to
  obtain apparently generic services.
- `RuntimeApplicationBootstrap`, `RuntimeAssetBootstrap`,
  `RuntimeAdapterBootstrap`, and `RuntimeRenderingBootstrap` install asset,
  serializer, world, rendering, window, video, VR and subsystem services.
  Even the headless composition is not proof of browser safety.
- `XREngine.Runtime.Core/Scene/SceneNode.Lifecycle.cs`,
  `Scene/Components/XRComponent.cs`, and `World/RootNodeCollection.cs` supply
  the real begin-play path. Preserve that path and public serialization/type
  identities rather than building an unrelated demonstration scene graph.
- `RuntimeRenderThreadHost` owns native pumping/blocking/thread lifecycle.
  Browser scheduling needs a single-frame entry driven by animation callbacks.
- Core's `EnsureCoACD`, Rendering's native copies/FreeType/FFmpeg setup and
  Bootstrap's factory generator require separate build/runtime ownership.
- `Directory.Build.targets` can copy yt-dlp and NVIDIA native binaries for
  executables. The future browser executable must disable shared executable
  dependencies and verify its actual publication contents.
- Factory/serializer registration must be limited to included types; inspect
  generated output, reflection/dynamic code paths and retained assemblies.
- Case-distinct `XRENGINE.Runtime.Core` and `XREngine.Runtime.Core` trees exist.
  `XRENGINE.Runtime.Core/World/RuntimeWorld.cs` is outside the canonical project's
  default source glob, with no explicit include found. Verify evaluated `Compile`
  items on the selected build platform before moving source; presence in the
  tree does not prove project inclusion.

## Deterministic sample contract

[mobile-browser-sample.json](mobile-browser-sample.json) specifies a **planned**
fixture, not a runnable scene asset. It uses engine-owned procedural/cooked
geometry and textures so no external model, licensing purchase or desktop
importer is required at runtime. Preserve seed, authored transforms, camera and
animation sample times across backends.

The scene-boot subset constructs a root and parent/child transforms, adds a real
component, records begin/update/end lifecycle, advances 120 fixed 1/60-second
steps, checks transform propagation, then tears down. It needs no GPU.
The first-frame subset adds the camera, static meshes, depth and unlit texture
material with explicit `CpuDirect` submission. This is a deliberate first-frame
profile; it does not satisfy the later compute/indirect capability gate.
The representative subset adds skinning, opacity modes, light/shadow, environment
and basic UI. A browser bridge triangle alone satisfies none of these gates.

## Devices and measurable budgets

Qualification targets: one physical iPhone 15 running Safari and one physical
Pixel 8 running Chrome. These are proposed reference models, not devices known
to be available to the maintainer. Hardware availability and exact OS/browser
builds remain pending. Record actual versions, adapter features/limits and
thermal/power state at execution. Both rows are **not run**. Desktop automation
is supplementary. Feature/adapter acquisition must determine support; do not
infer it from model names or user-agent strings.

These initial engineering limits are adjustable only with a recorded rationale
before qualification. They are not measured promises. Use the representative
sample, production HTTPS delivery, a 20-Mbit/s/80-ms-RTT network profile for cold
loads, fresh cache for cold runs, retained cache for warm runs, and a 10-minute
foreground session after 30 seconds of warm-up. Record ten startup runs and
frame-time p50/p95/p99. Both reference devices must pass independently.

| Metric | Initial acceptance limit |
| --- | --- |
| Initial compressed application + sample transfer | 20 MiB |
| Cold useful frame / interactive input | 12 s / 15 s |
| Warm useful frame / interactive input | 3 s / 4 s |
| Frame-time p50 / p95 / p99 | 16.7 / 20 / 33.3 ms at a 60-Hz target |
| Simulation + collection + packet CPU / JS executor CPU, p95 | 4 / 2 ms |
| Output resolution | DPR capped at 1.5, longest backing edge at 1280 pixels, preserve aspect |
| Sample complexity | 100k triangles, 128 draws, 32 materials, one 64-bone skinned mesh |
| Textures | At most 16 at 1024 squared; at most 64 MiB estimated residency including mips |
| Managed heap / decoded content / staging / estimated GPU memory | 128 / 64 / 32 / 128 MiB steady; total accounting peak at most 512 MiB |
| Frame command packets / upload arena | 4 / 16 MiB resident; hard caps 8 / 32 MiB; overflow rejects frame and reports required capacity |
| Uploads | 256 KiB/frame steady, 4 MiB/frame streaming burst |
| Resource creation | Zero steady-state GPU creations; at most 8/frame while streaming |
| Ordinary frame managed/JS crossings | At most 4, unchanged between 1 and 128 draws |
| Render allocations | Zero recurring per-draw allocations; log all remaining per-frame bytes |
| Device recovery | Useful frame within 5 s after replacement device is available |
| Recovery retention | No monotonic resource-count growth across 10 recoveries after completions retire |

Browser total memory is not uniformly observable. Label CPU/JS/GPU estimates,
allocator counters and device measurements separately; unavailable measurements
are unresolved, not zero. Gate acceptance must include inspected image output,
shader/layout correctness and lifecycle diagnostics as well as timings.

## Required services and gate evidence

| Service | Local playable world | Networked client |
| --- | --- | --- |
| Scene lifecycle, transforms, timing, async cooked assets, failure UI | Required | Required |
| WebGPU rendering, resize, suspension, loss recovery | Required | Required |
| Touch camera/navigation, UI focus, skinned animation | Required | Required |
| Collision | Required for floor/wall navigation; browser implementation selected separately | Required |
| Audio | Required sample cue after user gesture; denied/suspended state visible | Required |
| Native physics parity, GI, editor, video, native XR | Excluded | Excluded unless separately requested |
| Session/auth, production replication, bounded transport, reconnect | Excluded | Required |
| Microphone/voice | Excluded | Optional for initial network sample; must be explicitly selected before claiming voice support |

Required services either initialize or produce a blocking named diagnostic;
silently installing no-op implementations is not acceptance. Permission-dependent
audio remains visibly pending until interaction. Network sample targets: 30-Hz
state updates, coalesced replaceable state queue at most 256 KiB, reliable control
queue at most 1 MiB, application state age p95 at most 200 ms on the declared
network profile, resync within 5 s after transport/auth restoration. These need
validation with the production server and are not transport guarantees.

Before accepting scene boot, record clean publish, evaluated dependency/runtime
asset closure, actual browser lifecycle/transform evidence and relevant desktop
regression evidence. Before accepting the first frame, additionally inspect
camera/mesh/material/resource-wrapper/packet/output execution, depth, resize and
unsupported-device diagnostics. Physical-device qualification remains mandatory
for the playable release.

## Remaining acceptance and validation

The audit tool and planning records implement the first workstream's reviewable
deliverables. The complete resolved transitive package/runtime closure, source
reachability, hardware availability, exact device versions and real browser
results remain open. The active
[TODO](../../todo/rendering/mobile-webgpu-runtime-todo.md) records partial items
without marking these missing results complete. The next implementation work is
portable scene/runtime extraction, followed by browser hosting and scene boot.

No C# runtime, desktop renderer, package, submodule or build hook is modified by
this change. A desktop/browser runtime build is therefore not claimed as
validation of these planning artifacts. Material AI assistance was used for the
tool and documentation.

Validation of this change: the audit ran against the reviewed checkout and
reported 17 available project files, 99 distinct declared package names and two
missing submodule projects (OpenVR.NET and OscCore-NET9). Repeated runs were
byte-identical; project source hashes, sample JSON and relative document links
were checked. Invalid explicit roots produce an argument error. No browser,
physical-device, restored NuGet graph or C# build result is asserted.
