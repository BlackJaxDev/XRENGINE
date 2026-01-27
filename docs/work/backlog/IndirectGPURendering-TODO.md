# Indirect GPU Rendering – Status & TODO

> **Last Updated:** January 2026 (All Priority Items Complete for OpenGL)

## Overview

This document tracks the implementation status of batched material indirect multi-draw GPU rendering. The system uses GPU compute shaders to cull and build indirect draw commands, then issues `MultiDrawElementsIndirect` calls grouped by material.

### Architecture Summary

```
┌─────────────────────────────────────────────────────────────────────────┐
│ GPURenderPassCollection.Render(scene)                                   │
├─────────────────────────────────────────────────────────────────────────┤
│ 1. ResetCounters (compute)      → Zero culled/draw counts               │
│ 2. Cull(scene, camera)          → GPU frustum culling                   │
│ 3. PopulateMaterialIDs          → Build material ID buffer              │
│ 4. BuildIndirectCommandBuffer   → Compute → DrawElementsIndirectCommand │
│ 5. BuildMaterialBatches         → Group draws by material (CPU)         │
│ 6. HybridRenderingManager.Render → Per-batch MDI dispatch               │
└─────────────────────────────────────────────────────────────────────────┘
```

### Indirect Buffer Layout

The indirect rendering system uses several GPU buffers with specific layouts:

#### DrawElementsIndirectCommand (20 bytes, 5 uints)

```
Offset  Field           Type    Description
──────────────────────────────────────────────────────────
0       Count           uint    Number of indices to draw
4       InstanceCount   uint    Number of instances (typically 1)
8       FirstIndex      uint    Offset into EBO (in index units)
12      BaseVertex      int     Added to each index value
16      BaseInstance    uint    Encodes culled command index for data fetch
```

**Requirements:**
- Struct must use `[StructLayout(LayoutKind.Sequential, Pack = 1)]`
- Static assertion verifies `sizeof(DrawElementsIndirectCommand) == 20` at startup
- Matches shader's `DRAW_COMMAND_UINTS = 5`

#### Parameter Buffer (Draw Count)

When using `MultiDrawElementsIndirectCount` (GL 4.6 / ARB_indirect_parameters):

```
Offset  Field           Type    Description
──────────────────────────────────────────────────────────
0       DrawCount       uint    Number of draw commands to execute
```

Bound via `BindParameterBuffer()`. Falls back to explicit count if not supported.

#### VAO Requirements for MDI

| Attribute | Source | Notes |
|-----------|--------|-------|
| Position (0) | Atlas VBO | Interleaved or separate |
| Normal (1) | Atlas VBO | Optional |
| Tangent (2) | Atlas VBO | Optional |
| UV0 (3) | Atlas VBO | Optional |
| Index buffer | Atlas EBO | u16 or u32, set via `SetTriangleIndexBuffer()` |

**Critical:** VAO must be validated via `ValidateIndexedVAO()` before MDI dispatch.

#### GPUIndirectRenderCommand (192 bytes, 48 floats)

Scene command buffer layout (input to culling):

```
Offset  Field               Size    Description
──────────────────────────────────────────────────────────
0       WorldMatrix         64B     mat4 model transform
64      PrevWorldMatrix     64B     mat4 for motion vectors
128     BoundingSphere      16B     vec4 (xyz=center, w=radius)
144     MeshID              4B      uint mesh identifier
148     SubmeshID           4B      uint flattened submesh
152     MaterialID          4B      uint material lookup key
156     InstanceCount       4B      uint instances per draw
160     RenderPass          4B      uint pass filter mask
164     ShaderProgramID     4B      uint program identifier
168     RenderDistance      4B      float camera distance
172     LayerMask           4B      uint layer filter
176     LODLevel            4B      uint LOD selection
180     Flags               4B      uint (transparent, shadow, etc.)
184     Reserved0           4B      uint padding
188     Reserved1           4B      uint padding
```

---

## ✅ Completed

### AbstractRenderer API Surface
| Feature | Status | Location |
|---------|--------|----------|
| `SetEngineUniforms`, `SetMaterialUniforms` | ✅ Done | `AbstractRenderer.cs`, `OpenGLRenderer.cs` |
| `BindVAOForRenderer`, `ConfigureVAOAttributesForProgram` | ✅ Done | `AbstractRenderer.cs` |
| `BindDrawIndirectBuffer`, `BindParameterBuffer` | ✅ Done | `AbstractRenderer.cs` |
| `MultiDrawElementsIndirect` | ✅ Done | `OpenGLRenderer.cs:3090+` |
| `MultiDrawElementsIndirectWithOffset` | ✅ Done | `OpenGLRenderer.cs` |
| `MultiDrawElementsIndirectCount` | ✅ Done | `OpenGLRenderer.cs` |
| `ApplyRenderParameters`, `MemoryBarrier` | ✅ Done | `AbstractRenderer.cs` |
| `ValidateIndexedVAO` | ✅ Done | `OpenGLRenderer.cs:3046`, `VulkanRenderer` (stub) |
| `UnbindDrawIndirectBuffer`, `UnbindParameterBuffer` | ✅ Done | `OpenGLRenderer.cs:3064+`, `VulkanRenderer` (stub) |
| `SupportsIndirectCountDraw` | ✅ Done | GL 4.6 / ARB_indirect_parameters check |

