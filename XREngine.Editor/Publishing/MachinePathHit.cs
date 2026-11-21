namespace XREngine.Editor.Publishing;

/// <summary>A machine path that <see cref="CookedOutputPathGuard"/> found in a file.</summary>
/// <param name="Offset">The byte offset of the first character in the file.</param>
/// <param name="ByteLength">The length in bytes of the found text.</param>
/// <param name="Utf16">True for UTF-16 little-endian text, false for UTF-8 or ASCII text.</param>
/// <param name="Text">The found text.</param>
public readonly record struct MachinePathHit(long Offset, int ByteLength, bool Utf16, string Text);
