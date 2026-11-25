using XREngine.Components;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Timers;

namespace XREngine.Editor;

/// <summary>Displays bounded realization and cumulative UI work during the opt-in fixture.</summary>
internal sealed class VirtualCollectionFixtureCounterComponent : XRComponent
{
    private UIListTransform? _list;
    private VirtualCollectionFixtureProvider? _provider;
    private UITextComponent? _text;
    private GameTimer? _timer;

    public void Initialize(UIListTransform list, VirtualCollectionFixtureProvider provider, UITextComponent text)
    {
        _list = list;
        _provider = provider;
        _text = text;
        UpdateText();
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        _timer = new GameTimer(this);
        _timer.StartMultiFire((_, _) => UpdateText(), TimeSpan.FromSeconds(1), -1, null,
            ETickGroup.Late, (int)ETickOrder.Scene);
    }

    protected override void OnComponentDeactivated()
    {
        _timer?.Cancel();
        _timer = null;
        base.OnComponentDeactivated();
    }

    private void UpdateText()
    {
        if (_list is null || _provider is null || _text is null)
            return;
        int realized = _list.VirtualEndIndex - _list.VirtualFirstIndex;
        _text.Text = $"Items {_provider.Count:N0}  Visible {realized:N0}  Pooled {_provider.CreatedRowCount:N0}  Selected {_provider.SelectedId:N0}\n" +
            $"Measure {UIRuntimeCounters.Read(UIWorkCounter.MeasureVisits):N0}  Arrange {UIRuntimeCounters.Read(UIWorkCounter.ArrangeVisits):N0}  " +
            $"Hit {UIRuntimeCounters.Read(UIWorkCounter.HitQueries):N0}  Batch {UIRuntimeCounters.Read(UIWorkCounter.BatchEntriesCollected):N0}";
    }
}
