# Resolved Shader Source Optimization Remaining Todos

Last Updated: 2026-05-29
Status: Partially implemented; remaining work is architecture cleanup,
diagnostics, coverage, and validation.

## Goal

Finish moving shader source pruning into a backend-neutral resolved-source
optimization stage that runs after includes, snippets, generated fragments, and
known compile-time defines are resolved.

The optimized source must be the text used for program hashing, binary-cache
identity, reflection, compile, and link for uber, deferred, forward, compute,
post-process, UI, shadow, and future shader families.

## Implemented Baseline

- `ResolvedShaderSource` exists as a renderer-neutral payload for original
  path, original source, resolved source, dependency list, macro summary, and
  source identity.
- `ResolvedShaderSourceOptimizer` exists and is invoked through
  `XRShader.TryGetOptimizedSource(...)`.
- OpenGL shader compilation and source hashing consume optimized source before
  `GLShaderSourceCompatibility` applies OpenGL-specific rewrites.
- `XRRenderProgramDescriptor` includes optimizer identity and optimized shader
  source in its stable shader identity.
- `RenderDiagnosticsFlags.ShaderSourceOptimizerEnabled` and
  `XRE_SHADER_SOURCE_OPTIMIZER=0` can disable the generic optimizer.
- `ShaderUiManifest` parses property mutability values:
  `runtime`, `material-static`, `pass-static`, `engine-static`, and
  `debug-static`.
- The material inspector exposes static-property update expectations for
  explicitly annotated properties.
- `ShaderSnippets` now delegates snippet registration, discovery, and
  resolution to `ShaderSourceResolver`.
- Initial unit coverage exists for resolved-source optimization, static literal
  folding, keep annotations, pinned layout-bound resources, and shared snippet
  resolution.

## Remaining Work

1. Audit the current landed implementation.
   - Verify every runtime path that can compile shader text: `XRShader`,
     `XRRenderProgramDescriptor`, `XRRenderProgram`, `GLShader`,
     `GLRenderProgram`, `VulkanShaderTools`, shader editor tools, prewarm
     paths, inline shaders, generated vertex shaders, generated uber variants,
     compute shaders, and post-process shaders.
   - Record where each path currently sees raw, resolved, optimized, or
     backend-transformed source.
   - Record which paths still bypass `ResolvedShaderSource` or
     `ResolvedShaderSourceOptimizer`.

2. Finish separating uber intent from generic pruning.
   - Keep `UberShaderVariantBuilder` responsible for material intent only:
     feature axes, pass macros, static property values, generated identity, and
     variant telemetry.
   - Move generally valid source cleanup out of the uber builder:
     static uniform stripping, static literal inlining, static-if pruning, and
     whole-source dead-code pruning.
   - Keep uber-specific conditional specialization in the builder only when it
     is tied to uber feature or pass intent.
   - Ensure generated uber variants no longer depend on private uber-only
     helpers for generic pruning.

3. Feed static property literals into non-uber optimization.
   - Build optimizer options from resolved shader manifests and program/pass
     descriptors, not only from uber variant state.
   - Treat `material-static`, `pass-static`, `engine-static`, and
     `debug-static` values as source identity axes when they affect optimized
     text.
   - Preserve authored material parameters even when optimized reflection no
     longer exposes the folded uniforms.
   - Add diagnostics when a static property cannot be folded safely.

4. Replace regex-only reachability with a conservative GLSL scanner.
   - Tokenize comments, strings, preprocessor lines, declarations, layout
     qualifiers, uniform blocks, samplers, images, SSBOs, UBOs, structs,
     constants, globals, and function bodies.
   - Preserve unknown preprocessor regions unless the resolve stage has made
     them concrete.
   - Recognize keep/interface roots from `//@keep`, `//@shader-interface`, and
     `#pragma xre_keep`.
   - Preserve stage interfaces, explicit layout-bound resources, transform
     feedback roots, and engine-required reflection roots.
   - Preserve overloads and forward declarations conservatively when ambiguity
     exists.

5. Harden function, global, and resource pruning.
   - Root from `main` plus explicit extra roots.
   - Build a function call graph from reachable function bodies.
   - Remove unreachable functions only when overload resolution is unambiguous.
   - Mark globals referenced by live functions and live initializers.
   - Keep transitive type dependencies, structs, constants, macros, and helper
     globals required by live code.
   - Remove unreferenced uniforms, samplers, images, SSBOs, and UBO members
     only when reflection and binding semantics remain correct.
   - Preserve explicitly bound or engine-required resources unless proven safe.

