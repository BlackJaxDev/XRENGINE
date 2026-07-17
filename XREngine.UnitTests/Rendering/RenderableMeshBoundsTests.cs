using System.Numerics;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderableMeshBoundsTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void ResolveBoundsDebugColor_UsesYellowOnlyForOcclusionCulledMeshes(bool occlusionCulled)
    {
        ColorF4 normalColor = ColorF4.Blue;

        ColorF4 resolved = RenderableMesh.ResolveBoundsDebugColor(normalColor, occlusionCulled);

        resolved.ShouldBe(occlusionCulled ? ColorF4.Yellow : normalColor);
    }

    [Test]
    public void BoundsDebugCommand_RunsAfterEveryCpuQueryTestablePass()
    {
        SceneNode node = new("BoundsDebugRoot");
        ModelComponent component = node.AddComponent<ModelComponent>()!;
        component.Model = new Model(
            new SubMesh(
                XRMesh.Shapes.SolidBox(Vector3.Zero, new Vector3(2.0f)),
                new XRMaterial()));

        RenderableMesh renderable = component.Meshes.Single();
        FieldInfo field = typeof(RenderableMesh).GetField(
            "_renderBoundsCommand",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        RenderCommand command = (RenderCommand)field.GetValue(renderable)!;
        command.RenderPass.ShouldBe((int)EDefaultRenderPass.OnTopForward);
    }

    [Test]
    public void TransformBounds_AffineMatrix_MatchesCornerWiseMatrixTransform()
    {
        var method = typeof(RenderableMesh).GetMethod("TransformBounds", BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull();

        AABB bounds = new(new Vector3(-2f, -1f, -3f), new Vector3(2f, 4f, 5f));
        Matrix4x4 matrix = Matrix4x4.CreateScale(1.25f, 0.75f, 2.0f)
            * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.25f, -0.3f, 0.4f)))
            * Matrix4x4.CreateTranslation(6f, -2f, 9f);

        AABB transformed = (AABB)method!.Invoke(null, [bounds, matrix])!;
        AABB expected = bounds.Transformed(p => Vector3.Transform(p, matrix));

        transformed.Min.X.ShouldBe(expected.Min.X, 0.0001f);
        transformed.Min.Y.ShouldBe(expected.Min.Y, 0.0001f);
        transformed.Min.Z.ShouldBe(expected.Min.Z, 0.0001f);
        transformed.Max.X.ShouldBe(expected.Max.X, 0.0001f);
        transformed.Max.Y.ShouldBe(expected.Max.Y, 0.0001f);
        transformed.Max.Z.ShouldBe(expected.Max.Z, 0.0001f);
    }

    [Test]
    public void AabbAffineTransform_MatchesCornerWiseTransform()
    {
        AABB bounds = new(new Vector3(-2f, -1f, -3f), new Vector3(2f, 4f, 5f));
        Matrix4x4 matrix = Matrix4x4.CreateScale(-1.25f, 0.75f, 2.0f)
            * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.25f, -0.3f, 0.4f)))
            * Matrix4x4.CreateTranslation(6f, -2f, 9f);

        AABB transformed = bounds.Transformed(matrix);
        AABB expected = bounds.Transformed(point => Vector3.Transform(point, matrix));

        transformed.Min.X.ShouldBe(expected.Min.X, 0.0001f);
        transformed.Min.Y.ShouldBe(expected.Min.Y, 0.0001f);
        transformed.Min.Z.ShouldBe(expected.Min.Z, 0.0001f);
        transformed.Max.X.ShouldBe(expected.Max.X, 0.0001f);
        transformed.Max.Y.ShouldBe(expected.Max.Y, 0.0001f);
        transformed.Max.Z.ShouldBe(expected.Max.Z, 0.0001f);
    }

    [Test]
    public void HasUsableSkinnedBoundsResult_AcceptsGpuOnlyBoundsWithoutCpuPositions()
    {
        AABB bounds = new(new Vector3(-1f, -2f, -3f), new Vector3(1f, 2f, 3f));
        var result = new SkinnedMeshBoundsCalculator.Result([], bounds, Matrix4x4.Identity);

        RenderableMesh.HasUsableSkinnedBoundsResult(result).ShouldBeTrue();
    }

    [Test]
    public void HasUsableSkinnedBoundsResult_RejectsDefaultEmptyBoundsWithoutCpuPositions()
    {
        var result = new SkinnedMeshBoundsCalculator.Result([], default, Matrix4x4.Identity);

        RenderableMesh.HasUsableSkinnedBoundsResult(result).ShouldBeFalse();
    }

    [Test]
    public void ResolveSkinnedRootBoneTransform_PrefersSerializedRootBoneOverInferredAncestor()
    {
        var serializedRootBone = new Transform();
        var inferredAncestor = new Transform();

        TransformBase? rootBone = RenderableMesh.ResolveSkinnedRootBoneTransform(
            serializedRootBone,
            inferredAncestor);

        ReferenceEquals(rootBone, serializedRootBone).ShouldBeTrue();
    }

    [Test]
    public void ResolveSkinnedRootBoneTransform_UsesInferredPrefabBoneWhenSerializedRootIsOutsideInstance()
    {
        SceneNode prefabRoot = new("PrefabRoot");
        SceneNode inferredBoneNode = new(prefabRoot, "Bone");
        Transform serializedRootBone = new();

        TransformBase? rootBone = RenderableMesh.ResolveSkinnedRootBoneTransform(
            serializedRootBone,
            inferredBoneNode.Transform,
            prefabRoot.Transform);

        ReferenceEquals(rootBone, inferredBoneNode.Transform).ShouldBeTrue();
    }

    [Test]
    public void TryGetWorldBounds_UsesRenderInfoCullingBasisForBindPoseFallback()
    {
        SceneNode node = new("BoundsRoot");
        Transform transform = node.SetTransform<Transform>();
        transform.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

        ModelComponent component = node.AddComponent<ModelComponent>()!;
        component.Model = new Model(
            new SubMesh(
                XRMesh.Shapes.SolidBox(Vector3.Zero, new Vector3(2.0f)),
                new XRMaterial()));

        RenderableMesh renderable = component.Meshes.Single();
        renderable.RenderInfo.LocalCullingVolume = new AABB(new Vector3(-1.0f), new Vector3(1.0f));
        renderable.RenderInfo.CullingOffsetMatrix = Matrix4x4.CreateTranslation(12.0f, -3.0f, 4.0f);

        renderable.TryGetWorldBounds(out AABB worldBounds).ShouldBeTrue();

        worldBounds.Center.X.ShouldBe(12.0f, 0.0001f);
        worldBounds.Center.Y.ShouldBe(-3.0f, 0.0001f);
        worldBounds.Center.Z.ShouldBe(4.0f, 0.0001f);
    }

    [Test]
    public void RuntimeSkinnedBoneCullingBounds_ReplaceStaticAuthoredBoundsDuringPreCollect()
    {
        SceneNode root = new("SkinnedRoot");
        SceneNode meshNode = new(root, "MeshNode");
        SceneNode boneNode = new(root, "Bone");

        Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)> weights = new()
        {
            [boneNode.Transform] = (1.0f, Matrix4x4.Identity),
        };

        XRMesh mesh = new(
            [
                new Vertex(new Vector3(-0.25f, 0.0f, 0.0f)) { Weights = weights },
                new Vertex(new Vector3(0.25f, 0.0f, 0.0f)) { Weights = weights },
                new Vertex(new Vector3(0.0f, 0.5f, 0.0f)) { Weights = weights },
            ],
            new List<ushort> { 0, 1, 2 });
        mesh.RebuildSkinningBuffersFromVertices();

        AABB authoredBounds = new(new Vector3(-3.0f, -2.0f, -1.0f), new Vector3(3.0f, 2.0f, 1.0f));
        SubMesh subMesh = new(new SubMeshLOD(new XRMaterial(), mesh, 0.0f))
        {
            CullingBounds = authoredBounds,
            RootBone = boneNode.Transform,
            RootTransform = root.Transform,
        };

        ModelComponent component = meshNode.AddComponent<ModelComponent>()!;
        component.Model = new Model(subMesh);

        RenderableMesh renderable = component.Meshes.Single();
        renderable.IsSkinned.ShouldBeTrue();

        MethodInfo ensureSkinnedBounds = typeof(RenderableMesh).GetMethod(
            "EnsureSkinnedBounds",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool ensured = (bool)ensureSkinnedBounds.Invoke(renderable, null)!;
        ensured.ShouldBeFalse();

        MethodInfo processPending = typeof(RenderableMesh).GetMethod(
            "ProcessPendingRenderMatrixUpdates",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        processPending.Invoke(null, null);

        AABB runtimeBounds = renderable.RenderInfo.LocalCullingVolume.ShouldNotBeNull();
        runtimeBounds.Min.X.ShouldBe(-0.25f, 0.0001f);
        runtimeBounds.Min.Y.ShouldBe(0.0f, 0.0001f);
        runtimeBounds.Min.Z.ShouldBe(0.0f, 0.0001f);
        runtimeBounds.Max.X.ShouldBe(0.25f, 0.0001f);
        runtimeBounds.Max.Y.ShouldBe(0.5f, 0.0001f);
        runtimeBounds.Max.Z.ShouldBe(0.0f, 0.0001f);
        renderable.RenderInfo.CullingOffsetMatrix.ShouldBe(Matrix4x4.Identity);
    }

    [Test]
    public void RefreshSkinnedCullingBoundsForSceneCulling_PublishesAnimatedBoundsWithoutPendingMatrixQueue()
    {
        SceneNode root = new("SkinnedRoot");
        SceneNode meshNode = new(root, "MeshNode");
        Transform bone = CreateBone(Vector3.Zero);

        XRMesh mesh = CreateSingleBoneWeightedMesh(bone);
        SubMesh subMesh = new(new SubMeshLOD(new XRMaterial(), mesh, 0.0f))
        {
            CullingBounds = new AABB(new Vector3(-3.0f), new Vector3(3.0f)),
            RootBone = bone,
            RootTransform = root.Transform,
        };

        ModelComponent component = meshNode.AddComponent<ModelComponent>()!;
        component.Model = new Model(subMesh);

        RenderableMesh renderable = component.Meshes.Single();
        renderable.IsSkinned.ShouldBeTrue();
        renderable.RenderInfo.CullingIntersectionOverride.ShouldNotBeNull();
        renderable.RenderInfo.LocalCullingVolume = new AABB(new Vector3(-50.0f), new Vector3(-49.0f));
        renderable.RenderInfo.CullingOffsetMatrix = Matrix4x4.CreateTranslation(-50.0f, 0.0f, 0.0f);

        MoveBone(bone, new Vector3(8.0f, 2.0f, -1.0f));

        renderable.RefreshSkinnedCullingBoundsForSceneCulling().ShouldBeTrue();

        AABB runtimeBounds = renderable.RenderInfo.LocalCullingVolume.ShouldNotBeNull();
        runtimeBounds.Min.X.ShouldBe(7.95f, 0.0001f);
        runtimeBounds.Min.Y.ShouldBe(1.95f, 0.0001f);
        runtimeBounds.Min.Z.ShouldBe(-1.0f, 0.0001f);
        runtimeBounds.Max.X.ShouldBe(8.05f, 0.0001f);
        runtimeBounds.Max.Y.ShouldBe(2.05f, 0.0001f);
        runtimeBounds.Max.Z.ShouldBe(-1.0f, 0.0001f);
        renderable.RenderInfo.CullingOffsetMatrix.ShouldBe(Matrix4x4.Identity);
    }

    [Test]
    public void RefreshSkinnedCullingBoundsForSceneCulling_ForceUnboundedPublishesNullBounds()
    {
        bool previous = RenderDiagnosticsFlags.ForceSkinnedUnbounded;
        RenderDiagnosticsFlags.SetForceSkinnedUnbounded(true);
        try
        {
            SceneNode root = new("SkinnedRoot");
            SceneNode meshNode = new(root, "MeshNode");
            Transform bone = CreateBone(Vector3.Zero);

            XRMesh mesh = CreateSingleBoneWeightedMesh(bone);
            SubMesh subMesh = new(new SubMeshLOD(new XRMaterial(), mesh, 0.0f))
            {
                CullingBounds = new AABB(new Vector3(-3.0f), new Vector3(3.0f)),
                RootBone = bone,
                RootTransform = root.Transform,
            };

            ModelComponent component = meshNode.AddComponent<ModelComponent>()!;
            component.Model = new Model(subMesh);

            RenderableMesh renderable = component.Meshes.Single();
            renderable.RefreshSkinnedCullingBoundsForSceneCulling().ShouldBeTrue();

            renderable.RenderInfo.LocalCullingVolume.ShouldBeNull();
            renderable.RenderInfo.CullingOffsetMatrix.ShouldBe(Matrix4x4.Identity);
        }
        finally
        {
            RenderDiagnosticsFlags.SetForceSkinnedUnbounded(previous);
        }
    }

    [Test]
    public void ResolveSkinnedBoundsBasisTransform_PrefersRootBoneOverImportRoot()
    {
        var importRoot = new Transform();
        var rootBone = new Transform();
        TransformBase? basis = RenderableMesh.ResolveSkinnedBoundsBasisTransform(
            rootBone,
            importRoot);

        ReferenceEquals(basis, rootBone).ShouldBeTrue();
    }

    [Test]
    public void ResolveSkinnedBoundsBasisTransform_FallsBackToImportRootWithoutRootBone()
    {
        var importRoot = new Transform();
        TransformBase? basis = RenderableMesh.ResolveSkinnedBoundsBasisTransform(null, importRoot);

        ReferenceEquals(basis, importRoot).ShouldBeTrue();
    }

    [Test]
    public void BuildSkinnedBoneCullingVolumes_CreatesOneLocalBoxPerWeightedBone()
    {
        var boneA = new Transform();
        var boneB = new Transform();
        XRMesh mesh = CreateTwoBoneWeightedMesh(boneA, boneB);

        RenderableMesh.SkinnedBoneCullingVolume[] volumes = RenderableMesh.BuildSkinnedBoneCullingVolumes(mesh, boneA);

        volumes.Length.ShouldBe(2);
        volumes.Any(volume => ReferenceEquals(volume.Transform, boneA)).ShouldBeTrue();
        volumes.Any(volume => ReferenceEquals(volume.Transform, boneB)).ShouldBeTrue();
        volumes.All(volume => volume.LocalBounds.IsValid).ShouldBeTrue();
    }

    [Test]
    public void IntersectsSkinnedBoneCullingVolumes_ReturnsTrueWhenAnyBoneBoxIntersects()
    {
        var visibleBone = new Transform();
        var hiddenBone = new Transform(new Vector3(50.0f, 0.0f, 0.0f));
        visibleBone.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);
        hiddenBone.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

        XRMesh mesh = CreateTwoBoneWeightedMesh(visibleBone, hiddenBone);
        RenderableMesh.SkinnedBoneCullingVolume[] volumes = RenderableMesh.BuildSkinnedBoneCullingVolumes(mesh, visibleBone);
        AABB viewBounds = new(new Vector3(-2.0f), new Vector3(2.0f));

        bool intersects = RenderableMesh.IntersectsSkinnedBoneCullingVolumes(volumes, viewBounds, containsOnly: false);

        intersects.ShouldBeTrue();
    }

    [Test]
    public void IntersectsSkinnedBoneCullingVolumes_ReturnsFalseWhenEveryBoneBoxIsOutsideView()
    {
        var boneA = new Transform(new Vector3(50.0f, 0.0f, 0.0f));
        var boneB = new Transform(new Vector3(-50.0f, 0.0f, 0.0f));
        boneA.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);
        boneB.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

        XRMesh mesh = CreateTwoBoneWeightedMesh(boneA, boneB);
        RenderableMesh.SkinnedBoneCullingVolume[] volumes = RenderableMesh.BuildSkinnedBoneCullingVolumes(mesh, boneA);
        AABB viewBounds = new(new Vector3(-2.0f), new Vector3(2.0f));

        bool intersects = RenderableMesh.IntersectsSkinnedBoneCullingVolumes(volumes, viewBounds, containsOnly: false);

        intersects.ShouldBeFalse("the per-bone narrow phase only accepts the mesh when at least one transformed bone box intersects.");
    }

    [Test]
    public void IntersectsSkinnedBoneCullingVolumes_DoesNotUseAggregateGapAsFinalVisibility()
    {
        Transform boneA = CreateBone(new Vector3(12.0f, 0.0f, 0.0f));
        Transform boneB = CreateBone(new Vector3(-12.0f, 0.0f, 0.0f));
        XRMesh mesh = CreateTwoBoneBlendedMesh(boneA, boneB);
        RenderableMesh.SkinnedBoneCullingVolume[] volumes = RenderableMesh.BuildSkinnedBoneCullingVolumes(mesh, boneA);
        AABB viewBounds = AABB.FromCenterSize(Vector3.Zero, new Vector3(1.0f));

        Vector3 blendedPosition = ComputeSkinnedPosition(mesh.Vertices[0]);
        viewBounds.ContainsPoint(blendedPosition).ShouldBeTrue();
        EachIndividualBoneBoxShouldBeOutsideView(volumes, viewBounds);

        bool intersects = RenderableMesh.IntersectsSkinnedBoneCullingVolumes(volumes, viewBounds, containsOnly: false);

        intersects.ShouldBeFalse("final skinned visibility is determined by individual transformed bone boxes, not by their aggregate span.");
    }

    [Test]
    public void IntersectsSkinnedBoneCullingVolumes_CullsAfterAnimatedPoseMovesEveryBoneBoxOutsideView()
    {
        Transform boneA = CreateBone(Vector3.Zero);
        Transform boneB = CreateBone(Vector3.Zero);
        XRMesh mesh = CreateTwoBoneBlendedMesh(boneA, boneB);
        RenderableMesh.SkinnedBoneCullingVolume[] volumes = RenderableMesh.BuildSkinnedBoneCullingVolumes(mesh, boneA);
        AABB viewBounds = AABB.FromCenterSize(Vector3.Zero, new Vector3(1.0f));

        RenderableMesh.IntersectsSkinnedBoneCullingVolumes(volumes, viewBounds, containsOnly: false)
            .ShouldBeTrue("bind pose sanity check: both bone boxes start around the visible blended geometry.");

        MoveBone(boneA, new Vector3(12.0f, 0.0f, 0.0f));
        MoveBone(boneB, new Vector3(-12.0f, 0.0f, 0.0f));

        Vector3 blendedPosition = ComputeSkinnedPosition(mesh.Vertices[0]);
        viewBounds.ContainsPoint(blendedPosition).ShouldBeTrue();
        EachIndividualBoneBoxShouldBeOutsideView(volumes, viewBounds);

        bool intersects = RenderableMesh.IntersectsSkinnedBoneCullingVolumes(volumes, viewBounds, containsOnly: false);

        intersects.ShouldBeFalse("once animation moves every transformed bone box outside the view, the mesh is culled.");
    }

    [Test]
    public async Task IntersectsSkinnedBoneCullingVolumes_UsesPublishedRenderMatrixWhenWorldMatrixHasAdvanced()
    {
        Transform bone = CreateBone(new Vector3(12.0f, 0.0f, 0.0f));
        await bone.SetRenderMatrix(Matrix4x4.CreateTranslation(0.1f, 0.0f, 0.0f), recalcAllChildRenderMatrices: false);
        XRMesh mesh = CreateSingleBoneWeightedMesh(bone);
        RenderableMesh.SkinnedBoneCullingVolume[] volumes = RenderableMesh.BuildSkinnedBoneCullingVolumes(mesh, bone);
        AABB viewBounds = AABB.FromCenterSize(Vector3.Zero, new Vector3(1.0f));

        bool intersects = RenderableMesh.IntersectsSkinnedBoneCullingVolumes(volumes, viewBounds, containsOnly: false);

        intersects.ShouldBeTrue("culling must match the render-thread skin palette pose when RenderMatrix and WorldMatrix diverge.");
    }

    private static XRMesh CreateTwoBoneWeightedMesh(TransformBase boneA, TransformBase boneB)
    {
        Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)> weightsA = new()
        {
            [boneA] = (1.0f, Matrix4x4.Identity),
        };
        Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)> weightsB = new()
        {
            [boneB] = (1.0f, Matrix4x4.Identity),
        };

        XRMesh mesh = new(
            [
                new Vertex(new Vector3(-0.5f, -0.5f, 0.0f)) { Weights = weightsA },
                new Vertex(new Vector3(0.5f, -0.5f, 0.0f)) { Weights = weightsA },
                new Vertex(new Vector3(0.0f, 0.5f, 0.0f)) { Weights = weightsA },
                new Vertex(new Vector3(-0.25f, -0.25f, 0.0f)) { Weights = weightsB },
                new Vertex(new Vector3(0.25f, -0.25f, 0.0f)) { Weights = weightsB },
                new Vertex(new Vector3(0.0f, 0.25f, 0.0f)) { Weights = weightsB },
            ],
            new List<ushort> { 0, 1, 2, 3, 4, 5 });
        mesh.RebuildSkinningBuffersFromVertices();
        return mesh;
    }

    private static XRMesh CreateSingleBoneWeightedMesh(TransformBase bone)
    {
        Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)> weights = new()
        {
            [bone] = (1.0f, Matrix4x4.Identity),
        };

        XRMesh mesh = new(
            [
                new Vertex(new Vector3(-0.05f, -0.05f, 0.0f), weights),
                new Vertex(new Vector3(0.05f, -0.05f, 0.0f), weights),
                new Vertex(new Vector3(0.0f, 0.05f, 0.0f), weights),
            ],
            new List<ushort> { 0, 1, 2 });
        mesh.RebuildSkinningBuffersFromVertices();
        return mesh;
    }

    private static XRMesh CreateTwoBoneBlendedMesh(TransformBase boneA, TransformBase boneB)
    {
        XRMesh mesh = new(
            [
                new Vertex(new Vector3(-0.05f, -0.05f, 0.0f), CreateEvenBlendWeights(boneA, boneB)),
                new Vertex(new Vector3(0.05f, -0.05f, 0.0f), CreateEvenBlendWeights(boneA, boneB)),
                new Vertex(new Vector3(0.0f, 0.05f, 0.0f), CreateEvenBlendWeights(boneA, boneB)),
            ],
            new List<ushort> { 0, 1, 2 });
        mesh.RebuildSkinningBuffersFromVertices();
        return mesh;
    }

    private static Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)> CreateEvenBlendWeights(
        TransformBase boneA,
        TransformBase boneB)
        => new()
        {
            [boneA] = (0.5f, Matrix4x4.Identity),
            [boneB] = (0.5f, Matrix4x4.Identity),
        };

    private static Transform CreateBone(Vector3 translation)
    {
        Transform bone = new(translation);
        RecalculateForCulling(bone);
        return bone;
    }

    private static void MoveBone(Transform bone, Vector3 translation)
    {
        bone.Translation = translation;
        RecalculateForCulling(bone);
    }

    private static void RecalculateForCulling(TransformBase transform)
        => transform.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

    private static Vector3 ComputeSkinnedPosition(Vertex vertex)
    {
        Vector3 result = Vector3.Zero;
        foreach ((TransformBase bone, (float weight, Matrix4x4 bindInvWorldMatrix) data) in vertex.Weights!)
            result += Vector3.Transform(vertex.Position, data.bindInvWorldMatrix * bone.WorldMatrix) * data.weight;
        return result;
    }

    private static void EachIndividualBoneBoxShouldBeOutsideView(
        RenderableMesh.SkinnedBoneCullingVolume[] volumes,
        AABB viewBounds)
    {
        foreach (RenderableMesh.SkinnedBoneCullingVolume volume in volumes)
            RenderableMesh.IntersectsSkinnedBoneCullingVolumes([volume], viewBounds, containsOnly: false)
                .ShouldBeFalse();
    }
}
