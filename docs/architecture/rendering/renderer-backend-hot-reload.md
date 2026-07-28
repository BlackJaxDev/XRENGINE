# Renderer Backend Hot Reload

Status: shader reload and managed method-body reload are implemented. OpenGL
collectible replacement is implemented. Vulkan collectible replacement has a
known process-global NVIDIA Streamline defect and currently requires a full
editor restart for structural changes (updated 2026-07-27).

Renderer development has three reload levels. Shader dependency changes rebuild
only affected programs and pipelines. Compatible method-body edits use .NET Hot
Reload. Structural OpenGL edits can build a new collectible backend generation
and replace the renderer without restarting the editor. The same mechanism
exists for Vulkan, but must not currently be used after Streamline has
initialized; see [Current Vulkan limitation](#current-vulkan-limitation).

## Assembly and ownership boundary

`XREngine.Runtime.Rendering` is the stable host. It owns logical assets,
windows, viewports, render pipelines, the backend ABI/catalog, replacement
coordinator, state preservation, and process-lifetime native callback bridges.
It does not reference or construct concrete OpenGL or Vulkan renderers.

`XREngine.Runtime.Rendering.OpenGL` and
`XREngine.Runtime.Rendering.Vulkan` are leaf modules. Each owns its API
wrappers, shader/program or pipeline implementation, device/context resources,
workers, UI renderer resources, diagnostics, and backend-specific native
integration. The leaf projects reference the stable host, never each other or
an executable.

Process-global native SDK ownership is a stricter boundary than ordinary leaf
implementation ownership. The stable host already owns native callback
bridges, but Streamline runtime/library state is still held by collectible
Vulkan-generation statics. That incomplete ownership split is the current
Level 3 Vulkan limitation.

The applications have packaging references so both leaf DLLs are copied, while
runtime creation goes through `IRendererBackendCatalog`. Production and AOT use
static registration. Renderer-development mode may replace the registration
with a collectible generation using the same contract.

## Stable module contract

`IRendererBackendModule` exposes metadata, capability routing, renderer
creation, and cooperative unload preparation. Metadata validates:

- ABI version, backend ID, entry point, generation, and build hash;
- target framework and process architecture;
- the complete staged-file hash set; and
- explicit reload limitations.

Factories must pass `RendererBackendCreateContext.ModuleGeneration` into the
renderer constructor. The renderer installs that generation before creating API
wrappers, so no wrapper can capture the previous generation identity.

The loader uses one collectible `AssemblyLoadContext` and
`AssemblyDependencyResolver` per generation. Stable engine assemblies are
unified with the default load context. Duplicate contract DLLs, unknown search
roots, incompatible native generations, invalid hashes, ABI mismatches, and
wrong architectures are rejected before activation.

Generation directories are immutable. A manifest is atomically published only
after a successful backend-only build and complete shadow copy. The active,
candidate, and last-good generation have distinct ownership. Retention is
bounded to at least two and at most sixteen generations; the editor default is
three. Abandoned partial and build directories are reclaimed on every build and
completed builds remove their disposable output in `finally`.

## Replacement transaction

`RendererReplacementCoordinator` serializes Level 3 and device-loss work. It:

1. coalesces/cancels work only before teardown;
2. selects all windows using the retiring backend;
3. stops new wrappers and generation-owned work publication;
4. drains GPU work and joins workers on the render thread;
5. unregisters UI, debug, XR, vendor, and native callbacks;
6. retires API wrappers, deferred resources, native handles, and the API object;
7. calls module unload preparation and verifies the old collectible context;
8. validates and registers the candidate;
9. recreates renderers, swapchains/contexts, UI resources, and physical
   pipeline generations from stable logical state;
10. waits for a backend-validated complete scene frame before accepting the
    candidate (recovery clears and overlay-only frames do not count); and
11. uses the same primitives to clean the candidate and restore last-good on
    failure.

No global engine lock is held while building, draining, loading, collecting, or
waiting for the first frame. The coordinator publishes explicit states and
phase timings. Old shader, upload, pipeline, and command results carry a source
or module generation and are rejected after retirement.

OpenGL teardown unregisters `KHR_debug`, disposes all Silk/native API objects,
and clears wrapper caches only after every wrapper has retired. ImGui platform
callbacks enter through `RendererImGuiViewportCallbackBridge`, whose unmanaged
entry points live in the stable assembly; the collectible adapter registration
is removed during teardown. Vulkan teardown stops workers and recording,
empties retirement/lifetime registries, unregisters debug and vendor callbacks,
and recreates the full Vulkan instance/device/allocator/swapchain resource
graph.

## Preserved and reset state

The stable host preserves the world, logical assets, unsaved authoring state,
selection, hierarchy/inspector targets, undo/redo, cameras, viewport layout,
pipeline choice, effective settings, and ImGui context/docking state.
Backend API handles, wrapper caches, command and descriptor pools, swapchains,
UI renderer resources, queries, transient pipeline generations, and temporal
history are recreated or intentionally reset. A global overlay is shown during
the non-idle transaction so stale/uninitialized output is never presented as a
completed frame.

GPU-only resources without a reconstruction source are an explicit reload
limitation. Stale backend wrappers validate retirement/generation state rather
than invoking disposed module code.

## Shader and managed hot reload

`ShaderSourceDependencyIndex` maintains weak reverse dependencies from
normalized top-level/include/snippet paths to loaded shaders and generated
variants. Asset create/change/rename/delete events are debounced, readable
files are stabilized, and watcher callbacks never compile synchronously.
Every source change advances a monotonic revision.

OpenGL and Vulkan compile candidates while last-good remains active. A result
publishes only when its source revision is current and at a legal render
boundary. Interface changes invalidate material/reflection, descriptor,
pipeline, transform-feedback, vertex-input, and recorded-command state.
Replaced GPU objects retire behind the backend's in-flight-use boundary.
Vulkan shader-module and pipeline-layout replacement first blocks new native
graphics-pipeline jobs and drains the bounded in-flight compile. A source reload
can therefore pause for the current driver call, but it cannot free native
dependencies underneath `vkCreateGraphicsPipelines`.

Vulkan source invalidations are batched on the render thread so a multi-stage
program cannot observe a partially invalidated dependency set. Shader modules,
program layouts, pipeline build requests, mesh prepared state, cached pipelines,
pipeline libraries, and recorded-command dependency signatures carry the
program link generation. Results compiled or recorded against an older
generation are rejected or rebuilt even when the managed program object itself
did not change identity.

The `Watch-Editor-RendererDevelopment` task applies supported method-body
deltas. Decline any `dotnet watch` process restart for a rude edit and use
`Build and Reload Renderer`; structural edits are a Level 3 operation. For
Vulkan, decline the process restart only for a compatible method-body edit.
Accept a full editor restart for a structural edit until the Streamline
limitation below is resolved. Metadata-update handlers invalidate renderer
caches and report which mechanism handled the change. Level 2 and Level 3 share
the coordinator gate and cannot run concurrently.

## Developer workflow

Use the `Editor (Renderer Development)` launch profile or
`Start-Editor-RendererDevelopment-NoDebug`. Open **Tools > Renderer
Development** to inspect the active backend/generation/hash/load context,
current phase, build diagnostics, shader counters, phase timings, and reload
counters. The panel provides shader reload, same-generation restart, build and
reload, candidate retry, last-good rollback, OpenXR-session restart, and copy
diagnostics actions. Automatic backend builds are opt-in and debounced.

MCP exposes `get_renderer_reload_status`, `reload_renderer_shaders`,
`restart_renderer`, and `build_and_reload_renderer`; ordinary authorization,
read-only, allow-list, and deny-list policy still applies.

Use this current routing matrix:

| Change | Vulkan action | Process retained |
|---|---|---|
| GLSL, include, or generated shader source | **Reload Shaders** or `reload_renderer_shaders` | Yes |
| Compatible C# method body | `dotnet watch` Hot Reload | Yes |
| Recreate device, swapchain, or backend resources without changing types | **Restart Renderer** or `restart_renderer` | Yes |
| Structural OpenGL leaf edit | **Build and Reload Renderer** | Yes |
| Structural Vulkan leaf edit | Full editor restart | No |

### Current Vulkan limitation

The `build_and_reload_renderer` Vulkan path can terminate the editor with
Windows fast-fail `0xc0000409` when a candidate collectible generation
reinitializes NVIDIA Streamline. Each collectible generation currently owns
independent managed Streamline state and function pointers, but
`sl.interposer.dll` and its SDK state are process-global. Shutting down/freeing
the old generation and initializing the candidate is therefore not yet a legal
collectible boundary.

This failure is native and cannot be rolled back by the managed replacement
coordinator. Do not invoke structural Vulkan reload after Streamline
initialization. The required architectural fix is a stable-host,
process-lifetime Streamline broker (or a pre-teardown rejection that visibly
requires an editor restart). Shader reload and same-generation Vulkan restart
do not unload the collectible module and are the supported fast iteration paths.
See the
[2026-07-27 investigation](../../work/investigations/rendering/vulkan-uber-pipeline-stall-black-recovery-2026-07-27.md)
for the crash evidence and validation captures.

## Reload limitations

- `XREngine.Runtime.Rendering`, ABI, target-framework, dependency-version,
  process-architecture, and driver changes require rebuilding/restarting the
  stable host.
- A loaded native DLL can only be reused when its registered identity/hash is
  compatible. It is never silently replaced.
- NVIDIA Streamline is process-global. Until its runtime ownership moves out of
  the collectible Vulkan module, structural Vulkan replacement requires an
  editor restart even when the native DLL hash is unchanged.
- Mixed-backend windows are independent only when they share no process-global
  integration; same-backend windows always move as one atomic generation.
- Active OpenXR/OpenVR blocks ordinary replacement. OpenXR has an explicit
  editor-preserving stop/reload/restart transaction; unavailable hardware does
  not weaken desktop reload. Unsupported active XR/native integrations fail
  visibly and name the boundary.
- An explicitly requested accelerated feature is never silently disabled and no
  reload failure switches graphics API or CPU fallback.

See the
[implementation closeout](../../work/progress/rendering/rendering-backend-hot-reload-closeout-2026-07-25.md)
for inventory, timings, stress results, and validation evidence.
