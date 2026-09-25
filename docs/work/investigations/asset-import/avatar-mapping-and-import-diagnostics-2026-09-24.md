# Avatar skeleton detection and import diagnostics

- Status: fixed and verified through the live desktop import path.
- Input: the private `jax2031.fbx` selected by Unit Testing World settings.
- Scope: humanoid detection, visible import warnings, and missing-texture reporting.
- Related: [avatar transforms](native-fbx-avatar-transforms-2026-09-24.md) and [Console Vulkan routing/focus](../editor/console-vulkan-focus-2026-09-24.md).

## Findings and repairs

Humanoid detection considered mesh/helper transforms and several independent
rigs in the same imported hierarchy. Those transforms distorted the estimated
body height, and detection chose an armature wrapper instead of the primary
hips. Short side suffixes such as `Leg_L` also lacked semantic alias support.

Detection now prefers complete semantic hips candidates with distinct torso,
left-leg, and right-leg branches. It ranks candidate rigs by distinct bones
actually referenced by skinned meshes, then measures height within that rig.
The primary rig in this asset covers 273 utilized bones; the next candidate
covers 151. Wrapped hips must satisfy the branch checks at the actual joint.
Namespaced long names and short left/right aliases remain supported. Existing
confidence thresholds remain intact; no artificial confidence floor is used.

`C_Pants` really contains 89 source control points with no bone weights; import
did not discard their influences. Their authoring intent cannot be determined
from the FBX. The earlier rigid mesh-node repair preserves authored placement,
but these vertices do not follow skeletal animation. The warning now explains
that limitation and tells the user to assign weights in the source model.

FBX warnings and errors now have a diagnostic sink independent of optional
progress tracing. The engine sends them to the Console's Meshes category and
`log_meshes.log`, including when `FbxLogVerbosity` is Off.

Unresolved texture references are collected for each model import. After model
processing and streaming-scope disposal, they publish under one diagnostics lock
and signal the existing Missing Assets panel once. Its first snapshot therefore
contains the complete import batch, with model context and original paths.
Discovered references also publish if the import fails. This boundary is one
model's processing completion, not completion of every startup model or every
streamed texture. Texture resolution behavior is unchanged.

## Live validation

The named isolated `avatar-transforms-0924` session used the configured Vulkan
backend and advanced pipeline, with a private settings copy and FBX tracing Off.
The final editor build passed with zero warnings and zero errors in 88.85 seconds.
No tests were added or modified during this runtime repair.

- All required humanoid bones were found. The generated avatar profile reported
  98% confidence, 39 mapped bones, and zero fallbacks.
- The startup Unity clip entered native playback. `IsPlaying` was true and
  sampled playback time advanced. Front and oblique captures were viewed; the
  primary skeleton animates without the earlier oversized or spiking geometry.
- The pants warning appeared in the Meshes log with tracing Off.
- Missing Assets displayed 46 unresolved texture rows and 46 hits after import.
  The batch entries share the same first-seen timestamp. The final run again
  reported 46 references at the model-processing boundary.
- A focused independent review found no remaining blockers in branch selection,
  side aliases, confidence handling, or atomic missing-reference publication.
- The owned editor session was stopped and its logs inspected.

Evidence root:
`Build/_AgentValidation/00000000-000000-shared/avatar-transforms-20260924/`.

- `logs/mapping-final-build.log`
- `logs/mapping-final-log_animation.log`
- `logs/mapping-final-log_meshes.log`
- `mcp-captures/mapped-walk-front/`
- `mcp-captures/mapped-walk-oblique/`
- `mcp-captures/mapping-final-front/`

## Limits

Some clothing uses independent rigs and remains in its bind pose while the
primary skeleton animates. This detection fix does not merge or retarget those
rigs. Equal-evidence candidates still require confidence validation or explicit
mapping. Missing textures are reported for the user to resolve, and missing
skeletal weights are not invented.
