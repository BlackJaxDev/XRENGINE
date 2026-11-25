using XREngine.Components;

namespace XREngine.Editor.UI;

/// <summary>Refreshes a retained list detail view after a source change.</summary>
public sealed class InspectorListDetailRefreshComponent : XRComponent
{
    internal bool IsRetired { get; private set; }
    public Action? Refresh { get; set; }
    public Action? Release { get; set; }

    public void Retire()
    {
        IsRetired = true;
        Action? release = Release;
        Release = null;
        Refresh = null;
        release?.Invoke();
    }

    protected override void OnDestroying()
    {
        try { Retire(); }
        catch (Exception error) { Debug.LogException(error, "Could not retire the list detail view."); }
        base.OnDestroying();
    }
}
