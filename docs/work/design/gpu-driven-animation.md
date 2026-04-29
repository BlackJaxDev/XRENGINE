# GPU-Driven Animation Architecture

[<- Work docs index](../README.md)

## Goal

Move high-volume animation evaluation from CPU property playback into a GPU-resident animation pipeline where clip data, skeleton data, state machine graphs, and animated uniform channels are uploaded once and reused by many animated instances.

The target runtime shape is:

- immutable clip, skeleton, and graph data lives in GPU buffers or texture atlases,
- each animated instance owns only small mutable GPU state,
- compute shaders evaluate state machines, sample clips, blend poses, solve skeleton hierarchy, and publish render-facing outputs,
- rendering consumes GPU-produced bone palettes, blendshape weights, and material/uniform values without CPU readback on the main visible path.

This should build on the existing compute skinning and GPU-driven bone-palette seams. It is a separate execution backend for eligible animation, not a replacement for the current CPU animation pipeline.

## Current Engine Shape

The current animation model is still mostly CPU object graph evaluation:

- `AnimationClip` and `AnimationMember` bind to reflected properties, fields, and methods.
- `AnimationClipComponent` ticks property animations, applies values to scene objects, and can suspend a sibling state machine.
- `AnimStateMachine` evaluates layers, states, transitions, blend trees, and typed value stores on CPU.
- The typed `AnimationValueStore` already moves state-machine value blending away from boxed dictionaries, but it still applies the final values back to CPU-side engine objects.

Relevant code:

- [XREngine.Animation/Property/Core/AnimationClip.cs](../../../XREngine.Animation/Property/Core/AnimationClip.cs)
- [XREngine.Animation/Property/Core/AnimationMember.cs](../../../XREngine.Animation/Property/Core/AnimationMember.cs)
- [XREngine.Animation/Property/Core/MotionBase.cs](../../../XREngine.Animation/Property/Core/MotionBase.cs)
- [XREngine.Animation/Property/Core/AnimationValueStore.cs](../../../XREngine.Animation/Property/Core/AnimationValueStore.cs)
- [XREngine.Animation/State Machine/AnimStateMachine.cs](../../../XREngine.Animation/State%20Machine/AnimStateMachine.cs)
- [XREngine.Animation/State Machine/Layers/AnimLayer.cs](../../../XREngine.Animation/State%20Machine/Layers/AnimLayer.cs)
- [XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/AnimationClipComponent.cs](../../../XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/AnimationClipComponent.cs)
- [XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/AnimStateMachineComponent.cs](../../../XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/AnimStateMachineComponent.cs)

The rendering side already has the right pieces for a GPU animation consumer:

- `XRMeshRenderer` has renderer-owned bone and inverse bind buffers, plus external GPU-driven bone source support.
- `SkinningPrepassDispatcher` can bind active bone matrix sources and run compute skinning into GPU-resident output buffers.
- The compute skinning shader already consumes `boneMatrixBase` and `boneMatrixCount` for palette slices.
- GPU physics-chain work already includes direct GPU palette publication patterns for driven renderers.

Relevant code:

- [XRENGINE/Rendering/XRMeshRenderer.cs](../../../XRENGINE/Rendering/XRMeshRenderer.cs)
- [XRENGINE/Rendering/Compute/SkinningPrepassDispatcher.cs](../../../XRENGINE/Rendering/Compute/SkinningPrepassDispatcher.cs)
- [XRENGINE/Rendering/Compute/GlobalAnimationInputBuffers.cs](../../../XRENGINE/Rendering/Compute/GlobalAnimationInputBuffers.cs)
- [Build/CommonAssets/Shaders/Compute/Animation/SkinningPrepass.comp](../../../Build/CommonAssets/Shaders/Compute/Animation/SkinningPrepass.comp)
- [Build/CommonAssets/Shaders/Compute/Animation/SkinningPrepassInterleaved.comp](../../../Build/CommonAssets/Shaders/Compute/Animation/SkinningPrepassInterleaved.comp)
- [Build/CommonAssets/Shaders/Compute/PhysicsChain/PhysicsChainBonePalette.comp](../../../Build/CommonAssets/Shaders/Compute/PhysicsChain/PhysicsChainBonePalette.comp)

