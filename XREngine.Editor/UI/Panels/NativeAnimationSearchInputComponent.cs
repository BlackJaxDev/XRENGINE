using XREngine.Rendering.UI;

namespace XREngine.Editor;

/// <summary>Offers the Animation panel a pre-activation point for one retained search draft.</summary>
internal sealed class NativeAnimationSearchInputComponent : UITextInputComponent
{
    private AnimationPanel? _panel;
    internal AnimationPanel? Panel
    {
        get => _panel;
        set => SetField(ref _panel, value, publishNotifications: false);
    }

    protected override void PublishActivationText(UITextComponent display, string value)
    {
        if (Panel?.SearchReceiveReceipt is { } receipt)
            receipt.WriteActivationDisplay(this, display, value);
        else
            base.PublishActivationText(display, value);
    }

    protected override void OnComponentActivated()
    {
        Panel?.ResumeSearchDraft(this, beforePanelActivation: true);
        base.OnComponentActivated();
    }
}
