using System;
using XREngine.Data.Core;

namespace XREngine.Editor;

/// <summary>Proves that a captured Animation editing view is still current.</summary>
internal readonly record struct NativeAnimationPanelViewReceipt(
    AnimationPanel Panel,
    ObjectConstructionIdentity PanelIdentity,
    NativeAnimationPanelViewValues Values,
    ulong AuthoringEpoch,
    ulong SourceRevision,
    ulong PlotViewRevision,
    ulong StateIntentRevision,
    Func<bool>? ToolbarCurrent = null)
{
    internal bool IsCurrent => PanelIdentity.IsCurrent && Panel.IsViewCurrent(this) &&
        (ToolbarCurrent?.Invoke() ?? true);
}
