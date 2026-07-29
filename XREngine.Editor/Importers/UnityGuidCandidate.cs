namespace XREngine.Scene.Importers;

/// <summary>
/// One deterministic candidate for a GUID discovered in a Unity project or package.
/// </summary>
public sealed class UnityGuidCandidate
{
    public required string Guid { get; init; }
    public required string AssetPath { get; init; }
    public required string MetaPath { get; init; }
    public required string PortablePath { get; init; }
    public required int Precedence { get; init; }
}
