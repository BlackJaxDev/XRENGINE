using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

internal sealed class UITextRebindingValidator : UITextInputComponent
{
    public Action? OnValidate { get; set; }

    public override bool PostValidateInput(string input)
    {
        Action? callback = OnValidate;
        OnValidate = null;
        callback?.Invoke();
        return true;
    }
}
