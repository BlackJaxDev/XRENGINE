# Runtime Project Organization

[Architecture](../README.md) · [Codebase guide](../getting-started-in-codebase.md) · [Portable project rules](../../developer-guides/runtime/portable-projects.md)

The runtime is split into shared managed libraries, engine integration layers, backend modules, and application composition roots. Project names describe assembly ownership; public namespaces can retain their original names across these boundaries. The former `XRENGINE` facade project and production directory are removed.

## Shared managed projects

These projects target `net10.0` and compile their full source set for desktop and browser consumers. The authoritative browser compile closure is [PortableProjects.tsv](../../../Build/Portable/PortableProjects.tsv). Membership establishes the intended compilation boundary; browser execution, trimming, and AOT require their own qualification.

| Project | Responsibility |
| --- | --- |
| `XREngine.Extensions` | Shared managed extensions and algorithms. Native mesh processing is supplied through an optional backend. |
| `XREngine.Data` | Shared data types, serialization primitives, image buffers, compression, and platform service contracts. `System.Drawing.Primitives` value types are allowed; Windows bitmap APIs are not. |
| `XREngine.Animation` | Animation assets and evaluation. |
| `XREngine.Audio` | Audio contracts, buffers, managed processing, and backend selection. |
| `XREngine.Input` | Device, action, and input-state contracts. |
| `XREngine.Modeling` | Managed mesh editing and modeling operations. |
| `XREngine.Runtime.Core` | Worlds, scenes, transforms, components, physics contracts/catalog, replication, and runtime services. |
| `XREngine.Runtime.Rendering` | Backend-neutral render objects, pipelines, windows/viewports, capture, text/UI, and renderer contracts. |
| `XREngine.Runtime.Host` | Shared `Engine` facade, `EngineTimer`, world hosting, startup settings, snapshots, and managed host-service implementations. Platform startup policy, backend catalogs, and default pipeline creation are supplied by application composition. |
| `XREngine.Runtime.AnimationIntegration` | Scene/component integration for animation. |
| `XREngine.Runtime.AudioIntegration` | Scene/component integration for portable audio services. |
| `XREngine.Runtime.InputIntegration` | Player, pawn, and scene integration for input. |
| `XREngine.Runtime.ModelingIntegration` | Runtime scene integration for modeling. |

`XREngine.Runtime.Rendering.WebGPU` and `XREngine.Browser` also belong to the compile closure. They provide the browser renderer and host; their presence does not imply that the browser host can run every engine world or desktop feature. Browser gameplay integration is tracked separately from compilation.

## Backend modules

Modules own vendor API calls, native object lifetimes, and backend-specific components. Neutral contracts remain in the shared projects. Backend modules consume those contracts rather than referencing another backend module. An application or integration host connects cooperating modules through the lower contracts.

Most desktop modules currently use Windows-targeted project configurations. Some native adapters, such as `XREngine.Input.Silk`, target `net10.0` but remain outside the portable compile closure. A neutral target framework, portable vendor library, or browser build proposal does not by itself make an engine module a supported browser backend.

