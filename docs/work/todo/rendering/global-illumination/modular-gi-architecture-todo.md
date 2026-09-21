# Modular GI architecture for Default and Advanced pipelines

Created: 2026-09-21
Status: Phase 0-6 implementation is complete; Phase 7 source acceptance passed, while runtime visual/performance acceptance and cleared regression work remain open. Phase 4 remains explicitly unsupported pending a real cascade producer.
Source baseline: `cd5822051` (recheck HEAD before implementing)
Primary implementer: GPT-5.6 Terra
Scope: make GI implementations self-contained and reusable by both `DefaultRenderPipeline` and `AdvancedRenderPipeline`.

## Objective and completion boundary

Adding a GI implementation must require its module, algorithm resources/shaders,
settings/authoring support, and registration. It must not require adding method
booleans, concrete-provider checks, resource factories, pass lists, or shader
branches throughout either pipeline.

Both pipelines must host the same GI module implementations through a common
contract. Pipeline adapters supply their own surface inputs, execution phases,
output targets, and capabilities. Providers own their algorithms. Advanced must
retain its native frame contract and execution owner; it must not render through
a hidden Default pipeline or nested pipeline instance.

This is an architecture migration, not a claim that every planned GI algorithm
already works. Track unfinished DDGI, surfel, voxel, LPV, and other algorithm work
in their existing TODOs. A placeholder is not a supported provider. Establish
module boundaries for planned methods without fabricating working implementations.

Related work:

- [DDGI implementation](ddgi-implementation-todo.md)
- [Surfel GI repair](surfel-gi-repair-todo.md)
- [Voxel cone tracing and VXAO](voxel-cone-tracing-and-vxao-implementation-todo.md)
- [LPV implementation](lpvgi-implementation-todo.md)
- [GI developer guide](../../../../developer-guides/gi/global-illumination.md)
- [DDGI integration design](../../../design/global-illumination/ddgi-integration-plan.md)

## Instructions for Terra

1. Read root `AGENTS.md`, this document, and only the source needed for the current
   phase. Recheck the checkout because file locations and implementation status
   can change after this baseline.
2. Execute phases in dependency order. Complete the stated exit gate before
   marking a phase complete. Do not tick a box solely because code compiles.
3. Keep each implementation batch bounded to one contract or migration seam.
   Prefer a buildable vertical slice over simultaneous rewrites of all methods.
4. Preserve current DDGI algorithm behavior in the first migration. Do not mix
   probe tuning, lighting changes, and ownership changes into the extraction.
5. Record existing rendering failures before refactoring. A known baseline
   failure is not a passing result and must remain visible in the evidence.
6. Follow repository validation policy: use named isolated editor sessions for
   live validation; do not add/modify regression tests until the feature is
   runtime-validated and the user explicitly clears test work. This TODO does
   not itself provide that clearance. Existing diagnostic checks may be used
   where required by repository policy.
7. After each batch update the execution record below with files changed,
   validation/evidence, unresolved issues, and the exact next action. Keep
   durable findings in tracked docs and disposable captures under the repository's
   `Build/_AgentValidation/` policy. Never put machine-specific paths in docs.
8. Use repository-authorized delegation only for bounded independent work.
   Escalate unresolved GPU lifetime or cross-pipeline architecture questions with
   a compact evidence packet; do not repeatedly improvise around failed invariants.

## Non-negotiable design rules

- One shared provider/module API; no independent Default and Advanced algorithm
  implementations. Small pipeline adapters may translate inputs and phases.
- Provider code must not reference concrete Default/Advanced pipeline types,
  their static resource-name constants, or their framebuffer factories.
- Common host/admission code must not switch on provider classes, algorithm enum
  values, pass class names, or algorithm-specific stage flags.
- A central composition root may register providers and map serialized selection
  IDs/enums. That mapping is the permitted algorithm-selection boundary.
- Providers declare their own resources and graph accesses. Reuse the existing
  resource layout and render graph; do not create a parallel graph framework.
- No universal ray/probe/propagation base class. Ray generation, probe relocation,
  voxelization, surfel allocation, cascade merging, and LPV propagation stay private.
- No allocation/update work for inactive methods. Do not construct every GI pass
  and rely on repeated `UsesX` execution guards.
- No silent substitution of a requested GPU path with CPU processing or another
  GI algorithm. Report unsupported/pending/failed states and reasons explicitly.
- Initially select one diffuse provider. Select specular independently. Combining
  diffuse providers requires a later explicit coverage/energy policy.
- Preserve submission acceptance, in-flight resource protection, renderer-owner
  invalidation, and aborted-update semantics throughout the migration.
- Physical sharing is optional and must be proved safe. Modularity does not
  require immediately moving GPU caches to a world singleton.

## Source map and confirmed coupling

All paths below are repository-relative. Let `R` mean
`XREngine.Runtime.Rendering/Rendering` in this document only.

| Area | Start here | Required change |
|---|---|---|
| Shared selection API | `R/Pipelines/Contracts/IGlobalIlluminationPipelineProvider.cs` | Replace `UsesX` fan-out and voxel material hook with a neutral plan/host seam. |
| Default integration | `R/Pipelines/Types/Default/DefaultRenderPipeline.{cs,CommandChain.cs,Resources.cs,Textures.cs,FBOs.cs,BindingPublishers.cs,PostProcessing.cs}` | Move algorithm declarations and pass trains to modules; retain pipeline target handling in adapter. |
| Advanced integration | `R/Pipelines/Types/Advanced/AdvancedRenderPipeline.{cs,CommandChain.cs,Resources.cs,NativeShading.cs,ProbeResources.cs}` | Use the same module plan; remove DDGI-specific command and resource mapping. |
| Advanced provider admission | `R/GI/Advanced/`, `R/Pipelines/Commands/Advanced/VPRC_AdvancedRenderStage.cs` | Replace concrete two-provider whitelist and mode checks with capability/output validation. |
| Advanced ownership/stages | `R/Pipelines/Types/Advanced/Contracts/AdvancedRenderPipelineFrameContract.cs`, `R/Pipelines/Types/Advanced/AdvancedRenderPipeline.CommandChain.cs` | Preserve stage identities, active execution owner, minimal-output profiles, and composed OpenXR stage families. |
| DDGI algorithm/runtime | `R/GI/DDGI/`, `R/Pipelines/Commands/Features/GI/VPRC_DDGI*.cs` | Encapsulate passes/state; eliminate class-name dependency switch and pipeline-name dependencies. |
| DDGI authoring | `XREngine.Runtime.Rendering/Scene/Components/Lights/DDGIVolumeComponent.cs` | Separate authored settings and deterministic selection from physical runtime state. |
| Shaders | `Build/CommonAssets/Shaders/Compute/DDGI/`, `Build/CommonAssets/Shaders/Snippets/DDGI*.glsl`, deferred/forward/Advanced lighting consumers | Separate algorithm sampling, material response, composition, and debug semantics. |
| Other GI methods | `R/Pipelines/Commands/Features/GI/VPRC_{SurfelGI,RadianceCascades,VoxelConeTracing,LightVolumes,ReSTIR}Pass.cs` | Inventory actual behavior before wrapping; preserve method-specific scheduling needs. |
| Settings/bootstrap | `XREngine.Runtime.Bootstrap/Settings/UnitTestingWorldSettings.cs`, bootstrap lighting builder, GI enum/settings consumers | Centralize selection; keep provider settings/factories local instead of growing flat `InitializeX` clusters. |
| Existing integration checks | `XREngine.UnitTests/Rendering/DDGIScaffoldingContractTests.cs`, Advanced rendering contract tests | Record assumptions invalidated by the migration; defer changes until test clearance. |

