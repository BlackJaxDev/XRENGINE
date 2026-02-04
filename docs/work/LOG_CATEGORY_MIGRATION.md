# Log Category Migration Guide

This document tracks all logging call sites that need to be updated with proper log categories.

## Overview

The logging system now supports explicit categories for better organization and filtering:
- `ELogCategory.General` - Default for non-specialized logs
- `ELogCategory.Rendering` - Graphics, rendering pipelines, viewports
- `ELogCategory.OpenGL` - OpenGL-specific operations
- `ELogCategory.Physics` - PhysX, collision, physics simulation
- `ELogCategory.Audio` - Audio sources, microphones, TTS, lip sync
- `ELogCategory.Animation` - IK solvers, animation clips, state machines
- `ELogCategory.UI` - UI components, layouts, Rive, SVG

## Available Logging Methods

### Informational Logging (existing)
```csharp
Debug.Out(message, args)                     // General output (goes to General category)
Debug.Rendering(message, args)               // Rendering category
Debug.OpenGL(message, args)                  // OpenGL category
Debug.Physics(message, args)                 // Physics category
Debug.Audio(message, args)                   // Audio category
Debug.Animation(message, args)               // Animation category
Debug.UI(message, args)                      // UI category
Debug.Log(category, message, args)           // Explicit category
```

### Warning/Error Logging (IMPLEMENTED)
```csharp
// Category-specific warnings (with stack trace)
Debug.RenderingWarning(message, args)
Debug.OpenGLWarning(message, args)
Debug.PhysicsWarning(message, args)
Debug.AudioWarning(message, args)
Debug.AnimationWarning(message, args)
Debug.UIWarning(message, args)
Debug.LogWarning(ELogCategory, message, args)  // Explicit category

// Category-specific errors (with stack trace)
Debug.RenderingError(message, args)
Debug.OpenGLError(message, args)
Debug.PhysicsError(message, args)
Debug.AudioError(message, args)
Debug.AnimationError(message, args)
Debug.UIError(message, args)
Debug.LogError(ELogCategory, message, args)    // Explicit category

// Category-specific exceptions
Debug.RenderingException(ex, message)
Debug.OpenGLException(ex, message)
Debug.PhysicsException(ex, message)
Debug.AudioException(ex, message)
Debug.AnimationException(ex, message)
Debug.UIException(ex, message)
Debug.LogException(ELogCategory, ex, message)  // Explicit category

// Legacy (routes to General category with prefixes)
Debug.LogWarning(message)    // Prefixed with [WARN]
Debug.LogError(message)      // Prefixed with [ERROR]
Debug.LogException(ex, msg)  // Prefixed with [EXCEPTION]
```

---

## Call Sites Requiring Updates

### PHYSICS Category ✅ COMPLETED

| File | Line | Current Call | Status |
|------|------|--------------|--------|
| `XRENGINE/Scene/Physics/Physx/PhysxScene.cs` | 468 | `Debug.Physics(...)` | ✅ Done |
| `XRENGINE/Scene/Physics/Physx/InstancedDebugVisualizer.cs` | 553, 586, 618 | `Debug.PhysicsException(...)` | ✅ Done |
| `XRENGINE/Scene/Physics/Physx/Controller.cs` | 635, 660, 691 | `Debug.PhysicsException(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Physics/PhysicsChainComponent.cs` | 32 | `Debug.PhysicsWarning(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Physics/PhysicsActorComponent.cs` | 166, 254 | `Debug.PhysicsException(...)` | ✅ Done |

### RENDERING Category ✅ COMPLETED

