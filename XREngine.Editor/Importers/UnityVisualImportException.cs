namespace XREngine.Scene.Importers;

/// <summary>
/// Raised when required visual dependencies prevent a usable Unity prefab conversion.
/// </summary>
public sealed class UnityVisualImportException : Exception
{
    public UnityVisualImportException(string message)
        : base(message)
    {
    }
}
