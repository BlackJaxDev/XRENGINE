using System.Numerics;
using XREngine.Rendering.Info;

namespace XREngine.Scene;

public partial class VisualScene2D
{
    /// <summary>Queries the published UI tree under the same lock as render collection and tree mutation.</summary>
    public bool TryFindInputIntersections(Vector2 position, Span<RenderInfo2D?> destination, out int count)
    {
        lock (_renderablesLock)
            return RenderTree.TryFindAllIntersecting(position, destination, out count);
    }
}
