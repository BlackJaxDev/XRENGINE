# Imported Mesh Instance Dedupe Design

Last Updated: 2026-06-12
Status: design proposal
Scope: import-time detection of duplicate static mesh instances, including
duplicates whose object transforms were baked into vertex positions, plus the
runtime rendering contract needed to render detected copies as instances.

Related docs:

- [Model import](../../../developer-guides/assets/model-import.md)
- [Native FBX import and export](../../../developer-guides/assets/native-fbx-import-export.md)
- [Model import cooked asset cache design](model-import-binary-cache-design.md)
- [Mesh submission strategies](../../../architecture/rendering/mesh-submission-strategies.md)
- [Zero-readback GPU-driven rendering plan](../rendering/zero-readback-gpu-driven-rendering-plan.md)
- [GPU meshlet zero-readback rendering design](../rendering/gpu-meshlet-zero-readback-rendering-design.md)
- [Avatar optimization and virtualized rendering design](../rendering/avatar-optimization-and-virtualized-rendering-design.md)
- [Avatar mesh/submesh geometry optimization TODO](../../todo/avatar/avatar-mesh-submesh-geometry-optimization-todo.md)

## 1. Summary

Many authored scenes contain repeated mesh instances: bolts, screws, chairs,
foliage cards, modular wall pieces, mirrored accessories, kitbash props, crowd
attachments, and avatar decorations. Some source formats preserve this as one
mesh referenced by many nodes. Other exporters bake each copy into separate
vertex buffers, leaving the engine with N distinct `XRMesh` assets even though
the geometry is transform-equivalent.

XRENGINE should detect these duplicates during model import and convert them
into a representation that shares geometry and, when safe, renders repeated
copies through instancing.

There are two separate wins:

1. **Mesh asset dedupe**: multiple imported `SubMeshLOD` records reference one
   canonical `XRMesh`, while each authored object keeps its own scene identity.
2. **Render instance aggregation**: multiple authored objects using the same
   canonical mesh/material/render state are rendered by one draw with multiple
   per-instance transforms.

The first win can land without changing renderer batching semantics. The second
win requires an explicit per-instance transform contract; increasing
`XRMeshRenderer.SubMesh.InstanceCount` alone is not enough because the current
`ModelComponent -> RenderableMesh -> RenderCommandMesh3D` path carries one world
matrix per render command.

This design treats transform recovery as a proof problem:

> Two submeshes are duplicates if there is a valid transform `M` such that the
> candidate's positions, normals, tangents, topology, and attribute streams match
> the canonical mesh after applying `M` and the corresponding normal/tangent
> transforms within deterministic tolerances.

Stable frame construction from vertices is useful for candidate discovery, but
it must not be the final proof. Symmetric and near-planar meshes can produce
ambiguous frames. Final acceptance requires transform-and-attribute validation.

## 2. Goals

- Detect exact duplicate imported mesh payloads.
- Detect transform-equivalent duplicate meshes even when translation, rotation,
  scale, negative scale, or non-uniform scale were baked into vertex positions.
- Preserve authored scene semantics: names, nodes, animation targets, selection,
  metadata, colliders, sockets, and child transforms must not disappear silently.
- Share canonical `XRMesh` data across duplicate copies when safe.
- Provide a path to true hardware/GPU-driven instancing for static rigid
  duplicates.
- Keep the importer deterministic: same source, import options, backend version,
  and dedupe settings produce the same result and the same report.
- Make every rejected candidate explainable with a structured reason.
- Keep failure visible. If an explicitly requested GPU/instancing path is not
  available, emit diagnostics instead of silently pretending the optimization
  occurred.
- Integrate with cooked model caches so duplicate analysis is not repeated on
  every warm load.
- Keep per-frame render paths allocation-free after import data has been
  published.

## 3. Non-Goals

- This does not destructively rewrite the source `.fbx`, `.gltf`, `.glb`, `.obj`,
  or Unity asset.
- This does not collapse scene nodes by default. Rendering can be aggregated
  while authoring identity remains intact.
- This does not require skinned mesh instancing in the first implementation.
- This does not merge materials, textures, shader variants, render passes, light
  map assignments, or other render state.
- This does not attempt arbitrary shape matching. Accepted duplicates must be
  explainable as the same mesh under a supported transform class.
