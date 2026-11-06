using XREngine.Cutscenes;

namespace XREngine.Editor.Cutscenes;

/// <summary>One asset of a <see cref="CutsceneT0Report"/>.</summary>
/// <param name="AssetId">The asset identifier.</param>
/// <param name="Kind">The kind of the asset.</param>
/// <param name="ProxyBytes">The estimated size of the T0 data of the asset, in bytes.</param>
public readonly record struct CutsceneT0ReportEntry(Guid AssetId, EResidencyAssetKind Kind, long ProxyBytes);
