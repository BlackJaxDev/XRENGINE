# World Shader Prewarm Graph

Status: design proposal.

Related docs:

- [OpenGL Program Linking](../../../developer-guides/rendering/opengl-program-linking.md)
- [Uber Shader Varianting](../../../architecture/rendering/uber-shader-varianting.md)
- [Default Render Pipeline Known Issues](../../../architecture/rendering/default-render-pipeline-notes.md)
- [Rendering Frame Lifecycle And Dispatch Paths](../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)

## Problem

XRENGINE can already warm a small hard-coded set of engine shaders and can build
OpenGL program binaries through the async source-link and binary-upload lanes.
That lower-level machinery does not yet know the answer to the higher-level
question:

> Given this world, these cameras, these render pipelines, and these authored
> assets, what shader programs should be prepared before the first important
> frame?

The current lazy path discovers many shader combinations only when a mesh,
pipeline pass, light, UI batch, decal, particle system, or material variant is
first rendered. That is correct but not ideal for editor startup, play mode, XR,
and large imported worlds where a first-use hitch is visible.

The desired architecture is a typed dependency graph where scene objects and
assets can declare what they will need, render pipelines can declare their pass
requirements, and the engine can collapse those declarations into concrete
shader program warmup work.

## Goals

- Let components and transforms declare the assets, render pipelines, shaders,
  and shader variant intents they may use.
- Let assets declare dependent assets, render pipelines, shaders, and generated
  variants they require.
- Let render pipelines and render-pipeline commands declare the internal
  materials, compute programs, blit shaders, shadow/depth variants, and
  post-processing shaders they require.
- Build a deterministic, deduplicated warmup manifest for a world.
- Support world-authored load zones that prewarm shader programs before the
  player enters a region or while a region is being unloaded.
- Convert the manifest into backend-specific warmup work, starting with
  OpenGL program binary cache load/source compile/link and optional first-use
  validation draws.
- Keep runtime rendering correct when warmup is incomplete, stale, canceled, or
  failed.
- Make shader warmup diagnosable with stable keys, per-stage timings, and
  cache-hit/miss summaries.

## Non-Goals

- Do not ship OpenGL program binaries as portable build artifacts. They remain
  driver/GPU-local cache entries.
- Do not block ordinary world loading on full warmup by default. Blocking is a
  selectable policy for XR-critical or loading-screen paths.
- Do not make the graph builder a per-frame render hot path.
- Do not replace the existing OpenGL source compile/link, binary cache, or Uber
  variant systems. The prewarm graph feeds them earlier.
- Do not derive shader program combinations from reflection-only asset graph
  traversal. Reflection can find serialized references, but it cannot express
  render-pass intent, generated vertex variants, shadow variants, or feature
  masks.

## Design Summary

Add a CPU-side `RenderPrewarmGraph` layer above the existing rendering backend:

```text
XRWorld / scene roots / active cameras
  -> SceneNode components and transforms
  -> referenced assets and prefab roots
  -> materials, meshes, textures, render pipelines
  -> render-pipeline commands and pass requirements
  -> shader variant intents
  -> shader program descriptors
  -> backend warmup jobs
  -> OpenGL binary load/source link/validation draw
```

The graph builder is allowed to run during editor boot, world load, play-mode
transition, or an external target-local shader-cache compiler run. It produces
stable descriptors, not live GL handles. Backends consume descriptors and use
their existing queues to load binaries, link source programs, reflect uniforms,
and swap ready handles.

## Core Concepts

### Dependency Providers

Any object that can affect rendering should be able to describe its dependencies
without forcing a render. Providers are pull-based and versioned so the builder
can skip subtrees whose render-relevant state has not changed:

```csharp
public interface IRenderPrewarmDependencyProvider
{
    // Stable hash of this provider's render-relevant state. The builder skips
    // re-traversal when the hash matches the prior manifest's node hash. Must
    // change whenever any field that could alter declared dependencies changes
    // (shader assignments, Uber state, transparency mode, pipeline feature
    // flags, mesh capability flags, etc.). Per-frame transform values must not
    // affect this hash.
    long GetRenderPrewarmDependencyVersion();

    void CollectRenderPrewarmDependencies(RenderPrewarmDependencyCollector collector);
}
```

This removes the need to wire dirty-flag plumbing into every component: the
builder simply re-asks providers for their version on rebuild and reuses the
prior subgraph when nothing changed.

Candidate implementers:

- `XRWorld` or `XRWorldInstance`: root world assets, scene roots, active
  cameras, world render settings, environment assets.
- `SceneNode`: forwards to its `Transform`, components, prefab link, and child
  nodes.
- `XRComponent`: renderable components, lights, cameras, decals, particles,
  UI canvases, audio-visual components, editor gizmos, and custom game
  components.
- `XRAsset`: materials, meshes, submeshes, textures, shaders, prefabs,
  animation assets, render pipelines, and author-defined asset containers.
- `RenderPipeline` and render-pipeline commands: internal FBO materials,
  full-screen passes, compute passes, lighting passes, shadow variants,
  transparency resolve passes, GI passes, debug views, and UI paths.

