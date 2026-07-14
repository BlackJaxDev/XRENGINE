using NUnit.Framework;
using Shouldly;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Physics;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Physics.Jolt;

namespace XREngine.UnitTests.Physics;

[TestFixture]
[NonParallelizable]
public unsafe class JoltQueryParityTests
{
    private const int IncludedLayer = 5;
    private const int ExcludedLayer = 2;
    private JoltScene? _scene;

    [SetUp]
    public void SetUp()
    {
        _scene = new JoltScene();
        _scene.Initialize();
    }

    [TearDown]
    public void TearDown()
    {
        _scene?.Destroy();
        _scene = null;
    }

    private (JoltStaticRigidBody Body, StaticRigidBodyComponent Component) CreateStaticSphere(
        Vector3 position,
        float radius = 0.5f,
        int layerBit = IncludedLayer)
    {
        JoltStaticRigidBody? body = _scene!.CreateStaticRigidBody(
            new IPhysicsGeometry.Sphere(radius),
            (position, Quaternion.Identity),
            Vector3.Zero,
            Quaternion.Identity,
            new LayerMask(1 << layerBit));

        body.ShouldNotBeNull();
        StaticRigidBodyComponent component = new();
        body.OwningComponent = component;
        return (body, component);
    }

    private (JoltDynamicRigidBody Body, DynamicRigidBodyComponent Component) CreateDynamicSphere(
        Vector3 position,
        float radius = 0.5f,
        int layerBit = IncludedLayer)
    {
        JoltDynamicRigidBody? body = _scene!.CreateDynamicRigidBody(
            new IPhysicsGeometry.Sphere(radius),
            (position, Quaternion.Identity),
            Vector3.Zero,
            Quaternion.Identity,
            new LayerMask(1 << layerBit));

        body.ShouldNotBeNull();
        DynamicRigidBodyComponent component = new();
        body.OwningComponent = component;
        return (body, component);
    }

    [Test]
    public void RaycastMultiple_UsesFullSegmentAndReturnsDistanceOrderedWorldSpaceHits()
    {
        (_, StaticRigidBodyComponent farComponent) = CreateStaticSphere(new Vector3(6.0f, 0.0f, 0.0f));
        (_, StaticRigidBodyComponent nearComponent) = CreateStaticSphere(new Vector3(3.0f, 0.0f, 0.0f));
        Segment ray = new(Vector3.Zero, new Vector3(10.0f, 0.0f, 0.0f));
        SortedDictionary<float, List<(XRComponent? item, object? data)>> results = [];

        bool hit = _scene!.RaycastMultiple(ray, new LayerMask(1 << IncludedLayer), null, results);

        hit.ShouldBeTrue();
        var ordered = results
            .SelectMany(pair => pair.Value.Select(entry =>
                (Distance: pair.Key, Component: entry.item, Hit: (RaycastHit)entry.data!)))
            .ToArray();
        ordered.Length.ShouldBe(2);
        ordered[0].Component.ShouldBeSameAs(nearComponent);
        ordered[1].Component.ShouldBeSameAs(farComponent);
        ordered[0].Distance.ShouldBe(2.5f, 0.01f);
        ordered[1].Distance.ShouldBe(5.5f, 0.01f);
        ordered[0].Hit.Distance.ShouldBe(ordered[0].Distance, 0.0001f);
        ordered[0].Hit.Position.X.ShouldBe(2.5f, 0.01f);
        Vector3.Distance(ordered[0].Hit.Normal, -Vector3.UnitX).ShouldBeLessThan(0.01f);
        ordered[0].Hit.FaceIndex.ShouldBe(uint.MaxValue);
        ordered[0].Hit.UV.ShouldBe(Vector2.Zero);
    }

