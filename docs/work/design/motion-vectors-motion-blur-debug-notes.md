# Motion Vectors And Motion Blur Debug Notes

## Current State

- Motion blur consumption was partially corrected by converting the velocity texture from NDC delta space into UV or pixel space in the blur shader.
- The default render pipeline now exposes temporal TAA and TSR resolve tuning in the per-camera post-processing UI under a dedicated `Temporal AA` section.
- The render-pipeline ImGui preview path had real OpenGL state leakage bugs:
  - preview sampling and swizzle state was being written onto live textures;
  - preview texture views could be created before the viewed GL texture existed, producing black previews;
  - preview logic could become an accidental lazy initialization path for render resources.
- Even after the most recent preview isolation fixes, framebuffer and texture previews are still not trustworthy at runtime:
  - most FBO and texture previews are still rendering fully black;
  - depth-view style previews can saturate to solid white rather than showing useful depth structure.
- The motion-vector pass was updated to force a generated engine vertex stage so custom material vertex shaders do not suppress required varyings like `FragPosLocal`.
- The motion-vector pass no longer rasterizes with unjittered projection against the main jittered scene depth buffer.

## Confirmed Findings

### Velocity Contract

- `MotionVectors.fs` writes velocity as current minus previous NDC position.
- `TemporalAccumulation.fs` already consumes this as NDC-to-UV by multiplying by `0.5`.
- The velocity pass intentionally uses unjittered current and previous view-projection matrices.
- Because of that, temporal reprojection must also add the previous-minus-current camera jitter delta in UV space when looking up history.
- If the jitter delta is omitted, static geometry can visibly wobble frame-to-frame even when motion vectors themselves are correct.
- `MotionBlur.fs` previously consumed it as if it were already UV-space, which was incorrect.

### Preview UI Side Effects

- Opening the preview UI could change runtime output because the preview path was not isolated from live GL textures.
- That explains why motion blur or other post effects could appear to change behavior simply by selecting a preview target.
- This should be treated as a pipeline bug, not as valid behavior.
- The preview path is still not a reliable validator for render correctness because current previews can remain black even when the underlying resources may exist.
- Depth-oriented previews saturating white suggest the preview path still lacks correct remapping or texture-view interpretation for at least some formats.

### Fullscreen Resolve Attachment Rules

- Fullscreen resolve passes that sample from textures at mixed resolutions must not auto-derive FBO render targets from every framebuffer-capable material texture.
- TSR is a concrete case: its internal-resolution source textures and full-resolution history texture must coexist in one material while the FBO writes only to the explicit full-resolution output target.
- If the quad FBO derives attachments from the material anyway, it can resize or validate unrelated input textures against the wrong size and cause stalls, warnings, or out-of-memory behavior.

### MSAA Sampler Binding Rules

- The deferred MSAA shaders expect sampler names like `AlbedoOpacity`, `Normal`, `RMSE`, `DepthView`, `NormalMS`, and `DepthMS`, even though the pipeline resources themselves are named `Msaa*`.
- When those samplers are not rebound explicitly, the OpenGL layer falls back to dummy textures, which can turn the editor output black while still logging only sampler binding warnings.

### Non-TAA Requirement

- Motion vectors should exist whenever motion blur needs them, even if TAA or TSR is disabled.
- The temporal begin and commit commands are already scheduled unconditionally in the default pipeline.
- `VPRC_TemporalAccumulationPass` also flags history as captured even when temporal accumulation is skipped.
- Therefore, the remaining motion-vector failure is not explained by TAA or TSR being off.

## Remaining Suspicions

### 1. The Velocity Pass Still Does Not Cover Every Mesh Path

Even after forcing a generated vertex stage, some renderable paths may still bypass the standard CPU override-material path used by `VPRC_RenderMotionVectorsPass`.

Possible examples:

- mesh or draw-command classes that do not route through the standard `RenderCommandMesh3D -> XRMeshRenderer -> GLMeshRenderer` path;
- special render passes that populate the main scene but are omitted from the velocity-pass render-pass list;
- GPU-indirect-only or hybrid-rendered content that is visible in the main scene but absent from the CPU motion-vector pass.

Potential fixes:

- add diagnostics for command counts per pass during the velocity pass versus the main scene passes;
- compare visible draw counts between the main scene pass and the velocity pass for the same frame;
- expand the velocity pass coverage or add a dedicated GPU-compatible velocity path if CPU override rendering still misses content.

### 2. Depth-Test Mismatch May Still Exist For Specific Paths

The obvious jitter mismatch was removed, but some content may still be depth-tested against a buffer produced under slightly different vertex logic or viewport state than the velocity pass.