`TransformBase` does not implement `IRenderPrewarmDependencyProvider` directly.
Most transforms have nothing to declare and the cost of giving every transform
the interface outweighs the benefit. Specialized transforms that genuinely
affect program shape (billboards, impostors, procedural-placement transforms,
skinned-root selectors) opt in via a marker interface instead:

```csharp
public interface IRenderPrewarmTransformContributor : IRenderPrewarmDependencyProvider
{
}
```

The builder probes for this interface only on transforms that own render-facing
data.

### Collector And Planner Surfaces

Provider intent and concrete program emission are separated structurally so
components cannot bypass the planner.

The declarative collector is what components, assets, and transforms see:

```csharp
public sealed class RenderPrewarmDependencyCollector
{
    public void AddAsset(XRAsset asset, RenderPrewarmReason reason);
    public void AddRenderPipeline(RenderPipeline pipeline, RenderPrewarmReason reason);
    public void AddShader(XRShader shader, RenderPrewarmReason reason);
    public void AddMaterialUsage(
        XRMaterial material,
        RenderPrewarmPassIntent passIntent,
        RenderPrewarmMeshContextHint meshContextHint,
        RenderPrewarmReason reason);
}
```

The planner-only surface is reachable only from pipeline-pass providers and
from variant resolvers running inside the planner:

```csharp
public sealed class RenderPrewarmProgramPlanner
{
    public void EmitProgramDescriptor(RenderPrewarmProgramDescriptor descriptor);
    public void EmitVariantIntent(RenderPrewarmVariantIntent intent);
}
```

This enforces the rule that components declare intent and the planner emits
descriptors. A renderable component cannot inject a hand-rolled program
descriptor; it can only describe a material plus a pass intent.

### Reasons

`RenderPrewarmReason` is a structured value, not free text, so diagnostics can
answer "why was this program warmed?" without string parsing:

```csharp
public readonly record struct RenderPrewarmReason(
    RenderPrewarmReasonKind Kind,
    Guid? SceneNodeId,
    Guid? AssetId,
    string? PipelinePassName,
    string? Note);

public enum RenderPrewarmReasonKind
{
    SceneObject,
    Asset,
    PipelinePass,
    LightShadow,
    EditorPreview,
    LoadZone,
    ManualSeed,
}
```

Provider methods must be CPU-only. They may read asset metadata, material state,
pipeline command structure, and world/camera settings. They must not create GL
objects, synchronously link programs, or perform render-thread-only work.

### Graph Nodes

The graph should use typed nodes so diagnostics can answer "why was this
program warmed?"

| Node | Meaning |
| --- | --- |
| `WorldRoot` | A world or scene root selected for warmup. |
| `SceneObject` | A `SceneNode`, `XRComponent`, or `TransformBase`. |
| `Asset` | An `XRAsset` dependency, including external and embedded assets. |
| `RenderPipeline` | A pipeline asset selected by a camera, viewport, world, or asset. |
| `PipelinePass` | A command or pass inside a render pipeline. |
| `MaterialIntent` | A material plus the pass family it may render in. |
| `ShaderVariantIntent` | A generated shader request, such as an Uber feature mask or define-based pass variant. |
| `ProgramDescriptor` | A concrete shader-stage topology and render-state context that can produce one backend program. |
| `WarmupJob` | Backend work derived from a `ProgramDescriptor`. |

Edges are also typed:

- `ReferencesAsset`
- `UsesPipeline`
- `UsesMaterial`
- `UsesShader`
- `RequiresVariant`
- `RequiresProgram`
- `DiscoveredFromPipelinePass`
- `DiscoveredFromSceneObject`

Typed edges matter because duplicated shader programs should collapse to one
job while retaining all reasons for diagnostics.

### Existing Asset Graph

`XRAssetGraphUtility` should remain the serialization graph maintainer. It can
be used as a helper to discover embedded assets when a provider adds an asset,
but it should not become the warmup authority.

Warmup needs render intent that a serialized object graph cannot infer:

- Which render pipeline is active for a camera.
- Which render pass will consume a material.
- Whether a material needs a forward, deferred, shadow, depth-normal, PPLL,
  weighted OIT, depth-peel, stereo, instanced, skinned, or meshlet variant.
- Which pipeline features are enabled at runtime.
- Which Uber properties are static and therefore part of the shader source.
- Which first-use validation draw is needed to flush deferred driver work.

## Dependency Declaration Rules

### Components

Components declare dependencies from authored state and runtime configuration.
Examples:

- A mesh/renderable component adds its `SubMesh`, each LOD `XRMeshRenderer`,
  `XRMesh`, `XRMaterial`, and skinning/morph/instancing capabilities.
- A camera component adds its `RenderPipeline`, anti-aliasing mode, stereo mode,
  MSAA sample count, and post-process feature set.
- A light component adds shadow mode, shadow-map encoding, contact-shadow
  settings, cascade/cubemap render mode, and any shadow material variants it
  can cause.
- A decal component adds the decal material plus deferred or forward decal pass
  intent.
- A UI canvas adds its UI pipeline/materials and batched text shaders.
- A particle or procedural component adds compute shaders, simulation buffers,
  materials, and generated mesh families it will use.

Components should prefer conservative declarations. If a component can render
through multiple pass families based on settings, it can add multiple
`MaterialIntent` records with different reasons and priorities.

### Transforms