Observed at baseline: Default lists the DDGI pass train directly; Advanced also
lists and remaps it. `AdvancedGlobalIlluminationContract.IsNativeProvider` accepts
specific DDGI/light-probe classes. `VPRC_AdvancedRenderStage` rejects other GI modes.
`VPRC_DDGIComputePass` switches on pass class names for graph declarations.
`VPRC_DDGICompositePass` selects the first active volume and chooses an MSAA target.
`DDGIFrameContext` and geometry resources are keyed by physical pipeline instance.
These are migration targets, not permission to remove their safety guarantees.

## Target contracts and ownership

Use the following as responsibilities, not mandatory class names. Reuse existing
engine types where they already express the contract. Keep the public surface small.

| Contract | Responsibilities | Must not contain |
|---|---|---|
| GI registry/descriptor | Stable provider identity, factory, settings schema, capability requirements, diagnostics metadata | Pipeline-specific pass code or provider-type admission switches |
| GI host adapter | Active execution owner, surface-input handles, view description, supported phase anchors, composition target | DDGI atlas dimensions, voxel material factories, algorithm update stages |
| GI plan | Immutable selected configuration, requirements, selection snapshot, resource-layout identity, contribution policy | Live mutable component lookup in each pass |
| GI runtime | Provider-owned persistent state, resource generation lifecycle, update/publication status, disposal | Implicit world-global ownership of renderer-specific resources |
| GI module | Declare resources, contribute update/resolve passes, return outputs; optional shader sampling bindings | Fixed universal algorithm sequence |
| GI lighting output | Typed resource handles, signal semantics, validity/coverage, selected contribution and view layout | Assumed framebuffer names or untyped texture-name-only success |
| GI scene services | Demand-driven geometry/material/light/environment inputs and optional tracing | Mandatory BVH usage for every method |

Suggested module organization:

```text
Rendering/GI/
  Contracts/           # provider, plan, capabilities, outputs, host inputs
  Integration/         # registry, selection, common composition/debug policy
  Scene/               # shared scene inputs introduced when used
  DDGI/                # DDGI provider, resources, passes, settings, runtime
  Surfels/
  RadianceCascades/
  VoxelConeTracing/
  LightPropagationVolumes/
  LightProbes/
  ReSTIR/
```

This is logical ownership; engine-required shader/component directories can stay
where they are. Do not perform broad file moves before the DDGI vertical slice
works. Avoid empty frameworks for unimplemented providers.

### Signal contract

The initial common screen output is linear HDR, material-shaded outgoing indirect
diffuse radiance. DDGI currently applies material response and AO in its screen
resolve, so preserve that behavior first. The compositor must not apply either
again. Do not infer physical normalization from a variable named `irradiance`:
document where cosine integration and any `1/pi` factor actually occur.

Explicitly record color/exposure convention, whether AO is already applied,
resolution, view/eye layers, validity, and geometric coverage. Invalid output is
not equivalent to valid black lighting. An optional specular result uses a separate
contribution slot. Debug images are separate presentation outputs, not GI radiance.

Optional world-space sampling must be a separately declared shader capability
with an equally precise signal contract. A screen-space texture does not provide
GI for arbitrary forward/transparent surfaces or secondary rays. Initially support
only consumers the provider can actually serve; expose unsupported consumers.

### Lifetime contract

Keep authored settings, persistent algorithm state, and per-view resolve/history
state distinct. A resource generation must account for provider identity, physical
execution/renderer owner, scene identity, selection/layout identity, and dimensions
or view layout that affect allocation. Distinguish content revisions from layout
revisions so a lighting change does not unnecessarily reallocate a field.

Publish only valid, safely ordered results. Submitted and GPU-complete are distinct
states. A pending update may reuse a previously published generation only while its
resources remain valid and protected; otherwise publish explicit unavailability.
Do not let rejected submissions or partial writes advance temporal cursors.

## Phase 0 — Record baseline and exact integration surfaces

- [x] P0.1 Recheck HEAD, local changes, applicable instructions, and existing DDGI
  investigation evidence. Do not modify unrelated work.
- [x] P0.2 Inventory every GI enum branch, `UsesX`, provider-type check, DDGI resource
  constant, shader flag, pass insertion, and post-process special case in both
  pipelines and their shared/back-end consumers. Include Advanced `EnableDdgi`
  stage data, native shading, and resource generation/admission code.
- [x] P0.3 Trace one Default frame and one Advanced frame on paper: surface inputs,
  scene preparation, GI update, GI resolve, composition, transparency, temporal/post,
  and output. Identify which inputs exist at each stage and which are multisampled.
- [x] P0.4 Record actual readiness of light probes, DDGI, surfels, radiance cascades,
  VCT, light volumes/LPV, and ReSTIR. Do not equate `LightVolumes` with a complete
  LPV algorithm without inspecting the implementation.
- [x] P0.5 Capture available baseline runtime behavior using the matrix below;
  reuse relevant existing evidence with its commit and limitations recorded.

Exit gate: source inventory, frame placement, current failures, and provider support
matrix are documented. Missing hardware/backend access is recorded, not marked passed.

### Phase 0 baseline record — 2026-09-21

#### Checkout and evidence (P0.1)

The audit was performed at `cd58220514ae6455af68a9526f2fa32d8d127e4a` on
`master`, after reading the repository instructions. The only non-ignored working
tree entries were this new TODO, `Build/Dependencies/vcpkg/`, and the
`Build/Submodules/OpenVR.NET` submodule state; none were changed by this phase.

The existing [DDGI working-copy verification](../../../investigations/rendering/2026-09-20-ddgi-working-copy-verification.md)
is the runtime baseline. It contains the isolated-session commands, captures,
logs, RenderDoc artifacts, build results, and limitations for the DDGI work
already present at this commit. Phase 0 makes no new renderer, settings, shader,
or test changes and does not reinterpret that evidence as universal GI support.

#### Coupling inventory (P0.2)

