# Vulkan Shader Object Pipeline Replacement

Status: future design draft

Owner: rendering team (TBD)

Target Vulkan baseline: Vulkan 1.3 + `VK_EXT_shader_object` (native) for the accelerated path. Vulkan 1.4 is not required.

Primary goal: evaluate and stage a later migration from Vulkan graphics/compute `VkPipeline` objects to `VK_EXT_shader_object` where it improves engine architecture, iteration speed, and runtime hitch behavior. The new program-binding architecture must be **runtime-toggleable** against the existing pipeline-object path so the two can coexist and be A/B compared on the same build.

Prerequisite design: [Vulkan Dynamic Rendering Migration](vulkan-dynamic-rendering-migration-design.md).

> Hard dependency: this work must not start until the dynamic-rendering migration is **completed and the default Vulkan graphics path**. As of this draft that prerequisite is itself only a design draft, so this design is blocked. The clean shader-object path depends on dynamic rendering being the only graphics target the renderer has to bind against.

## Summary

`VkPipeline` is not formally deprecated in current Vulkan guidance. It remains valid, performant, and central to many Vulkan applications. The problem is different from `VkRenderPass`: pipelines are not obsolete, but the monolithic pipeline model creates permutation pressure, expensive creation points, and complex prewarm requirements for engines with many materials, render states, and shader variants.

`VK_EXT_shader_object` offers a pipeline-free workflow where shader stages are created as `VkShaderEXT` objects, bound directly with `vkCmdBindShadersEXT`, and paired with dynamic fixed-function state commands. This can reduce pipeline permutation pressure and improve hot-reload/iteration ergonomics, but it requires broader renderer changes than dynamic rendering.

Important scope limit on the benefit: shader objects collapse the **fixed-function render-state** permutation space (blend, depth/stencil, cull, topology, etc.). They do **not** collapse the **shader-variant** permutation space. Specialization constants, `#define`-driven variants, and linked stage groups still multiply, just at `VkShaderEXT` granularity instead of `VkPipeline` granularity. The win should be framed as render-state combinatorial collapse plus better hot-reload ergonomics, not as eliminating shader variants.

Important caveat on the emulation layer: `VK_LAYER_KHRONOS_shader_object` recreates `VkPipeline` objects internally. On a layer-only device the engine keeps essentially all of the permutation pressure and creation hitches and gains only API ergonomics. Because hitch and permutation reduction are the primary goals, the layer path does **not** advance the primary goals; it only helps native-driver GPUs. The layer is therefore an iteration/coverage aid, not a shipping performance path.

This design intentionally comes after the renderpass/framebuffer migration. Dynamic rendering is a prerequisite for the clean shader-object path, and removing `VkRenderPass` first also simplifies the graphics compatibility surface that pipelines currently encode.

## Decision Gate

This migration adds a second program-binding backend and carries a meaningful dual-path tax. Before committing engineering effort past the audit phase, the following gate must be cleared. If it is not, the engine should keep investing in the already-present graphics pipeline library (GPL) path instead.

### Baseline (must be measured first)

Before any backend work, capture the current pipeline-object pain on representative target GPUs (one native shader-object-capable GPU and one GPL-only GPU):

- distinct graphics pipeline permutation count for the Unit Testing World and editor default scene
- first-frame and interactive-camera pipeline creation hitch time (reuse `XRE_VK_TRACE_PIPECREATE` and `profiler-render-stalls.log`)
- prewarm database size and warm-start creation count
- shader hot-reload time

Without this baseline, the Phase 8 "clearly win" decision has nothing to compare against.

### GPL comparison

The engine already has graphics pipeline library support behind `SupportsGraphicsPipelineLibrary`, which targets the same permutation/hitch problem with broader hardware availability today. The gate must explicitly answer:

- Does native shader-object mode beat current GPL on the baseline metrics by a margin that justifies a second permanent backend?
- Is native `VK_EXT_shader_object` available on enough target GPUs that the work helps shipping users, not just dev machines?

If GPL already closes most of the hitch/permutation gap on target hardware, prefer continuing to invest in GPL and keep shader objects as an opt-in editor/iteration feature only.

## External Context

References:

- Vulkan Guide pipeline objects: https://docs.vulkan.org/guide/latest/deprecated.html
- `VK_EXT_shader_object` proposal: https://docs.vulkan.org/features/latest/features/proposals/VK_EXT_shader_object.html
- `VK_EXT_graphics_pipeline_library` proposal: https://docs.vulkan.org/features/latest/features/proposals/VK_EXT_graphics_pipeline_library.html

Important distinction:

- `VkRenderPass` / `VkFramebuffer`: deprecated by dynamic rendering in Vulkan 1.4.
- `VkPipeline`: not formally deprecated, but shader objects are a newer alternative.

## Goals