## Design Principles

1. Store source animation as local T/R/S channels, not final matrices.
2. Treat final bone render matrices as derived output caches.
3. Upload immutable clip, skeleton, and state machine data once per cooked asset revision.
4. Keep per-instance mutable GPU state small and dense.
5. Compile CPU object graphs into flat GPU tables instead of executing reflection or managed callbacks on GPU.
6. Expose a simple CPU/GPU backend selector on `AnimationClipComponent` and `AnimStateMachineComponent`.
7. Keep CPU as the default, fully supported backend for unsupported animation features and gameplay systems that need CPU transforms.
8. Feed the existing renderer active bone source and compute skinning contracts.
9. Avoid GPU readback in the normal visible rendering path.

## Separate CPU And GPU Backends

GPU-driven animation is an additional runtime mechanism. The CPU animation pipeline remains intact, testable, and selectable. No component should silently stop using the CPU evaluator just because GPU resources exist.

Both `AnimationClipComponent` and `AnimStateMachineComponent` should expose a small backend selector, for example:

```csharp
public enum AnimationEvaluationBackend
{
    Auto,
    Cpu,
    Gpu
}
```

Backend behavior:

- `Cpu` preserves the current behavior and is the safest default while the GPU path matures.
- `Gpu` explicitly registers the component with the GPU animation runtime and refuses or falls back with diagnostics when the clip or graph is not GPU eligible.
- `Auto` may choose GPU for eligible data and fall back to CPU for unsupported channels, callbacks, IK, root-motion consumers, or other CPU-only dependencies.

The two backends should share authoring data and component-facing controls, but they should not share mutable playback internals. CPU playback may continue using current `AnimationClip` and `AnimStateMachine` runtime state. GPU playback should use compiled immutable data plus dense per-instance GPU state. Switching a component back to CPU should restore the current CPU tick path without depending on GPU readback.

### AnimationClipComponent

`AnimationClipComponent` remains the single-clip playback component. In CPU mode it keeps ticking and applying the current clip exactly as it does now. In GPU mode it should register a `GpuAnimatorInstance` for single-clip playback, upload or reference cooked clip/skeleton data, and update only compact per-instance playback values such as clip ID, time, speed, weight, loop mode, and output slots.

Unsupported clip channels should either keep the component on CPU or produce an explicit mixed/fallback diagnostic. GPU mode must not mutate the shared CPU clip graph to make it GPU-friendly.

### AnimStateMachineComponent

`AnimStateMachineComponent` remains the component boundary for state machine parameters, network replication, debugging, and gameplay integration. In CPU mode it keeps the current evaluation tick and parameter application path. In GPU mode it should register compiled state machine tables, dense parameter buffers, layer state ranges, and output slots with the GPU runtime.

Network/gameplay systems should continue writing state-machine parameters through the component-facing API. The backend bridge can then stage dense parameter updates to GPU buffers. Unsupported callbacks, reflected property/method animation, CPU IK, and CPU-observed root motion keep the state machine on CPU or trigger explicit fallback diagnostics.

## Why T/R/S Is The Clip Format

The clip atlas should not primarily store blended or final render matrices. Matrix interpolation produces shear, non-orthogonal rotations, and poor additive behavior. It also makes state machine blending, layer masks, mirroring, retargeting, and compression harder.

The preferred source format is local transform channels:

- translation: `vec3`,
- rotation: normalized quaternion,
- scale: `vec3`,
- optional per-channel flags for constant/default/missing channels.

The GPU samples local T/R/S values, blends them, then composes local matrices. A later hierarchy solve produces world/render matrices. Those matrices are then written into the render-facing bone palette.

## Static GPU Data

Introduce a `GpuAnimationDatabase` or equivalent renderer-owned asset cache for immutable animation data.

Suggested resources:

