# Advanced late-lane executable audit

Revision: working tree on `8b104bf7a`, 2026-09-06. Source inventory only;
this is not a rendering acceptance result.

The executable entry is `AdvancedRenderPipeline.CommandChain.AppendStage`,
which appends `AppendAdvancedLatePassCommands`, post-processing, output, and
screen-space UI commands after the corresponding Advanced stage markers.

| Lane | Executable path and resources | Remaining boundary |
|---|---|---|
| Sorted alpha | `LateAndPostCommands`: CPU-direct `TransparentForward` over native HDR/depth; depth writes disabled. | Ordering/material parity remains ARP-V26. |
| Participating transparent motion | `AdvancedLatePassMetadata.ParticipatesInMotionVectors` exists. | Metadata has no production caller; this chain has no transparent velocity/reactive merge. ARP-I32. |
| Refraction | Scene-copy quad produces `TransparentSceneCopyTex`. | Snapshot predicate only sees weighted/exact consumers, not refractive sorted-alpha consumers. `RequiresSceneColorSnapshot` has no production caller. ARP-I33. |
| Weighted OIT | Visible weighted-pass predicate; clears accumulation to zero and revealage to one, draws into accumulation FBO, resolves over scene copy. | Attachment precision/overdraw and mixed-lane composition need ARP-V28. It has no PPLL-style node capacity. |
| PPLL | Reset head pointers/counters, SSBO bindings 24/25, forward collection, fullscreen resolve into native HDR. | Mono, exact-techniques preference, and output policy gates apply. Two nodes/pixel (minimum 1024), 32-byte nodes, 16 retained fragments/pixel, traversal capped at 256. Overflow increments a counter but drops fragments; conservative recovery is missing. ARP-I34/ARP-V29. |
| Depth peeling | Up to four layer FBOs with per-layer program bindings, then HDR resolve. | Same exact-technique/mono gates. Layer depth initialization, opaque-depth rejection, and mixed-lane composition require ARP-I35/ARP-V39. |
| Volumetrics | `AppendAdvancedAtmosphereAndFog` produces composite inputs from native depth/HDR; inactive inputs get neutral writes. | Mono and startup/output-policy gates apply; active atmosphere/volume registries and settings control execution. ARP-V30 remains open. |
| Overlays | CPU-direct `OnTopForward`, depth comparison Always, then state restoration. | Editor consumer parity remains ARP-V37. |
| UI | `AppendAdvancedScreenSpaceUi` invokes `VPRC_RenderScreenSpaceUI` under `AllowsScreenSpaceUi`. | Composition/profile acceptance remains ARP-V37/V44. |

`AdvancedLatePassEligibilityValidator`, `AdvancedLatePassMetadata`, and
`AdvancedSpecialEffectDescriptor` are declarations without executable consumers
in the rendering core or either backend. Their existence therefore does not
prove admission, rejection diagnostics, scene-color dependencies, or motion
participation. ARP-I36 owns wiring this boundary; special-effect family mapping
remains ARP-A03.

The September 6 implementation changes exact-technique admission from
construction-time visible-command checks to runtime `VPRC_IfElse` checks.
The chain includes all four bounded peel layers and evaluates the selected
layer count at execution. This fixes omission when consumers become visible
after pipeline construction; build and runtime closure are tracked by ARP-I31.

Sources: `AdvancedRenderPipeline.LateAndPostCommands.cs`,
`AdvancedRenderPipeline.ExactTransparency.cs`,
`AdvancedRenderPipeline.Resources.cs`, `AdvancedRenderPipeline.CommandChain.cs`,
`AdvancedLatePassEligibilityValidator.cs`, `AdvancedLatePassMetadata.cs`,
`AdvancedSpecialEffectDescriptor.cs`, `ExactTransparencyPpll.glsl`, and
`PerPixelLinkedListResolve.fs`.

## September 6 executable follow-up

ARP-I33 now has authored XRMaterial metadata and frozen per-published-pass
scene-copy consumer counts. Built-in dynamic water explicitly requests
refraction sampling; its Advanced-only material binding preserves the Default
pipeline's existing Texture0 behavior. The canonical snapshot resource is a
view of the actual TransparentSceneCopy allocation, and late render-graph
scopes declare the read. Runtime descriptor/copy-elision checks remain open.

ARP-I35 now clears each peel layer to transparent, seeds every layer from
native opaque depth, and handles normal/reversed previous-depth comparisons.
An Advanced-specific resolve shader composites premultiplied layers over the
current HDR target, preserving earlier late lanes. Default's shared snippet
bindings were made explicit. This does not globally sort fragments across
independent transparency techniques. Three fragment compiler checks and an
isolated editor build pass; runtime ARP-V39 remains open.

The earlier table describes the initial audit; its ARP-I33/I35 gaps are now
implemented as above. Full eligibility validation (ARP-I36), transparent
motion (ARP-I32), and PPLL recovery (ARP-I34) remain unfinished.
