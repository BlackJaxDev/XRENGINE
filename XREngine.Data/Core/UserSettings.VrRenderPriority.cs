using System.ComponentModel;

namespace XREngine;

public partial class UserSettings
{
    private EVrRenderPriority _vrRenderPriority = EVrRenderPriority.Vr;

    /// <summary>
    /// Selects which output gets render-thread time first while VR is active.
    /// </summary>
    [Category("Performance")]
    [Description("Which output gets render time first while VR is active. Vr holds desktop frames that do not fit before the next headset frame. Desktop renders every due desktop frame, also when that makes headset frames late.")]
    public EVrRenderPriority VrRenderPriority
    {
        get => _vrRenderPriority;
        set { if (SetField(ref _vrRenderPriority, value)) MarkDirty(); }
    }
}
