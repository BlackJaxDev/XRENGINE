using System.Reflection;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Editor;

/// <summary>Supplies ordinary and flags enum properties to retained editor tests.</summary>
public sealed class InspectorEnumTestTarget
{
    private EVisibility _first = EVisibility.Visible;
    public Action? OnRead { get; set; }
    public EVisibility First { get { OnRead?.Invoke(); return _first; } set => _first = value; }
    public EVisibility Second { get; set; } = EVisibility.Collapsed;
    public BindingFlags Flags { get; set; } = BindingFlags.Public | BindingFlags.Instance;
}
