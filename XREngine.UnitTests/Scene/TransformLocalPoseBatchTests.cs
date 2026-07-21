using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

[TestFixture]
public sealed class TransformLocalPoseBatchTests
{
    [Test]
    public void SetLocalTranslationRotation_PublishesOneFrameStateChange()
    {
        Transform transform = new();
        Vector3 translation = new(1.25f, -2.5f, 3.75f);
        Quaternion rotation = Quaternion.CreateFromYawPitchRoll(0.4f, -0.2f, 0.6f);
        int notificationCount = 0;

        transform.PropertyChanged += (_, args) =>
        {
            args.PropertyName.ShouldBe(nameof(Transform.FrameState));
            notificationCount++;
        };

        transform.SetLocalTranslationRotation(translation, rotation);

        notificationCount.ShouldBe(1);
        transform.Translation.ShouldBe(translation);
        transform.Rotation.ShouldBe(rotation);
        transform.Scale.ShouldBe(Vector3.One);
    }

    [Test]
    public void HierarchyMutationBatch_EnqueuesRootOnceAndPreservesLocalAndWorldPoses()
    {
        TestRuntimeWorld world = new();
        TestTransform root = new();
        Transform child = new();
        Transform grandchild = new();
        root.AttachWorld(world);
        child.Parent = root;
        grandchild.Parent = child;
        child.SetLocalTranslationRotation(Vector3.UnitX, Quaternion.Identity);
        grandchild.SetLocalTranslationRotation(Vector3.UnitX, Quaternion.Identity);
        root.RecalculateMatrixHierarchy(true, true, ELoopType.Sequential).GetAwaiter().GetResult();
        world.ClearDirtyObjects();

        Quaternion quarterTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f);
        using (TransformHierarchyMutationBatch batch = root.BeginHierarchyMutationBatch())
        {
            batch.AddWorldRotationDelta(root, quarterTurn);
            batch.RecalculateMatrices(root);
            batch.SetWorldTranslation(child, new Vector3(0.0f, 2.0f, 0.0f));
            batch.RecalculateMatrices(child);
            batch.SetWorldTranslation(grandchild, new Vector3(0.0f, 3.0f, 0.0f));
        }

        world.DirtyObjects.Count.ShouldBe(1);
        world.DirtyObjects[0].ShouldBeSameAs(root);
        root.RecalculateMatrixHierarchy(true, true, ELoopType.Sequential).GetAwaiter().GetResult();

        AssertVectorNear(root.WorldTranslation, Vector3.Zero);
        AssertVectorNear(child.WorldTranslation, new Vector3(0.0f, 2.0f, 0.0f));
        AssertVectorNear(grandchild.WorldTranslation, new Vector3(0.0f, 3.0f, 0.0f));
        AssertVectorNear(child.Translation, new Vector3(2.0f, 0.0f, 0.0f));
        AssertVectorNear(grandchild.Translation, Vector3.UnitX);
    }

    [Test]
    public void HierarchyMutationBatch_SteadyStateAllocatesZeroBytes()
    {
        Transform root = new();
        Transform child = new() { Parent = root };
        for (int i = 0; i < 64; ++i)
        {
            using TransformHierarchyMutationBatch warmup = root.BeginHierarchyMutationBatch();
            warmup.SetWorldTranslation(child, new Vector3(i, 0.0f, 0.0f));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; ++i)
        {
            using TransformHierarchyMutationBatch batch = root.BeginHierarchyMutationBatch();
            batch.SetWorldTranslation(child, new Vector3(i, 0.0f, 0.0f));
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0L);
    }

    private static void AssertVectorNear(Vector3 actual, Vector3 expected)
    {
        actual.X.ShouldBe(expected.X, 0.0001f);
        actual.Y.ShouldBe(expected.Y, 0.0001f);
        actual.Z.ShouldBe(expected.Z, 0.0001f);
    }

    private sealed class TestTransform : Transform
    {
        public void AttachWorld(IRuntimeWorldContext world)
            => World = world;
    }

    private sealed class TestRuntimeWorld : IRuntimeWorldContext
    {
        public bool IsPlaySessionActive => true;
        public List<RuntimeWorldObjectBase> DirtyObjects { get; } = [];

        public void RegisterTick(ETickGroup group, int order, WorldTick tick) { }

        public void UnregisterTick(ETickGroup group, int order, WorldTick tick) { }

        public void AddDirtyRuntimeObject(RuntimeWorldObjectBase worldObject)
            => DirtyObjects.Add(worldObject);

        public void EnqueueRuntimeWorldMatrixChange(RuntimeWorldObjectBase worldObject, Matrix4x4 worldMatrix) { }

        public void ClearDirtyObjects()
            => DirtyObjects.Clear();
    }
}