Potential fixes:

- temporarily disable depth testing in the velocity pass as a diagnostic to determine whether the buffer starts showing expected motion;
- if that proves the issue, make the pass use a more tightly matched raster state or write velocity during the main geometry pass instead of replaying geometry later.

### 3. Previous Transform Tracking May Be Correct For Objects But Insufficient For Some Camera Cases

`RenderCommandMesh3D` currently tracks previous model matrices per command and appears structurally sound, but there are still edge cases worth checking:

- commands recreated every frame could lose stable previous transform history;
- non-model-space or special billboard paths may intentionally collapse to static behavior;
- some objects may receive current and previous transforms that are numerically identical because of update order.

Potential fixes:

- log previous and current model matrices for a small sample of moving objects during the velocity pass;
- verify that commands are not being recreated in a way that destroys previous-frame continuity;
- add targeted exemptions or alternate previous-transform sources for known special cases.

### 4. Camera Motion May Still Be Lost In Practice Even Though Temporal History Exists On Paper

The pipeline schedules temporal history independently of TAA or TSR, but it is still possible that actual previous view-projection values are not valid at the moment the velocity pass consumes them.

Potential fixes:

- log current and previous unjittered view-projection matrices during the velocity pass when the camera is moving;
- verify that previous view-projection changes frame-to-frame even when anti-aliasing mode is off;
- if needed, move or duplicate temporal history commit timing so the velocity pass always sees the intended previous camera state.

### 5. Velocity Buffer Content May Be Correctly Written But Poorly Visualized

Velocity stored in `RG16f` can easily appear black if preview tonemapping, range mapping, or signed-channel display is not suitable.

Potential fixes:

- add a dedicated signed velocity preview mode that remaps `[-max, +max]` into visible grayscale or false color;
- add a debug stat that samples a handful of velocity texels and reports min, max, and average magnitude after the velocity pass;
- add a fullscreen debug material that visualizes vector direction and magnitude directly in the scene.

### 6. Motion Blur Activation May Still Depend On Lazy Resource Materialization Elsewhere

The preview path clearly used to force GL object creation for textures. If motion blur only appeared after opening the preview UI, some render resource or post-process material may still be relying on incidental `GetOrCreateAPIRenderObject(...)` calls instead of deterministic setup.

Potential fixes:

- audit motion-blur and velocity-related textures, FBOs, and quad materials to ensure they are all materialized by normal pipeline execution before first use;
- add explicit warmup or validation in the relevant cache-or-create commands;
- detect and log first-use resource creation during the frame to find any remaining lazy-init dependencies.

### 7. The Preview Pipeline Still Likely Misinterprets Some Texture Formats Or Subresources

The newest runtime report shows that most FBO previews are still black, while depth-view previews can become uniformly white. That means the preview path itself still has unresolved format or subresource handling bugs.

Possible causes:

- the preview helper may still be selecting the wrong mip or array layer for some textures or texture views;
- depth or depth-stencil views may still be previewed without correct normalization or depth-mode handling;
- framebuffer attachments backed by texture views may not be using the same effective subresource selection as the live pass;
- the ImGui preview path may be sampling formats whose default visual interpretation is not meaningful without explicit remapping.

Potential fixes:

- add a preview fallback that performs CPU readback for the selected texture and renders the preview from a temporary debug texture, bypassing live GPU sampling state entirely;
- add explicit format-aware preview modes for `RG16f`, depth-only, and depth-stencil textures;
- report the exact source texture type, format, mip, layer, and whether the preview target is a base texture or a view directly in the inspector;
- add a histogram or min/max display for previewed textures so fully black or fully white output can be distinguished from bad preview interpretation.

## Highest-Value Next Debug Steps

1. Add post-pass diagnostics that sample the velocity texture and report whether any non-zero vectors were written that frame.
2. Log draw counts for the velocity pass versus the main scene passes to identify missing geometry coverage.
3. Add a dedicated false-color velocity visualization shader so signed RG values are unambiguous in the editor.
4. Verify previous and current unjittered camera view-projection matrices while moving the camera with TAA or TSR disabled.
5. Add a format-aware preview fallback path, especially for velocity and depth textures, so the editor preview is no longer dependent on live GL texture-view sampling.
6. If coverage still looks incomplete, prototype writing velocity during the main geometry render path instead of replaying geometry in a separate pass.

## Likely Long-Term Fix Direction

If separate-pass replay continues to be fragile, the cleaner v1 architecture is likely to generate motion vectors during the main geometry rendering path where the engine already has the exact active vertex shader, raster state, depth behavior, and transform data. That would avoid most of the current replay mismatches and remove the need for a fragile override-material velocity pass.
