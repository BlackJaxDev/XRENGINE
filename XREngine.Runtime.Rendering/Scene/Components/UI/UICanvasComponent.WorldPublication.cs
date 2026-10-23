using XREngine.Rendering.UI;

namespace XREngine.Components;

public partial class UICanvasComponent
{
    internal void RenderPublishedWorldCommand(UIPublishedWorldMeshCommand command,
        Action draw)
    {
        UIPublication publication = command.Publication;
        publication.Retain();
        try { draw(); }
        finally { publication.Release(); }
    }
}
