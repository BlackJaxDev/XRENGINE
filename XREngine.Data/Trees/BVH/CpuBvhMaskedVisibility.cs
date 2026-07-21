using XREngine.Data.Geometry;

namespace XREngine.Data.Trees;

public readonly record struct CpuBvhFrustum(
    Frustum Frustum,
    int ParentViewIndex = -1,
    bool ParentContainsView = false);

public interface ICpuBvhMaskedVisitor<in T>
{
    void Visit(T item, ulong survivingViewMask);
}