| Coupling | Current source owner | Migration implication |
|---|---|---|
| Selection fan-out | `Rendering/Pipelines/Contracts/IGlobalIlluminationPipelineProvider.cs`; both `DefaultRenderPipeline.cs` and `AdvancedRenderPipeline.cs` | The enum is projected into seven `UsesX` booleans plus the VCT material factory. Both hosts duplicate the projection. |
| Default resource selection | `DefaultRenderPipeline.Resources.cs` | The resource-profile switch selects LightVolumes, Radiance Cascades, Surfel, DDGI, ReSTIR, and VCT resources. DDGI has direct atlas/buffer declarations; the other screen methods have host-owned output/FBO declarations. |
| Default pass insertion | `DefaultRenderPipeline.CommandChain.cs` | VCT is inserted before scene rendering. ReSTIR, LightVolumes, Radiance Cascades, Surfels, the full DDGI train, and its debug pass are inserted directly after opaque forward/masked work and before MSAA resolve/transparency. Inactive algorithms are still command-chain members and self-gate with `UsesX`. |
| Default shader/probe special cases | `DefaultRenderPipeline.cs`, `DefaultRenderPipeline.BindingPublishers.cs`, and DDGI command/shader files | The host publishes `UsesDDGI`; probe synchronization treats DDGI as a light-probe exception. DDGI commands default to `DefaultRenderPipeline` resource names. |
| Advanced resource and pass ownership | `AdvancedRenderPipeline.Resources.cs` and `AdvancedRenderPipeline.CommandChain.cs` | Advanced allocates DDGI resources only when `UsesDDGI` and not minimal-output, then appends the hard-coded DDGI train immediately after `NativeOpaqueShading`, explicitly wiring depth, normal, albedo, RMSE, AO, output, and all DDGI resource names. |
| Advanced admission | `Rendering/GI/Advanced/AdvancedGlobalIlluminationContract.cs`, `IAdvancedGlobalIlluminationProvider.cs`, and `VPRC_AdvancedRenderStage.cs` | Native admission is a concrete two-class whitelist (`AdvancedLightProbesAndIblProvider` or `AdvancedDdgiProvider`) and separately rejects every GI mode except light probes/IBL and DDGI. `EnableDdgi` is carried in the backend stage request. |
| DDGI graph declaration | `VPRC_DDGIComputePass.cs` | A `switch (PassName)` maps concrete DDGI pass names to graph accesses and imports Default-pipeline resource constants. This is the central class-name dependency that Phase 2 must remove. |
| DDGI resolve and lifecycle | `VPRC_DDGICompositePass.cs`, `Rendering/GI/DDGI/DDGIFrameContext.cs`, and `DDGIVolumeComponent.cs` | The composite finds the first active volume, owns MSAA/stereo target selection, samples the output and additively composites it. Runtime contexts and geometry resources are physically keyed by pipeline instance. |
| Other method coupling | `VPRC_{SurfelGI,RadianceCascades,VoxelConeTracing,LightVolumes,ReSTIR}Pass.cs` | Each pass self-gates on a concrete `UsesX` member. Surfel/VCT also default to Default-pipeline constants; VCT calls the host material factory. |
| Settings/bootstrap and legacy checks | `UnitTestingWorldSettings.cs`, `BootstrapLightingBuilder.cs`, `BootstrapRenderSettings.cs`, and `XREngine.UnitTests/Rendering/DDGIScaffoldingContractTests.cs` | Bootstrap has DDGI-specific volume settings/factory behavior. Existing tests assert the `UsesX` scaffolding surface, so they are migration-sensitive but remain unchanged until runtime validation and explicit test clearance. |

The scan also found backend consumers in OpenGL Advanced-stage/native-shading
files and Vulkan Advanced closure, command-recording, feature-operation, and
visibility-resource files. Those consumers take the Advanced stage request and
DDGI resource family as input; they are part of the Phase 1 adapter boundary,
not authorization to add a backend-specific GI API.

#### Frame placement and available inputs (P0.3)

| Host | Ordered GI-relevant frame path | Inputs and boundaries that the adapter must preserve |
|---|---|---|
| Default | Temporal begin → optional VCT voxelization → pre-render → deferred G-buffer → forward depth/AO/lighting → opaque and masked forward → ReSTIR/LightVolumes/Radiance Cascades/Surfel/DDGI update and resolve → MSAA resolve → transparency and transparent forward → velocity, motion blur/DoF, bloom, temporal accumulation → post/AA/upscale → output. | DDGI resolves before transparency, from depth, normal, albedo, RMSE, AO, its atlases and probe state; it writes `DDGITexture` then additively composites into the forward target. The G-buffer and forward targets can be multisampled before the later resolve, so a provider must receive the resolved-versus-MSAA surface contract explicitly rather than infer it from a host FBO name. |
| Advanced | Optional BRDF precompute → temporal begin → ordered native stages. The `NativeOpaqueShading` stage produces native visibility/shading outputs, then background and the fixed DDGI train run; late passes, temporal/post-processing, output, and UI follow as separately identified stages. | The current DDGI seam consumes Advanced depth, normal, albedo-opacity, RMSE, and Advanced GTAO output, writes the DDGI texture, and composites to the native forward target. Minimal-visibility output suppresses DDGI resources and `EnableDdgi`. The immutable frame-view set, output reservation/publication, and ordered stage identities remain owned by Advanced. |

The DDGI composite has distinct mono and stereo shaders and explicitly performs
the compute-write-to-sampled transition before its graphics draw. Its placement
before transparency means the current result is a deferred/opaque diffuse
contribution, not an advertised transparent or arbitrary world-space GI source.

#### Provider readiness matrix (P0.4)

| Representation | Source state at baseline | Claimed Phase 0 support |
|---|---|---|
| Light probes and IBL | Native Advanced provider exists; both hosts select the mode and maintain probe resources. | Existing light-probe baseline; not a new modular-provider claim. |
| DDGI | Resource lifecycle, dynamic/baked state, update passes, screen resolve, debug visualization, and an Advanced provider are implemented. | Experimental and evidence-backed only for the cases in the verification ledger; it is the first extraction candidate. |
| Surfel GI | A source-described initial implementation and debug visualization exist, with host-owned buffers/output and a `UsesSurfelGI` gate. | Unvalidated; not supported by Advanced native admission. |
| Radiance Cascades | A gated pass and host-owned output/resource declarations exist. | Unvalidated; not supported by Advanced native admission. |
| Voxel cone tracing | A gated voxelization pass, host factory hook, and VCT volume resource exist. | Unvalidated; not supported by Advanced native admission. |
| LightVolumes / LPV | `VPRC_LightVolumesPass` and shaders exist, but the inspected code is a light-volume pass, not proof of complete LPV injection/propagation/baking semantics. | Unvalidated and must not be advertised as LPV support. |
| ReSTIR / path tracing | A gated `VPRC_ReSTIRPass` and reservoir resources exist; selection maps `PathTracing` to `UsesRestirGI`. | Unvalidated; not a supported Advanced GI provider. |

#### Runtime baseline and known limitations (P0.5)

The verification ledger records successful isolated build(s) with zero warnings
and substantial DDGI runtime coverage on all four host/backend lanes. Its latest
evidence supports these limited baseline statements:

| Lane | Available evidence | Baseline limitation retained for later phases |
|---|---|---|
| Default + OpenGL | Dynamic/baked DDGI, mono/stereo, resize, scene replacement, and allocation windows were exercised with viewed captures. | This does not validate any non-DDGI provider or prove the future abstraction. |
| Advanced + OpenGL | Dynamic/baked DDGI, stereo, probe topology/cache recovery, and diffuse/specular coexistence were exercised with viewed captures. | This is still a concrete Advanced DDGI path, not generic-provider validation. |
| Default + Vulkan | Desktop dynamic/baked DDGI evidence exists. The latest stereo run rejected a Default bloom multiview frame-plan mismatch; build 49b contains the targeted correction but has no replacement runtime acceptance. | Vulkan Default stereo remains open. |
| Advanced + Vulkan | Stereo, ownership, activation, and desktop/stereo DDGI paths were exercised; the last sustained stereo run reported no steady-state VUID/error match. | The eight-probe run found a zero BRDF lookup despite nonzero reflection data, so specular/cache recovery remains open. |

No baseline runtime evidence was found for surfels, radiance cascades, VCT,
LightVolumes/LPV, or ReSTIR on either host. Those cells are intentionally
unexecuted, not failures or support claims. The ledger also records that DDGI
test updates are deferred pending explicit user clearance; Phase 0 neither adds
nor changes tests.

## Phase 1 — Introduce the neutral contract and two host adapters

Depends on: Phase 0.

- [x] P1.1 Define provider identity/registration and explicit support results:
  supported, unsupported with reason, initializing/pending, and failed. Separate
  static capabilities from current resource readiness.
- [x] P1.2 Define host inputs, lighting outputs, contribution policy, and optional
  sampling capability using existing typed resource infrastructure.
- [x] P1.3 Define lifecycle operations for resource declaration, runtime creation,
  update/resolve graph contribution, invalidation, and release. Do not put a
  DDGI update-stage enum or ray/probe count into a common contract.
