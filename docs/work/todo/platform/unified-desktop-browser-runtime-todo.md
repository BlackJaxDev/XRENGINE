# Unified Desktop And Browser Runtime TODO

[<- Work docs index](../../README.md) · Design: [Unified desktop and browser runtime](../../design/platform/unified-desktop-browser-runtime-design.md) · Prerequisite: [Native subsystem integration debugging and validation](native-subsystem-project-split-todo.md) · Backend detail: [Browser renderer module design](../../design/rendering/browser-wasm-renderer-design.md) · Device and delivery validation: [Mobile WebGPU runtime TODO](../rendering/mobile-webgpu-runtime-todo.md)

Status: build stabilization (UR00) in progress; the build gate passes locally, and harness runs remain. Browser gameplay integration is proposed. The shared project retargeting and native extraction are implemented in source; their deferred qualification is tracked in the prerequisite checklist. Current ownership and portable build rules are documented in [Runtime Project Organization](../../../architecture/runtime/project-organization.md).

Created: 2026-09-29. Updated: 2026-09-30.

Owner: Runtime architecture / rendering / platform.

## Goal

Run the same engine, worlds, and C# game code in the browser (WebGPU, .NET 10 WebAssembly) as on desktop, following the engine model used by Unity and Godot web builds:

- **Same assets:** one serialized world, prefab, and component format.
- **Same code:** one set of engine and game assemblies.
- **Per-platform differences are limited to** platform leaves (renderer backend, audio, input, windowing, physics native build, transports) and cooked GPU/format variants (shaders, textures, audio codecs).

The editor stays a desktop application and gains an honest browser publish target on this path. The separate browser runtime on the `codex/webgpu-readiness-audit` branch is stabilized as a reference harness and retired once this path reaches parity.

## Current State (2026-09-30)

A source and build review of the branch found the following. Build results are recorded in the [build stabilization record](../../progress/platform/unified-runtime-build-stabilization.md).

| Area | Finding | Tracked by |
| --- | --- | --- |
| Build | The branch's source had never been compiled after the organizational refactor. The portable source guard did not compile as an inline build task, which stopped every shared project and therefore every application. Resolved on 2026-09-30: every build gate check passes with no warnings. | UR00.01, UR00.09 |
| Toolchain | The `wasm-tools` workload was not installed on the development machine and no `global.json` pins the SDK. The workload is now installed and its versions recorded; the pin is still open. | UR00.02, decision D5 |
| Engine host | The `Engine` facade, `EngineTimer`, tick lists, world host, startup settings, and about 30 host-service implementations live in the Windows-only `XREngine.Runtime.Bootstrap`. The browser host installs none of them and drives the minimal `RuntimeSceneHost` instead of `RuntimeWorld`. | UR17, decision D1 |
| Parity target | The MonkeyBall sample targets `net10.0-windows7.0`, references Bootstrap, and uses PhysX-specific, VR, and OpenVR APIs. | UR10.06–UR10.08, decision D2 |
| Shaders | The engine shader tree holds 552 GLSL sources and 2 Slang pilots. No task covered porting the hand-written web-tier shaders. | UR05.07, decision D7 |
| Jolt leaf | `XREngine.Runtime.Physics.Jolt` targets `net10.0-windows7.0`, and the portability guard rejects every native file reference in portable projects, including the browser host. | UR07.06, UR07.07 |
| Path casing | The git index holds the Core project under two directory casings, which breaks case-sensitive checkouts such as the Linux compile lane. | UR00.08, decision D8 |
| Unit tests | The unit-test project builds, but boundary and source-contract tests fail on stale paths, moved types, and existing references. | UR00.10, UR00.12, prerequisite checklist |

## Rules