    [Test]
    public void RaycastAnyAndSingle_FilterBeforeSelectingNearestCandidate()
    {
        CreateStaticSphere(new Vector3(1.5f, 0.0f, 0.0f), layerBit: ExcludedLayer);
        CreateStaticSphere(new Vector3(3.0f, 0.0f, 0.0f));
        (_, DynamicRigidBodyComponent dynamicComponent) = CreateDynamicSphere(new Vector3(5.0f, 0.0f, 0.0f));
        Segment ray = new(Vector3.Zero, new Vector3(8.0f, 0.0f, 0.0f));
        PhysicsQueryFilter dynamicOnly = new(actorTypes: PhysicsQueryActorTypes.Dynamic);

        bool anyHit = _scene!.RaycastAny(
            ray,
            new LayerMask(1 << IncludedLayer),
            dynamicOnly,
            out uint faceIndex);

        anyHit.ShouldBeTrue();
        faceIndex.ShouldBe(uint.MaxValue);

        int callbackCount = 0;
        SortedDictionary<float, List<(XRComponent? item, object? data)>> results = [];
        bool singleHit = _scene.RaycastSingleAsync(
            ray,
            new LayerMask(1 << IncludedLayer),
            dynamicOnly,
            results,
            _ => callbackCount++);

        singleHit.ShouldBeTrue();
        callbackCount.ShouldBe(1);
        results.Count.ShouldBe(1);
        results.Keys.Single().ShouldBe(4.5f, 0.01f);
        results.Values.Single().Single().item.ShouldBeSameAs(dynamicComponent);

        callbackCount = 0;
        results.Clear();
        bool missedSingle = _scene.RaycastSingleAsync(
            new Segment(Vector3.Zero, new Vector3(4.0f, 0.0f, 0.0f)),
            new LayerMask(1 << IncludedLayer),
            dynamicOnly,
            results,
            _ => callbackCount++);

        missedSingle.ShouldBeFalse();
        callbackCount.ShouldBe(1);
        results.ShouldBeEmpty();

        _scene.RaycastAny(
            ray,
            new LayerMask(1 << ExcludedLayer),
            dynamicOnly,
            out _).ShouldBeFalse();
    }

    [Test]
    public void RaycastHitDetail_PopulatesOnlyRequestedFieldsAndReturnsZeroUvForNonMeshShapes()
    {
        CreateStaticSphere(new Vector3(3.0f, 0.0f, 0.0f));
        Segment ray = new(Vector3.Zero, new Vector3(6.0f, 0.0f, 0.0f));
        SortedDictionary<float, List<(XRComponent? item, object? data)>> normalResults = [];
        PhysicsQueryFilter normalOnly = new(hitDetail: PhysicsQueryHitDetail.Normal);

        _scene!.RaycastMultiple(ray, new LayerMask(1 << IncludedLayer), normalOnly, normalResults).ShouldBeTrue();

        RaycastHit normalHit = (RaycastHit)normalResults.Values.Single().Single().data!;
        normalHit.Position.ShouldBe(Vector3.Zero);
        Vector3.Distance(normalHit.Normal, -Vector3.UnitX).ShouldBeLessThan(0.01f);
        normalHit.FaceIndex.ShouldBe(uint.MaxValue);

        PhysicsQueryFilter uv = new(hitDetail: PhysicsQueryHitDetail.UV);
        SortedDictionary<float, List<(XRComponent? item, object? data)>> uvResults = [];
        _scene.RaycastMultiple(ray, new LayerMask(1 << IncludedLayer), uv, uvResults).ShouldBeTrue();
        ((RaycastHit)uvResults.Values.Single().Single().data!).UV.ShouldBe(Vector2.Zero);
    }

