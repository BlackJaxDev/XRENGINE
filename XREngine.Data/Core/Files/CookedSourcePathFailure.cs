namespace XREngine.Core.Files;

/// <summary>A source path that a cook could not make portable, because it is outside every asset root.</summary>
/// <param name="AssetName">The name of the asset that holds the path, or its type name when it has no name.</param>
/// <param name="Path">The absolute source path.</param>
public readonly record struct CookedSourcePathFailure(string AssetName, string Path);
