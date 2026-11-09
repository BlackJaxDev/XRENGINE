# Model Inspector

Select a node with a `ModelComponent` to inspect its meshes and blendshapes.

## Bounds

Enable **Render Bounds** to show the model's mesh bounds. The submesh panel also
has a separate bounds switch. The Advanced pipeline draws these debug shapes
over the scene. Capture policies can disable debug overlays.

For a skinned mesh, **Calculate Skinned Bounds In Compute Shader** selects the
live GPU bounds source. When that setting is off, the preview uses the normal
CPU culling bounds. The switch does not change the avatar's mesh submission path.

## Blendshape Filter

**Hide Blendshapes Without Effect** starts enabled. It hides controls whose mesh
data has no position, normal, or tangent delta. A zero current weight does not
make a blendshape ineffective.

The **Blendshapes (All LODs)** list keeps a name if any mesh or LOD uses it.
Each submesh list checks only that mesh. Disable the filter to show every
blendshape. The filter changes the list only. It does not remove blendshapes or
change their weights. If the source data cannot be checked, the controls remain
visible.
