namespace XREngine.Rendering;

/// <summary>
/// Minimal renderer backend identity shared across the runtime rendering assembly and the concrete host.
/// </summary>
public enum RuntimeGraphicsApiKind
{
    Unknown = 0,
    OpenGL,
    Vulkan
}
