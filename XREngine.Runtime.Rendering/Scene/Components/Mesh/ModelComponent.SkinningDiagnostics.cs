using System.Numerics;
using System.Text;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Scene.Mesh;

public partial class ModelComponent
{
    /// <summary>
    /// Describes the live skinning inputs owned by this component. Intended for editor/MCP
    /// diagnostics when imported meshes render differently from their CPU bind-pose result.
    /// </summary>
    internal string GetRuntimeSkinningDiagnosticSummary()
    {
        StringBuilder summary = new();
        TransformBase hierarchyRoot = SceneNode.Transform;
        while (hierarchyRoot.Parent is TransformBase parent)
            hierarchyRoot = parent;

        RenderableMesh[] renderableMeshes = [.. Meshes];
        summary.Append("component=")
            .Append(Name ?? SceneNode.Name ?? "<unnamed>")
            .Append(" renderables=")
            .Append(renderableMeshes.Length);

        for (int renderableIndex = 0; renderableIndex < renderableMeshes.Length; ++renderableIndex)
        {
            RenderableMesh.RenderableLOD[] lods = renderableMeshes[renderableIndex].GetLodSnapshot();
            for (int lodIndex = 0; lodIndex < lods.Length; ++lodIndex)
            {
                XRMeshRenderer renderer = lods[lodIndex].Renderer;
                XRMesh? mesh = renderer.Mesh;
                if (mesh is null)
                    continue;

                int detachedBoneCount = 0;
                float maximumExpectedTranslation = 0.0f;
                float maximumBufferedTranslation = 0.0f;
                float maximumPaletteDifference = 0.0f;
                string? maximumExpectedBone = null;
                string? maximumBufferedBone = null;
                string? maximumDifferenceBone = null;

                for (int boneIndex = 0; boneIndex < mesh.UtilizedBones.Length; ++boneIndex)
                {
                    (TransformBase bone, Matrix4x4 inverseBind) = mesh.UtilizedBones[boneIndex];
                    if (!IsSelfOrDescendantOf(hierarchyRoot, bone))
                        ++detachedBoneCount;

                    Matrix4x4 current = GetCurrentBoneMatrix(bone);
                    Matrix4x4 expected = (mesh.BindRootMatrix ?? Matrix4x4.Identity) * inverseBind * current;
                    float expectedTranslation = expected.Translation.Length();
                    if (expectedTranslation > maximumExpectedTranslation)
                    {
                        maximumExpectedTranslation = expectedTranslation;
                        maximumExpectedBone = bone.SceneNode?.Name ?? bone.Name;
                    }

                    XRDataBuffer? paletteBuffer = renderer.SkinPaletteBuffer;
                    SkinPaletteMatrix? buffered = paletteBuffer?.Get<SkinPaletteMatrix>(
                        checked((uint)(boneIndex + 1) * paletteBuffer.ElementSize));
                    if (!buffered.HasValue)
                        continue;

                    Matrix4x4 bufferedMatrix = buffered.Value.ToRowVectorMatrix();
                    float bufferedTranslation = bufferedMatrix.Translation.Length();
                    if (bufferedTranslation > maximumBufferedTranslation)
                    {
                        maximumBufferedTranslation = bufferedTranslation;
                        maximumBufferedBone = bone.SceneNode?.Name ?? bone.Name;
                    }

                    float paletteDifference = MaximumElementDifference(expected, bufferedMatrix);
                    if (paletteDifference > maximumPaletteDifference)
                    {
                        maximumPaletteDifference = paletteDifference;
                        maximumDifferenceBone = bone.SceneNode?.Name ?? bone.Name;
                    }
                }

                summary.AppendLine()
                    .Append("renderable=")
                    .Append(renderableIndex)
                    .Append(" lod=")
                    .Append(lodIndex)
                    .Append(" mesh=")
                    .Append(mesh.Name ?? "<unnamed>")
                    .Append(" vertices=")
                    .Append(mesh.VertexCount)
                    .Append(" bones=")
                    .Append(mesh.UtilizedBones.Length)
                    .Append(" detached=")
                    .Append(detachedBoneCount)
                    .Append(" convention=")
                    .Append(mesh.SkinningShaderConvention)
                    .Append(" encoding=")
                    .Append(mesh.SkinningInfluenceEncoding)
                    .Append(" indexFormat=")
                    .Append(mesh.SkinningCoreIndexFormat)
                    .Append(" paletteCount=")
                    .Append(renderer.ActiveSkinPaletteCount)
                    .Append(" externalPalette=")
                    .Append(renderer.HasExternalSkinPaletteSource)
                    .Append(" expectedMaxT=")
                    .Append(maximumExpectedTranslation.ToString("F6"))
                    .Append('@')
                    .Append(maximumExpectedBone ?? "<none>")
                    .Append(" bufferedMaxT=")
                    .Append(maximumBufferedTranslation.ToString("F6"))
                    .Append('@')
                    .Append(maximumBufferedBone ?? "<none>")
                    .Append(" paletteMaxDiff=")
                    .Append(maximumPaletteDifference.ToString("F6"))
                    .Append('@')
                    .Append(maximumDifferenceBone ?? "<none>");
            }
        }

        return summary.ToString();
    }

    private static Matrix4x4 GetCurrentBoneMatrix(TransformBase transform)
    {
        Matrix4x4 renderMatrix = transform.RenderMatrix;
        if (!renderMatrix.Equals(Matrix4x4.Identity))
            return renderMatrix;

        Matrix4x4 worldMatrix = transform.WorldMatrix;
        return worldMatrix.Equals(Matrix4x4.Identity) ? renderMatrix : worldMatrix;
    }

    private static bool IsSelfOrDescendantOf(TransformBase root, TransformBase candidate)
    {
        for (TransformBase? current = candidate; current is not null; current = current.Parent)
            if (ReferenceEquals(current, root))
                return true;

        return false;
    }

    private static float MaximumElementDifference(in Matrix4x4 left, in Matrix4x4 right)
    {
        float maximum = 0.0f;
        maximum = MathF.Max(maximum, MathF.Abs(left.M11 - right.M11));
        maximum = MathF.Max(maximum, MathF.Abs(left.M12 - right.M12));
        maximum = MathF.Max(maximum, MathF.Abs(left.M13 - right.M13));
        maximum = MathF.Max(maximum, MathF.Abs(left.M14 - right.M14));
        maximum = MathF.Max(maximum, MathF.Abs(left.M21 - right.M21));
        maximum = MathF.Max(maximum, MathF.Abs(left.M22 - right.M22));
        maximum = MathF.Max(maximum, MathF.Abs(left.M23 - right.M23));
        maximum = MathF.Max(maximum, MathF.Abs(left.M24 - right.M24));
        maximum = MathF.Max(maximum, MathF.Abs(left.M31 - right.M31));
        maximum = MathF.Max(maximum, MathF.Abs(left.M32 - right.M32));
        maximum = MathF.Max(maximum, MathF.Abs(left.M33 - right.M33));
        maximum = MathF.Max(maximum, MathF.Abs(left.M34 - right.M34));
        maximum = MathF.Max(maximum, MathF.Abs(left.M41 - right.M41));
        maximum = MathF.Max(maximum, MathF.Abs(left.M42 - right.M42));
        maximum = MathF.Max(maximum, MathF.Abs(left.M43 - right.M43));
        maximum = MathF.Max(maximum, MathF.Abs(left.M44 - right.M44));
        return maximum;
    }
}
