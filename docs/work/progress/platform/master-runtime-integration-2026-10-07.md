# Master runtime integration

Updated: 2026-10-07.

This merge integrates master `fc61bf0a20ea57b3b10b4b28288b245c28f759a3`
into WebGPU parent `de85ce96d535cb8281b84a89e4aa6ae8976fd4e5`.
The common base is `3a37bceec78e1a187407a0d47c7c4c3575805f73`.
Master contributes two commits. The merge required fifteen path resolutions:
eight C# files, `.gitignore`, and six documentation paths.

## Runtime and rendering

- Property notifications keep the shared typed filters and ordered dispatch.
  Transform registration still precedes world-change notification and UI
  activation. Registered animation getters keep the reflection fallback for
  discrete values when no supported registered setter exists.
- Physics chain execution keeps disposal and reentrancy guards and includes
  master's late-tick telemetry. Browser and caller modes choose serial work
  before native worker queues and waits. Desktop and VR keep their independent
  clocks, collection/render overlap, and phase ownership.
- Renderable meshes retain retirement guards and master's final render-matrix,
  bounds, and command publication. GPU bounds updates use the accepted renderer
  command snapshot. Temporary geometry payload references are cleared after
  publication preparation, including rejected preparation.
- WebGPU native shading consumes the captured world ambient environment record.
  The existing 128-byte environment row, table 14, 36-table scene directory,
  and 160-byte shading parameters are unchanged. Decals use flag 16; baseline
  diffuse suppression uses flag 32. All 28 native/export recipes require
  `XR_ADV_WORLD_AMBIENT_SCHEMA_VERSION=1`, so old native companions require
  recooking. Uber companions retain their raster-produced lighting contract.
- Desktop aggregate deformation seeds current CPU palette rows, then overlays
  only the captured GPU-owned index ranges through ordered GPU copies. Explicit
  external palettes retain whole-range copies, including buffer aliases.
  Shared submeshes retain the captured source and ownership state. The packed
  WebGPU path keeps its existing generation checks and GPU-owned copy ranges.

## Requirement records

Master separates open code work from validation and completed evidence. The
[canonical runtime TODO](../../todo/platform/unified-desktop-browser-runtime-todo.md)
now contains fifteen open implementation IDs and one open owner ID. The
[validation view](../../testing/platform/platform-validation.md#unified-runtime-ur-verification-view)
contains the open runtime checks. The
[dated ledger](unified-runtime-requirement-ledger-2026-10-07.md) preserves all
163 original IDs, kinds, states, requirements, and evidence limits.

The count remains **122 complete and 41 open out of 163**: implementation
103/118, verification 13/38, and owner decisions 6/7. The document move adds no
completion credit. Counting only boxes in the shortened code TODO would no
longer reproduce the whole requirement metric. Compare the ledger's ID, kind,
and state rows with the original parent to reproduce this reconciliation.

## Validation and limits

The merged Release Host, WebGPU, Desktop platform, OpenGL, Vulkan, and
ShaderCooker projects build with .NET 10.0.401, with zero warnings and errors.
The Host graph was rebuilt after the animation fallback correction. All 28
native/export shader recipes cook with pinned Slang 2026.8. Their reflected
layouts, entry points, coordinates, required features and limits, workgroup
sizes, and semantic identities match the prior exact-commit CI descriptors.
The only added define is the world-ambient schema marker. Optional companions
still match their full companion's layout and entry point, and add only their
specific absence marker. Cooked source lengths and SHA-256 values match their
descriptors.

Independent source reviews cover core lifecycle and phase scheduling, GPU
layout and palette ownership, and the requirement-state mapping. These checks
do not establish live desktop palette behavior, browser ambient pixels, or
hardware performance. Exact merge-commit CI and runtime results must be
reported separately.

The WebGPU parent's [CI run](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37556721794)
finished with seven passes and two failures. UI animation-frame progress and
the shadow-enabled native shader compile deadline remain open. The merge does
not claim to repair those prior failures or change their deadlines.
