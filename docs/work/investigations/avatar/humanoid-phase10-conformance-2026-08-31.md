# Humanoid Phase 10 conformance investigation (2026-08-31)

## Objective

Close Phase 9A with current runtime evidence, implement the Phase 10 reproducible
single-path conformance matrix, and validate `Sexy Walk.anim` on the private Jax
prefab/FBX, Mitsuki, and redistributable avatars without putting private fixture
identities into production behavior.

## Phase 9A status

The native path still uses the Phase 9A provisional-FK, public humanoid Body
model, Body-frame alignment, and Hips compensation transaction. Source review
found no alternate fitted or named-avatar playback path. The existing focused
acceptance evidence remains applicable; Phase 10 adds broader corpus and
provenance gates rather than replacing that solve.

## Live integration evidence

### Jax prefab and FBX

- Source prefab:
  `K:\Unity\Jax Main Avatars\Assets\Avatars\JAX\Mine\jax2026.prefab`.
- Source FBX:
  `K:\Unity\Jax Main Avatars\Assets\Avatars\JAX\Mine\jax2026.fbx`.
- The prefab conversion completed through the normal Unity-prefab import path:
  52 models, 41 materials, and 37 source-aware material conversions.
- `HumanoidComponent` mapped every required role and reported 93% profile
  confidence. The only low-confidence optional-axis observations were the two
  toe roles.
- `Sexy Walk.anim` played through the native direct-clip transaction with
  `BodyModel=XRE.PublicHumanoidMassHierarchy.v1` and compensation enabled.
- Audit schema 6 captured 81 samples at 25 Hz for the complete 3.2-second clip.
  The audit contained 95 muscle probes and 39 mapped bones.
- Maximum requested-to-compensated Body-center residual was
  `0.000502 mm`; maximum Body-rotation residual was `0.030304 deg`; character
  root drift was `0 mm`; no non-finite values were present.
- The motion was not a static/rest-pose false positive. Body translation spans
  were `82.320 / 114.315 / 74.914 mm` on X/Y/Z. Representative maximum pose
  changes from the first sample included `120.77 deg` right lower leg,
  `118.61 deg` left lower leg, `62.51 deg` left upper leg, `55.51 deg` right
  upper leg, `31.02 deg` spine, and `19.82 deg` right lower arm.

The ignored numeric report is retained at
`Build/_AgentValidation/20260831-184646-humanoid-phase10/reports/jax-sexy-audit.json`.
It is disposable evidence, not a checked-in reference fixture.

### Mitsuki discovery

The obsolete Desktop path recorded by the older investigation is absent. Current
copies were found at:

- `K:\Unity\Mitsuki VSF\Assets\!Mitsuki\Mitsuki.fbx`
- `K:\Unity\New Project\Assets\!Mitsuki\Mitsuki.fbx`
- `K:\Unity\vrc avis 2\Assets\!Mitsuki\Mitsuki.fbx`

The dedicated `Mitsuki VSF` project copy is the primary live-validation input.
Mitsuki remains optional integration evidence and is not part of the checked-in
CI corpus.

## Visual-validation blocker

The animation data and skeleton transaction are healthy, but the first visual
captures are not acceptance evidence:

- OpenGL repeatedly calls `glNamedBufferData` on immutable advanced-deformation
  buffers (`GLDataBuffer.PushDataImmediate`), producing
  `GL_INVALID_OPERATION` and a black scene viewport.
- The same large FBX under Vulkan reaches the scene and maps as `jax2026`, but
  foreground capture encounters repeated `VulkanPresentNowReadinessException`
  failures because an unnamed compute pipeline remains pending while asynchronous
  compute compilation is disabled.
- The OpenGL run also exposed a separate concurrent access in
  `XRMaterialBase.Parameter<T>` while skinned bounds queried a material.

These failures are renderer defects, not humanoid-pose discrepancies. A renderer
root-cause review is in progress. Visual acceptance will be repeated from at
least two cameras after the renderer can publish a frame; numerical acceptance
is not being presented as a substitute for that capture.

## Phase 10 implementation work

In progress:

- a Unity-free versioned conformance manifest and stale-reference validator;
- strict numerical/capability gate evaluation;
- a production-source fixture-identity scan;
- three independently authored, redistributable imported humanoid FBX fixtures
  covering automatic mapping, persisted corrected mapping, distinct proportions
  and axes, and missing optional roles;
- a complete clip/avatar/playback matrix that includes every repository walk and
  the portable Unity serialization fixtures.

No test was added or changed before live/runtime validation, in accordance with
the repository sequencing policy.

## Remaining acceptance work

- Finish Mitsuki direct and state-machine Sexy Walk audits.
- Restore visual captures for Jax, Mitsuki, and all three redistributable avatars.
- Generate and check in independently captured, provenance-complete known-answer
  references for the redistributable corpus.
- Run all compatible clip/avatar rows across seeks, reverse, signed loop epochs,
  transitions, interruptions, and blend-tree modes.
- Exercise events, object bindings, IK/contact, packed encodings, input moves,
  and persisted mapping reload.
- Only after those live rows pass, add/run the focused automated matrix and
  reconcile the TODO and public developer guidance.
