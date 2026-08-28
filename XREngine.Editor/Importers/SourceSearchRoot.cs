namespace XREngine.Scene.Importers;

/// <summary>
/// Physical Unity search root and its portable manifest prefix.
/// </summary>
internal sealed class SourceSearchRoot
{
    public required string PhysicalPath { get; init; }
    public required string PortablePrefix { get; init; }
    public required int Precedence { get; init; }
}
