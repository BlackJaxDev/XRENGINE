# Render Pipeline Scripts

A render pipeline script (`.xrs`) is a text file that holds a complete render pipeline. Every pipeline can save to a script: an authored `CustomRenderPipeline`, a code pipeline with changed settings, and a derived pipeline with patches. This guide describes the syntax. For derived scripts and patches, see [Derived Render Pipelines](derived-render-pipelines.md).

Load a script with `RenderPipelineScriptLoader.LoadFile(path)` or `RenderPipelineScriptLoader.Load(text)`. Save a pipeline with `RenderPipelineScriptWriter.Write(pipeline)`. `RenderPipelineScriptWriter.WriteExpanded(pipeline, profile, notes)` writes a code pipeline as a standalone script for one resource profile; [default-render-pipeline.xrs](default-render-pipeline.xrs) is an example.

## File Kinds

| Kind | First statement | Result |
|---|---|---|
| Standalone | No `derive` | A `CustomRenderPipeline` with the commands, passes, resources, and stages of the file. |
| Derived from a code base | `derive Default;` or `derive Advanced(stereo: true);` | An instance of the base type, with settings and patches. |
| Derived from a parent script | `derive "../parent.xrs";` | The pipeline of the parent script, with more settings and patches. The path is relative to the file. |

`RenderPipelineScriptBases` lists the code bases and their arguments. The registered bases are `Default`, `Advanced`, and `Rvc`.

## Standalone Scripts

```
passes {
    scene 0;
    scene 3 sorter nearToFar;
    depends 3 on 0;
}

resources {
    Texture HDRSceneTex;
    Texture HDRSceneDepth(Format=Depth24Stencil8);
    FrameBuffer HDRSceneFBO(Attachments=[attachment(ResourceName="HDRSceneTex"), attachment(ResourceName="HDRSceneDepth", Attachment=DepthStencilAttachment)]);
}

schema {
    library tonemapping;
    stage "my.glow"(DisplayName="Glow", Category="effects") {
        param Strength(Kind=Float, Min=0, Max=4);
    }
}

bindFBOByName(FrameBufferName="HDRSceneFBO");
renderMeshesPass();
renderMeshesPass(RenderPass=3);
unbindFBO();
if (stage "my.glow" enabled) as "Glow" anchor my.glow {
    annotation(Label="Glow pass");
}
bindOutputFBO(ClearDepth=false, ClearStencil=false);
tonemap(SourceTextureName="HDRSceneTex");
unbindFBO();
```

### Passes Block

- `scene N;` registers scene pass `N`. Add `sorter name` to sort its commands. The sorters are in `RenderPipelineScriptSorters`, `nearToFar`, `farToNear`, and `opaqueStateBucket`.
- `depends A on B;` adds an explicit pass dependency.

### Resources Block

Each line declares one resource: `Kind Name(Properties);`. The kinds are `Texture`, `TextureView`, `RenderBuffer`, `Buffer`, `FrameBuffer`, and `QuadMaterial`. The properties are the properties of `AuthoredRenderResource`, for example `Format`, `Size`, `Samples`, `Layers`, `MipLevels`, `StorageImage`, `Optional`, `SourceTexture`, `Attachments`, `FragmentShader`, and `InputTextures`. A property that has its default value is not written. A `QuadMaterial` with a `FragmentShader` path uses a custom shader.

The pipeline converts the declarations into the same layout builder calls that code pipelines make. Commands read declared names only; they never create a resource.

### Schema Block

- `library key;` adds a stage from the shared stage library, for example `tonemapping` or `bloom`.
- `stage key(Properties) { param Name(Properties); }` adds an authored stage with parameters. Quote a key that has a dot, for example `"my.glow"`.

See [Post-Process Editor Extension](../post-process-editor-extension.md#authored-stages).

### Commands

A command is `name(Property=value, ...);`. The name is the command type without the `VPRC_` prefix, with a lower-case first letter. Values are numbers, strings, `true` and `false`, enum names, lists in `[...]`, and nested objects such as `attachment(...)` and `size(...)`. Push commands, such as `bindFBOByName`, pair with a pop command, such as `unbindFBO`. Any command can set `AnchorId="id"`.

### Branches

```
if (condition) as "Label" anchor some.id { ... } else { ... }
switch (condition) as "Label" { case 0: { ... } default: { ... } }
repeat(2) { ... }
```

`as "Label"` and `anchor id` are optional. A data condition saves to text:

| Condition | Meaning |
|---|---|
| `var Name`, `var Name == 1` | A pipeline variable. The operators are `==`, `!=`, `>`, `>=`, `<`, and `<=`. `!var Name` tests for false. |
| `setting Name` | A registered pipeline setting. |
| `stage key enabled` | A schema stage is on for the camera. |
| `capability StereoPass` | A runtime capability, such as `StereoPass`, `ShadowPass`, `MirrorPass`, or `SceneCapturePass`. |
| `true`, `false` | A constant. |

A branch with a code condition saves as `(code)` with an `// unconverted:` comment. It loads as a branch with no condition, and the validator reports it.

## Derived Scripts

```
// base-hash: 1f2e3d4c5b6a7980
derive Default;

settings {
    EnableDeferredMsaa = false;
}

after(post.bloom) {
    annotation(Label="After bloom");
}
```

A derived script holds a settings block, patch statements, and the patch forms of the resources and schema blocks. It cannot hold plain commands or a passes block. The writer adds the `base-hash` comment when the script has patches. See [Derived Render Pipelines](derived-render-pipelines.md).

## Problems

A load returns `RenderPipelineScriptLoadResult` with the pipeline and a list of problems. Each problem has a severity, a line, and a column. A syntax error never throws. Unconverted items, such as resources that only code can create, are listed in the problems and in `// unconverted:` comments.
