namespace XREngine.Rendering.UI.State;

/// <summary>Names the UI work made stale by a change.</summary>
[Flags]
public enum UIChangeDomain
{
    None = 0,
    Measure = 1,
    Arrange = 2,
    Transform = 4,
    Visual = 8,
    Clip = 16,
    HitTest = 32,
    Topology = 64,
    Text = 128,
    All = 255,
}
