namespace XREngine.Cutscenes;

/// <summary>One asset that a residency bucket needs, with the level that the bake recorded.</summary>
/// <param name="AssetIndex">The index of the asset in <see cref="CutsceneResidencyManifest.Assets"/>.</param>
/// <param name="TargetLevel">The level index that the bucket needs. 0 is T0, 1 is T1, and 2 is T2.</param>
/// <param name="ScreenCoverage">The fraction of the screen that the asset covers in the bucket, from 0 to 1.</param>
public readonly record struct ResidencyEntry(int AssetIndex, int TargetLevel, float ScreenCoverage);
