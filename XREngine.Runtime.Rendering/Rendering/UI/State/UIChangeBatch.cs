namespace XREngine.Rendering.UI.State;

/// <summary>Describes a journal drain or the pending work when a drain cannot finish.</summary>
public readonly record struct UIChangeBatch(
    long Revision,
    int Count,
    UIChangeDomain Domains,
    bool RequiresFullResync);
