# Advanced authored background rendering

Advanced initializes every native HDR pixel and its velocity/reactive sidecars in
`ShadeBackground.comp`. Draw IDs zero and `uint.MaxValue` are background sentinels:
they receive the configured clear RGB and alpha zero. Native opaque shading owns
all valid scene geometry.

Immediately after native opaque shading, the authored Background bucket executes
through `VPRC_RenderAdvancedBackground`. This is an explicit CPU-direct lane for
specialized sky shaders; native opaque submission remains GPU driven. The lane
loads the canonical HDR/native-depth framebuffer without clearing. It writes RGB
only, before scene-color snapshots, transparency, temporal processing and post.

A material must have an `AdvancedBackgroundMaterialProfile` receipt for its current
`ShaderStateRevision`. Creating that receipt asserts that every active vertex
variant emits canonical far depth, no shader writes fragment depth, and only color
attachment zero is produced. Mesh shape and local vertex depth may vary; arbitrary
geometry that retains varying raster depth is outside this profile. Shader edits
invalidate the receipt and require the authoring code to recreate it after review.

Runtime admission checks every effective submesh material, the Background bucket,
enabled depth testing, disabled depth writes, canonical `Lequal`, disabled alpha
writes, disabled stencil and blending. Backends map canonical `Lequal` to `Gequal`
for reversed depth. Pipeline overrides, per-draw render-state overrides and a
conflicting viewport submission override reject the lane with a diagnostic.

`SkyboxComponent` supplies mono and OVR stereo far-depth vertex shaders. Texture
mode requires an authored environment texture; gradient and procedural modes have
no texture prerequisite. `AtmosphericScatteringComponent` currently certifies its
mono sky shader. Its sky is explicitly rejected in SPS/multiview until an
eye-correct shader profile is supplied. OpenXR two-pass and Vulkan captures retain
their own runtime validation requirements.

Material inspection through MCP `get_material_uniforms` exposes
`advancedBackground.monoValid`, `stereoValid`, and the corresponding rejection
reasons. A receipt alone is not runtime acceptance: validate clear/HDR/alpha,
opaque preservation, depth conventions, both eyes, and each owned output profile.