- `GpuClipTable`: clip metadata, cadence, sample ranges, channel ranges, flags.
- `GpuChannelTable`: target kind, target index, interpolation mode, sample base, sample count.
- `GpuSkeletonTable`: bone count, root bone, depth ranges, bind pose ranges, parent index range.
- `GpuStateMachineTable`: graph metadata for layers, states, transitions, conditions, and motion nodes.
- `GpuCurveTable`: custom transition and parameter-driver curves.
- `AnimationSampleAtlas`: texture buffer, 2D texture atlas, or SSBO containing clip samples.
- `BindPoseBuffer`: local bind pose and static inverse bind matrices.

The first implementation should favor clarity over compression. Use `RGBA32F` samples until the pipeline is correct, then compress.

Example fixed-rate sample packing:

```text
texel 0: T.xyz, S.x
texel 1: Q.xyzw
texel 2: S.yz, flags, unused
```

Lookup formula:

```text
sampleBase = clip.sampleBase + ((frameIndex * clip.channelCount + channelIndex) * 3)
```

Later compression options:

- `RGBA16F` T/R/S samples for most humanoid and prop animation,
- smallest-three quaternion encoding,
- keyframe-compressed tracks in SSBOs,
- per-track quantization ranges,
- constant channel elision,
- skeleton-local channel remapping for shared clips.

## Channel Target Model

The GPU path should use dense numeric channel targets, not reflected member paths.

Suggested target kinds:

```text
BoneTranslation
BoneRotation
BoneScale
BlendshapeWeight
MaterialFloat
MaterialVector2
MaterialVector3
MaterialVector4
RendererUniformFloat
RendererUniformVector4
CustomNumericSlot
```

Each target resolves to a dense output slot during import, cooking, or GPU animation binding. String paths remain authoring/import metadata, but runtime GPU evaluation should use integer IDs.

Uniform-driving animation should write into an animated uniform buffer, not classic CPU-updated GL uniforms. Materials and shaders then fetch by `animatedUniformBase + slotIndex`.

## Per-Instance GPU State

Per animated instance state should be tiny compared with the static database.

Suggested logical layout:

```c
struct GpuAnimatorInstance
{
    uint skeletonId;
    uint stateMachineId;
    uint layerStateBase;
    uint parameterBase;
    uint localPoseBase;
    uint worldPoseBase;
    uint boneMatrixBase;
    uint uniformOutputBase;
    float deltaTime;
    float globalWeight;
};
```

Layer state:

```c
struct GpuLayerState
{
    uint currentState;
    uint nextState;
    uint currentTransition;
    uint flags;
    float currentTime;
    float nextTime;
    float transitionTime;
    float layerWeight;
};
```

Parameters should be split by type for simple shader access:

- float parameter buffer,
- int parameter buffer,
- bool bitset buffer,
- trigger bitset buffer.

CPU/gameplay/network systems may update these buffers, but GPU evaluation owns the derived layer and motion state once the frame begins.

## GPU Pass Graph

The full GPU-driven path should be a small sequence of compute passes. The first shipped implementation can combine some passes, but the contracts should remain separable.

### 1. Parameter Update

Apply external parameter writes for the frame:

- gameplay or networked float/int/bool parameter values,
- trigger set/clear masks,
- optional GPU-produced parameters from prior compute systems.

This pass should normalize parameter storage so state-machine evaluation sees dense arrays only.

### 2. State Machine Evaluation

Run one invocation per animated instance and layer.

Responsibilities:

- evaluate transition conditions,
- pick the highest-priority valid transition,
- apply exit-time rules,
- advance current and next state times,
- start, progress, and finish transitions,
- consume triggers at deterministic points,
- write active motion evaluation records.

The GPU state machine should be a compiled subset of the CPU graph. It should not execute arbitrary `AnimStateComponent` callbacks or reflected property/method animation.

### 3. Motion And Clip Sampling

Sample active clip channels into local pose and uniform output intermediates.

For fixed-rate clips:

```text
normalizedTime = wrapOrClamp(time / clip.length)
frame = normalizedTime * (clip.frameCount - 1)
frame0 = floor(frame)
frame1 = min(frame0 + 1, frameCount - 1)
t = frac(frame)
```

Translation and scale use linear interpolation. Rotation uses normalized quaternion slerp or normalized lerp when the quality setting allows it.

