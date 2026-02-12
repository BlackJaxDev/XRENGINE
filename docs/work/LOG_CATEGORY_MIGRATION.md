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
- `ELogCategory.Vulkan` - Vulkan-specific operations, shaders, pipelines
- `ELogCategory.Networking` - REST APIs, OSC, webhooks, discovery
- `ELogCategory.VR` - VR headset, device models, OpenXR
- `ELogCategory.Scripting` - Assembly loading, hot-reload, CSProj

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
Debug.Vulkan(message, args)                  // Vulkan category
Debug.Networking(message, args)              // Networking category
Debug.VR(message, args)                      // VR category
Debug.Scripting(message, args)               // Scripting category
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
Debug.VulkanWarning(message, args)
Debug.NetworkingWarning(message, args)
Debug.VRWarning(message, args)
Debug.ScriptingWarning(message, args)
Debug.LogWarning(ELogCategory, message, args)  // Explicit category

// Category-specific errors (with stack trace)
Debug.RenderingError(message, args)
Debug.OpenGLError(message, args)
Debug.PhysicsError(message, args)
Debug.AudioError(message, args)
Debug.AnimationError(message, args)
Debug.UIError(message, args)
Debug.VulkanError(message, args)
Debug.NetworkingError(message, args)
Debug.VRError(message, args)
Debug.ScriptingError(message, args)
Debug.LogError(ELogCategory, message, args)    // Explicit category

// Category-specific exceptions
Debug.RenderingException(ex, message)
Debug.OpenGLException(ex, message)
Debug.PhysicsException(ex, message)
Debug.AudioException(ex, message)
Debug.AnimationException(ex, message)
Debug.UIException(ex, message)
Debug.VulkanException(ex, message)
Debug.NetworkingException(ex, message)
Debug.VRException(ex, message)
Debug.ScriptingException(ex, message)
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

### AUDIO Category ✅ COMPLETED

| File | Line | Current Call | Status |
|------|------|--------------|--------|
| `XRENGINE/Scene/Components/Audio/VoiceMcpBridgeComponent.cs` | 467-900 | Various `Debug.Audio(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Audio/TTS/TextToSpeechComponent.cs` | 304-451 | `Debug.Audio(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Audio/OVRLipSyncComponent.cs` | 101-331 | `Debug.Audio/AudioWarning(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Audio/MicrophoneComponent.cs` | 480, 622-630 | `Debug.Audio(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Audio/Converters/MicrophoneComponent.RVCConverter.cs` | 48-409 | `Debug.Audio(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Audio/Converters/MicrophoneComponent.ElevenLabsConverter.cs` | 170-363 | `Debug.Audio(...)` | ✅ Done |

### ANIMATION Category ✅ COMPLETED

| File | Line | Current Call | Status |
|------|------|--------------|--------|
| `XRENGINE/Scene/Components/VR/VRPlayerCharacterComponent.cs` | 691 | `Debug.AnimationWarning(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Movement/HeightScaleBaseComponent.cs` | 74 | `Debug.Animation(...)` | ✅ Done |

### UI Category ✅ COMPLETED

| File | Line | Current Call | Status |
|------|------|--------------|--------|
| `XRENGINE/Scene/Components/UI/UISvgComponent.cs` | 180, 193, 345 | `Debug.UIWarning(...)` | ✅ Done |
| `XRENGINE/Scene/Components/UI/Core/Transforms/UITransform.cs` | 417 | `Debug.UIException(...)` | ✅ Done |
| `XRENGINE/Scene/Components/UI/Core/Transforms/UICanvasTransform.cs` | 150 | `Debug.UIException(...)` | ✅ Done |
| `XRENGINE/Scene/Components/UI/Core/Transforms/UIBoundableTransform.cs` | 636 | `Debug.UIException(...)` | ✅ Done |
| `XRENGINE/Scene/Components/UI/Rive/RiveUIComponent.cs` | 143, 397-538 | `Debug.UIWarning(...)` | ✅ Done |
| `XRENGINE/Scene/Components/UI/Core/UIVideoComponent.cs` | 434-1377 | Various `Debug.UI/UIWarning/UIError(...)` | ✅ Done |

### VR Category ✅ COMPLETED

| File | Line | Current Call | Status |
|------|------|--------------|--------|
| `XRENGINE/Scene/Components/VR/VRHeadsetComponent.cs` | 30 | `Debug.VRWarning(...)` | ✅ Done |
| `XRENGINE/Scene/Components/VR/VRDeviceModelComponent.cs` | 117 | `Debug.VRWarning(...)` | ✅ Done |

