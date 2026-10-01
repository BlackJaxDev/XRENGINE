# Unified browser implementation checkpoint

Updated: 2026-10-01. This is a partial implementation checkpoint, not a playable
browser release or completed parity qualification.

## Implemented and compiled

- The browser composes the shared `Engine`, `RuntimeWorld`, registrations,
  caller-thread frame loop, asset catalog, Jolt leaf, Web Audio leaf, input
  viewport, and WebSocket transport instead of a second gameplay runtime
- The caller-thread scheduler reuses the engine's fixed/update/visibility/swap/
  render callbacks. A production-assembly CPU smoke completed 2,048 warmed
  empty frames with zero managed caller-thread allocations; this is not a
  rendered game allocation measurement
- Runtime assets use the shared serializers, hash-verified catalog entries,
  bounded reads, stale-completion rejection, and explicit teardown ownership
- The WebGPU renderer now adapts engine buffers, programs, materials, meshes,
  and batched frame commands. The implemented raster shader group is the
  explicitly authored depth/depth-probe diagnostic
- `RollingBall` gameplay is portable, with desktop VR composition in
  `RollingBall.DesktopVR`. The desktop Jolt/PhysX default is unchanged
- The editor publisher packages canonical engine assets and statically linked
  game code, with named capability failures and staged output activation
- The server gateway and browser transport share the versioned realtime
  protocol; the quaternion/wire compatibility break is intentional for this
  undeployed application

Browser interpreter/native-Jolt publishing, Editor, Server, VRClient, and the
RollingBall desktop host have successful local build logs. The shader cooker
compiled both engine depth recipes with pinned Slang 2026.8 and validated their
explicit ABI. The codec budget smoke exercised 21 valid/invalid payload cases.
These focused checks do not replace the full build/test/device acceptance matrix.

The resumed current-tree check built all 18 portable projects and completed a
fresh browser publish. Publishing emitted zero warnings/errors. The compile
sweep emitted no compiler warnings/errors, but retained `NU1900` package-audit
cache warnings caused by this host's read-only home. The host also requires the
supported in-process MSBuild task override because its separate task-host
Unix sockets are unavailable. CI uses the ordinary toolchain path and must
independently qualify clean restore, build, and publish.

## Deliberately unavailable

The production browser player advances the real world but reports that rendering
is unavailable. `DefaultRenderPipeline` rejects WebGPU output until the required
forward lighting, attachment, material, and tonemap routes exist. Engine texture
and framebuffer wrappers, lit material generation, the remaining raster shader
groups, shadows/environment/post processing, engine UI/text, and rendered sample
parity are not implemented by the depth diagnostic.

The separate browser reference runtime remains frozen and present. Its removal
is gated on genuine published-project parity. Physical-device budgets, desktop
render preservation, AOT measurements, tolerance-based cross-platform physics,
full recovery, and production networking qualification remain open.

## Browser CI scope

The browser workflow builds the pinned managed/native Jolt supply, compiles the
portable projects, publishes the host and native diagnostic, cooks the engine
depth shaders, and runs the Playwright harness. The harness checks actual depth
pixels, two real engine-world lifecycle iterations, fetched asset lifetime, and
native physics startup/teardown. It records screenshots, adapter details,
console output, and check outcomes.

CI deliberately selects software WebGPU and labels its evidence as API/shader
correctness only. It does not claim hardware performance or full sample parity.
Local Chromium execution is blocked by the current host's Unix-socket policy;
no local browser execution pass is claimed. The CI result must be attached to
the exact published commit before accepting its live checks.

The first real Chromium run for commit
`36c4bde1b7575c6b9887d61aab1b9e27c0c1bf09`
([Actions run 36912281747](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36912281747))
completed clean build/publish, Slang cooking, and local delivery. Its production
engine-world check completed two start/stop cycles with twelve caller-thread
frames each; the asset delivery/lifetime check also passed. This is a minimal
world with one scene/root transform, not RollingBall gameplay acceptance.

