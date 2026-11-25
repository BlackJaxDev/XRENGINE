using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Stores transient hierarchy expansion and scroll for one live document.</summary>
internal sealed record NativeHierarchyPanelViewValues(
    RuntimeWorld World,
    XRWorld? Document,
    ObjectConstructionIdentity? DocumentIdentity,
    Guid[] CollapsedNodes,
    Guid[] CollapsedSections,
    float ScrollOffset,
    bool ShowEditorScene)
{
    internal bool HasCurrentDocument => !World.IsDisposing &&
        ReferenceEquals(World.TargetWorld, Document) &&
        (Document is null || DocumentIdentity?.IsCurrent == true &&
            !Document.IsDestroyed && !Document.IsDestroyQueued) &&
        float.IsFinite(ScrollOffset) && ScrollOffset >= 0;
}
