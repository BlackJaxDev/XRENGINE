# XRE Virtual Geometry Design

Last Updated: 2026-05-01
Status: design
Scope: engine-native design for paged clustered geometry, runtime detail selection, GPU-driven visibility, streaming residency, and renderer integration.

Related docs:

- [GPU-driven rendering zero-readback plan](zero-readback-gpu-driven-rendering-plan.md)
- [GPU-based rendering TODO](../todo/gpu-rendering.md)
- [Bindless deferred texturing plan](bindless-deferred-texturing-plan.md)
- [Texture management runtime design](texture-management-runtime-design.md)
- [Default render pipeline notes](../../architecture/rendering/default-render-pipeline-notes.md)
- [OpenGL renderer](../../architecture/rendering/opengl-renderer.md)
- [Vulkan renderer](../../architecture/rendering/vulkan-renderer.md)

## 1. Summary

XRE Virtual Geometry, abbreviated as `XVG`, is the proposed XRENGINE subsystem for rendering extremely high-detail static meshes without depending on artist-authored whole-mesh LOD chains as the only scalability mechanism.

The core idea is to treat geometry as a paged render resource:

1. Offline tools split source meshes into small bounded clusters.
2. The compiler builds a multi-resolution hierarchy over those clusters.
3. Root-level coverage remains resident so every eligible mesh has a guaranteed coarse representation.
4. Runtime selection chooses a valid set of clusters for the current view.
5. Finer pages stream in under a geometry memory and upload budget.
6. The GPU performs visibility, detail selection, command generation, and material binning as much as possible.

The first production milestone should stay conservative: static opaque meshes, software-managed logical pages, OpenGL 4.6 and Vulkan-capable buffer layouts, hardware rasterization, conservative Hi-Z culling, and compatibility with the existing `GPUScene` / `GPURenderPassCollection` path. Later phases can add DAG compaction, visibility-buffer material resolve, small-triangle software rasterization, sparse-resource fast paths, and richer editor tooling.

## 2. Current Engine Context

XRENGINE already has several systems that should be reused rather than bypassed:

| Existing system | Relevant capability |
|---|---|
| `XRMesh`, `SubMesh`, `SubMeshLOD` | Imported mesh storage, material association, and distance LOD authoring |
| `MeshOptimizerIntegration` | Meshlet generation, auto LOD generation, simplification settings, meshlet statistics |
| `GPUScene` | GPU mesh atlas, command registration, tiered static/dynamic/streaming geometry buffers, LOD table infrastructure |
| `GPURenderPassCollection` | BVH/frustum/Hi-Z culling, sort keys, indirect command generation, view-set support |
| `HybridRenderingManager` | Traditional indirect submission and meshlet-path routing |
| `RenderGraph` and VPRC commands | Pass/resource metadata, command-chain execution, and future synchronization authority |
| `DefaultRenderPipeline` | Deferred, forward, transparency, temporal, AO, shadow, and post-processing integration points |
| Texture runtime service | Budgeted streaming, upload chunking, per-session logs, and live diagnostics patterns |

`XVG` should become a renderer feature that composes with these systems. It should not be a separate mini-renderer with its own material system, transform system, logging stack, and asset lifetime rules.

## 3. Goals

- Render high-detail static opaque meshes with bounded visible work.
- Use small clustered geometry units instead of whole-mesh LOD selection only.
- Keep runtime detail selection GPU-driven and valid for every view.
- Preserve surface coverage when fine pages are missing by rendering a resident parent or fallback mesh.
- Share renderer infrastructure with `GPUScene`, GPU-driven dispatch, Hi-Z culling, render graph metadata, and material tables.
- Keep the first implementation portable across OpenGL and Vulkan.
- Avoid render-thread stalls from geometry page uploads.
- Provide telemetry that explains detail selection, page residency, upload waits, culling efficiency, fallback usage, and budget pressure.
- Keep hot paths allocation-aware: no LINQ, captured lambdas, transient lists, string formatting, or boxing in per-frame selection, culling, or command-building loops after warmup.

## 4. Non-Goals

