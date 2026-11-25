namespace XREngine.Editor;

/// <summary>Shows asset load state in the native inspector.</summary>
public sealed record NativeAssetSelectionStatus(string SourcePath, string Status);
