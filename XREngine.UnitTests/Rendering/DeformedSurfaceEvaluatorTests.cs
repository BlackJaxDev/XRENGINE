using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Budgets;
using XREngine.Rendering.Models.Gaussian.Baking.Motion;
using XREngine.Rendering.Models.Gaussian.Baking.Surface;
using XREngine.Scene;
using XREngine.Scene.Components.Particles;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class DeformedSurfaceEvaluatorTests
{
    private const float PositionTolerance = 1e-4f;

    [Test]
    public void Evaluate_StaticMesh_AppliesWorldMatrix()
    {
        SceneNode root = new("Root", new Transform());
        SceneNode cubeNode = new(root, "Cube", new Transform(translation: new Vector3(1.0f, 2.0f, 3.0f)));
        XRMesh cube = BakeMotionTestScenes.AddStaticCube(cubeNode);
        root.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);

        DeformedSurface surface = DeformedSurfaceEvaluator.Evaluate(root);

        surface.MeshCount.ShouldBe(1);
        surface.VertexCount.ShouldBe(cube.VertexCount);
        surface.MeshVertexStart.ShouldBe([0, cube.VertexCount]);
        surface.Positions.Length.ShouldBe(cube.VertexCount * 3);
        surface.TriangleCount.ShouldBe(cube.Triangles.ShouldNotBeNull().Count);
        surface.Indices.Length.ShouldBe(surface.TriangleCount * 3);
        surface.TriangleMesh.Length.ShouldBe(surface.TriangleCount);
        surface.TriangleMesh.ShouldAllBe(mesh => mesh == 0);
        for (int triangle = 0; triangle < surface.TriangleCount; triangle++)
        {
            surface.Indices[triangle * 3].ShouldBe((uint)cube.Triangles[triangle].Point0);
            surface.Indices[triangle * 3 + 1].ShouldBe((uint)cube.Triangles[triangle].Point1);
            surface.Indices[triangle * 3 + 2].ShouldBe((uint)cube.Triangles[triangle].Point2);
        }

        for (int vertex = 0; vertex < cube.VertexCount; vertex++)
            AssertPosition(surface, vertex, cube.GetPosition((uint)vertex) + new Vector3(1.0f, 2.0f, 3.0f));
    }

    [Test]
    public void Evaluate_SkinnedGlb_RigidSingleInfluenceVerticesKeepDistances()
    {
        SceneNode root = ImportSkinnedAvatar(out BakeMotionChoice choice);
        XRMesh mesh = BakeMotionTestScenes.FindRenderers(root).ShouldHaveSingleItem().Renderer.Mesh.ShouldNotBeNull();
        mesh.HasSkinning.ShouldBeTrue();
        Dictionary<string, List<int>> rigidVerticesByBone = FindRigidVerticesByBone(mesh);
        rigidVerticesByBone.ShouldContainKey("RootJoint");
        rigidVerticesByBone["RootJoint"].Count.ShouldBeGreaterThanOrEqualTo(2);

        DeformedSurface rest = BakeFrameSampler.EvaluateAt(root, choice, 0.0f);
        float length = choice.Players[0].Animation.ShouldNotBeNull().LengthInSeconds;
        for (int sample = 0; sample < 8; sample++)
        {
            DeformedSurface posed = BakeFrameSampler.EvaluateAt(root, choice, sample * length / 8.0f);
            foreach (List<int> vertices in rigidVerticesByBone.Values)
            {
                for (int a = 0; a < vertices.Count; a++)
                {
                    for (int b = a + 1; b < vertices.Count; b++)
                    {
                        float restDistance = Vector3.Distance(Position(rest, vertices[a]), Position(rest, vertices[b]));
                        float posedDistance = Vector3.Distance(Position(posed, vertices[a]), Position(posed, vertices[b]));
                        posedDistance.ShouldBe(restDistance, PositionTolerance);
                    }
                }
            }
        }
    }

    [Test]
    public void Evaluate_SkinnedGlb_MovesBetweenTimes()
    {
        SceneNode root = ImportSkinnedAvatar(out BakeMotionChoice choice);
        float length = choice.Players[0].Animation.ShouldNotBeNull().LengthInSeconds;

        DeformedSurface start = BakeFrameSampler.EvaluateAt(root, choice, 0.0f);
        DeformedSurface middle = BakeFrameSampler.EvaluateAt(root, choice, length * 0.5f);

        start.VertexCount.ShouldBe(middle.VertexCount);
        float maxDisplacement = 0.0f;
        for (int vertex = 0; vertex < start.VertexCount; vertex++)
            maxDisplacement = MathF.Max(maxDisplacement, Vector3.Distance(Position(start, vertex), Position(middle, vertex)));
        maxDisplacement.ShouldBeGreaterThan(1e-3f);
    }

    [Test]
    public void Evaluate_SkinnedMesh_AppliesBindRootThenInverseBindThenBoneMatrix()
    {
        SceneNode rig = new("Rig", new Transform());
        SceneNode boneANode = new(rig, "BoneA", new Transform());
        SceneNode boneBNode = new(boneANode, "BoneB", new Transform(translation: Vector3.UnitY));
        SceneNode visual = new(rig, "Visual", new Transform(translation: new Vector3(3.0f, 0.0f, 0.0f)));
        Transform boneA = boneANode.GetTransformAs<Transform>(true).ShouldNotBeNull();
        Transform boneB = boneBNode.GetTransformAs<Transform>(true).ShouldNotBeNull();
        Matrix4x4 inverseBindA = Matrix4x4.Identity;
        Matrix4x4 inverseBindB = Matrix4x4.CreateTranslation(0.0f, -1.0f, 0.0f);
        Vertex[] vertices =
        [
            new Vertex(new Vector3(0.5f, 1.0f, 0.0f), new Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>
            {
                [boneB] = (1.0f, inverseBindB),
            }),
            new Vertex(new Vector3(1.0f, 1.0f, 0.0f), new Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>
            {
                [boneA] = (0.6f, inverseBindA),
                [boneB] = (0.4f, inverseBindB),
            }),
            new Vertex(Vector3.Zero, new Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>()),
        ];
        XRMesh mesh = new(vertices, [0, 1, 2]);
        mesh.RebuildSkinningBuffersFromVertices(vertices);
        mesh.BindRootMatrix = Matrix4x4.CreateScale(2.0f) * Matrix4x4.CreateTranslation(0.0f, 0.0f, 2.0f);
        mesh.HasSkinning.ShouldBeTrue();
        visual.AddComponent<ModelComponent>().ShouldNotBeNull().Model = new Model(new SubMesh(mesh, new XRMaterial()));

        boneA.Translation = new Vector3(0.25f, 0.0f, 0.0f);
        boneB.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2.0f);
        rig.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);

        DeformedSurface surface = DeformedSurfaceEvaluator.Evaluate(rig);

        // BindRoot scales each skinned vertex by 2 and then moves it by +2 in Z. BoneB then removes its bind offset
        // (Y - 1), turns 90 degrees about +Z, and moves to its posed origin (0.25, 1, 0). BoneA moves by (0.25, 0, 0).
        // Another matrix order gives other positions, because the scale does not commute with the offsets.
        surface.VertexCount.ShouldBe(3);
        AssertPosition(surface, 0, new Vector3(-0.75f, 2.0f, 2.0f));
        // BoneA alone gives (2.25, 2, 2) and BoneB alone gives (-0.75, 3, 2). The weights 0.6 and 0.4 are exact in
        // the unorm8 influence buffers (153 and 102 of 255).
        AssertPosition(surface, 1, new Vector3(1.05f, 2.4f, 2.0f));
        // A vertex with no influences uses the component transform.
        AssertPosition(surface, 2, new Vector3(3.0f, 0.0f, 0.0f));
    }

    [Test]
    public void Evaluate_MorphFixture_WeightOne_AddsDelta()
    {
        SceneNode root = GaussianBakeTestAssets.ImportPrefab(BakeMotionTestScenes.MorphModelPath).RootNode.ShouldNotBeNull();
        (RenderableComponent component, XRMeshRenderer renderer) = BakeMotionTestScenes.FindRenderers(root).ShouldHaveSingleItem();
        XRMesh mesh = renderer.Mesh.ShouldNotBeNull();
        mesh.BlendshapeCount.ShouldBe(1u);
        root.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);

        renderer.SetBlendshapeWeightNormalized(0u, 0.0f);
        float[] basePositions = [.. DeformedSurfaceEvaluator.Evaluate(root).Positions];
        renderer.SetBlendshapeWeightNormalized(0u, 1.0f);
        renderer.GetBlendshapeWeightNormalized(0u).ShouldBe(1.0f);
        DeformedSurface morphed = DeformedSurfaceEvaluator.Evaluate(root);

        Matrix4x4 world = component.Transform.WorldMatrix;
        int movedVertices = 0;
        for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
        {
            // The morph target of the fixture moves the two top vertices (local Y = 1) up by 0.25.
            bool isTopVertex = MathF.Abs(mesh.GetPosition((uint)vertex).Y - 1.0f) < PositionTolerance;
            Vector3 localDelta = isTopVertex ? new Vector3(0.0f, 0.25f, 0.0f) : Vector3.Zero;
            if (isTopVertex)
                movedVertices++;

            Vector3 expected = BakeMotionTestScenes.PositionOf(basePositions, vertex) + Vector3.TransformNormal(localDelta, world);
            AssertPosition(morphed, vertex, expected);
        }

        movedVertices.ShouldBe(2);
    }

    [Test]
    public void Evaluate_RigidMeshUnderAnimatedBone_Moves()
    {
        SceneNode root = ImportSkinnedAvatar(out BakeMotionChoice choice);
        SceneNode childJoint = root.FindDescendantByName("ChildJoint").ShouldNotBeNull();
        SceneNode prop = new(childJoint, "Prop", new Transform(translation: new Vector3(0.0f, 0.5f, 0.0f)));
        XRMesh cube = BakeMotionTestScenes.AddStaticCube(prop);
        float length = choice.Players[0].Animation.ShouldNotBeNull().LengthInSeconds;

        DeformedSurface start = BakeFrameSampler.EvaluateAt(root, choice, 0.0f);
        DeformedSurface middle = BakeFrameSampler.EvaluateAt(root, choice, length * 0.5f);

        start.MeshCount.ShouldBe(2);
        int propMesh = Enumerable.Range(0, start.MeshCount)
            .Single(mesh => start.MeshVertexStart[mesh + 1] - start.MeshVertexStart[mesh] == cube.VertexCount);
        for (int vertex = start.MeshVertexStart[propMesh]; vertex < start.MeshVertexStart[propMesh + 1]; vertex++)
            Vector3.Distance(Position(start, vertex), Position(middle, vertex)).ShouldBeGreaterThan(1e-3f);
    }

    [Test]
    public void FindSkipped_ListsParticleEmitter()
    {
        SceneNode root = new("Root", new Transform());
        BakeMotionTestScenes.AddStaticCube(new SceneNode(root, "Body", new Transform()));
        SceneNode sparks = new(root, "Sparks", new Transform());
        sparks.AddComponent<ParticleEmitterComponent>().ShouldNotBeNull();

        IReadOnlyList<BakeSkippedComponent> skipped = DeformedSurfaceEvaluator.FindSkipped(root);

        BakeSkippedComponent entry = skipped.ShouldHaveSingleItem();
        entry.NodePath.ShouldBe("Root/Sparks");
        entry.ComponentType.ShouldBe(nameof(ParticleEmitterComponent));
        entry.Reason.ShouldBe("not-captured");
    }

    [Test]
    public void Evaluate_InactiveComponentOrNode_IsLeftOut()
    {
        SceneNode root = new("Root", new Transform());
        XRMesh visible = BakeMotionTestScenes.AddStaticCube(new SceneNode(root, "Visible", new Transform()));
        SceneNode hiddenNode = new(root, "HiddenNode", new Transform());
        BakeMotionTestScenes.AddStaticCube(hiddenNode);
        hiddenNode.IsActiveSelf = false;
        SceneNode hiddenComponentNode = new(root, "HiddenComponent", new Transform());
        BakeMotionTestScenes.AddStaticCube(hiddenComponentNode);
        hiddenComponentNode.GetComponent<ModelComponent>().ShouldNotBeNull().IsActive = false;
        root.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);

        DeformedSurface surface = DeformedSurfaceEvaluator.Evaluate(root);

        surface.MeshCount.ShouldBe(1);
        surface.VertexCount.ShouldBe(visible.VertexCount);
    }

    [Test]
    public void Evaluate_WithReuse_DoesNotAllocate()
    {
        SceneNode root = ImportSkinnedAvatar(out _);
        BakeMotionTestScenes.AddStaticCube(new SceneNode(root, "Static", new Transform(translation: new Vector3(2.0f, 0.0f, 0.0f))));
        SceneNode morph = GaussianBakeTestAssets.ImportPrefab(BakeMotionTestScenes.MorphModelPath).RootNode.ShouldNotBeNull();
        morph.Parent = root;
        BakeMotionTestScenes.FindRenderers(morph).ShouldHaveSingleItem().Renderer.SetBlendshapeWeightNormalized(0u, 0.5f);
        root.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);

        DeformedSurface surface = DeformedSurfaceEvaluator.Evaluate(root);
        surface.MeshCount.ShouldBe(3);
        DeformedSurfaceEvaluator.Evaluate(root, surface).ShouldBeSameAs(surface);

        long before = GC.GetAllocatedBytesForCurrentThread();
        DeformedSurfaceEvaluator.Evaluate(root, surface);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0L);
    }

    [Test]
    public void ForEachFrame_ReusesSurfaceAndRestoresPoseAndEnabledState()
    {
        SceneNode root = BakeMotionTestScenes.CreateRig(out SceneNode bone);
        BakeMotionTestScenes.AddStaticCube(bone);
        BakeMotionTestScenes.AddClip(root, BakeMotionTestScenes.CreateClip("Idle", 2.0f, endTranslationX: 4.0f));
        AnimationClipComponent other = BakeMotionTestScenes.AddClip(root, BakeMotionTestScenes.CreateClip("Wave", 2.0f, endTranslationX: -4.0f));
        Transform boneTransform = bone.GetTransformAs<Transform>(true).ShouldNotBeNull();
        boneTransform.Translation = new Vector3(0.0f, 1.0f, 0.0f);
        root.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);
        Matrix4x4 restWorld = boneTransform.WorldMatrix;
        BakeMotionChoice choice = ChooseForPrefab(root, requestedClip: null);
        choice.ClipName.ShouldBe("Idle");

        List<int> frames = [];
        DeformedSurface? firstSurface = null;
        bool otherWasInactive = true;
        float firstVertexX0 = 0.0f;
        float firstVertexXLast = 0.0f;
        BakeFrameSampler.ForEachFrame(root, choice, (frame, surface) =>
        {
            firstSurface ??= surface;
            surface.ShouldBeSameAs(firstSurface);
            frames.Add(frame);
            otherWasInactive &= !other.IsActive;
            if (frame == 0)
                firstVertexX0 = surface.Positions[0];
            firstVertexXLast = surface.Positions[0];
        });

        frames.ShouldBe(Enumerable.Range(0, choice.FrameCount));
        otherWasInactive.ShouldBeTrue();
        float lastTime = (choice.FrameCount - 1) * choice.DurationSeconds / choice.FrameCount;
        (firstVertexXLast - firstVertexX0).ShouldBe(4.0f * lastTime / 2.0f, 1e-3f);
        other.IsActive.ShouldBeTrue();
        boneTransform.Translation.ShouldBe(new Vector3(0.0f, 1.0f, 0.0f));
        boneTransform.WorldMatrix.ShouldBe(restWorld);
    }

    [Test]
    public void ForEachFrame_VisitorThrows_RestoresPoseAndEnabledState()
    {
        SceneNode root = BakeMotionTestScenes.CreateRig(out SceneNode bone);
        BakeMotionTestScenes.AddStaticCube(bone);
        BakeMotionTestScenes.AddClip(root, BakeMotionTestScenes.CreateClip("Idle", 2.0f));
        AnimationClipComponent other = BakeMotionTestScenes.AddClip(root, BakeMotionTestScenes.CreateClip("Wave", 2.0f));
        Transform boneTransform = bone.GetTransformAs<Transform>(true).ShouldNotBeNull();
        BakeMotionChoice choice = ChooseForPrefab(root, requestedClip: null);

        Should.Throw<InvalidOperationException>(() => BakeFrameSampler.ForEachFrame(root, choice, (frame, _) =>
        {
            if (frame == 3)
                throw new InvalidOperationException("Visitor failure.");
        }));

        other.IsActive.ShouldBeTrue();
        boneTransform.Translation.ShouldBe(Vector3.Zero);
    }

    [Test]
    public void ForEachFrame_PlayerOutsideRoot_PosesAndRestoresItsHierarchy()
    {
        SceneNode first = BakeMotionTestScenes.CreateRig(out SceneNode firstBone);
        BakeMotionTestScenes.AddStaticCube(firstBone);
        SceneNode second = BakeMotionTestScenes.CreateRig(out SceneNode secondBone);
        BakeMotionTestScenes.AddClip(second, BakeMotionTestScenes.CreateClip("Spin", 2.0f, endTranslationX: 2.0f));
        SceneNode follower = new(secondBone, "Follower", new Transform());
        Transform secondBoneTransform = secondBone.GetTransformAs<Transform>(true).ShouldNotBeNull();
        BakeMotionChoice choice = BakeMotionSelector.ChooseForWorld(
            new XRWorld("SamplerWorld", new XRScene("SamplerScene", first, second)),
            GaussianBakeBudget.For(GaussianBakeKind.World));
        choice.Players.Count.ShouldBe(1);
        const int sampleFrame = 8;
        float followerX = float.NaN;
        int meshCount = 0;

        BakeFrameSampler.ForEachFrame(first, choice, (frame, surface) =>
        {
            meshCount = surface.MeshCount;
            if (frame == sampleFrame)
                followerX = follower.Transform.WorldMatrix.Translation.X;
        });

        // The clip moves the bone by 1 unit each second, so the follower is at X = frame time.
        meshCount.ShouldBe(1);
        followerX.ShouldBe(sampleFrame * choice.DurationSeconds / choice.FrameCount, 1e-3f);
        secondBoneTransform.Translation.ShouldBe(Vector3.Zero);
        follower.Transform.WorldMatrix.Translation.ShouldBe(Vector3.Zero);
    }

    private static SceneNode ImportSkinnedAvatar(out BakeMotionChoice choice)
    {
        SceneNode root = GaussianBakeTestAssets.ImportPrefab(GaussianBakeTestAssets.SkinnedAvatarModelPath).RootNode.ShouldNotBeNull();
        choice = ChooseForPrefab(root, requestedClip: null);
        choice.Players.Count.ShouldBe(1);
        return root;
    }

    private static BakeMotionChoice ChooseForPrefab(SceneNode root, string? requestedClip)
    {
        List<string> warnings = [];
        BakeMotionChoice choice = BakeMotionSelector.ChooseForPrefab(root, requestedClip, GaussianBakeBudget.For(GaussianBakeKind.Avatar), warnings);
        warnings.ShouldBeEmpty();
        return choice;
    }

    /// <summary>
    /// Groups the vertices that have one influence of weight 0.999 or more by the name of that bone.
    /// </summary>
    private static Dictionary<string, List<int>> FindRigidVerticesByBone(XRMesh mesh)
    {
        XRMeshSkinningInfluenceReader.TryCreate(mesh, out XRMeshSkinningInfluenceReader reader).ShouldBeTrue();
        Dictionary<string, List<int>> groups = new(StringComparer.Ordinal);
        int[] bones = new int[reader.MaxInfluenceCount];
        float[] weights = new float[reader.MaxInfluenceCount];
        for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
        {
            int count = reader.ReadInfluences(vertex, bones, weights);
            for (int influence = 0; influence < count; influence++)
            {
                if (weights[influence] < 0.999f)
                    continue;

                string boneName = mesh.UtilizedBones[bones[influence]].tfm.SceneNode?.Name ?? string.Empty;
                if (!groups.TryGetValue(boneName, out List<int>? vertices))
                    groups[boneName] = vertices = [];
                vertices.Add(vertex);
            }
        }

        return groups;
    }

    private static Vector3 Position(DeformedSurface surface, int vertex)
        => BakeMotionTestScenes.PositionOf(surface.Positions, vertex);

    private static void AssertPosition(DeformedSurface surface, int vertex, Vector3 expected)
    {
        Vector3 actual = Position(surface, vertex);
        actual.X.ShouldBe(expected.X, PositionTolerance);
        actual.Y.ShouldBe(expected.Y, PositionTolerance);
        actual.Z.ShouldBe(expected.Z, PositionTolerance);
    }
}