| Project | Implementation ownership |
| --- | --- |
| `XREngine.Runtime.Physics.PhysX` | MagicPhysX actors, scenes, shapes, joints, controllers, debug data, GPU device selection, and PhysX extension capabilities. |
| `XREngine.Runtime.Physics.Jolt` | JoltPhysicsSharp scenes, actors, shapes, queries, controllers, and joints. |
| `XREngine.Runtime.Physics.Jitter` | Experimental Jitter2 implementation; excluded from default application composition. |
| `XREngine.Runtime.Physics.Authoring` | CoACD and uncooked collider generation for editor/cook hosts. Shipping worlds use neutral cooked geometry. |
| `XREngine.Audio.OpenAL` | OpenAL devices, contexts, sources, buffers, transport, and EFX. |
| `XREngine.Audio.NAudio` | NAudio/SDL2 output, device capture, file decoding, and LAME encoding. |
| `XREngine.Audio.SteamAudio` | Steam Audio native bindings and acoustic scene integration. |
| `XREngine.Audio.OVRLipSync` | Meta lip-sync native integration. |
| `XREngine.Audio.Audio2Face` | Audio2Face native integration and optional bridge. |
| `XREngine.Input.Silk` | Silk input device wrappers. |
| `XREngine.Input.XInput` | Windows XInput devices. |
| `XREngine.Runtime.Platform.Desktop` | Window creation/event pumping, native handles, input acquisition, filesystem discovery/watching/mapping, platform paths, clipboard, processes, development assembly loading, and the native-callable renderer and ImGui viewport callback entry points. |
| `XREngine.Runtime.XR.OpenVR` | OpenVR devices, actions, compositor, and render models. |
| `XREngine.Runtime.XR.OpenXR` | Renderer-neutral OpenXR instance/session, action, pose, swapchain, and frame lifecycle. |
| `XREngine.Runtime.Rendering.OpenGL` | OpenGL objects, renderer-specific UI/XR bridges, and native ReSTIR execution. |
| `XREngine.Runtime.Rendering.Vulkan` | Vulkan objects, allocator/native bridges, renderer-specific UI/XR bridges, and swapchains. |
| `XREngine.Runtime.Rendering.ImGui` | Shared ImGui integration installed by editor/debug hosts; graphics controllers remain in their renderer modules. |
| `XREngine.Runtime.Imaging.Magick` | ImageMagick decoding, encoding, format conversion, and mip generation. |
| `XREngine.Runtime.Media.FFmpeg` | FFmpeg media/audio decoding, HLS playback services, and optional yt-dlp process resolution. |
| `XREngine.Runtime.Text.FreeType` | Character enumeration and distance-field font atlas tool execution. |
| `XREngine.Runtime.UI.Skia` | Bitmap font and SVG rasterization. |
| `XREngine.Runtime.UI.Ultralight` | Ultralight web UI backend. |
| `XREngine.Runtime.UI.Rive` | Rive rendering components and input integration. |
| `XREngine.Runtime.IO.DirectStorage` | DirectStorage asset I/O and GDeflate codec implementation. |
| `XREngine.Runtime.Diagnostics.Desktop` | WMI/device inventory and native CUDA, nvCOMP, and NVIDIA diagnostics/compression capabilities. |
| `XREngine.Runtime.Net.Sockets` | TCP/UDP/TLS transports, socket gateways, profiler transport, and socket-based capture components. |
| `XREngine.Runtime.Net.Osc` | OSC/VMC transport and tracking components. |
| `XREngine.Runtime.MeshProcessing.Meshoptimizer` | Native meshoptimizer processing. |

Physics selection uses the backend catalog and preserves serialized `EPhysicsLibrary` values. PhysX remains the desktop default; Jolt is installed as an alternative. An unregistered selection reports the missing backend. Jolt browser execution and default promotion require the outstanding owner and parity decisions; there is no implemented Box3D module. See [physics architecture](../physics/overview.md).

`XRWindow` is a neutral facade in Rendering. Desktop owns the native window backend and event pump. Renderer modules borrow desktop GL-context or Vulkan-surface services through `IRendererDesktopWindowServices`; shared libraries do not expose Silk window/input objects. OpenXR graphics resources and API dispatch remain renderer-owned, with neutral graphics-host and lifetime contracts connecting them to the XR module.

Engine-owned presentation windows poll native events. Frame pacing belongs to the engine timer; blocking in a native event wait would suspend resource convergence, rendering and asynchronous readback while the window is idle. Borrowed GL function lookup returns zero for an unavailable optional symbol and retains owner-thread/lifetime checks. OpenGL capability admission is cached from each context's extension list and numeric version.

Native libraries call back into renderers through entry points that must outlive collectible renderer generations. Rendering keeps the managed registrations (`RendererNativeCallbackBridge`, `RendererImGuiViewportCallbackBridge`); the desktop platform module supplies the native-callable addresses when it registers. A native renderer created without them fails with a named error.

Image consumers exchange neutral buffers with dimensions, format, stride, origin, and mip data. GPU readback stays in renderer modules; encoding stays in the imaging module. Cooked raw textures need no ImageMagick decoder. Font and UI services similarly expose neutral data to Rendering. See [image, media, and font boundaries](../rendering/image-media-font-boundaries.md).

## Composition and supporting projects