### 4. Blend Tree, Transition, And Layer Resolve

Blend sampled values according to:

- transition blend weight,
- 1D blend tree thresholds,
- 2D blend tree weights,
- direct blend tree parameter weights,
- layer weight,
- additive or override layer mode,
- bone masks.

The current CPU semantics in `BlendManager`, `BlendTree1D`, `BlendTree2D`, and `BlendTreeDirect` should be the reference behavior for the first compiled subset.

### 5. Skeleton Hierarchy Solve

Convert local T/R/S pose output into render/world matrices.

Because each bone depends on its parent, skeletons should be cooked into depth ranges:

```text
depth 0: roots
depth 1: children of roots
depth 2: grandchildren
...
```

Dispatch one depth range at a time, or use a persistent per-skeleton workgroup strategy later if that proves faster. The simple depth-range path is easier to validate and works well for many characters batched together.

### 6. Palette And Uniform Publish

Publish final outputs:

- current bone render matrices,
- previous bone render matrices for temporal passes,
- animated blendshape weights,
- animated material/uniform values,
- optional root motion deltas.

Bone palette output must match XRENGINE's current row-vector skinning convention. The existing compute skinning path expects:

```glsl
mat4 boneMatrix = BoneInvBindMatrices[idx] * BoneMatrices[idx];
vec4 p = vec4(basePosition, 1.0f) * boneMatrix;
```

So the GPU animation path should write the same kind of render/world bone matrices that CPU transform updates currently place in `XRMeshRenderer.BoneMatricesBuffer`.

### 7. Render Consumption

The renderer should consume GPU animation outputs through the existing active bone source pattern:

- compute skinning binds active bone matrix and inverse bind matrix buffers,
- direct vertex skinning uses the same palette base and active buffers,
- material shaders fetch animated uniform values from bound buffers,
- blendshape compute uses GPU-produced blendshape weights instead of CPU-pushed weights.

## State Machine Compiler

The CPU animation graph remains the authoring and editor representation. A compiler should produce GPU tables from it.

Compiler outputs:

- dense parameter schema,
- state table,
- layer table,
- transition table,
- condition table,
- motion node table,
- blend tree child tables,
- clip references,
- channel target remaps,
- fallback feature flags.

Supported GPU subset for the first full graph implementation:

- bool/int/float parameters,
- transition comparisons,
- transition priority,
- exit time,
- fixed-duration transitions,
- linear/cosine/quadratic/custom-curve transition weights,
- clip motions,
- 1D, 2D, and direct blend trees,
- additive and override layers,
- bone masks,
- blendshape and material numeric outputs.

CPU fallback cases:

- arbitrary `AnimStateComponent` callbacks,
- reflected property/method animation,
- object/string/discrete channels,
- CPU gameplay root motion consumers,
- CPU IK or humanoid retargeting paths not yet ported to data-oriented GPU jobs.

The compiler should report fallback reasons so editor tooling can explain why a graph is not fully GPU eligible.

## Root Motion

Root motion has two viable modes:

1. GPU-only root motion for visual-only or GPU-simulated crowds.
2. CPU-observed root motion for gameplay, navigation, physics, and networking.

Avoid synchronous GPU readback for CPU-observed root motion. Prefer one of these patterns:

- keep authoritative gameplay locomotion on CPU and let animation follow,
- use delayed asynchronous readback for diagnostics or non-critical consumers,
- move the dependent system to GPU when it is part of a crowd/simulation pipeline.

## Bounds And Culling

GPU-driven animation can silently lose its benefit if bounds require readback every frame.

Initial options:

- conservative CPU bounds per clip or state machine,
- author-provided bounds inflation,
- GPU-computed bounds used by GPU culling,
- asynchronous readback only for editor diagnostics or offline validation.

The visible rendering path should not require GPU-to-CPU sync just to draw an animated character.

## Temporal Data

Motion vectors, temporal reconstruction, and skinned bounds often need previous pose data. The GPU animation runtime should maintain previous output pages explicitly:

- previous local pose when needed,
- previous world pose,
- previous bone palette,
- previous animated uniform values if shader effects require it.

