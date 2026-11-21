# Derived Render Pipelines

A derived render pipeline changes a base pipeline with patches instead of a full copy. The base can be a code pipeline (`Default`, `Advanced`, or `Rvc`) or another script. When the base changes, the patches apply to the new base. This guide describes anchors, the patch kinds, rebase, and VR use. For the general script syntax, see [Render Pipeline Scripts](render-pipeline-scripts.md).

## Model

A derived pipeline is an instance of the base type plus `RenderPipeline.Overlays`, root overlay first. Each `RenderPipelineOverlay` holds:

- Command patches (`RenderCommandPatch`). They apply after `GenerateCommandChain`.
- Resource patches (`RenderResourcePatch`). They apply after `DescribeResources`.
- Schema patches (`PostProcessSchemaPatch`). They apply after `DescribePostProcessSchema`.
- Setting overrides. They apply through the property setters when the script loads.
- The base type name, the parent path, and the saved base structure hash.

A pipeline never renders part of an overlay. When a command patch does not apply, the pipeline renders the base chain without any command patches, and `OverlayReport` records the failure. Resource patches and schema patches are checked the same way: a part with a failed patch does not apply.

## Anchors

A patch finds its target by an anchor id, such as `post.bloom`. Built-in pipelines set `ViewportRenderCommand.AnchorId` on the commands that users are likely to change. A built-in pipeline keeps each anchor id when it changes its chain, or records the rename in `RenderPipeline.AnchorAliases`.

| Pipeline | Anchors |
|---|---|
| Default | `default.sceneWorkload`, `default.ambientOcclusion`, `default.forwardSurface`, `default.ambientOcclusionResolve`, `default.lightProbeSync`, `default.transparency`, `default.velocity`, `default.screenSpaceUI`, `post.bloom`, `post.motionBlur`, `post.dof`, `post.temporalBegin`, `post.temporal`, `post.compositeDefaults`, `post.exposure`, `post.composite`, `post.final`, `post.upscale`, `post.atmosphere`, `post.volumetricFog` |
| Advanced | The stage markers `advanced.frameBegin`, `advanced.deformation`, `advanced.visibilityPreparation`, `advanced.visibility`, `advanced.lateVisibility`, `advanced.directionalShadows`, `advanced.ambientOcclusionStage`, `advanced.classification`, `advanced.shading.native`, `advanced.latePasses`, `advanced.post`, `advanced.output`, `advanced.userInterface`; and `advanced.ambientOcclusion`, `advanced.ppll`, `advanced.depthPeeling`, `advanced.lateTransparency`, `advanced.weightedBlendedOit`, `advanced.screenSpaceUI`, `advanced.finalOutputSource`, `post.processing`, `post.bloom`, `post.temporal`, `post.temporalBegin`, `post.antiAliasing`, `post.motionBlur`, `post.dof`, `post.atmosphere`, `post.volumetricFog`, `post.exposure`, `post.composite`, `post.final` |
| RVC | The Default anchors, plus `rvc.visibilityMaskStencil`, `rvc.visibilityTargets`, `rvc.attributeReconstruction`, `rvc.hzbRejection`, `rvc.pixelToShadeletMap`, `rvc.materialShadeletShading`, `rvc.foveatedShadingRate`, `rvc.headSpaceLightClusters`, `rvc.sharedLighting`, `rvc.reuseValidation`, `rvc.temporalCache`, `rvc.transparencyForwardPlus`, `rvc.foveatedResolve`, `rvc.diagnosticOverlay` |

Use `get_render_pipeline_structure` or the Workbench command context menu (**Copy anchor id**) to find anchors. Commands that a patch inserts get anchors of the form `overlay0.patch1.cmd0`, so a later overlay can target them.

A command with no anchor can be targeted by a structural path, such as `"path:VPRC_IfElse[Bloom]#0>TrueCommands/VPRC_BloomPass#0"`. Use **Copy patch target** in the Workbench to get it. A structural path breaks when the base adds commands before the target, so the validator warns about it.

