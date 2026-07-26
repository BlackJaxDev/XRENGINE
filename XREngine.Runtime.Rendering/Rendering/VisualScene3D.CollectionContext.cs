using System;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Data.Trees;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.Scene;

public partial class VisualScene3D
{
    [ThreadStatic]
    private static CollectionContextStack? t_collectionContextStack;

    private static readonly Action<RenderInfo3D> CollectRenderCommandsCallback = CollectRenderCommands;
    private static readonly OctreeNode<RenderInfo3D>.DelIntersectionTest IntersectionTestCallback = TestIntersection;

    private static bool TestIntersection(RenderInfo3D item, IVolume? cullingVolume, bool containsOnly)
    {
        CollectionContext context = GetActiveCollectionContext();
        RenderCommandCollection commands = context.Commands!;
        bool allowed = item.AllowRender(
            cullingVolume,
            commands,
            context.Camera,
            containsOnly,
            context.CollectMirrors);

        if (RenderDiagnosticsFlags.SkinCullRejectDiag)
        {
            item.DiagIntersectGen = context.Scene!._collectGen;
            item.DiagIntersectResult = allowed;
        }

        if (!allowed && context.ModelDiagnosticsActive)
        {
            ModelRenderDiagnostics.LogRejected(
                item,
                cullingVolume,
                commands,
                context.Camera,
                containsOnly,
                context.CollectMirrors);
        }

        return allowed;
    }

    private static void CollectRenderCommands(RenderInfo3D renderable)
    {
        CollectionContextStack stack = t_collectionContextStack
            ?? throw new InvalidOperationException("No active VisualScene3D collection context.");
        ref CollectionContext activeContext = ref stack.Current;
        activeContext.VisibleRenderables++;

        // Copy the references before invoking component code. A nested collection may grow the
        // context stack's backing array, so no ref into that array may survive the callback.
        CollectionContext context = activeContext;
        if (RenderDiagnosticsFlags.SkinCullRejectDiag)
            renderable.DiagCollectedGen = context.Scene!._collectGen;
        if (context.ModelDiagnosticsActive)
        {
            ModelRenderDiagnostics.LogVisibilityAccepted(
                renderable,
                context.Commands!,
                context.Camera,
                context.CollectMirrors);
        }

        renderable.CollectCommands(context.Commands!, context.Camera);
    }

    private static CollectionContext GetActiveCollectionContext()
    {
        CollectionContextStack stack = t_collectionContextStack
            ?? throw new InvalidOperationException("No active VisualScene3D collection context.");
        return stack.Current;
    }

    private static string GetCpuSceneCullingStructureName(ECpuSceneCullingStructure structure)
        => structure switch
        {
            ECpuSceneCullingStructure.Octree => nameof(ECpuSceneCullingStructure.Octree),
            ECpuSceneCullingStructure.Bvh => nameof(ECpuSceneCullingStructure.Bvh),
            _ => "Unknown",
        };

    private struct CollectionContext
    {
        public VisualScene3D? Scene;
        public RenderCommandCollection? Commands;
        public IRuntimeCullingCamera? Camera;
        public bool CollectMirrors;
        public bool ModelDiagnosticsActive;
        public int VisibleRenderables;
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
                    throw new InvalidOperationException("No active VisualScene3D collection context.");

                return ref _contexts[_depth - 1];
            }
        }

        public void Push(
            VisualScene3D scene,
            RenderCommandCollection commands,
            IRuntimeCullingCamera? camera,
            bool collectMirrors,
            bool modelDiagnosticsActive)
        {
            if (_depth == _contexts.Length)
                Array.Resize(ref _contexts, _contexts.Length * 2);

            _contexts[_depth++] = new CollectionContext
            {
                Scene = scene,
                Commands = commands,
                Camera = camera,
                CollectMirrors = collectMirrors,
                ModelDiagnosticsActive = modelDiagnosticsActive,
            };
        }

        public void Pop()
        {
            if (_depth == 0)
                throw new InvalidOperationException("No active VisualScene3D collection context.");

            _contexts[--_depth] = default;
        }
    }
}
