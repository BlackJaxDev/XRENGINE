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