- Do not replace every mesh path in the first milestone.
- Do not require mesh shaders, sparse buffers, sparse residency, bindless textures, or software rasterization for correctness.
- Do not support translucent, skinned, morph-targeted, spline-deformed, cloth, or heavily procedural meshes in phase one.
- Do not force masked-card foliage through `XVG` until a geometry-based foliage or assembly workflow exists.
- Do not require a new material graph before the geometry path can render.
- Do not make OpenGL depend on non-portable bindless or sparse behavior for the baseline.
- Do not silently change editor/server/client launch flows while bringing the subsystem online.

## 5. Product Boundaries

The first supported content class is:

- static or rigid opaque triangle meshes
- stable topology
- standard engine PBR materials
- one or more UV sets, with UV0 required for the first material path
- no runtime vertex deformation
- no alpha-masked overdraw-heavy card fields unless explicitly allowlisted

Content outside that boundary should keep using the existing renderer path or an explicit fallback mesh until `XVG` support is mature.

## 6. Core Terms

| Term | Meaning |
|---|---|
| `XVG asset` | Cooked paged geometry asset, stored as `.xvg` by the proposed format |
| Cluster | Small renderable group of triangles with local bounds, material metadata, and page payload |
| Root cluster | Coarse cluster that remains resident or is part of the minimum resident set |
| Page | Independently streamable payload blob containing cluster triangle and vertex data |
| Valid cut | Runtime-selected set of clusters that covers the source surface once, without selecting both a parent and its descendants |
| Fallback mesh | Conventional mesh representation used by unsupported paths, missing features, ray/physics fallbacks, and diagnostics |
| Eligibility | Import-time decision that says whether a source mesh/material can use `XVG` |
| Material bin | GPU-visible list of selected clusters that share a material or state class |

## 7. Core Invariants

### 7.1 Valid Runtime Selection

Runtime selection must never emit a cluster and any of its descendants together. It must also avoid holes where neither a cluster nor its descendants are emitted. The MVP should use a strict tree because it is easiest to validate. A DAG-capable encoding can be added later for storage compaction.

### 7.2 Resident Coverage

Each `XVG` mesh must have enough resident coverage to render a complete surface when fine pages are missing. Missing child pages may reduce quality, but they must not create cracks, missing geometry, or same-frame stalls.

### 7.3 Generation-Gated Streaming

Every page upload, page-table update, and cluster-payload reference must carry an asset generation and residency version. If a mesh is unloaded, recooked, resized in GPU storage, or moved to a new arena slot, stale queued work must cancel or restart.

### 7.4 GPU-Driven Frame Work

The shipping path should not require same-frame CPU readback of GPU-selected clusters, visible counts, or material bins. Page requests are allowed to reach the CPU for IO, but they must be consumed through a delayed or async staging path that does not block the current frame.

### 7.5 Backend-Neutral Policy

Selection policy, page priority, fallback behavior, telemetry naming, and asset validation belong above the backend layer. Buffer binding, barrier lowering, sparse-resource details, and draw encoding belong in backend adapters.

## 8. Capability Tiers

| Tier | OpenGL requirements | Vulkan requirements | Intended scope |
|---|---|---|---|
| Portable baseline | Compute shaders, SSBOs, multi-draw indirect, draw ID, persistent upload buffers, indirect-parameter count path where available | Compute, storage buffers, indirect drawing, transfer/copy, ordinary device-local buffers | Tree hierarchy, GPU culling, page table, hardware raster |
| Production target | Same baseline plus robust indirect-count path and fixed binding layouts | Descriptor indexing, draw-indirect-count, synchronization2, optional buffer device address | Lower CPU overhead, stronger material/resource indirection |
| Advanced optional | Sparse buffers only as an experiment | Sparse resources, transfer queue specialization, optional mesh shader experiments | More aggressive page remapping and research paths |

The baseline must stay useful on desktop OpenGL because that is the primary tested rendering path today. Vulkan can carry cleaner fast paths, but `XVG` correctness cannot depend on a Vulkan-only feature in its first milestone.

## 9. Asset Pipeline

### 9.1 Import Flow

`XVG` cooking should run as an offline or editor-triggered compiler stage:

1. Read source geometry from the engine import result (`XRMesh`, `SubMesh`, materials, skin/deform metadata).
2. Decide eligibility per submesh and material.
3. Canonicalize topology and attributes.
4. Split by material, hard edge, UV seam, unsupported deformation flags, and any boundary that must remain stable.
5. Build leaf clusters.
6. Group adjacent clusters and simplify into parent clusters.
7. Compute bounds, normal cones, geometric error, hierarchy edges, and page membership.
8. Quantize and pack cluster payloads.
9. Compress logical pages.
10. Emit `.xvg`, fallback mesh data, and a deterministic build report.