- This does not replace mesh simplification, meshlet generation, HLOD, or avatar
  optimization. It is an input cleanup pass that can feed those systems.

## 4. Current Baseline

The current import and runtime model surfaces provide much of the needed
structure, but not the complete instance contract.

### 4.1 Import

- `ModelImporter` owns the Assimp compatibility path.
- `NativeFbxSceneImporter` and `NativeGltfSceneImporter` own native format paths.
- `ModelImportOptions` already carries cross-backend settings such as
  `SplitSubmeshesIntoSeparateModelComponents`, `GenerateSceneNodesPerSubmesh`,
  `SeparateMeshIslands`, `ProcessMeshesAsynchronously`, and
  `BatchSubmeshAddsDuringAsyncImport`.
- Imported geometry becomes `Model` -> `SubMesh` -> `SubMeshLOD` -> `XRMesh`.
- `ModelImportMeshIslandSplitter` can split disconnected islands inside a
  material/grouped submesh. Duplicate detection should run after final mesh
  island decisions so each dedupe candidate has stable topology and material
  ownership.

### 4.2 Runtime rendering

- `ModelComponent` creates one `RenderableMesh` per source `SubMesh`.
- `RenderableMesh` creates renderers from `SubMeshLOD`, tracks culling and LOD,
  and emits a `RenderCommandMesh3D`.
- `RenderCommandMesh3D` carries one `WorldMatrix` and one `Instances` count.
- `XRMeshRenderer.SubMesh.InstanceCount` already exists and feeds indirect draw
  command generation inside an `XRMeshRenderer`, but it does not by itself
  describe N different instance transforms.
- `GPUScene` stores command metadata and a transform buffer, but current command
  conversion allocates one transform id per render command. Existing GPU-driven
  shader code resolves one model matrix from draw metadata, not a per-instance
  range.

Therefore:

- Sharing `XRMesh` assets is immediately compatible with the current model
  asset graph.
- Rendering N duplicates as one draw requires new per-instance transform data
  and shader/command semantics.

## 5. Terminology

| Term | Meaning |
| --- | --- |
| Source mesh | A mesh or primitive as represented by the imported source document. |
| Imported submesh | A final engine `SubMesh` produced by import settings, material grouping, chunking, and optional island splitting. |
| Canonical mesh | The one `XRMesh` kept as the shared geometry payload for a duplicate group. |
| Candidate mesh | A mesh being tested against a canonical mesh. |
| Exact duplicate | Same geometry streams and indices in the same local space. |
| Transform-equivalent duplicate | Same geometry after applying a supported transform from canonical mesh space to candidate mesh space. |
| Baked transform | Translation, rotation, scale, or mirror that was applied directly to vertex positions before import instead of being represented as a scene node transform. |
| Instance record | The preserved authored object identity plus the transform needed to draw the canonical mesh at that object's location. |
| Instance group | A runtime render object that draws one canonical mesh/material/render-state tuple with N instance transforms. |
| Geometry-to-node transform | The transform from canonical mesh local space to the owning scene node or component space for one imported copy. |

## 6. Import Modes

Expose the optimization as an explicit import option, disabled until the
runtime pieces have enough validation.

```csharp
public enum MeshInstanceDedupeMode
{
    Disabled = 0,
    ReportOnly = 1,
    ShareExactMeshes = 2,
    ShareTransformEquivalentMeshes = 3,
    RenderInstancedStaticMeshes = 4,
}

public enum MeshInstanceDedupeTransformPolicy
{
    Rigid = 0,                 // translation + rotation
    UniformScale = 1,          // translation + rotation + uniform scale
    NonUniformScale = 2,       // translation + rotation + non-uniform scale
    Mirrors = 3,               // negative determinant allowed with winding/tangent handling
    AffineNoShear = 4,         // affine accepted only when it decomposes to TRS within tolerance
}
```

Recommended v1 defaults:

- `Mode = Disabled` globally until validation lands.
- Editor import UI can offer `ReportOnly`.
- Batch optimization tools can opt into `ShareExactMeshes` and later
  `ShareTransformEquivalentMeshes`.
- `RenderInstancedStaticMeshes` should remain opt-in until selection, picking,
  culling, CPU fallback, and GPU-driven paths all preserve scene identity.

Suggested option fields:

