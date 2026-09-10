# Slang Shader Cross-Compile Plan (Phased)

## Goal

Add Slang as an additive shader frontend for native `.slang` sources. The initial Slang route is direct SPIR-V for Vulkan 1.4, with reflection and explicit capability metadata. Existing GLSL remains an independent source and frontend for Vulkan and OpenGL 4.6.

## Non-Goals

- Do not mass-convert existing `.glsl` assets to `.slang`.
- Do not require GLSL to pass through Slang.
- Do not break, gate, or retire existing GLSL, Shaderc, Vulkan, or OpenGL paths.
- Do not introduce a global Slang default switch.

## Supported Routes

1. Existing GLSL -> existing GLSL preprocessing and Shaderc Vulkan compilation.
2. Existing GLSL -> existing GLSL preprocessing and OpenGL driver compilation.
3. Native Slang (`.slang`) -> direct Vulkan 1.4 SPIR-V plus reflection.
4. Native Slang -> generated OpenGL GLSL only as an experimental, separately validated route. Shared passes use authored GLSL counterparts for OpenGL until generated output is proven.

These routes operate side by side. Slang adoption is opt-in per user, project, profile, or pass. There is no required GLSL-to-Slang translation stage and no automatic GLSL/OpenGL retirement.

## Phase 0 — Discovery & Contract Definition

### Objectives

- Define a language-agnostic shader compilation contract.
- Specify frontend, backend, stage, entry point, capabilities, artifacts, reflection, layouts, bindings, semantic metadata, and diagnostics.
- Lock Vulkan 1.4 SPIR-V as the initial Slang target.

### Deliverables

- `ShaderCompileRequest`/`ShaderCompileResult` contract draft.
- Capability matrix for GLSL and Slang against Vulkan SPIR-V and OpenGL GLSL.
- Diagnostic format with source file, line, entry point, stage, and frontend.
- Explicit ABI rules for packing, padding, scalar widths, array/matrix strides, matrix order, resource bindings, and GPU-address representation.

### Exit Criteria

- The contract supports coexistence without translating or retiring existing GLSL.
- Reflection metadata is joined with explicit engine ownership and update-frequency semantics; reflection alone does not define runtime semantics.

## Phase 1 — Optional Slang Toolchain Integration

### Objectives

- Integrate deterministic invocation for the externally provisioned Slang compiler, without adding a package or redistributing compiler binaries.
- Use process-per-compile as the initial isolation model.
- Keep existing GLSL compilation usable when Slang is unavailable.

### Deliverables

- Setup and discovery documentation for locally installed Slang 2026.8 through Vulkan SDK 1.4.350.0.
- Smoke command compiling one native `.slang` shader directly to Vulkan 1.4 SPIR-V.
- Compiler version and target profile in compile/cache identity.

### Exit Criteria

- A configured development machine can invoke Slang 2026.8 and produce Vulkan 1.4 SPIR-V.
- Existing GLSL/Shaderc and OpenGL driver paths remain independently usable.

## Phase 2 — Unified Frontend Abstraction

### Objectives

- Introduce `GLSLFrontend` and `SlangFrontend` behind one compile service.
- Route callers through the abstraction without changing existing asset formats.

### Deliverables

- `IShaderFrontend` (or equivalent) with stage, entry point, macro, include, and diagnostic handling.
- GLSL adapter preserving current behavior.
- Native Slang adapter producing direct Vulkan SPIR-V and reflection.

### Exit Criteria

- Engine/editor call sites can select either frontend explicitly.
- Existing GLSL outputs remain independent of Slang availability.

## Phase 3 — Native Slang Vulkan Route

### Objectives

- Compile native `.slang` sources directly to Vulkan 1.4 SPIR-V.
- Preserve existing GLSL authoring and compilation behavior.

### Deliverables

- Opt-in frontend/profile selection for direct Slang compilation.
- Reflection and source diagnostics for native Slang inputs.
- Capability checks that fail visibly when the selected Vulkan target or feature is unsupported.

### Exit Criteria

- A representative native Slang shader compiles to validated Vulkan 1.4 SPIR-V.
- Equivalent existing GLSL remains buildable through its existing frontend.

## Phase 4 — Editor & Asset Pipeline Support

### Objectives

- Expose explicit frontend and backend selection with actionable diagnostics.
- Support projects containing `.glsl`, authored OpenGL GLSL counterparts, and `.slang` assets without ambiguity.

### Deliverables

- Per-asset or profile frontend/backend selection.
- Cache keys containing source language, compiler/version, target, capabilities, stage, entry point, defines, dependencies, layout options, and schema versions.
- Asset metadata for language, entry point, generated artifact, and counterpart relationships.

### Exit Criteria

- Developers can inspect and choose the compile frontend.
- Frontend or target changes invalidate incompatible artifacts.

## Phase 5 — Validation and Opt-In Rollout

### Objectives

- Validate correctness, ABI compatibility, diagnostics, stability, and measured compile/CPU/GPU costs under representative content.
- Promote only individually validated Slang passes.

### Deliverables

- Matrix covering shader stages, includes/macros, UBO/SSBO, push constants, textures/images/samplers, stereo/multiview, editor/client/server warmup, and Vulkan 1.4 capability profiles.
- Separate validation of generated OpenGL GLSL; authored GLSL counterparts remain the supported OpenGL route for shared passes.
- Opt-in rollout controls and documented failure diagnostics.

### Exit Criteria

- Validation establishes whether each pilot pass should retain or expand Slang.
- Existing GLSL Vulkan/OpenGL routes remain available at every rollout stage.
- No phase declares a global Slang default or automatic retirement.

## Compatibility & Risk Notes

- Language and layout semantic mismatches require explicit ABI validation.
- Pin the compiler version to avoid cache churn and non-reproducible outputs.
- Preserve source-path and line mapping for diagnostics.
- Slang frontend session state is not assumed reentrant; serialize use or create independent process/session ownership for workers.
- Generated OpenGL GLSL is experimental and cannot replace authored GLSL until engine-specific validation proves it.

## Primary References

- [Slang command-line compilation](https://shader-slang.org/slang/user-guide/command-line-slangc.html)
- [Slang reflection API](https://shader-slang.org/slang/user-guide/reflection-api.html)
- [Vulkan SDK](https://vulkan.lunarg.com/sdk/home)

## Suggested Implementation Order

1. Shared compile contracts and explicit ABI metadata.
2. Slang tool invocation and diagnostics adapter.
3. GLSL frontend parity lock.
4. Native Slang direct-SPIR-V frontend.
5. Reflection, cache identity, and editor integration.
6. Pilot validation and opt-in rollout controls.

## Success Criteria

- Existing GLSL shaders continue to work without authoring conversion.
- New shaders can be authored in Slang and compiled directly to Vulkan 1.4 SPIR-V.
- Shared passes have authored GLSL counterparts for OpenGL unless generated GLSL is separately validated.
- Diagnostics and ABI metadata remain clear and actionable across both frontends.
