using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Core;
using XREngine.Rendering.Models;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Importers;

internal static partial class SerializedSceneImporter
{
    /// <summary>
    /// Moves a synthetic root basis into its children without changing their world transforms.
    /// The imported geometry and inverse bind matrices already contain the backend conversion.
    /// </summary>
    private static bool TryRedistributeSyntheticRootBasis(
        SceneNode wrapper,
        SceneNode root,
        out string reason)
    {
        if (root.Transform is not Transform rootTransform || root.Components.Count != 0 ||
            !ModelMatricesMatch(wrapper.Transform.LocalMatrix, Matrix4x4.Identity))
        {
            reason = "The synthetic root must have a standard transform, no components, and an identity wrapper.";
            return false;
        }

        Matrix4x4 basis = rootTransform.LocalMatrix;
        if (!Matrix4x4.Invert(basis, out Matrix4x4 inverseBasis) ||
            !ModelMatricesMatch(basis * inverseBasis, Matrix4x4.Identity))
        {
            reason = "The synthetic root basis is not finite and invertible.";
            return false;
        }

        if (HasSyntheticRootBindings(root, rootTransform))
        {
            reason = "A skin palette or explicit culling bound uses the synthetic root basis.";
            return false;
        }

        var childMatrices = new List<(Transform Transform, Matrix4x4 Matrix)>(rootTransform.Children.Count);
        foreach (TransformBase child in rootTransform.Children)
        {
            Matrix4x4 target = child.LocalMatrix * basis;
            if (child is not Transform transform ||
                !Matrix4x4.Decompose(target, out Vector3 scale, out Quaternion rotation, out Vector3 translation))
            {
                reason = "A direct child cannot represent the redistributed root basis.";
                return false;
            }

            Matrix4x4 reconstructed = Matrix4x4.CreateScale(scale) *
                Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
            if (!ModelMatricesMatch(target, reconstructed) ||
                !Matrix4x4.Invert(reconstructed, out Matrix4x4 inverseChild) ||
                !ModelMatricesMatch(reconstructed * inverseChild, Matrix4x4.Identity))
            {
                reason = "A redistributed child matrix is sheared, singular, or not finite.";
                return false;
            }

            childMatrices.Add((transform, target));
        }

        // Validate every child before changing the hierarchy. No vertex or bind palette
        // conversion is needed because each child's matrix product remains unchanged.
        foreach ((Transform child, Matrix4x4 matrix) in childMatrices)
            child.DeriveLocalMatrix(matrix);
        rootTransform.DeriveLocalMatrix(Matrix4x4.Identity);
        rootTransform.RecalculateMatrixHierarchy(
            forceWorldRecalc: true,
            setRenderMatrixNow: true,
            childRecalcType: ELoopType.Sequential).GetAwaiter().GetResult();
        SaveImportedModelBindHierarchy(rootTransform);
        reason = string.Empty;
        return true;
    }

    private static void SaveImportedModelBindHierarchy(TransformBase transform)
    {
        transform.SaveBindState();
        foreach (TransformBase child in transform.Children)
            SaveImportedModelBindHierarchy(child);
    }

    private static bool HasSyntheticRootBindings(SceneNode node, TransformBase syntheticRoot)
    {
        foreach (ModelComponent component in node.Components.OfType<ModelComponent>())
        {
            if (component.Model is not Model model)
                continue;
            foreach (SubMesh subMesh in model.Meshes)
            {
                if (subMesh.CullingBounds.HasValue &&
                    ReferenceEquals(subMesh.RootBone ?? subMesh.RootTransform, syntheticRoot))
                    return true;
                foreach (SubMeshLOD lod in subMesh.LODs)
                {
                    if (lod.Mesh is not { } mesh)
                        continue;
                    foreach (var bone in mesh.UtilizedBones)
                    {
                        if (ReferenceEquals(bone.tfm, syntheticRoot))
                            return true;
                    }
                }
            }
        }

        foreach (TransformBase child in node.Transform.Children)
        {
            if (child.SceneNode is { } childNode && HasSyntheticRootBindings(childNode, syntheticRoot))
                return true;
        }
        return false;
    }

    private static bool ModelMatricesMatch(Matrix4x4 left, Matrix4x4 right)
    {
        ReadOnlySpan<float> leftValues =
        [
            left.M11, left.M12, left.M13, left.M14,
            left.M21, left.M22, left.M23, left.M24,
            left.M31, left.M32, left.M33, left.M34,
            left.M41, left.M42, left.M43, left.M44,
        ];
        ReadOnlySpan<float> rightValues =
        [
            right.M11, right.M12, right.M13, right.M14,
            right.M21, right.M22, right.M23, right.M24,
            right.M31, right.M32, right.M33, right.M34,
            right.M41, right.M42, right.M43, right.M44,
        ];
        for (int i = 0; i < leftValues.Length; i++)
        {
            float expected = leftValues[i];
            float actual = rightValues[i];
            float tolerance = 1.0e-5f * MathF.Max(1.0f, MathF.Abs(expected));
            if (!float.IsFinite(expected) || !float.IsFinite(actual) || MathF.Abs(expected - actual) > tolerance)
                return false;
        }

        return true;
    }
}
