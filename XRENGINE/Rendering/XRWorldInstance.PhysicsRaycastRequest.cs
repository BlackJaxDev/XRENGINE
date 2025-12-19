using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Scene;

namespace XREngine.Rendering
{
    public partial class XRWorldInstance
    {
        private sealed class PhysicsRaycastRequest
        {
            public Segment Segment;
            public LayerMask LayerMask;
            public AbstractPhysicsScene.IAbstractQueryFilter? Filter;
            public SortedDictionary<float, List<(XRComponent? item, object? data)>> Results = null!;
            public Action<SortedDictionary<float, List<(XRComponent? item, object? data)>>>? FinishedCallback;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Set(
                Segment segment,
                LayerMask layerMask,
                AbstractPhysicsScene.IAbstractQueryFilter? filter,
                SortedDictionary<float, List<(XRComponent? item, object? data)>> results,
                Action<SortedDictionary<float, List<(XRComponent? item, object? data)>>>? finishedCallback)
            {
                Segment = segment;
                LayerMask = layerMask;
                Filter = filter;
                Results = results;
                FinishedCallback = finishedCallback;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Clear()
            {
                Results = null!;
                Filter = null;
                FinishedCallback = null;
                Segment = default;
                LayerMask = default;
            }
        }
    }
}