- Add an explicit Vulkan program binding architecture that can support both pipeline objects and shader objects during migration.
- Make backend selection a **runtime toggle** on a single build, so the pipeline-object and shader-object paths can coexist and be A/B compared without recompiling. Selection must be resolvable per program/pass, not only as a global switch, because the migration is inherently mixed-mode (for example ImGui or compute on pipelines while materials run on shader objects).
- Preserve current Vulkan output and validation behavior while refactoring.
- Reduce or remove graphics pipeline render-state permutation pressure in the long term.
- Improve shader hot-reload by compiling/binding stages without rebuilding every monolithic pipeline permutation.
- Keep render-state changes explicit and tracked.
- Avoid per-frame heap allocations in the shader-object bind path.
- Keep unsupported accelerated paths visible. If shader-object mode is requested and unavailable, fail with a clear diagnostic instead of silently using another path.
- Retain `VkPipeline` for ray tracing pipelines unless a separate ray tracing design replaces them.

## Non-Goals

- Do not start this migration before dynamic rendering is the only Vulkan graphics target path.
- Do not delete the current pipeline-object path until shader-object parity is proven across default rendering, ImGui, compute, meshlets, and tooling scenarios. The runtime toggle and pipeline backend stay in the build through every phase below.
- Do not treat the `VK_LAYER_KHRONOS_shader_object` layer as a shipping performance path; it recreates pipelines internally and only aids coverage/iteration.
- Do not rely on CPU fallback rendering.
- Do not require shader objects for OpenGL.
- Do not rewrite the material system as part of the first shader-object prototype.
- Do not assume the shader-object extension is universally available on every user device.

## Current Pipeline Architecture

### Shader Modules

`Objects/Types/VkShader.cs` currently wraps shader-module creation:

- compiles or rehydrates SPIR-V
- creates `ShaderModule`
- stores descriptor binding metadata
- stores shader identity/fingerprint information
- raises invalidation events

Despite the class name, this is currently a `VkShaderModule` wrapper, not a `VkShaderEXT` shader object.

### Render Programs

`Objects/Types/VkRenderProgram.cs` owns:

- a cache from `XRShader` to `VkShader`
- stage lookup by `EProgramStageMask`
- descriptor set layout creation
- `PipelineLayout`
- graphics pipeline creation through `CreateGraphicsPipeline`
- compute pipeline creation through `CreateComputePipeline`
- graphics and compute pipeline fingerprints

`VkRenderProgramPipeline.cs` mirrors part of this pipeline creation model for program-pipeline style usage.

### Mesh Renderer Pipeline Cache

`Objects/Types/VkMeshRenderer.Pipeline.cs` owns per-mesh/material graphics pipeline selection:

- `PipelineKey`
- `_pipelines`
- optional graphics pipeline library path
- monolithic fallback path
- draw-state dependent pipeline creation
- `PipelineRenderingCreateInfo` for dynamic rendering

The key currently includes shader/program identity, vertex layout, descriptor layout, material layout, pass metadata, feature profile, sample count, depth/stencil state, culling/front-face state, blend state, color write mask, and dynamic-rendering attachment data.

### Command Buffer Binding

`Objects/CommandBuffers.cs` tracks graphics and compute pipeline handles to avoid redundant `CmdBindPipeline` calls.

Current bind model:

- bind `VkPipeline`
- bind descriptor sets against `PipelineLayout`
- push constants against `PipelineLayout`
- bind vertex/index buffers
- draw/dispatch

### Existing Hitch Mitigation

The renderer already has:

- persistent `VulkanPipelineCache`
- semantic `VulkanPipelinePrewarmDatabase`
- graphics pipeline library support behind `SupportsGraphicsPipelineLibrary`
- pipeline creation diagnostics via `XRE_VK_TRACE_PIPECREATE`
- prewarm capture via `XRE_VK_PIPELINE_PREWARM_CAPTURE`

Shader objects should be evaluated against these existing mechanisms, not assumed automatically better.

## Target Architecture

### Program Binding Backend

Introduce an internal abstraction for command-buffer program binding.

```csharp
internal interface IVulkanProgramBindingBackend
{
    EVulkanProgramBindingMode Mode { get; }

    bool EnsureGraphicsProgram(
        VkRenderProgram program,
        in VulkanGraphicsProgramState state,
        out VulkanProgramBinding binding);

    bool EnsureComputeProgram(
        VkRenderProgram program,
        in VulkanComputeProgramState state,
        out VulkanProgramBinding binding);

    void BindGraphics(
        CommandBuffer commandBuffer,
        in VulkanProgramBinding binding,
        in VulkanGraphicsDynamicState state);

    void BindCompute(
        CommandBuffer commandBuffer,
        in VulkanProgramBinding binding);
}
```

Initial backends:

- `VulkanPipelineProgramBindingBackend`: wraps current pipeline-object behavior.
- `VulkanShaderObjectProgramBindingBackend`: creates and binds `VkShaderEXT` objects and emits dynamic state.

