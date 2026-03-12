# Depth-Normal Pre-Pass Design

> Status: **Phase 1 implemented (vertex normals only), Phase 2 proposed (normal-mapped normals)**

## Problem Statement

The default render pipeline runs ambient occlusion (AO) after deferred geometry but **before** forward geometry renders. This means:

1. Forward meshes don't contribute depth to AO — they appear to "float" without contact shadows
2. Forward meshes don't contribute normals to the GBuffer — AO algorithms that use normals for hemisphere orientation (SSAO, HBAO+, GTAO) produce incorrect results at forward-covered pixels

The fix is a pre-pass that renders forward geometry depth and normals into the shared GBuffer textures before the AO resolve step.

## Current Implementation (Phase 1)

### What renders in the pre-pass

**Forward geometry only** — `OpaqueForward` and `MaskedForward` passes.

Deferred geometry (`OpaqueDeferred`, `DeferredDecals`) does **not** need a pre-pass because it already renders to the GBuffer (including depth and normals) before the AO resolve. The existing pipeline sequence is:

```
1. AO FBO setup         — VPRC_SSAOPass etc. create FBOs, textures, materials (resource factory)
2. Bind AO FBO          — OpaqueDeferred + DeferredDecals render → populates depth, normals,
                           albedo, RMSE, transformId into GBuffer attachments
3. Forward pre-pass     — OpaqueForward + MaskedForward render depth + normals (NEW)
4. AO resolve           — Full-screen quad reads depth + normals → produces AO intensity texture
5. LightCombine         — Deferred lighting reads AO texture
6. Forward color pass   — Forward meshes render again with full lighting + AO sampling
```

### Components

| File | Role |
|------|------|
| `Shaders/Common/DepthNormalPrePass.fs` | Override fragment shader — outputs `normalize(FragNorm)` to `location 0`, depth writes implicitly |
| `VPRC_ForwardDepthNormalPrePass.cs` | Pipeline command — pushes override material, forces generated vertex program, renders mesh passes |
| `DefaultRenderPipeline.FBOs.cs` — `CreateForwardDepthPrePassFBO()` | FBO with `Normal` (color0) + `DepthStencil` (depth/stencil) attachments |
| `DefaultRenderPipeline.FBOs.cs` — `CreateDepthNormalPrePassMaterial()` | Override material with `DepthNormalPrePass.fs`, depth test Lequal, depth write true |
| `DefaultRenderPipeline.cs` — constructor + lazy field | `_depthNormalPrePassMaterial` lazily constructed |
| `DefaultRenderPipeline.cs` — `CreateViewportTargetCommands()` | Pre-pass inserted between deferred GBuffer rendering and AO resolve |

### Override mechanism

The pre-pass uses the same `PushOverrideMaterial` + `PushForceShaderPipelines` + `PushForceGeneratedVertexProgram` stack as the motion vectors pass:

```csharp
using var overrideTicket = ActivePipelineInstance.RenderState.PushOverrideMaterial(material);
using var pipelineTicket = ActivePipelineInstance.RenderState.PushForceShaderPipelines();
using var generatedVertexTicket = ActivePipelineInstance.RenderState.PushForceGeneratedVertexProgram();
```

This completely replaces each mesh's fragment shader with the pre-pass shader. The engine-generated vertex program remains, which provides `FragNorm` (location 1) for all meshes.

### FBO layout

```
ForwardDepthPrePassFBO:
  ColorAttachment0  →  Normal texture (RGB16F, shared with GBuffer)
  DepthStencil      →  DepthStencil texture (Depth24Stencil8, shared with GBuffer)
```

No clear is performed on bind (`clearColor=false, clearDepth=false, clearStencil=false`) — the deferred pass has already populated both textures with deferred geometry data. The forward pre-pass only **adds** forward geometry on top.

### Limitation: vertex normals only

The override material replaces the entire material, including all texture bindings. The pre-pass shader only has access to the interpolated vertex normal (`FragNorm`) — it **cannot** perform normal mapping because:

1. The original mesh material's normal map texture (`Texture1`) is not bound
2. The override system is full-material replacement with no texture merging
3. `FragTan`/`FragBinorm`/`FragUV0` are emitted by the generated vertex shader but the pre-pass fragment shader doesn't read them

This means forward meshes with normal maps will contribute **smooth vertex normals** to the GBuffer rather than detailed normal-mapped normals. For AO (a low-frequency effect), this is acceptable — the depth contribution is far more impactful than normal detail. But it's not ideal.

