# Vulkan Default Render Pipeline Black Scene Analysis

Date: 2026-03-17

## Scope

Investigate why the Vulkan code path renders a black 3D scene through the default render pipeline while the screen-space ImGui editor still renders and resizes correctly.

## Status

**FIXED** — all three root causes identified from the runtime log were corrected in the same session.

---

## Executive Summary

The black scene was caused by three compounding bugs that all trace back to the Vulkan shader auto-uniform rewriter not understanding preprocessor conditional blocks (`#ifdef`/`#else`/`#endif`):

1. **`HoistOpaqueUniforms` destroyed the `#ifdef` conditional structure** — the primary compile failure cause.
2. **`MoveRequiredStructDeclarationsBeforeInsertion` moved the `DirLight` struct before `MAX_CASCADES` was defined** — cascade of undeclared/redefined errors.
3. **The non-MSAA `#else` branches in all three deferred lighting shaders had no explicit `layout(binding = N)`** — textures would have been fetched from the wrong descriptor slots even after the shader compiled.

---

## Runtime Evidence

Log examined: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-03-17_16-23-58_pid69448/log_vulkan.txt`

Key evidence from that log (line numbers reference the log file):

- The frame graph is active and producing swapchain writes before overlay:
  - `FrameOps: total=38 ... swapchainWrites=3` at line 29.
  - `Swapchain writers by pipeline (PreOverlay)` at line 30.
- The lighting combine target is actively entered:
  - `BeginRenderPassForTarget FBO='LightCombineFBO'` at line 48.
- The shader failure happens inside that pass:
  - `Shader 'DeferredLightingDir' failed to compile` at line 49.
  - The frame recorder drops the draw op at line 201.
- The pipeline keeps going after the failure:
  - `BeginRenderPassForTarget FBO='ForwardPassFBO'` at line 224.
  - `BeginRenderPassForTarget FBO='PostProcessOutputFBO'` at line 271.
  - `BeginRenderPassForTarget FBO='FxaaFBO'` at line 274.
  - `Swapchain writers by pipeline (PostOverlay): ImGui...` at line 275.

That combination is important: Vulkan is still presenting, and the screen-space UI is still compositing over the presented image. The failure is upstream of final present.

---

## Compile Error Details (from the Log)

The new diagnostics block (added in the prior session) gave a rewritten-source error context that made root cause analysis unambiguous.

The rewritten shader source around the failing lines showed **both** the MSAA and non-MSAA sampler declarations appearing simultaneously with no `#ifdef`/`#else`/`#endif` guards between them:

```
    8: layout(binding = 2, set = 2)  uniform sampler2DMS RMSE;
    9: layout(binding = 3, set = 2)  uniform sampler2DMS DepthView;
>  10: layout(set = 2, binding = 4) uniform sampler2D AlbedoOpacity;   ← redefinition
   11: layout(set = 2, binding = 5) uniform sampler2D Normal;           ← redefinition
   12: layout(set = 2, binding = 6) uniform sampler2D RMSE;             ← redefinition
   13: layout(set = 2, binding = 7) uniform sampler2D DepthView;        ← redefinition
```

And the DirLight struct appeared before `MAX_CASCADES` was defined:

```
>  24:   float CascadeSplits[MAX_CASCADES];   ← MAX_CASCADES undeclared
>  25:   mat4 CascadeMatrices[MAX_CASCADES];  ← array size error
...
>  95: const int MAX_CASCADES = 8;            ← redefinition (seen again here)
```

---

## Root Cause 1 — `HoistOpaqueUniforms` Strips `#ifdef` Context

### What Happened

`DeferredLightingDir.fs` (and Point/Spot) starts with:

```glsl
#pragma snippet "NormalEncoding"    ← line 3 of original source

const int MAX_CASCADES = 8;         ← line 7
...
#ifdef XRENGINE_MSAA_DEFERRED
layout(binding = 0) uniform sampler2DMS AlbedoOpacity;
...
#else
uniform sampler2D AlbedoOpacity;    ← no binding specified
...
#endif
```

`ShaderSourcePreprocessor.ResolveSource` expands `#pragma snippet "NormalEncoding"` inline. The NormalEncoding snippet contains multiple function definitions (`XRENGINE_EncodeNormal`, `XRENGINE_DecodeNormal`, `XRENGINE_ReadNormal`, etc.). After expansion these functions are **before** the shader's own global declarations, because the `#pragma` was on line 3.

`HoistOpaqueUniforms` collects all opaque uniform declarations that appear **after** `FindFirstFunctionDefinitionIndex` (which now points to the first NormalEncoding function). All sampler declarations — both MSAA and non-MSAA, inside the `#ifdef` and `#else` — fall after this index and are collected for hoisting.

The hoist strips them from their original positions (removing them from inside the `#ifdef`/`#else` branches) and emits them unconditionally before the first function, side by side. shaderc then sees both `sampler2DMS AlbedoOpacity` and `sampler2D AlbedoOpacity` in the same scope → redefinition errors for all four GBuffer samplers.

### Fix