- [x] P1.4 Implement Default and Advanced adapters with generic phase anchors.
  A module can request early scene preparation, field update, surface-dependent
  resolve, and optional material sampling; it need not use every anchor. Validate
  input availability and graph dependencies rather than assuming all GI runs late.
- [x] P1.5 Preserve Advanced ordered stage identities and active execution owner.
  Respect minimal visibility output and composed OpenXR two-pass eye families.
  No nested Default/Advanced execution instance may be introduced.
- [x] P1.6 Introduce a single immutable GI plan consumed consistently by resource
  layout, graph construction, shader binding, and runtime. A settings change must
  transition those together at a safe boundary, never mix old layout/new settings.

Exit gate: both pipelines can resolve the same registered module contract; a
disabled selection produces no GI work. Temporary adapters are tracked for removal.

### Phase 1 implementation record — 2026-09-21

`Rendering/GI/Contracts/` now owns the provider descriptor/registry, explicit
support results, contribution semantics, host capabilities, execution anchors,
immutable plan, and module lifecycle contract. The contract carries no DDGI
stage enum, probe/ray count, pipeline type, framebuffer name, or backend object.
It separates static selection admission from module-owned resource readiness.

`Rendering/GI/Integration/DefaultGlobalIlluminationHostAdapter.cs` and
`AdvancedGlobalIlluminationHostAdapter.cs` describe only algorithm-neutral host
capabilities. Advanced preserves native ownership, ordered-stage execution, and
minimal-output rejection; the adapter creates no nested pipeline. Both pipeline
types resolve their `GlobalIlluminationPlan` through the same registry whenever
selection changes. Advanced also refreshes it when the offscreen/minimal-output
profile changes. Default resource-generation selection and all existing `UsesX`
compatibility guards now consume that plan, so resource layout, command graph,
and existing shader/binding decisions observe the same immutable selection
snapshot.

The currently registered Light Probes/IBL and experimental DDGI entries are
explicit **legacy compatibility** descriptors: their `ModuleFactory` is null
until their algorithm ownership moves in Phase 2. Every other existing GI mode
resolves with an explicit unsupported diagnostic based on Phase 0 evidence.
This avoids advertising source-only implementations as supported without
silently changing their existing legacy behavior before their migration.

The temporary seams deliberately retained for removal in Phase 6 are
`IGlobalIlluminationPipelineProvider`, all `UsesX` guards, the Advanced-only
provider/admission contract, host resource feature bits, and hard-coded pass
trains. Phase 2 removes the DDGI subset by having the module factory own DDGI
resources and graph contributions.

## Phase 2 — Encapsulate DDGI without changing its algorithm

Depends on: Phase 1.

- [x] P2.1 Create the DDGI module and move ownership of resource declarations,
  layout descriptors, shaders/programs, and pass sequence into it. Move names out
  of `DefaultRenderPipeline`; give resources provider-instance/generation scopes.
- [x] P2.2 Supply depth, normals, material inputs, view constants, and output handles
  through the host adapter. Remove concrete pipeline references from DDGI passes.
- [x] P2.3 Move graph read/write declarations beside each pass. Remove the central
  `switch (PassName)`; resolve pass identity through graph handles or the existing
  typed graph mechanism. Keep compute-write and graphics-sample accesses distinct.
- [x] P2.4 Replace repeated registry lookups with one selected volume/configuration
  snapshot. Initially preserve single-volume execution using an explicit stable
  selection policy; report ambiguity rather than use registration order.
- [x] P2.5 Retain current per-physical-pipeline DDGI state and renderer ownership.
  Preserve program preflight, baked upload protection, interruption recovery,
  publication receipts, composite-use protection, and cache clearing.
- [x] P2.6 Have BOTH adapters request DDGI graph contributions from the module.
  Delete their duplicated DDGI pass trains and resource-name mappings as each
  becomes unused. Do not leave separate Advanced DDGI algorithm ownership.

Exit gate: DDGI builds and exercises the same update/baked lifecycle through both
pipelines. Evidence shows no new behavior regression in supported baseline cases.

Exit result (2026-09-21): passed for the supported Vulkan mono path. A named
isolated session exercised the same 256-probe/64-ray module through Advanced and
Default. Advanced reached 265 completed dynamic updates and Default reached 170;
both reported initialized resources, ready geometry/environment, and complete
updates. A forced visibility-stage interruption in each host produced one matched
abort, one accepted non-publishing receipt, no unexpected completion/publication,
and recovery on the next update. Both hosts then baked a 256-probe asset with a
verified serialization round trip and loaded it in `Baked` mode. The broader
OpenGL/stereo matrix remains Phase 7 coverage, not a condition silently inferred
from this targeted exit run.

## Phase 3 — Unify lighting composition and Advanced admission

Depends on: Phase 2.

- [x] P3.1 Split DDGI field/screen sampling from final framebuffer composition.
  Common composition consumes declared outgoing radiance; adapters select the
  actual HDR target, MSAA handling, view layers, and ordering before temporal/post.
- [x] P3.2 Replace Advanced concrete-provider whitelist, enum exclusions, and
  `EnableDdgi`-style data with the selected plan's validated contribution/binding
  requirements. Inspect backend/native consumers too; renaming only the C# flag
  is insufficient. A descriptor alone must not grant unsupported native shading.
- [x] P3.3 Separate diffuse and specular policies. Replace DDGI-specific diffuse
  suppression with contribution semantics across deferred mono/stereo, forward,
  and Advanced shading. Preserve reflection-probe specular when replacing diffuse.
- [x] P3.4 Specify unavailable/partial-coverage behavior. Suppress baseline diffuse
  only under the declared replacement/coverage policy. Avoid black frames during
  initialization and avoid adding both fallback and GI for the same contribution.
  Fallback is explicitly configured and observable, never a silent method switch.
- [x] P3.5 Move diagnostic presentation into a neutral debug output contract.
  Remove DDGI runtime checks from general post-processing; document tone mapping
  and direct-light suppression for debug outputs independently of normal GI.
- [x] P3.6 Establish optional forward/transparent/world-space sampling bindings.
  Validate supported consumers and explicitly report remaining unsupported ones;
  do not advertise screen resolve as universal surface sampling.

Exit gate: each contribution is applied exactly once; Default and Advanced consume
the same output semantics; admission and post-processing contain no DDGI exceptions.

Exit result (2026-09-21): passed for the supported deferred-opaque contract. Both
live hosts produced viewed DDGI frames through the single module contribution and
neutral composition command. Source audit found one module contribution point per
host, one composition command per selected module, plan-driven baseline-diffuse
suppression, and no DDGI/provider-type condition in Advanced admission or either
post-processing path. Forward, transparent, and world-space DDGI consumers remain
explicitly unsupported; this result does not advertise them.

## Phase 4 — Validate the contract with another representation

Depends on: Phase 3. Do this before speculative shared-service generalization.

- [x] P4.1 Select the most runtime-ready structurally different implementation
  from the Phase 0 inventory (prefer radiance cascades or surfels if viable).
- [x] P4.2 Move that implementation's resources, settings, history, and passes into
  its module. Adapt its output to the precise shared radiance contract; do not
  reinterpret an existing texture without inspecting its shader math.
- [ ] P4.3 Run it through both host adapters. If algorithm defects prevent parity,
  record the defect in its own TODO and keep that capability unsupported until
  fixed. Shared adapter acceptance still requires a working second representation.
  The source inspection is now recorded in
  [Radiance Cascades runtime completion](radiance-cascades-runtime-completion-todo.md):
  only pre-authored 3D volumes and screen resolve exist, with no injection,
  propagation, or live update producer. The registry therefore rejects selection.
