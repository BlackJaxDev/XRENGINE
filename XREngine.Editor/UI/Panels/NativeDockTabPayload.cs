using XREngine.Editor.UI;

namespace XREngine.Editor;

/// <summary>Retains the exact source and eligible native windows of one dock tab drag.</summary>
internal sealed class NativeDockTabPayload
{
    internal const string DragType = "Editor:dock-tab";

    internal static NativeDockTabPayload? Read(EditorDragDropUtility.DragPayload payload)
        => payload.TypeId == DragType ? payload.Data as NativeDockTabPayload : null;

    internal NativeDockTabPayload(NativeDockTreeHost source, NativeDockNode sourceStack,
        ulong renderRevision, ulong dockRevision, string panel,
        NativeDockDragWindowAdmission sourceWindow,
        NativeDockDragWindowAdmission[] windows)
    {
        Source = source;
        SourceStack = sourceStack;
        RenderRevision = renderRevision;
        DockRevision = dockRevision;
        Panel = panel;
        SourceWindow = sourceWindow;
        Windows = windows;
    }

    internal NativeDockTreeHost Source { get; }
    internal NativeDockNode SourceStack { get; }
    internal ulong RenderRevision { get; }
    internal ulong DockRevision { get; }
    internal string Panel { get; }
    internal NativeDockDragWindowAdmission SourceWindow { get; }
    private NativeDockDragWindowAdmission[] Windows { get; }

    internal bool IsSourceCurrent => SourceWindow.IsCurrent && Source.IsCurrentDockDrag(this);

    internal bool TryGetWindow(nint handle, out NativeDockDragWindowAdmission admission)
    {
        admission = default;
        if (handle == 0)
            return false;
        for (int index = 0; index < Windows.Length; index++)
        {
            NativeDockDragWindowAdmission candidate = Windows[index];
            if (candidate.WindowHandle != handle)
                continue;
            if (admission.WindowHandle != 0)
                return false;
            admission = candidate;
        }
        return admission.WindowHandle != 0 && admission.IsCurrent;
    }
}
