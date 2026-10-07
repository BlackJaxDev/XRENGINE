# WebGPU module and engine snapshot bridge

**Date:** 2026-09-28. **Base:** `be1c472f4f94a8e78ad5133728b709c7e60aedcb`.
**State:** Source implemented. Builds, browser/GPU execution, desktop regressions
and device validation remain deferred at the user's request. No tests were added
or run. No runtime or performance acceptance is claimed.

## Registered renderer leaf

`XREngine.Runtime.Rendering.WebGPU` now owns the generated GPU imports, managed
renderer host, JavaScript WebGPU executor, resource table, shader loader, WGSL
source, recipe and generated shader package. The application imports its
`BrowserAssets.props` to publish these files under `wwwroot/webgpu`; shader cooking
uses the same command with defaults redirected into this module. The portable
project guard explicitly admits this leaf and retains its existing native and
package restrictions.

`BrowserRendererComposition` registers `WebGpuRendererBackendModule` through the
existing `RendererBackendCatalog`. The factory receives the actual
`BrowserCanvasRenderTarget`, checks target/module generation, and returns a
pending `IRuntimeRendererHost`. The session resolves its `IBrowserRendererHost`
capability for uploads and submissions. The module declares only browser canvas
presentation; compute, indirect rendering, WebXR and desktop capabilities are
not inferred from the WebGPU name.

The catalog's original capability/generation checks remain in use. Portable
extraction splits desktop-only members from the existing host/context interfaces
into `.Desktop.cs` partial declarations. Desktop builds include both parts and
retain their API; portable builds include the common part without pulling in
native windows, advanced pipeline resources or RVC. No replacement catalog or
shadow desktop resource types were introduced.

Browser startup creates the pending managed session before awaiting device and
shader readiness. It then binds the JavaScript session owner, registers routing,
and acknowledges readiness before uploads. Packet submission checks managed
owner/surface identity and consumes the sealed arena synchronously; the executor
retains its complete packet/resource checks. New resource uploads make bounded
cold-path copies across the assembly boundary. Existing draws do not repeat
those descriptor copies.

Failure/loss marks the host terminal. Shutdown invalidates managed resource
ownership and disposes the corresponding executor. Module unregistration disposes
remaining hosts; cooperative unload requires all sessions to have stopped. The
module remains statically registered for the application lifetime, with no claim
of browser assembly hot reload or AOT qualification. Successful packet submission
marks a replacement frame ready; this is submission state, not GPU completion or
visual validation.

## Existing engine asset adapters

Shared resource descriptions moved from the Browser executable into the existing
rendering assembly under `Runtime/Browser/Assets`, in namespace
`XREngine.Rendering`. Desktop-only adapter files remain outside the portable
source allowlist.

| Input | Supported conversion | Explicit exclusions |
| --- | --- | --- |
| `XRMesh` | Resident CPU positions, UV0 and indexed triangles copied into immutable position/UV and uint32 buffers | Skinning, blendshapes, non-triangles, vertex colors, multiple/missing UV sets, missing CPU buffers, invalid indices or a geometry revision change |
| `XRMaterial` | An explicit `BrowserMaterialRecipe` tied to the exact source object, with opaque linear tint and optional caller-decoded RGBA8 texture | Automatic GLSL/Slang translation, inferred PBR semantics, transparency and arbitrary desktop texture decoding |
| `XRCamera` | Exact standard perspective/orthographic parameter types; inverse render transform and current projection copied into a snapshot | Custom subclasses, XR camera projections and undeclared depth conventions |
| Model transform | Finite matrix represented by an engine `Transform` | Nondecomposable, shear or perspective model matrices |

Standard engine perspective/orthographic projections use System.Numerics' 0..1
depth range, so the adapter does not apply an erroneous -1..1-to-0..1 conversion.
The captured projection includes its current authored camera state. It is static;
the browser does not instantiate the full desktop camera or reconstruct jitter,
temporal history, viewport settings or live camera behaviors.

Example export from an existing desktop composition/cook step:

```csharp
using System.Numerics;
using XREngine.Rendering;

// mesh, material, camera and modelMatrix come from the existing engine scene.
// Supply decodedTexture only when an authored browser texture is required.
var recipe = new BrowserMaterialRecipe(material, Vector4.One, decodedTexture);
var snapshot = new BrowserSceneSnapshot(
    [BrowserAssetAdapter.FromXRMesh(mesh)],
    [BrowserAssetAdapter.FromXRMaterial(material, recipe)],
    [new BrowserSceneInstance(0, 0, modelMatrix)],
    BrowserCameraSnapshot.FromXRCamera(camera));
File.WriteAllText("scene.browser.json", snapshot.ToJson());
```

Export from a stable scene/resource boundary. Geometry revision checks detect
ordinary mutation; they are not a lock or permission to read mesh buffers while
another thread releases them. Material mapping is deliberate author intent, not
a promise that every source material appears unchanged in the unlit profile.

## Browser scene import

The page's **Import scene** control reads a local versioned JSON snapshot. It
checks the 16 MiB file bound and parses the shared schema before stopping the
current session. A fresh imported session creates real `SceneNode`,
`BrowserMeshComponent` and `Transform` instances, uploads shared descriptions via
the module, and collects their render matrices into the existing packet path.
The transform composition check occurs during session creation, so an unsupported
model transform can still fail replacement startup after the old scene stops.
**Demo scene** clears the imported payload and restores the interactive fixture.

The format has schema version 1, packed float position/UV arrays, uint32 indices,
base64 RGBA8 pixels, material tints, indexed resource references and row-major
16-float matrices. Parsing rejects unknown properties, missing/null resources,
unsupported versions, invalid indices, nonfinite values and invalid dimensions.
It is capped at 4,096 meshes/materials, 2,048 instances, 64 MiB of decoded resource
payload and 16 MiB of UTF-8 JSON. The bounded text format is an initial interchange
path, not compressed or streamed production world content. DTO/resource copies
allocate at import/export boundaries; peak memory has not been profiled.

Imported scenes have one captured full-canvas camera. Demo density and split-view
controls are disabled, and the demo spin/input motion component is not attached.
The original projection is retained across resizes, which can stretch its aspect
on a differently shaped canvas. Restart reuses the imported payload with a fresh
scene/device. Input events continue to be handled safely but do not move the
captured static world.

## Remaining integration and acceptance

- Generic engine resource and render-pass wrappers, `RenderFrameOutputDescription`
  with its scheduling dependency, acquired-output transaction and visibility /
  render-buffer publication. The packet path remains CPU-direct collection.
- Live mesh/material/camera updates, full scene hierarchy/components, asset
  streaming, texture deduplication across serialized materials, editor export UI,
  animation, lighting, transparency and production shading.
- Broader device capabilities, automatic device/resource recovery, mobile input,
  gameplay services and the separate WebGL2 fallback implementation.
- Portable publish (including linked module assets), desktop API compatibility,
  export/import image correspondence, matrix/depth/aspect behavior, shader paths,
  startup cancellation, multi-canvas isolation, resource teardown and physical
  mobile acceptance.

The active [runtime TODO](../../todo/platform/unified-desktop-browser-runtime-todo.md)
checks off module and static asset-bridge code separately from those open gates.
Usage and publish commands remain in the [browser README](../../../../XREngine.Browser/README.md).

Follow-up source work: [canvas output, visibility and live resource updates](browser-webgpu-frame-output.md).