Swap current and previous pages once per render frame. Do not reconstruct previous pose from CPU transforms.

## Integration With Existing Systems

### AnimationClipComponent

The component should expose `AnimationEvaluationBackend` next to the existing playback controls. Its CPU backend keeps the current activation, tick registration, suspension, weight, speed, and exact-time sampling behavior. Its GPU backend creates or reuses a GPU animator instance for the selected clip and target renderer outputs.

GPU single-clip playback should stage only compact state each frame:

- clip table ID,
- skeleton ID,
- playback time,
- speed and weight,
- pose and palette output bases,
- animated blendshape and uniform output bases.

The CPU clip object remains the authoring and CPU fallback object. Cooked GPU clip data is a separate immutable representation.

### AnimStateMachineComponent

The component should expose the same backend selector and keep the state machine component as the public parameter/debug/network boundary. CPU mode keeps the current `EvaluationTick` behavior. GPU mode registers compiled graph data and per-instance state with the GPU animation runtime, while gameplay and network systems continue to write through the component API.

GPU state-machine mode should stage only dense parameter writes and trigger masks before the graph evaluation pass. Debug snapshots from GPU state should be optional and asynchronous so the visible path does not wait on readback.

### XRMeshRenderer

`XRMeshRenderer` should treat GPU animation like any other external bone matrix producer. The active skin source should expose:

- current bone matrices buffer,
- previous bone matrices buffer,
- inverse bind matrices buffer,
- palette base,
- palette count,
- source ownership/lifetime.

The current `SetGpuDrivenBoneMatrixSource` pattern is a good starting seam.

### Compute Skinning

`SkinningPrepassDispatcher` should bind active bone sources without trying to repack GPU-originating palette data through CPU memory.

Preferred final shape:

- GPU animation writes directly into a global animation palette atlas,
- each renderer receives a stable palette slice,
- compute skinning receives `boneMatrixBase` and `boneMatrixCount`.

Acceptable early shape:

- bind per-animator or per-renderer external palette buffers directly,
- skip global packing for those renderers.

### Direct Vertex Skinning

Direct vertex skinning should keep supporting `boneMatrixBase` so it can consume shared palette atlases and external GPU sources the same way compute skinning does.

### Blendshapes

Animated blendshape channels should produce a GPU blendshape weight buffer. The existing compute skinning blendshape path can then consume the active weight source, similar to active bone sources.

### Material And Renderer Uniforms

Uniform-driving clips should not push classic uniforms from CPU each frame. They should write to animated uniform buffers. Shader/material binding should expose a base slot so shader code can fetch values by index.

## Validation Strategy

Validation should compare the CPU and GPU paths at several layers:

- clip sample equality at known times,
- quaternion interpolation and wrap/clamp behavior,
- transition condition selection,
- blend tree weights,
- local-to-world skeleton solve,
- final bone palette equality within tolerance,
- skinned vertex output equality within tolerance,
- animated uniform output equality,
- no synchronous readback in the visible path.

Tests should start with deterministic synthetic clips and tiny skeletons before moving to imported FBX/glTF clips.

## Non-Goals For The First Implementation

- Do not remove, rewrite, or demote CPU animation playback.
- Do not silently reroute existing CPU components to GPU without an explicit backend setting or `Auto` eligibility decision.
- Do not port every reflected property animation target to GPU.
- Do not require all state machines to be GPU eligible.
- Do not make GPU readback part of the normal visible animation path.
- Do not start with aggressive sample compression.
- Do not require runtime dependency upgrades or shader-language migration.

## Open Questions

- Should the first clip atlas be texture-buffer based or pure SSBO based for OpenGL 4.6?
- Should skeleton hierarchy solve batch all skeleton depth ranges globally or dispatch per skeleton archetype?
- How should editor tooling present GPU eligibility and fallback reasons?
- Which root motion consumers must stay CPU-authoritative for v1?
- Should material animated uniforms be one global buffer or grouped by material instance?

## Execution Tracker

Implementation phases are tracked in [../todo/gpu-driven-animation-todo.md](../todo/gpu-driven-animation-todo.md).
