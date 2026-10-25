using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Editor;

internal sealed class HierarchyBatchMatrixTransformProbe : TransformBase
{
    private Matrix4x4 _matrix;

    internal HierarchyBatchMatrixTransformProbe(Matrix4x4 matrix) : base(null)
    {
        _matrix = matrix;
        MarkLocalModified();
    }

    internal void SetMatrix(Matrix4x4 matrix)
    {
        if (SetField(ref _matrix, matrix))
            MarkLocalModified();
    }

    protected override Matrix4x4 CreateLocalMatrix() => _matrix;
}