| File | Line | Current Call | Status |
|------|------|--------------|--------|
| `XRENGINE/Engine/Engine.Windows.cs` | 44 | `Debug.RenderingWarning(...)` | ✅ Done |
| `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.SecondaryContext.cs` | 141, 154, 179 | `Debug.RenderingWarning(...)` | ✅ Done |
| `XRENGINE/Scene/Transforms/Transform.cs` | 341 | `Debug.Rendering(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Lights/Types/ShadowRenderPipeline.cs` | 14 | `Debug.Rendering(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Lights/Types/OneViewLightComponent.cs` | 95 | `Debug.Rendering(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Lights/Types/DirectionalLightComponent.cs` | 379-389 | `Debug.Rendering(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Mesh/RenderableMesh.cs` | 596 | `Debug.RenderingException(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Mesh/OctahedralBillboardComponent.cs` | 152, 220, 233 | `Debug.RenderingWarning(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Mesh/GaussianSplatComponent.cs` | 73 | `Debug.RenderingException(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Capture/SceneCaptureComponent.cs` | 337, 342, 352 | `Debug.Rendering/RenderingWarning(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Particles/ParticleEmitterComponent.cs` | 384, 402 | `Debug.RenderingWarning(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Landscape/LandscapeComponent.cs` | 557, 596, 611, 1747 | `Debug.RenderingWarning(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Misc/SkyboxComponent.cs` | 246, 288, 297, 336 | `Debug.Rendering/RenderingWarning(...)` | ✅ Done |

### AUDIO Category

| File | Line | Current Call | Suggested Update |
|------|------|--------------|------------------|
| `XRENGINE/Scene/Components/Audio/VoiceMcpBridgeComponent.cs` | 467-900 | Various `Debug.Out(...)` | `Debug.Audio(...)` |
| `XRENGINE/Scene/Components/Audio/TTS/TextToSpeechComponent.cs` | 304-451 | `Debug.Out(...)` | `Debug.Audio(...)` |
| `XRENGINE/Scene/Components/Audio/OVRLipSyncComponent.cs` | 101-331 | `Debug.Out/LogWarning(...)` | `Debug.Audio/AudioWarning(...)` |
| `XRENGINE/Scene/Components/Audio/MicrophoneComponent.cs` | 480, 622-630 | `Debug.Out(...)` | `Debug.Audio(...)` |
| `XRENGINE/Scene/Components/Audio/Converters/MicrophoneComponent.RVCConverter.cs` | 48-409 | `Debug.Out(...)` | `Debug.Audio(...)` |
| `XRENGINE/Scene/Components/Audio/Converters/MicrophoneComponent.ElevenLabsConverter.cs` | 170-363 | `Debug.Out(...)` | `Debug.Audio(...)` |

### ANIMATION Category

| File | Line | Current Call | Suggested Update |
|------|------|--------------|------------------|
| `XRENGINE/Scene/Components/VR/VRPlayerCharacterComponent.cs` | 691 | `Debug.LogWarning(...)` | `Debug.AnimationWarning(...)` |
| `XRENGINE/Scene/Components/Movement/HeightScaleBaseComponent.cs` | 74 | `Debug.Out(...)` | `Debug.Animation(...)` |

### UI Category

| File | Line | Current Call | Suggested Update |
|------|------|--------------|------------------|
| `XRENGINE/Scene/Components/UI/UISvgComponent.cs` | 180, 193, 345 | `Debug.LogWarning(...)` | `Debug.UIWarning(...)` |
| `XRENGINE/Scene/Components/UI/Core/Transforms/UITransform.cs` | 417 | `Debug.LogException(...)` | `Debug.UIException(...)` |
| `XRENGINE/Scene/Components/UI/Core/Transforms/UICanvasTransform.cs` | 150 | `Debug.LogException(...)` | `Debug.UIException(...)` |
| `XRENGINE/Scene/Components/UI/Core/Transforms/UIBoundableTransform.cs` | 636 | `Debug.LogException(...)` | `Debug.UIException(...)` |
| `XRENGINE/Scene/Components/UI/Rive/RiveUIComponent.cs` | 143, 397-538 | `Debug.LogWarning(...)` | `Debug.UIWarning(...)` |
| `XRENGINE/Scene/Components/UI/Core/UIVideoComponent.cs` | 434-1377 | Various `Debug.Out/LogWarning/LogError(...)` | `Debug.UI/UIWarning/UIError(...)` |