## Proposed Phase 2: Normal-Mapped Pre-Pass

### Goal

Support full normal mapping in the pre-pass so AO sees accurate surface detail for forward meshes.

### Approach A: Depth-Normal Shader Permutation System

Instead of a single override fragment shader, generate a **depth-normal variant** for each material's fragment shader. This variant strips all lighting code but preserves normal map sampling.

**Concept:**

```
Original shader:                    Depth-normal variant:
─────────────────                   ─────────────────────
layout(0) out vec4 OutColor;        layout(0) out vec3 Normal;
                                    
#pragma snippet "NormalMapping"     #pragma snippet "NormalMapping"
#pragma snippet "AO"                
#pragma snippet "Lighting"          
                                    
void main() {                       void main() {
  vec3 N = perturbNormal(TBN,       vec3 N = perturbNormal(TBN,
    texture(Texture1, uv).rgb);       texture(Texture1, uv).rgb);
  vec3 lighting = ...;              Normal = N;
  OutColor = vec4(result, 1.0);     }
}
```

**Pros:**
- Full normal map accuracy
- Uses the mesh's own textures (no override needed — render with original material)
- Can be automated via snippet/pragma if a convention is established

**Cons:**
- Requires a shader compilation step per material variant — more GPU program objects
- Need a convention to strip lighting and re-target outputs

### Approach B: Texture Passthrough Override

Extend the override material system so the pre-pass material can access the original mesh material's texture bindings.

**Concept:** Add a `PushOverrideMaterialWithTexturePassthrough` method that replaces the fragment shader program but binds textures from the original material.

This would require changes to:
- `RenderingState` — new stack entry type that remembers the "original" material
- `GLMeshRenderer.Rendering.cs` — `GetRenderMaterial()` path that returns the override material but passes original textures
- `GLMaterial.SetUniforms()` — texture binding path that merges slots

A simpler variant: add a flag `PreserveOriginalTextures` on the override. When active, after the override material's shader program is bound, bind the original material's textures instead of the override material's (empty) textures.

**Pros:**
- Single pre-pass shader works for all meshes
- No extra shader compilation
- Original material's TBN, normal map, and UV coordinates all work naturally

**Cons:**
- Requires renderer-level changes (GLMeshRenderer, RenderingState)
- The pre-pass shader must declare compatible texture uniforms (`Texture1` for normal map)
- Must handle meshes that don't have a normal map (fallback to vertex normal)
- Texture slot conventions must be consistent across all materials

### Approach C: Dual-Output Deferred Shaders (GBuffer Normal + Depth Pre-Pass Combined)

Restructure so that every shader writes normals to a shared output, and the pre-pass simply re-renders forward meshes using their **own** material but targeting only the normal+depth outputs.

This is effectively what the deferred pass already does — the AO FBO has the Normal texture attached at `ColorAttachment1`. The issue is that forward shaders write to `OutColor` (lit result), not to a GBuffer normal layout.

If forward shaders could optionally write normals (via a `#define DEPTH_NORMAL_PREPASS`), the same material could be used in both passes:

```glsl
#ifdef DEPTH_NORMAL_PREPASS
layout(location = 0) out vec3 Normal;
void main() {
    Normal = getNormalFromMap();  // normal map logic preserved
}
#else
layout(location = 0) out vec4 OutColor;
void main() {
    // ... full lighting ...
}
#endif
```

**Pros:**
- Normal map support with zero new infrastructure
- Uses mesh's own material and textures
- No override mechanism needed

**Cons:**
- Requires #define injection mechanism (does the engine support per-draw defines?)
- All forward shaders need the `DEPTH_NORMAL_PREPASS` ifdef block
- Two compilation variants per forward shader

### Recommended Path

**Phase 2a — Approach B (Texture Passthrough Override)**

This is the most pragmatic next step:

1. Add `PushOverrideMaterialPreservingTextures(XRMaterial override)` to `RenderingState`
2. In `GLMeshRenderer.Rendering.cs`, when this mode is active: use the override's shader program but bind the original material's textures
3. Update `DepthNormalPrePass.fs` to declare `Texture1` (normal map) and `FragTan`/`FragBinorm`/`FragUV0` inputs, with a `uniform bool HasNormalMap` fallback
4. The pre-pass material sets `HasNormalMap = false` as default; the renderer overrides it based on whether the original material has a texture at slot 1

