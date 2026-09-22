# Advanced Editor Picking Y-Flip Investigation

## Problem

With the editor camera pawn and the Advanced render pipeline, hover highlighting
and click selection sample the scene at the vertically mirrored mouse position.
The Default render pipeline was not known to be affected.

## Findings

- Editor cursor input is converted from the window's top-left origin into the
  engine's bottom-left normalized viewport convention before picking.
- Default-pipeline selection consumes that normalized coordinate as a camera ray,
  so it does not cross a framebuffer texture-coordinate boundary.
- Advanced-pipeline selection converts the normalized coordinate to an integer
  visibility-attachment texel and previously passed Y to the renderer unchanged.
- Vulkan's framebuffer texture row zero is the top row under the default Y-up
  clip-space policy. The existing depth-readback path already accounts for this
  through `RenderClipSpacePolicy.FramebufferTextureYDirection`.

## Solution

Convert the logical bottom-left viewport Y to the physical framebuffer texture
row in `XRViewport.TryPickAdvancedAsync`, using the active renderer backend and
the shared framebuffer-orientation policy. This keeps Default ray picking and
camera coordinate conventions unchanged while also supporting non-default
clip-space policies.

## Validation

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`
  succeeds with no warnings or errors.
- An isolated Vulkan editor session (`picking-y-0921`) built the full editor with
  no warnings or errors and ran the Advanced pipeline.
- After focusing the camera on `sponza_379`, the captured frame visibly placed the
  object across the lower center of the viewport. Advanced picking at normalized
  `(0.50, 0.15)` and `(0.50, 0.50)` resolved `sponza_379`; the vertically mirrored
  sky coordinate `(0.50, 0.85)` correctly returned no hit.
- The camera was moved between captures and the result changed with the view,
  ruling out a stale visibility attachment.
- The isolated session emitted no per-session log files and stopped cleanly.

## User Confirmation

The user exercised mouse hover highlighting and click selection in the editor and
confirmed that the fix works.

## Follow-up: Hover Input Ownership

After the coordinate fix, two related hover-lifetime issues remained:

- A GPU/async pick completed after the cursor left the viewport and restored the
  last yellow hover outline.
- The ImGui Scene window allowed world hover input across the whole window,
  including editor chrome and UI drawn over the scene image.

The editor now publishes whether the current ImGui frame gives pointer ownership
to the scene. Scene-panel mode grants ownership only while the actual viewport
image item is unobstructed and hovered; full-viewport mode additionally requires
the pointer to be over the main platform window and not captured by ImGui.

The camera pawn tracks a revision for cursor position and scene-input ownership.
Async results may update the yellow hover outline only when their revision is
still current. Losing ownership immediately clears hover state, cancels queued
hover work, and requests a redraw, while an already dispatched click selection
may still complete and retain its independent white selection outline. Mouse
movement also invalidates render-on-demand so a quick window exit is presented.

The follow-up built successfully in both the normal editor output and an isolated
Vulkan/Advanced editor session with zero warnings or errors. The isolated editor
reached MCP-ready state and was stopped cleanly. Interactive cursor validation was
ended at the user's request to wrap up; no tests were added under the repository's
feature-regression testing policy.
