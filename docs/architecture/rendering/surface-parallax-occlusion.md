# Surface parallax occlusion mapping

Imported materials whose surface-detail texture is a height map (for example the
OBJ `bump` maps on the Sponza brick walls) render with parallax occlusion mapping
(POM) and a depth offset. Lighting normals still come from the height map through
the Sobel height-to-normal path (`NormalMapMode = 1`).

## Import selection

`ModelAssetImporter` resolves the surface-detail texture. A normal slot wins over
a height (bump) slot. The import option `ModelImportOptions.SurfaceDetailMapMode`
selects how the texture is used:

| Mode | Use |
| --- | --- |
| `Auto` (default) | Classify the texture content (see below). |
| `Height` | POM plus height-derived lighting normals. |
| `Bump` | Height-derived lighting normals only. |
| `Normal` | Tangent-space RGB normal map. |
| `Albedo` | Color texture in a detail slot: no surface detail; it becomes the base color when the material has none. |

The source slot does not settle this. OBJ `map_bump` files can be height fields
(Sponza bricks: white bricks, dark mortar) or embossed bump maps (Sponza lion,
columns and vases: a flat gray base with light and dark ridges that encode slope).
POM on an embossed map turns every ridge into a false step.

### Auto classification

`SurfaceDetailMapClassifier` decodes the file once (cached by path) and samples
at most 128 x 128 pixels:

1. **Normal:** mean blue at least `0.6`, mean red and green within `0.2` of
   `0.5`, and mean chroma at least `0.08`.
   Otherwise **Albedo** when mean chroma is at least `0.08` (a colored map).
2. Otherwise grayscale. **Height** when the median luminance is at least `0.68`
   (a height field whose surface top dominates, with recessed detail such as
   mortar grooves), or when the file name contains `height`, `disp` or
   `parallax`.
3. Otherwise **Bump** (bump and detail maps are authored around a mid-gray
   base).

On Sponza the brick height field has a median of `0.71`, and the lion, column,
vase and plant bump maps have `0.47` to `0.65`. POM on those bump maps is
wrong: the lion texture spans the whole sculpt, so its parallax depth reaches
several centimeters and pushes the relief behind the wall. The margin is small;
use the explicit `Height` or `Bump` mode when Auto picks wrong.

An unreadable file follows its slot: a normal slot is `Normal`, a bump or height
slot is `Bump`. The mode is part of the model-cache import settings.

### Pipelines

- **Height, Default deferred:** the shader is the existing
  `TexturedNormal*Deferred.fs` family with two defines: `XRENGINE_HEIGHTMAP_MODE`
  (height-to-normal) and `XRENGINE_HEIGHTMAP_POM` (parallax). The material gets
  the parameters `ParallaxScale`, `ParallaxMinSteps`, `ParallaxMaxSteps`,
  `ParallaxRefineSteps` and `ParallaxSilhouette`. Its render options require the
  `Camera` and `ClipSpacePolicy` engine uniforms.
- **Height, Forward+ Uber:** the same file binds to `_ParallaxMap`, the
  `parallax` feature is enabled and `_ParallaxMode` is occlusion (`1`).
  `_BumpMap` keeps the height-to-normal lighting detail.
- **Bump:** height-to-normal only in every pipeline.
- Forward textured (non-Uber) imports never use POM.

### Silhouette clip

`ParallaxSilhouette = 1` (Uber `_ParallaxMode = 2`) discards fragments whose
parallax ray leaves the UV square `[0, 1]`. That matches the mesh edge only for a
single quad mapped to the whole texture. On tiled surfaces it removes most of the
surface. The importer never enables it; set it on a material that fits that
layout.

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

When forward lighting is active, the parallax view direction comes from
`XRENGINE_GetForwardResolvedCameraPosition()`, which follows the fragment's view
index. The shared `mesh.viewDir` keeps `CameraPosition`. Uber parallax does not write a depth offset.

## Limitations

- Shadow casters use the shared depth shaders, so shadows show the flat surface.
- The Advanced visibility pipeline does not run parallax yet.
