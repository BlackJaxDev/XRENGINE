# GPU-Driven Animation TODO

Last Updated: 2026-04-28
Current Status: design captured, implementation not started
Scope: implement the architecture defined in [GPU-Driven Animation Architecture](../design/gpu-driven-animation.md).

## Current Reality

What exists now:

- CPU animation clips and state machines already support property animation, blend trees, transitions, typed value stores, and networked parameter replication.
- `AnimationClip` and `AnimationMember` still carry mutable runtime binding/playback state, so clip instances are not safe as the shared immutable GPU data model.
- `XRMeshRenderer` already owns bone matrices, inverse bind matrices, blendshape weights, and external GPU-driven bone source hooks.
- `SkinningPrepassDispatcher` already runs compute skinning and can consume active bone matrix sources.
- Existing skinning shaders already use row-major palette buffers and row-vector math.
- GPU physics-chain code already demonstrates GPU-side bone palette publication for renderers.

What does not exist yet:

- a cooked immutable GPU animation database,
- a GPU clip sample atlas,
- a GPU state machine compiler,
- GPU-owned per-instance animator state,
- GPU animation passes for clip sampling, graph evaluation, pose blending, skeleton solving, and animated uniform output,
- material/shader conventions for animated uniform buffers,
- validation comparing CPU and GPU animation output.

## Target Outcome

At the end of this work:

- static clip, skeleton, and state machine data uploads once per cooked asset revision,
- animated instances update tiny GPU state records per frame,
- GPU compute evaluates eligible state machines and blends local T/R/S poses,
- GPU compute publishes render-compatible bone matrices, blendshape weights, and animated uniform values,
- compute skinning and direct vertex skinning consume the same active GPU animation sources,
- `AnimationClipComponent` can switch between CPU, GPU, and automatic backend selection,
- `AnimStateMachineComponent` can switch between CPU, GPU, and automatic backend selection,
- CPU animation remains available as fallback and for gameplay systems that need CPU-side state,
- GPU animation is a separate runtime mechanism with its own cooked data and per-instance state,
- normal visible rendering does not depend on synchronous animation readback.

## Non-Goals

- Do not remove, replace, or weaken the existing CPU animation path.
- Do not silently reroute CPU animation components to GPU without an explicit backend setting or `Auto` eligibility decision.
- Do not require every `AnimStateMachine` feature to be GPU eligible in the first pass.
- Do not execute reflected properties, methods, or arbitrary managed callbacks on GPU.
- Do not start by compressing clip samples aggressively.
- Do not make root motion readback mandatory for rendering.
- Do not change major dependencies or submodules as part of this work without explicit approval.

---

## Phase 0 - Contracts, Cooked Data Shape, And Test Fixtures

Outcome: the project has an agreed GPU animation contract, deterministic fixtures, and tests that can guide implementation.

### 0.1 Finalize Runtime Contracts

- [ ] Define the first `GpuAnimationDatabase` resource set: clip table, channel table, skeleton table, state machine table, curve table, sample atlas, and bind pose buffers
- [ ] Define fixed-rate local T/R/S sample packing and interpolation rules
- [ ] Define channel target kinds for bones, blendshapes, material values, renderer uniforms, and custom numeric slots
- [ ] Define per-instance GPU animator state layout
- [ ] Define current/previous output page ownership for temporal data
- [ ] Document row-vector matrix conventions and the expected skinning equation

### 0.2 Create Deterministic CPU Reference Fixtures

- [ ] Add tiny synthetic skeleton fixtures: one bone, two-bone chain, branching hierarchy
- [ ] Add simple clip fixtures: constant pose, linear translation, quaternion rotation, scale, wrap/clamp cases
- [ ] Add blend fixtures: transition blend, 1D blend tree, 2D blend tree, direct blend tree
- [ ] Add uniform-driving fixtures for float and vector outputs

### 0.3 Add Baseline Tests

- [ ] Test clip sampling against CPU reference values at exact frames and interpolated times
- [ ] Test skeleton hierarchy solve against CPU matrix output
- [ ] Test blend tree weight calculations against CPU behavior
- [ ] Test state transition condition selection against CPU behavior
- [ ] Test final bone palette math against `SkinningPrepass.comp` expectations

### 0.4 Define Component Backend Selection

