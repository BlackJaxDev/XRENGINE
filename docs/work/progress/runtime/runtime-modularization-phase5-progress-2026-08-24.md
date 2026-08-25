# Runtime Modularization Phase 5 Progress

Started: 2026-08-24

Branch: `codex/runtime-modularization-phase5`

Implementation base: `86900eaed58cb47f124b2425a80d6d7bf35b4f4c`

## Scope And Entry State

This ledger records the Phase 5 subsystem-adapter cleanup and its validation.
Phase 4 production code was already present at the implementation base, but its
hardware/runtime validation and branch-integration gates were not closed. Phase
5 therefore proceeded on that exact base while retaining those gates as explicit
prerequisites rather than treating their tracker location under `COMPLETED/` as
evidence that they passed.

The final closeout below subsequently executed every machine-available Phase 4
and Phase 5 prerequisite. Device-specific checks that require a physical XR
runtime/headset or supported Streamline hardware are recorded separately and
are not claimed as executed.

Pre-existing worktree changes in the local-agent broker, its documentation, the
`OscCore-NET9` submodule worktree, `.github/copilot-instructions.md`, and
`Build/Dependencies/vcpkg/` are unrelated to this phase and remain preserved.

## Final Adapter Graph

| Adapter | Production C# files | Direct project dependencies |
|---|---:|---|
| `Runtime.AnimationIntegration` | 68 | Animation, Data, Runtime.Core, Runtime.Rendering, OscCore transport |
| `Runtime.AudioIntegration` | 47 | Audio, Data, Runtime.Core, Runtime.Rendering |
| `Runtime.InputIntegration` | 32 | Data, Extensions, Input, Runtime.Core, Runtime.Rendering |
| `Runtime.ModelingBridge` | 89 | Animation, Data, Fbx, Gltf, Modeling, Runtime.Rendering |

The ModelingBridge format and animation edges are deliberate. The bridge owns
runtime asset construction and reconstruction from the lower format libraries;
splitting those format-specific adapters would add composition surfaces without
removing any upward feature dependency. The reference design now records this
approved aggregate import graph, and graph tests enforce it.

No adapter references another adapter, the facade, Editor, an application,
UnitTests, or a concrete rendering backend. Animation, Audio, Input, Modeling,
Fbx, and Gltf retain no upward runtime/application project edge. Runtime.Core
and Runtime.Rendering retain their lower-layer dependency contracts.

## Ownership Changes

- All subsystem host adapters, game-mode/pawn/player-controller composition,
  VR lifecycle/state/input composition, adapter profiles, and startup/manifest
  serialization now compile from `Runtime.Bootstrap/SubsystemHost` instead of
  the transitional facade. Installation uses an explicit profile lease with
  deterministic restoration and teardown.
- Editor and VRClient install all four adapters. Server installs only animation
  and modeling services; it does not register local audio/input/VR adapters.
- OpenXR exposes a backend-neutral lifecycle contract from Runtime.Rendering.
  Bootstrap no longer needs a concrete OpenXR implementation type at its public
  composition boundary.
- ARKit blendshape names moved to Data so audio lip-sync and animation can share
  a lower value contract. The AudioIntegration-to-AnimationIntegration project
  edge was removed.
- AudioIntegration owns the per-world listener registry. `XRWorldInstance` no
  longer owns audio listener attachment or audio-specific settings behavior.
- Input owns OpenVR devices, action manifests/bindings, `openvr_api.dll`, and
  neutral VR input delegates. AudioIntegration owns optional OVR LipSync cargo.
  Audio owns OpenAL packages and native license cargo; redundant Editor OpenAL
  package references were removed. Legacy facade copies and duplicate native
  publish rules were removed.
- Editor no longer references either backend leaf directly. Unused OpenGL imports
  were removed, DLSS diagnostics consume the stable vendor-upscale capability,
  and shader cross-compilation uses a stable kernel facade backed by a
  module-registered Vulkan/Shaderc implementation.
- Runtime.Bootstrap owns AOT factory generation and scans all four adapters plus
  the transitional facade and runtime layers. Adapter registration is therefore
  explicit instead of being retained accidentally through a facade scan.

## Compatibility And Phase 6 Handoff

The remaining facade dependencies are bounded compatibility/consumer-migration
work, not Phase 5 adapter implementation:

- animation cooked-binary, YAML, asset-manager, and serializer coordination is
  coupled to facade-owned serialization contexts;