This indirection should be added before behavior changes. The first refactor should keep the current pipeline backend as the default and produce no rendering changes.

### Binding Modes

Add an explicit mode:

```csharp
internal enum EVulkanProgramBindingMode
{
    PipelineObjects,
    ShaderObjectsNative,
    ShaderObjectsLayer
}
```

Selection is a runtime toggle, resolved per program/pass rather than as a single global enum value. A global default sets the baseline, but individual passes (ImGui, compute, ray tracing, meshlets) can pin a specific mode while the rest of the frame uses another. This is required because the migration is mixed-mode for most of its life: ray tracing always stays on pipelines, ImGui and compute may stay on pipelines after materials move to shader objects, and the editor may want shader objects only for hot-reload.

Suggested policy:

- default to `PipelineObjects` until shader-object parity is complete
- expose the toggle at runtime (preference + env var) so both backends can be A/B compared on one build without recompiling
- allow `ShaderObjectsNative` only when the device exposes `VK_EXT_shader_object`
- allow `ShaderObjectsLayer` only if the shader-object layer is deliberately packaged and enabled, and treat it as a coverage/iteration aid, not a shipping performance path
- if the user explicitly requests shader objects and support is missing, fail visibly with device/extension diagnostics

Do not silently downgrade an explicitly requested shader-object mode to pipeline objects.

Mixed-mode command recording rule: `vkCmdBindShadersEXT` and `vkCmdBindPipeline` clobber each other's state. When recording switches between a pipeline-bound program and a shader-object-bound program inside one command buffer, the dynamic-state tracker must be fully invalidated so all required dynamic state is re-emitted after the switch. Minimize these transitions by sorting/batching draws by binding mode within a pass where possible, and record the transition count as a diagnostic.

### VkShader Evolution

`VkShader` should become a shader artifact wrapper capable of owning either or both of:

- `ShaderModule` for pipeline-object mode
- `ShaderEXT` for shader-object mode

It should continue to own:

- SPIR-V bytes or rehydrate path
- stage flags
- entry point
- descriptor binding metadata
- push constant usage
- shader identity/fingerprint
- invalidation events

Possible split:

- `VkShaderModuleArtifact`
- `VkShaderObjectArtifact`
- `VkShader` coordinates both

The split should happen only if it reduces complexity. A single wrapper with explicit fields may be simpler during migration.

### Pipeline Layout Still Matters

Shader objects do not remove descriptor set layouts or push constants. The engine still needs a layout contract for:

- descriptor set compatibility
- push constant ranges
- descriptor update templates
- material binding policy
- bindless/resource table layout

Keep `PipelineLayout` initially even in shader-object mode if the binding commands and Silk.NET signatures require it for descriptor sets/push constants. Later, this can be renamed to `ProgramLayout` if the wrapper no longer maps cleanly to Vulkan `VkPipelineLayout`.

### Dynamic Fixed-Function State

Pipeline objects bake a lot of fixed-function state. Shader objects require the renderer to set that state dynamically before draws.

State to inventory and cover:

- primitive topology
- primitive restart
- vertex input bindings/attributes
- viewport
- scissor
- rasterizer discard
- polygon mode
- cull mode
- front face
- depth clamp
- depth bias
- line width
- rasterization samples
- sample mask
- alpha-to-coverage
- color blend enable
- color blend equation/factors
- color write mask
- logic op
- depth test enable
- depth write enable
- depth compare op
- depth bounds
- stencil test enable
- stencil ops
- stencil compare/write masks
- stencil reference

Some of these commands are core, while others come from dynamic-state extensions. The implementation must query and record a capability matrix before shader-object mode is enabled.

Important scoping note: when `VK_EXT_shader_object` is supported natively, the extension already requires the dynamic-state functionality from `VK_EXT_extended_dynamic_state`, `VK_EXT_extended_dynamic_state2`, and the vertex-input dynamic state as part of enabling the feature. So for the native path the capability matrix is much smaller than the full list above suggests; most basic states are guaranteed. The real residual risk concentrates in the `VK_EXT_extended_dynamic_state3` family (per-attachment blend equations, polygon mode, rasterization samples, alpha-to-coverage, color write mask, depth clamp, etc.), which exposes many individually-optional feature bits, plus a few other optional states. The audit should focus its gap analysis there rather than re-validating states the native feature already mandates.

Relevant extension families to evaluate:

- `VK_EXT_shader_object`
- `VK_EXT_extended_dynamic_state`
- `VK_EXT_extended_dynamic_state2`
- `VK_EXT_extended_dynamic_state3`
- `VK_EXT_vertex_input_dynamic_state`
- `VK_EXT_color_write_enable`
- `VK_EXT_depth_clip_control`

The engine should not assume an EDS3-class state is available just because one development GPU supports it.

### State Tracking