### VR Category (currently mapped to Animation or General)

| File | Line | Current Call | Suggested Update |
|------|------|--------------|------------------|
| `XRENGINE/Scene/Components/VR/VRHeadsetComponent.cs` | 30 | `Debug.LogWarning(...)` | Keep as `LogWarning` or new VR category |
| `XRENGINE/Scene/Components/VR/VRDeviceModelComponent.cs` | 117 | `Debug.Out(...)` | Keep as `Out` or new VR category |

### GENERAL Category (Editor/Tool code - keep as-is)

The following are editor/tool utilities and should remain in General category:

| File | Reason |
|------|--------|
| `XREngine.Editor/Selection.cs` | Editor selection state |
| `XREngine.Editor/Undo.cs` | Editor undo/redo |
| `XREngine.Editor/Program.cs` | Editor startup |
| `XREngine.Editor/ProjectBuilder.cs` | Build system |
| `XREngine.Editor/IMGUI/*` | Editor UI panels |
| `XREngine.Editor/UI/*` | Editor tooling |
| `XREngine.Editor/Unit Tests/*` | Test harness |
| `XREngine.Editor/Mcp/*` | MCP server |
| `XREngine.Editor/ComponentEditors/*` | Inspector editors |
| `XRENGINE/Scene/Prefabs/*` | Prefab system (General) |
| `XRENGINE/Scene/SceneNode.Transform.cs` | Scene graph (General) |
| `XRENGINE/Scene/Components/Scripting/*` | Scripting/assembly loading (General) |
| `XRENGINE/Scene/Components/Networking/*` | Networking (General) |
| `XRENGINE/Scene/Components/Pawns/*` | Pawn input (General) |
| `XRENGINE/Scene/Components/Debug/*` | Debug visualization (General) |

---

## Migration Priority

### High Priority (Core Engine Systems)
1. Physics - All PhysX-related logging
2. Rendering - Pipelines, shaders, GPU resources
3. OpenGL - GL-specific calls

### Medium Priority (Runtime Features)  
4. Audio - TTS, microphone, lip sync
5. Animation - IK, animation state
6. UI - Layout, Rive, video

### Low Priority (Keep as General)
7. Editor code - Intentionally General
8. Networking - Could be new category later
9. Prefabs/Scenes - General scene management

---

## Implementation Tasks

- [x] Add `Debug.Animation()` helper method
- [x] Add `Debug.UI()` helper method
- [x] Add category-specific warning methods:
  - [x] `Debug.RenderingWarning()`
  - [x] `Debug.OpenGLWarning()`
  - [x] `Debug.PhysicsWarning()`
  - [x] `Debug.AudioWarning()`
  - [x] `Debug.AnimationWarning()`
  - [x] `Debug.UIWarning()`
- [x] Add category-specific exception methods:
  - [x] `Debug.RenderingException()`
  - [x] `Debug.OpenGLException()`
  - [x] `Debug.PhysicsException()`
  - [x] `Debug.AudioException()`
  - [x] `Debug.AnimationException()`
  - [x] `Debug.UIException()`
- [x] Add category-specific error methods:
  - [x] `Debug.RenderingError()`
  - [x] `Debug.OpenGLError()`
  - [x] `Debug.PhysicsError()`
  - [x] `Debug.AudioError()`
  - [x] `Debug.AnimationError()`
  - [x] `Debug.UIError()`
- [x] Update legacy methods to add `[WARN]`, `[ERROR]`, `[EXCEPTION]` prefixes
- [ ] Update call sites (see tables above)
- [ ] Consider adding new categories:
  - [ ] `ELogCategory.Networking`
  - [ ] `ELogCategory.VR`
  - [ ] `ELogCategory.Scripting`
