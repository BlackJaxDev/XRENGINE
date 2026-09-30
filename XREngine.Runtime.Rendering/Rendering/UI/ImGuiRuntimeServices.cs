namespace XREngine.Rendering.UI;

/// <summary>Stores the explicitly installed Dear ImGui context capability.</summary>
public static class ImGuiRuntimeServices
{
    private static IRuntimeImGuiServices? _current;

    public static IRuntimeImGuiServices? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    public static IRuntimeImGuiServices Required => Current ??
        throw new NotSupportedException("Dear ImGui services are not installed. Install the Dear ImGui backend in the host.");
}
