# Portable browser render capabilities

Source implementation on `codex/webgpu-readiness-audit`, September 28, 2026.
Builds, tests, browser execution and physical-device validation were deferred by
the user. This record describes source behavior, not verified GPU results.

`IRuntimeRendererHost.TryGetBackendCapability<T>` exposes five focused contracts
implemented by the WebGPU host. Resource creation/destruction is
`IBrowserResourceCapability`; `IBrowserFrameSubmissionCapability` executes the
existing packet's opaque canvas pass and indexed draw commands;
`IBrowserTextureCopyCapability` submits texture copies;
`IBrowserPresentationCapability` describes the drawable canvas without retaining
its swapchain texture; `IBrowserCompletionCapability` asynchronously observes
queue work submitted before the request. `IBrowserRendererHost` composes these
contracts with browser session lifecycle for existing callers. No desktop
stateful renderer API is required by the portable source profile.

Copy is an explicit cold control call, ordered on the same WebGPU queue as frame
packets, so packet ABI v2 is unchanged. It accepts distinct live textures owned
by the same session with nonnegative origins and a positive rectangle wholly
inside both extents. Both textures have the current fixed single-mip
`rgba8unorm-srgb` encoding. Source textures receive `COPY_SRC` usage on
creation; destination textures already have `COPY_DST`. The JavaScript executor
checks resource kind, owner, slot generation, dimensions and extent before
encoding the copy. The managed host checks its live handle set and rejects
operations outside Ready state. Handle removal invalidates future commands
immediately, while physical storage continues to retire after submitted work.
An in-flight copy can therefore finish after either handle is released.
Copies mutate GPU contents without rewriting immutable CPU descriptors. A fresh
session must replay the application's copy operations; automatic recovery replay
is not implemented. `gpuCopiedBytes` counts texture-copy traffic separately from
the packet bridge's CPU `copiedBytes` counter.

The explicit completion task uses `queue.onSubmittedWorkDone()` and rejects
results from a disposed, failed, lost or replaced session. Managed cancellation
stops the caller's wait; it does not cancel already submitted GPU work. This
call is for deliberate synchronization or diagnostics, never a recurring
per-frame wait. Submission itself remains synchronous only through packet
consumption/queue enqueue, not through GPU completion or presentation. The
canvas texture is acquired within each packet execution, with no persistent
output texture handle exposed to managed code.

The present pass is one color/depth clear and opaque indexed draws. The
resource contract currently covers static meshes, uploaded RGBA8 textures
and opaque materials only. No arbitrary buffers, texture formats/mips,
render/compute pipeline construction, general render graph, readback, error
scope tickets, or automatic desktop resource lowering is claimed. The active
[browser runtime TODO](../../todo/platform/unified-desktop-browser-runtime-todo.md)
continues to track the broader integration and runtime acceptance.