Shader-object mode moves work from pipeline creation to command recording. The command recorder must avoid redundant dynamic-state emission.

Add a tracked state block:

```csharp
internal struct VulkanGraphicsDynamicState
{
    public PrimitiveTopology Topology;
    public VulkanVertexInputSignature VertexInput;
    public CullModeFlags CullMode;
    public FrontFace FrontFace;
    public SampleCountFlags Samples;
    public bool DepthTestEnabled;
    public bool DepthWriteEnabled;
    public CompareOp DepthCompareOp;
    public bool StencilTestEnabled;
    public StencilOpState FrontStencil;
    public StencilOpState BackStencil;
    public VulkanColorBlendSignature ColorBlend;
    public uint ViewMask;
}
```

State tracker rules:

- compare structs by value
- emit only changed state
- do not allocate arrays per draw
- cache vertex input descriptions by mesh vertex layout
- cache color blend signatures by render state class
- invalidate state on command buffer reset and when switching between backend modes

### Render State Keys

In pipeline-object mode, `PipelineKey` combines shader identity, render state, attachment formats, material layout, and vertex layout because all of that affects the `VkPipeline`.

In shader-object mode, split this into:

- `ShaderObjectKey`: shader stage identities, entry points, specialization constants, linked/unlinked mode.
- `ProgramLayoutKey`: descriptor set layouts and push constants.
- `RenderStateKey`: dynamic fixed-function state needed for state emission and batching.
- `AttachmentSignature`: dynamic rendering color/depth/stencil formats, samples, and view mask.
- `VertexInputSignature`: binding and attribute layout.

This split lets the CPU and GPU sorting layers reason about render-state class without implying a pipeline object exists.

### Linked Shader Objects

`VK_EXT_shader_object` allows shader objects to be linked at creation time. The engine should support both:

- linked stage groups for production performance
- unlinked stage objects for faster iteration or partial hot reload

Initial policy:

- prototype unlinked or minimally linked vertex+fragment first
- add linked stage groups once correctness is proven
- record whether linked/unlinked mode affects performance and hitching

The shader identity must include linked-mode decisions so caches and diagnostics remain deterministic.

### Compute

Compute pipelines are also `VkPipeline` objects today. Shader object support can include compute, but compute should not be the first production milestone unless graphics parity is already stable.

Recommended order:

1. graphics direct draw path
2. ImGui graphics path
3. compute dispatch path
4. mesh/task shader path

Compute has fewer fixed-function states, so it may be easier technically, but graphics is where permutation pressure is highest.

### Mesh And Task Shaders

Meshlet rendering uses mesh/task shader stages and indirect dispatch/draw flows. Shader-object support must explicitly validate:

- task shader only
- mesh shader only
- task+mesh
- mesh/task plus fragment
- generated draw parameters
- indirect count dispatch path

Do not route meshlet production paths through shader-object mode until validation covers them.

### ImGui

ImGui currently owns a dedicated graphics pipeline and shader modules. It is a good mid-stage migration target because:

- state is simple
- shaders are fixed
- descriptor layout is simple
- visual validation is obvious

It is not the first target because its path is special and may hide problems the material path must solve.

### Ray Tracing

Ray tracing uses ray tracing pipeline APIs. This design does not replace ray tracing pipelines.

`VkPipeline` may remain in:

- `VK_KHR_ray_tracing_pipeline`
- `VK_NV_ray_tracing`
- any future pipeline APIs without shader-object equivalents

Source tests should distinguish graphics/compute pipeline-object removal from ray tracing pipeline usage.

## Implementation Plan

### Phase 0: Capability And Binding Audit

1. Confirm Silk.NET Vulkan binding coverage for:
   - `VkShaderEXT`
   - `vkCreateShadersEXT`
   - `vkDestroyShaderEXT`
   - `vkCmdBindShadersEXT`
   - required dynamic-state commands
2. Add device capability queries for `VK_EXT_shader_object`.
3. Add dynamic-state extension capability queries, focusing gap analysis on the EDS3 family rather than states the native feature already mandates.
4. Add startup diagnostics listing shader-object readiness:
   - native extension available
   - layer available, if intentionally supported
   - missing dynamic-state requirements
   - unsupported stages
5. Inventory all current graphics pipeline state fields and map each to a dynamic command or blocking gap.
6. Capture the Decision Gate baseline (pipeline permutation count, creation hitch, prewarm size, hot-reload time) on a native shader-object GPU and a GPL-only GPU. This baseline is the comparison target for Phase 8 and feeds the GPL-vs-shader-object gate.

Exit criteria:

- The engine can report whether shader-object mode is possible on the current GPU without changing rendering behavior.
- The Decision Gate baseline is recorded, and the GPL-vs-shader-object gate has an explicit go/no-go answer before backend work proceeds.

### Phase 1: Backend Interface, Pipeline Behavior Preserved