```csharp
public sealed class MeshInstanceDedupeOptions
{
    public MeshInstanceDedupeMode Mode { get; init; }
    public MeshInstanceDedupeTransformPolicy TransformPolicy { get; init; }
    public float PositionToleranceRelativeToBounds { get; init; }
    public float PositionToleranceAbsolute { get; init; }
    public float NormalAngleToleranceDegrees { get; init; }
    public float UvTolerance { get; init; }
    public float ColorTolerance { get; init; }
    public int MinVertexCount { get; init; }
    public int MinInstanceCountForRenderInstancing { get; init; }
    public bool AllowSkinnedMeshes { get; init; }
    public bool AllowBlendshapeMeshes { get; init; }
    public bool PreserveSceneNodes { get; init; }
    public bool WriteDiagnosticsReport { get; init; }
}
```

## 7. Safety Classification

Each candidate group receives a classification before any rewrite:

| Class | Can share `XRMesh`? | Can render-instance? | Notes |
| --- | --- | --- | --- |
| Static rigid, same material state | Yes | Yes | Primary v1 target. |
| Static non-uniform scale | Yes | Yes if normal matrix/culling are correct | Requires inverse-transpose normal handling and conservative bounds. |
| Mirrored static | Yes | Conditional | Requires winding/cull/tangent handedness handling. |
| Same geometry, different material | Yes | No in same draw | Can share mesh but not batch unless material table path supports it deliberately. |
| Animated node transform | Yes | Conditional | Instance transform must update when node animates. |
| Skinned same skeleton/palette | Maybe | Later | Palette, root bone, culling, and blendshape semantics are harder. |
| Skinned different skeleton/palette | Usually no | No | Geometry might match but deformation inputs differ. |
| Different UV/color/bone/blendshape data | No | No | Positions alone are insufficient. |
| Mesh with per-object lightmap UV/state | Maybe | Conditional | Must preserve per-instance lightmap metadata. |
| Mesh with unique collider/editing semantics | Yes | No by default | Rendering may aggregate, physics/editor identity should remain separate. |

## 8. Detection Pipeline

Run duplicate detection after final submesh material grouping and optional mesh
island splitting, but before externalized sub-assets and cooked caches are
published.

High-level stages:

1. Collect dedupe candidates.
2. Use source-document references for free exact groups when available.
3. Build cheap geometry signatures for broad grouping.
4. Build transform-invariant signatures for baked-transform candidates.
5. Recover candidate transforms.
6. Validate all streams and topology.
7. Emit canonical meshes and per-copy instance records.
8. Publish diagnostics and cache metadata.

### 8.1 Candidate collection

Collect only submeshes whose data can be safely compared:

- triangle topology only for v1;
- finite positions and normals;
- non-empty index data or generated stable index data;
- supported vertex attribute layouts;
- no unsupported topology such as patches or lines unless a later phase adds
  explicit handling;
- same source material slot or compatible render-state class for render
  instancing;
- same LOD tier when comparing LODs.

Early reject by:

- vertex count;
- index count;
- primitive topology;
- material render pass and render-state class;
- presence/absence of normals, tangents, colors, UV sets, skinning, blendshapes;
- axis/unit conversion policy;
- source importer backend if backend-specific data semantics differ.

### 8.2 Source-reference groups

Some formats preserve mesh instancing natively:

- glTF has nodes that reference the same mesh primitive.
- FBX can connect multiple model nodes to the same geometry object.
- Unity scenes can reference the same serialized mesh asset.

When the importer sees one source geometry object referenced by multiple nodes,
record this as a high-confidence duplicate group. The importer should still
validate material/render-state compatibility before choosing render instancing,
but it should not need expensive baked-transform matching to know the geometry
payload can be shared.

### 8.3 Exact geometry signature

For non-baked exact duplicates, compute a deterministic signature over:

- topology type;
- vertex count and index count;
- index size and index values after canonical widening to `uint`;
- vertex stream layout;
- position bytes or quantized positions;
- normals;
- tangents including handedness;
- UV sets;
- vertex colors;
- bone indices and weights;
- blendshape names and deltas;
- mesh bounds and channel metadata.

Use this only after normalizing importer noise that is known to be semantically
irrelevant. Do not normalize away meaningful differences such as UVs or color
sets.

### 8.4 Transform-invariant broad signature

To find duplicates with baked transforms, compute signatures that survive
translation, rotation, and supported scales.

Useful broad filters:

