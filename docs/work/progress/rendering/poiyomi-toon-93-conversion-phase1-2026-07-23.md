# Poiyomi Toon 9.3 Conversion - Phase 1 Complete

- Date: 2026-07-23
- Status: Complete
- Scope: Lossless Unity material and asset ingestion
- Target: Poiyomi Toon 9.3.64

## Outcome

Phase 1 replaces the importer-local Unity YAML model with reusable source
documents. Shader-specific conversion now starts only after the material and
shader GUID metadata have been parsed and resolved.

The normalized Poiyomi descriptor preserves:

- The exact source YAML for diagnostic round trips.
- Shader references, render queue, valid/invalid keywords, disabled passes,
  override tags, and unknown serialized fields.
- Texture, float, integer, vector/color, and string properties.
- Every texture reference, UV scale/offset, resolved asset path, and Unity
  texture-import setting relevant to sampling.
- Locked-material animation identity, including Thry optimizer property
  renames using `thry_rename_suffix` and `<property>Animated` tags.
- Missing references and array/cube assets as structured diagnostics instead
  of flattening or silently dropping them.

## Main Contracts

- `UnityMaterialDocument` and `UnityMaterialDocumentParser`
- `UnityTextureImportDocument` and `UnityTextureImportDocumentParser`
- `UnityAssetReference`, `UnityResolvedAsset`, and `UnityAssetResolver`
- `PoiyomiMaterialDescriptor`, `PoiyomiTextureDescriptor`, and
  `PoiyomiMaterialDescriptorFactory`
- `UnityMaterialImportResult.SourceDocument`, `ShaderAsset`, and
  `PoiyomiDescriptor`

`UnityMaterialImporter` still owns the existing descriptor-to-uber conversion
temporarily. Later phases will move that shader-specific work into the
dedicated converter types described by the main plan.

## Locked Material Rules

The matcher accepts the pinned unlocked shader GUID and optimizer-generated
materials whose `OriginalShaderGUID` override tag identifies that shader. It
does not require the original shader file to remain at its previous path.

For a renamed animated property, the descriptor retains both names:

- `SourceName`: the serialized generated-shader property.
- `SemanticName`: the original Poiyomi property.

The normalized value is stored under the semantic name, so equivalent locked
and unlocked materials produce equivalent descriptor dictionaries.

## Texture Boundary

Phase 1 resolves texture paths and preserves 2D, 2D-array, cube, cube-array,
and unknown shapes in descriptors. It intentionally does not reinterpret an
array or cube as a 2D texture. Native array/cube runtime binding is part of the
feature conversion phases and is reported as preserved-but-not-yet-bound.

Sampler metadata includes color space, normal/data role, normal channel,
alpha source, alpha-as-transparency, U/V/W wrapping, filter mode, mip
generation, mip bias, anisotropy, and texture shape.

## Validation

- `dotnet build .\XRENGINE\XREngine.csproj --no-restore`
  - Passed with 0 warnings and 0 errors.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~PoiyomiPhase1IngestionTests"`
  - Passed: 4/4.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-build --no-restore --filter "FullyQualifiedName~PoiyomiPhase0CatalogTests|FullyQualifiedName~UnityPoiyomiMaterialImporterTests"`
  - Passed: 7/7.

The Phase 1 fixtures cover an older scalar-keyword Unity material layout, a
new valid/invalid-keyword layout, full TextureImporter sampling metadata,
locked/unlocked semantic equivalence, array preservation, and missing GUID
diagnostics.
