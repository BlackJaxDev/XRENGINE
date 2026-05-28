# Resolved Shader Source Optimization Todo

Last Updated: 2026-05-28
Status: Proposed architectural restructuring.

## Goal

Move shader source pruning out of uber-only variant generation and into a
backend-neutral source optimization stage that runs after all shader includes,
snippets, generated source fragments, and known compile-time defines have been
resolved.

The optimized source should be the text used for program hashing, binary-cache
keys, reflection, compile, and link. This applies to uber, deferred, forward,
compute, post-process, UI, shadow, and any future shader family.

The restructuring should also organize shader source handling by pipeline stage
instead of continuing to spread preprocessing, snippet resolution, variant
generation, backend fixups, and editor-only optimization across loosely related
utility classes.

## Context

The May 27 Sponza run showed several `Combined:*` uber programs timing out in
OpenGL source linking after the shader source had expanded into very large
monolithic programs. The immediate symptom was beige pending-uber fallback
rendering, but the broader architectural issue is that source pruning is tied
too closely to the uber variant builder.

Uber materials must remain robust and efficient even if the final production
world path prefers deferred for static world geometry and uber for avatars or
expressive forward materials.

Shader pipelines versus combined programs must remain user-decidable. The
source optimizer must improve both paths rather than forcing one path as the
fix.

## Design Principles

- Resolve first, optimize second.
- Organize source handling by stage: resolve, optimize, backend-transform,
  compile/link, reflect/cache.
- Run in the same source-resolution lane/thread, not in render submission.
- Apply to any shader source, not only `UberShader.frag`.
- Keep backend-specific transforms after the generic optimizer so OpenGL and
  Vulkan consume the same optimized resolved source before applying their own
  compatibility rewrites.
- Keep pass/material variant factories separate from generic source
  preprocessing; they define render intent, not whole-source cleanup policy.
- Treat property mutability as an explicit shader contract. Static properties
  become variant axes and runtime properties remain uniforms.
- Preserve user control over shader pipelines, async compilation, binary
  caching, and combined-program behavior.
- Prefer fails-open safety: if a source construct cannot be proven safe to
  remove, keep it and report why.
- Keep optimizer output deterministic so binary cache keys are stable.
- Include an optimizer version in source/cache identity so layout changes
  invalidate stale binaries.
- Avoid heap allocations in per-frame hot paths; optimization belongs to
  program preparation and prewarm paths.

## Non-Goals

- Do not make shader pipelines mandatory for uber materials.
- Do not make deferred rendering a workaround for uber shader health.
- Do not require the uber variant builder to understand every GLSL helper that
  can later be removed by generic reachability analysis.
- Do not rely on driver dead-code elimination as the primary source-size
  strategy.
- Do not create a single mega shader preprocessor that owns every backend,
  compiler, reflection, and editor concern.
- Do not fold OpenGL compatibility rewrites or Vulkan descriptor rewrites into
  the generic optimizer; keep those backend responsibilities explicit.

## Source Pipeline Organization

Target shape:

```text
raw XRShader source
 -> ShaderSourceResolver
 -> ResolvedShaderSourceOptimizer
 -> backend source transforms
 -> compile/link/cache/reflection
```

Recommended ownership:

- `ShaderSourceResolver` is the canonical implementation for include
  expansion, snippet expansion, dependency tracking, source-map spans, and
  resolution diagnostics.
- `ShaderSourcePreprocessor` may remain as a small public/runtime facade over
  the resolver, but should not grow separate behavior.
- `ShaderSnippets` should become a snippet registration/discovery facade. Its
  recursive source expansion path should be removed or delegated to
  `ShaderSourceResolver` so there is one snippet resolution implementation.
- `ResolvedShaderSourceOptimizer` should own generic resolved-source cleanup:
  static property literal folding, static conditional pruning, symbol scanning,
  function/global/resource reachability, keep annotations, and optimization
  diagnostics.
- `UberShaderVariantBuilder` should own uber material/pass intent only:
  variant axes, static property values, generated variant identity, and pass
  macros. Generic pruning should move out of the uber builder.