Most transforms own no render dependencies. Specialized transforms participate
by implementing the opt-in `IRenderPrewarmTransformContributor` marker rather
than being probed wholesale. Examples:

- A transform that references a target asset for procedural placement can add it.
- A transform that affects vertex generation, billboarding, impostors, or
  skinned root selection can add a variant axis to the owning material/mesh
  intent.
- Editor-only transforms can add gizmo/debug dependencies at low priority.

Transforms must not emit per-frame dependencies for changing matrices. Warmup
cares about program shape, not current transform values, and per-frame matrix
churn must not affect `GetRenderPrewarmDependencyVersion()`.

### Assets

Assets declare dependencies that travel with the asset regardless of where it is
used.

Examples:

- `XRMaterial` adds its shaders, textures, required engine uniforms, render
  pass, transparency technique, Uber authored state, and material-side generated
  variants.
- `XRShader` adds include dependencies and UI manifest dependencies. It does
  not create a program by itself unless paired with a material/pipeline intent.
- `XRMesh` or `SubMesh` adds LOD renderers, material slots, skinning, morph,
  tangent, meshlet, and indirect-draw capabilities that affect generated vertex
  programs.
- A prefab asset adds its root scene node dependency graph.
- A render target or material framebuffer adds its quad material and attached
  resources.
- A render pipeline asset adds its command-chain dependencies.

External asset references and embedded assets should both be represented. The
graph key distinguishes "this asset file is needed" from "this embedded object
is needed" so cache invalidation remains precise:

- File asset key: `{ AssetGuid, FileHash }`.
- Embedded asset key: `{ OwnerAssetGuid, EmbeddedPath, OwnerFileHash }`.

`OwnerFileHash` is required so edits to the owner asset that do not change the
embedded path still invalidate dependent descriptors. Hashes are the same
content hashes the existing asset cache uses; the graph never recomputes them
out-of-band.

### Render Pipelines

`RenderPipeline` and `ViewportRenderCommandContainer` need an explicit
enumeration surface for warmup:

```csharp
public virtual void CollectRenderPrewarmDependencies(
    RenderPrewarmDependencyCollector collector,
    RenderPrewarmPipelineContext context)
{
}
```

The context supplies stable feature choices for a candidate frame:

- camera type and stereo mode
- effective AA mode and MSAA sample count
- output HDR/LDR format
- active debug views
- transparency mode support
- enabled GI/AO/fog/shadow features
- render library and backend capabilities

Pipeline commands then add the shaders/materials they use. For the default
pipeline this includes, but is not limited to:

- deferred GBuffer and light-combine passes
- forward plus light culling compute
- motion vectors
- depth-normal prepass
- temporal accumulation, TSR, FXAA/SMAA/TAA paths
- bloom, color grading, tonemap, LUT, depth of field, motion blur
- volumetric fog passes
- transparency resolve paths: weighted OIT, PPLL, depth peeling
- shadow materials for directional, spot, and point lights
- GI composites and debug visualization
- UI text and screen-space UI materials

The existing `WarmFirstRenderShaders()` and `WarmDeferredLightingShaders()`
methods become bootstrap seed providers, then can be reduced as pipeline-command
providers become complete.

## Program Descriptors

The graph collapses material, mesh, and pipeline intent into
`RenderPrewarmProgramDescriptor` records.

```csharp
public sealed record RenderPrewarmProgramDescriptor(
    RenderPrewarmProgramFamily ProgramFamily,
    RenderPrewarmStageSet Stages,
    RenderPrewarmPassContext PassContext,
    RenderPrewarmMeshContext MeshContext,
    RenderPrewarmMaterialContext MaterialContext,
    RenderPrewarmPipelineContext PipelineContext,
    RenderPrewarmPriority Priority);
```

`ProgramFamily` is an enum (`Graphics`, `Shadow`, `DepthNormal`, `Compute`,
`PostProcess`, `UI`, `BlitOrFullscreen`, `Decal`, `Particle`, ...), not a
string, so descriptor hashing avoids per-comparison string work in the planner
hot path.

`RenderPrewarmStageSet` is a fixed struct of optional stage slots
(`Vertex`, `TessControl`, `TessEval`, `Geometry`, `Fragment`, `Compute`),
rather than `IReadOnlyList<RenderPrewarmShaderStage>`. This guarantees
canonical stage ordering and value-equality for free, so dedup hashing is
stable without custom equality glue.

Descriptor fields should be stable and serializable:

- shader stage asset ids or resolved engine shader paths
- generated source variant hashes
- stage topology and separable/monolithic mode
- render pass id and pass family
- mesh-generated vertex axes: skinned, morph, instanced, stereo, transform id,
  velocity, meshlet/indirect, shadow layer mode
- material axes: shader-state revision, Uber variant hash, transparency
  technique, alpha test, required engine uniforms, sampler layout version
- pipeline axes: stereo, MSAA, output HDR, AA mode, feature toggles, debug view
- backend cache policy

The descriptor is not the OpenGL binary cache key. It is the high-level request
that eventually feeds the existing `XRRenderProgram` and `GLRenderProgram`
hash/cache-key machinery.

### Deduplication Contract

Descriptor deduplication is part of the public contract:

- Stages are stored in a fixed-slot struct (`RenderPrewarmStageSet`) so stage
  order is canonical and value equality is automatic.
