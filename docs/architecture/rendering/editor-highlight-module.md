# Editor Highlight Module

The editor installs an optional highlight module for Advanced and RVC pipeline instances. Runtime applications have no module unless a host installs one. `EnableEditorHighlightModule` lets each Advanced or RVC instance turn the installed module on or off. A change rebuilds the command chain and invalidates the physical resource generation. Offscreen profiles keep the module inactive.

The module keeps editor hover and selection data out of scene stencil. Native visibility metadata seeds an `EditorHighlightMask` RGBA8 target. The mask uses R for hover, G for selection, B for built-in gizmos, and A for valid coverage. The mask uses nearest filtering. Late authored scene-color fragments write the same mask target as their color target. An opaque depth-writing fragment replaces the mask value, including zero flags on an unselected occluder. A blended fragment uses a per-channel MAX blend. The shader writes the mask before it calls the authored fragment entry, so `discard` and alpha-to-coverage affect color and mask together. A utility draw with no mask output disables writes to the mask attachment.

The editor outline compositor reads the mask after scene post-processing and anti-aliasing, then writes `EditorHighlightOutput` before presentation. Scene history does not contain the editor outline. Built-in gizmo materials carry an explicit overlay flag. Vulkan Advanced and RVC use that flag for mask B and omit the built-in gizmo stencil write. The Default pipeline retains its stencil-based editor display. OpenGL Advanced with the module off still uses the legacy built-in gizmo stencil marker.

`XRMaterial.ConfigureGizmoMaterial` sets the built-in overlay flag. Clear `EditorOverlayFlags` before a later use of that material as an authored scene material. An explicit render-options override keeps its stencil test in Advanced and RVC.

The module requires a Vulkan renderer and a single-sample scene target. It rejects unsupported OpenGL, WebGPU, MSAA, and weighted or exact OIT paths while enabled. An authored fragment must have one float `vec4` color output at location 0 and leave locations 1 and 2 free for the editor. The Vulkan shader compiler and reflected SPIR-V output interface enforce that contract. A failed or unsupported shader reports an explicit error; the renderer does not move the mask to a CPU path.

The runtime owns the neutral module contract and mask resources. The editor supplies the preference reader. The Vulkan backend freezes each draw's mask role and editor flags in the draw request. Program and graphics pipeline keys include the mask ABI and attachment location. Hover and selection flags change uniform values without changing the shader or pipeline key.

The seed has an explicit compute pass after native opaque shading. Forward mesh and material-outline passes depend on that seed and declare their mask attachment access. A pending seed declines the frame before later mask consumers run. A terminal seed failure propagates through nested command containers. Earlier work on intermediate targets can still submit. The producer uses a pooled submission marker and does not wait for the GPU on the CPU.

## Editor Picking Identity

Mesh submission scopes keep the authored render pass separate from the
render-graph scheduling index. Source-ordered prefix and suffix stages have
different scheduling indices but retain their authored transparent pass.
Vulkan uses the authored pass to classify mask and picking writes. It uses the
graph index for dependencies and submission. A scheduling split must not turn
a visible scene fragment into a utility draw that preserves the old identity.

The optional module also owns `EditorPickingIdentity`, an RGBA16 UNORM target at color location 2. R and G encode the low and high halves of a 32-bit key. B is 1 for a native logical draw index and 2 for an authored `StableQueryKey`. A is binary coverage. The compute seed copies native visibility identity. Late scene-color fragments then update the target in the same invocation as the authored color output. Native indices and authored keys use separate resolvers; equal numeric values do not identify the same object.

The identity attachment uses independent `SRC_ALPHA` and `ONE_MINUS_SRC_ALPHA` blending. A covered fragment replaces the previous identity. A discarded fragment or a zero-coverage fragment preserves it. Blend-disabled and replace draws use raster coverage. Straight-alpha draws use nonzero final alpha. Premultiplied draws use nonzero final alpha or a nonzero contribution in an enabled RGB channel. Additive draws require a nonzero enabled RGB contribution, and a nonzero alpha when the source blend factor uses alpha. These rules preserve signed contributions to the floating-point HDR scene target. Draws with no RGB writes preserve identity. Unsupported scene blend rules report an error. This policy does not change the highlight mask's MAX blend.

The fragment adapter calls the authored entry, then reads its final color. Shaderc preprocessing selects the active output declaration after automatic uniform rewriting. The adapter accepts a direct `layout(location = 0) out vec4` declaration, adds the color reader, and compiles that exact source. SPIR-V certification requires scene color, mask, and identity outputs. It does not depend on retained debug names. The draw's frozen `TransformId` supplies the authored command key. Its frozen blend state and RGB write mask supply the coverage rule. Both uniforms have Object frequency, and the object publication signature includes their inputs.

The identity format requires storage-image, color-attachment, and attachment-blend support. Query the physical format properties. The `shaderStorageImageExtendedFormats` feature provides a format-support guarantee; its device-enable value does not replace that query. See the [Vulkan feature contract](https://docs.vulkan.org/refpages/latest/refpages/source/VkPhysicalDeviceFeatures.html).

The editor retains a bounded snapshot of collected command keys and mesh owners. It pairs that snapshot with the pipeline resource generation, package identity, identity texture, and optional native scene publication. The required seed producer's fence proves native submission. A source becomes available only after the full command chain is authored and the same fence reports submission. A newly admitted seed invalidates the previous source because it can overwrite the same texture, even if later commands fail. Already queued queries keep their own mappings and ordered pixel copies.

`TryPickEditorAsync` and MCP `query_editor_pick` return the retained mesh owner. Native visibility diagnostics keep `TryPickAdvancedAsync` and `query_advanced_pick`; those APIs do not include authored late surfaces. Editor queries copy one eight-byte pixel through bounded asynchronous staging. They do not change the scene's GPU submission strategy. Request slots use generation tokens, so cancellation and later reuse cannot make an old GPU callback resolve a new query.

Turning off hover or selection colors keeps picking available. Removing the module removes its GPU identity and mask resources and makes complete editor picking unavailable. It does not substitute a native-only hit through an authored surface.

The snapshot ring has four reusable slots. Hover uses the returned `RenderableMesh` directly and checks that its scene node still belongs to the current world.

## Visual Validation

Use `capture_render_pipeline_texture` with `EditorHighlightMask` and companion `EditorHighlightOutput` to inspect the mask and composite at the same frame boundary. Use `capture_viewport_screenshot` with `include_screen_space_ui=true` to check the final window. The default `ViewportTarget` capture can read the scene target before the editor composite and omit a valid outline.
