# Physics-chain compute backend contract

Physics-chain GPU simulation is selected explicitly. The dispatcher depends on
`IPhysicsChainComputeBackend`; renderer-specific casts, raw buffer handles,
copy commands, and readback details belong only in the backend adapter.

## Implemented GPU solver path

`PhysicsChainGpuKernelMask` and `PhysicsChainKernelBucket` select two families:

| Family | Shader | Work ownership |
| --- | --- | --- |
| `ShortLinear` | `PhysicsChain.comp` | One invocation advances one linear tree in parent-before-child order. The current cutoff is 32 particles. |
| `BranchedOrLong` | `PhysicsChainBranched.comp` | One workgroup advances one tree through precomputed depth ranges. Storage visibility and workgroup barriers separate dependent depths. |

`GPUPhysicsChainDispatcher.Kernels.DispatchSpecializedPhysics` uses GPU-authored
active lists and indirect arguments for these families. Each shader runs the
tree's resolved substeps inside its dispatch. `GPUDispatchGroupKey` contains
dispatch isolation, not loop count. `PhysicsChainWorld.Clock` resolves the
batched GPU cadence and substeps through `PhysicsChainSimulationClock` before
submission. One shared CPU/GPU reset, teleport, and integration contract
remains open.

Dynamic particle state and static particle templates use separate buffers.
Source versions control seed/reset and static-template uploads. Dynamic
per-tree headers carry the resolved loop count and time scale. Headers and
affine transforms use retained mapped input pages. Exact versions control
dirty-range publication. The retained shader field set still needs its audit.

The dispatcher keeps eight mapped input pages. Before a batch starts, it rounds
the required element counts to buffer capacities and keeps the largest target
for each input type until the pages retire. It prepares a page only when a
buffer is missing, too small, or not mapped. Before it replaces a buffer, it
checks the page's fence and native content-reuse state. It records the producer
renderer before it allocates the first buffer, including when preparation
stops partway through. A page that cannot be reused stays pending. A failed
map or capacity check reports once for that page and target, then retries on a
later frame. When all pages meet the target, the capacity check does not poll
the pages. The selected page still gets its own safety and capacity checks
before a CPU write.

`GPUPhysicsChainDispatcher.LastFenceFailureObservation` copies the latest
input or output page fence failure under its observation gate. It records
the page kind and index, input submission ordinal or output producer epoch,
observation frame and stage, fence rental ID, and the marker's first failure
diagnostic. Failure paths copy this evidence before marker disposal. Missing
fences have rental ID zero and no marker failure record. The snapshot does not
retain a fence or page object.