## Patch Kinds

### Commands

```
after(post.bloom) {
    annotation(Label="After bloom");
}
before(post.final) { ... }
replace(post.dof) { ... }
append(post.bloom) { ... }
remove(post.motionBlur);
wrap(post.bloom) if (var UseBloom == 1) as "Bloom gate";
wrap(post.final) {
    bindFBOByName(FrameBufferName="HDRSceneFBO");
}
set(post.bloom).Label = "Bloom choice";
condition(post.bloom) = (stage bloom enabled);
```

| Statement | Effect |
|---|---|
| `after(t) { ... }`, `before(t) { ... }` | Inserts the commands after or before the target. A push target counts with its paired pop. |
| `replace(t) { ... }` | Removes the target and inserts the commands in its place. |
| `append(t) { ... }` | Adds the commands at the end of the first child container of the target, such as the true branch of an `if`. `append(root)` adds them at the end of the chain. |
| `remove(t);` | Removes the target, with its paired pop and its children. |
| `wrap(t) if (condition) as "Label";` | Puts the target in a new `if` branch. |
| `wrap(t) { push; }` | Puts the target between one push command and its paired pop. |
| `set(t).Property = value;` | Sets one property of the target. |
| `condition(t) = (condition);` | Replaces the data condition of an `if` or `switch` target. |

Patch bodies use the normal command syntax, and their errors report the line in the script.

### Resources

```
resources {
    Texture GlowTex(Format=Rgba16f);
    override Texture HDRSceneTex(Format=Rgba32f);
    remove UnusedTex;
}
```

A declaration adds a resource, `override` replaces a base resource, and `remove Name;` removes one. A resource patch changes the resource generation key, so a change builds a new generation.

### Schema

```
schema {
    stage "my.glow"(DisplayName="Glow");
    hide motionBlur;
    default bloom.Intensity = 0.5;
    category bloom = lens;
}
```

A stage declaration adds a stage. `hide` removes a stage. `default` changes the default value of a parameter. `category` moves a stage to another category.

### Settings

A `settings { Name = value; }` block sets pipeline properties. The writer saves only the settings that differ from the base (or from the parent script).

## Rebase

The writer saves `// base-hash: <hash>` above `derive` when the script has patches. The hash covers the anchors, the command types, the resource names, and the stage keys of the base. When a script loads and the hash differs, the loader reports a warning. Each patch then reports one status:

| Status | Meaning |
|---|---|
| Applied | The patch applied. |
| AnchorMoved | The target resolved through `AnchorAliases`. The patch applied. |
| AnchorMissing | The base has no such target. The part does not apply. |
| Failed | The patch did not apply, for example a `set` with an unknown property. The part does not apply. |
| NotApplied | An earlier patch failed, so this patch did not run. |

In the Workbench Patches view, **Accept rebase** writes the new anchor ids into the patches and stores the current base hash. **Revert** removes one patch. Save the script to keep the change.

## Validation

| Rule | Severity | Meaning |
|---|---|---|
| `derived.patch-not-applied` | Error or warning | A patch has a missing anchor or failed. A renamed anchor is a warning. |
| `derived.structural-path` | Warning | A patch targets a structural path. |
| `derived.base-type-mismatch` | Error | An overlay was made for a different base type. |

## VR Eye Pipelines

`VR Render Pipeline Script` (`VrRenderPipelineScript` in the engine rendering settings) selects an `.xrs` script for VR eye output. The script must derive from the pipeline type of `VR Render Pipeline`, for example `derive Rvc;` when `VR Render Pipeline` is `Rvc`. `OpenXrEyeRenderPipelineFactory` creates the eye pipeline for the eye topology, then applies the overlays and settings of the script. The base arguments of the script are ignored, because the eye topology selects them.

When the script does not exist, has errors, has patches that do not apply, or derives from a different type, the factory throws `NotSupportedException`. The XR output does not render, and no other pipeline is used. Leave the setting empty to use the plain family pipeline.
