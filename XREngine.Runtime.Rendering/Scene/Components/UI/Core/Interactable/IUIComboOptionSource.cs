namespace XREngine.Rendering.UI;

/// <summary>Provides stable option IDs without creating a scene row per option.</summary>
public interface IUIComboOptionSource
{
    int Count { get; }
    ulong Revision { get; }
    UIComboOption GetOption(int index);
    int IndexOfId(ulong id);
}
