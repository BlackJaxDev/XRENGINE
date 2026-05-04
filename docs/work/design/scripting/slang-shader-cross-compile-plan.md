# Slang Shader Cross-Compile Plan (Phased)

## Goal

Add Slang as a first-class shader frontend/cross-compiler path so the engine can emit backend targets (starting with SPIR-V) without requiring migration of existing GLSL shader sources.

## Non-Goals

- Do not mass-convert existing `.glsl` assets to `.slang`.
- Do not break current GLSL runtime/build-time compilation behavior.
- Do not gate current Vulkan/OpenGL paths on Slang adoption.

## Key Requirement

Existing GLSL must remain source-of-truth where it already exists. The new pipeline must support:

1. Existing GLSL -> Slang-compatible ingest path (directly or via generated wrapper/stub) -> target backend output.
2. Native Slang (`.slang`) shaders for new development.
3. Side-by-side operation so teams can adopt Slang incrementally.

## Phase 0 — Discovery & Contract Definition

### Objectives

- Define the engine shader compilation contract independent of frontend language.
- Decide where Slang compiler invocation lives (editor tooling vs runtime service vs shared compile utility).
- Lock minimum supported targets per backend (initially Vulkan SPIR-V; later HLSL/MSL as needed).

### Deliverables

- A language-agnostic `ShaderCompileRequest`/`ShaderCompileResult` contract draft.
- Capability table for source language (`GLSL`, `Slang`) vs output target (`SPIR-V`, future targets).
- Error/diagnostic format spec (file/line/entrypoint/stage) used by runtime + editor.

### Exit Criteria

- Team agreement on compile API and adoption constraints.
- No unresolved blocker on including Slang binaries/packages in the build pipeline.

## Phase 1 — Slang Toolchain Integration (No Behavior Change)

### Objectives

- Integrate Slang compiler dependency and deterministic invocation path.
- Keep current GLSL path as default; Slang path is feature-flagged/off by default.

### Deliverables

- Slang compiler bootstrap in engine/editor build scripts.
- Version-pinned Slang dependency docs and setup instructions.
- Smoke test command that compiles a trivial shader to SPIR-V.

### Exit Criteria

- CI/dev machines can resolve and invoke Slang compiler reliably.
- Existing GLSL compile path remains unchanged and passing.

## Phase 2 — Unified Frontend Abstraction

### Objectives

- Introduce a shader frontend abstraction (`GLSLFrontend`, `SlangFrontend`) behind a single compile service.
- Route existing compile callers through the shared abstraction without changing asset format.

### Deliverables

- `IShaderFrontend` (or equivalent) interface with stage/entrypoint/macro/include handling.
- GLSL frontend adapter preserving existing behavior.
- Slang frontend adapter for native `.slang` input.

### Exit Criteria

- Engine/editor call sites compile through unified API.
- No regression in existing GLSL shader compile outputs for current targets.

## Phase 3 — GLSL -> Slang-Compatible Cross-Compile Path

### Objectives

- Enable existing GLSL shaders to flow through Slang-compatible processing for backend emission.
- Preserve shader authoring in GLSL for legacy assets.

### Approach

- Implement a GLSL ingest strategy:
  - Preferred: direct GLSL ingestion supported by Slang toolchain APIs/options.
  - Fallback: generated translation wrapper/stub that maps GLSL stages/entrypoints/defines/includes into Slang-accepted form.
- Ensure include semantics, macro expansion, and stage metadata remain equivalent.

### Deliverables

- Feature flag: `UseSlangForGlslCrossCompile` (default off during rollout).
- Mapping layer for GLSL stage/entrypoint/define/include normalization.
- Golden-output tests comparing legacy GLSL->SPIR-V vs new GLSL->(Slang path)->SPIR-V for representative shaders.

### Exit Criteria

- Representative GLSL corpus compiles via Slang path with acceptable parity.
- Any deltas are documented and either fixed or explicitly waived.

## Phase 4 — Editor & Asset Pipeline Support

### Objectives

- Expose compiler selection and diagnostics in editor tooling.
- Support dual-source projects (`.glsl` + `.slang`) without ambiguity.

### Deliverables

- Editor shader compile UI updates for frontend/backend selection per asset or profile.
- Cache key/version updates including frontend + Slang version + target profile.
- Asset import/build metadata updates for language + entrypoint conventions.

### Exit Criteria

- Developers can inspect/choose compile frontend in tools.
- Shader cache invalidation behaves correctly across frontend switches.

## Phase 5 — Validation, Rollout, and Defaulting

### Objectives

- Validate correctness/perf/stability under real project content.
- Roll out in stages, then optionally switch defaults.

### Deliverables

- Test matrix results across:
  - Shader stages: vertex/fragment/compute (and others used by engine).
  - Feature classes: includes, macros, UBO/SSBO, push constants, sampler/image usage.
  - Runtime modes: editor, client, dedicated server shader warmup paths.
- Rollout plan:
  1. Opt-in per user/project.
  2. Opt-out default after stability threshold.
  3. Full default switch with legacy fallback retained for one release window.

### Exit Criteria

- No high-severity regressions in target content set.
- Documented fallback path remains available if Slang pipeline fails.

## Compatibility & Risk Notes

- Language semantic mismatches (layout qualifiers, extension behavior, preprocessor edge cases) are the primary migration risk.
- Keep deterministic compiler version pinning to avoid cache churn and non-reproducible outputs.
- Preserve source-path/line mapping so diagnostics still point to original GLSL where possible.
- Avoid hard cutovers; always retain legacy GLSL compile fallback until parity is proven.

## Suggested Implementation Order (Code Touchpoints)

1. Shared compile contracts and abstraction layer.
2. Slang tool invocation service + diagnostics adapter.
3. GLSL frontend adapter parity lock.
4. GLSL->Slang-compatible ingest/mapping path.
5. Editor integration + cache key updates.
6. Rollout flags, telemetry, and fallback controls.

## Success Criteria

- Existing GLSL shaders continue to work without authoring conversion.
- New shaders can be authored in Slang.
- Engine can cross-compile through a unified path to required backend targets.
- Build/editor/runtime diagnostics remain clear and actionable across both frontends.