- vertex count and index count;
- sorted triangle edge-length ratios;
- sorted triangle area ratios;
- normalized local neighborhood signatures;
- valence histograms;
- pairwise distance samples from deterministic anchor vertices;
- UV topology signature;
- material slot and attribute layout;
- blendshape topology signature when enabled.

These signatures should only reduce the candidate set. They are not proof.

### 8.5 Stable frame construction

A stable frame can be computed to produce a rough canonical space:

1. Compute a centroid from all positions.
2. Compute a covariance matrix over centered positions.
3. Use principal axes as candidate basis vectors.
4. Resolve axis signs using deterministic secondary data: normal distribution,
   tangent distribution, UV gradient orientation, or farthest anchor vertices.
5. Quantize the resulting normalized coordinates for broad grouping.

Limitations:

- Cubes, cylinders, wheels, mirrored shapes, flat panels, and repeated modular
  pieces can have ambiguous or unstable axes.
- Slight authoring edits can swap PCA axes.
- Near-planar meshes have an ill-conditioned third axis.
- Mirrored meshes may produce a frame with the wrong handedness.

Because of this, stable frames are an acceleration structure. A match is
accepted only after transform recovery and full validation.

## 9. Transform Recovery

Transform recovery answers:

> Can candidate mesh local space be represented as canonical mesh local space
> transformed by a supported matrix?

The recovered transform is then stored as the per-copy geometry-to-node or
instance transform.

### 9.1 Same vertex order

If vertex and index order match, solve directly from corresponding position
pairs.

For rigid or uniform-scale transforms:

- Use a least-squares rigid alignment such as Kabsch/Umeyama over all vertices
  or a deterministic representative sample.
- Recover translation, rotation, and optional uniform scale.
- Validate against all vertices.

For non-uniform scale:

- Solve a least-squares affine transform from canonical positions to candidate
  positions.
- Decompose the 3x3 linear part using polar/SVD decomposition.
- Accept only if shear is below tolerance and the transform decomposes to the
  configured TRS policy.
- Validate normals using inverse-transpose of the accepted linear part.

### 9.2 Different vertex order

When vertex order differs, build a deterministic correspondence:

1. Hash vertices by transform-invariant local features: incident edge-length
   ratios, incident triangle angle sets, UV coordinates, normal neighborhoods,
   color, and skin/blendshape metadata.
2. Build triangle signatures from the three vertex feature hashes and edge
   ratios.
3. Use rare feature buckets as anchors.
4. Solve a provisional transform from anchor correspondences.
5. Transform canonical vertices and match to candidate vertices through
   quantized spatial buckets.
6. Validate full topology and attributes through the recovered permutation.

If ambiguous buckets remain, reject unless a deterministic tie-break produces a
single proof that passes full validation. Do not guess on symmetric meshes.

### 9.3 Mirroring and negative scale

Negative determinant transforms are allowed only when the policy explicitly
permits mirrors.

Validation must account for:

- triangle winding reversal;
- face culling behavior;
- normal transform using inverse transpose;
- tangent handedness flip;
- bitangent reconstruction;
- two-sided materials;
- shadow pass consistency;
- meshlet winding and cone data when meshlets are present.

If the renderer cannot represent a mirrored instance without changing culling or
tangent semantics, the importer may still share the canonical `XRMesh` but must
not render-instance the group.

### 9.4 Baked transform extraction

For baked duplicates, keep one canonical mesh in canonical local space and store
one transform per copy.

For a source copy with existing node transform `N` and recovered baked transform
`B`:

```text
world = N * B * canonicalVertex
```

The importer should not blindly rewrite `N`, because that changes scene-node
semantics for children, colliders, animation targets, sockets, and editor tools.
Instead, publish `B` as mesh-local render data:

```text
SubMeshInstance.MeshToComponentTransform = B
```

or, for a render instance group:

```text
InstanceRecord.WorldTransform = NodeWorldTransform * B
```

This preserves authored scene transforms while allowing shared geometry.

## 10. Full Validation

A candidate is accepted only after full validation.

### 10.1 Position validation

For every matched vertex:

```text
candidate.position ~= M * canonical.position
```

Use both absolute and relative tolerances:

```text
positionEpsilon = max(
    options.PositionToleranceAbsolute,
    options.PositionToleranceRelativeToBounds * max(canonicalBoundsDiagonal, candidateBoundsDiagonal))
```

