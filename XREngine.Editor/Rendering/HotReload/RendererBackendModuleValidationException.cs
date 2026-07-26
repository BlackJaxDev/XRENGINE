namespace XREngine.Editor.HotReload;

public sealed class RendererBackendModuleValidationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
