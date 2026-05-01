# Octahedral Billboard Capture

The ImGui model editor can bake a model or selected submeshes into a 26-layer octahedral billboard asset. The runtime billboard is a directional blended billboard, not a depth/parallax impostor in v1.

## Editor Workflow

1. Select a scene node with a `ModelComponent`.
2. Open the model inspector and switch the Submeshes panel to `Tools`.
3. Use `Generate Model Billboard Capture` to capture every renderable submesh, or select submeshes and use `Generate Selected Submesh Capture`.
4. Wait for the layer progress bar to complete. Captures are saved under `Assets/Generated/OctahedralBillboards`.
5. Use `Create Billboard Impostor` after the generated asset is saved.

Individual submesh `Tools` panels also expose `Generate Billboard Capture For This Submesh`.

## Capture Contract

Generated captures are stored as `OctahedralBillboardAsset` assets. The asset records:

- color `XRTexture2DArray` views
- capture direction order
- capture resolution and padding
- world and local bounds
- capture center in world and local space
- orthographic extent
- source capture mode and selected submesh indices
- source model path when available
- source material mode summary
- source layer and source shadow flag metadata

The billboard component references this asset and applies its color views, capture center, quad size, and render layer. Billboard shadow casting is disabled by default because v1 does not generate a shadow/depth impostor path.

## Depth And Backend Notes

Depth output is deferred for v1. Captures still render with a depth buffer so overlapping source geometry sorts correctly inside each color view, but no depth texture array is persisted or sampled by the runtime billboard shader.

Captures disable auto exposure and other view-history-dependent color grading state so generated layers are stable. They do not force a shadow-map recapture; shadowed materials use whatever shadow state the active render pipeline already has available at capture time.

OpenGL 4.6 is the primary validation target. Preview thumbnails use texture-array layer views so Vulkan has an explicit path, but Vulkan capture should remain separately validated before it is treated as production-ready.

## Runtime Notes

The billboard shader blends the three nearest capture directions from the 26-layer array. The C# generator and `Common/OctahedralImposter.glsl` must keep the same direction order. `OctahedralMappingTests` covers this contract and compiles the billboard shaders when the unit-test project is buildable.
