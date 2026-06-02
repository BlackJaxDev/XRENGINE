using System.Numerics;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderableMeshBoundsTests
{
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
    public void AuthoredSkinnedCullingBounds_AreNotReplacedByRuntimeSkinnedBounds()
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

        renderable.RenderInfo.LocalCullingVolume.ShouldBe(authoredBounds);
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
}
