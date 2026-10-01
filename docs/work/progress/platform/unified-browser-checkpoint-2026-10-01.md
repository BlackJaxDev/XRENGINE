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

See the [browser smoke instructions](../../../../Tools/BrowserSmoke/README.md),
[shader integration record](../rendering/unified-webgpu-shader-cooking.md), and
[caller-thread validation](caller-thread-frame-stepping.md) for reproducible
commands and detailed boundaries.