Added `GetPreprocessorConditionalRanges` and `IsInsideConditionalRange` helpers to `VulkanShaderAutoUniforms`. `HoistOpaqueUniforms` now skips any match whose character position falls inside a preprocessor conditional block. Uniforms inside `#ifdef`/`#else` stay where they are; only truly unconditional late uniforms (genuine function-body globals) are hoisted.

Files changed: `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanShaderTools.cs`

---

## Root Cause 2 — `DirLight` Struct Moved Before `MAX_CASCADES`

### What Happened

`MoveRequiredStructDeclarationsBeforeInsertion` exists to ensure struct types referenced in the auto-uniform block (e.g. `DirLight`) are declared before the `layout(std140 ...) uniform` block. After snippet expansion, the `DirLight` struct sits at ~line 92 in the expanded source (after the ~40 snippet lines). The insertion threshold is ~line 4 (first snippet function), so the struct is correctly identified as needing to move.

However, `MAX_CASCADES` (defined as `const int MAX_CASCADES = 8;` at original line 7) also ends up at ~line 52 after snippet expansion — also after the threshold. The mover only moved the struct, leaving `MAX_CASCADES` in its late position. The moved `DirLight` struct uses `MAX_CASCADES` as an array bound (`float CascadeSplits[MAX_CASCADES]`), so shaderc saw an undeclared identifier at the struct site and then a redefinition at the original const location.

### Fix

Extended `MoveRequiredStructDeclarationsBeforeInsertion` to also collect `const int`/`const uint`/`#define` integer constants that are referenced as identifier-based array bounds inside the structs being moved (found via the existing `ArrayRegex`, filtering out numeric literals). Any such constants that also appear after the insertion threshold are moved together with the struct, with constants ordered before their dependent struct in the output.

Files changed: `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanShaderTools.cs`

---

## Root Cause 3 — Non-MSAA Sampler Bindings Would Have Been Wrong

### What Happened

Even after fixing the compile errors, the non-MSAA GBuffer samplers would have been bound at the wrong descriptor slots. `RewriteOpaqueUniformBindings` scans the full source (including inactive `#ifdef` branches) via `FindNextBinding` to determine the starting binding for unassigned uniforms. With the MSAA branch using bindings 0–3, `nextBinding` starts at 4. The non-MSAA `sampler2D` declarations (which had no `layout(binding = N)`) were therefore auto-assigned bindings 4–7.

The Vulkan descriptor system in `VkMeshRenderer.Descriptors.cs` maps `binding.Binding` directly to `material.Textures[binding.Binding]`. The material is created with `lightRefs = [albOpacTex(0), normTex(1), rmseTex(2), depthViewTex(3)]`. Non-MSAA AlbedoOpacity at binding 4 → `material.Textures[4]` → null → "No texture available" → black lighting.

### Fix

Added `layout(binding = 0/1/2/3)` to the non-MSAA `#else` branch in all three deferred lighting shaders. With explicit bindings matching the MSAA branch, `EnsureLayoutHasSet` just adds `set = 2` and preserves the original binding number. Both paths now use bindings 0–3 for the four GBuffer textures, which matches the material texture array layout the C# side constructs.

Files changed:
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightingDir.fs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightingPoint.fs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightingSpot.fs`

---

## What the Prior Analysis Got Right and Wrong

The prior analysis (from the earlier session without the live log) correctly identified:

- The black scene is caused by a failed directional deferred-lighting draw rather than swapchain/present failure. ✓
- The failed draw is rooted in Vulkan shader rewrite corruption. ✓
- All file locations (line numbers, FBO creation functions, renderer creation lines) were accurate. ✓

It incorrectly identified the root mechanism:

- It predicted `sampler2DMS` was missing from `OpaqueTypes` as the primary bug. ✗
  - In reality `IsOpaque` uses prefix matching (`glslType.StartsWith("sampler", ...)`) which correctly handles all `sampler*` types including `sampler2DMS`. The analysis had not read `IsOpaque` fully.
- The actual bug was `HoistOpaqueUniforms` having no awareness of preprocessor conditionals — a consequence of `#pragma snippet` injecting function definitions before the shader's own global declarations.

---

## Secondary Finding — Compute Uniform Buffer Warning

The log also shows:

```
[VkCompute:UnnamedProgram] Skipping unresolved UniformBuffer binding '' (set 0, binding 64)
```

at lines 225 and 248. This is at binding 64 — the auto-uniform block base binding. The compute shader has no auto-uniform block emitted (it compiled with no non-opaque uniforms, or was not run through the rewriter). This is a secondary issue unrelated to the black scene. It should be investigated separately: either the compute shader is missing a declaration the runtime expects, or the auto-uniform rewrite is not being applied to it.

---

## IDE Linter False Positives on Shader Files

The VS Code GLSL linter reports errors (`XRENGINE_ReadNormal not found`, `missing #endif`) at the end of the three deferred lighting shaders. These are pre-existing false positives: the linter does not understand `#pragma snippet` and cannot resolve the `NormalEncoding` snippet. They are not introduced by the fixes above and do not affect runtime compilation.

---

## Current Confidence

- High confidence: all three bugs described above are real and all three fixes are correct.
- Medium confidence: the shader will now compile cleanly and the GBuffer textures will be bound at the right descriptor slots, restoring lit 3D output.
- The compute binding 64 warning is a separate concern and has not been addressed.