- `GlslSnippetDeadCodeEliminator` should either be folded into
  `ResolvedShaderSourceOptimizer` or reduced to an internal pass used by that
  optimizer. It should not remain an uber-only or snippet-only final cleanup
  path.
- `GLShaderSourceCompatibility` should stay OpenGL-specific and run after
  generic optimization.
- Vulkan shader source handling should be split by responsibility:
  `VulkanShaderAutoUniforms`, `VulkanShaderCompiler`,
  `VulkanShaderReflection`, and SPIR-V parsing/helpers should live in separate
  files or classes instead of one large `VulkanShaderTools` unit.
- `ShaderCrossCompiler` should remain a general GLSL/HLSL-to-SPIR-V compiler
  wrapper and should consume already-resolved/optimized source when called from
  runtime shader paths.
- `ForwardDepthNormalVariantFactory` and `ShadowCasterVariantFactory` should
  stay as pass/material variant factories. They can move under an organized
  shader-variant namespace/folder, but they should not become generic
  preprocessors.
- Editor tools such as shader variant optimization and shader cross-compile
  windows should reuse the same resolver/optimizer services for preview and
  diagnostics, without becoming part of the runtime compile pipeline.

## Shader Property Mutability

Shader UI annotations should describe when a property is allowed to change. The
optimizer can only fold branches and prune dependent helpers/resources when a
property is static for the program being compiled.

Proposed modes:

- `runtime`: Default uniform behavior. The value may change per frame, per
  material instance, animation, script, networking update, or gameplay system.
  Branches depending on this value must remain live.
- `material-static`: Editable on the material, including in the editor, but a
  value change creates or selects a new shader variant. The optimizer may treat
  the value as a compile-time literal.
- `pass-static`: Chosen by render pass or render-pipeline intent, such as
  depth-normal, shadow caster, forward color, OIT mode, stereo mode, or
  pass-specific output layout. The optimizer may specialize per pass.
- `engine-static`: Chosen by renderer backend, engine settings, GPU capability,
  or platform profile. Changes invalidate affected shader cache entries.
- `debug-static`: Editor/debug toggle that may be edited interactively but is
  expected to rebuild variants, not animate as a runtime uniform.

Example annotation direction:

```glsl
//@property(name="_ShadowType", type="int", mutability="material-static")
uniform int _ShadowType;

//@property(name="_WindStrength", type="float", mutability="runtime")
uniform float _WindStrength;
```

If `_ShadowType` is `material-static`, the resolved optimizer can replace uses
with the material's literal value before static-if and reachability pruning. A
branch tree such as PCF versus VSM versus no shadows can then collapse to only
the selected path. If `_WindStrength` is `runtime`, it remains a uniform and any
branches that depend on it stay live.

Editor behavior must make this visible. Changing a static property should mark
the material variant dirty, show pending/rebuilding/failed state, and avoid
presenting the value as a cheap per-frame uniform edit.

## Todo

1. Create a dedicated branch for the restructuring work.
   - Suggested name: `rendering/resolved-shader-source-optimizer`.

2. Audit current shader source resolution.
   - Map every path that expands includes, snippets, generated vertex shaders,
     generated uber variants, inline shaders, and file-backed shaders.
   - Classify each path as canonical resolution, public facade, generic
     optimization, backend transform, pass/material variant generation, runtime
     compiler integration, or editor tooling.
   - Identify where `XRShader`, `XRRenderProgramDescriptor`,
     `GLRenderProgram`, shader binary-cache hashing, and reflection each see
     source text.
   - Record overlap between `ShaderSnippets` and `ShaderSourceResolver`, and
     between the separate responsibilities currently grouped in
     `VulkanShaderTools`.
   - Document current uber-specific pruning and `GlslSnippetDeadCodeEliminator`
     behavior before moving responsibilities.

3. Define a resolved source model.
   - Add a renderer-neutral `ResolvedShaderSource` payload with original path,
     resolved text, source identity, include/snippet dependency list, macro
     summary, and source-map spans where practical.
   - Make this payload the handoff between resolver, optimizer, backend
     transforms, compile/link, reflection, and cache hashing.
   - Track resolution diagnostics separately from optimization diagnostics.
   - Ensure generated shaders can still point back to canonical authoring files.

