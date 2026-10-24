using XREngine.Rendering.UI;

namespace XREngine.Editor;

internal interface IProfilerSettingBinding : IUIValueBinding<string>
{
    bool IsCurrent { get; }
    void Refresh();
}