### OPENGL Category ✅ COMPLETED

| File | Line(s) | Current Call | Status |
|------|---------|--------------|--------|
| `XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs` | 147-150 | `Debug.OpenGL(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs` | 172 | `Debug.OpenGLWarning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs` | 351 | `Debug.OpenGLWarning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs` | 410 | `Debug.OpenGLWarning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs` | 718 | `Debug.OpenGLWarning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs` | 1565, 1587 | `Debug.OpenGLWarning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture1D.cs` | 142, 168 | `Debug.OpenGLException/Warning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture1DArray.cs` | 142, 186 | `Debug.OpenGLException/Warning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D.cs` | 186, 199, 336 | `Debug.OpenGLException/Warning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2DArray.cs` | 299, 343-417 | Various `Debug.OpenGL*(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture3D.cs` | 98, 111 | `Debug.OpenGLException/Warning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTextureCube.cs` | 196, 209, 270 | `Debug.OpenGLException/Warning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTextureCubeArray.cs` | 146, 194 | `Debug.OpenGLException/Warning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/Types/Render Targets/GLFrameBuffer.cs` | 88-154 | `Debug.OpenGLWarning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/Types/Meshes/GLShader.cs` | 159-323 | Various `Debug.OpenGL*(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/Types/Meshes/GLMaterial.cs` | 107 | `Debug.OpenGL(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.cs` | 358-1558 | Various `Debug.OpenGL*(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Debug.cs` | 62 | `Debug.OpenGL(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Rendering.cs` | 30 | `Debug.OpenGLWarning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/Types/GLObjectBase.cs` | 97, 112, 136 | `Debug.OpenGL/OpenGLWarning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/Types/GLObject.cs` | 64, 71 | `Debug.OpenGLWarning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/Types/GLMeshGenerationQueue.cs` | 95-119 | `Debug.OpenGLException/OpenGL(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/Types/Buffers/GLDataBuffer.cs` | 73-709 | Various `Debug.OpenGL*(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenGL/Types/Buffers/GLUploadQueue.cs` | 100 | `Debug.OpenGLWarning(...)` | ✅ Done |

### VULKAN Category ✅ COMPLETED

