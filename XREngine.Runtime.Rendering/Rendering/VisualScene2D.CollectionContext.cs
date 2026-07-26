using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.Scene;

public partial class VisualScene2D
{
    [ThreadStatic]
    private static CollectionContextStack? t_collectionContextStack;

    private static readonly Action<RenderInfo2D> CollectRenderCommandsCallback = CollectRenderCommands;
    private static readonly Data.Trees.QuadtreeNode<RenderInfo2D>.DelIntersectionTest IntersectionTestCallback =
        TestIntersection;

    private static bool TestIntersection(
        RenderInfo2D item,
        BoundingRectangleF cullingVolume,
        bool containsOnly)
    {
        if (item.CullingVolume is null)
            return false;

        EContainment containment = cullingVolume.ContainmentOf(item.CullingVolume.Value);
        return containsOnly
            ? containment == EContainment.Contains
            : containment != EContainment.Disjoint;
    }

    private static void CollectRenderCommands(RenderInfo2D item)
    {
        CollectionContextStack stack = t_collectionContextStack
            ?? throw new InvalidOperationException("No active VisualScene2D collection context.");
        ref CollectionContext context = ref stack.Current;
        context.WalkedCount++;
        item.CollectCommands(context.Commands!, context.Camera);
    }

    private struct CollectionContext
    {
        public RenderCommandCollection? Commands;
        public IRuntimeCullingCamera? Camera;
        public int WalkedCount;
    }

    private sealed class CollectionContextStack
    {
        private CollectionContext[] _contexts = new CollectionContext[2];
        private int _depth;

        public ref CollectionContext Current
        {
            get
            {
                if (_depth == 0)
                    throw new InvalidOperationException("No active VisualScene2D collection context.");

                return ref _contexts[_depth - 1];
            }
        }

        public void Push(RenderCommandCollection commands, IRuntimeCullingCamera? camera)
        {
            if (_depth == _contexts.Length)
                Array.Resize(ref _contexts, _contexts.Length * 2);

            _contexts[_depth++] = new CollectionContext
            {
                Commands = commands,
                Camera = camera,
            };
        }

        public void Pop()
        {
            if (_depth == 0)
                throw new InvalidOperationException("No active VisualScene2D collection context.");

            _contexts[--_depth] = default;
        }
    }
}
