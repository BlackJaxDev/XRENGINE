# Native authored material vertex functions

The WebGPU backend executes a selected authored local vertex function in the
Advanced canonical geometry path. The offline material cooker emits a paired
PBR raster program and packed compute producer from the same isolated function.
The function is authored data, not a built-in deformation equation. A semantic
label or a descriptor link without the exact source closure cannot admit it.

## Authored and cooked contract

The optional `nativeVertex.body` extension belongs to the existing target
material recipe. The initial profile is opaque PBR color with four additional
`ShaderVector4` parameters, `NativeVertexInput0` through `NativeVertexInput3`,
after the six PBR parameters in the existing `XRMaterial.Parameters` array.
The persistent material identity determines the `mat-<guid>` cook name. The
material retains its exact cooked raster identity in its ordinary shader field.
There is no new authored material serialization format or desktop GLSL change.

The isolated function can modify local position and normal using those four
inputs and local arithmetic/control flow. Tangent, UV, color, primitive
topology, view state, stage builtins, external buffers and textures are outside
this profile. The source material must be the exact `XRMaterial` type; custom
subclasses and callback/binding behavior require a separate exact contract.

Both raster and compute wrappers use one pinned octahedral normal helper.
The raster canonicalizes normals before the function and after its result,
matching the packed canonical geometry boundary, including the static
`AdvancedPackedVertexCodec` fallback. Non-unit normal magnitude cannot change
the function's geometry differently in the two paths. Arbitrary function
position results still must be finite; the compiler does not prove arbitrary
numerical equations safe.

The cooker emits eight programs: ordinary PBR raster, packed native compute,
depth/normal, directional/point/spot casters, and directional/local shadow
receivers. All share the exact function, compiler and closed source dependency
set. Auxiliary metadata carries the exact descriptor identities and each
consumer validates the required program's entry points and physical layout.
Raster inputs occupy group 1 binding 1, a vertex-only dynamic 64-byte material
uniform, leaving the canonical shadow group intact. Compute uses separate
source/output geometry storage bindings and a dynamic 160-byte input block.
See [ShaderCooker](../../../../Tools/ShaderCooker/README.md#authored-native-vertex-functions)
for the recipe and ABI details.

## Native ownership and execution

Canonical material publication copies the four current inputs and retains the
verified compute descriptor. Later material edits cannot change an already
published frame. The publication ring clears managed descriptor references only
after its final pin retires.

Each output owns a geometry overlay in its completion-reclaimed frame slot.
Canonical skin/morph is produced first and copied in submission order into the
source arena and the overlay. The material function then appends its packed
64-byte current and previous vertex ranges. Source and destination are separate
storage buffers; no read/write binding aliases the same arena.

Previous input values advance only after the complete executed frame is accepted
for submission. Reuse requires an adjacent world frame, matching database epoch,
draw, geometry, deformation, vertex count and exact program identity. Missing
previous skin output or discontinuity uses the engine's named history-reset,
frame-gap, topology or vertex-count reason. Previous object matrices never stand
in for previous deformed vertices.

Visibility payloads and prepared temporal rows select the same output ranges.
Indexed, compute-meshlet and explicitly requested CPU-direct submission preserve
their requested strategies. Visibility, reconstruction, native shading and MSAA
all bind the output-local arena. The CPU never reads produced vertices or GPU
visibility/count results to emulate this producer.

An arbitrary function has no inferred finite envelope. Native candidates restore
the admitted view bit that early bind-pose classification may have cleared and
mark their bounds conservatively visible. GPU triangle expansion and the chosen
submission strategy remain active. Renderer callbacks, external vertex streams,
mesh deformers and scoped binding overrides reject when their source behavior
cannot be preserved by this profile.

## Raster auxiliary ownership and bounds

Runtime-only, source-owned materials select exact depth/normal and caster
companions. Their callbacks validate the current source identity and publish the
live four input values. Input value changes do not freeze an old parameter
array. The original material's serialized bytes do not change when these caches
are created. Source invalidation and destruction retire all owned auxiliaries.
PBR receivers select the matching generated shadow receiver, retaining the
canonical light, bias, projection and filtering logic. Missing companions are
reported rather than replaying undeformed geometry.

Static generic raster and its auxiliary passes share the canonical function
interface. The existing generic skin/morph producer has a different normal
domain from the canonical aggregate producer: raw versus packed bind normals
before morphs, and cofactor versus direct palette normal transforms. This new
function profile therefore rejects generic raster skin/morph until that route
has an equivalent canonical source. Advanced native morph/skin followed by the
material function remains implemented. This is a source-equivalence gap, not an
inherent WebGPU limitation. Existing generic and aggregate deformation arithmetic
and composition order are unchanged.

Tessellation, topology-changing stages, alpha coverage extensions, textured PBR
functions, view-dependent functions and other unmodeled source profiles retain
specific unsupported diagnostics. Unknown profiles do not select an unlit or
CPU substitute.

## Validation and remaining acceptance

The production cooker has exercised sinusoidal, polynomial/control-flow and
constant bodies, each producing all eight programs. Unsupported scope/resource/
stage access and modified source/ABI identities reject. Old unlit and authored
PBR V1/V2 recipes retain byte-identical descriptor and WGSL output when the
extension is absent.

Actual runtime assembly probes cover immutable material publication, exact
core/auxiliary admission, retained PBR factors, source-owned auxiliary caches,
live input replacement, serialization/hydration, named history discontinuities,
and warmed allocation. A separate probe invokes the production dispatch method
with an explicit fake GPU boundary to check binding separation, byte offsets,
previous source selection, pending/rejected programs and dispatch ordering. This
trace does not establish physical GPU execution.

The production content cooker packages all eight exact programs together and
individually. The unchanged browser manifest validator and verified-payload
loader preserve descriptor and WGSL bytes, including their source hashes. The
packaging probe covers malformed identities, missing source entries and duplicate
identities. This validates the shipped catalog/loading boundary without treating
a local-file fetch adapter as a live browser.

Evidence is under
`Build/_AgentValidation/20261001-225000-lit-surface/native-vertex/`, with the
canonical eight-program manifest and source/compiler hashes in
`auxiliary/report.json`. Source review and managed/compiler checks are distinct
from live browser acceptance. Current/previous rendered motion, representative
skin/morph composition, actual depth/normal and shadow pixels, bounds behavior,
MSAA output, and completion/device-recovery execution still require the selected
physical browser/device acceptance runs.