| File | Line(s) | Current Call | Status |
|------|---------|--------------|--------|
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkTexture2D.cs` | 452, 553, 668, 733, 822 | `Debug.VulkanWarning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.cs` | 357-393 | `Debug.VulkanWarningEvery(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.cs` | 779, 804, 1724, 1864 | `Debug.VulkanWarning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkShader.cs` | 69 | `Debug.VulkanException(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgram.cs` | 97, 119 | `Debug.VulkanWarning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkObject.cs` | 100 | `Debug.VulkanWarning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Types/VkDataBuffer.cs` | 222, 227 | `Debug.VulkanWarning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/LogicalDevice.cs` | 106, 110, 169, 173 | `Debug.Vulkan/VulkanWarning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs` | 108-114 | `Debug.VulkanEvery(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs` | 487, 520 | `Debug.VulkanWarning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Validation.cs` | 106, 108, 110 | `Debug.VulkanError/VulkanWarning/Vulkan(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/Vulkan/PhysicalDevice.cs` | 74-76, 82 | `Debug.Vulkan/VulkanWarning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.cs` | 617, 749, 1025-1127, 1212-1247 | Various `Debug.Vulkan*(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanShaderTools.cs` | 880, 904 | `Debug.VulkanWarning(...)` | ✅ Done |
| `XRENGINE/Rendering/API/Rendering/OpenXR/OpenXRAPI.Vulkan.cs` | 32, 44, 53 | `Debug.Vulkan(...)` | ✅ Done |

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
| `XRENGINE/Scene/Components/Pawns/*` | Pawn input (General) |
| `XRENGINE/Scene/Components/Debug/*` | Debug visualization (General) |

### NETWORKING Category ✅ COMPLETED

| File | Line | Current Call | Status |
|------|------|--------------|--------|
| `XRENGINE/Scene/Components/Networking/OscReceiverComponent.cs` | 76 | `Debug.NetworkingWarning(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Networking/OscReceiverComponent.cs` | 84 | `Debug.Networking(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Networking/RestApiComponent.cs` | 376 | `Debug.NetworkingWarning(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Networking/RestApiComponent.cs` | 536 | `Debug.NetworkingWarning(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Networking/RestApiComponent.cs` | 596 | `Debug.Log(ELogCategory.Networking, ...)` | ✅ Done |
| `XRENGINE/Scene/Components/Networking/RestApiComponent.cs` | 607 | `Debug.Log(ELogCategory.Networking, ...)` | ✅ Done |
| `XRENGINE/Scene/Components/Networking/NetworkDiscoveryComponent.cs` | 332 | `Debug.NetworkingWarning(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Networking/NetworkDiscoveryComponent.cs` | 349 | `Debug.NetworkingWarning(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Networking/NetworkDiscoveryComponent.cs` | 387 | `Debug.NetworkingWarning(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Networking/NetworkDiscoveryComponent.cs` | 441 | `Debug.Log(ELogCategory.Networking, ...)` | ✅ Done |
| `XRENGINE/Scene/Components/Networking/WebhookListenerComponent.cs` | 286 | `Debug.NetworkingWarning(...)` | ✅ Done |

### SCRIPTING Category ✅ COMPLETED

| File | Line | Current Call | Status |
|------|------|--------------|--------|
| `XRENGINE/Scene/Components/Scripting/GameCSProjLoader.cs` | 60 | `Debug.ScriptingException(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Scripting/GameCSProjLoader.cs` | 69 | `Debug.ScriptingWarning(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Scripting/GameCSProjLoader.cs` | 107 | `Debug.Scripting(...)` | ✅ Done |
| `XRENGINE/Scene/Components/Scripting/GameCSProjLoader.cs` | 111 | `Debug.ScriptingException(...)` | ✅ Done |

---

## Migration Priority

### High Priority (Core Engine Systems)
1. Physics - All PhysX-related logging
2. Rendering - Pipelines, shaders, GPU resources
3. OpenGL - GL-specific calls
4. Vulkan - Vulkan-specific calls

### Medium Priority (Runtime Features)  
4. Audio - TTS, microphone, lip sync
5. Animation - IK, animation state
6. UI - Layout, Rive, video
7. Networking - REST APIs, OSC, webhooks, discovery
8. VR - Headset, device models
9. Scripting - Assembly loading, hot-reload
10. Vulkan - Validation, shaders, pipelines, swapchain

### Low Priority (Keep as General)
7. Editor code - Intentionally General

---

## Implementation Tasks

- [x] Add `Debug.Animation()` helper method
- [x] Add `Debug.UI()` helper method
- [x] Add `Debug.Networking()` helper method
- [x] Add `Debug.VR()` helper method
- [x] Add `Debug.Scripting()` helper method
- [x] Add `Debug.Vulkan()` helper method
- [x] Add category-specific warning methods:
  - [x] `Debug.RenderingWarning()`
  - [x] `Debug.OpenGLWarning()`
  - [x] `Debug.PhysicsWarning()`
  - [x] `Debug.AudioWarning()`
  - [x] `Debug.AnimationWarning()`
  - [x] `Debug.UIWarning()`
  - [x] `Debug.NetworkingWarning()`
  - [x] `Debug.VRWarning()`
  - [x] `Debug.ScriptingWarning()`
  - [x] `Debug.VulkanWarning()`
- [x] Add category-specific exception methods:
  - [x] `Debug.RenderingException()`
  - [x] `Debug.OpenGLException()`
  - [x] `Debug.PhysicsException()`
  - [x] `Debug.AudioException()`
  - [x] `Debug.AnimationException()`
  - [x] `Debug.UIException()`
  - [x] `Debug.NetworkingException()`
  - [x] `Debug.VRException()`
  - [x] `Debug.ScriptingException()`
  - [x] `Debug.VulkanException()`
- [x] Add category-specific error methods:
  - [x] `Debug.RenderingError()`
  - [x] `Debug.OpenGLError()`
  - [x] `Debug.PhysicsError()`
  - [x] `Debug.AudioError()`
  - [x] `Debug.AnimationError()`
  - [x] `Debug.UIError()`
  - [x] `Debug.NetworkingError()`
  - [x] `Debug.VRError()`
  - [x] `Debug.ScriptingError()`
  - [x] `Debug.VulkanError()`
- [x] Update legacy methods to add `[WARN]`, `[ERROR]`, `[EXCEPTION]` prefixes
- [x] Update call sites (see tables above)
- [x] Add new categories:
  - [x] `ELogCategory.Networking`
  - [x] `ELogCategory.VR`
  - [x] `ELogCategory.Scripting`
  - [x] `ELogCategory.Vulkan`
