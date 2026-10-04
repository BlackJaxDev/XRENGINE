# Caller-thread world publication identity

The caller-owned timer collects visibility and swaps world buffers before
`DispatchRenderFrame` calls `BeginRenderFrame`. The ordinary `RuntimeWorldHost`
previously subscribed its parameterless `GlobalSwapBuffers` method, which stamped
the current render ID. The following render therefore consumed a mesh submission
publication from the preceding frame. The authored GPU submission contract
rejects that mismatch instead of replaying the publication on the CPU.

The timer now chooses one canonical output ID immediately before caller-thread
visibility collection. Rendering host collection preparation and the world swap
both use that ID. `EngineTimer.CollectFrameId` remains the independent collection
counter; the rendering host's `CollectFrameId` supplies the planned canonical ID
only while the caller-thread loop is active. The world host uses its existing
explicit-ID swap overload for this topology. Noncaller swaps retain the ambient
render-ID overload and the rendering host retains the raw collect counter.

This also handles an already-offset renderer clock and restarting a caller loop:
authored source-order packages no longer assume the collection counter happens to
equal the next render ID. Collection-generation admission remains unchanged.

## Collection and render snapshots

`XRViewport.CollectVisible` still acquires the existing rendered-world snapshot
for its collection state. That token can identify the preceding render; the
optional CPU BVH family collector uses it independently of authored source-order
publication. Package preparation and full-resident ordering capture their scene,
camera, collection generation and canonical output ID directly. They do not copy
the collection state's previous world token into the prepared package.

The collection state must not publish pre-swap global resources under the future
render ID. After the world swap, render-state acquisition publishes the new
world snapshot with the freshly captured global resources. This change therefore
leaves `RenderingState` and `RenderWorldSnapshotPublication` untouched.

## Runtime evidence and limits

An ignored managed lifecycle probe drives `RuntimeWorldHost.BeginEditModeAsync`,
`EngineTimer.StepFrame`, the real viewport collection and swap methods, and
render-state world-snapshot acquisition with one resident authored mesh. It does
not substitute a copied frame algorithm or a source-text assertion.

Before the change, consecutive render/mesh-publication/package IDs were
`1/0/1`, `2/1/2` and `3/2/3`. Advancing the renderer independently before the next
caller frame produced `5/4/4`, exposing the separate collection-counter mismatch.
The corrected run aligned all three IDs and the global-resource frame across
five rendered frames, including the offset clock and a stopped/restarted caller
loop: `1/1/1`, `2/2/2`, `3/3/3`, `5/5/5` and `6/6/6`. The raw collect counter was
still `4` and `5` for the last two frames. The noncaller swap retained ambient
render ID `5` and raw collection counter `4`.

The same probe invoked the production pipeline frame-profile binding and
`BackendReadyFramePackage.TryGetFullResidentMeshOrder`. `CpuDirect` retained its
direct collection. `GpuIndirectZeroReadback` and `GpuMeshletZeroReadback` retained
their selected modes and accepted the frozen authored source order, including
after the clock offset and loop restart. All five frames retained one resident
source, with zero identity mismatches and zero required-order mismatches. During
collection, the future render ID was absent from the global world-snapshot
publication; render acquired the fresh swapped global resources.

The probe uses the pinned managed toolchain and shared narrow-build lock, with
isolated artifacts under the existing ignored validation run. The source overlay
is based on `cd4f79ed09f1c995633c528d832f6f34f4575c15`. The narrow host/probe build
completed with zero warnings and errors. Evidence is retained as
`logs/caller-scene-frame-baseline.log`, `logs/caller-scene-frame-fixed.log`,
`logs/caller-scene-frame-fixed-build.log`, `logs/caller-scene-frame-probe-build.log`
and `reports/caller-scene-frame-source.sha256` beneath
`Build/_AgentValidation/20261001-225000-lit-surface/`. No tracked tests were added.
This is managed lifecycle evidence, not physical browser GPU execution. It does
not establish rendered acceptance for ordinary authored GPU-indirect or meshlet
main worlds, change the selected submission strategies, add readbacks or fallback,
or qualify the separate desktop `StepFrame` scheduling work.