- All context records (`PassContext`, `MeshContext`, `MaterialContext`,
  `PipelineContext`) are `record struct` or sealed `record` types whose
  members are themselves value-comparable. No `IReadOnlyList<T>` of reference
  types may be a hash input without an explicit canonical ordering.
- The descriptor hash is `xxHash64` of the value-equal field set. Implementers
  must include unit tests proving two semantically identical descriptors
  produce the same hash regardless of construction order.
- Two descriptors that hash equal must be merged in the graph; the merged node
  retains the union of all `RenderPrewarmReason` entries for diagnostics.

### Avoiding Explosive Cross Products

The graph must not eagerly produce every theoretical combination. It should
only combine:

- materials reachable from the selected world or selected asset set
- pass families reachable from active render pipelines
- mesh axes observed on reachable renderers
- light/shadow variants implied by reachable lights and pipeline settings
- explicitly requested editor validation variants

For example, an imported material with normal/roughness/metallic textures should
not automatically produce every transparency, stereo, shadow, and debug variant.
It should produce the variants that the active pipelines and reachable scene
objects can actually draw.

Two concrete mechanisms enforce this beyond policy text:

#### Bounded Planner

The planner accepts a budget and refuses to exceed it:

```csharp
public sealed record RenderPrewarmBudget(
    int MaxDescriptors,
    int MaxDescriptorsPerMaterial,
    int MaxDescriptorsPerPipelinePass,
    int MaxVariantsPerShader);
```

When any cap is hit, the planner drops the lowest-priority descriptors first,
emits `Prewarm.BudgetExceeded` with the dropped set, and continues. Dropped
descriptors fall back to the lazy runtime path. Default budgets target a
working-set size that fits the existing OpenGL link queue without dominating
load time.

#### Canonical Mesh Contexts

`RenderPrewarmMeshContext` is built from a closed enum set rather than
free-form axes:

```csharp
public enum RenderPrewarmMeshTopology
{
    Static,
    Skinned,
    SkinnedMorph,
    Instanced,
    InstancedSkinned,
    StereoStatic,
    StereoSkinned,
    MeshletIndirect,
}
```

Cross-products are only legal between members of canonical enum sets
(topology x pass family x stereo x AA x transparency technique). This caps
combinatorics by construction and makes the descriptor space enumerable for
tests.

## World Build Lifecycle

### 1. Select Roots

Warmup begins from a `RenderPrewarmRequest`:

```csharp
public sealed record RenderPrewarmRequest(
    XRWorld World,
    IReadOnlyList<XRCamera> Cameras,
    IReadOnlyList<RenderPipeline>? PipelineOverrides,
    RenderPrewarmMode Mode,
    RenderPrewarmPolicy Policy);
```

`PipelineOverrides` is optional and authoritative when present. Precedence:

1. If `PipelineOverrides` is non-null, those pipelines are used and camera
   pipeline assignments are ignored for traversal (cameras still contribute
   their non-pipeline state such as stereo and AA mode).
2. Otherwise the builder derives pipelines from `Cameras` plus world settings.

This removes the prior ambiguity where camera and pipeline lists could disagree.

The editor can create requests for the active world, selected prefab, imported
asset preview, unit-testing world, or current play-mode target. Standalone can
create a request from the startup world and active cameras.

### 2. Snapshot State

The graph builder snapshots relevant CPU-side state before traversal:

- active/inactive/editor-only inclusion policy
- camera pipeline selection
- renderer backend and capabilities
- GL runtime fingerprint when available
- relevant rendering settings and editor preferences
- asset file timestamps and source hashes where already known

Snapshotting prevents the warmup manifest from changing under the builder while
the user edits the world.

### 3. Traverse Providers

The builder traverses providers breadth-first. It tracks visited object identity
and asset identity to avoid cycles. Provider exceptions are logged and isolated
to the failing node.

Traversal emits graph nodes and high-level dependency declarations. It does not
link programs.

### 4. Resolve Variants

Variant resolvers turn declarations into shader/material variants:

- Uber material state -> generated fragment shader request.
- `ShaderHelper.CreateDefinedShaderVariant` requests for depth-normal, shadow,
  point-shadow, weighted OIT, PPLL, and depth-peel passes.
- Pipeline-driven compute shader variants.
- Generated vertex shader variants from mesh and pass requirements.

Variant resolution runs on CPU worker lanes where possible. The render thread is
only used for backend adoption.

### 5. Emit Program Descriptors

The planner pairs reachable materials and pipeline pass contexts with mesh
contexts. It deduplicates descriptors by stable descriptor hash and attaches all
discovery reasons.

Descriptors are sorted by priority:

1. `BlockingCritical`: current camera/pipeline resources needed before first
   visible XR or game frame.
2. `VisibleHigh`: currently visible world content and selected editor content.
3. `WorldNormal`: active world content likely to appear soon.
4. `EditorLow`: editor-only gizmos, inspectors, previews, debug views.
5. `Speculative`: optional variants requested by tooling.

`XRStartup` mode implies that all descriptors targeted by the request are
treated as `BlockingCritical` for the duration of the startup blocking budget;
the priority field still drives ordering inside that budget. Other modes use
the descriptor's authored priority directly.

### 6. Schedule Backend Work

