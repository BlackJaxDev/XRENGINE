namespace XREngine.Rendering;

/// <summary>
/// Publishes one default world font request and result to debug text readers.
/// </summary>
internal sealed record DefaultWorldFontPreparationSnapshot(
    string? Folder,
    string? File,
    long Epoch,
    EDebugPrimitivePreparationStatus Status,
    FontGlyphSet? Font,
    string Detail)
{
    public bool Matches(string folder, string file)
        => string.Equals(Folder, folder, StringComparison.Ordinal) &&
           string.Equals(File, file, StringComparison.Ordinal);
}
