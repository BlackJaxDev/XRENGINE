namespace XREngine.Editor;

/// <summary>
/// One file for <see cref="NativeAssetCreation"/>: its full path in the assets root, its exact
/// bytes, and the GUID that its metadata records. For an <c>.asset</c> file, the GUID must be the
/// ID that the serialized asset contains.
/// </summary>
internal readonly record struct NativeAssetCreationFile(string AssetPath, byte[] Bytes, Guid Guid);
