namespace XREngine.Editor.Publishing;

/// <summary>
/// A publish fails because a published file holds a machine path that the strip cannot remove safely, for example in a
/// compressed or checksummed container. The message names the file.
/// </summary>
/// <remarks>
/// It is not an <see cref="InvalidOperationException"/>, so the cook does not fall back to a copy of the YAML source.
/// </remarks>
public sealed class CookedSourcePathException(string message) : Exception(message);