- [ ] Add an `AnimationEvaluationBackend` contract with `Auto`, `Cpu`, and `Gpu` modes
- [ ] Define default backend behavior for existing scenes and imported assets
- [ ] Define `Auto` eligibility and fallback rules for clips, state machines, CPU-only targets, root motion, callbacks, and IK
- [ ] Define diagnostic data returned when `Gpu` or `Auto` falls back to CPU
- [ ] Confirm CPU mode keeps the current tick/evaluation paths for both animation components
- [ ] Confirm GPU mode uses separate cooked data and does not mutate CPU clip or state machine runtime state

Acceptance criteria:

- [ ] GPU data contracts are documented and reviewed.
- [ ] Tests exist before shader/runtime work starts.
- [ ] CPU reference fixtures are deterministic and small enough for fast iteration.
- [ ] Component-level backend selection is specified before implementation starts.

---

## Phase 1 - Static GPU Clip And Skeleton Database

Outcome: clips and skeletons can be cooked into immutable GPU data and uploaded once.

### 1.1 Add Cooked Data Types

- [ ] Add CPU-side packed data models for clips, channels, skeletons, and bind poses
- [ ] Convert imported transform animation channels into dense bone target IDs
- [ ] Convert blendshape and material animation channels into dense output slot IDs
- [ ] Preserve authoring metadata for diagnostics without using strings in the runtime hot path

### 1.2 Add GPU Resource Ownership

- [ ] Add renderer/runtime owner for static animation tables and sample atlas buffers
- [ ] Support reference counting or asset lifetime tracking for shared clips and skeletons
- [ ] Add dirty/version tracking so changed clips reupload cleanly
- [ ] Avoid per-instance duplication of immutable clip samples

### 1.3 Upload First Sample Atlas

- [ ] Implement `RGBA32F` local T/R/S sample upload
- [ ] Add clip metadata for sample base, frame count, sample rate, length, loop mode, and channel range
- [ ] Add channel metadata for target kind, target index, interpolation mode, and default handling
- [ ] Add debug/inspection path for atlas sizes and table counts

Acceptance criteria:

- [ ] Multiple instances can share one uploaded clip and skeleton data set.
- [ ] Static animation data upload is independent from per-frame playback state.
- [ ] Tests verify table offsets and atlas lookup math.

---

## Phase 2 - CPU-Selected GPU Clip Sampling And Palette Output

Outcome: CPU still chooses clip/time/weight, but GPU samples clips and publishes renderer-compatible bone matrices.

### 2.1 Add Minimal Animator Instance Buffer

- [ ] Add per-instance state records for skeleton ID, clip ID, playback time, pose base, palette base, and output slots
- [ ] Add registration from animation components or render bindings to GPU animator instances
- [ ] Keep state updates small and explicit

### 2.1a Add AnimationClipComponent GPU Backend

- [ ] Add backend selector plumbing to `AnimationClipComponent`
- [ ] Keep CPU mode on the current clip tick and property application path
- [ ] Make GPU mode register a single-clip `GpuAnimatorInstance`
- [ ] Stage clip ID, playback time, speed, weight, loop mode, and output slot state without per-bone CPU uploads
- [ ] Implement explicit fallback or refusal diagnostics for unsupported clip channels
- [ ] Add tests that switching `AnimationClipComponent` between CPU and GPU does not mutate shared clip data

### 2.2 Add Clip Sampling Compute Pass

- [ ] Sample local T/R/S from the static atlas
- [ ] Interpolate translation, scale, and quaternion rotation
- [ ] Apply missing channel defaults from bind pose
- [ ] Write local pose output buffers

### 2.3 Add Skeleton Hierarchy Solve

- [ ] Cook skeleton parent indices and depth ranges
- [ ] Dispatch hierarchy solve by depth range
- [ ] Compose local T/R/S to local matrices
- [ ] Produce current world/render bone matrices
- [ ] Maintain previous bone matrices with explicit page swapping

### 2.4 Publish To Renderers

- [ ] Publish GPU-produced bone matrices through `XRMeshRenderer` active/external bone source hooks
- [ ] Reuse existing inverse bind matrix buffers where possible
- [ ] Ensure compute skinning consumes GPU-produced palettes without CPU repacking
- [ ] Extend direct vertex skinning validation for `boneMatrixBase`