**Phase 2b — Approach C (Ifdef permutations)** as a long-term clean solution when the shader compilation infrastructure supports per-draw defines.

## Normal Encoding

### Current: Uncompressed RGB16F

The GBuffer normal texture is `RGB16F` (3 × 16-bit float). All consumers read with `texture(Normal, uv).rgb` — no encoding or decoding. Normals are world-space, range `[-1, 1]`.

### Future: Octahedral RG16F

Octahedral encoding maps a unit normal to 2 channels, saving 33% bandwidth:

```glsl
// Encode (write side)
vec2 OctEncode(vec3 n) {
    n /= (abs(n.x) + abs(n.y) + abs(n.z));
    if (n.z < 0.0)
        n.xy = (1.0 - abs(n.yx)) * vec2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    return n.xy * 0.5 + 0.5;
}

// Decode (read side)
vec3 OctDecode(vec2 f) {
    f = f * 2.0 - 1.0;
    vec3 n = vec3(f, 1.0 - abs(f.x) - abs(f.y));
    float t = clamp(-n.z, 0.0, 1.0);
    n.xy -= vec2(n.x >= 0.0 ? t : -t, n.y >= 0.0 ? t : -t);
    return normalize(n);
}
```

This would require updating:
- Normal texture format: `RGB16F` → `RG16F`
- All deferred shader normal writes (11 shaders)
- All forward pre-pass normal writes
- All normal consumers: DeferredLightCombine, all AO passes, GI composites, decals, motion vectors, etc.
- The `NormalMapping.glsl` snippet could gain `XRENGINE_EncodeNormal()` / `XRENGINE_DecodeNormal()` functions

This is a separate effort from the pre-pass and should be evaluated independently based on bandwidth profiling.

## GBuffer Attachment Reference

For context, the AO FBO (which renders deferred geometry) has these attachments:

| Attachment | Texture | Format | Pre-pass writes? |
|---|---|---|---|
| `ColorAttachment0` | AlbedoOpacity | RGBA16F | No |
| `ColorAttachment1` | Normal | RGB16F | **Yes** (via separate pre-pass FBO) |
| `ColorAttachment2` | RMSE | RGBA8 | No |
| `ColorAttachment3` | TransformId | R32UI | No |
| `DepthStencilAttachment` | DepthStencil | Depth24Stencil8 | **Yes** (via separate pre-pass FBO) |

The pre-pass FBO attaches only Normal (color0) and DepthStencil — it does not write albedo, RMSE, or TransformId.

## Forward AO Sampling (Companion Feature)

Forward shaders also **consume** the AO texture during their color pass. This is handled by:

- `AmbientOcclusionSampling.glsl` snippet — declares `AmbientOcclusionTexture` sampler (unit 14) and `XRENGINE_SampleAmbientOcclusion()` helper
- `Lights3DCollection.SetForwardLightingUniforms()` — binds AO texture and `AmbientOcclusionEnabled` flag
- All 20 lit forward shader variants include the snippet and call the helper

This is the read-side complement to the depth-normal pre-pass write side.

## Performance Considerations

The pre-pass adds one extra rendering of forward opaque geometry per frame. This is typically a small set of meshes (most opaque geometry is deferred). The cost is:

- **Vertex processing**: full transform + skinning — same as the later forward color pass
- **Fragment processing**: minimal — single `normalize()` + a normal texture write
- **Bandwidth**: one `RGB16F` write per pixel (normal) + depth write (already happens in color pass)
- **Draw call overhead**: same draw calls as the forward color pass, but with a lightweight override shader

For scenes with few forward meshes, the cost is negligible. For scenes with many forward meshes, the pre-pass could be gated behind an AO quality setting.

## Related Files

- `Build/CommonAssets/Shaders/Common/DepthNormalPrePass.fs` — Pre-pass fragment shader
- `Build/CommonAssets/Shaders/Snippets/AmbientOcclusionSampling.glsl` — Forward AO consumption
- `Build/CommonAssets/Shaders/Snippets/NormalMapping.glsl` — Normal map utilities
- `XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_ForwardDepthNormalPrePass.cs` — Pre-pass command
- `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs` — Pipeline orchestration
- `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.FBOs.cs` — FBO + material creation
- `XRENGINE/Rendering/Pipelines/RenderingState.cs` — Override material stack
- `XRENGINE/Rendering/Generator/DefaultVertexShaderGenerator.cs` — Generated vertex varyings
- `docs/features/gi/ambient-occlusion.md` — User-facing AO documentation
