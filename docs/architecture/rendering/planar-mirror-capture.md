# Planar mirror capture

`MirrorCaptureComponent` renders a planar reflection into a private offscreen
capture and displays that result on the mirror surface. It is an in-world
render-to-texture feature; it does not request main-view post-processing work.

## Authoring

The component creates a local `+Z` quad (`VertexQuad.PosZ`). Place and orient
the owning node so its positive local Z faces the reflecting side. Node scale
sets the display plane size; a scale of `4, 4, 4` creates a four-unit quad in
the plane's local axes.

`UseAdvancedCapturePipeline` selects the capture and display path:

- `false` is the default and uses the traditional capture/display path.
- `true` uses the Advanced offscreen capture family and native projective
  mirror material.

`TextureWidthOverride` and `TextureHeightOverride` default to 512. A missing
override also resolves to 512. The effective size is clamped to at least one
pixel in each dimension. Changing the Advanced selection retires the existing
private views before rebuilding them; a resolution change withdraws the
published output and refreshes the private slots at the new size.

## Source cameras and reflected view

A display collection requests a bank for its source `XRCamera`. The component
admits at most three source-camera banks, with two immutable capture slots per
bank. Repeated collection of the same camera reuses its bank. Native
single-pass stereo collects the display once but also requests the distinct
right-eye camera from the scoped stereo collection state. When all three banks
are occupied, later cameras are not admitted and the component emits one
capacity warning.

Each slot owns a private capture camera. Immediately before authoring, it:

1. copies the source camera's lens parameters and depth mode;
2. reflects the source render transform about the mirror plane;
3. applies an oblique clipping plane using that reflected transform and the
   mirror plane; and
4. seals the unjittered reflected view-projection matrix and framebuffer-Y
   convention with the output.

The capture viewport keeps the copied source lens read-only: its resolution
controls sampling density and does not update the source camera aspect ratio.
The capture has its own offscreen pipeline, collection, swap, and completion
path. It does not cause a main-view post-processing request.

## Publication and lifetime

A slot becomes displayable only after its capture writer completes and the
output has a nonzero canonical generation, a valid mutable publication
lifetime, and a finite reflected matrix. Until then the mirror has no newly
published generation; this is the cold generation gate.

Native early and late visibility also require a valid published entry for the
exact source-camera identity before writing the mirror's visibility or depth.
This keeps cold, withdrawn, or unmatched-eye surfaces absent even though their
material remains in the resident scene.

Legacy display collection retains the exact published generation before adding
the display command. GPU display work takes a separate retained reader and
settles it through a completion fence. Advanced publication retains the exact
resource generation in its immutable scene snapshot. A slot is not republished,
resized, retired, or reused while its collected, package, or GPU readers still
own that generation.

When a reader or writer is pending, capture refresh is deferred rather than
overwriting the output. A missing or failed trustworthy GPU completion
quarantines the affected capture resources and prevents unsafe refresh. This
can leave an older published reflection in place until the ownership condition
settles or the component is retired.

Deactivation and destruction withdraw published views, stop capture scheduling,
and retire each private slot only after its readers and capture resources have
drained. Reactivation waits for partial retirement to finish, then marks the
configuration dirty and rebuilds fresh private views. The component's native
display is enabled only when the Advanced mode has at least one published bank.

Unknown GPU completion deliberately retains its resources. A quarantined bank
cannot be recycled or drained by ordinary retirement; recovery of that failure
condition requires shutting down its renderer/session. Diagnostics expose the
quarantine and pending retirement.

## Mirror recursion exclusion

Mirror capture collection uses `collectMirrors: false`. The canonical view
records produced for that collection carry `ExcludeProjectiveMirrors`, and the
Advanced early and late visibility passes reject the projective mirror material
for those views. The resident scene remains unchanged, so normal main views
continue to render the mirror.

## Diagnostics

`GetMirrorCaptureDiagnostics()` is a cold-path ownership snapshot. It reports
the completed capture count, bank capacity, slots per bank, retirement state,
last capture failure, first-bank output texture and dimensions, and per-bank
source-camera identity, published texture, and slot diagnostics. It does not
poll GPU fences and is not an atomic frame snapshot.

`EnvironmentTexture` and `Viewport` are diagnostic accessors for the first
bank. They may be null before a bank is created or before a capture publishes
an output. The renderer's advanced profile diagnostics can target a mirror
capture owner after its offscreen viewport exists.

## Current acceptance boundaries

Bounded native Vulkan, native OpenGL and traditional OpenGL fixtures have live
reflection, clipping, resize and retirement evidence. Hardware stereo and the
complete reader-pressure/fault matrix remain open. Native OpenGL reconstructs
world positions using the frozen view's depth-range flag, including zero-to-one
clip control and reversed depth.

Vulkan shares immutable scene payloads across mirror/main views of the exact
same publication within a frame-slot generation. Views, frame metadata, global
descriptors and completion ownership remain separate. Large imported-scene
fixtures with two mirrors have continued through publication changes, resizing
and complete retirement. Distinct publications retain independent payloads;
occupied-slot growth waits for a completed empty boundary and respects the
existing memory ceilings. The broader mirror pressure/fault profile remains
tracked separately from [ARP-I95](../../work/todo/rendering/vulkan-xr-and-advanced-rendering-todo.md#arp-i95).
