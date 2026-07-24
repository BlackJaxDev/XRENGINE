# Runtime Rendering Host Capability Inventory

This inventory records the P4.5 split of the former 1,800-line
`IRuntimeRenderingHostServices` contract. The focused interface files are the
authoritative member-by-member inventory: every member from the former
composite is declared exactly once in one of the contracts below, and the
composite now contains only interface inheritance.

The pre-split call-site scan found 753
`RuntimeRenderingHostServices.Current` references in 112 files under
`XREngine.Runtime.Rendering`. The first migration slice moved profiling,
statistics, frame-output telemetry, and scheduling call sites to cached focused
accessors. Remaining `Current` references are compatibility debt and can move
capability-by-capability without changing installation semantics.

| Capability contract | Former responsibility regions | Lifecycle / installation owner | Thread affinity | Optionality |
| --- | --- | --- | --- | --- |
| `IRuntimeRenderSettingsServices` | Import/shader and shadow settings | Application composition root; stable for a host installation | Read from any thread; change callbacks follow host timer policy | Defaults are available before installation |
| `IRuntimeRenderFrameTimingServices` | Frame timing, frequently-read render state, effective frame policy | Application composition root | Allocation-free reads from render/update threads | Defaults are available before installation |
| `IRuntimeRenderSchedulingServices` | Frame subscriptions, render/app/window dispatch, task pumps | Application composition root | Methods enforce the named target thread | Required; missing access fails before work is queued |
| `IRuntimeRenderDiagnosticsServices` | Pipeline context and host logging | Runtime host | Any thread; pipeline scopes are render-thread scoped | Optional allocation-free no-op when uninstalled |
| `IRuntimeRenderStatisticsServices` | All render, GPU, VR, RVC, Vulkan, memory, and frame telemetry members | Runtime host telemetry owner | Render hot path; implementations must not allocate on disabled paths | Optional allocation-free no-op when uninstalled |
| `IRuntimeRenderDebugDrawingServices` | Debug shapes, transform IDs, spatial diagnostics, GPU-physics maintenance hooks | Editor/runtime host | Render thread unless the implementation explicitly queues | Optional allocation-free no-op when uninstalled |
| `IRuntimeRenderProfilingServices` | CPU profiling scopes | Runtime host profiler | Any rendering thread | Optional allocation-free no-op when uninstalled |
| `IRuntimeRenderAssetServices` | Asset resolution/load/eviction, file IO, texture streaming and background import jobs | Application asset composition root | IO/job thread; GPU finalization follows backend contract | Required |
| `IRuntimeRendererFactoryServices` | View/player lookup, render pipeline/renderer/window/panel factories and teardown | Application composition root | Creation/teardown follow window/render ownership | Required |
| `IRuntimeRenderPresentationServices` | Window panel policy, desktop output graph, VR/OpenXR, mirror composition and presentation telemetry | Desktop/VR application composition root | Window/render/XR pacing threads as documented by each member | Required |
| `IRuntimeRenderBackendInteropServices` | Backend object destruction, active viewport checks, pipeline overrides, material/compute/blit interop | Installed renderer backend and host bridge | Render thread | Required |

`RuntimeRenderingHostServices` stores the hot settings, frame-timing, optional
telemetry, debug, diagnostics, and profiling references directly when the host
is installed. Access therefore performs a static field read and interface call;
there is no service-provider lookup, boxing, delegate capture, or per-frame
adapter allocation.

Required accessors throw an `InvalidOperationException` naming the missing
capability and installation action. `Install` returns a replacement-safe scope:
disposing a stale scope cannot tear down a newer installation. `Reset` restores
the uninstalled singleton, whose only supported focused behavior is settings
and timing defaults plus optional diagnostics/statistics/debug/profiling no-ops.