- `Engine.Input`, networking input managers, `VRPlayerInputSet`, character-pawn
  input, and `XRWorldInstance` input use still serve facade consumers;
- Unity conversion, prefab source/import publication, model-cache codecs, and
  asset-manager third-party import policy still compose ModelingBridge from the
  facade;
- the corresponding facade project references to feature libraries,
  InputIntegration, and ModelingBridge remain until design Phase 6 migrates
  those consumers.

These files contain no remaining Phase 5-owned host adapter, registration root,
native cargo, or duplicate adapter public type.

## Validation Evidence

- Final `dotnet build XRENGINE.slnx --no-restore -m:1 -nr:false -v:minimal`:
  passed with zero warnings and zero errors.
- All `RuntimeModularizationPhase4*` and `RuntimeModularizationPhase5*` tests:
  58 passed, zero failed, zero skipped. The serialization-compatibility subset
  passed 24/24 and the dependency-boundary subset passed 28/28.
- `VulkanCoreHardeningPhase4Tests`: 18 passed, zero failed. The contracts were
  re-based onto the decomposed command, resource-lifetime, descriptor, frame,
  and output authorities rather than weakened to concatenate obsolete physical
  `VulkanRenderer` partial files.
- OpenXR timing, stereo temporal-isolation completion, and VR view-mode
  contracts: 105 passed, zero failed.
- Focused FBX, glTF, modeling, and mesh validation: 94 passed, zero failed after
  correcting the malformed native-FBX fixture path and closeout regressions.
- Named isolated Editor sessions rendered the Unit Testing World under OpenGL
  and Vulkan, answered MCP, and were stopped through the session manager. The
  OpenGL and Vulkan captures each changed with camera position and were visually
  inspected. The final Vulkan session
  `20260824-130327-runtime-p45-vulkan2` reported no validation-layer VUID,
  device-loss, lifetime, or teardown error.
- Vulkan screenshot/readback evidence is recorded as
  `Screenshot_20260824_130720_491_ec51fe76a8d44e4399704d639e563894.png`
  and
  `Screenshot_20260824_130824_939_a53bc8e5ce6c475996c1169848d4568f.png`
  under the task validation root. The inspected OpenGL captures are the
  `Screenshot_20260824_105153_*`, `105500_*`, and `105528_*` files in the same
  capture directory.
- The bounded live headless Server run entered play, initialized UDP networking,
  remained healthy, and stopped cleanly without a window or local
  audio/input/VR adapters. Its engine log session is
  `Build/Logs/Debug_net10.0-windows10.0.26100.0/windows_x64/xrengine_2026-08-24_13-26-57_pid26484/`.
- VRClient Release publish passed into
  `Build/_AgentValidation/20260824-102125-runtime-modularization-phase5/temp-build/vrclient-publish-final/`.
  The package contains all four adapters, both selected backend leaves, action
  manifest/bindings, OpenVR cargo, and required native dependencies. A launch
  with an intentionally nonexistent peer exited cleanly before XR
  initialization, proving the no-peer path without starting an external game.
- The dependency/license report was regenerated after cargo ownership moved.
  `docs/DEPENDENCIES.md`, license snapshots, and ownership overrides reflect the
  final Input, Audio, ModelingBridge, and backend owners.
- A physical OpenXR/OpenVR headset and supported Streamline frame-generation
  hardware were not available. No physical-device result is claimed; those
  paths remain external manual acceptance.
- The repository-wide test project was not used as a Phase 5 gate while
  concurrent user-owned humanoid/root-motion work was modifying its production
  and test inputs. The focused Phase 4/5 matrix above is isolated and green.

## Closeout State

Phase 5 is engineering-complete as of 2026-08-24, and its tracker has moved to
`docs/work/todo/COMPLETED/`. All four adapters own their runtime-facing
integration, Bootstrap owns explicit profile composition, compatibility and AOT
discovery are covered, native cargo has one owner, the Server runs headlessly,
and the intended VRClient package is reproducible.

Two deliberate dispositions are carried into design Phase 6 rather than being
misrepresented as unfinished adapter work: facade-owned general
animation/asset serialization must move with the lower serialization layer, and
the remaining facade project references disappear when their facade consumers
migrate. Neither surface contains a Phase 5-owned adapter implementation,
registration root, or native cargo.

Phase 4's available-machine Vulkan/runtime closeout was completed in the same
worktree. Physical XR and supported-hardware feature validation remains a named
external acceptance lane. No commit, merge, or branch promotion was requested
or performed.