- [x] P4.4 Verify new integration required no provider-type checks, new generic
  host flags, or algorithm resource factories in either pipeline. Revise the
  narrow contract if a real requirement was missed; do not add a method exception.

Exit gate: two different working GI representations use the same two adapters.
Record which provider proved the boundary and the exact supported configurations.

Exit result (2026-09-21): not passed. Phase 4 is wrapped at an honest unsupported
boundary rather than promoting a sampler-only implementation to a working GI
representation. The module now receives host resource identities during resource
declaration and its per-frame cascade list is allocation-free, but P4.3 remains
unchecked until the linked runtime-completion TODO supplies and validates the
missing cascade producer through both hosts.

## Phase 5 — Extract proven shared services and clarify selection/lifetimes

Depends on: Phase 4.

- [x] P5.1 Extract only actually reused geometry/material/light/environment inputs.
  Keep geometry packing and tracing separate capabilities: voxelization/LPV must
  not be forced to build a triangle BVH. Keep tracing backend choice independent
  from GI provider choice.
- [x] P5.2 Remove DDGI naming from extracted input schemas and shader utilities.
  Review material emission, cutout/transmission, deformation, light limits, and
  environment revisions; preserve known behavior and explicit limit diagnostics.
- [x] P5.3 Share CPU scene metadata/revisions where safe. Retain physical GPU
  allocations per owner until wrapper/resource-sharing rules and synchronization
  are proven. Never infer sharing safety from identical world or texture objects.
- [x] P5.4 Distinguish authored field identity, persistent field/update context,
  and view-dependent resolve/history. Camera-centered fields may require separate
  anchors; stereo eyes may share updates only within a verified ownership domain.
- [x] P5.5 Make selection deterministic using stable identity and explicit priority/
  bounds policy. Include selection changes in invalidation. Single-volume support
  remains valid if explicit; multi-volume blending is separate follow-up work.
- [x] P5.6 Extract reusable GPU retirement/submission utilities only where they
  reduce duplication without weakening safety. Keep algorithm update stages local.

Exit gate: ownership and invalidation are documented; no cross-viewport history
contamination, stale generation reuse, duplicate unintended updates, or unsafe sharing.

### Phase 5 implementation record — 2026-09-21

The ownership and invalidation contract is recorded in
[Global Illumination Ownership And Selection](../../../../architecture/rendering/global-illumination-ownership.md).
The audit found no second working provider that actually shares DDGI's geometry,
material, direct-light, or environment GPU ABI. Therefore no speculative common
allocation service was created: the DDGI triangle/BVH packing, bounded hit-light
UBO, and octahedral environment capture remain provider-local, while the neutral
host surface-resource schema remains common. This preserves alternative
voxel/LPV/raster/tracing representations and their own light/material limits.

`DDGIFrameContext` is still scoped to a physical pipeline instance and verifies
the renderer API-wrapper owner; the DDGI resource owners are likewise scoped to
the physical pipeline and release on cache clear. Stable component identity is
only an authored selection key, never a GPU-sharing key. Selection now uses an
explicit `SelectionPriority`: the sole highest-priority valid volume wins, while
a tie reports the competing component IDs and disables DDGI. A selection is
snapshotted for an entire render frame; the next-frame field switch invalidates
the persistent field history before publishing under the new selection. Bounds
are intentionally not used for selection or blending until a multi-volume
coverage policy exists.

`XRGpuFence` already provides the reusable retirement primitive. DDGI keeps its
separate update, dynamic-use, baked-use, and composite-use receipt handling
because collapsing those paths would erase distinct ordering/failure guarantees.
The Phase 2/3 two-host live lifecycle evidence plus the scoped-owner source audit
show no shared atlas cursor, stale renderer-owner generation, or duplicate
same-frame field update. The broader multi-viewport/stereo matrix remains Phase
7 coverage; it is not inferred as permission to share physical resources.

## Phase 6 — Complete the module migration and remove compatibility seams

Depends on: Phase 5.

- [x] P6.1 Migrate remaining implemented GI paths to the same module contract,
  including light probes and ReSTIR where present. Give planned VCT/LPV methods
  explicit readiness requirements in their TODOs; do not count stubs as complete.
- [x] P6.2 Move provider settings, debug controls, authoring factories, and bake
  capability metadata behind registration. Preserve dedicated component types
  where useful. Common configuration selects providers without flat method fields.
- [x] P6.3 Remove superseded `IGlobalIlluminationPipelineProvider` algorithm flags
  and the parallel Advanced-only GI contract once all consumers migrate. If an
  adapter must temporarily remain, record its consumer and deletion condition.
- [x] P6.4 Remove obsolete per-method feature bits, declarations, FBO factories,
  shader switches, and command guards from hosts. Keep only registry mappings and
  generic contribution/capability decisions.
- [x] P6.5 Document how to add a provider, supported inputs/outputs, ownership,
  runtime statuses, settings, debug integration, and both adapter validation paths.
  Update settings/schema generation if its source types change.

Exit gate: implemented GI algorithms are self-contained modules; inactive providers
allocate no resources or graph work; planned algorithms have honest support status.

### Phase 6 implementation record — 2026-09-21

Light probes/IBL and DDGI now resolve through the same registry/module contract.
Light probes intentionally contribute no screen-field resources; DDGI owns its output,
presentation material, atlas/buffer names, lifecycle, debug presentation, and bake
surface. Radiance Cascades retains a module-shaped resolve boundary but remains
explicitly unsupported, so the registry never invokes it. ReSTIR, VCT, Light
Volumes/LPV, and Surfel source were classified as unverified planned work rather
than wrapped in pretend providers; their descriptors have no module factory and
produce an unsupported plan with no GI resource or graph contribution.

Provider descriptors now carry the provider-owned settings type and explicit
authoring/debug/bake metadata. DDGI registers `DDGIVolumeComponent` plus all three
surfaces; Radiance Cascades registers its authoring/debug metadata without changing
its unsupported runtime status. VCT and LPV TODOs now state the exact modular
provider, lifecycle, debug, and both-host validation requirements before their
descriptors can become supported.

The obsolete `IGlobalIlluminationPipelineProvider`, `UsesX` fan-out, host feature
bits, per-method resource declarations, static DDGI aliases, DDGI/other-GI host FBO
factories, host shader switches, direct pass insertion, and host command guards are
removed. Default and Advanced retain only neutral plan-driven bindings, generic
resource/pass registry calls, and host-owned composition targets. The developer guide
now records the addition workflow, signal/ownership contract, settings/debug/baking
metadata, actual support status, and both-adapter validation path.

## Phase 7 — Final runtime acceptance and cleared regression work

Depends on: Phase 6. Test changes additionally require explicit user clearance.

- [ ] P7.1 Complete the runtime matrix below and attach evidence, commit, backend,
  pipeline, scene configuration, observations, and known limitations to each run.
- [ ] P7.2 Compare timings/allocations against baseline for DDGI. Verify the
  abstraction adds no per-ray/per-probe CPU virtual dispatch, avoidable recurring
  allocations, or additional scene preparation per eye/viewport.
- [ ] P7.3 After runtime validation and user test clearance, replace brittle source
  string/bit-position assertions with behavioral provider/adapter contracts. Keep
  meaningful existing algorithm/math coverage; do not encode the new class layout.
- [ ] P7.4 Cover registry resolution, explicit rejection, graph resource declarations,
  output semantics, mode/volume/layout changes, abort publication, disposal, owner
  restart, stereo, two viewports, and two authored volumes. Run the narrowest checks.
