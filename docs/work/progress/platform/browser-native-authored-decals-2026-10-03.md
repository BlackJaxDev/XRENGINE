# Browser native authored decals

## Implemented source contract

The ordinary `DeferredDecalComponent` default material now has an explicit browser
native producer. Component activation, deactivation, world reassignment and
destruction reconcile world registration. The world swap captures current and
previous inverse box transforms, extents, source command identity and the exact
sampled image generation. Browser-only capture leaves desktop shader source and
binding behavior unchanged.

The component owns its generated box, renderer and their constructor allocations
through an independent object ownership scope. Deactivation stops world users
before retiring that scope; terminal teardown disposes both render-info containers
before releasing the box. Borrowed materials/images remain outside this ownership.
Activation explicitly restores the matrix and culling volume even when hydration
assigned half-extents before node attachment and the attached transform is identity.

The supported default is the canonical `Scene3D/DeferredDecal.fs` behavior:

- Inclusive local box boundaries and local XZ texture projection
- Texture-alpha interpolation of receiver RGB
- Unchanged receiver alpha, normal, RMSI and separately retained material emission
- No edge or projection-depth fade: the desktop shader's computed intensity is unused
- The original deferred GBuffer receiver pass, excluding forward receivers

The exact desktop source path, text, snippets, material shape and render state are
checked during detached projection. Reflection adds the component-owned
`BoxWorldMatrix` and `BoxHalfScale` placeholders to the desktop material; the
source-free carrier strips only that exact typed pair. The component supplies
their actual values through the frozen native box record.

Admission also checks the actual component draw: its owned, unchanged box mesh
and renderer, material identity, transform, single instance and callbacks.
Material/raster overrides, replaced geometry, binding publishers and custom
callbacks receive a specific unsupported-draw diagnostic. The ordinary
render-info forwarding hooks and the exact owning scene's swap callback remain
valid after real scene registration.

`PublishedDeferredDecalMaterial` carries the slot-four image once and reuses the
existing 19-byte sampled-image settings record. Its decoder restores settings on
its newly decoded image. The original authored material, YAML, GLSL, general
texture codec and existing lit material carrier versions are unchanged.

## Graph and publication ownership

`VPRC_AdvancedRenderStage.EnableAuthoredDecals` defaults to false and declares the
`native-authored-decals` operation only for native opaque shading. The built-in
Advanced graph enables it explicitly. Graph demand requests the world producer;
omitted or disabled operations acquire no authored decal image references.
Selecting both native and raster application of the same deferred decal pass is
rejected. Classification and shading must consume the same selected operation.
Collection freezes that operation in the backend frame package and its dependency
signature. A shading command changed after collection rejects until the graph is
collected again. The optimized Advanced declaration reads the current executable
command chain through shared declarations, including appended and nested raster
commands and custom declarations. Mutable declaration contents are observed on
each demand query without allocation; stage and declaration replacement advance
graph generation.
Child-container replacement also advances graph generation. Switch case maps keep
their existing public dictionary format; exact retained key/container observations
detect in-place additions, removals and same-count replacements before cached
publication demand is reused. Both native decal and generic mesh publication
queries observe these changes independently. Body-bearing control commands expose
their child declarations without changing execution or branch scheduling.

The private capture lease belongs exclusively to `GPUScene`, survives retryable
publication rejection, and is released on replacement or teardown. Its pooled
storage is generation-checked. The public world-snapshot accessor strips the
producer lease before any output copies the wrapper. Outputs consume the existing
completion-pinned canonical snapshots, which retain their own image sources and
metadata. Warm capture uses reusable buffers rather than new transform arrays.

The publisher plans row and image/sampler ownership with the existing aggregate
resource transaction. Transform changes update rows, image changes exchange
references, and removed sources disable and retire their rows and image leases.
The tagged authored row stores its source command's monotonically allocated
32-bit key in otherwise-unused material words with generation zero. It is never
resolved as a PBR material handle. Canonical decal identity remains the table's
independent index/generation handle.