1. Introduce `IVulkanProgramBindingBackend`.
2. Move existing pipeline creation/bind logic behind `VulkanPipelineProgramBindingBackend`.
3. Keep `PipelineObjects` as the only enabled mode.
4. Preserve current pipeline cache and prewarm behavior.
5. Add tests that prove the refactor still calls the pipeline backend for graphics and compute.

Exit criteria:

- No visual change.
- Existing Vulkan tests pass.
- Pipeline miss diagnostics remain intact.

### Phase 2: Dynamic State Model

1. Extract current draw state into `VulkanGraphicsDynamicState`.
2. Add state diffing/emission helpers.
3. Make pipeline-object mode optionally use the same state model for diagnostics and batching identity.
4. Add source tests to catch heap allocations in hot state construction where practical.
5. Validate that render-state classes used by GPU-driven submission map to this state model.

Exit criteria:

- Render-state classification exists independently of `VkPipeline`.
- Pipeline-object mode still renders correctly.

### Phase 3: Minimal Shader Object Graphics Prototype

Scope:

- vertex + fragment
- direct mesh draws
- no tessellation
- no geometry
- no mesh/task
- no compute
- no secondary command buffers unless dynamic inheritance is already proven

Tasks:

1. Create `VkShaderEXT` objects from existing SPIR-V.
2. Bind shaders with `CmdBindShadersEXT`.
3. Bind descriptor sets and push constants using the existing layout contract.
4. Emit all required dynamic state for the simple path.
5. Draw using existing vertex/index buffer binding.
6. Gate behind an explicit runtime toggle (preference + env var) consistent with existing Vulkan flags, for example `XRE_VK_PROGRAM_BINDING=ShaderObjectsNative` alongside the existing `XRE_VK_TRACE_PIPECREATE` / `XRE_VK_PIPELINE_PREWARM_CAPTURE` style.

Exit criteria:

- A simple opaque scene renders through shader objects.
- Missing extension/state produces a startup error in shader-object mode.
- Pipeline-object mode remains unchanged.

#### Geometry And Tessellation Stages

Geometry and tessellation stages are excluded from Phase 3 but must not be silently dropped. Before Phase 4 exit, inventory whether the engine's shipping material/pass set actually uses geometry or tessellation stages on Vulkan:

- if used, fold them into Phase 4 pass coverage and add explicit shader-object validation for tessellation control/evaluation and geometry stages (these stages have their own `VkShaderEXT` stage bits and linking constraints)
- if unused on Vulkan, mark them out of scope here and require that any program declaring those stages stays pinned to the pipeline-object backend with a clear diagnostic

Do not let a program with a geometry/tessellation stage silently route through an unvalidated shader-object path.

Extend shader-object mode to:

- deferred opaque
- forward opaque
- masked
- transparent
- depth-only/shadow
- post-process fullscreen draws
- generated shader variants
- material descriptor updates
- push constants
- color write masks
- blend variants
- stencil variants

Exit criteria:

- Default render pipeline can render a representative Unit Testing World in shader-object mode.
- Shader hot reload invalidates and recreates only affected shader objects where possible.

### Phase 5: ImGui And Compute

1. Port ImGui to shader objects.
2. Port compute dispatches if native shader-object support and dynamic state requirements make it worthwhile.
3. Preserve compute pipeline path as fallback until all compute programs are validated.
4. Keep ray tracing pipeline code untouched.

Exit criteria:

- Editor UI renders in shader-object mode.
- Compute passes used by the default pipeline run correctly or explicitly remain pipeline-backed by policy.

### Phase 6: Meshlet And GPU-Driven Paths

1. Validate task/mesh shader object creation and binding.
2. Validate indirect mesh/task dispatch paths.
3. Ensure GPU-driven render-state classes map to shader-object state binding.
4. Measure CPU command recording overhead versus pipeline-object mode.

Exit criteria:

- Meshlet rendering path can run in shader-object mode without CPU readback or material fallback.

### Phase 7: Cache And Prewarm Redesign

Shader-object mode changes what needs prewarming.

Replace or repurpose:

- pipeline prewarm entries -> shader object prewarm entries
- pipeline cache miss summary -> shader object creation summary
- graphics pipeline library cache -> linked shader object cache, if useful

Track:

- shader object creation count
- linked group creation count
- dynamic-state bind count
- redundant state emissions skipped
- first-frame shader creation time
- hot reload time

Exit criteria:

- Warm startup has no unexpected shader-object creation in the render loop.
- Diagnostics explain any remaining creation hitches.

### Phase 8: Default Mode Decision

Only after parity and performance evidence:

1. Compare pipeline-object and shader-object mode on target GPUs against the Phase 0 baseline.
2. Compare cold start, warm start, shader reload, frame time stability, command recording cost, and VR frame pacing.
3. Choose one:
   - keep pipeline objects as default
   - make shader objects default on capable devices
   - use shader objects only for editor/hot-reload mode
   - use shader objects only for selected pass families