- [x] P7.5 Run the final source audit against Phase 0 inventory. Every remaining
  algorithm reference in generic pipeline code needs a documented justification;
  algorithm-specific resource/pass/shader branching is a failed acceptance gate.

Exit gate: runtime evidence is complete for claimed support, cleared regressions
pass, docs are current, and unresolved unsupported configurations are explicit.

### Phase 7 implementation and evidence — 2026-09-21

The final source audit exposed one remaining architectural seam: both hosts still
selected DDGI's generation-key resource variant and named DDGI output/presentation
resources while otherwise using the neutral module contract. The selected module now
owns its `RenderPipelineResourceVariant` and its output identities. The registry is
the only route from a supported plan to that module decision; the host resource
context now contains only neutral surface inputs and its composition target. A fresh
audit found no DDGI, Radiance Cascades, Surfel, VCT, LPV, ReSTIR, Light Volume, or
path-tracing reference in either Default or Advanced pipeline source subtree.

The Core, OpenGL, and Vulkan rendering projects each build with zero warnings and
zero errors after that move. No tests were added or modified: test work still needs
explicit user clearance.

Two named, isolated Advanced/Vulkan/mono sessions ran an uncommitted working tree
with a temporary 4 x 2 x 4, 8-rays-per-probe dynamic DDGI volume. The follow-up
session completed eight spaced viewport captures without readback failures or a
magenta fallback. It reached a full 240-sample DDGI allocation window and kept the
provider resource/pass sequence live. The inspected contact sheet is nevertheless
not acceptable lighting evidence: it contains a large black region and severely
clipped/bloomed lighting. The first run also completed 1,766 dynamic probe updates
with initialized geometry/resources, but its editor endpoint became unavailable
after capture; the named session was then stopped. Both session logs show the
following unresolved runtime defects:

- The Vulkan barrier planner cannot resolve the external `DDGIMaterialTextures`
  image for DDGI prepare, trace, hit-shade, and relocation passes.
- The frame-package validator reports descriptor-generation mismatches after the
  late `DDGILightBlock` buffer registration.
- All ten DDGI command scopes allocate managed memory in the full steady-state
  sample window (for example, 1,232-4,336 bytes in the final samples); no
  baseline comparison or zero-recurring-allocation result can be claimed.

These sessions found no shader compilation failure or Vulkan VUID in the reviewed
log excerpts. They do not clear P7.1, P7.2, or P7.4: the visual result is rejected,
the resource-synchronization and publication warnings remain, there is no approved
behavioral-test work, and the wider backend/stereo/multi-viewport/two-volume matrix
is still incomplete. Radiance Cascades is still unsupported and cannot satisfy the
second-working-representation requirement.

A subsequent repair pass removed the two resource-publication defects above. DDGI's
material array is now represented as an explicitly borrowed external Vulkan image,
with the exact native generation frozen into the frame package and checked again at
production seal. Imported textures and buffers are staged and published atomically
at a frame boundary, and backend-ready package identity now includes the imported
resource instance revision. DDGI direct lights are generation-owned instead of a
late external-buffer mutation. The live `phase7-accept-0921` run rebuilt the DDGI
generation, materialized `DDGIMaterialTextures`, compiled its compute/graphics
programs, waited one intentional frame for import publication, and then executed all
ten DDGI scopes without the former external-image or late-buffer generation failures.

That run did not clear the performance gate. After warm-up, every DDGI command still
reported recurring managed allocations. A temporary allocator probe (removed before
the final source build) isolated the common Vulkan compute path: link, generation
context, snapshot validation, and queue insertion were allocation-free; binding
snapshot capture was the dominant source, with frame-operation rent and semantic
resource-use preparation contributing smaller recurring allocations. The last
unprobed samples ranged from 1,248 bytes for the atlas updates to 4,360 bytes for the
environment pass. This is a confirmed remaining P7.2 defect, not a DDGI shader or
resource-lifetime failure. A post-repair DDGI screenshot/matrix run was not completed
before wrap-up, so P7.1 and P7.4 also remain unchecked. Test changes and execution
remain deferred pending explicit user clearance. Radiance Cascades remains governed
by its dedicated runtime-completion TODO and was intentionally not advanced here.

## Runtime validation matrix

Run relevant cases incrementally at phase exits, then fill the final matrix.
Do not run a full Cartesian product on every small edit. Unsupported configurations
must produce a clear rejection; never label an unexecuted case as passed.

| Dimension | Required cases |
|---|---|
| Hosts/backends | Default + OpenGL; Default + Vulkan; Advanced + OpenGL; Advanced + Vulkan, according to actual supported configurations |
| Providers | Disabled; light-probe baseline; DDGI dynamic and baked; second working representation; remaining implemented providers |
| Surface/view inputs | Mono; supported stereo/multiview; composed OpenXR two-pass owner; normal/reverse depth; relevant MSAA/resolution changes |
| Lighting semantics | Emissive-only scene; direct-only control; indirect-only/debug view; metallic/diffuse surfaces; preserved specular; AO enabled/disabled; field coverage edge |
| Consumers | Deferred; forward opaque/masked; Advanced native shading; transparent/world-space sampling where advertised |
| Transitions | Mode change; settings/layout change; camera cut; volume selection change; resize; scene reload; pipeline cache clear; renderer-owner replacement |
| Lifetime/failure | Two viewports/cameras; two authored volumes; interrupted/rejected update; pending program/resource preparation; baked upload while prior sampling is in flight |
| Isolation/performance | Inactive modules absent from resource layout and graph; no duplicate unintended eye updates; memory released/retired safely; allocation/timing comparison |

For live runs follow `AGENTS.md`: named isolated editor session, controlled scene,
actual screenshot inspection, relevant logs, and session-specific teardown. Use
RenderDoc when pass/resource evidence is needed. Build success alone does not
prove lighting correctness, GPU lifetime safety, or Advanced parity.

### Phase 7 evidence ledger — 2026-09-21

| Run | Configuration | Observed result | Acceptance status |
|---|---|---|---|
| Phase 2-4 exit work | Default and Advanced, Vulkan, mono DDGI dynamic/baked | Dynamic update, abort/recovery, bake round-trip, and baked load evidence are recorded above. | Supports the targeted Phase 2/3 exit gates only; not the Phase 7 matrix. |
| `phase7-gi` | Advanced, Vulkan, mono, dynamic temporary DDGI volume | 1,766 updates; provider resources and geometry became ready. Capture inspection rejected visual quality; the endpoint became unavailable after capture. | Evidence retained; not a pass. |
| `phase7-gi-followup-0921` | Advanced, Vulkan, mono, dynamic temporary 32-probe DDGI volume | Eight spaced captures completed with no drops or magenta. Full allocation window shows recurring per-command managed allocations; logs retain external-image barrier and descriptor-generation warnings. | Evidence retained; not a pass. |
| `phase7-accept-0921` | Advanced, Vulkan, mono, dynamic temporary 32-probe DDGI volume | Repaired borrowed-image and staged-import publication paths rebuilt and ran all ten DDGI scopes without the former native-generation/import failures. Steady recurring allocations remained. | Resource-publication repair accepted; performance and post-repair visual gates remain open. |
| `phase7-allocprobe3-0921` | Advanced, Vulkan, mono, dynamic temporary 32-probe DDGI volume | Temporary rolling probes localized recurring allocations to compute binding snapshots, frame-operation rent, and semantic resource-use preparation; zero-byte buckets ruled out link/context/validation/enqueue. Probe code was removed after capture. | Diagnostic evidence only; P7.2 remains open. |
| Remaining matrix | OpenGL, other host/modes, stereo/two-pass, two viewports, two authored volumes, and second representation | Not executed or explicitly unsupported. | Must remain unchecked. |