Reject if:

- any transformed vertex exceeds tolerance;
- transform contains unsupported shear;
- transform has near-zero scale;
- transformed bounds are invalid;
- candidate contains NaN or infinity.

### 10.2 Normal and tangent validation

Normals and tangents must be transformed by the normal matrix:

```text
normalMatrix = transpose(inverse(mat3(M)))
```

Validate:

- normal direction within angular tolerance;
- tangent direction within angular tolerance;
- tangent handedness adjusted correctly for mirrored transforms;
- missing tangents match missing tangents.

For non-uniform scale, this is mandatory. Position-only validation is not
enough.

### 10.3 UV, color, and custom attribute validation

UVs, colors, and custom vertex attributes are not transformed by object space
TRS. They must match through the same vertex correspondence.

Reject on:

- different UV set count;
- different UV values beyond tolerance;
- color mismatch beyond tolerance;
- different custom attribute layout;
- different normalized/integer attribute semantics.

### 10.4 Index and topology validation

Topology must match after applying the vertex permutation and optional mirror
winding rule.

Validate:

- same primitive topology;
- same triangle count;
- same triangle adjacency where required;
- same index order for exact duplicates, or same triangle set for reordered
  duplicates;
- mirrored winding only when allowed.

### 10.5 Skinning validation

V1 should reject skinned transform-equivalent instancing unless an explicit
`AllowSkinnedMeshes` option is set.

When enabled later, skinned validation must include:

- identical utilized-bone topology or a deterministic bone remap;
- identical bind-pose semantics after transform recovery;
- matching bone indices/weights through vertex correspondence;
- matching root bone behavior;
- compatible skin palette source;
- compatible compute/direct skinning path;
- compatible skinned culling bounds.

Even if a skinned mesh shares `XRMesh`, render instancing is unsafe unless the
instance group can supply separate palettes or all instances share the same
pose.

### 10.6 Blendshape validation

For blendshape meshes:

- names and channel order must match or be remapped deterministically;
- delta positions must transform by `M`;
- delta normals/tangents must transform by the normal matrix;
- weights remain per renderer/component, not per shared mesh.

V1 can share geometry for exact same blendshape data but should not
render-instance independently animated blendshape weights.

## 11. Import Output Contract

Duplicate detection produces three kinds of output.

### 11.1 Canonical mesh table

Each accepted group has:

- canonical `XRMesh`;
- source mesh ids included in the group;
- transform policy accepted;
- transform for every copy;
- validation tolerances used;
- rejection-free proof summary;
- bounds for canonical and aggregate forms.

### 11.2 Scene-preserving instance records

Each authored copy keeps an identity record:

```csharp
public sealed class ImportedMeshInstanceRecord
{
    public Guid SourceNodeId { get; init; }
    public SubMesh SourceSubMesh { get; init; }
    public XRMesh CanonicalMesh { get; init; }
    public Matrix4x4 MeshToComponentTransform { get; init; }
    public Matrix4x4 PreviousMeshToComponentTransform { get; init; }
    public int MaterialSlot { get; init; }
    public uint StableInstanceIndex { get; init; }
}
```

The stable instance index is used for editor picking, diagnostics, reports, and
deterministic cache output. It is not a replacement for scene node identity.

### 11.3 Dedupe diagnostics report

When enabled, write a report beside the generated import output or into the
cooked cache diagnostics chunk.

Report fields:

- source path;
- importer backend and version;
- import options hash;
- dedupe options hash;
- candidate count;
- exact duplicate groups;
- transform-equivalent groups;
- canonical mesh chosen per group;
- instance count per group;
- bytes saved by sharing mesh payloads;
- estimated draw reduction if render instancing is enabled;
- rejected candidate pairs and reason codes;
- maximum observed position/normal/UV error per accepted group.

## 12. Canonical Mesh Selection

Canonical mesh choice must be deterministic.

Preferred order:

1. Source geometry object that is already shared by the source format.
2. Mesh with identity or closest-to-identity recovered baked transform.
3. Mesh with the smallest transform error.
4. Mesh with the earliest stable source document order.
5. Mesh with the lexicographically smallest source node path.

Do not choose based on memory address, dictionary iteration order, thread timing,
or generated GUID order.

## 13. Runtime Data Model

### 13.1 Phase A: shared mesh plus mesh-local transform

