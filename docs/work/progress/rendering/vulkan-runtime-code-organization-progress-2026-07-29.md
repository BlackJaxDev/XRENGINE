# Vulkan Runtime Code Organization Progress

Date: 2026-07-29
Branch: `master`
Implementation base: `404df57741745359ea7e7dcaa0f3a67f667c1051`
Final commit: not created; this note describes the dirty integration tree

## Scope And Baseline

This note tracks implementation of the
[Vulkan Runtime Code Organization TODO](../../todo/rendering/vulkan-runtime-code-organization-todo.md).
The work began in a heavily modified shared tree: the initial
`git status --short` contained 167 entries, including unrelated runtime
modularization, editor, model-import, documentation, dependency-report, and
submodule work. Those pre-existing changes were preserved and are not claimed
as Vulkan organization outputs.

The pre-refactor leaf build was:

```powershell
dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj `
  --no-restore -p:XREngineUseExistingNativeBridges=true
```

It completed with zero errors and 140 existing `NU1901`/`NU1902` Magick.NET
advisory warnings. The same warning baseline applies to the integrated leaf
build unless a later validation entry says otherwise.

The initial source-consumer inventory found:

- 90 test/script/document files containing an exact Vulkan implementation path;
- 65 non-Vulkan source or test files naming a nested
  `VulkanRenderer.SomeType`;
- 96 local source-reading helper implementations in the test project before
  consolidation;
- active overlapping work in runtime modularization, the desktop frame-loop
  decomposition, renderer hot reload, OpenXR, and model import.

The shared test helper now discovers Vulkan sources recursively. Architecture
tests use one-way debt baselines: removing a legacy exception is accepted,
while introducing a new stateful renderer partial or multi-type dumping ground
fails with its exact path.

## Ownership Added In This Pass

| Responsibility | New owner or contract | Integration state |
|---|---|---|
| Backend object identity | `VulkanBackendObjectRegistry`, per-type `VulkanBackendObjectBucket<T>` | Per-renderer binding slots and wrapper publication replace generic static caches. |
| Device capabilities | `VulkanDeviceContext`, immutable `VulkanDeviceCapabilities`, query/builder/reporter types | Device creation delegates capability query, feature-chain construction, snapshot publication, and reporting. |
| Command scheduling | `VulkanCommandScheduler`, `VulkanCommandSchedulingContext` | Per-renderer owner captures cache/retry policy and scheduling inputs. |
| Command recording | `VulkanCommandRecorder`, `VulkanCommandRecordingContext` | Per-renderer owner validates/reset contexts and owns native begin/end lifecycle boundaries. |
| Render graph | `VulkanRenderGraphRuntime`, immutable `VulkanRenderGraphPlan`, immutable `VulkanBarrierPlan` | Compiler/planner/allocator/barrier state is grouped behind one runtime authority. |
| Binding grammar | `VulkanResourceBindingKey`, `EVulkanResourceBindingKind` | `tex::`, `fbo::`, and `buf::` parsing is centralized and tested. |
| Resource lifetime | `VulkanResourceLifetimeTracker`, `VulkanResourceRetirementQueue` | Mutable publication/retirement registries and counters have one per-renderer owner. |
| Descriptors | `VulkanDescriptorManager` | Device-lifetime descriptor state, immutable samplers, pools, layouts, allocations, and synchronization are grouped behind one owner. |
| Pipelines | `VulkanPipelineManager` | Program-link scheduling and graphics pipeline/library caches have an explicit device-lifetime owner. |
| Desktop frames | `VulkanDesktopFrameCoordinator`, `VulkanFrameAttempt` | Integration is in progress; final state and validation are recorded below. |

Mechanical cleanup in the same pass removed empty/comment-only partials,
replaced the excluded ray-tracing prototype with a durable design note, fixed
`VkSampler` casing, renamed phase-numbered diagnostics by responsibility, and
moved transform feedback to `BackendObjects/Queries`.

Large descriptor and program files were also separated by responsibility:
descriptor buffers, fingerprints, images, uniforms, and writes; and program
bindings, linking, layouts, graphics pipelines, compute descriptors, and
compute uniforms.

## Validation Ledger

| Gate | Result |
|---|---|
| Baseline Vulkan leaf build | Passed: 0 errors; 140 existing package-advisory warnings. |
| Final integrated Vulkan leaf build at wrap-up | Passed: 0 errors; same 140 pre-existing package-advisory warnings. |
| Focused ownership and architecture tests from the last built test output | Passed: 22/22 (`VulkanSourceArchitectureGuardrailTests`, backend-object registry, resource-binding key, and runtime-manager ownership). |
| Rebuilding the focused test project | Blocked outside this work by missing `ModelBinary*`/`ModelCacheReadLimits` types in concurrent ModelingBridge cache work. |
| Runtime-rendering kernel build | Passed: 0 errors. |
| Editor build | Pending final integrated source state. |
| Vulkan phase-3 regression task | Pending final integrated source state. |
| Isolated Vulkan Unit Testing World startup | Pending final integrated source state. |
| OpenXR smoke lane | Requires an available OpenXR runtime/headset lane. |
| Allocation/performance comparison | Pending final integrated source state. |

## Remaining Integration Work

This section is intentionally updated from evidence rather than inferred from
new type names. A phase is complete only when its old mutable authority is
removed, its tests pass, and the applicable validation gates above are
recorded.

- Finish migrating legacy renderer-owned command, resource, OpenXR, ImGui, and
  frame state into the new owners.
- Remove ordinary command-recording and OpenXR dependence on thread-static
  context.
- Complete namespace-level backend-wrapper migration and update external
  nested-type consumers.
- Complete the single-type-per-file and domain-folder migration for remaining
  baseline exceptions.
- Run the runtime, XR, validation-layer, and allocation/performance gates.

## 2026-07-29 Wrap-Up

Implementation expansion stopped at the user's request after restoring a clean
Vulkan leaf build. The integrated tree now includes the explicit owner types,
namespace-level backend/frame contracts, one-type-per-file command-chain,
resource allocator, upload, shader, and OpenXR splits, and passing structural
guardrails. No commit was created.

The broad TODO remains in progress rather than being marked complete. Remaining
work includes migrating the remaining legacy stateful renderer partials and
ordinary thread-static mesh/program scopes, rebuilding the test project after
the unrelated ModelingBridge cache work settles, and running editor, Vulkan
startup, OpenXR hardware/runtime, validation-layer, and allocation/performance
gates.
