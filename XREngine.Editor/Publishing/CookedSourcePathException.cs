namespace XREngine.Editor.Publishing;

/// <summary>
/// A cook fails because a cooked file would hold a machine path: a source path outside every asset root, or an absolute path
/// that the check after the cook finds in the cooked bytes. The message names the asset and the path.
/// </summary>
/// <remarks>
/// It is not an <see cref="InvalidOperationException"/>, so the cook does not fall back to a copy of the YAML source.
/// </remarks>
public sealed class CookedSourcePathException(string message) : Exception(message);