The lowest-risk runtime addition is a mesh-local transform on the source
`SubMesh` or `SubMeshLOD` path:

```csharp
public Matrix4x4 MeshToComponentTransform { get; set; } = Matrix4x4.Identity;
```

`RenderableMesh` composes:

```text
RenderWorldMatrix = ComponentWorldMatrix * MeshToComponentTransform
```

for static meshes. Culling uses:

```text
WorldBounds = canonicalMesh.Bounds transformed by RenderWorldMatrix
```

This supports sharing `XRMesh` for baked duplicates while preserving scene node
transforms. It does not reduce draw count by itself, but it reduces mesh memory,
meshlet generation, cooked payload size, upload work, and atlas duplication.

### 13.2 Phase B: scene-preserving instance groups

Add a render aggregation layer that can draw one canonical mesh/material/render
state tuple with N instance transforms while keeping N authored scene nodes.

Possible shape:

```csharp
public sealed class RenderableMeshInstanceGroup : XRBase
{
    public XRMesh CanonicalMesh { get; }
    public XRMaterial Material { get; }
    public IReadOnlyList<ImportedMeshInstanceRecord> Instances { get; }
    public XRDataBuffer? InstanceTransformBuffer { get; }
    public XRDataBuffer? PreviousInstanceTransformBuffer { get; }
}
```

The group is a rendering acceleration structure, not the authoring source of
truth. Removing the group must not remove scene nodes. Disabling optimization
should fall back to ordinary `RenderableMesh` instances with identical visual
output.

### 13.3 GPUScene command extension

To support true instancing, `GPUIndirectRenderCommand` and draw metadata need an
instance transform range instead of a single transform id:

```csharp
public struct GPUIndirectRenderCommand
{
    public uint InstanceCount;
    public uint InstanceTransformBase;
    public uint InstanceSourceBase;
    public uint BoundsBase;
}
```

Shader model:

```glsl
uint instanceIndex = InstanceTransformBase + uint(gl_InstanceID);
mat4 modelMatrix = LoadWorldMatrixFromTransforms(instanceIndex);
```

The existing transform buffer and `InstanceSourceIndexBuffer` concepts can be
used, but the current shader path that resolves one `TransformID` from one
draw id must be extended so `gl_InstanceID` indexes the instance range.

### 13.4 CPU direct fallback

CPU direct rendering must remain correct. Options:

- loop over instance transforms and issue one draw per instance in fallback
  mode, with a visible diagnostic that hardware instancing was unavailable;
- bind an instance-transform buffer/vertex attribute/SSBO for direct rendering;
- disable render aggregation when the active mesh submission strategy cannot
  support it.

Do not silently collapse to one instance at the first transform.

## 14. Culling and Bounds

There are two culling choices.

### 14.1 Aggregate group bounds

One draw command has one aggregate bounding volume containing all instances.

Pros:

- simple;
- compatible with one command/one indirect draw;
- no per-instance culling data required.

Cons:

- overdraws off-screen instances if any part of the group is visible;
- poor for large repeated objects spread across a scene.

This is acceptable for small local clusters and early validation.

### 14.2 Per-instance culling

Each instance has bounds and transform data. GPU culling compacts visible
instances into draw instance ranges or indirect commands.

Pros:

- correct visibility at scale;
- aligns with zero-readback GPU-driven rendering;
- useful for foliage, props, and large modular scenes.

Cons:

- requires instance compaction;
- requires stable source-index mapping for selection and picking;
- may generate multiple indirect draws per canonical mesh/material if visible
  instances are not contiguous.

Long term, this is the target for production rendering.

## 15. Picking, Selection, and Editor Identity

Rendering aggregation must preserve object identity.

Requirements:

- each instance maps back to a source `SceneNode` and source `SubMesh`;
- editor hover/selection can identify the instance, not just the canonical mesh;
- outline/highlight can target one instance or the whole group;
- inspector operations edit the source node/component, not the instance group;
- deletion or deactivation of one node removes or hides one instance;
- transform edits update that instance transform only;
- prefab overrides still record against the authored node.

For GPU picking or ID buffers, reserve an instance source id:

```text
pickedObject = InstanceSourceIndexBuffer[InstanceTransformBase + gl_InstanceID]
```

CPU fallback picking can test the canonical mesh against the selected instance's
world transform.