Pre-committed quantitative gates (tune the exact thresholds during Phase 0, but commit to numbers before Phase 8 so "clearly win" is not subjective). Shader objects may become a default only if, versus the pipeline-object baseline on target GPUs, all hold:

- no p99 frame-time regression beyond an agreed budget on the VR two-view worst case (VR frame pacing is the primary acceptance metric, not just a measurement)
- first-frame / interactive pipeline-creation hitch reduced by an agreed margin
- command-recording CPU time within an agreed budget of pipeline-object mode
- shader hot-reload time improved or at parity
- native `VK_EXT_shader_object` coverage across target GPUs is high enough that the change helps shipping users

If the gates are not met, keep pipeline objects (or GPL) as default and demote shader objects to an opt-in editor/iteration feature.

Dual-backend deadline: maintaining both backends doubles the surface area for every future Vulkan change. Set an explicit decision deadline (a milestone, not "someday") by which Phase 8 must resolve to a single default per device class, so the dual-path state does not become a permanent tax. The pipeline backend still remains in the build as the toggleable fallback per the Non-Goals, but it must stop being a co-equal default.

Do not delete the pipeline-object backend unless shader objects clearly win and support coverage is acceptable.

### Phase 9: Optional Pipeline Object Removal

If shader-object mode becomes the default production path:

- delete graphics pipeline object creation from material/direct draw paths
- keep ray tracing pipeline usage
- keep compatibility shims only where explicitly supported
- update docs and tests to reflect the new normal path

Exit criteria:

- Normal graphics command recording does not call `CreateGraphicsPipelines` or `CmdBindPipeline`.
- Source tests protect that invariant.
- Pipeline-object backend is either deleted or clearly labeled as a compatibility backend.

## Validation Plan

### Static Tests

Add source tests for:

- shader-object capability query exists
- explicit binding mode exists
- requested shader-object mode cannot silently fall back
- shader-object mode uses `CmdBindShadersEXT`
- pipeline-object mode still uses `CmdBindPipeline`
- ray tracing pipeline exceptions are explicitly allowed
- render-state key exists independently of pipeline key
- hot path avoids obvious LINQ/closure allocation patterns

### Runtime Tests

Run both modes while both exist:

```powershell
dotnet build .\XREngine.Editor\XREngine.Editor.csproj
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter Vulkan
```

Because backend selection is a runtime toggle, every runtime scenario below runs under each enabled mode: `PipelineObjects`, `ShaderObjectsNative`, and (if the layer is a sanctioned config) `ShaderObjectsLayer`. The native-vs-layer axis matters because the layer recreates pipelines internally and can diverge behaviorally; layer runs are a coverage aid, not a performance comparison.

Runtime scenarios:

- editor default scene
- editor `--unit-testing`
- generated material variants
- shader hot reload
- deferred GBuffer
- forward pass
- transparent pass
- post-process fullscreen pass
- ImGui
- compute passes
- meshlet path
- GPU-driven indirect path
- swapchain resize
- material invalidation
- descriptor layout change

### Performance Measurements

Compare against pipeline-object baseline:

- cold start time
- warm start time
- first-frame hitch time
- shader hot-reload time
- command recording CPU time
- GPU frame time
- pipeline/shader creation count during interactive camera movement
- dynamic-state command count
- redundant state emission skip count
- VR frame pacing where applicable

Shader objects are not automatically faster. The design should be accepted only if it improves architecture or measured performance enough to justify the added backend complexity.

## Diagnostics

Add diagnostics:

- `Vulkan.ProgramBinding.Mode`
- `Vulkan.ShaderObject.Capability`
- `Vulkan.ShaderObject.Create`
- `Vulkan.ShaderObject.Bind`
- `Vulkan.ShaderObject.MissingDynamicState`
- `Vulkan.ShaderObject.FallbackDenied`
- `Vulkan.DynamicState.Emit`
- `Vulkan.DynamicState.Skip`
- `Vulkan.ShaderObject.HotReload`

Diagnostics should include:

- program name
- shader stage set
- shader identities/fingerprints
- linked/unlinked mode
- descriptor layout signature
- push constant signature
- render-state signature
- attachment signature
- device extension support
- missing capability names
- binding-mode transition count within a command buffer (mixed-mode cost)

High-frequency diagnostics must be throttled or hidden behind explicit trace flags. Gate them with the existing `XRE_VK_*` env-var trace convention (for example `XRE_VK_TRACE_SHADEROBJECT`, mirroring `XRE_VK_TRACE_PIPECREATE`) rather than introducing a separate flag style, so trace gating stays consistent across the Vulkan backend.

## Risks And Mitigations

