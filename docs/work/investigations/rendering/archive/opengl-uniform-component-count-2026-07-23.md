# OpenGL Uniform Component Count Investigation

## Problem

OpenGL reported `GL_INVALID_OPERATION` from `glProgramUniform3` while a
`ShaderVector3` material parameter was being uploaded. The driver described the
failure as an invalid component count. The same run also reported scalar uniform
component type/count failures.

## Issues Found

1. Programs restored from the binary cache could restore serialized uniform
   metadata without reflecting the interface of the actual linked OpenGL
   program handle. Uniform upload validation therefore had a path where its
   cached type information could disagree with the driver.
2. Missing location/name metadata was treated as valid when `GLDebug` was
   disabled, allowing an unverifiable upload to reach OpenGL.
3. GTAO, SSAO, MVAO, MSVO, HBAO+, and bloom uploaded integer viewport
   dimensions to GLSL `float ScreenWidth` and `float ScreenHeight` uniforms.

## Solution

- Reflect active uniforms from every successfully loaded binary program handle
  before rebuilding uniform location/type caches. Serialized metadata remains
  useful as cache data, but is not trusted as runtime truth.
- Skip uploads whose active uniform metadata cannot be established, regardless
  of diagnostic verbosity. Detailed warnings remain gated by `GLDebug`.
- Upload screen dimensions as floats in the affected AO and bloom passes.

## Validation

### Baseline

- Isolated session: `codex-gl-uniform-component`
- OpenGL, shader pipelines enabled, binary program caching enabled.
- Entered play mode through MCP and captured the viewport.
- No high-severity component-count error reproduced.
- The uniform guard caught repeated `ScreenWidth`/`ScreenHeight` integer-to-float
  mismatches in GTAO and bloom, identifying the nearby deterministic defect.
- Evidence:
  `Build/_AgentValidation/20260723-opengl-uniform-component/`

### Post-fix

- `XREngine.Runtime.Rendering.csproj` built with zero warnings and zero errors.
- Repeated the same isolated OpenGL session and entered play mode with shader
  pipelines and binary caching enabled.
- Binary-loaded programs reported live reflection work in their build telemetry.
- The new session logged no `GL_INVALID_OPERATION`, invalid/wrong component
  count, or `ScreenWidth`/`ScreenHeight` uniform mismatch warnings.
- The post-fix viewport capture rendered the loaded unit-testing scene normally.
- `GLMaterialTextureBindingContractTests`: 6 passed.
- Post-fix logs are copied into the evidence root with a `postfix-` prefix.