    [Test]
    public void MeshQueries_ReturnAuthoredSourceFaceIndexAcrossRaySweepAndOverlap()
    {
        const uint sourceFaceIndex = 37;
        Vector3[] vertices =
        [
            new(0.0f, -2.0f, -2.0f),
            new(0.0f, 0.0f, 2.0f),
            new(0.0f, 2.0f, -2.0f),
        ];
        uint[] indices = [0, 1, 2];
        uint[] sourceFaceIndices = [sourceFaceIndex];
        PhysicsTriangleMeshGeometry geometry = new(vertices, indices)
        {
            SourceFaceIndices = sourceFaceIndices,
        };

        JoltStaticRigidBody? body = _scene!.CreateStaticRigidBody(
            geometry,
            (new Vector3(3.0f, 0.0f, 0.0f), Quaternion.Identity),
            Vector3.Zero,
            Quaternion.Identity,
            new LayerMask(1 << IncludedLayer));
        body.ShouldNotBeNull();
        body.OwningComponent = new StaticRigidBodyComponent();

        Segment ray = new(Vector3.Zero, new Vector3(6.0f, 0.0f, 0.0f));
        _scene.RaycastAny(
            ray,
            new LayerMask(1 << IncludedLayer),
            new PhysicsQueryFilter(hitDetail: PhysicsQueryHitDetail.FaceIndex),
            out uint rayFaceIndex).ShouldBeTrue();
        rayFaceIndex.ShouldBe(sourceFaceIndex);

        SortedDictionary<float, List<(XRComponent? item, object? data)>> rayResults = [];
        _scene.RaycastMultiple(
            ray,
            new LayerMask(1 << IncludedLayer),
            new PhysicsQueryFilter(
                hitDetail: PhysicsQueryHitDetail.FaceIndex | PhysicsQueryHitDetail.UV),
            rayResults).ShouldBeTrue();
        RaycastHit meshHit = (RaycastHit)rayResults.Values.Single().Single().data!;
        meshHit.FaceIndex.ShouldBe(sourceFaceIndex);
        Vector2.Distance(meshHit.UV, new Vector2(0.5f, 0.25f)).ShouldBeLessThan(0.001f);

        SortedDictionary<float, List<(XRComponent? item, object? data)>> sweepResults = [];
        _scene.SweepSingle(
            new IPhysicsGeometry.Sphere(0.25f),
            (Vector3.Zero, Quaternion.Identity),
            Vector3.UnitX,
            6.0f,
            new LayerMask(1 << IncludedLayer),
            new PhysicsQueryFilter(hitDetail: PhysicsQueryHitDetail.FaceIndex),
            sweepResults).ShouldBeTrue();
        ((SweepHit)sweepResults.Values.Single().Single().data!).FaceIndex.ShouldBe(sourceFaceIndex);

        SortedDictionary<float, List<(XRComponent? item, object? data)>> overlapResults = [];
        _scene.OverlapAny(
            new IPhysicsGeometry.Sphere(0.25f),
            (new Vector3(2.9f, 0.0f, 0.0f), Quaternion.Identity),
            new LayerMask(1 << IncludedLayer),
            new PhysicsQueryFilter(hitDetail: PhysicsQueryHitDetail.FaceIndex),
            overlapResults).ShouldBeTrue();
        ((OverlapHit)overlapResults.Values.Single().Single().data!).FaceIndex.ShouldBe(sourceFaceIndex);
    }

    [Test]
    public void SweepVariants_AreOrderedUseWorldSpaceContactsAndHonorActorFiltering()
    {
        (_, DynamicRigidBodyComponent farComponent) = CreateDynamicSphere(new Vector3(6.0f, 0.0f, 0.0f));
        (_, StaticRigidBodyComponent nearComponent) = CreateStaticSphere(new Vector3(3.0f, 0.0f, 0.0f));
        IPhysicsGeometry query = new IPhysicsGeometry.Sphere(0.25f);
        SortedDictionary<float, List<(XRComponent? item, object? data)>> multiple = [];

        bool multipleHit = _scene!.SweepMultiple(
            query,
            (Vector3.Zero, Quaternion.Identity),
            Vector3.UnitX,
            10.0f,
            new LayerMask(1 << IncludedLayer),
            null,
            multiple);

        multipleHit.ShouldBeTrue();
        var ordered = multiple
            .SelectMany(pair => pair.Value.Select(entry =>
                (Distance: pair.Key, Component: entry.item, Hit: (SweepHit)entry.data!)))
            .ToArray();
        ordered.Length.ShouldBe(2);
        ordered[0].Component.ShouldBeSameAs(nearComponent);
        ordered[1].Component.ShouldBeSameAs(farComponent);
        ordered[0].Distance.ShouldBe(2.25f, 0.02f);
        ordered[1].Distance.ShouldBe(5.25f, 0.02f);
        ordered[0].Hit.Position.X.ShouldBe(2.5f, 0.02f);
        Vector3.Distance(ordered[0].Hit.Normal, -Vector3.UnitX).ShouldBeLessThan(0.02f);
        ordered[0].Hit.FaceIndex.ShouldBe(uint.MaxValue);

        PhysicsQueryFilter dynamicOnly = new(actorTypes: PhysicsQueryActorTypes.Dynamic);
        SortedDictionary<float, List<(XRComponent? item, object? data)>> single = [];
        _scene.SweepSingle(
            query,
            (Vector3.Zero, Quaternion.Identity),
            Vector3.UnitX,
            10.0f,
            new LayerMask(1 << IncludedLayer),
            dynamicOnly,
            single).ShouldBeTrue();
        single.Keys.Single().ShouldBe(5.25f, 0.02f);
        single.Values.Single().Single().item.ShouldBeSameAs(farComponent);

        SortedDictionary<float, List<(XRComponent? item, object? data)>> translated = [];
        _scene.SweepSingle(
            query,
            (Vector3.UnitX, Quaternion.Identity),
            Vector3.UnitX,
            10.0f,
            new LayerMask(1 << IncludedLayer),
            null,
            translated).ShouldBeTrue();
        translated.Keys.Single().ShouldBe(1.25f, 0.02f);
        translated.Values.Single().Single().item.ShouldBeSameAs(nearComponent);

        _scene.SweepAny(
            query,
            (Vector3.Zero, Quaternion.Identity),
            Vector3.UnitX,
            10.0f,
            new LayerMask(1 << ExcludedLayer),
            null,
            out _).ShouldBeFalse();
    }