Match `FenceRentalId` with
`VulkanRenderer.CaptureGpuFenceFailureHistory()` to separate failure origin
from a later page observation. The renderer keeps a bounded history of value
records; see [retained Vulkan fence failures](../rendering/render-pipeline-resource-lifecycle.md#retained-vulkan-fence-failures).
These diagnostics do not change input/output failure counters, native reuse
checks, quarantine, or selective readback behavior. No solved physics data
is read back for this inspection. A failed marker remains a failure even if
a separate native content-reuse check later permits page reuse.

## Global GPU chain debug

Selected chain debug uses one bounded compact batch per world. The batch caps
selection at 256 chains and 16,384 particles. A compute pass writes point and
line instance storage and one 20-byte indexed indirect command. Two indexed
draws use point input topology; their geometry shaders produce points and
lines. The pass completes with shader-storage and command visibility. It
performs no CPU readback. No selected chain means no batch generation or draw.

Each batch owns its buffers, mesh renderers, and compute program. It borrows
the shared cached shader from `ShaderHelper` and must not destroy that shader.
Successful content stays fixed for all views in the same render frame. The
renderer must match the simulation output owner. A later frame can mutate the
single retained batch only after deferred authoring uses end. Vulkan also
requires the original renderer and exact native buffer content to report
Ready. If deferred or native work still uses the batch, that frame skips debug
generation and drawing. Debug output is not guaranteed on every frame.
OpenGL uses its ordered context and buffer-update contract.

The Advanced pipeline draws selected GPU chains in a depth-tested late pass.
It loads the existing HDR color and native visibility depth, enables depth
writes, and uses `LessEqual`. This hook does not draw the world's general
physics debug frame. Capture policy can exclude the hook. See the
[Advanced debug contract](../rendering/advanced-render-pipeline.md#selected-gpu-chain-debug).

Raw mesh requests, compute and copy operations, and physical frame payloads
retain an `IRenderResourceLeaseOwner`. `RenderResourceLeaseOwner` joins the use
count and retirement state in one atomic value. Retirement stops zero-use
resurrection. The final counted release disposes owned storage once, on that
releasing thread, through public deferred resource disposal. Transfer from a
counted retiring use remains legal. Independent admission must hold a use
before it accesses owned storage.

`TryAcquireActiveUse` atomically rejects admission after retirement. Debug
generation holds this use through both draw enqueues and releases it in
`finally`. A reuse check permits exactly the caller's use; every other counted
user blocks storage changes. Buffer growth initializes the replacement before
it publishes the new allocation and disposes the old one.

Each available Vulkan `ComputeDispatchOp` retains its own sealed snapshot
storage. The operation registers snapshot ownership before it copies bindings,
so partial-copy and enqueue failures release acquired uses. A detached authoring
copy owns separate snapshot storage. Frame-plan lowering copies those bindings
before it releases the operation. Retained operations cannot be reset.

Vulkan revalidates captured particle/static source identities before native
materialization. A retired shared source rejects the debug request. The debug
batch lease does not keep the simulation arena alive across replacement. The
five-argument compute backend method remains available; a backend that does
not implement the leased overload rejects a non-null owner as `Unsupported`.

`PhysicsChainShaderContractTests` and `PhysicsChainGpuKernelFamilyTests` cover
record layouts and source contracts. `PhysicsChainGpuDependencyOrderingTests`
and `PhysicsChainGpuBranchedDependencyOrderingTests` provide GPU ordering
checks. The source audit on 2026-10-06 did not run these tests. See the
[validation plan](../../work/testing/physics/physics-validation.md#physics-chain-scale)
for runtime and hardware acceptance.

## Required capabilities and failure behavior

A backend used by the current pipeline must report all of these capabilities:

- compute dispatch;
- shader-storage visibility barriers;
- device-local buffer copies used for resident growth and selective gather;
- asynchronous fence/readback support.

Capability selection produces a queryable `GPUPhysicsChainBackendStatus` with a
state and diagnostic. `Ready` is the only state that may execute GPU work.
`Unavailable`, `Unsupported`, and `Disabled` are visible outcomes. Pending work
is retained for a later retry where appropriate. An explicitly selected GPU
path never runs CPU simulation merely because adapter creation, capacity,
shader compilation, allocation, dispatch, copy, fence creation, or readback
failed. Diagnostics are rate-limited, while status remains queryable every
frame.

Capacity and synchronization failures follow the same rule: reject the work,
preserve valid resident state, and report the reason. No particle, collider,
palette, bounds, active-ID, or readback range may be truncated.

## Retained rigid rest inputs

`PhysicsChainComponent.EnableRigidGpuRestInputCache` is an explicit runtime
option. It defaults to false and is not serialized. The Math Intersections
GPU-skinned scenario enables it after rig construction and copies the option
to benchmark instances. This cache changes CPU input preparation only. It
keeps the existing GPU solver, input records, palettes, bounds, and clock.

For one supported standard-transform tree, the range retains child rest
matrices relative to the root. A warm capture reads the current root and its
parent input, expands the retained matrices, and publishes the normal matrix
span and spatial data. All existing world dependency and compatibility checks
remain active. Cache rejection uses ordinary GPU input preparation.

The caller must enable or rearm the cache at a quiescent authoring boundary.
Filtered child property notifications block reuse before an authored write.
The block remains after the write; ordinary captures cannot rearm it. After
authoring ends, toggle the option off and on to rearm. Topology replacement
also discards the old certificate. Notification-suppressed child writes require
explicit disable and rearm. The option does not make concurrent transform
authoring atomic.

Capture and consumption check the certified input generation and the world
ownership generation. This prevents a changed or newly shared child from
reusing an old template. Topology replacement, opt-out, and slot retirement
detach the child listeners. `RigidGpuRestInputCacheDiagnostics` reports
cumulative hits and misses plus the current blocked range count.

## OpenGL 4.6 mapping

`OpenGLPhysicsChainComputeBackend` is the first implementation. It owns the
only `OpenGLRenderer` checks in this subsystem and maps the contract as follows:

| Contract operation | OpenGL mapping |
| --- | --- |
| Ensure storage is GPU-ready | create/find `GLDataBuffer` and allocate storage |
| Device-local range copy | `glCopyNamedBufferSubData` |
| Pass completion visibility | renderer memory barrier with the pass's explicit barrier mask |
| Asynchronous completion | renderer-owned `XRGpuFence`/GL sync |
| Delayed staging read | buffer-subdata read only after fence completion |

World scheduling and `GPUPhysicsChainDispatcher` do not cast to
`OpenGLRenderer`. In-flight entries retain the adapter that submitted them, so
polling or teardown cannot accidentally use a newly active renderer backend.

OpenGL exposes no asynchronous-compute claim. Dispatches execute on the owning
graphics context with explicit storage/command visibility barriers.

## Vulkan mapping

Vulkan implements the same logical resource and synchronization contract; it
does not change world records or output semantics:

| Contract operation | Vulkan mapping |
| --- | --- |
| Resident storage | device-local storage buffers with stable arena offsets |
| Small dirty upload | frame-slot persistent/host-visible staging followed by a narrow copy |
| Device-local growth copy | `vkCmdCopyBuffer` for the live prefix at an explicit rebuild boundary |
| Compute dispatch | bound compute pipeline/descriptors, direct or indirect dispatch |
| Pass visibility | `vkCmdPipelineBarrier2` buffer barriers with compute/transfer/indirect/vertex consumers named explicitly |
| Completion/lifetime | timeline semaphore value or renderer fence represented by `XRGpuFence` |
| Selective readback | transfer to a rotating host-visible staging slot, mapped only after non-blocking completion polling |

The Vulkan adapter must advertise `Ready` only after storage-buffer limits,
descriptor capacity, compute queue support, synchronization2/barrier support,
indirect dispatch support when requested, and staging memory are validated. A
dedicated compute queue is optional and remains disabled until a GPU trace
shows useful overlap without graphics contention. Queue-family ownership
transfers must be explicit if it is enabled.

## Direct3D 12 mapping

DX12 remains a later backend. Its explicit mapping is committed here so RHI
work cannot hide synchronization requirements: default-heap UAV buffers,
upload/readback ring resources, `CopyBufferRegion`, compute PSO/root signature,
direct or indirect dispatch, UAV/transition barriers, and fence values for
resource retirement and readback polling. Until implemented and capability
tested, selecting DX12 physics-chain compute returns `Unsupported`; it never
falls back to CPU.
