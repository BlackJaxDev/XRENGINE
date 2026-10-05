# Static Meshlet Parity

This saved BrowserWebGPU project is a static-only derivative of RenderingParity.
It retains the mapped panel's source mesh, texture bindings, material identity,
scene camera, lights, transform, and one authored LOD. The saved Default camera
pipeline and `Meshlets.Enabled: true` select the generic authored raster path.
The skinned and morphed ribbon is intentionally absent; this fixture provides
no deformation evidence. The default startup requests `GpuMeshletZeroReadback`.
The saved dispatch override is required. During play the fixture binds that
request to its camera's owned viewport before the first render frame; it does
not change the process-wide mesh submission setting.

The normal Editor browser publisher loads the saved world, builds the game
assembly, calls the canonical native meshoptimizer `GetOrCreateMeshletPayload`
cook for enabled submeshes, and writes the cooked game package. The runtime
bootstrap checks the saved source identities and rejects a published mesh
without an owner-validated cooked payload. A missing meshoptimizer native
library or export is a publication failure (`BrowserCook.MeshletBackendUnavailable`),
not permission to generate a browser-side substitute.

Prepare the shared engine shader catalog and publish from the Editor CLI:

```powershell
pwsh Samples/StaticMeshletParity/Prepare-BrowserShaders.ps1
dotnet run --project XREngine.Editor/XREngine.Editor.csproj -c Release --no-build --no-launch-profile -- --build-project Samples/StaticMeshletParity/StaticMeshletParity.xrproj --build-configuration Release --build-platform BrowserWebGPU --output-subfolder browser-game
```

The browser smoke lane stages two copies of this exact project. Only the staged
CPU copy changes `GPURenderDispatchOverride.Value` to `false`. Both copies go
through the real Editor publisher. The lane compares the same mapped panel
at the same initial and resized canvas extents, camera, material, lights and
output settings after each capture settles. It checks the explicit startup
request, CPU direct indexed and GPU indexed-indirect draws through the same
mapped raster module and entry points selected from each published material
catalog. The observer checks the selected descriptor and WGSL hashes against
the modules bound at the draws. It also checks actual LOD selection, meshlet cull and
finalize compute dispatches, zero GPU READ maps of any label, two fresh GPU
page starts, and zero live or retiring renderer resources after each owned
host stops. Indexed draw calls do not expose GPU-visible meshlet or triangle
counts.

This is SwiftShader browser correctness evidence. A passing source or Windows
Editor build alone does not establish a live GPU frame. The Linux Chromium
result and any physical GPU comparison must be reported separately.