The backend warmup service consumes descriptors:

- convert descriptor to `XRRenderProgram` or a renderer-specific program
  request
- call existing link preparation so binary-cache hits avoid source prep
- prefer async binary upload/source compile lanes
- use bounded sync fallback only from the existing upload pump
- keep old lazy render behavior if warmup is canceled or incomplete

For OpenGL, this should reuse the existing `GLRenderProgram.Link(nonBlocking:
true)` path and `PollPendingAsyncPrograms` pump rather than introducing a second
program-build system.

### 7. Validate First Use

Program binary load and source link are not always the end of driver work. Some
drivers still do final work on first bind or first draw.

Warmup policy can request an optional validation step:

- bind the program or separable pipeline
- bind minimal compatible dummy resources
- issue a tiny offscreen draw/dispatch where safe
- mark validation as complete only after the command has passed through the
  render thread

Validation is opt-in per descriptor family and gated by an explicit hazard
deny-list rather than ad-hoc per-driver workarounds at runtime:

- **Compute programs**: bind only, never dispatch. Dispatch requires real
  resource bindings and risks driver state corruption with placeholders.
- **Tessellation programs**: skip validation entirely. First-bind cost is
  unavoidable on most drivers and a dummy patch draw is unsafe.
- **Geometry shaders**: skip on driver fingerprints in the known-bad list
  (recorded alongside the OpenGL binary cache fingerprint metadata).
- **Programs requiring SSBO/UBO bindings before draw**: skip; placeholder
  bindings risk caching false-positive driver work tied to the wrong layout.
- **Stereo/multiview**: validate single-view only; the stereo path is exercised
  through the normal first-frame instead.
- **Single-stage separable programs**: follow the hazard rules in the OpenGL
  program-linking doc; validation must respect the same constraints as live
  rendering.

Descriptors that fall under any deny-list rule record
`ValidationStatus = Skipped` rather than failing.

## Prewarm Modes

| Mode | Behavior |
| --- | --- |
| `EditorBackground` | Build graph and warm at low priority while the editor remains interactive. |
| `PlayModeTransition` | Warm current world and active camera pipelines before entering play. Blocking budget is configurable. |
| `StandaloneLoadingScreen` | Warm critical and visible-high descriptors while a loading screen is active. |
| `XRStartup` | Block on critical descriptors, then continue world-normal work asynchronously. |
| `AssetImportPreview` | Warm descriptors for imported assets, preview materials, and preview pipeline only. |
| `LoadZoneTriggered` | Warm descriptors for a zone-specific asset/world slice when a load-zone trigger activates or a character enters/exits its volume. |
| `ExternalCacheCompiler` | Separate target-local process that loads a world or manifest and populates the same OpenGL binary cache. |
| `DiagnosticsOnly` | Build and save the manifest without scheduling backend work. |

### Pacing Policy

Non-blocking modes (`EditorBackground`, `WorldNormal`, `Speculative`,
`AssetImportPreview`) must run under an explicit pacing budget so they do not
tank the editor or play-mode framerate on large worlds:

```csharp
public sealed record RenderPrewarmPacing(
    int MaxLinksPerFrame,
    double MaxRenderThreadMillisecondsPerFrame,
    int MaxConcurrentSourceCompiles);
```

The backend service derives these values from the existing async link/upload
pump limits and clamps per-frame work accordingly. Blocking modes
(`PlayModeTransition`, `XRStartup`, `StandaloneLoadingScreen`,
`LoadZoneTriggered` with `CriticalBlocking`) bypass pacing for descriptors
marked `BlockingCritical` only.

## Load Zone Component

Large worlds should be able to author shader warmup boundaries directly in the
scene. Add a `ShaderPrewarmLoadZoneComponent` that launches target-local
prewarm work when the zone becomes relevant.

Trigger sources:

- component activation
- component deactivation
- character collision entry
- character collision exit
- explicit script call

The component owns a `RenderPrewarmLoadZoneProfile`:

```csharp
public sealed class RenderPrewarmLoadZoneProfile : XRBase
{
    public string ZoneId { get; set; } = string.Empty;
    public RenderPrewarmTriggerMask Triggers { get; set; }
    public RenderPrewarmMode Mode { get; set; } = RenderPrewarmMode.LoadZoneTriggered;
    public RenderPrewarmPolicy Policy { get; set; } = RenderPrewarmPolicy.DefaultLoadZone;
    public RenderPrewarmRootSet RootSet { get; set; } = new();
    public bool LaunchExternalCompiler { get; set; } = true;
    public bool LoadProgramsAfterCompilerExit { get; set; } = true;
    public bool ValidateFirstUse { get; set; }
    public TimeSpan TriggerDebounce { get; set; } = TimeSpan.FromMilliseconds(250);
}
```

`RootSet` can point at one or more scene roots, prefab assets, world-streaming
chunks, render pipelines, cameras, or explicit materials. If empty, the
component warms its owning scene node subtree plus the active camera pipelines.

### Triggered Workflow

The load-zone workflow is intentionally two-stage:

```text
Load zone trigger
  -> debounce trigger transitions (default 250 ms)
  -> build or load zone prewarm manifest
  -> launch XREngine.ShaderCacheCompiler.exe
  -> compiler creates/refreshes OpenGL program binaries in Build/Cache
  -> compiler exits and writes .complete sentinel after fsync
  -> main app observes process exit AND sentinel before reading result
  -> main app reloads affected binary-cache entries
  -> main app schedules program binary loads
  -> callbacks report per-program and all-program completion
```

The coalescing key is `(ZoneId, ManifestHash, RuntimeFingerprint, EngineBuildId)`.
`EngineBuildId` is required so dev iteration after an engine recompile does not
reuse a stale in-flight job. Repeated enter/exit events that hash to the same
key coalesce onto the existing job; events with a different key cancel the
prior low-priority job (subject to policy) and start a new one.

Trigger storms must be debounced. The default 250 ms debounce window prevents a
player walking back and forth across a portal threshold from launching or
canceling compiler processes per step. Debounce duration is part of the
`RenderPrewarmLoadZoneProfile`.

The main app must not read the compiler's result manifest until both:

1. The compiler process has exited.
2. A `.complete` sentinel file has been written and fsynced next to the result
   manifest.

This closes the race where the compiler still holds a write lock on the binary
cache while the main app tries to load it. If the process exits without a
sentinel, or the result manifest is missing or partial, the session is treated
as Failed and rendering remains on the lazy path. Compiler crashes
(non-zero exit code) follow the same path: missing or partial result manifest
plus non-zero exit → Failed session, no binary adoption.

Deactivation/exit can either cancel low-priority queued work or run an
unload-side profile that prepares the next likely zone, depending on the
authored policy.

The main app must not assume the compiler made every requested binary. It
should reload the cache index or result manifest, then feed available binaries
through the normal backend path. Missing or failed binaries fall back to source
compile/link exactly like ordinary cache misses.

### Process Boundary

The load-zone component must not share GL handles with the compiler process.
The handoff is file-based:

- request manifest path
- compiler exit code
- compiler result manifest path
- result `.complete` sentinel file
- `.bin` and `.bin.json` entries under `Build/Cache/OpenGL/ShaderPrograms/`
- optional `Build/Cache/OpenGL/PrewarmManifests/<zone>.result.json`

The result manifest should list every requested descriptor with:

- descriptor hash
- backend cache key
- binary payload path
- metadata path
- status: `Created`, `AlreadyCached`, `Failed`, `Skipped`, `Canceled`
- failure reason when relevant
- compile/link/binary-capture timings

### Runtime Program Load Callbacks

The main app should expose callbacks at the runtime warmup service level, not
inside the external compiler process:

```csharp
public sealed class RenderPrewarmSession
{
    public event Action<RenderPrewarmProgramLoadResult>? ProgramLoaded;
    public event Action<RenderPrewarmSessionResult>? AllProgramsLoaded;
}

public sealed record RenderPrewarmProgramLoadResult(
    string SessionId,
    string ZoneId,
    string DescriptorHash,
    string? BackendCacheKey,
    RenderPrewarmProgramStatus Status,
    double QueueMilliseconds,
    double LoadMilliseconds,
    string? FailureReason);

public sealed record RenderPrewarmSessionResult(
    string SessionId,
    string ZoneId,
    int RequestedProgramCount,
    int ReadyProgramCount,
    int FailedProgramCount,
    int CanceledProgramCount,
    double TotalMilliseconds);
```

`ProgramLoaded` fires when the main app has accepted or rejected an individual
program load from the backend point of view. For OpenGL that means the program
binary was loaded with `glProgramBinary` and passed link-status validation, or
the source fallback reached ready/failed state.

`AllProgramsLoaded` fires when all descriptors in the session are ready,
failed, skipped, or canceled. It should fire exactly once per session, including
partial or timeout completion.

Both events are raised on the engine's main thread (the same thread that owns
world/scene mutation), not the render thread or a worker. Subscribers may touch
scene/UI state directly. Render-side resource handles are already swapped by
the time `ProgramLoaded` fires; subscribers must not assume the GL context is
current.

Load-zone scripts can use these callbacks to:

- remove loading gates for a doorway/elevator/portal
- raise priority for visible renderers once their programs are ready
- keep a fallback loading screen up until critical descriptors finish
- log a zone-specific warmup summary for profiling

### Runtime Adoption

After the compiler exits, the main app has three choices depending on policy:

1. `LoadAvailableBinariesOnly`: load binaries that exist, report misses, and let
   gameplay continue.
2. `FallbackToSource`: load binaries first, then schedule source compile/link
   for misses or rejected binaries.
3. `CriticalBlocking`: wait for critical descriptors up to a time budget, then
   continue with a partial result.

All adoption still goes through the normal clone-and-swap program lifecycle.
The load-zone path must not replace linked programs in place or bypass uniform
reflection/binding metadata restoration.

### Authoring Guidance

Use load zones for expensive, predictable transitions:

- doors, elevators, teleports, and portals
- world-streaming chunk boundaries
- entering/exiting large interiors
- switching between gameplay and cinematic/render-heavy scenes
- XR transitions where a single hitch is highly visible

Avoid triggering full-world warmup from small overlap volumes. The profile
should target the next relevant slice of the world and leave speculative
background warming to lower-priority editor or loading-screen modes.

## External Cache Compiler

A separate application is useful when it runs on the target machine with the
same driver stack as the main app.