## Definition of done

- [x] Both pipelines consume one common provider contract and the same modules.
- [x] DDGI internals are absent from generic pipeline resource/command/post code.
- [ ] Advanced admission is capability-based and retains native execution ownership.
- [ ] At least two distinct working representations prove the common boundary.
- [ ] Every existing implemented GI path has migrated or has an explicit unresolved
  blocker; no placeholder is advertised as supported.
- [ ] Diffuse/specular, material response, AO, exposure, validity, and coverage are
  explicit and validated, with no duplicate indirect contribution.
- [ ] Lifecycle, frame transitions, stereo, and renderer replacement retain safety.
- [ ] A new method can be added inside its module plus central registration and
  authoring/settings integration without algorithm edits in either host adapter.
- [ ] Runtime evidence, approved tests, developer docs, and support matrix are current.

## Execution record

| Date / phase | Changes | Validation and evidence | Remaining issue / next action |
|---|---|---|---|
| 2026-09-21 / planning | Created this TODO from source review at `cd5822051`; no runtime implementation changes | Source inspection only; no build, tests, or live renderer validation for this document | Start P0.1; record baseline and actual provider support before editing runtime code |
| 2026-09-21 / Phase 0 | Completed the checkout audit, coupling inventory, host-frame trace, provider readiness matrix, and runtime-baseline record in this document; no runtime code, settings, shaders, or tests changed | Rechecked `cd58220514ae6455af68a9526f2fa32d8d127e4a`; source inventory covers Default, Advanced, shared DDGI, bootstrap, and backend consumers. Reused the dated isolated-session evidence in `2026-09-20-ddgi-working-copy-verification.md`; Markdown and whitespace validation pass | Begin P1.1: define the neutral provider identity, registration, and explicit support-result contract without changing DDGI behavior |
| 2026-09-21 / Phase 1 | Added neutral GI contracts, registry/plan resolution, lifecycle surface, and Default/Advanced host adapters. Both pipeline selection surfaces now refresh and consume the same immutable plan; Default resource feature selection does too. No DDGI algorithm/pass/resource ownership moved. | `dotnet build .\\XREngine.Runtime.Rendering\\XREngine.Runtime.Rendering.csproj --no-restore --no-incremental -m:1` passed with 0 warnings and 0 errors. No tests were added or changed under the repository validation policy. | Begin P2.1: introduce the DDGI module and move its resource declarations and pass sequence out of both hosts. |
| 2026-09-21 / Phase 2 partial | Added `DDGIGlobalIlluminationModule`, provider-owned resource identifiers, neutral host surface bindings, registry-driven module resource declaration, and registry-driven DDGI command contribution. Removed the duplicated Default/Advanced DDGI pass trains; moved common DDGI environment/atlas/buffer declarations behind the module. | `dotnet build .\\XREngine.Runtime.Rendering\\XREngine.Runtime.Rendering.csproj --no-restore --no-incremental -m:1` passed with 0 warnings and 0 errors. No live session was run because the extraction has not yet met its graph-declaration and selection-snapshot gates. | P2.3: replace `VPRC_DDGIComputePass` class-name graph switch with per-pass declarations. P2.4: introduce a deterministic selected-volume snapshot and ambiguity diagnostic, then validate ownership/lifecycle before closing P2. |
| 2026-09-21 / Phase 2 implementation | Completed the DDGI vertical extraction: module-owned common resource declarations and command sequence, provider resource names, neutral host bindings, and module contributions from both adapters. DDGI command classes no longer reference Default/Advanced pipeline types; the Default adapter now supplies the MSAA-aware composition target. Each DDGI pass declares its own graph accesses, and `DDGIFrameContext` snapshots the selected volume per pipeline/frame. The volume registry now rejects ambiguous active-volume sets with a rate-limited diagnostic instead of selecting by registration order. Existing per-pipeline state, renderer-owner invalidation, program preflight, baked upload, interruption, publication, and composite-use receipt paths remain in `DDGIFrameContext`. | Source audit found no DDGI class-name graph switch, first-active DDGI lookup, or Default/Advanced reference in DDGI provider/pass code. `dotnet build .\\XREngine.Runtime.Rendering\\XREngine.Runtime.Rendering.csproj --no-restore --no-incremental -m:1` passed with 0 warnings and 0 errors. Two named isolated editor sessions were started and cleanly stopped while their isolated editor builds were still in progress, so neither reached MCP readiness and no live rendering claim is made. No tests were added or changed. | Run Default and Advanced dynamic/baked lifecycle captures in a named ready editor session, inspect screenshots/logs, and record any regression before declaring the Phase 2 live exit gate complete. |
| 2026-09-21 / Phase 3 partial | Replaced the Advanced native-stage GI provider whitelist and DDGI-specific `EnableDdgi` request bit with the registry-resolved plan and a neutral `RequiresMaterialSurfaceExports` binding requirement. The plan now states native material-export, probe/IBL-binding, and probe-diffuse-replacement semantics. Both OpenGL and Vulkan preserve the same bit position while consuming the neutral requirement. Replaced direct DDGI diagnostic-state reads in Default/Advanced post-processing and exposure with `GlobalIlluminationDiagnosticPresentation`. Deferred mono/stereo shaders now use `SuppressProbeDiffuse`, driven by contribution semantics rather than a DDGI method flag; probe specular remains bound. | `dotnet build` passed with 0 warnings and 0 errors for `XREngine.Runtime.Rendering`, `XREngine.Runtime.Rendering.OpenGL`, and `XREngine.Runtime.Rendering.Vulkan`. Source audit found no `EnableDdgi`, `IsNativeProvider` admission call, or DDGI diagnostic presentation read in those projects. No tests were added or changed. | P3.1 still needs a generic final-radiance composition command while retaining DDGI composite-use receipts. P3.3/P3.4 need live evidence that native, deferred, and forward contribution coverage prevents both duplicate and missing diffuse; P3.6 needs an explicit supported transparent/world-space binding or a documented rejection. Phase 2's live gate remains pending. |
| 2026-09-21 / Phase 3 implementation | Added `VPRC_GlobalIlluminationCompositePass` so DDGI now produces only its outgoing screen radiance; a neutral command owns source sampling, HDR-target blend/replacement, stereo choice, graph presentation, and ordering. `VPRC_DDGICompositeCompletionPass` retains DDGI's GPU-read receipt after that neutral presentation. `GlobalIlluminationCompositionState` declares the initialization policy: retain probe diffuse during warm-up, publish the first valid DDGI opaque result without compositing it, then replace the deferred probe-diffuse contribution on following frames. Debug output is immediately composable and replaces the destination. Deferred and native Advanced shading both suppress only probe diffuse; probe specular remains enabled. Removed the obsolete Advanced-only GI interface/providers/whitelist. Provider descriptors now declare supported consumers: DDGI explicitly supports deferred opaque screen composition only, while forward, transparent, and world-space DDGI sampling return an unsupported diagnostic through `GetConsumerSupport`. | `dotnet build .\\XREngine.Runtime.Rendering\\XREngine.Runtime.Rendering.csproj --no-restore --no-incremental -m:1`, `dotnet build .\\XREngine.Runtime.Rendering.OpenGL\\XREngine.Runtime.Rendering.OpenGL.csproj --no-restore --no-incremental -m:1`, and `dotnet build .\\XREngine.Runtime.Rendering.Vulkan\\XREngine.Runtime.Rendering.Vulkan.csproj --no-restore --no-incremental -m:1` all passed with 0 warnings and 0 errors. Source audit found no `EnableDdgi`, Advanced GI provider whitelist, or DDGI-specific diagnostic presentation read. No tests were added or changed. | Run the Phase 2/3 live matrix in named isolated editor sessions: Default and Advanced, OpenGL and Vulkan, dynamic/baked DDGI, warm-up, debug, deferred/forward coverage, and unsupported-consumer diagnostics. Inspect screenshots and session logs before declaring either live exit gate complete. |
| 2026-09-21 / Phase 3 live-validation attempt | Started isolated MCP editor session `phase3-gi` and probed its endpoint, then stopped the named session cleanly. | The isolated build did not complete startup: the MCP endpoint refused the connection and the session manager reported `McpReady: False` with no editor process. No capture, runtime behavior, or live exit gate is claimed. | Resolve the isolated editor startup failure, then rerun the Phase 2/3 live matrix and inspect captures/logs. |
| 2026-09-21 / Phase 4 partial | Selected Radiance Cascades as the structurally different candidate because it already has authored cascade volumes plus mono/stereo screen resolve and per-view history. Added its provider-owned resource identities and `RadianceCascadesGlobalIlluminationModule`; resource declaration, history, resolve pass, and neutral composition now use the same registry/module boundary as DDGI. Both Default and Advanced request the selected module at the surface-resolve anchor, and Advanced's native material-surface resource profile is now driven by the plan requirement rather than DDGI selection. The resolve now converts sampled incident cascade radiance into material-shaded outgoing diffuse radiance using albedo, metallic exclusion, and `1/pi` before temporal history/composition. | Core, OpenGL, and Vulkan rendering projects build with 0 warnings and 0 errors. Source audit found no host command-chain ownership of `VPRC_RadianceCascadesPass` and no Default/Advanced reference from the cascade module or resolve pass. No tests were added or changed. A background isolated editor session `phase4-gi` again stopped before MCP readiness or editor-process creation, so no shader compile, capture, or runtime claim exists. | Keep Radiance Cascades explicitly unsupported until controlled Default and Advanced captures validate its output math, lifecycle, history, mono/stereo behavior, and contribution coverage. P4.3 and the Phase 4 exit gate remain blocked by missing live evidence; do not advertise this as the second working representation yet. |
| 2026-09-21 / Phase 2-4 exit wrap-up | Repaired the module declaration context so both hosts supply their neutral surface/resource identities during resource declaration. Ran the same DDGI module through Advanced and Default Vulkan, including dynamic updates, deterministic interruption/recovery receipts, bake round trips, and baked-mode load. Audited the neutral composition/admission path. Finalized Radiance Cascades as an explicit unsupported capability and split its missing producer work into a dedicated TODO; removed its per-frame active-cascade LINQ/materialization. | Editor build passed with 0 warnings/errors. Advanced: 265 dynamic updates before the interruption check; Default: 170. Each interruption reported one matched abort, one accepted non-publishing receipt, zero unexpected completion/publication, and next-update recovery. Both 256-probe bake files passed round-trip verification and loaded with `updateMode=Baked`, initialized resources, and submitted state. Viewed both host captures. Startup publication-generation warnings were observed while resources materialized; no shader compile failure, Vulkan VUID, or steady-state fatal/error was found. No tests were added or changed. | Phase 2 and Phase 3 targeted exit gates are complete. Phase 4 remains intentionally incomplete at P4.3 because Radiance Cascades has no live injection/propagation/update producer. Complete `radiance-cascades-runtime-completion-todo.md`, then run both-host mono/stereo captures before claiming the second representation. |
| 2026-09-21 / Phase 5 | Added the documented GI ownership/selection contract and an explicit DDGI selection priority. Kept triangle/BVH geometry, direct-light UBO, and environment capture provider-local after confirming that no second working provider consumes their GPU ABI. `DDGIFrameContext` now records a stable authored-field ID beside its per-frame weak-reference snapshot; renderer/pipeline object identity remains the physical ownership key. | `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore --no-incremental -m:1` is the required narrow build. The source audit confirms conditional-weak-table ownership per physical pipeline, renderer-owner invalidation, cache-clear disposal, same-frame field ownership, and separate submission receipts. Existing Phase 2/3 live evidence covers two hosts, updates, abort/recovery, and baked publication; no tests were added or changed. | Phase 5 is complete with explicit no-sharing rules. Run the broader multi-viewport/stereo matrix under Phase 7 before broadening any sharing claim. Phase 4's missing second working provider remains unchanged. |
| 2026-09-21 / Phase 6 | Removed the legacy flag interface and all Default/Advanced per-method GI profile bits, resource declarations, output/FBO factories, static DDGI aliases, VCT host hook/pass, direct pass insertion, and host command guards. Added registry-owned feature metadata and module ownership for light probes/IBL and DDGI; classified all other source-only methods as unsupported. Updated the VCT, LPV, and developer documentation with readiness and two-host validation requirements. | Core, OpenGL, and Vulkan rendering projects each build with 0 warnings and 0 errors. The source audit finds no legacy GI flag interface, `UsesX` fan-out, DDGI host alias, or VCT host bridge; unsupported plans return before module resource/pass contribution. `git diff --check` reports no whitespace errors. No tests were added or changed. | Phase 6 is complete. Phase 4 remains unsupported and Phase 7 remains the broader runtime matrix. |
| 2026-09-21 / Phase 7 partial | Moved selected resource-variant and output-identity ownership fully into the GI module/registry, removing the last DDGI resource/layout inspection from Default and Advanced. Updated the acceptance ledger with source, build, live-capture, allocation, and log evidence. | Core, OpenGL, and Vulkan rendering builds passed with 0 warnings/errors. The source audit finds no named GI algorithm reference in generic Default/Advanced source. Two named isolated Advanced/Vulkan/mono DDGI sessions were run and stopped; eight follow-up captures completed without drops or magenta, but the inspected output is visually invalid. The full 240-sample allocation window reports recurring managed allocations in every DDGI command scope; logs retain unresolved external-image barrier warnings and `DDGILightBlock` descriptor-generation mismatches. No tests were added or changed. | P7.5 is complete. Next: fix the external DDGI texture synchronization and late buffer-publication contract, eliminate or justify steady-state allocations against a baseline, then rerun visual/matrix validation. P7.3 needs explicit user test clearance; Phase 4 remains unsupported. |
| 2026-09-21 / Phase 7 resource repair | Made imported image/buffer publication frame-boundary atomic, added imported-resource instance identity to backend-ready packages, modeled external images as borrowed physical groups, froze and revalidated exact native image generations, and made DDGI direct lights generation-owned. Refreshed GI generations when runtime settings change. | Editor and Vulkan builds passed with 0 warnings/errors before the final diagnostic pass. `phase7-accept-0921` rebuilt and executed DDGI without the prior external-image or late-buffer generation failures. Temporary rolling probes localized the remaining recurring allocation floor to binding snapshot capture, operation rent, and semantic preparation, then were removed. No tests were added, changed, or run. | The resource-lifetime/publication bugs are fixed. P7.1/P7.4 still need post-repair visual and matrix evidence; P7.2 remains open on recurring allocations; P7.3 remains gated on explicit test clearance. Radiance Cascades remains intentionally excluded. |

For each later entry identify the exact next unchecked step. Mark partial work as
partial; keep failed/blocked validation visible. Do not mark the architecture done
while either host still owns an algorithm-specific implementation path.
