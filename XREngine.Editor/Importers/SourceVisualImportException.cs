namespace XREngine.Scene.Importers;

/// <summary>
/// Raised when required visual dependencies prevent a usable Unity prefab conversion.
/// </summary>
public sealed class SourceVisualImportException : Exception
{
    public SourceVisualImportException(string message)
        : base(message)
    {
    }
}