The same run failed the engine depth diagnostic at `Engine` static
initialization and the standalone Jolt query diagnostic in the Mono runtime.
No rendered pixel or full native-query acceptance is claimed. The bare runtime
bundle has no authored launch descriptor, so published-player auto-start was
explicitly skipped. The failures are being repaired using the preserved console
logs, screenshots, and exact published WebAssembly module.

The Jolt repair preserves typed managed APIs while moving callback import
parameters to pointer-sized ABI tokens supported by the interpreter. Its exact
published module now completes sixteen native world lifecycles, 1,920 steps,
two foundation initialization/shutdown passes, contact callbacks, raycast hits,
and disposal checks under Node. This is native WASM evidence; Chromium remains
the browser gate. The correction is published as
`2fc658ca641aad91fd419268bdabb77a91bb2352`.

The engine initializer failure was a missing catalog in diagnostic composition:
settings tracking reached the asset manager before a browser asset source was
installed. The corrected diagnostic uses the same genuine fetch-backed catalog
as production before first engine access. Published WASM checks now pass repeated
create/stop, overlapping-start supersession, cancellation, and restart. Node's
content fetch was mapped to the actual cooked files for this managed-boot check;
it is not browser transport or rendering evidence.

The resource/canvas slice adds exact-format engine texture/framebuffer
wrappers and a real-world canvas owner. It remains unqualified by that run.
The production pipeline gate stays closed until its lighting, materials,
attachments, and presentation route are genuinely available.

An explicit built-in material semantic and immutable exact-hash variant catalog
also compile. They do not yet make desktop-GLSL material factories browser-ready;
target-aware construction and real lit shader variants remain required. The
RollingBall binary format is unchanged.

The combined checkpoint `fe3a11974100a7946b6d9f768eb28646dadc1621`
([Actions run 36918408996](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36918408996))
completed clean Linux build/publish, all 18 portable compile rows, native Jolt
build/publish, Slang cooking, and canonical asset packaging. Chromium passed
the two real-world start/stop cycles, asset delivery/lifetime, native Jolt
startup/queries/callbacks/teardown, and local delivery. This is the first
Chromium acceptance of the repaired native callback ABI. No worker threads
were created by the Jolt check.

The depth render remained the only failed check. It reached the shared frame
loop, where GPU-scene mirroring eagerly generated its LOD-transition buffer
before the buffer's construction was published. The cache publication guard
correctly rejected it. A focused repair removes the premature generation and
leaves native allocation to the existing backend-binding lifecycle; the guard
is unchanged. The same frame path also eagerly constructed an unused indirect
manager and loaded desktop GLSL for CpuDirect submission; that manager is now
created at its actual GPU-use boundary. The static diagnostic now steps the
real caller-thread timer and standard viewport collection/swap callbacks,
preserving output and visibility generations rather than inventing them.

The repaired published WebAssembly reaches clear, shader-module, vertex/index
buffer, pipeline, and mesh-command imports in two start/stop cycles under Node.
Eight frames per cycle complete without a publication, asset catalog, pipeline,
or generation-decline error. The fixture's orthographic camera now explicitly
uses the centered origin its three test triangles require. Both cycles prepare
and submit all three draw commands by frame three. This is boundary evidence
only: the Node imports do not execute WebGPU. Actual depth pixels, production
lit rendering, and RollingBall browser play remain unqualified.

The run used Google SwiftShader software WebGPU. Its screenshots, console,
adapter metadata, and smoke outcomes are in the
[qualification artifact](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36918408996/artifacts/11190823487).
The bare host's published-player auto-start remains explicitly skipped until
an authored startup-world descriptor is supplied. Physical hardware budgets
and the broader recovery matrix remain open.

See the [browser smoke instructions](../../../../Tools/BrowserSmoke/README.md),
[shader integration record](../rendering/unified-webgpu-shader-cooking.md), and
[caller-thread validation](caller-thread-frame-stepping.md) for reproducible
commands and detailed boundaries.
