# Physics-chain compute backend contract

Physics-chain GPU simulation is selected explicitly. The dispatcher depends on
`IPhysicsChainComputeBackend`; renderer-specific casts, raw buffer handles,
copy commands, and readback details belong only in the backend adapter.

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
