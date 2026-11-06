# Surface parallax occlusion mapping

Imported materials whose surface-detail texture is a height map (for example an
OBJ `bump` map such as the Sponza brick walls) render with silhouette parallax
occlusion mapping (POM). Lighting normals still come from the height map through
the Sobel height-to-normal path (`NormalMapMode = 1`).

## Import selection

`ModelAssetImporter` resolves the surface-detail texture. A normal map wins over
a height map. When the result is a height map:

- **Default deferred:** the shader is the existing `TexturedNormal*Deferred.fs`
  family with two defines: `XRENGINE_HEIGHTMAP_MODE` (height-to-normal) and
  `XRENGINE_HEIGHTMAP_POM` (parallax). The material gets the parameters
  `ParallaxScale`, `ParallaxMinSteps`, `ParallaxMaxSteps`, `ParallaxRefineSteps`
  and `ParallaxSilhouette`. Its render options require the `Camera` and
  `ClipSpacePolicy` engine uniforms.
- **Forward+ Uber:** the same file binds to `_ParallaxMap`, the `parallax`
  feature is enabled and `_ParallaxMode` selects occlusion (`1`) or silhouette
  occlusion (`2`). `_BumpMap` keeps the height-to-normal lighting detail.

Forward textured (non-Uber) imports keep height-to-normal only.

### Silhouette clip and the UV domain

The silhouette clip discards fragments whose parallax ray leaves the UV square
`[0, 1]`. This approximates the mesh edge only when the mesh UVs cover that
square. Tiled layouts, such as walls with UVs far outside `[0, 1]`, would lose
most of the surface. After the importer creates a material, it scans UV0 of every
scene mesh that uses the material:

- All UV0 inside `[0, 1]` (tolerance `1e-3`): `ParallaxSilhouette = 1`, Uber
  `_ParallaxMode = 2`.
- Otherwise, or no UV0: `ParallaxSilhouette = 0`, Uber `_ParallaxMode = 1`.
  Parallax and the depth offset stay active.

A material from the import material remap keeps its own settings.

## Default shader contract

`Snippets/SurfaceDetailParallax.glsl` owns the Default implementation. Without
`XRENGINE_HEIGHTMAP_POM`, its functions are pass-throughs and the shader keeps
early depth testing.

- `XRENGINE_BeginSurfaceParallax(uv, positionWS, normalWS)` runs first. It
  computes all derivatives, marches the height field and returns the parallax UV.
- `XRENGINE_SampleSurface(texture, uv)` samples with the gradients of the
  unmodified UV0, so mip selection does not jump at height discontinuities.
- `XRENGINE_EndSurfaceParallax()` runs last. It writes `gl_FragDepth` and
  performs the silhouette discard after all derivative work.

Height convention: red channel, `1` is the surface top and `0` is the deepest
point. `ParallaxScale` is the full depth in UV units.

The ray uses a derivative tangent frame (`dP/du`, `dP/dv` from screen
derivatives of world position and UV0), not the vertex tangents. That frame
keeps the offset direction and scale correct for mirrored UVs, non-uniform
scale and tiling. The world depth at full scale is `ParallaxScale` times the
isotropic UV-to-world scale `sqrt(|dP/du x dP/dv|)`.

### Depth offset

The fragment moves along the view ray to the height-field intersection. The
snippet projects the original and the displaced world positions with the eye's
view-projection matrix and adds the window-depth difference to
`gl_FragCoord.z`. `ClipDepthRange` selects the `[0, 1]` or `[-1, 1]` mapping.
The depth offset disables early depth testing for these materials only.

### Stereo

The camera is selected by `FragViewIndex` (location 22, written by the generated
vertex shaders): index `1` reads the `RightEye*` matrices, otherwise the
`LeftEye*` matrices. `XRCamera.SetUniforms` writes the per-eye inverse-view and
view-projection names in mono passes too, which matches the Vulkan backend. The
mono `CameraPosition` uniform is not used: single-pass stereo binds both eyes to
one program, so it holds only the last eye.

## Uber

`Uber/parallax.glsl` reads the height with `sampleHeightMap` and converts it to
depth with `sampleParallaxDepth` (`1 - height`) for the steep, occlusion,
silhouette and relief modes. Simple offset mode uses the height directly.

When forward lighting is active, the Uber view direction comes from
`XRENGINE_GetForwardResolvedCameraPosition()`, which follows the fragment's view
index. Uber parallax does not write a depth offset.

## Limitations

- Shadow casters use the shared depth shaders, so shadows show the flat surface.
- The Advanced visibility pipeline does not run parallax yet.