### 9.2 Leaf Cluster Policy

The existing meshlet settings are a good starting point:

- `MaxVertices = 64`
- `MaxTriangles = 124`
- `ConeWeight = 0.25`
- build modes: dense, scan, flex, spatial

`XVG` leaf clusters do not have to be identical to current meshlets, but the compiler should reuse the meshoptimizer bridge where it fits. The first implementation should not invent a parallel clustering library unless the current meshlet generator cannot satisfy boundary locking, page packing, or hierarchy validation.

### 9.3 Hierarchy Build Policy

Parent clusters are built by grouping adjacent child clusters, simplifying the merged surface while preserving external borders, then reclustering the simplified result back into bounded renderable units.

Required build outputs:

- child range
- optional parent range
- local bounds and bounding sphere
- normal cone
- geometric error
- material or material-class reference
- page id
- payload offset
- vertex and triangle counts
- quantization table index
- flags for eligibility, fallback, and debug validation

The compiler must validate:

- hierarchy is acyclic
- roots cover the source surface
- every non-root node has a reachable root
- every child edge points to a valid cluster
- parent error is monotonic relative to children
- page offsets and sizes stay within page bounds
- material references are valid
- fallback mesh exists when the source asset needs one

### 9.4 File Format

The proposed cooked extension is `.xvg`.

| Chunk | Purpose |
|---|---|
| `HEAD` | Magic, version, endian marker, chunk table, build settings hash |
| `MESH` | Mesh records, root range, fallback reference, resident-page defaults |
| `CLST` | Fixed-size cluster metadata |
| `EDGE` | Variable-length hierarchy edges |
| `PAGE` | Logical page directory |
| `PACK` | Compressed page payloads |
| `MATL` | Material remap table and material eligibility flags |
| `FALL` | Conventional fallback mesh payload |
| `META` | Build diagnostics, source hash, cluster stats, error histograms |
| `STRS` | String table for source path fragments, debug labels, and material names |