| Risk | Mitigation |
|------|------------|
| Extension support is not universal | Keep pipeline backend until support data justifies a default change. |
| Emulation layer gives no hitch/permutation benefit | Treat `VK_LAYER_KHRONOS_shader_object` as a coverage/iteration aid only; never count it toward the primary goals or use it as a shipping perf path. |
| GPL already closes most of the gap on target GPUs | Clear the Decision Gate (baseline + GPL comparison) before backend work; keep shader objects opt-in if GPL wins. |
| Dual backend doubles Vulkan maintenance surface | Set an explicit Phase 8 decision deadline; demote one path from co-equal default once gates resolve. |
| Missing dynamic-state support blocks parity | Build a device capability matrix before enabling shader-object mode; focus on EDS3-class optional states since native shader objects mandate the rest. |
| Mixed-mode bind/state clobber causes corruption | Fully invalidate the dynamic-state tracker on `CmdBindShadersEXT`/`CmdBindPipeline` transitions and batch by binding mode. |
| Command recording gets slower due to state emission | Add state diffing and measure dynamic-state command counts. |
| Shader object layer adds deployment complexity | Treat layer use as explicit packaging work, not an invisible fallback. |
| Descriptor/push-constant layout assumptions break | Keep the existing layout contract during initial migration. |
| Mesh/task shaders have driver-specific issues | Validate meshlet paths late and keep pipeline backend available. |
| Hot reload invalidates too much | Split shader object identity from render-state identity. |
| Pipeline prewarm diagnostics become obsolete before replacement exists | Add shader-object creation diagnostics before disabling pipeline prewarm. |
| Ray tracing pipelines are accidentally caught by source tests | Explicitly exclude ray tracing pipeline APIs from graphics/compute replacement checks. |

## Acceptance Criteria

The shader-object migration is complete only if the chosen production policy is explicit and validated.

If shader objects become default:

- graphics command recording binds shader objects with `CmdBindShadersEXT`
- normal graphics paths do not create or bind `VkPipeline`
- all required fixed-function state is emitted dynamically
- shader hot reload does not require rebuilding unrelated render-state permutations
- first-frame and warm-frame hitch diagnostics are equal or better than pipeline-object mode
- Unit Testing World and editor default startup render correctly under validation
- ImGui and post-process paths render correctly
- meshlet/GPU-driven paths either work or are explicitly configured to use a supported backend
- ray tracing pipeline usage remains documented as an exception

If pipeline objects remain default:

- the backend abstraction still documents why
- shader-object mode remains an opt-in experiment or editor feature
- pipeline cache/prewarm docs stay current

## Files Expected To Change