Proposed shape:

```text
XREngine.ShaderCacheCompiler.exe
  --world Assets/Worlds/Sponza.xrworld
  --pipeline DefaultRenderPipeline
  --renderer OpenGL
  --mode Full
  --manifest Build/Cache/OpenGL/PrewarmManifests/Sponza.zone-a.json
  --zone ZoneA
  --validate-first-use
```

The compiler should:

- initialize a hidden/offscreen OpenGL context
- load the same engine settings and shader cache schema
- build the `RenderPrewarmGraph`
- run backend warmup jobs through the normal OpenGL queues
- write program binaries under `Build/Cache/OpenGL/ShaderPrograms/`
- write a manifest and summary under `Build/Cache/OpenGL/PrewarmManifests/`
- return a process exit code and result manifest that the main app can consume
  for load-zone sessions

It should not ship its output as universal content. The OpenGL program-binary
output is valid only for the runtime fingerprint captured by the existing
binary-cache metadata.

### Manifest Portability

The high-level prewarm manifest IS portable across machines that share an
engine build, because descriptors are CPU-derived from world/asset/pipeline
state. The OpenGL program binaries are NOT portable.

This enables a meaningful ship-to-customer flow:

1. CI ships the engine plus prewarm manifests for shipped worlds and load
   zones.
2. On first launch (or after driver/GPU change), the customer machine runs the
   external compiler against the shipped manifests to populate its local
   binary cache.
3. Subsequent launches hit the customer-local binary cache.

Manifests must therefore be reproducible: a deterministic build of the same
world and engine version must produce byte-identical manifest JSON.

## Cache And Invalidation

The world prewarm manifest should have its own schema version. It should include:

- manifest schema version
- engine build/version marker
- render library and backend capability snapshot
- GL runtime fingerprint when available
- world asset id/path and source timestamp/hash
- selected camera and render pipeline identities
- effective pipeline settings that change shader shape
- asset dependency ids and source hashes
- generated variant hashes
- program descriptor hashes
- final backend cache keys when available
- load-zone id and trigger policy when the manifest came from a
  `ShaderPrewarmLoadZoneComponent`

Invalidation rules:

- Shader source/include change invalidates affected shader variants and program
  descriptors.
- Material shader list, Uber state, render pass, transparency mode, or sampler
  layout change invalidates material intents and dependent descriptors.
- Mesh layout/capability changes invalidate generated vertex descriptors.
- Render-pipeline command-chain or feature settings changes invalidate pipeline
  pass descriptors.
- GL fingerprint change does not invalidate the high-level manifest, but it
  invalidates or bypasses backend program binaries through the existing binary
  cache metadata.

The manifest is an accelerator and diagnostic artifact. Runtime correctness must
not depend on it being present.

### Negative Manifest

The manifest also tracks variants that were *requested* but failed to compile,
link, or validate, with structured reasons. This negative manifest is stored
alongside the positive manifest and feeds two workflows:

- The editor surfaces failed variants in the prewarm panel so authors learn
  about driver-incompatible material/pipeline combinations without waiting
  for a player to hit them at runtime.
- The backend skips re-attempting known-failed source compiles for the same
  `(descriptorHash, sourceHash, runtimeFingerprint)` triple, complementing the
  existing negative source-hash cache at the descriptor level.

Negative entries are invalidated by the same rules as positive entries:
shader/material/mesh/pipeline state changes drop the negative record so the
planner retries on the next build.

### Hot-Reload Interaction

If a shader source or material changes while a load-zone session is in flight:

- In-flight backend jobs whose descriptor hash is invalidated by the change
  are canceled. `RenderPrewarmProgramLoadResult.Status = Canceled` with a
  `FailureReason` of `"hot-reload invalidated"`.
- The session's `AllProgramsLoaded` still fires once with the partial result.
- The next build cycle picks up the new descriptors normally.

Never adopt stale binaries that the editor knows are out of date.

## Diagnostics

Add a prewarm log category, ideally `log_prewarm.log`, with events such as:

- `Prewarm.GraphStarted`
- `Prewarm.ProviderFailed`
- `Prewarm.AssetDiscovered`
- `Prewarm.PipelineDiscovered`
- `Prewarm.VariantRequested`
- `Prewarm.ProgramDescriptorCreated`
- `Prewarm.ProgramDescriptorCoalesced`
- `Prewarm.JobQueued`
- `Prewarm.JobReady`
- `Prewarm.JobFailed`
- `Prewarm.LoadZoneTriggered`
- `Prewarm.ExternalCompilerStarted`
- `Prewarm.ExternalCompilerExited`
- `Prewarm.ProgramLoaded`
- `Prewarm.AllProgramsLoaded`
- `Prewarm.ValidationDrawStarted`
- `Prewarm.ValidationDrawCompleted`
- `Prewarm.Summary`

Each program descriptor should report:

- descriptor hash
- program family
- shader stage list
- material name/path/id
- render pass family
- pipeline name
- mesh axes
- variant hash
- priority
- backend cache key if known
- reasons and source nodes

The existing `[ShaderLink]`, `[ShaderBackend]`, `[ShaderGLCall]`,
`[ShaderProgramSummary]`, and profiler stall logs remain the backend truth. The
prewarm log explains why a backend job existed.