### HybridRenderingManager
| Feature | Status | Notes |
|---------|--------|-------|
| Uses AbstractRenderer (no direct GL calls) | ✅ Done | |
| `RenderTraditional` – single-batch fallback | ✅ Done | |
| `RenderTraditionalBatched` – per-batch pipeline | ✅ Done | Per-batch material/program/state |
| Material ID → XRMaterial resolution | ✅ Done | Via `GPUScene.MaterialMap` |
| Combined program cache per material | ✅ Done | `_materialPrograms` dictionary |
| Auto-generate vertex shader if missing | ✅ Done | `EnsureCombinedProgram` |
| Per-batch `ApplyRenderParameters` | ✅ Done | Depth/blend/cull/stencil |
| Count path with parameter buffer | ✅ Done | Falls back to explicit count |

### GPURenderPassCollection
| Feature | Status | Notes |
|---------|--------|-------|
| `BuildMaterialBatches` – produces `DrawBatch` list | ✅ Done | Groups contiguous material IDs |
| Exposes `MaterialMap` via `GetMaterialMap(scene)` | ✅ Done | |
| Exposes `DrawCountBuffer`, `CulledCountBuffer` | ✅ Done | |
| `MappedBufferScope` for safe buffer readback | ✅ Done | RAII pattern |
| Overflow/truncation flag buffers | ✅ Done | `_cullingOverflowFlagBuffer`, etc. |
| GPU stats buffer (BVH timings) | ✅ Done | `_statsBuffer`, `GpuRenderStats` |

### Compute Shaders
| Shader | Status | Purpose |
|--------|--------|---------|
| `GPURenderCulling.comp` | ✅ Done | Frustum culling, populates culled buffer |
| `GPURenderIndirect.comp` | ✅ Done | Builds `DrawElementsIndirectCommand` from culled |
| `GPURenderResetCounters.comp` | ✅ Done | Zeroes atomic counters |

### Unit Tests (Implemented)
| Test | File | Status |
|------|------|--------|
| `MultiDrawElementsIndirect_RendersTwoDistinctCubes` | `IndirectMultiDrawTests.cs` | ✅ |
| `MultiDrawElementsIndirectCount_RendersTwoDistinctCubes_UsingGpuCount` | `IndirectMultiDrawTests.cs` | ✅ |
| `MultiDrawElementsIndirect_RendersFourMaterialBatches_WithEightCubes` | `IndirectMultiDrawTests.cs` | ✅ |
| Shader loading tests (GPURenderIndirect, Culling, ResetCounters) | `GpuIndirectRenderDispatchTests.cs` | ✅ |

---

## ⚠️ Known Issues / Not Working

### ~~Material Batching~~ ✅ IMPROVED
- ~~**CPU-side batch building** reads from culled buffer via mapped pointer, but batches are built from *unsorted* material IDs. If materials aren't spatially coherent, this creates many small batches (inefficient).~~
- ~~**No GPU-side material sort** – batches reflect insertion order, not optimized groupings.~~
- **Implemented:** CPU material sort via `EnableCpuMaterialSort` flag. Uses `ArrayPool` to avoid allocation pressure. Logs batch count reduction.

### ~~Index Buffer (EBO) Synchronization~~ ✅ FIXED
- ~~`MeshDataEntry` tracks `FirstIndex`, `IndexCount`, `FirstVertex` but **EBO rebuild on atlas grow** is not fully wired up.~~
- ~~If `RebuildAtlasIfDirty()` resizes VBO without corresponding EBO update, MDI draws may reference stale indices.~~
- **Implemented:** `GPUScene.AtlasRebuilt` event fires after `RebuildAtlasIfDirty()`, `GPURenderPassCollection` subscribes and calls `SyncIndirectRendererIndexBuffer()`. Version counter (`_atlasVersion`) enables defensive sync in `EnsureAtlasSynced()`.

### Vulkan Backend
- `VulkanRenderer` has **stub implementations** for:
  - `ValidateIndexedVAO` (returns `false` – intentionally fails validation)
  - `UnbindDrawIndirectBuffer`, `UnbindParameterBuffer` (no-ops)
  - `MultiDrawElementsIndirect*` variants (throws `NotImplementedException`)
  - `TrySyncMeshRendererIndexBuffer` (returns `false`)