Acceptance criteria:

- [ ] A skinned mesh can be visually driven by GPU-sampled clip data.
- [ ] CPU no longer uploads per-bone matrices for that renderer in the tested path.
- [ ] CPU and GPU final bone palettes match within tolerance on test fixtures.
- [ ] `AnimationClipComponent` can choose CPU or GPU backend explicitly for the tested single-clip path.

---

## Phase 3 - GPU Blendshape And Uniform Outputs

Outcome: non-bone numeric animation channels can drive GPU-consumed blendshape and material/uniform data.

### 3.1 Blendshape Weight Output

- [ ] Add active blendshape weight source abstraction parallel to active bone sources
- [ ] Sample blendshape animation channels into GPU weight buffers
- [ ] Route compute skinning blendshape reads through the active source
- [ ] Preserve CPU blendshape path as fallback

### 3.2 Animated Uniform Buffers

- [ ] Define animated uniform slot layout for materials/renderers
- [ ] Add material/shader binding support for animated uniform base offsets
- [ ] Generate or author shader access helpers for animated float/vector values
- [ ] Add CPU fallback for materials that do not opt into animated uniform buffers

### 3.3 Validation

- [ ] Compare GPU-produced blendshape weights with CPU property animation output
- [ ] Add shader contract tests for animated uniform buffer declarations and binding names
- [ ] Add a visible test scene with animated material parameter and animated skinned mesh

Acceptance criteria:

- [ ] Blendshape weights can be driven by GPU clip sampling.
- [ ] At least one material parameter can be animated without CPU uniform pushes.
- [ ] CPU fallback remains available for unsupported channels.

---

## Phase 4 - GPU Blend Trees, Transitions, And Layers

Outcome: GPU evaluates motion blending while CPU still controls the graph state at a coarse level if needed.

### 4.1 Compile Motion Nodes

- [ ] Compile clip motions to GPU motion records
- [ ] Compile 1D blend tree children and thresholds
- [ ] Compile 2D blend tree children and positions
- [ ] Compile direct blend tree child weight parameter IDs
- [ ] Compile layer weight, additive/override mode, and masks

### 4.2 Add Blend Evaluation Compute

- [ ] Evaluate 1D blend tree child weights
- [ ] Evaluate 2D blend tree child weights for supported blend modes
- [ ] Evaluate direct blend tree weights from GPU parameters
- [ ] Blend sampled local T/R/S poses by transition and layer weights
- [ ] Blend animated uniform outputs consistently with pose outputs

### 4.3 Add CPU/GPU Parity Tests

- [ ] Test 1D blend output parity against CPU `BlendTree1D`
- [ ] Test 2D blend output parity against CPU `BlendTree2D`
- [ ] Test direct blend output parity against CPU `BlendTreeDirect`
- [ ] Test additive and override layer composition

Acceptance criteria:

- [ ] GPU blend output matches CPU reference behavior on deterministic fixtures.
- [ ] Multiple clips can contribute to one final GPU pose.
- [ ] Unsupported blend modes report clear fallback reasons.

---

## Phase 5 - GPU State Machine Evaluation

Outcome: eligible state machines can run transitions and playback state entirely on GPU.

### 5.1 Compile State Machine Tables

- [ ] Compile parameter schema to dense float/int/bool/trigger storage
- [ ] Compile layers, states, transitions, and condition ranges
- [ ] Compile transition priority, exit time, blend duration, offset, and blend type
- [ ] Compile custom blend curves into GPU curve tables
- [ ] Emit fallback diagnostics for unsupported callbacks, methods, discrete channels, or CPU-only targets

### 5.2 Add Parameter Update Path

- [ ] Add CPU-to-GPU parameter write staging for gameplay/network inputs
- [ ] Add trigger set/consume semantics
- [ ] Preserve existing network parameter replication semantics at the component boundary
- [ ] Add optional GPU-produced parameter write path for future simulation systems

### 5.2a Add AnimStateMachineComponent GPU Backend

- [ ] Add backend selector plumbing to `AnimStateMachineComponent`
- [ ] Keep CPU mode on the current `EvaluationTick` path
- [ ] Make GPU mode register compiled state machine tables and per-instance layer/parameter state
- [ ] Keep gameplay and network parameter writes flowing through the component API before staging dense GPU updates
- [ ] Emit fallback diagnostics for unsupported callbacks, reflected property/method animation, CPU IK, and CPU-observed root motion
- [ ] Add tests that switching `AnimStateMachineComponent` between CPU and GPU preserves parameter values and does not mutate the CPU graph

