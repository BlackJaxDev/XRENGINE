# Resolved Shader Source Optimization TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Uber Shader Varianting](../../../architecture/rendering/uber-shader-varianting.md)
Validation: [Default And Advanced Pipeline Validation](../../testing/rendering/default-and-advanced-pipeline-validation.md)

## Current State

`ResolvedShaderSource`, `ResolvedShaderSourceOptimizer`, `XRShader.TryGetOptimizedSource(...)`, optimizer identity in `XRRenderProgramDescriptor`, `RenderDiagnosticsFlags.ShaderSourceOptimizerEnabled`, and `XRE_SHADER_SOURCE_OPTIMIZER=0` exist. `ShaderUiManifest` parses mutability modes, and initial tests cover resolved-source optimization, static literal folding, keep annotations, pinned layout-bound resources, and shared snippet resolution.

## Open Code Items

### Source Pipeline Ownership

- [ ] Audit every runtime path that can compile shader text. `XRShader`, `XRRenderProgramDescriptor`, `XRRenderProgram`, `GLShader`, `GLRenderProgram`, Vulkan shader tooling, editor tools, prewarm paths, inline shaders, generated shaders, compute shaders, and post-process shaders. Done when: each path is classified as raw, resolved, optimized, or backend-transformed.
- [ ] Make all remaining compile paths pass through `ResolvedShaderSource` or an explicit documented exception. Done when: no shader family bypasses the optimizer by accident.
- [ ] Keep `UberShaderVariantBuilder` responsible for material intent only. Done when: generic pruning no longer depends on uber-only helpers.
- [ ] Move generally valid static uniform stripping, literal inlining, static-if pruning, and dead-code pruning into the generic optimizer. Done when: non-uber shaders can use the same cleanup.

### Static Properties And Scanner

- [ ] Build optimizer options from resolved shader manifests and program or pass descriptors. Done when: non-uber optimization can consume static property values.
- [ ] Treat `material-static`, `pass-static`, `engine-static`, and `debug-static` values as source identity axes when they affect optimized text. Done when: descriptor identity changes with effective optimized source.
- [ ] Preserve authored material parameters when optimized reflection no longer exposes folded uniforms. Done when: editor and serialized material data do not lose parameters.
- [ ] Add diagnostics when a static property cannot be folded safely. Done when: users can see the property and reason.
- [ ] Replace regex-only reachability with a conservative GLSL scanner. Done when: comments, strings, preprocessor lines, declarations, resources, structs, constants, globals, and functions are tokenized conservatively.
- [ ] Preserve unknown preprocessor regions and keep/interface roots. Done when: ambiguous code fails open.
- [ ] Preserve stage interfaces, layout-bound resources, transform feedback roots, engine reflection roots, overloads, and forward declarations when ambiguity exists. Done when: optimization does not break linkage or reflection.

### Pruning And Diagnostics

- [ ] Root pruning from `main` plus explicit extra roots. Done when: reachable helpers stay and unreachable helpers are removed only when safe.
- [ ] Build a function call graph from reachable function bodies. Done when: overloaded and ambiguous calls preserve required dependencies.
- [ ] Mark globals from live functions and live initializers. Done when: required constants, structs, macros, and helpers remain.
- [ ] Remove unreferenced uniforms, samplers, images, SSBOs, and UBO members only when reflection and binding semantics remain correct. Done when: pinned or engine-required resources stay active.
- [ ] Emit original, resolved, optimized, and backend-transformed byte and line counts. Done when: diagnostics show source-size changes for any program descriptor.
- [ ] Include enabled passes, folded literal count, removed function/global/resource counts, roots, and fail-open reasons. Done when: optimizer decisions are explainable.
- [ ] Add debug settings to dump original, resolved, optimized, and backend source for a program descriptor or failed source hash. Done when: failed-hash diagnostics name the optimized source size and dump location.
- [ ] Add Shader Program Links details for optimizer identity, passes, sizes, static axes, and fail-open reasons. Done when: editor diagnostics show optimization context.

### Reflection And Backends

- [ ] Reflect active optimized source so inactive samplers do not create fallback sampler noise. Done when: optimized reflection drives material binding diagnostics.
- [ ] Keep material parameter preservation independent from active shader reflection. Done when: pruned parameters remain authored.
- [ ] Distinguish pruned-static, pruned-unreachable, and unexpectedly-missing material diagnostics. Done when: each diagnostic has a specific cause.
- [ ] Keep `GLShaderSourceCompatibility` downstream of the generic optimizer. Done when: OpenGL-specific rewrites do not leak into generic optimization.
- [ ] Run Vulkan auto-uniform and descriptor rewrites after generic optimization. Done when: Vulkan shader tooling consumes resolved and optimized source.
- [ ] Ensure `ShaderCrossCompiler` consumes resolved and optimized source when called from runtime shader paths. Done when: cross-compiled output uses the same source identity.

### Tests And Docs

- [ ] Expand unit tests for reachable helpers, unreachable helpers, unreferenced resources, static axes, runtime properties, keep annotations, unknown preprocessor constructs, overloads, structs, and generated uber variants. `XREngine.UnitTests/Rendering/`. Done when: each optimizer rule has deterministic coverage.
- [ ] Update `docs/architecture/rendering/uber-shader-varianting.md` to separate uber material intent from generic source pruning. Done when: architecture docs match source ownership.
- [ ] Update `docs/work/design/rendering/world-shader-prewarm-graph-design.md` with optimized-source identity in prewarm descriptors. Done when: prewarm design uses the current identity model.
- [ ] Document shader source pipeline ownership and optimizer authoring limits. Done when: resolver, optimizer, backend transforms, compilers, variant factories, editor tools, keep annotations, mutability modes, and rebuild behavior are documented.

## Decisions Needed

- [ ] Decide whether keep annotations remain both comment-based and pragma-based. Owner: rendering lead.
- [ ] Decide how much preprocessor evaluation belongs in the optimizer versus the resolver. Owner: rendering lead.
- [ ] Decide which engine uniforms and bound resources are always pinned. Owner: rendering lead.
- [ ] Decide where source dumps should be written. Owner: rendering lead.
- [ ] Decide whether optimizer aggressiveness is global or backend-specific. Owner: rendering lead.
- [ ] Decide whether `debug-static` properties rebuild during Play mode by default. Owner: editor lead.

## Out Of Scope

- Changing shader pipeline selection policy.
- Removing authored material parameters only because optimized reflection prunes them.