    [Test]
    public void SweepInflation_ExpandsSupportedQueryGeometryExactly()
    {
        CreateStaticSphere(new Vector3(3.0f, 0.0f, 0.0f));
        PhysicsQueryFilter inflated = new(sweepInflation: 0.5f);
        SortedDictionary<float, List<(XRComponent? item, object? data)>> results = [];

        _scene!.SweepSingle(
            new IPhysicsGeometry.Sphere(0.25f),
            (Vector3.Zero, Quaternion.Identity),
            Vector3.UnitX,
            10.0f,
            new LayerMask(1 << IncludedLayer),
            inflated,
            results).ShouldBeTrue();

        results.Keys.Single().ShouldBe(1.75f, 0.02f);
    }

    [Test]
    public void OverlapVariants_ReturnOneOrAllDeterministicallyAndHonorActorFiltering()
    {
        CreateStaticSphere(Vector3.Zero, layerBit: ExcludedLayer);
        (_, StaticRigidBodyComponent firstComponent) = CreateStaticSphere(new Vector3(0.25f, 0.0f, 0.0f));
        (_, DynamicRigidBodyComponent secondComponent) = CreateDynamicSphere(new Vector3(-0.25f, 0.0f, 0.0f));
        IPhysicsGeometry query = new IPhysicsGeometry.Sphere(0.5f);

        SortedDictionary<float, List<(XRComponent? item, object? data)>> all = [];
        _scene!.OverlapMultiple(
            query,
            (Vector3.Zero, Quaternion.Identity),
            new LayerMask(1 << IncludedLayer),
            null,
            all).ShouldBeTrue();

        List<(XRComponent? item, object? data)> allHits = all[0.0f];
        allHits.Count.ShouldBe(2);
        allHits[0].item.ShouldBeSameAs(firstComponent);
        allHits[1].item.ShouldBeSameAs(secondComponent);
        ((OverlapHit)allHits[0].data!).FaceIndex.ShouldBe(uint.MaxValue);

        SortedDictionary<float, List<(XRComponent? item, object? data)>> any = [];
        _scene.OverlapAny(
            query,
            (Vector3.Zero, Quaternion.Identity),
            new LayerMask(1 << IncludedLayer),
            null,
            any).ShouldBeTrue();
        any[0.0f].Count.ShouldBe(1);
        any[0.0f][0].item.ShouldBeSameAs(firstComponent);

        SortedDictionary<float, List<(XRComponent? item, object? data)>> dynamicOnly = [];
        _scene.OverlapMultiple(
            query,
            (Vector3.Zero, Quaternion.Identity),
            new LayerMask(1 << IncludedLayer),
            new PhysicsQueryFilter(actorTypes: PhysicsQueryActorTypes.Dynamic),
            dynamicOnly).ShouldBeTrue();
        dynamicOnly[0.0f].Count.ShouldBe(1);
        dynamicOnly[0.0f][0].item.ShouldBeSameAs(secondComponent);
    }

    [Test]
    public void Overlap_UsesTranslatedAndRotatedQueryPose()
    {
        CreateStaticSphere(new Vector3(4.0f, 0.8f, 0.0f), radius: 0.25f);
        IPhysicsGeometry query = new IPhysicsGeometry.Box(new Vector3(1.0f, 0.1f, 0.1f));
        LayerMask layerMask = new(1 << IncludedLayer);

        _scene!.OverlapAny(
            query,
            (new Vector3(4.0f, 0.0f, 0.0f), Quaternion.Identity),
            layerMask,
            null,
            []).ShouldBeFalse();

        Quaternion rotatedAroundZ = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f);
        _scene.OverlapAny(
            query,
            (new Vector3(4.0f, 0.0f, 0.0f), rotatedAroundZ),
            layerMask,
            null,
            []).ShouldBeTrue();
    }
}