### 5.3 Add Graph Evaluation Compute

- [ ] Evaluate transition conditions per layer
- [ ] Select valid transition by priority
- [ ] Advance current and next state times
- [ ] Start, progress, and finish transitions
- [ ] Write active motion records for the sampling/blending passes
- [ ] Keep CPU-visible debug snapshots optional and asynchronous

### 5.4 Tests And Debugging

- [ ] Add graph compiler tests for dense table ranges and fallback reasons
- [ ] Add transition selection tests against CPU state-machine behavior
- [ ] Add trigger consumption tests
- [ ] Add editor diagnostics for current GPU state/layer/transition when debug capture is enabled

Acceptance criteria:

- [ ] A GPU-eligible state machine runs without per-frame CPU graph evaluation.
- [ ] CPU and GPU state transitions match on targeted test graphs.
- [ ] Unsupported graphs fall back explicitly instead of partially running incorrectly.
- [ ] `AnimStateMachineComponent` can choose CPU or GPU backend explicitly for eligible state-machine graphs.

---

## Phase 6 - Bounds, Temporal Data, And Zero-Readback Hardening

Outcome: GPU-driven animation stays GPU-driven across culling, temporal rendering, and diagnostics.

### 6.1 Bounds Strategy

- [ ] Add conservative clip/state-machine bounds for CPU culling fallback
- [ ] Add optional authoring/import bounds inflation controls
- [ ] Add GPU-computed bounds path for GPU culling workflows
- [ ] Ensure visible rendering does not require synchronous bounds readback

### 6.2 Temporal Outputs

- [ ] Maintain previous local/world pose pages where needed
- [ ] Maintain previous bone palette pages for motion vectors
- [ ] Define animated uniform previous-value handling for temporal shaders
- [ ] Add page swap lifecycle tied to render frame ID

### 6.3 Readback Audit

- [ ] Audit GPU animation path for accidental `PushSubData` or CPU staging of GPU-originating data
- [ ] Audit skinned bounds and culling paths for synchronous waits
- [ ] Add diagnostics for readback on visible animation paths

Acceptance criteria:

- [ ] GPU-driven animation can render without synchronous readback in the common visible path.
- [ ] Motion-vector and temporal consumers can access previous bone palette data.
- [ ] Bounds behavior is documented and testable.

---

## Phase 7 - Optimization, Compression, And Tooling

Outcome: the working GPU animation path becomes production-ready for many characters and large clip libraries.

### 7.1 Sample Compression

- [ ] Evaluate `RGBA16F` sample storage quality
- [ ] Add constant-channel elision
- [ ] Add quaternion compression experiments
- [ ] Add per-track quantization ranges where useful
- [ ] Measure memory bandwidth and cache behavior

### 7.2 Dispatch And Buffer Optimization

- [ ] Batch animator instances by skeleton and state machine where useful
- [ ] Batch hierarchy depth ranges across skeletons
- [ ] Reduce dispatch count for small skeletons
- [ ] Add persistent or pooled output buffer allocation strategy
- [ ] Add GPU timing diagnostics

### 7.3 Editor And Import Tooling

- [ ] Add GPU eligibility report for clips and state machines
- [ ] Show fallback reasons in animation/state-machine inspectors
- [ ] Add inspector controls for `AnimationClipComponent` CPU/GPU/Auto backend selection
- [ ] Add inspector controls for `AnimStateMachineComponent` CPU/GPU/Auto backend selection
- [ ] Add debug view for clip atlas usage and animator instance state
- [ ] Add validation scene for CPU/GPU animation comparison

Acceptance criteria:

- [ ] GPU animation is measurable, debuggable, and memory-conscious.
- [ ] Artists/developers can see why a graph is or is not GPU eligible.
- [ ] Compression options can be enabled only after correctness is proven.

---

## Validation Commands

Add focused commands as tests land. Expected eventual targets:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~GpuAnimation
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~Skinning
```

Use the existing `Build-Editor` task for broad editor validation after runtime/render integration changes.