## 16. Materials and Render State

Render instancing is allowed only when the draw can use one compatible render
state.

Must match:

- material asset or generated material table row policy;
- render pass;
- transparency mode;
- double-sided/cull mode;
- shadow/receive-shadow flags;
- shader variant;
- vertex layout;
- skin/blendshape path;
- lightmap/probe binding policy;
- render options that affect pipeline state.

If materials differ, share the canonical `XRMesh` but do not combine into one
draw unless a later material-table path explicitly supports per-instance
material ids for the pass.

Transparent meshes need special care. Per-instance sorting may conflict with one
aggregate draw. Initial render instancing should skip transparent groups unless
the pass supports approximate order-independent transparency or per-instance
sort keys.

## 17. LOD and Meshlets

For LODs, duplicate detection should compare corresponding LOD tiers:

- LOD0 canonical with LOD0 candidate;
- LOD1 canonical with LOD1 candidate;
- no cross-tier matching.

If a group has multiple LODs, every tier must either:

- share an equivalent canonical mesh and compatible instance transform; or
- opt out of render instancing and only share the tiers that validate.

Meshlet generation should happen after mesh asset dedupe, so one canonical mesh
produces one meshlet payload. For mirrored instances, meshlet cone/winding data
must be validated before render instancing is enabled.

## 18. Cooked Cache Integration

Duplicate analysis can be expensive, especially for unordered matching. The
cooked model cache should store:

- dedupe options hash;
- importer backend version;
- canonical mesh groups;
- per-instance transforms;
- report summary;
- rejection reason counts;
- schema version for dedupe records.

Any import option that changes candidate collection, channel validation,
tolerances, transform policy, or output mode must be part of cache freshness.

If cache data is stale or incompatible, fall back to source import and rerun
dedupe. Do not partially trust old instance transforms.

## 19. Diagnostics

Add structured rejection reasons:

```csharp
public enum MeshInstanceDedupeRejectReason
{
    None = 0,
    VertexCountMismatch,
    IndexCountMismatch,
    TopologyMismatch,
    AttributeLayoutMismatch,
    MaterialStateMismatch,
    PositionErrorExceeded,
    NormalErrorExceeded,
    TangentErrorExceeded,
    UvMismatch,
    ColorMismatch,
    SkinningMismatch,
    BlendshapeMismatch,
    TransformPolicyRejected,
    ShearExceeded,
    MirrorNotAllowed,
    AmbiguousCorrespondence,
    UnsupportedTopology,
    TransparentSortRisk,
    RendererPathUnsupported,
}
```

Importer logs should include:

- one summary line per source model;
- accepted group count and instance count;
- mesh memory saved;
- draw-count reduction opportunity;
- top rejection reasons;
- whether the output was report-only, mesh-sharing, or render-instanced.

Detailed pairwise diagnostics belong in the report, not normal logs.

## 20. Performance Strategy

The detector must avoid all-pairs full validation.

Pipeline cost controls:

- broad signatures group likely candidates;
- compare only inside groups with matching counts/layouts/material classes;
- skip tiny meshes below `MinVertexCount` unless source references already prove
  duplication;
- sample deterministic anchors before full transform solve;
- cap expensive unordered matching per source model unless explicitly requested;
- parallelize candidate groups, but keep canonical selection deterministic;
- use pooled arrays and reusable hash builders in import jobs;
- avoid LINQ in hot import inner loops if profiling shows pressure.

Expected cost:

- exact duplicate hashing: linear in vertex/index bytes;
- same-order transform validation: linear in vertex count;
- unordered matching: higher cost, reserved for candidates that pass strong
  broad filters.

## 21. Rollout Plan

### Phase 1: report-only detector

- Add `MeshInstanceDedupeOptions`.
- Collect candidates after final submesh publication decisions.
- Implement exact duplicate signatures.
- Implement transform-equivalent broad signatures.
- Implement same-order transform recovery.
- Write diagnostics reports.
- No asset rewrite yet.

### Phase 2: shared canonical `XRMesh`

- Add mesh-local `MeshToComponentTransform` or equivalent submesh render
  transform.
- Rewrite accepted static duplicates to reference canonical `XRMesh`.
- Preserve authored nodes and `ModelComponent` layout.
- Update culling to transform canonical bounds by mesh-local transform.
- Store dedupe data in cooked model cache.

