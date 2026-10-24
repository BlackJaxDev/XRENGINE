namespace XREngine.Rendering.UI.State;

/// <summary>States whether a publication reader received a complete change range.</summary>
public enum UICommittedChangeReadStatus
{
    Complete,
    Busy,
    NeedCapacity,
    FullResync,
    InvalidCursor,
}
