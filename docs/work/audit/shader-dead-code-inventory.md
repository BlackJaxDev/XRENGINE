# Shader Dead Code Inventory

Date: 2025-07-22
Status: Verified via reference tracing (C# code + shader includes + snippet system)

## Method

All files were traced against:
1. C# runtime references (`XRShader.EngineShader(...)`, `ShaderHelper.LoadEngineShader(...)`)
2. Shader `#include` directives
3. `#pragma snippet` resolution (Snippets/ directory, filename-based lazy loading)

Files with **zero** references from any of these sources are classified as dead code.

---

## Dead Code — Root Shaders Directory

| File | Notes | Recommendation |
|------|-------|----------------|
| `SNIP_LightingBasic.fs` | Blinn-Phong forward lighting. Not loaded by C#, not a snippet (wrong directory + prefix), not `#include`-d. Previously cleaned to use shared snippets but still orphaned. | **Delete** or move to `Unused/` |

## Dead Code — Scene3D

| File | Notes | Recommendation |
|------|-------|----------------|
| `PBRShaderStart.glsl` | Shared deferred PBR header. Was intended as a concatenation partial but no C# code performs that concatenation. No shader references it. | **Delete** or move to `Unused/` |
| `PBRShaderEnd.glsl` | Shared deferred PBR footer. Same as PBRShaderStart — orphaned partial. | **Delete** or move to `Unused/` |
| `DeferredLightingDir_Enhanced.fs` | Enhanced directional light with PCSS/contact shadows. Never loaded by C#. Contains pre-cleanup local PBR math and local PI defs. | **Delete** or move to `Unused/` |

## Quarantined — Scene3D/Unused/

Already moved to Unused/ by prior work.

| File | Notes | Recommendation |
|------|-------|----------------|
| `OLD_DeferredLighting.fs` | Legacy monolithic deferred lighting. | **Delete** (superseded by per-light-type shaders) |
| `LightCulling.comp` | Old light culling compute shader. Active version is `ForwardPlus/LightCulling.comp`. | **Delete** (superseded) |

## Quarantined — Compute/Unused/

Already moved to Unused/ by prior work. All have active replacements or were experimental.

| File | Notes | Recommendation |
|------|-------|----------------|
| `ApplyConstraints.comp` | Experimental physics constraint solver. | Defer (may revive) |
| `CalculateParticles.comp` | Experimental GPU particle simulation. | Defer (may revive) |
| `GPURenderRadixSort.comp` | Replaced by current GPU indirect pipeline. | **Delete** |
| `GPURenderSorting.comp` | Replaced by current GPU indirect pipeline. | **Delete** |
| `HiZCull.comp` | Replaced by `GPURenderOcclusionHiZ.comp`. | **Delete** |
| `MeshSDFGen_Advanced.comp` | Advanced SDF variant. Active version is `MeshSDFGen.comp`. | Defer (may revive for quality tier) |
| `Skinning.comp` | Replaced by `SkinningPrepass.comp` / `SkinningPrepassInterleaved.comp`. | **Delete** |
| `README.md` | Documents the quarantine directory. | Keep |

---

## Summary

- **4 files** in active directories are dead code (recommend delete or move to Unused/)
- **2 files** in Scene3D/Unused/ can be deleted (superseded)
- **7 compute files** in Compute/Unused/: 4 recommend delete, 2 defer, 1 keep (README)