Each frozen view package's sorted DeferredDecals membership determines selected
rows and their order. Enabled source identities are copied at collection into
reusable package storage, and their order-sensitive signature guards cohort reuse,
classification and shading. Later author edits cannot change that frozen list.
This preserves camera layer/frustum filtering and pass
sorting. The selected dense indices are appended to the existing cohort binding
map; two reserved uniform words carry their offset/count. Descriptor bindings and
the 160-byte uniform size remain unchanged. Cohort reuse includes decal selection
and command membership, and each selected image enters the exact texture closure.

The eight native/export/depth/MSAA recipes require
`XR_ADV_AUTHORED_DECAL_SCHEMA_VERSION=1`. Older programs with the same buffer sizes
are rejected for recook. Generic material decal rows keep their separate XY
contract and evaluate admitted engine-surface independent base/normal/metallic/
roughness roles and factors rather than reinterpreting them as packed RG data.
Generic decals modify receiver normals only when their admitted material has a
normal role and the decal requests normal modification.

## Validation and remaining acceptance

- Narrow Rendering, WebGPU and Editor builds passed with zero warnings/errors at
  the coordinated source checkpoints
- All eight native shader recipes passed the production offline cooker
- Real Editor default factory, detached projection, cooked graph serialization
  and hydration passed 28 checks, including alias identity, seven omitted image
  settings, version and sampler-name rejection, unchanged authored YAML/GLSL/raw
  image bytes, and custom/OIT rejection
- The material contract allocated zero bytes over 10,000 warmed reads
- The production component/RuntimeWorldRenderer/GPUScene witness passed 274 checks: component
  activation/removal, lease expiration, immutable captured values, canonical image
  ownership without synthetic materials, pinned prior outputs, injected preflight
  rejection/retry, image replacement, active teardown, failed-first-frame teardown
  and final row/image/sampler retirement
- Seven component lifetimes and 21 reactivations on one retained node/transform
  produced no object-cache or render-object-registry growth; identity-transform
  activation restored bounds, and borrowed material/images survived
- The same witness covers live graph enable/disable, command removal/replacement,
  duplicate raster declaration rejection before publication demand, in-place
  declaration and stage mutations, frozen reversed-order selection, draw overrides,
  geometry mutation and custom callback rejection/restoration after real scene
  registration. Appended and nested raster commands and mutable non-stage custom
  declarations are checked through the actual optimized Advanced family path
- Child-body and branch replacement, same-count Switch key/value changes, removal
  with remaining aliases, retained owner behavior and internal root-family rebind
  passed. The generic indirect publication cache detects case insertion/removal
  without first querying decal demand
- Warm registry capture allocated zero bytes over 1,000 static and 1,000 moving
  transform captures; 1,000 warmed mutable declaration and Switch topology queries
  also allocated zero
- A real `ExportAuthoredWorld` fixture with an ordinary decal, authored deferred
  receiver and Advanced camera passed ten export/hydration checks and produced a
  29,137-byte cooked startup world; source world, shader and image bytes remained
  unchanged

Evidence is under the existing ignored validation run's
`scratch/authored-decals/`, `scratch/deferred-decal-projection-probe/` and matching
`logs/authored-decals-*` / `logs/deferred-decal-*` outputs.

Custom decal shaders/component callbacks and weighted forward OIT decals require
their own exact lowering and are rejected with decal-specific diagnostics. An
explicit raster DeferredDecals graph likewise requires its own GBuffer route;
the native producer does not replace that authored pass. These source/cook checks
do not establish browser GPU rendering, overlap/mip edge quality, multisample
appearance or output-loss recovery acceptance.

The real export fixture exposed a separate existing source-format boundary:
inline image YAML uses the existing CookedBinary texture envelope, which does not
persist the seven additional import/sampler settings.
The browser carrier preserves the actual YAML-loaded source values; it cannot
reconstruct transient values absent from that file. Direct projection/hydration
does preserve all seven settings when present. No authored YAML or raw texture
format migration is included.

Existing cross-pipeline child attachment remains a separate ownership boundary:
the shared attachment helpers reparent child containers, and command insertion or
replacement invokes attachment callbacks after editing its collection. Making
cross-owner insertion reject atomically requires a coherent command-container
transaction change. This invalidation work preserves those existing behaviors
and does not validate cross-pipeline child sharing. Removed child aliases retain
the existing ownership behavior; replacing one edge does not detach another.