6. Improve optimizer diagnostics and source dumps.
   - Emit original, resolved, optimized, and backend-transformed byte/line
     counts.
   - Include enabled passes, folded literal count, removed function/global/
     resource counts, roots, and fails-open reasons.
   - Add debug settings to dump original, resolved, optimized, and backend
     source for a program descriptor or failed source hash.
   - Make failed-hash diagnostics report optimized source size and the dumped
     optimized-source location.
   - Add Shader Program Links details for optimizer identity, optimizer passes,
     original/resolved/optimized sizes, static axes, and fails-open reasons.

7. Integrate optimized reflection and material diagnostics.
   - Reflect active optimized source so inactive samplers do not create fallback
     sampler noise.
   - Keep material parameter preservation independent from active shader
     reflection.
   - Add diagnostics for material parameters that exist but are pruned from the
     active optimized shader.
   - Distinguish "pruned because static", "pruned because unreachable", and
     "missing unexpectedly" in shader/material diagnostics.

8. Keep backend responsibilities explicit.
   - Keep `GLShaderSourceCompatibility` OpenGL-specific and downstream of the
     generic optimizer.
   - Run Vulkan auto-uniform and descriptor rewrites after generic
     optimization.
   - Split `VulkanShaderTools` into smaller files/classes for auto-uniform
     rewriting, shader compilation, reflection, and SPIR-V parsing/helpers.
   - Ensure `ShaderCrossCompiler` consumes already-resolved/optimized source
     when called from runtime shader paths.

9. Expand unit coverage.
   - Reachable function calls keep required helpers.
   - Unreachable helpers are removed after include/snippet expansion.
   - Unreferenced samplers and uniforms are removed only when not pinned.
   - Static property annotations become source identity axes and fold into
     literals.
   - Runtime property annotations stay uniforms and keep dependent branches
     live.
   - Keep annotations preserve functions, globals, and resources.
   - Unknown preprocessor constructs fail open.
   - Overloaded functions and structs preserve required dependencies.
   - Generated uber variants no longer rely on uber-only pruning for generic
     dead-code removal.

10. Add integration coverage.
    - Compile optimized variants for representative deferred, forward, uber,
      compute, UI, shadow, and post-process shaders.
    - Validate OpenGL combined-program and shader-pipeline modes separately.
    - Validate Vulkan source rewriting after generic optimization.
    - Validate warm-cache and cold-cache behavior.
    - Confirm optimized reflection does not break material texture binding.

11. Validate the Sponza uber failure case.
    - Clear failed shader hashes and stale binary cache entries.
    - Load the same Sponza uber world that produced the May 27 timeout logs.
    - Confirm large `Combined:*` sources shrink materially after optimization.
    - Confirm failed entries no longer stick on the beige pending-uber fallback.
    - Record compile/link timings for combined and pipeline modes.

12. Update related docs.
    - Update `docs/architecture/rendering/uber-shader-varianting.md` to clarify
      that uber varianting specializes material intent while generic pruning
      runs later.
    - Update `docs/work/design/rendering/world-shader-prewarm-graph-design.md` with
      optimized-source identity in prewarm descriptors.
    - Document shader source pipeline ownership: resolver facade, canonical
      resolver, generic optimizer, backend transforms, runtime compilers,
      variant factories, and editor tools.
    - Update shader authoring docs with keep annotations, mutability modes,
      static-property rebuild behavior, and optimizer limits.

## Acceptance Criteria

- Resolved shader source optimization runs for all shader families through one
  generic stage.
- Uber variant generation no longer owns generic source pruning.
- Optimized source is used for hashing, binary-cache identity, reflection,
  compile, and link.
- Static shader property mutability affects manifests, descriptor/source
  identity, editor UI, and diagnostics.
- Shader pipeline choice remains a user/backend setting.
- Sponza uber variants no longer time out solely because unused resolved code
  remains in the final compiler-facing source.
- Diagnostics can show original, resolved, optimized, and backend-transformed
  source sizes for any shader program.
- Unit and integration tests cover combined-program and shader-pipeline modes.

## Open Questions

- Should keep annotations remain both comment-based and pragma-based?
- How much preprocessor evaluation should the optimizer own versus the
  resolver?
- Which engine uniforms and bound resources must always be pinned?
- Should source dumps live under the per-run log directory, shader-cache
  directory, or both?
- Should optimizer aggressiveness be selectable per renderer backend or only
  globally?
- Should `debug-static` properties rebuild during Play mode by default, or
  only when an editor preference permits live variant churn?
