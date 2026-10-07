# CPU Direct Fast Path TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Mesh Submission Strategies](../../../../architecture/rendering/mesh-submission-strategies.md), [XRDataBuffer RHI Write Model](../../../../architecture/rendering/xrdatabuffer-rhi-write-model.md)
Validation: [GPU-Driven Submission Validation](../../../testing/rendering/gpu-driven-submission-validation.md), [XRDataBuffer RHI Write Model Validation](../../../testing/rendering/xrdatabuffer-rhi-write-model-validation.md)

## Current State
`CpuDirect` remains the correctness baseline and the fallback for unsupported accelerated paths. Workstream 01 recorded measurement, attribution, manifests, and gates. Workstream 02 recorded primary reuse and invalidation. `XRDataBuffer` now has writer scopes, dirty ranges, persistent-ring and upload allocator types, readback tickets, and device-address telemetry. The open work is backend-neutral CPU submission cost, upload reuse, state caching, warmup, and test coverage.

## Open Code Items

### Command handoff and allocations
- [ ] Build visible collection into stable command buffers without copying full command payloads. Rendering collection and submission code. Done when: render submission consumes stable command storage.
- [ ] Preallocate per-frame command, state-key, and pass scratch storage. Submission hot paths. Done when: representative frames reuse storage.
- [ ] Replace LINQ in render submission with explicit loops. Rendering submission code. Done when: hot paths do not allocate enumerators or closures.
- [ ] Replace captured callbacks with cached delegates or static helpers. Rendering submission and profiler hooks. Done when: per-frame callback allocation is absent.
- [ ] Replace boxing-prone counters and enum log payloads with typed structs or pooled records. Stats and diagnostics. Done when: counter logging does not box.
- [ ] Replace `foreach` over class enumerators in hot paths with index loops or struct enumerators. Rendering submission code. Done when: no class enumerator allocation remains in hot paths.
- [ ] Make profiler labels stable strings or interned identifiers. Profiler labels in rendering code. Done when: no per-frame string concatenation creates labels.
- [ ] Add source-contract tests for known no-allocation hot-path methods. `XREngine.UnitTests/Rendering/`. Done when: tests fail on managed allocation in the selected methods.

### Object constants and data separation
- [ ] Define a stable per-object constant block layout. Rendering constants and shader records. Done when: transform ID, material ID, previous transform ID, skin ID, flags, masks, and editor ID have one documented layout.
- [ ] Separate per-object, per-material, per-camera, and per-pass data. Rendering data publication. Done when: per-draw uploads are minimized.
- [ ] Upload object constants through dirty ranges. Upload paths. Done when: full-scene rebuilds are not used for ordinary dirty updates.
- [ ] Bind per-pass object constant buffers once where possible. Backend binding code. Done when: per-draw bind churn is reduced.
- [ ] Keep per-material state in material tables or stable material buffers where supported. CPU direct and material table code. Done when: CPU direct does not force per-draw material uploads unnecessarily.
- [ ] Keep Vulkan buffer and descriptor topology stable across value-only updates. Vulkan binding code. Done when: ordinary frame changes use frame slots or dynamic offsets.
- [ ] Add counters for object constant bytes, dirty ranges, and constant-buffer binds. Stats and profiler capture JSON. Done when: each counter appears in captures.
- [ ] Validate static, skinned, blendshape, instanced, shadow, velocity, editor ID, and override pass consumers. `XREngine.UnitTests/Rendering/` or source contracts. Done when: each consumer accepts the layout.

### Persistent uploads and state cache
- [ ] Verify the OpenGL persistent-mapped ring buffer path for per-frame dynamic uploads. OpenGL buffer code. Done when: dynamic upload routes use fence-protected slots where supported.
- [ ] Verify Vulkan frame-indexed, persistently mapped upload arenas. Vulkan buffer code. Done when: stable descriptor bindings advance offsets or slots instead of recreating resources.
- [ ] Use fence sync or timelines to prevent overwriting GPU-visible ranges. Upload allocators. Done when: slot reuse waits on the correct completion signal.
- [ ] Provide fallback paths for drivers without persistent mapping. Backend buffer routes. Done when: fallback is visible and safe.
- [ ] Route transforms, previous transforms, bone matrices, blendshape weights, object constants, and small pass constants through the upload allocator where appropriate. Rendering data upload code. Done when: eligible buffers use the allocator.
- [ ] Avoid steady-state `glBufferSubData` except documented fallback paths. OpenGL renderer. Done when: production steady frames use ring or allocator routes.
- [ ] Update Vulkan dirty subranges of capacity-backed buffers. Vulkan buffer code. Done when: logical element-count changes do not recreate backing allocations unless capacity is exceeded.
- [ ] Add upload allocator counters. Stats and profiler. Done when: bytes reserved, bytes committed, wraps, stalls, fence waits, fallback events, and high-water mark are visible.
- [ ] Grow capacity only at safe generation boundaries. Buffer ownership code. Done when: old backing storage remains alive until last timeline use completes.
- [ ] Build compact state keys and caches for programs, VAOs, buffers, textures, descriptors, and render state. CPU direct submission code. Done when: redundant binds are skipped and counted.
- [ ] Sort opaque CPU-direct commands where pass semantics allow it. CPU direct submission code. Done when: opaque sorting does not affect transparent, UI, overlay, or diagnostic order.
- [ ] Add Vulkan equivalents for avoided pipeline, descriptor, vertex, index, dynamic-offset, and push-constant state changes. Vulkan renderer. Done when: repeated state changes are counted and skipped.
- [ ] Add tests or source-contract checks for transparent order preservation. `XREngine.UnitTests/Rendering/`. Done when: sorting cannot reorder transparent draws.

### Warmup boundaries
- [ ] Ensure world shader prewarm includes CPU direct variants for static, skinned, blendshape, instanced, shadow, depth, velocity, editor, forward, deferred, and override passes. Shader prewarm code. Done when: measured scenes do not link these programs during render.
- [ ] Prepare material table rows and texture residency before measured interactive frames where possible. Material and texture systems. Done when: known scene rows and textures are ready before first measured frame.
- [ ] Keep model import and cooked-cache work out of render submission. Asset and rendering boundaries. Done when: render submission does not call importer or cooker work.
- [ ] Bound texture upload budgets. Texture upload scheduler. Done when: texture upload cannot consume the whole frame.
- [ ] Surface missing warmup variants in editor diagnostics. Editor diagnostics. Done when: late variants warn instead of silently compiling in render.
- [ ] Add profiler events for startup, warmup, steady-state, and streaming. Profiler code. Done when: captures separate these phases.

## Decisions Needed
- [ ] Choose which no-allocation source-contract tests are stable enough for CI. Owner: rendering lead.

## Out Of Scope
- Zero-readback GPU-driven promotion.
- Shader quality changes and default pipeline GPU pass cost.
