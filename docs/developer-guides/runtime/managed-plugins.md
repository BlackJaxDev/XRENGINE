# Managed Plugins

A managed plugin extends a desktop CoreCLR host, such as the editor, without changes to the engine. It is an
assembly built outside the engine that registers services through the engine's public registries, for example
`ThirdPartyAssetTypeRegistry` for a new file extension and `RuntimeThirdPartyAssetLoadingServices` for its loader.

## Writing a plugin

Implement `IXREnginePlugin` from `XREngine.Data.Plugins` and name it in an assembly attribute:

```csharp
using XREngine.Data.Plugins;

[assembly: XREnginePlugin(typeof(MyImporter.MyImporterPlugin))]

namespace MyImporter;

public sealed class MyImporterPlugin : IXREnginePlugin
{
    public IDisposable Install(XREnginePluginContext context)
        => ThirdPartyAssetTypeRegistry.Install("MyImporter", typeof(XRPrefabSource), typeof(MyImportOptions), ["myext"]);
}
```

`Install` returns one lease for everything it registered. Use `RegistrationLeaseGroup` when it registers several
services. The host disposes plugins in reverse install order at shutdown.

Reference engine projects or assemblies for compilation only, so the plugin does not ship its own engine copies:

```xml
<PropertyGroup>
  <EnableDynamicLoading>true</EnableDynamicLoading>
</PropertyGroup>
<ItemGroup>
  <ProjectReference Include="..\XRENGINE\XREngine.Data\XREngine.Data.csproj" Private="false" ExcludeAssets="runtime" />
</ItemGroup>
```

## Installing a plugin

A plugin is a folder whose entry assembly has the folder's name, next to its private dependencies:

```text
Plugins/
  MyImporter/
    MyImporter.dll
    MyImporter.deps.json
    SomeLibrary.dll
```

`DesktopPluginLoader` searches two kinds of roots:

- the `Plugins` folder next to the host executable;
- each path in the `XRE_PLUGIN_PATHS` environment variable, separated by `;` on Windows.

A root is either one plugin folder or a folder of plugin folders. The first plugin with a given name wins; later
ones are skipped with a warning.

The editor installs plugins right after the built-in model pipeline, so a plugin that wraps
`RuntimeThirdPartyAssetLoadingServices.Current` falls back to the engine's model loaders.

## Loading rules

- Each plugin gets its own `AssemblyLoadContext`. An assembly the host already loaded or ships next to its
  executable comes from the default context, so engine types keep one identity. Other dependencies resolve from the
  plugin's `deps.json` and folder.
- A plugin that fails to load or install is logged and skipped. The other plugins and the host keep running.
- Plugins are not unloaded or hot reloaded; a changed plugin needs a host restart.
- Published players, CoreCLR or NativeAOT, do not load plugins. Assets a plugin imports in the editor are native
  assets, so players do not need the plugin.
- A plugin runs with the host's full privileges. Install only plugins you trust.
