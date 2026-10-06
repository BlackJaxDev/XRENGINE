# Advanced Rendering Parity

This authored BrowserWebGPU sample isolates one static, opaque standard PBR mesh
with saved normals, UV0, tangents, and base-color, normal, metallic, and roughness
maps. It uses a fixed inspection camera/pawn, a directional light, and a point
light. The four texture maps are saved as native assets under `Assets/Textures`;
the material's authored bindings and legacy sampler slots resolve to those same
assets. Its own saved project and game scripts are the inputs to the normal Editor
publisher; the browser bootstrap loads the saved world through `Engine.Assets`.

The saved camera contains a concrete `AdvancedRenderPipeline` source with no GI,
mono output, no MSAA or temporal AA, fixed artist exposure, and disabled bloom.
The startup settings disable GPU render dispatch initially for the CPU-direct
Advanced path. GPU meshlet dispatch is a separate profile of the same authored
sample once the native path is qualified; it must not read GPU results back to
the CPU.

Prepare the canonical hash-bound engine shader recipes, then use the Editor's
normal Build Project action on `AdvancedRenderingParity.xrproj`:

```powershell
pwsh Samples/AdvancedRenderingParity/Prepare-BrowserShaders.ps1
dotnet run --no-build --project XREngine.Editor/XREngine.Editor.csproj -c Release -p:Platform=AnyCPU -- --build-project Samples/AdvancedRenderingParity/AdvancedRenderingParity.xrproj --build-configuration Release --build-platform BrowserWebGPU --output-subfolder BrowserWebGPU
```

The generated `Assets/Shaders/WebGPU` catalog is ignored. The preparation script
uses the shared canonical recipe inventory, including required Advanced native
visibility and shading kernels. Qualification requires a saved-world reload,
production publisher cook, and a live browser capture. A source or build check
alone does not establish that the native frame was presented.

The browser CI also stages a bounded shadow comparison from this saved world.
`Tools/BrowserSmoke/prepare-advanced-shadow-comparison.mjs` makes two new
projects under `Build/_AgentValidation`. Both use the same saved model, scripts,
textures, startup settings, and canonical cooked shader catalog. The staged
worlds add one directional and one point occluder with shared model geometry.
Each light uses a separate 256 by 256 shadow map, with cascades, the shadow
atlas, and contact shadows disabled. The point light uses sequential rendering.
Both lights use eight samples for each PCSS stage. The OFF world differs from
the ON world only in the two `CastsShadows` values. The helper checks the exact source and
generated world hashes and refuses an existing output directory. It does not
change this authored sample.

The Windows CI job publishes each staged project through the normal Editor CLI
and saves both bundles. Each bundle has `shadow-comparison.json`, which binds
the staged world hash to the published world payload hash and records hashes
for the shared source and shader catalog. The Linux `advanced-shadow-parity`
game entry loads the ON bundle and uses the OFF bundle as its same-scene
reference through `--baseline-publish`. This checks live browser output within
the existing game job deadline and assertions.

The comparison accepts a capture only when the canvas bounds stay stable and
the visible surfaces match the pinned scene projection. It rejects page strips,
shifted scene origins, and mismatched ON/OFF alignment before it compares receiver
pixels. Capture retries share one budget of at most ten seconds. A bad capture
fails the check; it cannot supply shadow evidence.
