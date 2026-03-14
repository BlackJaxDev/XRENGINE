# Forward AO Prepass Investigation

Date: 2026-03-13

## Scope

This note captures the current state of the forward ambient occlusion investigation, specifically why the forward depth-normal prepass is still producing no visible contribution even after shader variant fixes.

## Confirmed Working

### GLSL preprocessing behavior

- `#define`, `#ifdef`, `#ifndef`, `#else`, and `#endif` are handled by the GLSL compiler and driver, not by XRENGINE.
- XRENGINE preprocessing only expands:
  - `#include`
  - `#pragma snippet`
- Source flow is:
  1. engine resolves includes and snippets
  2. resolved source is passed to `glShaderSource`
  3. the OpenGL compiler handles conditional compilation

### Variant creation is now functioning

- `XRShader.Load3rdParty()` was previously creating `new TextFile()` with no file path, leaving `Source.FilePath` null.
- That prevented `ShaderHelper.GetDepthNormalPrePassForwardVariant()` and related lookup methods from matching shader filenames.
- The fix to construct `new TextFile(filePath)` is in place.
- Variant lookup methods now also fall back to `shader.FilePath` when `shader.Source.FilePath` is null.

### Forward depth-normal fragment variants are being selected

- Runtime logging in `GLMeshRenderer.GetRenderMaterial()` shows `UseDepthNormalVariants=true` and `variantNull=False` for forward materials.
- Logged fragment paths are valid and match the expected forward shader files.
- This confirms the engine is not silently falling back to the original material for the prepass.

### Shader source differences are real even when the file path looks identical

- The depth-normal variant preserves the original shader `FilePath` intentionally.
- That is why the variant can appear to "refer to itself" in tooling.
- The important difference is in `Source.Text`, where `#define XRENGINE_DEPTH_NORMAL_PREPASS` is injected immediately after `#version`.

### Shadow caster variant being null is expected in many cases

- `GetShadowCasterForwardVariant()` only returns explicit variants for masked or alpha-tested forward shaders.
- For opaque forward shaders, a null shadow caster variant is expected behavior.

### Shader compilation is succeeding

- No OpenGL shader compile errors were observed in the runtime logs after the variant/file-path fixes.
- The prepass is issuing draw calls:
  - opaque forward count observed: 36
  - masked forward count observed: 3

### Generated vertex shader interface looks correct

- `DefaultVertexShaderGenerator` assigns explicit `layout (location = N)` qualifiers to generated vertex outputs.
- Key outputs include:
  - `FragPos` at location 0
  - `FragNorm` at location 1
  - texture coordinates starting at location 4
- Forward fragment shaders used by the prepass also declare explicit matching input locations.

### Separable programs are configured as separable

- `GLRenderProgram.CreateObject()` sets `ProgramSeparable` based on `XRRenderProgram.Separable`.
- The generated vertex program for pipeline mode is created as separable.
- Variant fragment-only programs are also created as separable.

## Current Failure State

Despite the above working correctly, the forward prepass still appears to render nothing useful into the forward-only depth-normal target.

Observed runtime result:

- forward AO debug readback: `centerAo=1.0000`

Interpretation:

- AO remains fully white at the sampled center texel
- that implies the forward prepass depth/normal data is still not contributing in the way expected

## Important Runtime Evidence

Recent diagnostics showed:

- materials do have depth-normal variants
- shaders compile successfully
- draw calls are submitted for both opaque and masked forward passes
- there is still no visible prepass contribution in the AO path

That pushes the likely fault domain away from shader text generation and toward runtime GL state or pipeline behavior.

## Most Likely Remaining Root Causes

### 1. Separable program pipeline validation is missing

- XRENGINE does not currently call `glValidateProgramPipeline`.
- In separable pipeline mode, interface or stage mismatches can fail in ways that do not surface clearly unless the pipeline is explicitly validated and the info log is queried.
- `GLRenderProgramPipeline` currently only binds stages via `UseProgramStages`; it has no validation or info-log reporting.

### 2. Pipeline-mode-only behavior may still be the failing edge

- The forward depth-normal prepass forces shader pipeline mode and forces generated vertex programs.
- That path is different from the normal combined-program rendering path.
- Even though the vertex and fragment interfaces look correct statically, the actual pipeline object is not yet being validated at runtime.

### 3. The failure is likely below material selection

- Material override selection is no longer the primary suspect.
- Variant generation is no longer the primary suspect.
- GLSL conditional compilation is no longer the primary suspect.
- The remaining suspicion is OpenGL runtime state, pipeline assembly, or prepass target interaction.

## Files Most Relevant To The Investigation

- `XREngine.Runtime.Rendering/Resources/Shaders/XRShader.cs`
- `XREngine.Runtime.Rendering/Resources/Shaders/ShaderHelper.cs`
- `XREngine.Runtime.Rendering/Shaders/ForwardDepthNormalVariantFactory.cs`
- `XRENGINE/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Rendering.cs`
- `XRENGINE/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Shaders.cs`
- `XRENGINE/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.cs`
- `XRENGINE/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgramPipeline.cs`
- `XRENGINE/Rendering/Generator/DefaultVertexShaderGenerator.cs`
- `XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_ForwardDepthNormalPrePass.cs`
- `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs`

## Next Steps

1. Add pipeline validation and info-log reporting after binding the separable vertex and fragment programs.
2. Verify whether the pipeline is actually complete and valid during the forced prepass path.
3. If the pipeline validates, inspect render-state differences for the prepass target itself.
4. Remove temporary diagnostic logging once the root cause is confirmed.

## Short Answers To The User Questions

### Do `#define` and `#ifdef` work?

Yes. They work normally through the OpenGL GLSL compiler.

### Should the engine be processing them itself?

No. The engine only needs to preprocess includes and snippets. GLSL conditionals should remain for the driver/compiler.

### Why does the depth prepass variant seem to refer to itself?

Because the variant preserves the original shader file path while changing the in-memory source text by injecting the prepass define.

### Why is the shadow caster variant null?

Because that is expected for many opaque forward shaders. Explicit shadow caster variants are generally only needed for masked or alpha-tested forward materials.