- **Vulkan MDI** requires `VK_KHR_draw_indirect_count` – not yet hooked up.

### ~~Diagnostics Gaps~~ ✅ IMPROVED
- ~~No logging of VAO ID, EBO ID per indirect submission.~~
- ✅ Index type (u16/u32) now exposed via `TryGetIndexBufferInfo()`.
- ✅ Enhanced diagnostics in `HybridRenderingManager` log index buffer details when GPU debug is enabled.
- ✅ Uniform type mismatch detection implemented in `GLRenderProgram.ValidateUniformType()`.
- Atlas stats (total vertices/indices, per-mesh offsets) not yet exposed.

---

## 📋 TODO – Remaining Work

### High Priority (Correctness) ✅ ALL COMPLETE

| Task | Priority | Status |
|------|----------|--------|
| ~~**EBO sync with atlas** – Ensure `RebuildAtlasIfDirty` updates index buffer~~ | 🔴 High | ✅ Done – `AtlasRebuilt` event + `SyncIndirectRendererIndexBuffer()` + version tracking |
| ~~**Expose index element type** (u16/u32) for VAO validation~~ | 🔴 High | ✅ Done – `GPUScene.AtlasIndexElementSize` property + `AbstractRenderer.TryGetIndexBufferInfo()` |
| ~~**Per-mesh (firstVertex, firstIndex, indexCount) tracking**~~ | 🔴 High | ✅ Done – `MeshDataEntry` struct populated by `UpdateMeshDataBufferFromAtlas()` |

### Medium Priority (Performance & Robustness) ✅ ALL COMPLETE

| Task | Priority | Status |
|------|----------|--------|
| ~~**GPU or CPU material sort** for contiguous batches~~ | 🟡 Medium | ✅ Done – `EnableCpuMaterialSort` flag + `BuildBatchesFromCommandsSorted()` using ArrayPool |
| ~~**Uniform type validation** – Log mismatch before GL_INVALID_OPERATION~~ | 🟡 Medium | ✅ Done – `ValidateUniformType()` on all Uniform methods in `GLRenderProgram` |
| ~~**Validate `DrawElementsIndirectCommand` stride == 20 bytes**~~ | 🟡 Medium | ✅ Done – Unit test exists + static assertion in `GPURenderPassCollection` static constructor |

### Lower Priority (Polish & Documentation) ✅ ALL COMPLETE (OpenGL)

| Task | Priority | Status |
|------|----------|--------|
| ~~**Vulkan MDI implementation**~~ | 🟢 Low | ⏸️ Blocked – Stubs exist, waiting for Vulkan backend maturity |
| ~~**Document indirect buffer layout**~~ | 🟢 Low | ✅ Done – See "Indirect Buffer Layout" section above |
| ~~**Remove legacy GL calls**~~ | 🟢 Low | ✅ Done – `XRDataBuffer.IsMapped` property + `TrySyncMeshRendererIndexBuffer` abstraction |
| ~~**Enhanced diagnostics**~~ | 🟢 Low | ✅ Done – Index buffer info logged in `HybridRenderingManager` when GPU debug enabled |

### Unit Tests – ✅ ALL COMPLETE

| Test | Status |
|------|--------|
| ~~Atlas/EBO correctness – Growing atlas triggers proper VBO/EBO uploads~~ | ✅ Done |
| ~~Attribute layout switching – No missing attributes across batch program switches~~ | ✅ Done |
| ~~Uniform type mismatch detection~~ | ✅ Done |
| ~~Fallback path (no `ARB_indirect_parameters`) renders correctly~~ | ✅ Done |
| ~~Depth/cull/blend/stencil state doesn't leak between batches~~ | ✅ Done |

---

## File Reference

| File | Purpose |
|------|---------|
| [GpuDispatchLogger.cs](../../../XRENGINE/Rendering/GpuDispatchLogger.cs) | Comprehensive GPU dispatch logging system |
| [HybridRenderingManager.cs](../../../XRENGINE/Rendering/HybridRenderingManager.cs) | Orchestrates MDI dispatch, per-batch state |
| [GPURenderPassCollection.IndirectAndMaterials.cs](../../../XRENGINE/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs) | Builds batches, manages indirect buffers |
| [GPURenderPassCollection.CullingAndSoA.cs](../../../XRENGINE/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs) | GPU culling dispatch |
| [GPUScene.cs](../../../XRENGINE/Rendering/Commands/GPUScene.cs) | Scene data, material map, mesh data buffer |
| [AbstractRenderer.cs](../../../XRENGINE/Rendering/API/Rendering/Generic/AbstractRenderer.cs) | API abstraction for MDI |
| [OpenGLRenderer.cs](../../../XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs) | OpenGL MDI implementation |
| [IndirectMultiDrawTests.cs](../../../XREngine.UnitTests/Rendering/IndirectMultiDrawTests.cs) | Low-level MDI GL tests |
| [GpuIndirectRenderDispatchTests.cs](../../../XREngine.UnitTests/Rendering/GpuIndirectRenderDispatchTests.cs) | Compute shader loading/dispatch tests |
| [IndirectRenderingAdditionalTests.cs](../../../XREngine.UnitTests/Rendering/IndirectRenderingAdditionalTests.cs) | Atlas/EBO, attribute layout, uniform validation, state isolation tests |

