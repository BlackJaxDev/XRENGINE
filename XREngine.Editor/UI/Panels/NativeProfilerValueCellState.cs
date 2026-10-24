using System.Numerics;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Reuses one value cell for labels and the numeric profiler settings.</summary>
internal sealed class NativeProfilerValueCellState
{
    private readonly Func<ENativeProfilerSetting, IProfilerSettingBinding> _createBinding;
    private readonly UIBoundableTransform _labelBounds;
    private readonly UITextComponent _label;
    private readonly UIBoundableTransform _integerBounds;
    private readonly UIIntInputComponent _integer;
    private readonly UIBoundableTransform _floatBounds;
    private readonly UIFloatInputComponent _float;
    private IProfilerSettingBinding? _binding;
    private ENativeProfilerSetting? _setting;

    public NativeProfilerValueCellState(SceneNode cell,
        Func<ENativeProfilerSetting, IProfilerSettingBinding> createBinding)
    {
        _createBinding = createBinding;
        SceneNode labelNode = new(cell) { Name = "Value label" };
        _labelBounds = labelNode.SetTransform<UIBoundableTransform>();
        _labelBounds.MaxAnchor = Vector2.One;
        _label = labelNode.AddComponent<UITextComponent>()!;
        _label.FontSize = 12;
        _label.VerticalAlignment = EVerticalAlignment.Center;
        _label.ClipToBounds = true;

        SceneNode integerNode = new(cell) { Name = "Integer setting" };
        _integerBounds = integerNode.SetTransform<UIBoundableTransform>();
        _integerBounds.MaxAnchor = Vector2.One;
        UITextComponent integerText = integerNode.AddComponent<UITextComponent>()!;
        integerText.FontSize = 12;
        integerText.VerticalAlignment = EVerticalAlignment.Center;
        _integer = integerNode.AddComponent<UIIntInputComponent>()!;
        _integer.Minimum = 1;
        _integer.Maximum = 10_000;
        _integer.EnableAltScrub = false;

        SceneNode floatNode = new(cell) { Name = "Float setting" };
        _floatBounds = floatNode.SetTransform<UIBoundableTransform>();
        _floatBounds.MaxAnchor = Vector2.One;
        UITextComponent floatText = floatNode.AddComponent<UITextComponent>()!;
        floatText.FontSize = 12;
        floatText.VerticalAlignment = EVerticalAlignment.Center;
        _float = floatNode.AddComponent<UIFloatInputComponent>()!;
        _float.Minimum = 0f;
        _float.Step = 0.1f;
        _float.EnableAltScrub = false;
        Unbind();
    }

    public void Bind(NativeProfilerDisplayRow row)
    {
        ENativeProfilerSetting? setting = row.SettingIndex >= 0 && row.SettingIndex <= (int)ENativeProfilerSetting.RootMaximum
            ? (ENativeProfilerSetting)row.SettingIndex : null;
        if (setting is >= ENativeProfilerSetting.SnapshotInterval and <= ENativeProfilerSetting.GraphSmoothing or
            ENativeProfilerSetting.RootMinimum or ENativeProfilerSetting.RootMaximum)
        {
            bool useFloat = setting is ENativeProfilerSetting.DropThreshold or
                ENativeProfilerSetting.DebugOutputMinimum or ENativeProfilerSetting.FpsMinimumPrevious or
                ENativeProfilerSetting.RenderStallThreshold or ENativeProfilerSetting.SpikeMinimumDrop or
                ENativeProfilerSetting.GraphSmoothing or ENativeProfilerSetting.RootMinimum or
                ENativeProfilerSetting.RootMaximum;
            if (_setting != setting || _binding?.IsCurrent != true)
            {
                ReleaseBinding();
                _binding = _createBinding(setting.Value);
                _setting = setting;
                if (useFloat)
                    _float.Binding = _binding;
                else
                    _integer.Binding = _binding;
            }
            _integer.Minimum = setting switch
            {
                ENativeProfilerSetting.HistoryCapacity or ENativeProfilerSetting.ProducerBufferCapacity => 2,
                ENativeProfilerSetting.GraphSamples => 30,
                _ => 1,
            };
            _integer.Maximum = setting switch
            {
                ENativeProfilerSetting.GraphSamples => 720,
                ENativeProfilerSetting.StatsThreadInterval => 1_000,
                ENativeProfilerSetting.SnapshotInterval => 5_000,
                ENativeProfilerSetting.HistoryCapacity or ENativeProfilerSetting.FpsBaselineWindow => 10_000,
                ENativeProfilerSetting.MaxOverflowPerCycle => 1_000_000,
                ENativeProfilerSetting.MaxOverflowQueueSize => 5_000_000,
                ENativeProfilerSetting.ProducerBufferCapacity => 1 << 20,
                _ => 10_000,
            };
            _float.Maximum = setting switch
            {
                ENativeProfilerSetting.GraphSmoothing => 0.95f,
                ENativeProfilerSetting.RootMinimum or ENativeProfilerSetting.RootMaximum => 1000f,
                _ => null,
            };
            _labelBounds.Visibility = EVisibility.Collapsed;
            _integerBounds.Visibility = useFloat ? EVisibility.Collapsed : EVisibility.Visible;
            _floatBounds.Visibility = useFloat ? EVisibility.Visible : EVisibility.Collapsed;
            UITextInputComponent input = useFloat ? _float : _integer;
            if (!input.IsFocused)
                _binding?.Refresh();
            return;
        }
        ReleaseBinding();
        _label.Text = row.Total;
        _labelBounds.Visibility = EVisibility.Visible;
        _integerBounds.Visibility = EVisibility.Collapsed;
        _floatBounds.Visibility = EVisibility.Collapsed;
    }

    public void Unbind()
    {
        ReleaseBinding();
        _label.Text = string.Empty;
        _labelBounds.Visibility = EVisibility.Visible;
        _integerBounds.Visibility = EVisibility.Collapsed;
        _floatBounds.Visibility = EVisibility.Collapsed;
    }

    private void ReleaseBinding()
    {
        _setting = null;
        _binding = null;
        _integer.Binding = null;
        _float.Binding = null;
    }
}
