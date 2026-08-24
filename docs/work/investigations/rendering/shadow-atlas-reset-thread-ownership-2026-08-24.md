# Shadow Atlas Reset Thread Ownership

Date: 2026-08-24

## Problem

The startup model-import completion callback queued `ResetAtlasKind` on the
engine main thread. That method immediately enumerated and mutated
`ShadowAtlasManager` planning dictionaries while the collect/planning thread
could be updating the same dictionaries. The observed result was
`InvalidOperationException: Collection was modified` in
`RemoveKeysForAtlasKind`.

## Root Cause And Fix

The dictionaries are designed around one planning-thread writer, but the public
reset and repack entry points bypassed that ownership boundary. Copying keys or
changing the removal loop would still race with concurrent dictionary writes.

Atlas-kind reset and repack are now atomic requests. `BeginFrameCore` drains
prior tile completions, atomically consumes the requests, and performs all
dictionary/resource mutation on the planning thread before accepting the new
frame's requests. Requests arriving after that boundary remain pending for the
next frame, so publication cannot lose a concurrent repack request.

## Validation

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -c Release --no-restore`
  completed with zero warnings and zero errors.
- Isolated Vulkan session `shadow-atlas-reset-20260824` reached startup model
  import completion and requested all atlas resets. The scene continued
  rendering, including visible shadowed geometry in the inspected MCP viewport
  capture.
- A second no-build startup pass independently reached the same reset trigger.
- Exact scans of both completed session logs found no `Collection was modified`,
  `RemoveKeysForAtlasKind`, `UnobservedTaskException`, Vulkan validation error,
  or cleanup/lifetime failure.
- No unit or regression tests were added or run. Repository policy requires
  explicit user clearance before test work for this regression after live
  feature validation.

Evidence root:
`Build/_AgentValidation/20260824-003300-shadow-atlas-reset-race/`.