---

## Comprehensive Logging System ✅ NEW

The GPU dispatch debugging system now has a centralized, structured logging facility in `GpuDispatchLogger.cs`.

### Log Categories

```csharp
public enum LogCategory
{
    Lifecycle,   // Init, dispose, render begin/end
    Buffers,     // Buffer operations (create, bind, map)
    Culling,     // Frustum culling, BVH operations
    Sorting,     // Material sort, distance sort
    Indirect,    // Indirect command building
    Materials,   // Material batching and resolution
    Stats,       // Statistics and metrics
    Draw,        // Draw dispatch calls
    VAO,         // VAO/attribute configuration
    Shaders,     // Shader program binding
    Uniforms,    // Uniform setting
    Sync,        // Memory barriers, synchronization
    Errors,      // Errors and warnings
    Timing,      // Performance timing
    Validation,  // Validation checks
    State,       // State transitions
}
```

### Log Levels

```csharp
public enum LogLevel
{
    Error,   // Critical errors only
    Warning, // Warnings and errors
    Info,    // Informational messages
    Debug,   // Detailed debug information
    Trace    // Extremely verbose trace logging
}
```

### Features

| Feature | Description |
|---------|-------------|
| **Category filtering** | Enable/disable specific categories via `EnabledCategories` flags |
| **Log levels** | Control verbosity via `CurrentLogLevel` |
| **Frame context** | Automatic frame numbers in log output |
| **Timestamps** | Millisecond timing within frame |
| **Thread IDs** | Optional thread identification |
| **Performance timing** | `BeginTiming()` disposable scope for timing sections |
| **Buffer dumps** | `DumpIndirectDrawBuffer()`, `DumpCulledCommandBuffer()` |
| **Validation logging** | `LogBufferValidation()`, `LogIndirectBufferValidation()` |
| **Statistics tracking** | Dispatch counts, draw calls, message counts by category |

### Usage Examples

```csharp
// Basic logging
GpuDispatchLogger.Info(LogCategory.Draw, "Starting render pass {0}", passIndex);

// Timing a section
using (GpuDispatchLogger.BeginTiming("DispatchCulling"))
{
    // ... culling code ...
}

// Category-specific helpers
GpuDispatchLogger.LogDispatchStart("RenderIndirect", drawCount, maxCommands);
GpuDispatchLogger.LogBufferBind("IndirectDrawBuffer", "DrawIndirect");
GpuDispatchLogger.LogMultiDrawIndirect(useCount: true, maxCommands, stride);
GpuDispatchLogger.LogDispatchEnd("RenderIndirect", success: true);

// Validation
GpuDispatchLogger.LogIndirectBufferValidation(buffer, expectedCommands, stride);
```

### Configuration

```csharp
// Enable all categories at Debug level
GpuDispatchLogger.EnabledCategories = LogCategory.All;
GpuDispatchLogger.CurrentLogLevel = LogLevel.Debug;

// Or selective categories
GpuDispatchLogger.EnabledCategories = LogCategory.Draw | LogCategory.Buffers | LogCategory.Errors;

// Include additional context
GpuDispatchLogger.IncludeTimestamps = true;
GpuDispatchLogger.IncludeFrameNumbers = true;
GpuDispatchLogger.IncludeThreadId = true;

// Control buffer dump size
GpuDispatchLogger.MaxBufferDumpSize = 16;
```

### Integration

The logging system integrates with:
- `HybridRenderingManager.cs` – All dispatch operations use structured logging
- `GPURenderPassCollection.*.cs` – The existing `Dbg()` method maps to `GpuDispatchLogger`
- Global toggle: `Engine.EffectiveSettings.EnableGpuIndirectDebugLogging`

---

## Debug Settings

Located in `GPURenderPassCollection.IndirectDebugSettings`:

```csharp
ForceCpuIndirectBuild    // Bypass GPU compute, build commands on CPU
DisableCountDrawPath     // Force explicit draw count (no parameter buffer)
DumpIndirectArguments    // Log indirect command contents
ValidateBufferLayouts    // Assert stride/capacity before draw
```

Enable verbose logging via `Engine.EffectiveSettings.EnableGpuIndirectDebugLogging`.
