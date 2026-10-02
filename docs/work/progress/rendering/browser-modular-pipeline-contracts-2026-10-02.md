# Authored browser pipeline contracts

The browser output retains its assigned `RenderPipeline` asset and executes the
normal viewport command chain. Publication does not select a pipeline class from
a whitelist or substitute Default for an authored asset. A genuinely unassigned
camera still uses the existing browser default-source recipe.

## Requirements and dependencies

`RenderPipeline.CreateRequirements(...)` constructs a detached, target-specific
description. `DescribeRequirements(...)` and the matching command hook declare
operations, scene pass indices, cooked program references, and material/program
dependencies. Publication traverses all declared branches without evaluating
callbacks, generating resources, or changing the live desktop command chain.
Pipeline-owned postprocess schemas determine camera settings; Default exposure,
tonemapping, bloom, and AO requirements are not imposed on unrelated pipelines.

Opaque callbacks and resource factories can use serializable
`DeclaredRequirements` on either the pipeline or command. Its `Backend` is a
stable backend string, such as `webgpu`; operation names describe the required
backend behavior. `Programs` maps catalog binding keys to optional exact
descriptor identities. Scoped keys use the catalog's `GetBindingKey(scope,
pass)` contract, so unrelated assets can select different programs for the same
logical pass. Referenced `Materials` remain in the ordinary serialized asset
graph; `ProgramIdentities` retains exact whole-program references without
serializing a runtime program's enumerable interface. Both are included by the
hydrated-world shader audit.
Direct compute program references remain separate from built-in compute-kernel
ABI catalogs.

`VPRC_DispatchCompute` serializes its shader collection before its authoritative
`CookedProgramIdentity`; the runtime `ComputeProgram` is not serialized as a
shader enumeration. Discovery and execution use that same restored program.
Runtime admission receives the output's explicit immutable artifact resolver
and validates identity-only and direct-program dependencies, including attached
verified companions. It does not consult a global resolver. Compute execution
prepares the backend wrapper before publishing scoped bindings; the WebGPU
wrapper subscribes the normal program dispatch event. Unsupported image bindings
still fail by their specific operation instead of silently dropping dispatch.

Known commands describe their own operations. A command without a declaration
reports its command identity and missing capability; a new command class can
implement the shared hook without changing a browser type registry. Scene mesh
materials must have a pass represented by at least one published camera's
declared scene routes. Pass indices are authored integers, not Default enum
membership. Pipeline-owned fullscreen/compute materials are audited separately.

## Hydration and output attachment

Browser preparation reconstructs the execution command chain and pass/sorter map
after authoring properties hydrate. Cold publication reads the authored custom
command graph without rebuilding it. Runtime admission reads stored camera
settings before refreshing the schema, then caches each camera/pipeline/state
identity, command generation, and postprocess change stamp. Unchanged frames do
not allocate another requirement description.

The exact package catalog binds through `RenderPipeline`. Legacy Default tonemap
access remains compatible. Prepared browser outputs resolve the invalid-material
factory only when needed, so merely attaching a clear-only viewport does not
import an unused desktop shader. Desktop invalid-material preparation is
unchanged. Explicit false/zero command settings with nonzero constructor defaults
carry matching serialization defaults so save/reload preserves them.

Standard lit materials use optional `IRenderPipelineAmbientOcclusionProvider`
publication. A pipeline without that provider receives neutral visibility from
the existing renderer-owned texture; an enabled provider must return its actual
generation-owned visibility texture. No Default pipeline cast is required.

## Capability boundaries

These changes expose real requirements; they do not implement missing shader
families or renderer operations. Advanced declares integer color attachments,
storage-image bindings, GPU-driven submission, explicit synchronization, and its
native stage executor. Their missing implementations remain specific capability
errors. Global CPU-only browser policy checks have been removed; GPU submission
admission belongs to the actual selected command/renderer operations, without an
implicit CPU or readback fallback.

Clear-only output uses the existing layoutless-resource readiness path and
stored canvas clear commands. Zero scene mesh draws is valid presentation.
Native construction, serialization, and compilation are structural evidence;
live WebGPU output still requires the GPU qualification workflow.

## Validation boundary

- Targeted Release WebGPU builds passed with zero warnings/errors, including
  the final program-reference serialization correction. The subsequent minimal
  compute-event-route build also passed with zero warnings/errors
- An ignored native fixture ran 39 assertions against freshly built engine
  assemblies and the actual browser publisher audit sources. It saved/reloaded
  a shader-free clear pipeline, attached its pipeline instance without shader
  IO, preserved explicit false clear/depth flags and pipeline identity, and
  retained an unrelated integer scene pass (`742`)
- A separate authored quad pipeline declared its own normal resource factory,
  non-PBR material, and `custom::custom-pass` companion. Save/reload preserved
  both shader stages and the pipeline ID. The production world audit admitted
  the real hash-verified companion and included it in publication dependencies
  without calling the resource factory or substituting the asset
- Exact compute program identity, source text, and standalone program references
  survived serialization. Runtime admission rejected an absent exact descriptor
  and admitted an attached verified program. Camera-state changes advanced the
  admission stamp; unchanged values did not. Two thousand warmed stamp reads
  measured zero managed allocation
- Independent readback covered hydration order, cold publication, pass maps,
  exact dependency publication, camera-state caching, and lazy fallback lifetime

The fixture installs the ordinary Data and Rendering serialization registrations
and a minimal camera depth-preference host leaf. It does not execute a renderer,
claim GPU pixels, or certify a live browser startup. Clear-frame presentation
with zero draws is separately covered by the JavaScript engine-frame boundary
probe. The subsequent frozen integration gate passes Editor, Server, VRClient,
all nineteen portable project rows and fresh native-Jolt browser publication
with zero compiler warnings/errors. Real GPU qualification of the new authored
pipeline profiles remains separate.

Disposable native evidence is under
`Build/_AgentValidation/20261001-225000-lit-surface/scratch/modular-pipeline/`;
the real custom companion was produced by the existing scoped shader-cooker
fixture. No tracked tests, dependencies, desktop scheduling changes, or default
pipeline substitution were introduced.
