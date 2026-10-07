# GPU-Driven Animation TODO

Last Updated: 2026-10-06
Status: Planned (no code exists)
Architecture: none yet (see the last code item)  Design: [GPU-Driven Animation Architecture](../../../design/rendering/gpu/gpu-driven-animation.md)
Validation: [GPU Deformation Validation](../../../testing/rendering/gpu-deformation-validation.md#gpu-driven-animation)

## Current State

No GPU animation code exists (no `GpuAnimation*` or `AnimationEvaluationBackend` types). CPU clips and state machines support property animation, blend trees, transitions, typed value stores, and network parameter replication. `AnimationClip` and `AnimationMember` hold mutable binding and playback state, so they cannot be the shared immutable GPU data. `XRMeshRenderer` owns bone matrices, inverse bind matrices, blendshape weights, and external GPU bone-source hooks. `SkinningPrepassDispatcher` runs compute skinning (`SkinningPrepass*.comp`) from active bone sources with row-major palettes and row-vector math. GPU physics chains already publish bone palettes on the GPU.

## Open Code Items

### Contracts

- [ ] Define the `GpuAnimationDatabase` resource set (clip, channel, skeleton, state machine, and curve tables, sample atlas, bind pose buffers), fixed-rate local T/R/S sample packing and interpolation, channel target kinds (bone, blendshape, material value, renderer uniform, custom slot), the per-instance animator state layout, current and previous output pages, and the row-vector skinning equation. Done when: the types exist and the design doc matches them.
- [ ] Add `AnimationEvaluationBackend` (`Auto`, `Cpu`, `Gpu`) with default behavior for existing scenes and imports, `Auto` eligibility rules (CPU-only targets, root motion, callbacks, IK), and a fallback diagnostic record. CPU mode keeps the current tick paths; GPU mode never mutates CPU clip or state machine state. Done when: both animation components expose the setting and CPU behavior is unchanged.

### Static Database

- [ ] Add packed CPU models for clips, channels, skeletons, and bind poses. Convert transform channels to dense bone IDs and blendshape and material channels to dense output slots. Keep authoring metadata for diagnostics outside the hot path. Done when: a cooked clip has no runtime strings.
- [ ] Add a runtime owner for static tables and the sample atlas with shared-asset lifetime tracking, version-based reupload, and no per-instance duplication. Done when: two instances of one clip share one upload.
- [ ] Upload an `RGBA32F` local T/R/S atlas with clip metadata (sample base, frame count, rate, length, loop mode, channel range) and channel metadata (target kind and index, interpolation, defaults), plus a debug path for atlas sizes and table counts. Done when: the atlas uploads once per cooked revision.

### Clip Sampling And Palette Output

- [ ] Add per-instance records (skeleton, clip, time, pose base, palette base, output slots) and registration from animation components. Done when: an instance registers without per-bone CPU uploads.
- [ ] Add the `AnimationClipComponent` GPU backend: register a single-clip instance, stage clip ID, time, speed, weight, loop mode, and output slots, and report unsupported channels. Done when: the component plays a clip through the GPU path.
- [ ] Add the clip sampling compute pass: sample and interpolate T/R/S (quaternion rotation), apply bind-pose defaults, write local poses. Done when: the pass writes the local pose buffer.
- [ ] Add the skeleton hierarchy solve: cook parent indices and depth ranges, dispatch by depth, compose local matrices, write current and previous world matrices with explicit page swaps. Done when: the solve writes both pages.
- [ ] Publish GPU bone matrices through the `XRMeshRenderer` external bone-source hooks, reuse inverse bind buffers, feed compute skinning without CPU repacking, and support `boneMatrixBase` on the direct vertex path. Done when: a skinned mesh deforms from GPU-sampled data with no per-bone CPU upload.

### Blendshape And Uniform Outputs

- [ ] Add an active blendshape weight source parallel to bone sources, sample blendshape channels into GPU weight buffers, and route compute skinning reads through it. Keep the CPU path. Done when: compute skinning reads GPU weights for an opted-in renderer.
- [ ] Define an animated uniform slot layout with material binding of base offsets, shader access helpers for float and vector values, and a CPU fallback for materials that do not opt in. Done when: one material parameter animates with no CPU uniform push.

### Blend Trees, Transitions, And Layers

- [ ] Compile clip motions, 1D and 2D blend tree children, direct blend weight parameters, layer weights, additive and override modes, and masks into GPU motion records. Done when: the compiler emits records for each kind.
- [ ] Add blend evaluation compute for 1D, 2D (supported modes), and direct trees, blending T/R/S poses and animated uniforms by transition and layer weights. Report unsupported blend modes. Done when: several clips contribute to one GPU pose.

### State Machines

- [ ] Compile parameters (dense float, int, bool, trigger), layers, states, transitions, condition ranges, priority, exit time, blend duration, offset, blend type, and custom curves. Emit fallback diagnostics for callbacks, methods, discrete channels, and CPU-only targets. Done when: an eligible graph compiles to dense tables.
- [ ] Add CPU-to-GPU parameter staging with trigger set and consume semantics, keeping network replication at the component boundary, and an optional GPU-produced parameter write path. Done when: gameplay writes reach the GPU tables.
- [ ] Add the `AnimStateMachineComponent` GPU backend: register compiled tables and per-instance state, keep parameter writes on the component API, and report CPU IK, CPU-observed root motion, and reflected targets. Done when: an eligible graph runs with no per-frame CPU graph evaluation.
- [ ] Add graph evaluation compute: conditions per layer, priority selection, state time advance, transition start, progress, and finish, and active motion records for sampling. Keep CPU debug snapshots optional and asynchronous. Done when: the pass drives the sampling and blend passes.
- [ ] Show the current GPU state, layer, and transition in the editor when debug capture is on. Done when: the inspector shows the snapshot.

### Bounds, Temporal Data, And Readback

- [ ] Add conservative clip and state machine bounds for CPU culling, import bounds inflation controls, and a GPU bounds path for GPU culling with no synchronous readback. Done when: visible rendering never waits on bounds readback.
- [ ] Keep previous pose and palette pages for motion vectors, define previous values for animated uniforms, and swap pages by render frame ID. Done when: temporal consumers read the previous palette page.
- [ ] Add diagnostics for any readback, `PushSubData`, or CPU staging of GPU-originated data on visible animation paths. Done when: such a path logs a diagnostic.

### Optimization And Tooling

- [ ] Add `RGBA16F` samples, constant-channel elision, quaternion compression, and per-track quantization behind settings that stay off until correctness is proven. Done when: each option is a setting.
- [ ] Batch instances by skeleton and state machine, batch depth ranges across skeletons, reduce dispatches for small skeletons, pool output buffers, and add GPU timing. Done when: dispatch count no longer scales one-to-one with instances.
- [ ] Add a GPU eligibility report, fallback reasons and backend selectors in the clip and state machine inspectors, a clip atlas and instance debug view, and a CPU versus GPU comparison scene. Done when: each tool is in the editor.

### Tests (Owner Clearance Required)

- [ ] Add deterministic CPU reference fixtures (one bone, two-bone chain, branching hierarchy; constant, translation, rotation, scale, wrap and clamp clips; transition, 1D, 2D, and direct blends; float and vector uniform outputs). Done when: the fixtures load in tests.
- [ ] Add parity tests for clip sampling, hierarchy solve, blend weights (`BlendTree1D`, `BlendTree2D`, `BlendTreeDirect`), additive and override layers, transition selection, trigger consumption, final palette math against `SkinningPrepass.comp`, atlas offsets, graph compiler ranges and fallback reasons, blendshape weights, and animated uniform shader declarations. Done when: each has a deterministic test.
- [ ] Add tests that switching `AnimationClipComponent` and `AnimStateMachineComponent` between CPU and GPU keeps parameters and does not mutate shared CPU data. Done when: both components have the test.

### Documentation

- [ ] Write `docs/architecture/animation/gpu-driven-animation.md` when the first runtime slice lands. Done when: the doc describes the landed types, data layout, and backend rules.

## Out Of Scope

- Removing or weakening the CPU animation path.
- Silent rerouting of CPU components to GPU without an explicit backend or `Auto` decision.
- GPU execution of reflected properties, methods, or managed callbacks.
- Mandatory root-motion readback for rendering.
- Dependency or submodule changes without approval.

## Recovered Items To Triage

The 2026-10-06 todo cleanup removed these items, and no match was found in other docs. Classify each item as code, check, decision, or done. Then move it to the correct doc or delete it.

### From `todo/rendering/gpu/gpu-driven-animation-todo.md`

- [ ] Audit GPU animation path for accidental `PushSubData` or CPU staging of GPU-originating data
- [ ] Add quaternion compression experiments