### Phase 3: robust baked-transform matching

- Add unordered vertex correspondence for exporters that reorder vertices.
- Add mirror support behind explicit policy.
- Add non-uniform scale validation.
- Add blendshape exact-data sharing where safe.
- Keep skinned meshes report-only unless full skin validation is implemented.

### Phase 4: render instance groups

- Add scene-preserving `RenderableMeshInstanceGroup`.
- Add CPU direct fallback behavior.
- Add editor picking and highlight source mapping.
- Add aggregate bounds culling.
- Require material/render-state compatibility.

### Phase 5: GPU-driven per-instance path

- Extend `GPUScene` command metadata with instance transform ranges.
- Extend shaders to resolve `InstanceTransformBase + gl_InstanceID`.
- Add instance source-index buffer consumption for picking/diagnostics.
- Add per-instance culling or visible-instance compaction.
- Validate zero-readback GPU-driven behavior.

### Phase 6: advanced content

- Skinned same-pose or same-palette groups.
- Blendshape instance data.
- Per-instance material table ids where supported.
- Lightmap/probe metadata.
- Meshlet-specific mirrored instance validation.

## 22. Tests

### 22.1 Unit tests

Add deterministic tests under `XREngine.UnitTests/` for:

- exact duplicate static mesh accepted;
- translated baked duplicate accepted;
- rotated baked duplicate accepted;
- uniform-scale baked duplicate accepted;
- non-uniform-scale duplicate accepted only under the right policy;
- mirrored duplicate rejected unless mirrors are allowed;
- shear rejected;
- UV mismatch rejected;
- normal mismatch rejected;
- tangent handedness validated under mirror;
- vertex order permutation accepted only when correspondence is proven;
- symmetric cube ambiguity does not produce nondeterministic output;
- different material render state shares mesh but does not render-instance;
- transparent material skipped for render instancing in v1;
- skinned meshes rejected by default;
- blendshape mismatch rejected;
- canonical mesh selection deterministic across runs.

### 22.2 Import tests

- Native FBX repeated geometry reference shares mesh.
- Native glTF repeated mesh node shares mesh.
- Assimp path reports or shares equivalent groups consistently.
- `SeparateMeshIslands` output is deduped after island splitting.
- `GenerateSceneNodesPerSubmesh` still preserves source node identity.
- Cooked cache reload preserves canonical groups and instance transforms.

### 22.3 Runtime tests

- `ModelComponent` draws shared canonical mesh at each original location.
- Bounds/culling match pre-dedupe output.
- CPU direct fallback renders all instances.
- GPU indirect path renders all instances when enabled.
- Editor picking maps instance back to source node.
- Transform edit of one source node updates one instance.
- Deactivation/removal of one source node removes one instance.

### 22.4 Visual validation

Use the unit-testing world and screenshot comparisons for:

- exact duplicates;
- rotated/scaled baked duplicates;
- mirrored meshes;
- dense modular prop scenes;
- transparent opt-out scene;
- large scattered repeated static props.

Capture before/after:

- visible output;
- draw count;
- uploaded mesh bytes;
- generated meshlet bytes;
- culling behavior;
- per-frame allocations.

## 23. Open Questions

- Should mesh-local baked transforms live on `SubMesh`, `SubMeshLOD`, or a new
  imported-instance asset so authored `SubMesh` remains purely geometry/material
  data?
- Should render instance groups be represented as a new component type or as a
  hidden acceleration structure built by `VisualScene3D`/`GPUScene`?
- How should per-instance lightmap and reflection-probe metadata be stored?
- Should mirrored instances use negative scale directly or generate a mirrored
  canonical mesh variant when cull/tangent state cannot be represented cheaply?
- How aggressive should unordered matching be for huge imports by default?
- Should editor UI expose this as a model import setting, an avatar optimizer
  operation, or both?

## 24. Design Invariants

- Mesh sharing must not remove authored scene identity.
- Render instancing must not be represented as one instance with the first
  transform. Missing instance-transform support is a visible fallback.
- Stable frames are candidate filters, not proof.
- Positions, normals, tangents, topology, UVs, colors, skinning, and blendshapes
  must validate through one consistent correspondence.
- Import output and reports must be deterministic.
- Cooked cache freshness must include dedupe options and detector schema.
- Per-frame rendering must not allocate because of imported instance groups.
