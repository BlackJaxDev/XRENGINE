namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Carries failure of a required render command through nested command containers.
/// </summary>
public sealed class RequiredRenderCommandException(string message, Exception innerException)
    : Exception(message, innerException);