### 9.5 C# Record Layout

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct XvgHeader
{
    public uint Magic;               // 'XVG0'
    public ushort VersionMajor;
    public ushort VersionMinor;
    public uint ChunkCount;
    public ulong FileSizeBytes;
    public ulong BuildSettingsHash;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct XvgMeshRecord
{
    public uint MeshId;
    public uint RootClusterStart;
    public ushort RootClusterCount;
    public ushort Flags;
    public uint MaterialStart;
    public ushort MaterialCount;
    public ushort FallbackIndex;
    public uint MinResidentPageStart;
    public ushort MinResidentPageCount;
    public ushort Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct XvgClusterRecord
{
    public Vector3 BoundsMin;
    public Vector3 BoundsMax;
    public Vector4 BoundsSphere;     // xyz + radius
    public Vector4 NormalCone;       // axis.xyz + cosHalfAngle
    public float GeometricError;
    public uint EdgeRangeStart;
    public ushort ChildCount;
    public ushort ParentCount;
    public uint MaterialId;
    public uint PageId;
    public uint PayloadOffset;
    public ushort VertexCount;
    public ushort TriangleCount;
    public uint QuantizationTableIndex;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct XvgPageRecord
{
    public ulong FileOffsetBytes;
    public uint CompressedBytes;
    public uint UncompressedBytes;
    public uint FirstCluster;
    public ushort ClusterCount;
    public ushort PriorityClass;
    public ulong ContentHash;
}
```

The cluster record is the hot metadata unit. Vertex and triangle payloads must stay in pages until a selected cluster actually needs them.

### 9.6 Quantization And Compression

The default packing policy should use:

- cluster-local position quantization with scale and bias
- octahedral normal encoding
- optional explicit tangents, depending on material needs
- 16-bit UVs where error checks allow it
- byte or ushort local triangle indices depending on cluster limits
- independent page compression blocks
- stable hashes for deterministic cache behavior

Compression is part of the design, not a packaging afterthought. Disk footprint, IO bandwidth, upload bandwidth, and decompression cost all need telemetry from the first useful prototype.

## 10. Runtime Architecture

### 10.1 Module Layout

| Module | Responsibility | Ownership |
|---|---|---|
| `XREngine.Rendering.VirtualGeometry.Compiler` | Build clusters, hierarchy, pages, fallback meshes, and reports | Offline/editor worker pool |
| `XREngine.Rendering.VirtualGeometry.Runtime` | Asset registration, instance submission, frame orchestration | Main/render thread boundary |
| `XREngine.Rendering.VirtualGeometry.Streaming` | Async IO, decompression, page priorities, residency state | IO pool and streaming coordinator |
| `XREngine.Rendering.VirtualGeometry.Backend.OpenGL` | SSBOs, page arena, persistent staging, compute dispatch, GL barriers | Render thread or GL context thread |
| `XREngine.Rendering.VirtualGeometry.Backend.Vulkan` | Device buffers, descriptors, transfers, barriers, optional fast paths | Render thread and transfer queue |
| `XREngine.Editor.VirtualGeometry` | Inspectors, conversion UI, visualization overlays, validation reports | ImGui editor first |

### 10.2 Public API Sketch

```csharp
public interface IXvgBackend
{
    XvgBackendCapabilities Capabilities { get; }
    void Initialize(in XvgBackendDesc desc);
    void CreatePermanentResources(in XvgResourceDesc desc);
    void UploadPages(ReadOnlySpan<XvgPageUpload> uploads, in FrameContext frame);
    void ExecuteSelection(in XvgFrame frame, in ViewContext view);
    void ExecuteRaster(in XvgFrame frame, in ViewContext view);
    void Shutdown();
}

public sealed class XvgSystem
{
    public ValueTask<XvgAsset> LoadAsync(string path, CancellationToken ct);
    public void Submit(in XvgInstance instance);
    public void Render(in ViewContext view);
    public void SetBudget(in XvgBudget budget);
}

public readonly record struct XvgBudget(
    long MaxResidentGeometryBytes,
    int MaxPageUploadsPerFrame,
    float MaxErrorPixels,
    int MaxVisibleClusters);
```

The actual implementation should follow existing naming and namespace conventions when created. The important design rule is that backend differences do not leak into scene components or asset importers.

## 11. Frame Flow

### 11.1 CPU Work

The CPU should:

1. Register loaded `XVG` assets and fallback meshes.
2. Submit visible-capable instances with stable transform/material IDs.
3. Publish view constants and budgets.
4. Feed completed async page uploads into the render-thread upload scheduler.
5. Submit fixed render graph passes.

It should not synchronously inspect same-frame GPU visibility output.

### 11.2 GPU Work

The GPU should:

1. Reset per-frame counters and append buffers.
2. Cull instances using existing frustum/BVH/Hi-Z infrastructure where practical.
3. Traverse the `XVG` hierarchy for surviving instances.
4. Select a valid cluster set per view.
5. Emit page requests for missing detail.
6. Emit visible cluster records.
7. Bin visible clusters by material or state class.
8. Build indirect draw or mesh-task dispatch records.
9. Raster selected cluster payloads.
10. Resolve material output into the current pipeline contract.

## 12. Runtime Selection

The runtime selection algorithm must accept a cluster or descend into its children, never both.

```csharp
void SelectVisibleClusters(in ViewContext view, ReadOnlySpan<XvgInstance> instances)
{
    ClearFrameAppendBuffers();

    foreach (ref readonly XvgInstance instance in instances)
    {
        if (!FrustumVisible(instance.WorldBounds, view))
            continue;

        if (CoarseHiZOccluded(instance.WorldBounds, view))
            continue;

        XvgTraversalStack stack = XvgTraversalStack.Rent();

        foreach (int root in instance.Asset.RootClusterIds)
            stack.Push(root);

        while (stack.TryPop(out int clusterId))
        {
            ref readonly XvgClusterMeta cluster = ref ClusterMeta[clusterId];
            Bounds worldBounds = TransformBounds(cluster.LocalBounds, instance.WorldMatrix);

            if (!FrustumVisible(worldBounds, view))
                continue;

            if (cluster.HasNormalCone && BackfaceConeRejected(cluster.NormalCone, instance, view))
                continue;

            if (FineHiZOccluded(worldBounds, view))
                continue;

            float errorPixels = ProjectedErrorPixels(cluster.GeometricError, worldBounds, view);
            bool resident = Residency.IsResident(cluster.PageId);
            bool stopHere =
                cluster.ChildCount == 0 ||
                errorPixels <= Settings.XvgMaxErrorPixels ||
                !resident;

            if (!resident)
                EnqueuePageRequest(cluster.PageId, ComputePagePriority(instance, cluster, view));

            if (stopHere)
            {
                if (resident)
                    EmitVisibleCluster(instance, clusterId);
                else
                    EmitNearestResidentAncestor(instance, clusterId);
            }
            else
            {
                foreach (int child in ChildrenOf(clusterId))
                    stack.Push(child);
            }
        }

        stack.Return();
    }

    BinVisibleClustersByMaterial();
    BuildDrawStreams();
}
```

The real GPU implementation will not use a managed `foreach` loop or rented C# stack in the shader. The pseudocode documents behavior, not literal implementation.

## 13. Page Streaming And Residency

### 13.1 Streaming Rules

- Root or minimum-resident pages are loaded before an `XVG` asset can render through the `XVG` path.
- Fine page misses request IO but render a resident parent in the current frame.
- Page requests are deduplicated and prioritized by projected error, screen size, view distance, and recent visibility.
- Uploads share the render-work budget with texture uploads, mesh uploads, shader warmup, and shadow atlas work.
- Page eviction never removes minimum-resident coverage.
- Eviction prefers invisible, old, low-priority fine pages.

### 13.2 Request Readback Policy

Page request buffers are GPU-authored, but reading them must not stall rendering. Use a ring of staging buffers and consume request data only after a fence confirms completion from an earlier frame. Late requests are acceptable because resident parents preserve coverage.

### 13.3 Upload Flow

```csharp
async ValueTask PumpXvgStreamingAsync(XvgPageRequestSet requests, CancellationToken ct)
{
    foreach (XvgPageRequest request in requests.UniqueSortedByPriority())
    {
        if (Residency.IsResident(request.PageId) || Residency.IsPending(request.PageId))
            continue;

        Residency.MarkPending(request.PageId);

        XvgPageRecord record = PageDirectory[request.PageId];
        byte[] compressed = await Io.ReadAsync(record.FileOffsetBytes, record.CompressedBytes, ct);

        ValidatePageRecord(record, compressed);
        byte[] pageBytes = Decompress(record, compressed);

        XvgArenaSlot slot = GpuArena.Allocate(record.UncompressedBytes);
        await GpuUploader.UploadAsync(slot, pageBytes, ct);

        Residency.Commit(request.PageId, slot, CurrentFrameIndex);
    }

    EvictUntilWithinBudget();
}
```

The final implementation should avoid long-lived byte-array churn by using pooled buffers or streaming decompression into owned upload slices.

## 14. Renderer Integration

### 14.1 MVP Integration

The first integration should route selected clusters through a hardware raster path that can reuse existing renderer assumptions:

- selected clusters become GPU-visible draw records
- material IDs map to existing material/state classes
- cluster payloads live in a geometry page arena
- indirect commands use existing count-based submission patterns where possible
- unsupported or nonresident cases render fallback meshes or resident parents

This gives correctness, diagnostics, and page residency before tackling a full visibility-buffer pipeline.

### 14.2 Production Direction

The target architecture is:

```text
XVG cluster selection
    -> visible cluster bins
    -> hardware raster into coverage / visibility data
    -> material resolve
    -> existing GBuffer or native deferred lighting path
```

This aligns with the bindless deferred texturing plan. The geometry pass should eventually write identity and interpolation inputs rather than fully evaluating material textures during cluster rasterization.

Suggested coverage payload:

- depth
- cluster id
- primitive or local triangle id
- instance id
- material id
- optional barycentric or derivative metadata

Material resolve then reconstructs attributes and writes `AlbedoOpacity`, `Normal`, `RMSE`, or a future native deferred lighting input.

### 14.3 Meshlet Path Relationship

Existing meshlets and `XVG` clusters should not diverge into unrelated systems. The relationship should be:

- meshlets are the bounded execution/rendering unit
- `XVG` adds hierarchy, residency, page tables, and valid-cut selection
- current meshlet tooling can seed leaf cluster generation and diagnostics
- mesh shader dispatch remains optional and backend-gated
- traditional indirect rendering remains the portable baseline

## 15. Backend Design

### 15.1 OpenGL

The OpenGL baseline should use:

- SSBOs for cluster metadata, hierarchy edges, page tables, visible lists, and material bins
- ordinary immutable buffers for page arenas
- persistent mapped staging buffers for uploads
- multi-draw indirect and indirect-parameter count when available
- `gl_DrawID` to recover draw metadata
- explicit `glMemoryBarrier` masks between compute, indirect draw, vertex/index fetch, and shader reads

The OpenGL path should use offset-based addressing into buffers. Sparse buffers should remain experimental because upload staging needs mappable memory and uncommitted-region behavior is not suitable for the baseline.

### 15.2 Vulkan

The Vulkan path should use:

- large device-local buffers for metadata, page tables, hierarchy edges, visible lists, and page payloads
- transfer staging for page uploads
- draw-indirect-count for GPU-authored command counts
- descriptor indexing for material/resource tables when enabled
- buffer device address only as an optimization path, not as the only representation
- synchronization2 or the engine's render graph barrier lowering for compute-to-graphics dependencies

The portable offset-table representation should remain available even when Vulkan fast paths are enabled.

### 15.3 Synchronization Abstraction

The higher-level frame graph should describe dependencies in engine terms:

```csharp
public readonly record struct XvgResourceTransition(
    ResourceUsage Before,
    ResourceUsage After,
    ResourceHandle Handle,
    Range? ByteRange = null);
```

OpenGL lowers this to the correct `glMemoryBarrier` bits and binding transitions. Vulkan lowers this to pipeline barriers and queue ownership transitions where needed.

## 16. Material Architecture

### 16.1 Compatibility Mode

Compatibility mode should keep standard material evaluation as close to the current renderer as possible:

- cluster records carry `MaterialID`
- visible cluster bins group by material or state class
- the renderer submits per-material or per-state streams without CPU readback of dynamic counts
- unsupported material features opt out to the fallback renderer

### 16.2 Visibility-Buffer Mode

Visibility-buffer mode is the production direction:

- cluster raster writes geometry identity
- material resolve reads material tables and texture tables
- texture sampling moves out of the heavy geometry raster path
- deferred decals and material modifiers migrate to the resolve domain over time

This should share as much as possible with the bindless deferred texturing material record and fallback-texture policy.

### 16.3 OpenGL Material Limits

If OpenGL lacks the required bindless texture support, `XVG` should use material windows, texture arrays, atlases, or the classic material path. It should not emulate a bindless design by rebinding arbitrary textures inside a supposedly GPU-driven hot path.

## 17. Diagnostics And Telemetry

Add a geometry-specific log once implementation begins:

```text
Build/Logs/<configuration>_<tfm>/<platform>/<session>/log_virtual_geometry.txt
```

Recommended event names:

- `XVG.AssetLoaded`
- `XVG.AssetRejected`
- `XVG.RootResidentReady`
- `XVG.PageRequested`
- `XVG.PageRequestCoalesced`
- `XVG.PageUploadQueued`
- `XVG.PageUploadApplied`
- `XVG.PageUploadCanceled`
- `XVG.PageEvicted`
- `XVG.SelectionSummary`
- `XVG.FallbackRendered`
- `XVG.ValidationFailed`
- `XVG.BudgetPressure`

Recommended counters:

- `VisitedClusters`
- `EmittedClusters`
- `HiZRejectRate`
- `FineCullRejectRate`
- `PageFaults`
- `ResidentBytes`
- `UploadBytes`
- `VisibleClusterBins`
- `FallbackDrawCount`
- `WorstPageUploadMs`
- `OldestPageRequestAge`
- `RootResidentBytes`
- `MaterialBinCount`

The editor should eventually expose an ImGui `XVG` diagnostics panel showing loaded assets, page residency, top memory users, current fallback reasons, oldest pending page requests, and per-view selection summaries.

## 18. Cost Model

```text
ResidentVRAM
  = RootResidentBytes
  + ActivePageBytes
  + ClusterMetaBytes
  + HierarchyEdgeBytes
  + PageTableBytes
  + VisibleListBytes
  + VisibilityBufferBytes
  + HiZBytes
  + UploadRingBytes

DiskBandwidthPerSecond
  = FPS * PageFaultsPerFrame * AvgCompressedPageBytes

UploadBandwidthPerSecond
  = FPS * PageFaultsPerFrame * AvgUncompressedPageBytes

GPUFrameCost
  = Cinstance * VisibleInstances
  + Cnode * VisitedClusters
  + Chiz * HiZTests
  + Ccmd * EmittedClusters
  + Craster * RasterizedTriangles
  + Cshade * VisiblePixels
```

Triangle count is not the primary runtime budget. The main control variables are visited clusters, emitted clusters, page faults, resident bytes, visible pixels, material divergence, and upload latency.

## 19. Performance Knobs

| Knob | Lower setting | Higher setting | Initial recommendation |
|---|---|---|---|
| Leaf triangle budget | Better culling, more metadata | Less metadata, coarser culling | Match meshlet default: up to `124` triangles |
| Page size | Less IO overfetch, more page records | Better compression, more overfetch | Start at `32 KiB` logical pages |
| Error threshold | Higher detail, more pages | Lower cost, more simplification | Express in pixels |
| Root residency | Less standing VRAM | Fewer page-miss quality drops | Tune per asset class |
| Hi-Z resolution | Better occlusion precision | More bandwidth and compute | Start conservative |
| Uploads per frame | Faster convergence | More render-thread pressure | Share budget with texture uploads |
| Material bin cap | Lower memory use | Lower overflow risk | Derive from command capacity |

## 20. Validation Plan

### 20.1 Compiler Tests

| Test | Checks |
|---|---|
| Deterministic cook | Same source and settings produce stable hashes and counts |
| Cluster bounds | Every triangle in a cluster is contained by cluster bounds |
| Hierarchy validity | No cycles, valid child ranges, reachable roots |
| Valid cut coverage | Parent/child selection cannot double-render or miss coverage |
| Border preservation | Adjacent cluster cuts do not create cracks |
| Page table integrity | Payload offsets and sizes fit the owning page |
| Fallback presence | Every eligible asset has a valid fallback route |

### 20.2 Runtime Tests

| Test | Checks |
|---|---|
| Root-only render | Asset renders completely with only root pages resident |
| Page miss fallback | Fine misses render resident parent, not holes |
| Async page load | Page requests load without render-thread stalls |
| Eviction safety | Minimum-resident pages are never evicted |
| Camera sweep | Stable quality with no flickering cuts |
| Resolution sweep | Screen-space error behaves consistently across output sizes |
| Backend parity | OpenGL and Vulkan select comparable clusters and output comparable images |

### 20.3 Stress Tests

- large architectural mesh with many clusters
- material-diverse imported scene
- low geometry memory budget
- rapid camera movement through high-detail geometry
- repeated asset load/unload
- many `XVG` instances sharing one asset
- mixed scene with `XVG`, classic meshes, auto LOD meshes, meshlets, shadows, and texture streaming active

## 21. Robustness Rules

Every `.xvg` load must validate before GPU upload:

- magic and version
- chunk table ranges
- compressed and uncompressed page sizes
- record counts
- cluster child/parent ranges
- hierarchy acyclicity
- page-local payload offsets
- material indices
- fallback references
- content hashes
- maximum page and asset byte limits

Runtime protection:

- traversal iteration caps
- checked page and cluster indices
- zero-initialized indirect buffers
- draw counts clamped to backend limits
- explicit capability downgrade logs
- single-writer residency state transitions
- generation counters for page arena slots

## 22. Migration Plan

### Phase 0: Research And Instrumentation

- Audit candidate asset corpus.
- Add renderer capability snapshots for `XVG` requirements.
- Confirm meshlet and auto LOD data can seed compiler prototypes.
- Add baseline metrics for large imported scenes.

### Phase 1: Offline Compiler MVP

- Build leaf clusters from `XRMesh`.
- Build a strict tree hierarchy.
- Emit `.xvg` with metadata, pages, fallback mesh, and build report.
- Add offline validators and deterministic cook tests.

### Phase 2: Runtime Residency MVP

- Load `.xvg` metadata and root pages.
- Add software-managed page arena.
- Add async IO and decompression.
- Render root-only or selected resident clusters through hardware raster.
- Log page requests, uploads, and fallback draws.

### Phase 3: GPU Selection And Hi-Z

- Add cluster-selection compute pass.
- Integrate conservative Hi-Z rejection.
- Emit visible cluster bins.
- Keep page request readback delayed and nonblocking.
- Add selection telemetry and validation overlays.

### Phase 4: GPU-Driven Submission

- Build indirect streams from visible clusters.
- Integrate with existing material/state-class dispatch rules.
- Avoid same-frame CPU readback of visible counts or bins.
- Share command limits and overflow handling with `GPURenderPassCollection`.

### Phase 5: Material Resolve Path

- Add visibility-buffer compatibility path.
- Reuse bindless deferred texturing material records where available.
- Keep classic material/fallback path for unsupported materials.
- Add debug views for cluster id, material id, page residency, and selected error.

### Phase 6: Backend Hardening

- Harden OpenGL barrier and persistent upload behavior.
- Add Vulkan fast paths behind capability gates.
- Validate cross-backend image and selection parity.
- Add capture-friendly debug markers and profiler stages.

### Phase 7: Tooling And Content Migration

- Add batch conversion tools.
- Add ImGui asset inspector and scene overlay.
- Add import eligibility reports.
- Add CI validators for cooked assets.
- Document authoring rules and fallback behavior.

### Phase 8: Advanced Features

- DAG compaction.
- Small-triangle software raster path.
- Sparse resource experiments.
- Mesh shader task dispatch experiments.
- Geometry-based foliage and assembly workflows.

## 23. Estimated Effort

| Phase | Estimated effort | Main risk |
|---|---:|---|
| Research and instrumentation | 2-4 engineer-weeks | Bad asset assumptions |
| Offline compiler MVP | 6-10 engineer-weeks | Cracks or nondeterministic output |
| Runtime residency MVP | 6-10 engineer-weeks | Upload stalls or page-table bugs |
| GPU selection and Hi-Z | 8-12 engineer-weeks | Invalid cuts or over-culling |
| GPU-driven submission | 4-8 engineer-weeks | Material/state-class integration |
| Material resolve path | 4-8 engineer-weeks | Deferred pipeline migration friction |
| Backend hardening | 6-12 engineer-weeks | Driver differences |
| Tooling and migration | 6-10 engineer-weeks | Unsupported-content regressions |
| Advanced features | 10-20 engineer-weeks | Complexity and correctness risk |

A credible MVP for static opaque meshes with hardware rasterization is roughly 30-50 engineer-weeks. A mature production system with advanced raster paths, polished tooling, and broad content migration support is closer to 60-100 engineer-weeks.

## 24. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Compiler creates cracks | Severe visual artifacts | Border locking, seam tests, cut validator |
| Page streaming thrashes | Stutter and quality bounce | Root residency, hysteresis, upload budget, pressure logs |
| Material diversity explodes bins | Draw or dispatch overhead | State-class grouping and material eligibility reports |
| OpenGL binding limits constrain materials | Backend-specific fallback behavior | Capability flags, material windows, classic fallback |
| Debugging GPU selection is hard | Slow bring-up | Strong overlays, logs, and offline validators |
| Fine pages arrive late | Visible simplification | Resident parents, request priority, prestream tooling |
| Hot-path allocation creeps in | Frame-time spikes | Pooled lists, fixed buffers, source-contract tests |

## 25. Open Questions

- Should `.xvg` assets live beside externalized `XRMesh` assets or inside a separate cooked geometry cache?
- Should the first runtime path raster selected clusters directly into the current GBuffer or into a visibility buffer with compatibility resolve?
- What default geometry memory budget should editor sessions use?
- How should `XVG` eligibility be represented in import settings and material inspectors?
- Should meshlet generation settings become the first `XVG` leaf-cluster settings UI, or should `XVG` expose a separate profile?
- Which scenes should become the canonical validation corpus for large static geometry?
- Should `log_virtual_geometry.txt` be enabled by default in Debug summary mode, or only when the feature is enabled?

## 26. Final Recommendation

Build `XVG` as a staged extension of the existing GPU-driven renderer:

1. Cook deterministic clustered page assets.
2. Keep root coverage resident.
3. Select valid cuts on the GPU.
4. Stream fine pages without blocking the current frame.
5. Raster through the hardware path first.
6. Move toward a visibility-buffer and material-resolve architecture once correctness and residency are solid.

The restraint matters. The engine already has GPU-driven rendering, LOD tables, meshlet tooling, Hi-Z, bindless deferred planning, and texture streaming diagnostics. `XVG` should connect those pieces into a coherent paged-geometry system instead of replacing them with a parallel renderer.