| Project | Role |
| --- | --- |
| `XREngine.Runtime.Bootstrap` | Windows desktop composition root. Installs platform, renderer, physics/audio/XR/network/storage/image/media/text providers, desktop startup policy, native window pumping, VR service lifetimes, and desktop launch profiles. References the shared Host facade and timer. |
| `XREngine.Editor` | Editor executable, authoring services, ImGui integration, model import, tooling, and unit-world bootstrap. |
| `XREngine.Server` | Dedicated server executable using desktop bootstrap with headless renderer selection. |
| `XREngine.VRClient` | OpenVR companion executable with its own runtime lifetime and interprocess frame/input exchange. |
| `XREngine.Browser` | Browser composition and canvas host; independent of desktop Bootstrap. |
| `XREngine.Runtime.ModelAssetPipeline` | Aggregate authoring/import adapter constructing scenes, meshes, materials, skinning, and animation. Owns Assimp-based import and consumes FBX/glTF support projects. |
| `XREngine.Fbx` | Managed FBX parser/writer and import data model. |
| `XREngine.Gltf` | glTF support with the native FastGltfBridge; outside the portable compile closure. |
| `XREngine.Runtime.Automation` | Runtime automation/MCP integration used by tooling hosts. |
| `XREngine.ControlPlane`, `XREngine.ControlPlane.Service` | Control-plane contracts/services and the standalone orchestration service. |
| `XREngine.Profiler`, `XREngine.Profiler.UI` | Standalone profiler and shared profiler UI. |
| `XREngine.RenderBench`, `XREngine.Benchmarks` | Rendering and managed benchmark hosts with their own composition. |
| `XREngine.UnitTests` | Backend-neutral fixtures, backend-specific suites, and dependency/source boundary checks. |

Composition is explicit. Referencing a module does not necessarily activate its feature or supply optional vendor binaries. Bootstrap's renderer references are controlled by `XREngineRendererBackends` (`All`, `OpenGL`, `Vulkan`, or headless `None`). Jitter requires explicit installation. The editor installs collider authoring, model import, and ImGui services in addition to the desktop services.

`XREngine.Server` and `XREngine.VRClient` directly reference `XREngine.Runtime.ModelAssetPipeline` at their application composition roots. Desktop Bootstrap includes that module's sources in its generated application factory inputs without taking a direct project reference. Shared `XREngine.Runtime.Core` and `XREngine.Runtime.Rendering` remain free of model-authoring project dependencies; authoring and import implementation stays in the aggregate adapter.

Shipping registration uses generated/static factories and cooked type metadata. Rendering owns its generated built-in render-command registry. Host owns factories from its portable managed closure; desktop Bootstrap resolves that closure for inheritance and emits factories only for its desktop inputs. Browser generates its bridge-module registrations from an explicit manifest. All three use `Tools/Generate-AotFactoryRegistrations.ps1` through `Build/Registration/FactoryRegistrations.targets`; generated files live under intermediate output. Development authoring can discover loaded managed types and use Desktop's optional assembly loader. See [AOT final game builds](../../developer-guides/runtime/aot-final-game-builds.md).

Application roots install desktop composition before accessing `Engine.Assets`. The shared facade's static constructor initializes managed defaults and hooks without registering native leaves. Explicit engine initialization requires `IRuntimeEngineStartupPolicy`; it applies host settings before resource creation and host launch ingress before shared networking admission checks. Headless world composition takes an explicit physics scene factory. Desktop dedicated servers retain their Jolt factory, independently of the desktop rendered-world physics default.

## Native assets and source identity

The owning module declares native build/copy/publish items. This does not require every binary's physical supply directory to match the module name: protected submodule and SDK sources, CoACD staging under Core, Steam Audio staging under Audio, and Rive staging under Rendering retain their established locations. A folder containing a native binary is not evidence that the portable project compiles or packages it. Consult the evaluated module project and [native dependency guide](../../developer-guides/runtime/native-dependencies.md) for supply and output ownership.

Public namespaces and type names generally remain stable when source moves between assemblies. Runtime-facing vendor signatures use neutral contracts, so this is not a binary compatibility guarantee for old callers. Serialized backend enum values remain stable; moved CLR names and persisted-name searches are recorded in the [type identity audit](../../work/progress/platform/native-subsystem-type-identities.md).

Package versions, notices, and review status are recorded in [DEPENDENCIES.md](../../DEPENDENCIES.md), not inferred from this project map. Application build/publish layouts and feature behavior require integration acceptance in the [remaining debugging and validation checklist](../../work/todo/platform/native-subsystem-project-split-todo.md).
