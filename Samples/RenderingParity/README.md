# Rendering Parity

This small authored world uses the same engine scene, game assembly, material
maps, skeleton and morph target on desktop and BrowserWebGPU. It complements
Rolling Ball without changing that game's course or gameplay.

The saved `Assets/Worlds/RenderingParityWorld.asset` contains a static textured
reference, an animated ribbon with two bones and the `Ripple` blendshape, an
inspection camera/pawn, a directional light and a point light. Base color is
sRGB; normal, metallic and roughness maps are linear. Both surfaces have saved
normals, UV0 and handed tangents. The authored game mode retains the marker
`lit-textured-deformed-v1` so hydration can distinguish it from the engine's
fallback game mode.

Space pauses only the ribbon animation; R returns to its bind pose. The camera
is fixed to keep desktop/browser captures comparable. The engine's normal play
pause controls still suspend simulation independently.

## Build and publish

Build the portable game source:

```powershell
dotnet build Samples/RenderingParity/RenderingParity.csproj -c Release
```

Prepare hash-bound shader artifacts from the existing canonical Slang recipes:

```powershell
pwsh Samples/RenderingParity/Prepare-BrowserShaders.ps1
```

Generated `Assets/Shaders/WebGPU` output is intentionally ignored. The preparation
script is the reproducible source and includes the actual packed-skinning
compute kernel and both textured vertex profiles. It requires the shader
cooker's existing pinned Slang setup; it adds no dependency.

Use the normal Editor Build Project action with `RenderingParity.xrproj`, or:

```powershell
dotnet run --no-build --project XREngine.Editor/XREngine.Editor.csproj -c Release -p:Platform=AnyCPU -- --build-project Samples/RenderingParity/RenderingParity.xrproj --build-configuration Release --build-platform BrowserWebGPU --output-subfolder BrowserWebGPU
```

This requires the existing browser SDK/workload and reviewed browser Jolt
source setup described in
[browser publishing](../../docs/work/progress/rendering/browser-project-publishing.md).
The publisher loads the saved XRWorld, builds the game assembly, projects only
the canonical desktop material stages into their cooked semantic companions,
cooks the ordinary runtime-binary object graph and packages a complete player.
The bootstrap loads the saved world through `Engine.Assets`; it never creates
a substitute browser scene, mesh, texture, skeleton or player rig.

For desktop inspection, open this project in the Editor and enter play. The
world keeps the canonical desktop material stages and uses the normal shared
ModelComponent/XRMeshRenderer animation path.

## Cooked cache compatibility

Publish this generic world with the matching engine build. The generic
`BinaryV1` member stream now carries the transform's effective serialized
reference identity separately from its runtime/cache ID. Older generic transform
caches lack that identity and report `CookedTransform.ReferenceIdentityMissing`;
recook from the unchanged authored assets. Older strict engine readers reject
the new named member, so unchanged outer format/version does not imply forward
compatibility. This is a derived-cache repair, not an authored-data migration;
Rolling Ball's custom v5/v6 codec and source bytes are unchanged.

## Qualification boundaries

Source construction, real generic hydration and the production Editor
save/cook/package path have passed strict graph checks. That includes the authored
game-mode marker and pawn/camera alias, exact scene bone/root identities, shared
geometry, named morph lookup, four mapped image roles, unchanged source YAML,
and constructor/retirement/retry behavior. Narrow cooking does not establish
rendered acceptance.
The milestone must prove the game-mode marker and authored pawn survived
hydration, the mesh palette still references the authored scene bones, actual
WebGPU deformation dispatches occurred, mapped lighting reached the presented
pixels, animation pause/reset work, and repeated start/stop releases resources.
Compare tolerant captures at the same camera and pose against desktop before
claiming parity. Physical-device performance and the browser/device matrix
remain separate acceptance checks.
