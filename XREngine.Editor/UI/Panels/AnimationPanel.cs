using XREngine.Components.Animation;
using XREngine.Components;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Shows playback state for the selected scene node's clip.</summary>
public class AnimationPanel : NativeVirtualTextPanel
{
    private readonly List<(ulong Id, string Text)> _rows = [];
    private DateTime _lastRefresh;

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        Selection.SelectionChanged += SelectionChanged;
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick);
    }

    protected override void OnComponentDeactivated()
    {
        Selection.SelectionChanged -= SelectionChanged;
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick);
        base.OnComponentDeactivated();
    }

    protected override void CreateCommands(SceneNode commands)
    {
        AddCommand("Play", () => { SelectedClip()?.Play(); RefreshRows(); });
        AddCommand("Pause", () => { SelectedClip()?.Pause(); RefreshRows(); });
        AddCommand("Resume", () => { SelectedClip()?.Resume(); RefreshRows(); });
        AddCommand("Stop", () => { SelectedClip()?.StopPlayback(); RefreshRows(); });
        AddCommand("Start", () => Seek(0));
        AddCommand("-0.1s", () => Step(-0.1f));
        AddCommand("+0.1s", () => Step(0.1f));
        AddCommand("End", () => Seek(SelectedClip()?.Animation?.LengthInSeconds ?? 0));
    }

    private void Step(float seconds)
    {
        AnimationClipComponent? component = SelectedClip();
        if (component is not null)
            Seek(component.PlaybackTime + seconds);
    }

    private void Seek(float seconds)
    {
        AnimationClipComponent? component = SelectedClip();
        if (component?.Animation is null)
            return;
        component.EvaluateAtTime(Math.Clamp(seconds, 0, component.Animation.LengthInSeconds), dispatchEvents: false);
        RefreshRows();
    }

    private static AnimationClipComponent? SelectedClip()
        => Selection.SceneNode?.GetComponent<AnimationClipComponent>();

    private void SelectionChanged(SceneNode[] _) => RefreshRows();
    private void RefreshTick()
    {
        DateTime now = DateTime.UtcNow;
        TimeSpan interval = SelectedClip()?.IsPlaying == true ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(1);
        if (now - _lastRefresh < interval)
            return;
        _lastRefresh = now;
        RefreshRows();
    }

    protected override void RefreshRows()
    {
        _rows.Clear();
        AnimationClipComponent? component = SelectedClip();
        if (component is null)
            _rows.Add((1, "Select a scene node with an AnimationClipComponent."));
        else
        {
            _rows.Add((1, "Clip: " + (component.Animation?.Name ?? "<none>")));
            _rows.Add((2, $"Time: {component.PlaybackTime:F3} / {component.Animation?.LengthInSeconds ?? 0:F3} s"));
            _rows.Add((3, "State: " + (component.IsPaused ? "Paused" : component.IsPlaying ? "Playing" : "Stopped")));
            if (component.PlaybackCapabilityDiagnostic.Length > 0)
                _rows.Add((4, component.PlaybackCapabilityDiagnostic));
        }
        SetRows(_rows);
    }
}