4. Add shader property mutability metadata.
   - Extend shader UI annotation parsing with a mutability field.
   - Default missing mutability to `runtime` unless an existing annotation or
     migration rule safely implies static behavior.
   - Add `material-static`, `pass-static`, `engine-static`, and `debug-static`
     support to shader manifest data.
   - Treat static property values as variant axes in descriptor/source identity.
   - Add editor UI affordances that show static changes rebuild variants.

5. Introduce a generic optimizer stage.
   - Add a `ResolvedShaderSourceOptimizer` or equivalent service invoked after
     include/snippet resolution.
   - Start by wrapping existing useful cleanup logic, such as
     `GlslSnippetDeadCodeEliminator`, behind optimizer passes where that keeps
     behavior stable.
   - Make the optimizer opt-out via engine/editor debug settings and env vars.
   - Include optimizer version, enabled passes, and conservative/fails-open
     mode in the program source identity.
   - Emit before/after byte and line counts into shader lifecycle diagnostics.

6. Build the GLSL symbol scanner.
   - Tokenize comments, strings, preprocessor lines, declarations, layout
     qualifiers, uniform blocks, samplers, storage buffers, structs, constants,
     global variables, and function bodies.
   - Preserve unknown preprocessor regions unless a previous resolve stage has
     already made them concrete.
   - Recognize keep annotations such as `//@keep`, `//@shader-interface`, or a
     chosen `#pragma xre_keep` form.
   - Preserve stage interfaces, layout-bound resources, transform feedback
     requirements, and explicit engine reflection roots.

7. Add static property literal folding.
   - Replace `material-static`, `pass-static`, `engine-static`, and
     `debug-static` uniforms with literals before static-if pruning.
   - Preserve original material parameters so static properties remain editable
     even when absent from optimized reflection.
   - Add diagnostics when a property marked static cannot be safely folded.

8. Add function reachability pruning.
   - Root from `main` plus declared extra roots.
   - Build a function call graph from reachable function bodies.
   - Remove functions that are unreachable and not annotated as keep/interface.
   - Preserve overloads and forward declarations conservatively when ambiguity
     exists.

9. Add global/resource reachability pruning.
   - Mark globals referenced by live functions and live initializers.
   - Keep transitive type dependencies, structs, constants, macros, and helper
     globals required by live code.
   - Remove unreferenced uniforms, samplers, images, SSBOs, UBO members only
     when reflection/binding semantics remain correct.
   - Preserve explicitly bound or engine-required resources unless proven safe.

10. Split static specialization from generic pruning.
   - Keep the uber variant builder responsible for material intent: authored
     feature state, static literals, pass macros, and variant identity.
   - Move generic whole-source dead-code removal into the new optimizer.
   - Move conditional pruning, static uniform stripping, and static literal
     inlining out of uber-only code when those operations are generally valid.
   - Ensure non-uber shaders receive the same optimizer treatment after their
     own source has resolved.

11. Integrate with program hashing and binary cache.
   - Hash optimized source, not pre-optimized resolved source.
   - Route OpenGL and Vulkan program preparation through the same optimized
     resolved source before backend-specific compatibility or descriptor
     rewrites run.
   - Store optimizer version and source dependency fingerprints in cache keys.
   - Store static property names and literal values in the shader source
     identity.
   - Ensure failed-hash diagnostics report optimized source size and can locate
     the dumped optimized source.
   - Keep binary cache invalidation correct when includes, snippets, optimizer
     version, or keep annotations change.

12. Integrate with reflection and uniform binding.
    - Reflect active optimized source so inactive/dead samplers do not produce
      fallback sampler noise.
    - Run Vulkan auto-uniform and descriptor rewrites after generic
      optimization, and keep OpenGL compatibility injection after generic
      optimization.
    - Ensure material parameter preservation remains independent from active
      shader reflection.
    - Add diagnostics for material parameters that exist but are pruned from the
      active shader.
    - Distinguish "pruned because static" from "missing unexpectedly" in
      material and shader diagnostics.

