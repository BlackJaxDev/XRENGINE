# Modular Pipeline Parity

This BrowserWebGPU sample publishes one saved world through the normal Editor
project builder. Its first two cameras share the exact same saved
`ModularClearRenderPipeline` object through a YAML anchor. The third camera
owns a separate `ModularQuadRenderPipeline` and a scoped `custom::custom-pass`
program. The fourth and fifth cameras own distinct `ModularMsaaRenderPipeline`
sources, saved with `CpuDirect` and `GpuIndirectZeroReadback` respectively.
All three source classes derive from `CustomRenderPipeline`;
the browser host uses its normal camera, command, resource and artifact paths.

The clear pipeline declares no programs, scene passes or material dependencies.
Its saved color is applied by `VPRC_SetClears`, then `VPRC_BindOutputFBO`
clears the output with depth and stencil clearing disabled. The quad pipeline
declares only its own program. It makes a normal generation-owned quad material
from the verified cooked program and draws it into the bound output. The quad
material explicitly disables blending, depth testing, culling and sample
coverage. Both saved cameras select AA None. The source class builds its command
chain with `AddUsing`, so each output bind receives the matching pop command.

The MSAA cameras select AA Msaa with four samples. Their game-defined pipeline
declares x4 RGBA16F color and Depth32Float attachments, a separate x1 RGBA16F
color target, and generation-owned framebuffers. Its mesh commands retain the
saved submission strategy even when the viewport requests another strategy.
Three saved scene components build indexed geometry with the exact scoped
`custom::msaa-scene` cooked program when a selected source receives its catalog:
one sloped opaque silhouette and two depth-sorted alpha-half quads. The
straight-alpha source-over blend uses RGB `SrcAlpha`/`OneMinusSrcAlpha` and
alpha `One`/`OneMinusSrcAlpha` over the opaque background. The center overlap
is linear RGB `(0.325, 0.5, 0.3)` with alpha one. A color-only framebuffer
resolve precedes the separate `custom::msaa-present` fullscreen shader, which
copies the resolved linear color to the player output without tone or gamma
conversion.

The saved game mode possesses clear camera A at startup. The ordinary pawn input
bindings select clear camera A, clear camera B, the quad, MSAA CPU or MSAA GPU
camera with keys 1 through 5. Each actual selection prints a Release-visible
camera/source/AA marker; MSAA selections also include sample count and strategy.
The bootstrap and runtime game mode check shared source reference
identity, saved source IDs, the quad's exact scoped program key, and camera AA
overrides. The two clear cameras have the same pixels by design, so their
distinct selection must be established from those checks and the switch logs.
One browser player has one physical viewport; this sample does not establish
simultaneous multi-viewport isolation.

Prepare the three sample-owned Slang recipes and four canonical GPU indirect
compute companions with the existing C# shader cooker. The script stages the
recipes together and produces one manifest so the custom programs remain
available alongside `indirect::cull-primitive`, `meshlets::select-lod`,
`authored-indexed::rank-sources` and
`authored-indexed::mask-ranked-arguments`:

```powershell
pwsh Samples/ModularPipelineParity/Prepare-BrowserShaders.ps1
```

Then use the Editor's normal Build Project action on
`ModularPipelineParity.xrproj`, or the normal Editor CLI:

```powershell
dotnet run --project XREngine.Editor/XREngine.Editor.csproj -c Release -p:Platform=AnyCPU -- --build-project Samples/ModularPipelineParity/ModularPipelineParity.xrproj --build-configuration Release --build-platform BrowserWebGPU --output-subfolder BrowserWebGPU
```

The expected activated player is
`Samples/ModularPipelineParity/Build/BrowserWebGPU/index.html`, accompanied
by `browser-publish.json` and `content/manifest.json`. Serve that build
directory over localhost HTTP or HTTPS. It is the shipping Editor-published
player shell. Capture clear A after the first presented frame, press 2 on the
focused canvas and capture clear B after another presented frame, then press 3
and capture the quad gradient after a further presented frame. Keys 4 and 5
select the two x4 scenes. Inspect the packaged manifest for all three scoped
custom programs and absence of Default tonemap/AO requirements. The named
`modular-pipeline-parity` BrowserSmoke case performs these checks on two fresh
starts, including resize, after each Release camera marker and a subsequent
canvas texture acquisition and GPU submission. Browser pixels,
YAML/cooked hydration, and the activated path all require a genuine publish
and live run; the tracked source alone does not qualify them.

The generated `Assets/Shaders/WebGPU` catalog is build output. Four-sample
framebuffers, blending, indirect submission and resize require the live x4
BrowserSmoke qualification. Precise unsupported-profile rejection remains
separate acceptance work.
