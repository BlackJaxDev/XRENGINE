# Portable Engine Host

[Runtime Project Organization](project-organization.md) · [Portable Project Rules](../../developer-guides/runtime/portable-projects.md) · [Platform Validation](../../work/testing/platform/platform-validation.md)

The portable engine host is the shared managed runtime layer used by desktop and browser composition roots. It owns the engine facade, time source, world host, settings, snapshots, and portable service installation. Platform projects install the concrete leaves that the host needs.

## Scope

`XREngine.Runtime.Host` targets `net10.0`. It is part of the browser compile closure in `Build/Portable/PortableProjects.tsv`. `XREngine.Browser` references it directly. Desktop applications reach it through `XREngine.Runtime.Bootstrap`, which targets Windows and installs desktop leaves.

The host keeps these responsibilities portable:

- the `Engine` facade and its managed service slots;
- `EngineTimer`, including explicit frame support;
- `RuntimeWorldHost` and `HeadlessRuntimeWorldHost`;
- startup settings and unit-testing settings;
- runtime snapshots and serialization registration;
- generated factory and contract registration for portable types.

The host does not own desktop window creation, native event pumping, OpenGL or Vulkan device ownership, VR startup, native audio devices, native physics libraries, file watching, process launch, clipboard access, or browser canvas and WebGPU objects.

## Project Map

| Project | Role |
|---|---|
| `XREngine.Runtime.Host` | Shared engine facade, timer, world host, settings, snapshots, and service installation. |
| `XREngine.Runtime.Bootstrap` | Desktop composition root. Installs desktop startup policy, renderer catalogs, windows, VR, desktop services, and backend leaves. |
| `XREngine.Browser` | Browser composition root. Installs browser static registrations and browser renderer composition. |
| `XREngine.Runtime.Rendering` | Backend-neutral render contracts, targets, catalogs, and render objects. |
| `XREngine.Runtime.Rendering.WebGPU` | Browser WebGPU renderer leaf. It accepts `BrowserCanvasRenderTarget` and publishes browser-canvas capability. |

## Host Services

The host installs or consumes service slots instead of referencing platform leaves directly. `RuntimeAdapterBootstrap` installs managed runtime services. `RuntimeEngineStartupPolicyServices` supplies startup policy from the composition root. `RuntimeWorldHostServices` owns world-host lookup and play/edit transitions.

Missing required services must fail by name. Optional services can install an unavailable implementation only when the diagnostic says why the platform cannot provide the service.

## Frame Step

`EngineTimer` owns normal runtime timing. `EngineTimer.BeginExplicitFrame` gives a deterministic frame scope that can run on the calling thread. Browser work extends this model instead of entering the desktop event loop. Desktop hosts can still use the normal bootstrap loop and render-thread path.

A host must reset timing after a large suspension gap. It must invalidate temporal render history when the output generation changes or when page or window lifecycle makes previous history unsafe.

## World Host

`RuntimeWorldHost` composes a `RuntimeWorld` with a `RuntimeWorldRenderer` and visual scene. `HeadlessRuntimeWorldHost` supports hosts that need simulation without presentation. `EngineRuntimeWorldHostServices` maps authored `XRWorld` assets to these hosts and controls play/edit state.

Browser startup must use the real world host for authored worlds. A reference scene host can remain only as a diagnostic harness until the unified browser path replaces it.

## Renderer Boundary

Rendering uses presentation targets and backend capabilities. `BrowserCanvasRenderTarget` implements `IRendererPresentationTarget` and `IRuntimeSurfaceHost`, and requires `RendererBackendCapabilities.BrowserCanvasPresentation`. `WebGpuRendererBackendModule` rejects a target that is not a browser canvas. Desktop targets require desktop or XR capabilities.

Generic render code must not see JavaScript GPU handles. WebGPU resource handles, packets, and imported objects stay inside the WebGPU leaf.

## Registration And Metadata

The browser host uses `XREngineFactoryRegistrationMode=BrowserManifest` and `browser-registration-manifest.json`. The generator writes generated C# under intermediate output. Desktop Bootstrap uses the same registration tool with a desktop input set. Generated source must not replace normal project source or bypass the portable guard.

Published runtimes must resolve cooked metadata through generated registries or explicit metadata. A missing type, property, transform, render command, or service must produce a named diagnostic.

## Known Limits

The portable host compiles for browser, but that does not prove browser gameplay. Jolt browser execution, real browser-world startup, WebGPU pipeline parity, trim/AOT, device recovery, and physical mobile device support require the checks in [Platform Validation](../../work/testing/platform/platform-validation.md).

PhysX remains the desktop default until Jolt promotion has owner approval and parity evidence. The browser host currently still contains reference-runtime types that the unified path must retire when the engine path covers their cases.
