# Binary Texture Cache Routed Through YAML

Status: resolved and live-validated on 2026-08-22.

## Problem

Loading the Unit Testing World could break in the debugger with:

```text
YamlDotNet.Core.SemanticErrorException: Did not find expected <document end>.
```

The stack ended in `XRAssetDeserializer.TryHandleScalarXRAsset`. The texture log identified the triggering asset as:

```text
Build/Editor/Debug/AnyCPU/Debug/net10.0-windows7.0/Cache/Engine/Textures/
TextureStreaming_v3_preview64_rgba8_uncompressed_binary/
overcast_soil_puresky_4k.exr.XREngine.Rendering.XRTexture2D.asset
```

That file is a 178,958,397-byte raw cooked-binary texture payload, not YAML. The failure was caught by the texture loader and replaced with a filler texture, but first-chance exception handling still stopped the debugger.

## Root Cause

The texture cache writer intentionally emits a raw streaming payload without a YAML envelope. Three read boundaries did not honor that contract:

- generic `AssetManager.DeserializeAssetFile` always opened `.asset` files with `StreamReader` and YamlDotNet;
- the texture cache codec passed the raw body to `RuntimeCookedBinarySerializer.Deserialize`, which expects a top-level type marker/envelope;
- preview type inspection attempted to parse binary cache bytes as YAML.

The YAML asset-reference converter also consumed an unknown scalar immediately before throwing. On malformed or binary input, advancing the parser could replace its intended actionable error with YamlDotNet's document-end error.

## Resolution

- Added an `XRTexture2D` streaming-payload decoder that accepts either a raw texture body or a complete runtime-cooked texture envelope and verifies that the payload is fully consumed.
- Routed synchronous, asynchronous, generic, and runtime-typed texture asset loads through that decoder when the file has a binary marker.
- Updated the registered texture cache codec to use the raw-body-aware decoder.
- Prevented binary texture payloads from falling through to UTF-8/YAML parsing in resident-data, manifest, and preview inspection paths.
- Kept normal authored YAML texture assets on the existing YamlDotNet path, including YAML files that begin with whitespace.
- Removed the unnecessary parser consume from the unknown XRAsset scalar error path.

## Validation

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`: passed, 0 warnings, 0 errors.
- `dotnet build .\XRENGINE\XRENGINE.csproj --no-restore`: passed, 0 warnings, 0 errors.
- A disposable diagnostic executable loaded the exact 178,958,397-byte cache file through `AssetManager.DeserializeAssetFile` as a 4096x2048 `XRTexture2D` with 13 mips and the correct original EXR path.
- Isolated MCP editor session `yaml-binary-texture-fix` ran the Vulkan Unit Testing World against the existing editor cache. Its `find_asset` call loaded the exact binary cache path successfully as `XREngine.Rendering.XRTexture2D`.
- The captured Vulkan viewport rendered textured Sponza content successfully:
  `Build/_AgentValidation/20260822-204655-yaml-binary-texture/mcp-captures/Screenshot_20260822_205610_609_2d08a8f54b674dcda8cff6699d8d436f.png`.
- Post-shutdown logs for process 37960 contained no `SemanticError`, `Did not find expected`, `YamlDotNet`, texture asset load failure, filler fallback, unhandled exception, or Vulkan validation error.

The isolated full-editor build reported nine existing warnings from the `OscCore-NET9` submodule; the changed projects themselves remained warning-free.
