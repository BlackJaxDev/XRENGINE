using XREngine.Input.Devices;

namespace XREngine.Rendering;

/// <summary>One native input event, ordered relative to other events in the same window.</summary>
public readonly record struct WindowInputEvent(
    WindowInputEventKind Kind,
    EKey Key,
    EMouseButton MouseButton,
    bool IsDown,
    char Character,
    float X,
    float Y);
