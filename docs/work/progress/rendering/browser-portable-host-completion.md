# Portable browser host implementation

Source delivery on `codex/webgpu-readiness-audit`, September 28, 2026. The supported
source profile is the minimum engine scene plus indexed unlit WebGPU canvas, using
the untrimmed interpreter and caller-thread scheduling. Builds, tests, audit-tool
execution and browser/device validation were deferred at the user's request.

## Implemented boundaries

| Responsibility | Source implementation |
| --- | --- |
| Dependency inventory | Declared and evaluated report modes, recursive project references, restored transitive package/assets, imports and initializer/API candidates. Missing restore/source inputs fail explicitly. |
| Portability | Existing project/package/native-asset guards plus evaluated-source lexical API checks with reviewed per-file/symbol reflection exceptions. Trim/AOT flags fail until separately qualified. |
| Registration | Explicit manifest and deterministic checked-in browser factories/IDs; existing component/transform factory and renderer-catalog mechanisms; source-generated scene JSON metadata. |
| Renderer | Separate resource, pass/packet, presentation, texture-copy and asynchronous-completion capabilities, backed by the WebGPU executor. |
| Frame publication | Fixed simulation, real engine transform-buffer publication, reusable frozen renderable records, per-view culling, packet submission, prompt return to JavaScript. |
| Timing and lifecycle | At most four 60 Hz simulation steps, bounded variable render delta, history-generation invalidation, pause/freeze/resume, epoch-guarded callbacks and repeatable stop/restart. |
| Canvas ownership | Supplied canvas and session-owned device, one active host per canvas element, multiple viewports and independently owned additional canvases. |
| Surface budget | CSS-to-physical sizing, DPR capped at 1.5, longest backing edge capped at 1280 and device limits, zero-size/detach suspension and generation publication. |

`RuntimeSceneHost.Advance(float)` retains automatic publication for existing
callers. The browser uses the new overload to defer publication across fixed-step
catch-up, then calls `SwapBuffers()` once. The snapshot prevents the two views from
reading different live component states. Session structural/resource mutation is
rejected while a frame is active; ordinary component transform simulation remains
part of the fixed update. This is CPU-direct collection for admitted browser
components, not GPU-indirect visibility or an arbitrary desktop world adapter.

The registry and scene serializer do not discover assemblies to construct browser
factories. Shared untrimmed engine helpers still contain reviewed reflection,
including replication metadata discovery and legacy construction helpers; these
are inventoried explicitly rather than advertised as absent. Registration and
serialization preserve the existing desktop assembly/type identities and flat
snapshot compatibility. Browser profile IDs are separately namespaced.

Python 3.10 or later is now required for the portable source guard. Set
`XREnginePortablePythonExecutable` if the executable is not `python`; on systems
where only `python3` is installed, pass `-p:XREnginePortablePythonExecutable=python3`.
The ordinary desktop build does not run this guard. The evaluated report defaults
to Release and consumes an existing portable restore; use `--configuration Debug`
only with the corresponding restored build. No report was generated in this delivery.

## Completion and remaining evidence

All source implementation rows through the browser host are tracked separately
from acceptance in the [active TODO](../../todo/rendering/mobile-webgpu-runtime-todo.md).
The dependency and initializer inventory tools are implemented; their evaluated
results still need execution and owner review. The lexical guard is not a semantic
reachability proof. Proposed physical devices still need availability confirmation
and exact OS/browser qualification records. Expanded browser publish, serializer
round trips, GPU output, lifecycle behavior and affected desktop paths are untested.

Later integration still includes general engine resource/pass lowering, automatic
live-world bindings, streaming uploads, production material/shader cooking and
representative-world rendering. These are not prerequisites for the selected
minimum host's code completion and are not claimed by this delivery.

Details: [dependency/API audit](browser-portability-audit.md),
[static registration](browser-static-registration.md),
[render capabilities](browser-render-capabilities.md), and
[frame publication](browser-frame-publication.md).
