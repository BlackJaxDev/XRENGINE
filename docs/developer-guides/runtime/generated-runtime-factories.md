# Generated Runtime Factories

`XREngine.SourceGenerators` uses Roslyn symbols to discover concrete runtime factory types and their accessible constructors. It targets `netstandard2.0` so the .NET 10 compiler can load it as an analyzer. The `Microsoft.CodeAnalysis.CSharp` and `Microsoft.CodeAnalysis.Analyzers` references are private build dependencies; they do not flow into the runtime application. Version 4.14.0 of Roslyn is used because it is already present in the repository's .NET 10 SDK environment and supports incremental generators.

`Build/Registration/RuntimeContracts.props` attaches the analyzer to the rendering, host, and bootstrap projects. `CommandsOnly` generates built-in render-command registrations in Rendering, `Portable` generates factories for the portable runtime closure in Host, and `Desktop` generates factories from the desktop integration closure in Bootstrap. The generator resolves inheritance and constructor signatures from compilation symbols, including referenced assemblies, and sorts registrations by fully qualified type name for deterministic output. Each destination assembly installs its generated factories through a module initializer, matching the existing registration lifetime.

NativeAOT launcher generation compiles game scripts through a separate source-referenced game project. That project keeps the game's assembly name and references `XREngine.Runtime.Bootstrap`, forwarding the selected renderer-backend property. The launcher references this project and Bootstrap; package restore follows that source-project graph rather than copying package references from the editor dependency manifest. The editor's managed game project retains its binary references for authoring and cooking. Bootstrap's optional desktop providers remain part of its source graph; choosing a runtime application profile does not remove their package references.

The browser registration manifest remains on `Tools/Generate-AotFactoryRegistrations.ps1`. Its allow-list and checked-in template are an explicit browser contract, not a discovery scan. The former Desktop, Portable, and CommandsOnly script modes were replaced after comparing the registered type sets on the current tree: Host 67, Rendering 265, and Bootstrap 0 registrations on each side, with no missing or extra registrations.

Assembly declarations install their generated contracts with `RegistrationLeaseGroup`, so unloading the owning module releases its registrations. `RuntimeTypeContract` gives an asset a stable ID and positive schema version; `Build/Registration/RuntimeContractSchemas.txt` records the public-member fingerprint. Duplicate IDs, mismatched fingerprints, and editor-only player references fail compilation. `RuntimeCookedAsset` selects the published codec for Data settings, the five Animation asset models, XRMesh, and XRTexture2D. The texture codec registration calls the existing typed streaming payload implementation; generation of that payload body remains open.

The user-settings contract uses version 2 for the combined body-measurement,
calibration, and spectator controls. Its reviewed fingerprint includes nullable
height and arm-span measurements and the persisted spectator output settings.

`RuntimeAnimationBinding` generates typed setters for the declared Transform members. `RuntimeClosedFormatter` roots declared closed List, Dictionary, HashSet, Nullable, ValueTuple, and one-dimensional array constructors, including jagged arrays with registered inner array types. Array readers preserve the encoded element type before applying a requested compatible conversion. Missing published array constructors fail explicitly. `RuntimeMemberAccess` generates leased property, field, method, and event accessors; `XRPersistentCall` consults generated method invokers before its reflective fallback. The current declarations are representative and must be expanded to the complete player contract set before the runtime fallback sites can be considered removed. `XREG001`–`XREG013` report invalid and duplicate declarations, but missing-construction and unclosed-generic coverage diagnostics are still pending.

Generated component factories now construct within a synchronous node-binding scope. The `XRComponent` base constructor claims the expected instance and assigns its `SceneNode` before the derived constructor runs. Completion attaches scene-node event handlers, reports the current transform, assigns `World`, and raises `ComponentCreated` in that order. A factory must return the same instance that claimed the scope; runtime-type registrations must also return the exact registered type. Failed construction removes the claimed instance from global object discovery and detaches engine handlers, but cannot undo arbitrary side effects of a derived constructor. Published builds require a registered factory; development builds report strict-parity diagnostics before using the reflective fallback. The generated metadata table currently contains declared asset identities and closed formatter types; additional player-reachable types and scan elimination remain open.

Third-party source imports and cache reconstruction consult
`RuntimeCookedBinarySerializer.TryCreateRegisteredRuntimeObject` before reflective
construction. Rendering installs an `XRShader` factory with its serialization
registration lease. Unregistered types still report reflective-factory parity
violations; a registered factory returning null or an incompatible type fails
explicitly. A supplied import target is reused without constructing another asset.

The explicit `SceneNode.AddComponent` factory overload uses the same node-binding
scope and accepts a factory returning a derived component. `TransformTool3D` uses
this path for editor selection.
