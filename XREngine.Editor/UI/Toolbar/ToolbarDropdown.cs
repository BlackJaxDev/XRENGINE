using XREngine.Rendering.UI;

namespace XREngine.Editor.UI.Toolbar;

public class ToolbarDropdown : ToolbarItemBase
{
    public delegate void DelOptionSelected(ToolbarDropdown dropdown, int index, string option);
    public event DelOptionSelected? OptionSelected;

    private bool _isOpen = false;
    private string[] _options = [];
    private int _selectedIndex = -1;
    private string _text = "";

    public bool IsOpen
    {
        get => _isOpen;
        set => SetField(ref _isOpen, value);
    }
    public string[] Options
    {
        get => _options;
        set => SetField(ref _options, value);
    }
    public int SelectedIndex
    {
        get => _selectedIndex;
        set => SetField(ref _selectedIndex, value);
    }
    public string Text
    {
        get => _text;
        set => SetField(ref _text, value);
    }

    public void SetOptionsWithEnum<T>(T value) where T : Enum
    {
        Text = value.ToString();
        Options = Enum.GetNames(typeof(T));
    }
    protected void OnOptionSelected(int index)
    {
        OptionSelected?.Invoke(this, index, Options[index]);
    }

    public override void OnInteracted(UIInteractableComponent component)
    {

    }

    public override void OnCancelInteraction(UIInteractableComponent component)
    {

    }
}
