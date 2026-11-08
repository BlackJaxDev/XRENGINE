namespace XREngine.Core.Files;

/// <summary>
/// A source path that a cook cleared, because it is an absolute path outside every asset root. The cooked bytes hold no
/// value for it, and the publish lists it in its warning.
/// </summary>
/// <param name="AssetName">The name of the asset that holds the path, or its type name when it has no name.</param>
/// <param name="Path">The absolute source path that the cook cleared.</param>
public readonly record struct ClearedCookedSourcePath(string AssetName, string Path);
