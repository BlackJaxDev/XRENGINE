# Vulkan Wrapper Parity TODOs

Last Updated: 2026-06-05
Status: Active audit-to-implementation tracker.

## Goal

Bring the Vulkan render-object wrappers to behavior parity with the OpenGL
wrappers for the engine-facing `XR*` types. Parity means engine code can request
the same generic object behavior and get equivalent correctness, invalidation,
diagnostics, resource lifetime, shader/material binding, and validation behavior
from either backend.

This is not a request to make Vulkan mimic OpenGL implementation details where
Vulkan has a better native model. The contract is engine-visible parity.

## Scope

| Type | Vulkan wrapper | OpenGL wrapper | TODO |
|---|---|---|---|
| `XRMeshRenderer.BaseVersion` | `VkMeshRenderer` | `GLMeshRenderer` | [xrmeshrenderer-vulkan-parity-todo.md](xrmeshrenderer-vulkan-parity-todo.md) |
| `XRMesh` | Owned through renderer/data-buffer wrappers | Owned through renderer/data-buffer wrappers | [xrmesh-vulkan-parity-todo.md](xrmesh-vulkan-parity-todo.md) |
| `XRMaterial` | `VkMaterial` | `GLMaterial` | [xrmaterial-vulkan-parity-todo.md](xrmaterial-vulkan-parity-todo.md) |
| `XRShader` | `VkShader` | `GLShader` | [xrshader-vulkan-parity-todo.md](xrshader-vulkan-parity-todo.md) |
| `XRTexture` and concrete texture types | `VkImageBackedTexture`, `VkTexture*` | `GLTexture`, `GLTexture*` | [xrtexture-vulkan-parity-todo.md](xrtexture-vulkan-parity-todo.md) |
| `XRDataBuffer` | `VkDataBuffer` | `GLDataBuffer` | [xrdatabuffer-vulkan-parity-todo.md](xrdatabuffer-vulkan-parity-todo.md) |

## Shared Rules For Implementation

- [ ] Treat `IsGenerated` consistently across backends. For OpenGL this means
      the API object has been created and has a non-zero object ID; it does not
      prove buffer contents, texture pixels, descriptors, programs, or draw
      readiness are valid. Vulkan wrappers should use the same distinction:
      generated means the relevant Vulkan handle or wrapper cache ID exists,
      while upload/readiness/descriptor validity must be tracked separately.
- [ ] Keep backend differences explicit in code comments when Vulkan chooses a
      native equivalent instead of an OpenGL-shaped implementation.
- [ ] Do not hide missing GPU/accelerated paths behind silent CPU fallbacks.
      Emit diagnostics or an intentional fallback signal.
- [ ] Add source-verifiable tests for wrapper contracts that do not require a
      live GPU.
- [ ] Add hardware validation steps for behavior that requires Vulkan command
      execution, validation layers, GPU capture, or visual comparison.
- [ ] Keep hot-path allocations out of per-frame draw, descriptor, buffer, and
      upload paths.
- [ ] Update this folder as parity gaps close; do not leave stale TODOs behind.

## Baseline Source Map

- OpenGL wrapper registration:
  `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs`
- Vulkan wrapper registration:
  `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Drawing.Core.cs`
- Vulkan renderer overview:
  `docs/architecture/rendering/vulkan-renderer.md`
- Vulkan manual validation guide:
  `docs/work/todo/vulkan.md`

## Validation Ladder

1. Source verification:
   - wrapper registration maps every generic type to the expected API wrapper;
   - event subscription/unsubscription symmetry is tested by source or unit
     checks;
   - shader/material/texture descriptor resolution has deterministic unit
     coverage where possible.
2. Narrow build:
   - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
3. Targeted tests:
   - Vulkan P0/P1 validation tests where relevant;
   - rendering wrapper source-contract tests added by each parity item.
4. Hardware validation:
   - run the default editor world with OpenGL and Vulkan using the same scene,
     camera, and resolution;
   - run Unit Testing World in Vulkan;
   - enable Vulkan validation layers and capture descriptor/buffer/image
     warnings;
   - compare visible output for opaque, forward, shadow, UI, FBO/post, and
     debug draw paths.