Likely implementation touchpoints:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/LogicalDevice.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Extensions.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkShader.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgram.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgramPipeline.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Pipeline.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Drawing.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanPipelineCache.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanPipelinePrewarmDatabase.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanRenderer.ImGui.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanRenderer.Meshlets.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanFeatureProfile.cs`
- `XREngine.UnitTests/Rendering/*Vulkan*`
- `docs/architecture/rendering/vulkan-renderer.md`
- `docs/work/todo/vulkan.md`

Potential new files:

- `VulkanProgramBindingBackend.cs`
- `VulkanPipelineProgramBindingBackend.cs`
- `VulkanShaderObjectProgramBindingBackend.cs`
- `VulkanGraphicsDynamicState.cs`
- `VulkanShaderObjectCapabilities.cs`
- `VulkanShaderObjectDiagnostics.cs`

## Modern Follow-On Opportunities

Shader objects remove the monolithic pipeline as the unit of binding, which is the same direction modern GPU-driven renderers move in. Once the program-binding backend exists and dynamic rendering is the default, the following modern capabilities become natural follow-ups toward a fully GPU-driven, bindless, ray-traced renderer. Each is its own scoped effort behind a capability query and the shared `XRE_VK_*` runtime toggle, must keep the "no silent fallback" rule, must avoid per-frame heap allocations in the hot path, and must not regress the Vulkan 1.3 baseline. None are required to finish the shader-object work; they are the roadmap it enables.

These items complement, and should reuse, the capability-tier query, runtime-toggle convention, and per-program/per-pass selection model from the dynamic-rendering design rather than re-implementing them.

### Bindless Resources And Descriptor Buffers

The largest modern win for material/draw scaling. Decouples resource access from per-draw descriptor-set binding so the GPU can index resources freely.

- Descriptor indexing (core in Vulkan 1.2): large runtime-indexed, partially-bound, update-after-bind descriptor arrays for textures/buffers/samplers. Foundation for a bindless material model and for GPU-driven draws that select their own resources.
- `VK_EXT_descriptor_buffer` — descriptors live in plain buffers addressed by offset instead of descriptor pools/sets; dramatically cheaper descriptor management and a much better fit for shader objects than classic descriptor-set binding. Strong long-term target for the program-binding backend.
- `VK_EXT_mutable_descriptor_type` — fewer distinct descriptor layouts by allowing a slot to hold different descriptor types; simplifies a unified bindless table.
- `VK_KHR_buffer_device_address` (core 1.2) — pass raw GPU pointers in push constants / buffers and dereference them in shaders; enables pointer-based scene/material/geometry access and is a prerequisite for ray tracing and device-generated commands.

A bindless table plus buffer device address lets material binding collapse to "bind one global descriptor table once, index per draw," which pairs naturally with shader objects and dynamic state.

### GPU-Driven Rendering

With bindless + buffer device address in place, move draw submission onto the GPU:

- `vkCmdDrawIndexedIndirectCount` / multi-draw indirect — the GPU produces draw arguments and counts; CPU stops issuing per-object draws. The engine already has GPU-driven plumbing (`EVulkanGpuDrivenProfile`); this extends it to the shader-object path.
- `VK_EXT_device_generated_commands` (DGC) — the GPU generates entire command sequences, including shader/pipeline switches, with minimal CPU involvement. DGC explicitly supports shader objects, so it composes well with this design and is a major CPU-submission-cost reduction for large scenes.
- GPU culling/LOD: frustum, occlusion (the profile already models occlusion), and LOD selection in compute, feeding indirect draws. Keep accelerated paths visible and avoid silent CPU fallback, consistent with engine policy.

### Mesh And Task Shaders

`VK_EXT_mesh_shader` replaces the vertex/geometry/tessellation front-end with task + mesh stages that emit meshlets directly, ideal for GPU-driven culling at meshlet granularity. The shader-object design already stages mesh/task shaders in Phase 6; modern follow-ups:

- meshlet-level GPU culling (cone/frustum/occlusion) in the task stage
- per-meshlet LOD
- amplification for procedural/instanced geometry
- pairing meshlets with bindless geometry via buffer device address

Mesh shaders plus shader objects plus DGC is the modern high-end submission path.

### Ray Tracing And Hybrid Rendering

Ray tracing pipelines stay as `VkPipeline` (explicitly out of scope for shader-object replacement), but a modern renderer should still expose them as an additive path:

- `VK_KHR_acceleration_structure` + `VK_KHR_ray_tracing_pipeline` — full ray tracing pipelines (shadows, reflections, GI, AO).
- `VK_KHR_ray_query` — inline ray queries from graphics/compute shaders, so hybrid effects (RT shadows/AO/reflections in a raster pass) do not need a separate RT pipeline; works from ordinary fragment/compute programs.
- `VK_KHR_deferred_host_operations` — multi-threaded acceleration-structure builds.
- `VK_KHR_ray_tracing_position_fetch`, `VK_KHR_ray_tracing_maintenance1`, opacity micromaps (`VK_EXT_opacity_micromap`), and shader-execution-reordering (`VK_NV_ray_tracing_invocation_reorder`) as later quality/perf refinements.

Ray query is the most pragmatic first step because it layers onto existing raster passes without a new pipeline type.

### Pipeline / Shader Binary Caching

- `VK_KHR_pipeline_binary` — explicit, app-managed pipeline/shader binaries for deterministic, portable on-disk caching and faster cold start; a modern complement to the existing `VulkanPipelineCache` and to shader-object creation diagnostics. Useful regardless of whether pipelines or shader objects win the Phase 8 decision.

### Compute And ML Acceleration

- `VK_KHR_cooperative_matrix` (and the newer cooperative-vector path) — hardware matrix-multiply primitives for in-engine ML inference: neural denoisers, DLSS/FSR-style upscalers, neural materials, and ML-driven animation. Increasingly central to "modern renderer" feature sets.
- `VK_KHR_compute_shader_derivatives`, `VK_KHR_shader_maximal_reconvergence`, `VK_KHR_workgroup_memory_explicit_layout`, `VK_KHR_shader_clock` — compute-quality and profiling primitives that help GPU-driven culling and ML kernels.

### Shared Plumbing

To avoid divergence, the bindless table, buffer-device-address scene model, capability-tier query, and runtime-toggle/diagnostic conventions used by these follow-ups should be defined once and shared with the dynamic-rendering design's follow-on roadmap. The shader-object program-binding backend should be the single seam through which pipeline-object, shader-object, and (later) device-generated-command submission are selected per program/pass.

## Open Questions

- Should shader-object mode be editor-first, production-first, or only an opt-in research backend?
- Should compute move to shader objects before or after graphics parity?
- Should the engine package `VK_LAYER_KHRONOS_shader_object`, or require native driver support only?
- Should shader objects replace graphics pipeline libraries entirely, or should GPL remain the default for GPUs where it performs better? (Decided at the Decision Gate using the Phase 0 baseline; this design does not assume shader objects win.)
- What is the minimum dynamic-state capability profile XRENGINE is willing to require for Vulkan shader-object mode?
- Should `PipelineLayout` be renamed to `ProgramLayout` after shader-object mode is stable?
- Should GPU-driven material state classes be redesigned around shader-object render-state keys before or after shader objects land?

