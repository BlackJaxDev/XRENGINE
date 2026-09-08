# Advanced classification evidence inventory (ARP-A05/A06)

Source-only audit; no build, test, editor run, or runtime assertion. Scope is
the Vulkan advanced classification kernels and their shared interface.

## Classifier key and immutable dimensions

`Build/CommonAssets/Shaders/Advanced/Classification/ClassifyTiles.comp:31-47`
resolves each visibility identity/metadata pair through
`XR_ADV_TryResolvePixelMaterial`, producing dense material and shading-kernel
indices. The view dimension is checked independently from metadata at `:34`;
array mode includes `u_Push.viewIndex` in the texture coordinate (`:5-12`).
The active tile record preserves view index and active coverage at `:64-66`.

The kernel writes membership records with kernel id, per-kernel coverage,
coverage classification, and the resolved dense kernel's analytical-derivative
requirement (`ClassifyTiles.comp:122-127`). Material rows remain data and the
tile retains its view index. `ShadeNativeOpaque.comp:80-115` independently
bounds the membership range, verifies its stored kernel id and derivative flag
against the resolved dense kernel, and requires metadata view identity before
reconstruction or shading.
Visibility coverage and derivatives originate in
`Build/CommonAssets/Shaders/Advanced/Visibility/VisibilityRaster.frag:11,41-42`
and `VisibilityRasterMasked.frag:11,41-42`, where coverage UV plus `dFdx/dFdy`
are consumed before classification. The classifier itself does not recompute
derivatives or material layout; it carries the resolved dense kernel identity.

## Independent capacity and overflow bounds

| Resource / bound | Source evidence | Failure behavior |
|---|---|---|
| Kernel histogram | `ClassifyTiles.comp:15,21,39-43`; `XR_ADV_MAX_SHADING_KERNELS` bounds shared histogram writes and rejects oversized dense kernel ids | Sets `XR_ADV_CLASSIFICATION_OVERFLOW_KERNELS` at `:40` |
| Active tile records | `ClassifyTiles.comp:54-59`; `tileCapacity=XR_ADV_TotalTileCount()`, then min with `ActiveTiles.records.length()` | Atomic reservation beyond capacity sets `OVERFLOW_TILES` and returns without writing |
| Per-kernel membership range | `ClassifyTiles.comp:104-124`; classifier bounds the product before calculating `kernel * tileCapacity`, then bounds the range against both logical and buffer capacity | Sets `OVERFLOW_MEMBERSHIPS`; skips the record |
| Dispatch command array | `BuildClassificationIndirect.comp:6-7`; kernel id must be below max kernels and command-array length | Out-of-range invocation returns with no command write |
| Dispatch tile capacity | `BuildClassificationIndirect.comp:8-12`; computes kernel range base and available capacity from `u_Push.maxKernelTiles`, clamps count | Dispatch count is clamped; zero count emits one workgroup in Y (`:15`) |
| Shared tile product and consumer range | `AdvancedShadingInterface.glslinc:38-45` rejects zero/overflowing extent-view products; `ShadeNativeOpaque.comp:82-94` bounds its range before multiplication | Producers and consumers observe zero capacity or return before any wrapped address |
| Global producer failure | `BuildClassificationIndirect.comp:13-14` | Any overflow flag forces dispatch count to zero, selecting the separate repair path |
| Counters | `ResetClassificationCounters.comp:3-25` resets active tiles, kernel tiles, classified/background/dropped pixels, and overflow flags before use | Reset is explicit; source audit does not prove pass ordering at runtime |

## Host integration and ordering

The host does define a typed permutation key. `XREngine.Runtime.Rendering/Rendering/Classification/Advanced/AdvancedClassificationKey.cs:8-27`
contains `ShadingKernelId`, `MaterialLayoutHash`, `CoverageClass`,
`DerivativeMode`, and `ViewMode`; equality covers all five dimensions at
`:30-35`. The GPU kernel identity record is
`XREngine.Runtime.Rendering/Rendering/Materials/Advanced/AdvancedShadingKernelRecord.cs:8-29`:
stable kernel id, generation, material layout hash, supported coverage mask,
eligibility/features, shader identity, render-state mask, and reserved fields.
`AdvancedClassificationKey` has no construction callsites in the repository
search, so it is not the live registry enforcement point. The resolved dense
kernel record is the executable grouping identity. The record/database path
does validate material layout hashes in
`AdvancedMaterialDatabase.cs:156,720,757,858` and publishes the hash from
`AdvancedGpuMaterialPublisher.cs:596`.

Host pipeline ordering is explicit in
`XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.NativeShading.cs`:

1. `RecordAdvancedNativeComputePayload` computes checked tile capacity from
   extent and view count (`:21-28`), validates active-tile/kernel-count/dispatch
   buffer sizes (`:29-32`), and derives `maxKernelTiles` from native size
   (`:36-40`).
2. Classification stage fills counters and kernel counts (`:42-43`), dispatches
   `Advanced.ClassifyTiles` (`:44-46`), inserts compute dependencies for counts
   and counters (`:47-53`), then dispatches `Advanced.BuildClassificationIndirect`
   (`:54`) and inserts the indirect-command dependency (`:55-58`).
3. Native shading consumes per-kernel indirect commands at `:90-103`; the
   explicit GPU repair/full-screen path is then dispatched as
   `Advanced.NativeOpaque.GpuOverflowRepair` at `:104-105`. Thus the repair pass
   is present and ordered after indirect consumption, rather than missing.
4. Counter clearing uses `FillNativeCounters` (`:140-148`) before classification.
   `ResetClassificationCounters.comp` remains a shader asset exposed by
   `AdvancedClassificationShaderLibrary.cs:10-12`, but this native recording
   path clears counters with Vulkan fill operations, so the standalone reset
   shader's host invocation is not established here.

The pipeline readiness owner registers the classify and indirect pipelines in
`VulkanAdvancedVisibilityPipelineRuntime.Native.cs:14-17`; the consumer shader
is registered at `:28`. This establishes asset/pipeline availability, not a
runtime proof of dispatch success.

## Closed source audit and remaining validation

**ARP-A05.** `XR_ADV_TryResolvePixelMaterial` rejects stale/invalid handles and
mismatched layout or coverage. The resolved dense kernel is the executable
grouping identity. The classifier carries its analytical-derivative requirement
into every membership and the indirect consumer rechecks kernel, derivative,
and metadata view identity before reconstruction/shading. The unconstructed
`AdvancedClassificationKey` remains a complete host descriptor but is not
relied upon for the live contract.

**ARP-A06.** Host recording derives checked capacities from extent and view
count. The shared tile product, classifier write, indirect argument generation,
and indirect shading consumer independently reject overflowing products and
out-of-range ranges. Host-owned Vulkan fills clear counters before
classification; the registered reset shader is not this path's reset owner.

**ARP-I30 remains required:** compile the changed mono/array and shared/subgroup
classification and shading variants. `ARP-V05` through `ARP-V07` still require
runtime evidence; this audit makes no runtime-performance or image-correctness
claim.
