# Browser octahedral impostor contract

The browser component has an additive `OctahedralImpostorV1` material semantic
and two exact cooked programs: ordinary transparent raster and the matching
GPU source-order gate. The desktop vertex, fragment, common GLSL source, and
existing authored asset formats remain unchanged.

## Source and material closure

`OctahedralImpostor.slang` preserves the original position at location 0 and
UV0 at location 4. It builds camera right/up from the frozen inverse view,
uses the original model X/Y column lengths, and keeps the model translation
as the billboard center. The fragment stage preserves the canonical 26-layer
order, strict nearest-three comparisons, nonnegative weights, RGBA blending,
and low-weight fallback. It computes derivatives of the clamped UV before
selection and uses explicit gradient samples to retain texture LOD without
nonuniform implicit-derivative control flow.

`EngineOctahedralImpostorShaderContract` pins the normalized hashes of all
three desktop sources, the complete Slang closure, and the physical layout.
The cooker verifies the source graph before and after compilation. Runtime
admission re-reads the descriptor and verifies source provenance, entries,
selectors and physical ABI. Same-named replacements do not acquire the
semantic. The ordered companion changes only the final vertex position using
the existing frozen GPU source-rank gate.

Browser-target component construction selects this explicit semantic without
attempting lazy desktop shader loads. The publisher visits the component,
checks its original engine source closure, and requires both exact companions
in the selected shader manifest. It packages those descriptors and modules
through the existing shader and material catalogs. The authored component and
texture payload formats are unchanged.

The color contract is the original transparent-forward material: standard
source-alpha blending for RGB and alpha, less-equal depth testing, no depth
writes, no culling, camera uniforms, and a single-sample RGBA16F color target
with depth. The component retains `BillboardMode.None` because the canonical
shader owns its camera-facing transformation. It declares sorted-alpha late
eligibility, so Advanced uses its explicit CPU-direct late lane independently
of its native opaque strategy. The texture must be exactly 26 single-sample
linear RGBA16F layers. Missing or altered companions, incompatible texture or
output profiles, and requested shadow casting report specific failures.

## Authored GPU submission and bounds

Default and custom pipelines retain their selected CPU-direct, traditional
GPU-indirect or compute-meshlet strategy. The existing producers own indirect
arguments, visibility, selected LOD/generation, expanded indices and ordered
replay. The impostor does not add a second renderer or read GPU visibility back
to the CPU.

The generic indirect reduction and meshlet plane/sphere culler receive the
same exact camera-facing affine transformation used by raster. It retains the
model X/Y lengths and translation, uses the frozen camera right/up, and has a
zero Z basis because the source shader ignores local Z. Applying the existing
local sphere to those planes conservatively contains every billboard vertex,
including off-center meshlet spheres and nonuniform model scale. Unproven
callbacks and instance publishers retain the existing explicit bounds and
raster-instance admission rules; no different submission mode is substituted.

The component publishes a camera-independent world-radius box for CPU and
resident broad culling, with a translation-only culling offset. Its radius
is the outward-rounded sum of the scaled X/Y half-extents, also covering
normalized camera axes that are not orthogonal under an authored shear.
A local box transformed by a rotated,
nonuniform model could otherwise be narrower than the camera-facing quad.
The raster command still retains the original model matrix.

`GeneratedStaticMeshletPayloadBuilder` prepares new static billboard and HLOD
proxy geometry before resident publication. It preserves every source
triangle/corner and uses at most 64 references and 124 triangles per cluster.
It supplies outward-rounded spheres, disables cone rejection, retains the
original source fingerprint and geometry revision, and uses the existing
portable validation contract. The producer has its own honest provenance;
no meshoptimizer execution or optimization is claimed. It is bounded to
1,048,576 vertices and triangles and rejects imported or deformed meshes.
Resizing a billboard creates and validates a new payload, while the old owner
follows normal deferred destruction. No persisted mesh format changes.

## Cooking and validation

Use the existing pinned Slang 2026.8 installation. The source root must retain
the ordinary engine shader tree: `WebGPU` has sibling `Scene3D` and `Common`
folders containing the canonical GLSL files.

```sh
dotnet run --project Tools/ShaderCooker/ShaderCooker.csproj -- \
  --source-root Build/CommonAssets/Shaders/WebGPU \
  --recipe Build/CommonAssets/Shaders/WebGPU/engine-octahedral-impostor.recipe.json \
  --recipe Build/CommonAssets/Shaders/WebGPU/engine-octahedral-impostor-authored-order.recipe.json \
  --output <artifact-output>
```

Both production recipes cooked successfully with exact ABI/provenance checks.
The isolated Editor and Rendering/WebGPU builds passed without warnings or
errors. A focused cold probe passed 958 checks against the final compiled
runtime, validating both descriptors and their order-gate link,
rejected changed source provenance, checked the emitted explicit-gradient WGSL
and location-4 UVs, reconstructed original primitive order across several
managed clusters, checked conservative bounds and geometry invalidation, and
exercised unattached component construction, billboard size replacement and
completion-safe deferred destruction.

Actual `ExportAuthoredWorld` runs for Default and Advanced passed 69 checks
each. Saved world and billboard assets, all 26 source layer payloads and all
three GLSL files remained byte-identical. Cooked hydration retained shared
asset/array identity, all float pixel bytes, the generated quad dimensions,
validated meshlets and explicit Advanced sorted-alpha eligibility. These
runs exposed and fixed the pre-existing detached YAML constructor's premature
`Transform` access; the component now waits for normal node attachment to
publish its real transform.

Both exported worlds passed the actual content packager and the browser asset
loader, with 261 immutable payloads and roughly 6.5 MB SHA-256 verified per
world. Each loader pass checked both impostor variants and rejected altered
pass/profile/version selectors. The loader used local file-backed transport;
it did not create a GPU device.

This evidence does not establish rendered pixel equivalence. Browser visual
acceptance remains required for the 26-view blends, ordinary and ordered
transparent raster, each authored GPU submission mode, and Default, Advanced
and custom pipeline outputs. Asynchronous capture, float uploads/readback and
layered mip generation have separate producer/lifetime validation.
