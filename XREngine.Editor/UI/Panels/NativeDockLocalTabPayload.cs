using XREngine.Data.Core;
using XREngine.Editor.UI;

namespace XREngine.Editor;

/// <summary>Retains one same-canvas tab drag when native pointer capture is unavailable.</summary>
internal sealed record NativeDockLocalTabPayload(
    NativeDockTreeHost Source,
    NativeDockNode SourceStack,
    ulong RenderRevision,
    ulong DockRevision,
    string Panel,
    ObjectConstructionIdentity RootIdentity)
{
    internal const string DragType = "Editor:dock-local-tab";

    internal static NativeDockLocalTabPayload? Read(EditorDragDropUtility.DragPayload payload)
        => payload.TypeId == DragType ? payload.Data as NativeDockLocalTabPayload : null;
}
