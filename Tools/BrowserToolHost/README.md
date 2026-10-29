# Native host services for browser tools

`BrowserToolHost.props` shares native file and platform-folder adapters between
`BrowserRuntimeMetadataCooker` and the BrowserSmoke publisher driver. These are
native command-line tools. The adapters do not enter the browser runtime
project graph. The include reuses the existing desktop cache writers without
adding the Windows-targeted desktop platform assembly.

Install `LocalAssetFileSystem` and `LocalPlatformPaths` before
`RuntimeAssetBootstrap.InstallEngineAssetServices` or another engine entry
point. Engine service registration can initialize `Engine.Assets`. Its native
constructor requires a metadata file provider, even when the caller only needs
serialization registrations. The file adapter does not start file watchers.

The metadata cooker uses `<output-directory>.host` for mutable assets, cache,
metadata, and platform folders. For `--verify`, this is beside the fixture
directory. For `--scan`, this is beside the site directory. The tool sets its
process-local game asset, cache, and metadata path variables before engine
initialization. It does not write those folders into the input payload. Engine
assets retain normal discovery and the `XRE_ENGINE_ASSETS_PATH` override.

The publisher uses its caller-owned validation root for platform folders and
loads its copied project through the normal engine project API. Callers can
set `XRE_GAME_ASSETS_PATH`, `XRE_GAME_CACHE_PATH`, and `XRE_GAME_METADATA_PATH`
for initial host state. Project loading selects the copied project's assets;
explicit cache and metadata overrides remain active.
