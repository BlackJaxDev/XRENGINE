using System.Numerics;
using XREngine.Animation;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Exact four-key local state used during a clone forest's property replay.</summary>
internal readonly record struct HierarchyBatchLocalValues(Matrix4x4 Matrix, Vector3 Translation,
    Quaternion Rotation, Vector3 Scale, ETransformOrder Order, bool IsLocal)
{
    internal static HierarchyBatchLocalValues Capture(TransformBase transform)
        => transform is Transform local
            ? new(default, local.Translation, local.Rotation, local.Scale, local.Order, true)
            : new(transform.LocalMatrix, default, default, default, default, false);

    internal bool Matches(TransformBase transform)
        => IsLocal && transform is Transform local
            ? local.Translation == Translation && local.Rotation == Rotation &&
              local.Scale == Scale && local.Order == Order
            : !IsLocal && transform is not Transform && transform.LocalMatrix == Matrix;

    internal static bool IsKey(string name) => name is
        nameof(Transform.Translation) or nameof(Transform.Rotation) or
        nameof(Transform.Scale) or nameof(Transform.Order);

    internal bool SameProperties(HierarchyBatchLocalValues other)
        => IsLocal == other.IsLocal && (IsLocal
            ? Translation == other.Translation && Rotation == other.Rotation &&
              Scale == other.Scale && Order == other.Order
            : Matrix == other.Matrix);

    internal HierarchyBatchLocalValues With(string name, HierarchyBatchLocalValues target) => name switch
    {
        nameof(Transform.Translation) => this with { Translation = target.Translation },
        nameof(Transform.Rotation) => this with { Rotation = target.Rotation },
        nameof(Transform.Scale) => this with { Scale = target.Scale },
        nameof(Transform.Order) => this with { Order = target.Order },
        _ => throw new InvalidOperationException("The property is not a local clone transform key.")
    };
}