## Failure Policy

- A failed provider logs once per graph build and does not abort the whole graph.
- A failed shader variant records a failed variant node and lets runtime lazy
  fallback preserve behavior.
- A failed source hash continues to use the existing negative cache.
- A failed binary load deletes only the affected binary and falls back to source.
- A validation draw failure marks validation failed but does not mark the program
  unavailable if linking succeeded.
- Blocking policies must have timeouts and return a structured partial result.
- A load-zone external compiler failure reports the session failed and leaves
  rendering on the existing lazy path.
- Character collision enter/exit storms coalesce by zone/session key and should
  not spawn unbounded compiler processes.

## Hot-Path Rules

- Graph construction runs outside render submission and visible collection.
- Providers should avoid allocations when called from a render-thread-adjacent
  path, but the intended use is load/setup time, not per-frame work.
- Do not call provider traversal from every frame to keep the manifest fresh.
  Instead, mark graph roots dirty from property changes and rebuild/debounce in
  editor or loading contexts.
- Descriptor structs should use stable strings/ids and pooled builders during
  implementation if graph builds become large.

## Implementation Plan

### Phase 0: Baseline Measurement

Before any provider/planner work, instrument the existing lazy path to capture
a reproducible baseline. Acceptance Signals reference "far fewer first-use
source-link events"; without a recorded baseline that claim is not measurable.

- Add a one-shot instrumentation pass that records, per first-frame after world
  load: count of source compiles, count of binary-cache hits, count of
  binary-cache misses, total render-thread shader-link stall milliseconds,
  and the descriptor-equivalent identity for each event.
- Run on the Unit Testing World and one large imported world. Save results
  under `Build/Logs/<session>/prewarm-baseline.json`.
- Re-run after each subsequent phase to track delta.

### Phase 1: Manifest And Provider Surface

- Add `IRenderPrewarmDependencyProvider`.
- Add `RenderPrewarmDependencyCollector`, graph node/edge records, descriptor
  records, and summary types.
- Implement providers for `SceneNode`, `XRComponent` base forwarding,
  `IRenderPrewarmTransformContributor` infrastructure, `XRAsset` base no-op,
  `XRMaterial`, `XRShader`, `SubMesh`, `XRMeshRenderer`, and `RenderPipeline`.
- Add diagnostics-only graph build for the active editor world.
- Save manifest JSON for inspection.

### Phase 2: Default Pipeline Coverage

- Add provider methods to `RenderPipelineScript` containers and common
  render-pipeline commands.
- Move the hard-coded `WarmFirstRenderShaders()` and
  `WarmDeferredLightingShaders()` entries into pipeline/provider declarations.
- Cover default and V2 pipeline post-processing, deferred lighting, forward
  plus, shadows, transparency, UI, and GI composites.

### Phase 3: Material And Mesh Program Planning

- Convert reachable `XRMaterial` + mesh + pipeline pass intent into program
  descriptors.
- Integrate Uber variant preparation as a graph planning stage.
- Add defined shader variant intents for depth-normal, shadow, point-shadow,
  weighted OIT, PPLL, and depth peeling.
- Deduplicate descriptor hashes and preserve all reasons.

### Phase 4: Backend Warmup Service

- Add `IRenderProgramWarmupBackend`.
- Implement OpenGL backend by feeding descriptors into the existing
  `GLRenderProgram` link/binary-cache path.
- Respect existing queue limits and render-thread sync budgets.
- Add progress reporting and cancellation.
- Add optional first-use validation draws for safe graphics descriptors.

### Phase 5: Tooling

- Add editor UI: current world graph summary, descriptor list, cache hit/miss
  view, slow jobs, failed providers, and "warm selected world" command.
- Add `ShaderPrewarmLoadZoneComponent`, collision/activation triggers,
  zone-root selection, external compiler launch policy, and callback wiring.
- Add `XREngine.ShaderCacheCompiler` or an `ExecTool` entry that can run a
  target-local prewarm pass.
- Add tests for deterministic graph output and descriptor deduplication.
- Add local hardware validation recipes for cold cache, warm cache, corrupted
  cache, stereo, XR startup, and imported large-world cases.

## Acceptance Signals

- Diagnostics-only graph build for Unit Testing World is deterministic across
  repeated runs with unchanged assets.
- Active default pipeline shaders are present in the manifest without relying on
  manual warm lists.
- A large imported world produces far fewer first-use source-link events during
  the first visible frame after prewarm.
- Warm-cache startup reports high binary-cache hit rate and low render-thread
  shader-link stall time.
- Corrupt or stale binary entries fall back to source without breaking rendering.
- XR startup can block on critical descriptors and continue non-critical warmup
  after the first stable frame.

## Open Questions

- Should graph manifests be stored per world asset, per editor session, or only
  under `Build/Cache`?
- What is the right default blocking budget for XR startup, and what default
  values should `RenderPrewarmPacing` ship with for editor background mode?
- Should generated vertex shader descriptors be owned by `XRMeshRenderer`, the
  OpenGL renderer, or a shared renderer-neutral planner?
- How much speculative off-camera content should editor background warmup cover?
- Should the first implementation support a separate cache compiler executable,
  or should it begin as an `ExecTool`/editor command that runs the same code in
  process?
