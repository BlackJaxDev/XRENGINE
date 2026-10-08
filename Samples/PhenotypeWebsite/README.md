# Phenotype native UI sample

This sample builds a small XRENGINE 2D UI in a host scene. It has a title, a panel, a button, and status text. A click on **Explore** changes the status text. It uses native UI components and a caller-supplied `FontGlyphSet`.

Build `PhenotypeWebsite.csproj` with the repository's .NET 10 SDK and restored dependencies. This is a class library, not an executable or a saved scene asset. Reference the project from the desktop host that creates your world. After the scene parent belongs to that world, call the factory with its active camera and input pawn:

```csharp
using PhenotypeWebsite;

// Supply a FontGlyphSet loaded by the host from its own font asset.
SceneNode uiRoot = PhenotypeUiScene.Create(sceneParent, camera, pawn, font);
```

Keep `uiRoot` in the scene for the life of the UI. The factory sets `camera.UserInterface` to its screen-space canvas. It adds `UICanvasInputComponent` to the canvas node, sets its `OwningPawn`, and sets `pawn.UserInterfaceInput`. The host must possess the pawn and route pointer or controller input through it. If the host already uses a UI canvas or input component, decide which UI owns these properties before calling the factory. The UI uses a 1280 × 720 canvas and fixed panel dimensions. Adjust the constants in `PhenotypeUiScene.cs` for a different composition.

This sample does not provide a WebGPU website or an Azure deployment. The current browser export rejects native UI transforms and camera `UserInterface` references. The browser's reference runtime does not render this native UI scene. A WebGPU UI renderer and a supported scene cook path are required before this scene can be cooked for the browser. Do not publish the reference fixture as this scene.

## Azure preparation

[Azure setup](Azure/README.md) contains the official MCP configuration example,
account sign-in steps, and a package script for a dedicated Windows App Service.
The package script accepts a complete cooked player output. It does not cook this
sample or add browser UI support. It does not create resources or deploy them.

## Verification

On October 8, 2026, `dotnet build Samples/PhenotypeWebsite/PhenotypeWebsite.csproj
-c Release -m:1` succeeded with .NET SDK 10.0.401, with zero warnings and errors.
The PowerShell package script, IIS XML, and MCP JSON passed syntax checks.
No native-editor visual or input verification was performed.

The separate browser publish restored dependencies and built shared libraries,
but failed in `ComputeWasmBuildAssets` with MSB4216 and MSB4027. The task host
reported a named-pipe socket permission failure in the execution environment.
SDK 10.0.401, workload set 10.0.401.1, and PowerShell were installed before that
attempt. No finished WebGPU bundle or cooked native UI world was produced.

Material AI assistance was used to prepare this sample and deployment templates.