- **No second runtime.** New browser functionality goes through engine components, the engine renderer contract, and engine pipelines. Do not add features to the separate browser scene, component, animation, or pipeline types. Do not reimplement the engine facade or host services inside `XREngine.Browser`.
- **Compiled before complete.** An item is complete only when its code builds under the [build gate](#build-gate). Do not start a workstream on top of one that has not passed the gate. Source that has not been compiled is not a deliverable.
- **Item kinds.** Every item carries one tag:
  - `impl`: source work that an implementation pass can finish and compile.
  - `verify`: needs a live run, capture, measurement, or physical device.
  - `owner`: needs an owner decision or approval before work proceeds.

  An implementation-only pass completes `impl` items, leaves `verify` items unchecked with a note of what is ready to validate, and stops at `owner` items. It never resolves an [owner decision](#owner-decisions) by guessing.
- **Named failures.** A required service, pass, or physics feature that a platform lacks fails with a named diagnostic. No silent CPU fallback for a requested GPU path, no WebGPU-to-WebGL2 switch, no dropped components.
- **Hot paths.** One managed-to-JavaScript crossing per frame for rendering; no per-draw interop; no per-frame heap allocations in recording, visibility, simulation, or submission.
- **No blocking on the browser path.** Asynchronous I/O and readback only; the browser runs frame-stepped on one thread.
- **Desktop preservation.** Validate every shared-contract change on OpenGL and Vulkan per the [pipeline invariants](../../../architecture/rendering/default-render-pipeline-notes.md) and [mesh submission contracts](../../../architecture/rendering/mesh-submission-strategies.md).
- **Validation order.** Validate each feature through its live path (desktop editor, browser page) before adding regression tests, following repository policy. Record browser evidence under `Build/_AgentValidation/<run>/` and durable findings in `docs/work/investigations/<subsystem>/`.
- **Approvals.** Toolchain pins, new dependencies (repository-built `joltc`, headless-browser test tooling), and supply-path changes need owner approval and license review.
- **No todo IDs in code.** Keep task IDs out of code, comments, type names, and diagnostics.

## Build Gate

Run the gate after every workstream and before checking any `impl` item. Fix failures caused by the change; list unrelated failures separately instead of working around them.

| Check | Command (repository root) |
| --- | --- |
| Shared closure, desktop | `dotnet build XREngine.Runtime.Rendering.WebGPU/XREngine.Runtime.Rendering.WebGPU.csproj` |
| Editor | `dotnet build XREngine.Editor/XREngine.Editor.csproj` |
| Server | `dotnet build XREngine.Server/XREngine.Server.csproj` |
| VRClient | `dotnet build XREngine.VRClient/XREngine.VRClient.csproj` |
| Shared closure, `browser-wasm` | `pwsh Tools/Test-PortableBrowserCompile.ps1 -Configuration Release` |
| Browser publish | `dotnet publish XREngine.Browser/XREngine.Browser.csproj -c Release -m:1` |

The two browser checks need the `wasm-tools` workload. A workstream that touches only desktop leaves may skip them; a workstream that touches any project in [PortableProjects.tsv](../../../../Build/Portable/PortableProjects.tsv) may not. Run repository PowerShell tools with PowerShell 7; Windows builds do not need it.

Last result: all six checks passed with no warnings on 2026-09-30. The [build stabilization record](../../progress/platform/unified-runtime-build-stabilization.md) has the toolchain versions and payload sizes.

## Owner Decisions

These block the listed items. Record each decision here with its date when it is made.

| ID | Decision | Blocks | Notes |
| --- | --- | --- | --- |
| D1 | Where the portable engine host lives. | UR17, UR01.06, UR02, UR10 | Recommended: a portable project that owns the `Engine` facade, timer, tick lists, world host, settings, and host-service implementations, leaving Bootstrap as desktop composition only. Reimplementing them in the browser host is a second runtime and is not an option. |
| D2 | The U3 parity target: port MonkeyBall to portable code, or choose another sample. | UR10.06–UR10.08, UR11.05 | MonkeyBall currently depends on PhysX, VR, OpenVR, and Bootstrap. |
| D3 | Jolt as the primary desktop/browser physics backend, and the parity criteria. | UR07 | Tracked in the [prerequisite checklist](native-subsystem-project-split-todo.md#jolt-browser-proof-and-default-promotion-gates). |
| D4 | Jolt native supply and toolchain plan. | UR07.01 | See the [native supply proposal](../../design/platform/jolt-browser-native-supply.md). |
| D5 | Pin the .NET SDK and `wasm-tools` workload (`global.json` and recorded workload version). | UR00.02, UR15.01 | The Emscripten version the runtime pack expects must match D4. |
| D6 | Static registration mechanism for the browser host: the existing script-based generator or the C# source generator the design names. | UR01.04, UR17.05 | The existing generator is [Generate-AotFactoryRegistrations.ps1](../../../../Tools/Generate-AotFactoryRegistrations.ps1), invoked from the Bootstrap and Rendering projects. |
| D7 | Authoring route for the web-tier shaders: Slang sources cooked to every target, or hand-written WGSL beside the GLSL. | UR05.02, UR05.07 | Decide after the UR05.01 inventory. |
| D8 | Normalize the Core project's directory casing in git. | UR00.08, UR15.01 | A case-only rename touching 243 tracked paths. |
| D9 | Headless-browser smoke tooling. | UR15.02 | New dependency; needs license review. |
| D10 | Cross-platform physics determinism. | UR07.03 | Requires repository-built `joltc` on every platform. |
| D11 | Shipping runtime mode (interpreter or AOT). | UR13.02 | Decided from UR13 measurements. |
| D12 | Retire the separate browser runtime entirely or keep a small standalone demo. | UR16 | Design open decision 7. |
| D13 | Whether Server and VRClient may reference the model asset pipeline, and whether Bootstrap's registration generator may scan it. | UR00.12 | The approved dependency graph in the boundary tests currently rejects both. |

## Remaining Work

As of 2026-09-30, 2 of 110 items here are complete, and the [prerequisite checklist](native-subsystem-project-split-todo.md) has 35 of 36 items open. The builds compile, but nothing has run yet: no desktop application launch, browser page, or full test suite since the refactor.

Sizes are rough planning estimates for one engineer: **S** is days, **M** is one to two weeks, **L** is several weeks, and **XL** is a month or more. Revise them once U1 is reached.

| Stage | Workstreams | Open items | Size | Blocked by |
| --- | --- | --- | --- | --- |
| Finish U0 | UR00 | 10 (3 `impl`, 4 `verify`, 3 `owner`) | S–M | D5, D8, D13 |
| Prerequisite integration | [Native subsystem checklist](native-subsystem-project-split-todo.md) | 35 | L | D3, D4 for the Jolt proof and promotion |
| U1: engine boots | UR17, UR01, UR02, UR03 | 25 | L each; UR01 M | D1, D6 |
| U2: engine renders | UR04, UR05, UR06 | 22 | UR04 XL, UR05 XL, UR06 L | D7 |
| U3: project plays | UR07, UR08, UR09, UR10, UR11 | 29 | UR07 L, UR08 M, UR09 L, UR10 M, UR11 M | D2, D3, D4 |
| U4: production | UR13, UR14, UR15, UR16 | 19 | M each | D9, D11, D12 |
| U5: networked client | UR12 | 3 | L | U3 |

The critical path is D1, then UR17, then UR02, then UR04 and UR05, then UR06. UR04 (the WebGPU `AbstractRenderer` backend), UR05 (porting the web-tier shaders), and UR17 (the portable engine host) are the three largest single pieces. UR07, UR08, and UR10 can proceed in parallel once U1 boots.

## Execution Order

1. **UR00** until the build gate passes and the harness has recorded evidence.
2. **UR17** and **UR01.01–UR01.05**. These are independent and may proceed together.
3. **UR01.06**, then **UR02** and **UR03**.
4. **UR04**, **UR05**, **UR06** in that order. The UR05.01 shader inventory can start as soon as UR00 is done.
5. **UR07**, **UR08**, **UR09**, **UR10**. These do not depend on each other; UR07 and UR10 do not depend on rendering.
6. **UR11**.
7. **UR13–UR15**, **UR12**, then **UR16**. Start the UR13.01 interpreter measurement as soon as U1 boots.

## Gates

| Gate | Result | Main workstreams |
| --- | --- | --- |
| U0 — Reference harness | The build gate passes. The branch's browser app and editor browser target build, run, and have recorded evidence. The separate runtime is frozen. | UR00 |
| U1 — Engine boots in the browser | The real portable assemblies load a real `XRWorld` asset through fetch, construct scenes, the game mode, and components, and tick fixed/variable updates without rendering. | Native subsystem integration acceptance; UR17; UR01–UR03 |
| U2 — Engine renders in the browser | Engine cameras, `ModelComponent`, and engine materials render through the WebGPU backend and the web tier of `DefaultRenderPipeline`, with resize and device-loss reporting. | UR04–UR06 |
| U3 — Published project plays | Editor Build Project with the browser target produces a site that runs the startup world with the project's game code, Jolt physics, audio, input, and UI. The parity target chosen in D2 plays. | UR07–UR11 |
| U4 — Production qualification | Physical mobile devices, budgets, runtime mode (interpreter or AOT), recovery, hosting, and CI pass. The separate runtime is retired. | UR13–UR16 |
| U5 — Networked browser client | The browser client joins the real server path. | UR12 |

## UR00 — Stabilize The Branch As A Reference Harness

- [x] **UR00.01** `impl` Repair the portable source guard so it compiles as an inline build task, then validate the implemented source policy: permit `System.Drawing.Primitives` value types and deny Windows bitmap APIs. Resolve any remaining guard failures in the shared closure. Done 2026-09-30: deny rules narrowed to actual vendor, registry, and socket API use; native callback entry points moved out of Rendering into the desktop platform module.
- [ ] **UR00.02** `owner` Pin the .NET SDK and install the `wasm-tools` workload per [the browser README](../../../../XREngine.Browser/README.md) (decision D5). Build and publish `XREngine.Browser` using its normal project configuration; the old portable-profile switch has been removed. Record exact SDK, workload, and runtime-pack versions. The workload install, publish, and version record are done (SDK 10.0.401, `wasm-tools` 10.0.112, WebAssembly runtime pack 10.0.12, Emscripten 3.1.56); the pin awaits D5.
- [ ] **UR00.03** `verify` Serve the published output locally. Run the demo in a WebGPU-capable desktop browser, capture and view screenshots, capture counters, and record results and failures in `docs/work/investigations/rendering/`.
- [ ] **UR00.04** `verify` Build the editor. Run Build Project with the `BrowserWebGPU` platform on a minimal world that meets the current exporter rules. Serve and open the output, and record the result.
- [ ] **UR00.05** `impl` Freeze feature work in the separate runtime (`Browser*` scene, component, animation, collision, and pipeline types, and `BrowserWorldPublishExporter`); allow fixes only to keep the harness running.
- [ ] **UR00.06** `impl` Reconcile the [mobile TODO](../rendering/mobile-webgpu-runtime-todo.md) code-completion rows with UR00.02–UR00.04 build evidence. Mark rows that never compiled.
- [ ] **UR00.07** `impl` If the branch's publish target is used before UR11 lands, stop shipping the developer harness as published output: no demo controls, no sample UI overlay, no "Demo scene" switch in the published page.
- [ ] **UR00.08** `owner` Normalize the Core project's directory casing (decision D8). The git index holds 243 tracked files under `XRENGINE.Runtime.Core/` and 515 under `XREngine.Runtime.Core/`. A case-insensitive checkout merges them; a case-sensitive one, including the `ubuntu-latest` compile lane, produces two directories and compiles a partial source set. Until this is fixed, source inventories must use case-insensitive pathspecs (`git grep ... -- ":(icase)XREngine.Runtime.Core/*.cs"`).
- [x] **UR00.09** `impl` Make the [build gate](#build-gate) pass: the shared closure, Editor, Server, VRClient, the unit-test project, the `browser-wasm` compile lane, and the browser publish. Fix failures that come from the extraction and retargeting; record unrelated failures separately. Done 2026-09-30, with the full solution also building; unit-test execution failures are recorded in the [build stabilization record](../../progress/platform/unified-runtime-build-stabilization.md).
- [ ] **UR00.10** `verify` Run the full unit-test suite and triage the failures together with the [prerequisite checklist](native-subsystem-project-split-todo.md#build-dependency-and-publish-boundaries). A filtered boundary and source-contract run had 61 failures of 233. The causes are source-text contracts that name pre-refactor paths or moved types, file lookups made ambiguous by duplicated names, and native Vulkan tests that need a device. Fix stale contracts to follow moved code without weakening what they assert; record device-only tests as such.
- [ ] **UR00.11** `verify` Exercise the relocated native callback entry points. Rendering now keeps only the registrations; `DesktopPlatformBackend.Register` installs the native-callable addresses. In a live editor session, check ImGui detached viewports, the ImGui clipboard, Vulkan validation messages, and Streamline logging. Confirm that hosts which create native renderers without the desktop platform backend (RenderBench, Benchmarks, GPU unit tests) either install the entry points or report the named error.
- [ ] **UR00.12** `owner` Decide (D13) whether `XREngine.Server` and `XREngine.VRClient` may reference `XREngine.Runtime.ModelAssetPipeline`, and whether Bootstrap's registration generator may scan it. The boundary tests reject both. Either remove the references or update the approved graph.

**Acceptance (U0):** the build gate passes, and there is reproducible build, publish, and run evidence for the branch as it stands, with its limits recorded.

## UR17 — Portable Engine Host And Facade

Runs after UR00 and before UR01.06, UR02, and UR10. Blocked on decision D1.

**Starting point.** `XREngine.Runtime.Bootstrap` targets `net10.0-windows7.0`, references every desktop leaf, and owns:

- the `Engine` static facade: about 40 source files under `Engine/`, `SubsystemHost/`, and `RenderingHost/`, including `Engine.Time`, `Engine.TickList`, `Engine.Worlds`, `Engine.State`, `Engine.Lifecycle`, `Engine.PlayMode`, `Engine.Settings`, and `Engine.Threading`;
- `EngineTimer` (`Core/Time/`), which owns the update, collect-visible, and fixed-update workers and the render-thread wait;
- `RuntimeWorldHost` and the world-host composition services (`WorldHost/`);
- `GameStartupSettings` and the other settings types (`Settings/`);
- the implementations installed into about 30 host-service slots declared by the shared projects: `RuntimeTimingServices`, `RuntimeWorldHostServices`, `RuntimeWorldRegistryServices`, `RuntimeWorldObjectServices`, `RuntimeGameModeHostServices`, `RuntimePawnHostServices`, `RuntimePlayerControllerServices`, `RuntimeInputServices`, `RuntimeInputCaptureServices`, `RuntimeAnimationHostServices`, `RuntimeAudioIntegrationServices`, `RuntimeRenderingHostServices`, `RuntimeRenderObjectServices`, `RuntimeShaderServices`, `RuntimePhysicsServices`, `RuntimeThreadServices`, `RuntimeTransformServices`, `RuntimeSceneNodeServices`, `RuntimeSceneStreamingHostServices`, `RuntimeNetworkingHostServices`, `RuntimeNetworkDiscoveryHostServices`, `RuntimeMaintenanceServices`, `RuntimeDebugHostServices`, `RuntimeWindowApplicationServices`, `RuntimeVideoStreamingServices`, `RuntimeVrRenderingServices`, `RuntimeVrStateServices`, `RuntimeVrInputServices`, `RuntimeCharacterMovementVisualizationServices`, `RuntimeStaticColliderAuthoringServices`, and `RuntimeApplicationCapabilityServices`.

`XREngine.Browser` installs none of these slots. Game code calls the facade directly (`Engine.Delta`, `Engine.FixedDelta`, `Engine.Assets`, `Engine.ShutDown`), so the same game assembly cannot load in the browser until the facade is portable.

**Constraint.** A C# partial type cannot span assemblies. When `Engine` moves, its desktop-only members (windows, VR lifecycle, network discovery, profiler transport) cannot stay behind as Bootstrap partials; they move behind host-service contracts or onto a separate desktop type, and their call sites are updated.

- [ ] **UR17.01** `owner` Decide the project boundary (decision D1) and the portable project's name.
- [ ] **UR17.02** `impl` Inventory Bootstrap file by file as portable as-is, portable once a leaf reference is replaced by an existing contract, or desktop-only. Inventory each host-service slot with its implementing type and the same classification. Record both tables in a progress doc under `docs/work/progress/platform/`.
- [ ] **UR17.03** `impl` Move the portable set into the portable project, keeping namespaces and type names (`XREngine.Engine`, `XREngine.Timers.EngineTimer`). Register the project in `Directory.Build.props`, [PortableProjects.tsv](../../../../Build/Portable/PortableProjects.tsv), [PortablePackages.tsv](../../../../Build/Portable/PortablePackages.tsv), and the solution. Update the [type identity audit](../../progress/platform/native-subsystem-type-identities.md).
- [ ] **UR17.04** `impl` Move desktop-only facade members behind host-service contracts or a desktop type. A missing service fails by name; it does not return defaults.
- [ ] **UR17.05** `impl` Give static factory generation an owner that both hosts can use (decision D6). Today the generator is wired into the Bootstrap project and scans desktop leaf sources.
- [ ] **UR17.06** `impl` Reduce Bootstrap to desktop composition: leaf installation, window and VR startup, and desktop launch profiles. Editor, Server, VRClient, benchmarks, and samples build against the new boundary.
- [ ] **UR17.07** `verify` Launch the Editor, Server, and VRClient and load the unit-testing world. Confirm startup, play mode, and frame pacing match the pre-move baseline.

**Acceptance:** the facade, timer, and host services compile for `browser-wasm` from the same assembly desktop uses, and Bootstrap contains only desktop composition.

## UR01 — Portable Engine Assemblies In The Browser Host

Shared projects already target `net10.0` with whole-project checks. Depends on their [integration acceptance](native-subsystem-project-split-todo.md#build-dependency-and-publish-boundaries); source completion alone does not establish browser startup.

- [ ] **UR01.01** `impl` Verify the browser host references the full portable assemblies and include the integration adapters needed for real-world boot. The host currently references Core, Rendering, the WebGPU module, and Animation only. Source-subset profiles and the old portable build property are already removed; qualify the evaluated closure and full API surface.
- [ ] **UR01.02** `impl` Make `XREngine.Browser` a composition root that installs, explicitly and as they land:
  - the host services from UR17, each either implemented or installed as a named unsupported service;
  - the browser leaves: WebGPU renderer, browser platform, Jolt, Web Audio, browser input, fetch asset source, WebSocket transport.

  Unavailable required services fail by name.
- [ ] **UR01.03** `impl` Audit static constructors, module initializers, and reflection scans reachable at browser startup. Qualify the implemented published-metadata lookup and desktop service boundaries, then move any remaining browser-reachable desktop initialization into leaves.
- [ ] **UR01.04** `impl` Generate static registrations for browser component, transform, serializer, and module registration with the mechanism chosen in D6. Retire the branch's Python registration generator.
- [ ] **UR01.05** `verify` Load real YAML/MemoryPack assets in the interpreter and round-trip representative worlds, prefabs, and components.
- [ ] **UR01.06** `impl` Boot a real `XRWorld` fetched from a cooked bundle through `RuntimeWorld` and the world host, replacing the host's use of `RuntimeSceneHost`. Construct scenes, the game mode, pawns, and components; run fixed and variable updates; report lifecycle state to the page. Depends on UR17.

**Acceptance:** the browser runs the engine's own world, scene, and component lifecycle from the same binaries desktop uses.

## UR02 — Platform Host, Frame Stepping, And Scheduling

Depends on UR17.

- [ ] **UR02.01** `impl` Extract a single-frame engine step (fixed-step simulation with bounded catch-up, variable update, visibility, render recording, submission) that desktop and browser hosts both call. The loop lives in `EngineTimer`: `RunGameLoop`, `BlockForRendering`, the `DispatchUpdate`/`DispatchCollectVisible`/`DispatchSwapBuffers`/`DispatchRender` methods, and the fixed-update worker. `RuntimeRenderThreadHost` in Rendering only wraps the render-thread side. Start from `EngineTimer.BeginExplicitFrame`, which already runs one deterministic frame on the calling thread with the workers stopped, and extend it to real elapsed time.
- [ ] **UR02.02** `impl` Support rendering on the calling thread: update, swap, collect, and render run in sequence within one step. Verify the engine's double-buffered render state works without a dedicated render thread.
- [ ] **UR02.03** `impl` Add a caller-thread executor for the job system (`JobManager`). Inventory every blocking and thread-creating site in the shared closure and make each asynchronous, move it to a desktop leaf, or confine it to cook and editor code. Regenerate the inventory with:

  ```sh
  git grep -l -E "\.Wait\(|\.WaitOne\(|\.Result\b|GetAwaiter\(\)\.GetResult\(\)|Thread\.Sleep|\.WaitAll\(|\.WaitAny\(|\.Join\(" -- ":(icase)<project>/*.cs"
  git grep -l -E "new Thread\(" -- ":(icase)<project>/*.cs"
  git grep -l -E "Task\.Run\(|Parallel\.(For|ForEach|Invoke)|ThreadPool\." -- ":(icase)<project>/*.cs"
  ```

  File counts on 2026-09-30 (lexical matches, so they include false positives such as `string.Join`):

  | Project | Blocking waits | `new Thread` | Task/parallel/pool |
  | --- | --- | --- | --- |
  | `XREngine.Runtime.Core` | 29 | 4 | 8 |
  | `XREngine.Runtime.Rendering` | 39 | 2 | 9 |
  | `XREngine.Data` | 9 | 0 | 7 |
  | `XREngine.Extensions` | 2 | 0 | 4 |
  | `XREngine.Animation` | 1 | 0 | 0 |
  | `XREngine.Runtime.AudioIntegration` | 3 | 0 | 2 |
  | `XREngine.Runtime.AnimationIntegration` | 3 | 0 | 0 |
  | `XREngine.Runtime.InputIntegration` | 1 | 0 | 1 |
  | `XREngine.Browser` | 1 | 0 | 0 |

  The project that UR17 creates adds `EngineTimer` and `Engine.Threading` to this list.
- [ ] **UR02.04** `impl` Create `XREngine.Runtime.Platform.Browser` from the branch's canvas host and surface contracts: `browser-canvas-host.js`, `IRuntimeSurfaceHost`, `RuntimeSurfaceState`, `BrowserCanvasRenderTarget`. It covers CSS/backing size, DPR caps, orientation, safe area, detach/reattach, and output generations.
- [ ] **UR02.05** `impl` Handle page visibility, freeze/resume, and `pagehide`/`pageshow`; reset timing and invalidate temporal history after suspension or large gaps.
- [ ] **UR02.06** `verify` Confirm desktop editor and VRClient frame pacing is unchanged.

**Acceptance:** one engine frame step drives desktop and browser, and no browser-reachable code blocks.

## UR03 — Asset I/O And Per-Platform Cooking

- [ ] **UR03.01** `impl` Route `AssetManager` loading through the asynchronous members of the asset-source contract and implement the browser source with same-origin, credential-free, hash-verified fetches, reusing `content-loader.js` and `content-manifest.js`. The contract is `IRuntimeAssetSource` with `IAssetReadBatch` (`XREngine.Runtime.Core/Assets/IO/`), installed through `DirectStorageIO.Source`; its only implementation is the DirectStorage leaf. It also exposes synchronous members (`Exists`, `ReadAllBytes`, `ReadRange`, `TryReadInto`, `TryReadFileInto`, `IAssetReadBatch.Execute`). Either remove those from the contract or make the browser source fail them by name; do not block on a fetch.
- [ ] **UR03.02** `impl` Remove synchronous load wrappers from runtime-reachable paths. On 2026-09-30 the asset manager had 10 sync-over-async sites, and Core and Rendering had 136 direct `File`/`Directory`/`FileStream` call sites across 39 files that bypass the asset source.
- [ ] **UR03.03** `impl` Add a platform target to cooking (`CookContent` in `XREngine.Editor/ProjectBuilder.cs`). Web cooking produces:
  - WGSL shader artifacts;
  - ASTC 4×4 and ETC2 texture variants with RGBA8 fallbacks, reconciled with the [texture compression TODO](../texturing/texture-compression-and-cooked-cache-todo.md);
  - web-decodable audio;
  - per-asset capability requirements.
- [ ] **UR03.04** `impl` Generalize the branch's `BrowserContentPackageBuilder` rules (immutable SHA-256 payload URLs, revalidated manifest, dependency closures, essential and streamed splits, strict limits) to the real asset graph instead of browser-only DTOs.
- [ ] **UR03.05** `impl` Keep bounded download concurrency, cancellation, retry, progress, and per-frame integration budgets, reusing the branch's delivery code. Track retained, staging, and estimated GPU bytes.
- [ ] **UR03.06** `verify` Validate cold and warm cache, throttled, failed, corrupt, and missing payloads, and cancel/restart. This carries over the mobile TODO's MW07 acceptance.

**Acceptance:** the browser loads the same world assets as desktop, with only cooked platform variants differing.

## UR04 — WebGPU Renderer Backend For The Engine

- [ ] **UR04.01** `impl` Implement `AbstractRenderer` and its API-object wrappers in `XREngine.Runtime.Rendering.WebGPU`. The base class is about 2,100 lines with 66 abstract members, and the module currently implements only `IBrowserRendererHost`. Land it in slices, each passing the build gate and rendering through the engine path before the next begins:
  - [ ] Renderer skeleton registered in the backend catalog: every abstract member is implemented or fails with a named unsupported diagnostic; clear and present work.
  - [ ] Data buffers and views, programs, mesh renderers, and vertex layouts: one unlit `ModelComponent` renders through an engine camera.
  - [ ] 2D, array, and cube textures and samplers: a textured material renders.
  - [ ] Framebuffers and render buffers: offscreen targets and resolves work.
  - [ ] Materials and uniform data: the engine's lit material renders.
  - [ ] Compute dispatch.
- [ ] **UR04.02** `impl` Track GL-shaped state in C# and resolve it into cached immutable render pipelines, layouts, and bind groups with complete keys and bounded caches, following the Vulkan backend's approach.
- [ ] **UR04.03** `impl` Record commands into reusable C# arenas and flush one packet per frame to the JavaScript executor. Reuse the branch's resource, command, readback, usage-scope, limits, and pipeline-cache executors. Move the policy logic in `browser-render-pipeline.js` into C#.
- [ ] **UR04.04** `impl` Clean up the renderer contract for non-blocking backends:
  - neutral `RuntimeImage` readbacks (implemented shared contract; browser renderer support still required);
  - asynchronous-only screenshots, pixel reads, and luminance;
  - `WaitForGpu` rejected with a named error on non-blocking hosts;
  - truthful capability probes: no indirect-count draw, no mesh shaders, no bindless textures, bounded bind groups.
- [ ] **UR04.05** `impl` Present to the canvas through `RenderFrameOutputDescription` with surface generations, re-acquiring the output each frame and rejecting obsolete plans after resize.
- [ ] **UR04.06** `impl` Handle pending, ready, failed, and lost states. Recovery reconstructs device resources from CPU-side and cooked sources, carrying over the mobile TODO's MW10.01–MW10.05.
- [ ] **UR04.07** `impl` Add error scopes, debug labels, and bridge counters. Recording and submission must not allocate per frame.
- [ ] **UR04.08** `verify` Validate every shared renderer-contract change on OpenGL and Vulkan with editor captures from multiple positions.

**Acceptance:** the engine's renderer runs on WebGPU through the same contract as OpenGL and Vulkan, with one JavaScript crossing per frame.

## UR05 — Shaders And Materials

**Starting point.** `Build/CommonAssets/Shaders` holds 552 GLSL sources: 222 fragment, 148 compute, 109 includes, 40 vertex, 20 geometry, 9 mesh/task, and 4 tessellation. Three of them (`Common/MaterialTable.glsl` and the two `Graphics/BindlessMesh` stages) require bindless-texture or 64-bit integer extensions. The same tree has 2 Slang pilots under `FrontendPilots/`. The only other Slang source and all 10 WGSL sources belong to the separate runtime. Slang does not ingest desktop GLSL 4.6 unchanged, so the web tier's shaders must be ported, not only cooked.

- [ ] **UR05.01** `impl` Inventory the shaders the web tier of `DefaultRenderPipeline` needs. Classify each as portable through Slang, needing a WGSL rewrite, or desktop-only. Record the list, grouped by pass, in a progress doc; it is the work list for UR05.07.
- [ ] **UR05.02** `impl` Cook engine shaders to WGSL with the pinned Slang route and the shader artifact format (reusing `Tools/ShaderCooker`, the `ShaderCompileTarget.WebGPUWgsl` target, and artifact schema checks). Follow the [Slang cross-compile plan](../../design/scripting/slang-shader-cross-compile-plan.md). Depends on decision D7.
- [ ] **UR05.03** `impl` Extend the engine's material shader generation with a WGSL target, replacing the separate browser material generator. Support the engine's lit material model, not only unlit and Lambert.
- [ ] **UR05.04** `impl` Implement the WebGPU encoding of logical material and texture references with bounded bind groups, texture arrays for qualifying content, and material batching. No desktop bindless handle reaches WGSL.
- [ ] **UR05.05** `impl` Report web-unsupported shaders and material features at cook time with material, pass, source location, and reason.
- [ ] **UR05.06** `verify` Verify coordinate conventions (clip depth range, texture Y, winding, matrix layout, reversed-Z where the engine uses it) with known-value renders.
- [ ] **UR05.07** `impl` Port the hand-written web-tier shaders from the UR05.01 list by the route chosen in D7, one pass group at a time: depth and shadow casters, forward lit surfaces, sky and environment, tonemapping and the bounded post-process set, then UI and text. Each group cooks without errors and has its known-value render listed under UR05.06 before the next group starts. Desktop GLSL behavior is unchanged.

**Acceptance:** engine materials cook to WGSL and render with correct interpretation; unsupported ones fail by name.

## UR06 — Render Pipeline Web Tier

- [ ] **UR06.01** `impl` Define the WebGPU capability profile of `DefaultRenderPipeline`: depth, forward lighting with the engine material model, directional/spot/point shadows within limits, sky and environment, alpha-masked and sorted transparency, HDR with tonemapping, a bounded post-process set, and engine UI.
- [ ] **UR06.02** `impl` Select passes from capabilities and report excluded passes explicitly; never discover unsupported passes by failing at runtime.
- [ ] **UR06.03** `impl` Make `AdvancedRenderPipeline` report unsupported for WebGPU outputs through its existing `Available` policy (explicitly unbound, with a reason) and fail under `Required`.
- [ ] **UR06.04** `impl` Resolve `CpuDirect` mesh submission for WebGPU. Later, and only after measurement, add GPU culling that writes fixed indexed slots with zero-instance culled draws, porting the branch's WGSL culling, BVH, and Hi-Z kernels into the engine's GPU scene path.
- [ ] **UR06.05** `impl` Run engine skinning and blendshapes through WebGPU compute (reusing the branch's WGSL skinning kernel as a canonical port) or CPU, with CPU/GPU parity checks.
- [ ] **UR06.06** `impl` Add mobile quality tiers to engine settings: backing resolution and DPR caps, shadow sizes and cadence, light counts, texture tiers, and post effects. Disabled effects must not allocate resources.
- [ ] **UR06.07** `verify` Render the same test worlds on OpenGL, Vulkan, and WebGPU; compare tolerant captures and document deliberate differences.

**Acceptance (U2):** a representative engine world renders in the browser through engine objects and the web tier, with documented differences from desktop.

## UR07 — Jolt Physics In The Browser

Depends on the [Jolt browser proof and default-promotion gates](native-subsystem-project-split-todo.md#jolt-browser-proof-and-default-promotion-gates) and decisions D3 and D4. The desktop Jolt module already exists.

- [ ] **UR07.01** `owner` Productize the `joltc` Emscripten archive build, pinned to the runtime pack's Emscripten version. Ship it as the Jolt leaf's `browser-wasm` native asset, with a `.props` file that adds the `NativeFileReference`.
- [ ] **UR07.02** `impl` Install the Jolt module in the browser composition with single-threaded job execution and a truthful capability report.
- [ ] **UR07.03** `verify` Compare desktop and browser results on matched scenes. If the approved native supply enables cross-platform determinism (decision D10), match state hashes; otherwise match within documented tolerances.
- [ ] **UR07.04** `impl` Fail browser publishing of worlds that require PhysX-only features with the component path and feature name.
- [ ] **UR07.05** `verify` Measure physics step time on the reference devices and set budgets.
- [ ] **UR07.06** `impl` Make the Jolt leaf buildable for the browser. `XREngine.Runtime.Physics.Jolt` targets `net10.0-windows7.0` and references `JoltPhysicsSharp` 2.22.0, whose native package has no `browser-wasm` asset. Retarget it to `net10.0`, keep desktop native resolution working, and admit the project and package to the portable policy files.
- [ ] **UR07.07** `impl` Add a reviewed native-asset allowance to the portability guard. [PortableRuntime.targets](../../../../Build/Portable/PortableRuntime.targets) rejects every `NativeFileReference`, native copy item, and resolved native runtime asset in a portable project, and the browser host is one. Allow named files per project and runtime identifier with a recorded reason, as the package policy does; do not disable the check. Update the [portable project rules](../../../developer-guides/runtime/portable-projects.md).

**Acceptance:** engine physics components behave the same on desktop and web through Jolt.

## UR08 — Audio In The Browser

- [ ] **UR08.01** `impl` Implement `XREngine.Audio.WebAudio` against the audio contracts (sources, listener, spatialization, gain, looping, streaming), reusing `browser-audio.js`. The name follows the existing audio leaves (`XREngine.Audio.OpenAL`, `XREngine.Audio.NAudio`, `XREngine.Audio.SteamAudio`). Engine audio components are unchanged.
- [ ] **UR08.02** `impl` Add gesture-driven activation, suspension, and resume. Gate simulation only when a world declares audio as required.
- [ ] **UR08.03** `verify` Validate cooked audio codecs on the browser matrix, including Safari.
- [ ] **UR08.04** `impl` Report Steam Audio features as unsupported on web unless a WebAssembly build is separately evaluated and approved.

**Acceptance:** engine audio components play in the browser with the same authored data.

## UR09 — Input, UI, And Text

- [ ] **UR09.01** `impl` Implement a browser input leaf that feeds `XREngine.Input` devices from pointer, touch, keyboard, IME, wheel, and Gamepad API events, reusing `browser-input.js`. Player controllers and input mappings are unchanged.
- [ ] **UR09.02** `impl` Render engine UI through WebGPU with hit testing in the same coordinate convention as rendering.
- [ ] **UR09.03** `impl` Cook glyph atlases with FreeType at cook time and render cooked fonts at runtime. Desktop shipping builds may use the same path.
- [ ] **UR09.04** `impl` Bridge text entry, IME, and accessibility to DOM elements.
- [ ] **UR09.05** `impl` Express mobile touch controls (virtual sticks, buttons) as engine input mappings and UI, not page-specific script.

**Acceptance:** a user can play with touch, keyboard/mouse, or gamepad through the engine's own input and UI.

## UR10 — Game Code And Components

Depends on UR17: game code reaches the engine through the `Engine` facade.

- [ ] **UR10.01** `impl` Make project templates and game projects target `net10.0` and reference only portable engine assemblies (plus leaf contracts where needed). The generated target framework is `net10.0-windows7.0` today, set in `XREngine.Editor/CodeManager.cs` and `XREngine.Editor/EditorProjectInitializer.cs`.
- [ ] **UR10.02** `impl` Report at build time which game-assembly references block browser publishing (desktop-only leaves or APIs), with type and member names.
- [ ] **UR10.03** `impl` Link the project's game assemblies into the browser publish, with generated static registrations. Editor hot reload remains desktop-only.
- [ ] **UR10.04** `impl` Keep editor-only code out of game builds, as today.
- [ ] **UR10.05** `verify` Load serialized game components, game modes, and pawns in the browser from the same assets as desktop.

The remaining items make the parity target portable. They apply to MonkeyBall unless decision D2 selects another sample. `Samples/MonkeyBallVR` currently targets `net10.0-windows7.0`, references `XREngine.Runtime.Bootstrap`, runs on PhysX (`XREngine.Scene.Physics.Physx`), uses VR components and the OpenVR action manifest, and ships its own cooked-world serializer.

- [ ] **UR10.06** `impl` Move the sample's PhysX-specific calls onto the backend-neutral physics contracts so it runs on Jolt. Otherwise UR07.04 rejects it at publish.
- [ ] **UR10.07** `impl` Separate the sample's VR rig, OpenVR manifest, and startup-settings generation from its gameplay code, so the gameplay assembly references only portable projects and the desktop build adds the VR part.
- [ ] **UR10.08** `impl` Bring the sample's cooked-world serializer under the platform cook target from UR03.03, or replace it with the engine's cooked format, so desktop and web load the same world asset.

**Acceptance:** the same compiled game code runs on desktop and in the browser.

## UR11 — Editor Browser Publishing On The Unified Path

- [ ] **UR11.01** `impl` Replace `BrowserWorldPublishExporter` with this flow:
  1. Cook the startup world's web closure.
  2. Build the game assemblies.
  3. Publish the browser host with the engine assemblies, game assemblies, and selected leaves.
  4. Write the launch descriptor.
  5. Activate the output atomically, reusing the branch's staging and rollback.
- [ ] **UR11.02** `impl` Before publishing, report web-unsupported components and features per world with scene paths and reasons. Unsupported required features block the publish; optional ones are listed.
- [ ] **UR11.03** `impl` Ship a player shell page (canvas, loading progress, errors, audio unlock) separate from the developer harness page.
- [ ] **UR11.04** `impl` Keep the CLI entry point (`--build-project <project> --build-platform BrowserWebGPU`) and the editor Build Project action stable. Browser publishing must also work from a packaged editor, not only a source checkout.
- [ ] **UR11.05** `verify` Publish the parity target and a lit, textured, animated test world; play them in the browser and compare with desktop.

**Acceptance (U3):** a published project plays in the browser from the editor's normal build action.

## UR12 — Networked Browser Client

- [ ] **UR12.01** `impl` Implement a WebSocket transport leaf over the transport contract (`INetworkTransportBackend` and `IDatagramTransport`, already separated from the desktop socket implementation in `XREngine.Runtime.Net.Sockets`), plus the matching server gateway.
- [ ] **UR12.02** `impl` Carry over the mobile TODO's MW11 requirements: bounded queues, backpressure, reconnect/resync, suspension, authentication, origin and credential policy, and optional voice.
- [ ] **UR12.03** `verify` Validate against the real server path under throttling, disconnects, and app switching.

**Acceptance (U5):** the browser client joins and plays through the production server path.

## UR13 — Performance, Runtime Mode, And Size

- [ ] **UR13.01** `verify` Measure the interpreter with representative worlds (CPU per frame, startup, memory) early. Do not wait until feature completion.
- [ ] **UR13.02** `owner` Qualify AOT: generated serialization and registration metadata, trimming roots, build time, download size, runtime cost. Choose the shipping mode from measurements (decision D11).
- [ ] **UR13.03** `impl` Reduce download size through trimming, lazy assembly loading, and streamed content, against the [readiness budgets](../../progress/rendering/mobile-browser-readiness.md#devices-and-measurable-budgets).
- [ ] **UR13.04** `verify` Demonstrate steady-state frames with no recurring managed allocations in simulation, visibility, recording, and submission; record unavoidable browser/API allocations separately.
- [ ] **UR13.05** `verify` Bound linear-memory use across the .NET heap, native Jolt heap, staging, and retained content; test repeated world load and unload.

## UR14 — Lifecycle And Recovery

- [ ] **UR14.01** `verify` Carry over the mobile TODO's MW10 recovery and lifecycle matrix to the unified runtime: device loss and reconstruction, hide/show, lock/unlock, orientation during loading, canvas removal, repeated load/unload, and explicit restart.
- [ ] **UR14.02** `impl` Reject stale asynchronous completions after teardown or device replacement.

## UR15 — CI, Hosting, And Evidence

- [ ] **UR15.01** `impl` Extend the browser compile lane into a build and publish lane next to the Windows desktop lane. [portable-browser-compile.yml](../../../../.github/workflows/portable-browser-compile.yml) already runs [Test-PortableBrowserCompile.ps1](../../../../Tools/Test-PortableBrowserCompile.ps1) on `ubuntu-latest`; it cannot pass there until UR00.08 fixes the directory casing, and it should use the SDK pin from D5.
- [ ] **UR15.02** `owner` Propose a headless-browser smoke harness (decision D9) that boots a cooked world and checks startup, rendering, and diagnostics.
- [ ] **UR15.03** `impl` Document production hosting: HTTPS, MIME types, compression, immutable caching, bootstrap revalidation, and CSP. Add cross-origin isolation only if threads are adopted.
- [ ] **UR15.04** `verify` Run the physical-device matrix from the mobile TODO (MW00.06, MW12.07) with the evidence template in its completion section.
- [ ] **UR15.05** `impl` Publish user-facing build, publish, hosting, support-matrix, and troubleshooting docs after validation.

**Acceptance (U4, together with UR13, UR14, and UR16):** reproducible clean publish and recorded device evidence.

## UR16 — Retire The Separate Browser Runtime

After U3, subject to decision D12:

- [ ] **UR16.01** `impl` Remove `BrowserMeshComponent`, `BrowserSpinComponent`, `SceneBootComponent`, and the browser registration manifest and generator.
- [ ] **UR16.02** `impl` Remove `BrowserCooked*` scene and instance DTOs and the `BrowserSceneSession` content, motion, collision, and animation paths.
- [ ] **UR16.03** `impl` Remove `BrowserRenderPipeline`, its packet types, and `browser-render-pipeline.js` once the engine pipeline covers their cases.
- [ ] **UR16.04** `impl` Remove `BrowserCpuAnimator`, `BrowserCookedAnimationPlayer`, and `BrowserKinematicCharacter`.
- [ ] **UR16.05** `impl` Remove `BrowserWorldPublishExporter`, `Tools/BrowserContentCooker`'s browser-only recipe format (keeping the generalized packager), and the Python scripts (`Tools/Generate-BrowserRegistrations.py`, `Tools/Reports/audit_browser_dependencies.py`, `Tools/Shaders/cook_browser_shaders.py`).
- [ ] **UR16.06** `impl` Keep the developer harness page only if it still exercises the unified runtime; otherwise remove it.
- [ ] **UR16.07** `impl` Close or rewrite superseded mobile TODO rows and progress docs; move durable content into stable docs.

## Mobile TODO Carry-Over

| Mobile TODO item | Disposition on the unified path |
| --- | --- |
| MW00 (scope, budgets, devices) | Still applies; budgets and device matrix are reused. |
| MW01 (same-identity portable profiles) | Superseded by the implemented whole-project layout; remaining qualification is in the native subsystem integration checklist and UR01. |
| MW02 (browser host) | Host contracts and canvas code carry into UR02. |
| MW03 (batched bridge) | Carries into UR04; its acceptance evidence still applies. |
| MW04 (WGSL artifacts) | Artifact format and Slang route carry into UR05; the separate browser material generator is superseded. |
| MW05 (WebGPU resources and submission) | Carries into UR04; acceptance still applies. |
| MW06 (focused browser pipeline) | Superseded by UR06; the branch pipeline is reference only. |
| MW07 (cooked content) | Delivery code carries into UR03; browser-only DTOs are superseded. |
| MW08 (interaction and services) | JavaScript services carry into UR08 and UR09; bounded animation and collision profiles are superseded by engine components. |
| MW09 (compute/indirect) | WGSL kernels become engine pipeline ports in UR06. |
| MW10 (recovery, budgets) | Carries into UR13 and UR14. |
| MW11 (multiplayer) | Carries into UR12. |
| MW12 (publish, hosting, validation) | Carries into UR11 and UR15. |
| MW-D01–MW-D06 (deferred) | Still deferred. |

## Deferred

- **Browser-hosted editor:** a separate design after the runtime ships. It needs ImGui for WebAssembly with a WebGPU backend, a compilation service, server-side import, and a virtual file system.
- **WebXR:** a separate presentation and input leaf.
- **WebGL2:** a separate backend under the [browser renderer design](../../design/rendering/browser-wasm-renderer-design.md).
- **Multithreaded browser runtime:** after .NET WebAssembly threading with native relinking is reliable and measurements justify it.
- **PWA/offline packaging and native mobile applications.**

## Validation Matrix

| Area | Required evidence |
| --- | --- |
| Build | The build gate after every workstream; clean publish |
| Boot | Engine world lifecycle in the browser through the portable engine host; absent desktop services; named failures for missing required services |
| Rendering | OpenGL/Vulkan/WebGPU captures of the same worlds; orientation, depth, linear/sRGB, alpha, transparency, shadows, tonemapping; resize and device loss |
| Content | Same world assets; cooked variant selection; cold/warm cache; failure and cancellation cases |
| Physics | Desktop/browser Jolt parity on matched scenes; PhysX-only rejection |
| Audio/input/UI | Gesture activation, spatial audio, touch/keyboard/gamepad, IME, UI hit testing |
| Game code | Same game assemblies on desktop and web; blocked-reference report |
| Performance | Interpreter/AOT comparison; frame-time percentiles; allocations; download size; memory peaks on reference devices |
| Desktop preservation | OpenGL/Vulkan editor smokes and targeted tests after each shared-contract change |

## Definition Of Done

- The browser runs the engine's own worlds, components, renderer, pipelines, physics, audio, input, and UI from the same assemblies as desktop, through the same engine facade and host services.
- Editor browser publishing produces a playable site from the project's real assets and game code, with named reports for unsupported features.
- Only platform leaves and cooked GPU/format variants differ between desktop and browser.
- The separate browser runtime is removed, and superseded mobile TODO rows are closed.
- Device, performance, recovery, hosting, and CI evidence is recorded.