13. Add source dump and UI diagnostics.
    - Add debug settings to dump original, resolved, and optimized GLSL for a
      program descriptor or failed hash.
    - Include removed function/global/resource counts in `ShaderBackend` and
      `ShaderLink` records.
    - Add Shader Program Links details for original/resolved/optimized sizes,
      optimizer passes, and fails-open reasons.
    - Include static property axes and folded literal counts in source
      optimizer diagnostics.

14. Add unit coverage.
    - Reachable function calls keep required helpers.
    - Unreachable helpers are removed after include/snippet expansion.
    - Unreferenced samplers and uniforms are removed only when not pinned.
    - Static property annotations become variant axes and fold into literals.
    - Runtime property annotations stay uniforms and keep dependent branches
      live.
    - Keep annotations preserve functions, globals, and resources.
    - Unknown preprocessor constructs fail open.
    - Overloaded functions and structs preserve required dependencies.
    - Generated uber variants no longer rely on uber-only pruning for generic
      dead-code removal.

15. Add integration coverage.
    - Compile optimized variants for representative deferred, forward, uber,
      compute, UI, shadow, and post-process shaders.
    - Validate OpenGL combined-program and shader-pipeline modes separately.
    - Validate warm-cache and cold-cache behavior.
    - Confirm optimized reflection does not break material texture binding.

16. Validate the Sponza uber failure case.
    - Clear failed shader hashes and stale binary cache entries.
    - Load the same Sponza uber world that produced the May 27 timeout logs.
    - Confirm large `Combined:*` sources shrink materially after optimization.
    - Confirm failed entries no longer stick on the beige pending-uber fallback.
    - Record compile/link timings for combined and pipeline modes.

17. Update related docs.
    - Update `docs/architecture/rendering/uber-shader-varianting.md` to clarify
      that uber varianting specializes material intent but generic pruning runs
      later.
    - Update `docs/architecture/rendering/world-shader-prewarm-graph.md` with
      optimized-source identity in prewarm descriptors.
    - Document shader source pipeline ownership: resolver facade, canonical
      resolver, generic optimizer, backend transforms, runtime compilers,
      variant factories, and editor tools.
    - Update shader authoring docs with keep annotations, mutability modes, and
      optimizer limits.

18. Organize shader source implementation layout.
    - Keep `ShaderSourceResolver` as the canonical include/snippet resolver.
    - Keep `ShaderSourcePreprocessor` as a small public/runtime facade only.
    - Convert `ShaderSnippets` into registration/discovery plus delegation to
      the canonical resolver.
    - Split `VulkanShaderTools` into separate source files/classes for
      auto-uniform rewriting, shader compilation, reflection, and SPIR-V
      parsing helpers.
    - Keep `GLShaderSourceCompatibility` OpenGL-specific and downstream of the
      generic optimizer.
    - Keep pass/material variant factories separate from generic preprocessing;
      optionally move them under an organized shader-variant namespace/folder.
    - Make editor shader tools consume resolver/optimizer services for preview,
      source dumps, and diagnostics.

19. Merge the dedicated branch back to `main` after implementation,
    validation, and documentation updates.

## Acceptance Criteria

- Resolved shader source optimization runs for all shader families.
- Uber variant generation no longer owns generic source pruning.
- Optimized source is used for hashing, binary-cache identity, reflection,
  compile, and link.
- Static shader property mutability is represented in shader manifests,
  descriptor identity, editor UI, and diagnostics.
- Shader pipeline choice remains a user/backend setting.
- Sponza uber variants no longer time out solely because unused resolved code
  remains in the final compiler-facing source.
- Diagnostics can show original, resolved, and optimized source sizes for any
  shader program.
- Unit and integration tests cover both combined-program and shader-pipeline
  modes.

## Open Questions

- Should keep annotations be comment-based, pragma-based, or both?
- Should mutability annotations be comment-based, pragma-based, attribute-like,
  or expressed in external shader metadata?
- How much preprocessor evaluation should the optimizer own versus the existing
  resolver?
- Which engine uniforms and bound resources must be pinned even when the
  optimized source does not reference them directly?
- Should source dumps be stored per run log directory, global shader-cache
  directory, or both?
- Should optimizer aggressiveness be selectable per renderer backend or only
  globally?
- Should `debug-static` properties rebuild during Play mode by default, or only
  when an editor preference permits live variant churn?
