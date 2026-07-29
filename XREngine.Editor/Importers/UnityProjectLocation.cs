namespace XREngine.Scene.Importers;

/// <summary>
/// Result of locating the Unity project that owns an imported source asset.
/// </summary>
public sealed class UnityProjectLocation
{
    public required string ProjectRoot { get; init; }
    public required string AssetsRoot { get; init; }
    public string? UnityEditorVersion { get; init; }
    public bool HasProjectVersionFile { get; init; }
}
