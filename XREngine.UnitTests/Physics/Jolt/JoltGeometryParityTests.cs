using JoltPhysicsSharp;
using NUnit.Framework;
using Shouldly;
using System.Numerics;
using XREngine;
using XREngine.Components;
using XREngine.Components.Physics;
using XREngine.Data.Geometry;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Physics.Jolt;

namespace XREngine.UnitTests.Physics;

[TestFixture]
[NonParallelizable]
public sealed class JoltGeometryParityTests
{
    private const int IncludedLayer = 4;
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

    [Test]
    public void ConvexHull_BakesPhysxScaleRotationExactly()
    {
        PhysicsConvexHullGeometry geometry = new(
        [
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.UnitY,
            Vector3.UnitZ,
        ])
        {
            Scale = new Vector3(2.0f, 3.0f, 4.0f),
            ScaleRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f),
        };

        using Shape shape = JoltShapeFactory.CreateShape(geometry);
        ConvexHullShape hull = shape.ShouldBeOfType<ConvexHullShape>();
        Vector3[] expected =
        [
            Vector3.Zero,
            new Vector3(3.0f, 0.0f, 0.0f),
            new Vector3(0.0f, 2.0f, 0.0f),
            new Vector3(0.0f, 0.0f, 4.0f),
        ];

        hull.GetNumPoints().ShouldBe(4u);
        for (uint pointIndex = 0; pointIndex < hull.GetNumPoints(); pointIndex++)
        {
            Vector3 point = hull.GetPoint(pointIndex) + hull.CenterOfMass;
            expected.Any(candidate => Vector3.Distance(candidate, point) < 0.0001f)
                .ShouldBeTrue($"Unexpected transformed hull point {point}.");
        }
    }

    [Test]
    public void TriangleMesh_BakesPhysxScaleRotationExactly()
    {
        PhysicsTriangleMeshGeometry geometry = new(
        [
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.UnitY,
        ],
        [0u, 1u, 2u])
        {
            Scale = new Vector3(2.0f, 3.0f, 4.0f),
            ScaleRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f),
        };

        using Shape shape = JoltShapeFactory.CreateShape(geometry);
        MeshShape mesh = shape.ShouldBeOfType<MeshShape>();
        Vector3 authoredMaximum = mesh.LocalBounds.Max + mesh.CenterOfMass;
        authoredMaximum.X.ShouldBe(3.0f, 0.0001f);
        authoredMaximum.Y.ShouldBe(2.0f, 0.0001f);
        authoredMaximum.Z.ShouldBe(0.0f, 0.0001f);
    }

    [Test]
    public void HeightField_PreservesPhysxRowColumnAxesAndSignedSampleScale()
    {
        short[] samples = new short[16];
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
                samples[row * 4 + column] = (short)(row * 10 + column);
        }

        PhysicsHeightFieldGeometry geometry = new(4, 4, samples)
        {
            HeightScale = 0.5f,
            RowScale = 2.0f,
            ColumnScale = 3.0f,
        };

        using Shape shape = JoltShapeFactory.CreateShape(geometry);
        HeightFieldShape heightField = shape.ShouldBeOfType<HeightFieldShape>();
        Vector3.Distance(
            heightField.GetPosition(2, 1),
            new Vector3(4.0f, 10.5f, 3.0f)).ShouldBeLessThan(0.01f);
    }

    [Test]
    public void HeightField_UsesMeshWhenRectangularTopologyOrHolesNeedPreservation()
    {
        PhysicsHeightFieldCell[] cells =
        [
            new(TessellatedDiagonal: false, LowerTriangleHole: true),
            new(TessellatedDiagonal: true),
        ];
        PhysicsHeightFieldGeometry geometry = new(
            rowCount: 2,
            columnCount: 3,
            samples: new short[] { 0, 1, 2, 3, 4, 5 },
            cells);

        using Shape shape = JoltShapeFactory.CreateShape(geometry);
        shape.ShouldBeOfType<MeshShape>();
    }

    [Test]
    public void CompoundBody_PreservesLocalPosesDisabledEntriesAndNestedMeshFaceIds()
    {
        PhysicsTriangleMeshGeometry mesh = new(
        [
            new Vector3(0.0f, -2.0f, -2.0f),
            new Vector3(0.0f, 0.0f, 2.0f),
            new Vector3(0.0f, 2.0f, -2.0f),
        ],
        [0u, 1u, 2u]);
        List<PhysicsColliderShape> shapes =
        [
            new()
            {
                Name = "Mesh",
                Geometry = mesh,
                LocalPosition = new Vector3(3.0f, 0.0f, 0.0f),
            },
            new()
            {
                Name = "Box",
                Geometry = new IPhysicsGeometry.Box(new Vector3(0.25f, 1.0f, 0.5f)),
                LocalPosition = new Vector3(6.0f, 0.0f, 0.0f),
                LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f),
            },
            new()
            {
                Name = "Disabled",
                Enabled = false,
                Geometry = new IPhysicsGeometry.Sphere(10.0f),
                LocalPosition = new Vector3(20.0f, 0.0f, 0.0f),
            },
        ];
        PhysicsRigidBodyCreateInfo createInfo = new(
            shapes,
            FallbackGeometry: null,
            RuntimeMaterial: null,
            MaterialDefinition: null,
            Pose: (Vector3.Zero, Quaternion.Identity),
            FallbackShapeOffsetTranslation: Vector3.Zero,
            FallbackShapeOffsetRotation: Quaternion.Identity,
            Density: 1.0f,
            LayerMask: new LayerMask(1 << IncludedLayer));

        JoltStaticRigidBody? body = _scene!.CreateStaticRigidBody(createInfo);

        body.ShouldNotBeNull();
        body!.OwningComponent = new StaticRigidBodyComponent();
        Vector3.Distance(body.Transform.position, Vector3.Zero).ShouldBeLessThan(0.0001f);
        Shape? root = _scene.PhysicsSystem!.BodyInterface.GetShape(body.BodyID);
        root.ShouldNotBeNull();
        StaticCompoundShape compound = root.ShouldBeOfType<StaticCompoundShape>();
        float authoredMaximumX = compound.LocalBounds.Max.X + compound.CenterOfMass.X;
        authoredMaximumX.ShouldBeGreaterThan(6.9f);
        authoredMaximumX.ShouldBeLessThan(7.1f);

        Segment ray = new(Vector3.Zero, new Vector3(8.0f, 0.0f, 0.0f));
        _scene.RaycastAny(
            ray,
            new LayerMask(1 << IncludedLayer),
            new PhysicsQueryFilter(hitDetail: PhysicsQueryHitDetail.FaceIndex),
            out uint faceIndex).ShouldBeTrue();
        faceIndex.ShouldBe(0u);
    }

    [Test]
    public void ColliderAsset_ClonesCpuGeometryForIndependentRuntimeMutation()
    {
        PhysicsConvexHullGeometry geometry = new(
        [
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.UnitY,
            Vector3.UnitZ,
        ]);
        PhysicsColliderAsset asset = new("Reusable Hull")
        {
            Shapes =
            [
                new PhysicsColliderShape
                {
                    Name = "Hull",
                    Geometry = geometry,
                    Material = new PhysicsMaterialDefinition { Restitution = 0.6f },
                    LocalPosition = Vector3.UnitX,
                },
            ],
        };

        List<PhysicsColliderShape> first = asset.CreateRuntimeShapes();
        List<PhysicsColliderShape> second = asset.CreateRuntimeShapes();

        first.ShouldHaveSingleItem();
        second.ShouldHaveSingleItem();
        first[0].ShouldNotBeSameAs(second[0]);
        PhysicsConvexHullGeometry firstGeometry = first[0].Geometry.ShouldBeOfType<PhysicsConvexHullGeometry>();
        PhysicsConvexHullGeometry secondGeometry = second[0].Geometry.ShouldBeOfType<PhysicsConvexHullGeometry>();
        firstGeometry.ShouldNotBeSameAs(secondGeometry);
        first[0].Material.ShouldNotBeSameAs(second[0].Material);
        firstGeometry.Vertices[0] = new Vector3(99.0f);
        first[0].Material!.Restitution = 0.1f;
        secondGeometry.Vertices[0].ShouldBe(Vector3.Zero);
        second[0].Material!.Restitution.ShouldBe(0.6f);
        geometry.Vertices[0].ShouldBe(Vector3.Zero);
    }

    [Test]
    public void ColliderAsset_YamlRoundTripPreservesBackendNeutralGeometry()
    {
        PhysicsColliderAsset original = new("Serialized Collider")
        {
            Shapes =
            [
                new PhysicsColliderShape
                {
                    Name = "Convex",
                    LocalPosition = new Vector3(1.0f, 2.0f, 3.0f),
                    Geometry = new PhysicsConvexHullGeometry(
                    [
                        Vector3.Zero,
                        Vector3.UnitX,
                        Vector3.UnitY,
                        Vector3.UnitZ,
                    ])
                    {
                        Scale = new Vector3(2.0f, 3.0f, 4.0f),
                    },
                },
                new PhysicsColliderShape
                {
                    Name = "Mesh",
                    Geometry = new PhysicsTriangleMeshGeometry(
                    [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                    [0u, 1u, 2u])
                    {
                        DoubleSided = true,
                    },
                },
                new PhysicsColliderShape
                {
                    Name = "Height Field",
                    Geometry = new PhysicsHeightFieldGeometry(
                        2,
                        2,
                        [0, 1, 2, 3],
                        [new PhysicsHeightFieldCell(TessellatedDiagonal: false, LowerTriangleHole: true)])
                    {
                        HeightScale = 0.25f,
                    },
                },
            ],
        };

        string yaml = AssetManager.Serializer.Serialize(original);
        PhysicsColliderAsset clone = AssetManager.Deserializer.Deserialize<PhysicsColliderAsset>(yaml);

        clone.Name.ShouldBe(original.Name);
        clone.Shapes.Count.ShouldBe(3);
        PhysicsColliderShape convexShape = clone.Shapes[0];
        convexShape.Name.ShouldBe("Convex");
        convexShape.LocalPosition.ShouldBe(new Vector3(1.0f, 2.0f, 3.0f));
        PhysicsConvexHullGeometry convex = convexShape.Geometry.ShouldBeOfType<PhysicsConvexHullGeometry>();
        convex.Vertices.Length.ShouldBe(4);
        convex.Scale.ShouldBe(new Vector3(2.0f, 3.0f, 4.0f));
        PhysicsTriangleMeshGeometry mesh = clone.Shapes[1].Geometry.ShouldBeOfType<PhysicsTriangleMeshGeometry>();
        mesh.Indices.ShouldBe([0u, 1u, 2u]);
        mesh.DoubleSided.ShouldBeTrue();
        PhysicsHeightFieldGeometry heightField = clone.Shapes[2].Geometry.ShouldBeOfType<PhysicsHeightFieldGeometry>();
        heightField.Samples.ShouldBe(new short[] { 0, 1, 2, 3 });
        heightField.HeightScale.ShouldBe(0.25f);
        heightField.Cells.ShouldHaveSingleItem().ShouldBe(
            new PhysicsHeightFieldCell(TessellatedDiagonal: false, LowerTriangleHole: true));
    }

    [Test]
    public void ReplaceCollisionShapes_KeepsBodyIdAndRefreshesCompoundFaceMetadata()
    {
        PhysicsRigidBodyCreateInfo initial = new(
            [new PhysicsColliderShape { Geometry = new IPhysicsGeometry.Sphere(0.5f) }],
            FallbackGeometry: null,
            RuntimeMaterial: null,
            MaterialDefinition: null,
            Pose: (Vector3.Zero, Quaternion.Identity),
            FallbackShapeOffsetTranslation: Vector3.Zero,
            FallbackShapeOffsetRotation: Quaternion.Identity,
            Density: 1.0f,
            LayerMask: new LayerMask(1 << IncludedLayer));
        JoltStaticRigidBody? body = _scene!.CreateStaticRigidBody(initial);
        body.ShouldNotBeNull();
        body!.OwningComponent = new StaticRigidBodyComponent();
        BodyID originalBodyID = body.BodyID;

        PhysicsTriangleMeshGeometry mesh = new(
        [
            new Vector3(0.0f, -2.0f, -2.0f),
            new Vector3(0.0f, 0.0f, 2.0f),
            new Vector3(0.0f, 2.0f, -2.0f),
        ],
        [0u, 1u, 2u]);
        PhysicsRigidBodyCreateInfo replacement = initial with
        {
            ColliderShapes =
            [
                new PhysicsColliderShape
                {
                    Geometry = mesh,
                    LocalPosition = new Vector3(3.0f, 0.0f, 0.0f),
                },
                new PhysicsColliderShape
                {
                    Geometry = new IPhysicsGeometry.Box(new Vector3(0.5f)),
                    LocalPosition = new Vector3(6.0f, 0.0f, 0.0f),
                },
            ],
        };

        _scene.TryReplaceCollisionShapes(body, replacement).ShouldBeTrue();

        body.BodyID.ShouldBe(originalBodyID);
        _scene.PhysicsSystem!.BodyInterface.GetShape(body.BodyID).ShouldBeOfType<StaticCompoundShape>();
        _scene.RaycastAny(
            new Segment(Vector3.Zero, new Vector3(8.0f, 0.0f, 0.0f)),
            new LayerMask(1 << IncludedLayer),
            new PhysicsQueryFilter(hitDetail: PhysicsQueryHitDetail.FaceIndex),
            out uint faceIndex).ShouldBeTrue();
        faceIndex.ShouldBe(0u);

        SortedDictionary<float, List<(XRComponent? item, object? data)>> rayResults = [];
        _scene.RaycastMultiple(
            new Segment(Vector3.Zero, new Vector3(8.0f, 0.0f, 0.0f)),
            new LayerMask(1 << IncludedLayer),
            new PhysicsQueryFilter(
                hitDetail: PhysicsQueryHitDetail.FaceIndex | PhysicsQueryHitDetail.UV),
            rayResults).ShouldBeTrue();
        RaycastHit rayHit = (RaycastHit)rayResults.Values.First().Single().data!;
        rayHit.FaceIndex.ShouldBe(0u);
        Vector2.Distance(rayHit.UV, new Vector2(0.5f, 0.25f)).ShouldBeLessThan(0.001f);
    }

    [Test]
    public void ReplaceCollisionShapes_PreservesSleepingDynamicBodyState()
    {
        PhysicsRigidBodyCreateInfo createInfo = new(
            [new PhysicsColliderShape { Geometry = new IPhysicsGeometry.Sphere(0.5f) }],
            FallbackGeometry: null,
            RuntimeMaterial: null,
            MaterialDefinition: null,
            Pose: (Vector3.Zero, Quaternion.Identity),
            FallbackShapeOffsetTranslation: Vector3.Zero,
            FallbackShapeOffsetRotation: Quaternion.Identity,
            Density: 1.0f,
            LayerMask: new LayerMask(1 << IncludedLayer));
        JoltDynamicRigidBody? body = _scene!.CreateDynamicRigidBody(createInfo);
        body.ShouldNotBeNull();
        BodyID originalBodyID = body!.BodyID;
        _scene.PhysicsSystem!.BodyInterface.DeactivateBody(body.BodyID);
        body.IsSleeping.ShouldBeTrue();

        PhysicsRigidBodyCreateInfo replacement = createInfo with
        {
            ColliderShapes =
            [
                new PhysicsColliderShape { Geometry = new IPhysicsGeometry.Box(new Vector3(0.5f)) },
                new PhysicsColliderShape
                {
                    Geometry = new IPhysicsGeometry.Sphere(0.25f),
                    LocalPosition = Vector3.UnitY,
                },
            ],
        };

        _scene.TryReplaceCollisionShapes(body, replacement).ShouldBeTrue();

        body.BodyID.ShouldBe(originalBodyID);
        body.IsSleeping.ShouldBeTrue();
    }

    [Test]
    public void MeshScale_RejectsZeroAndNonFiniteComponents()
    {
        Vector3[] vertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        uint[] indices = [0, 1, 2];

        Should.Throw<ArgumentOutOfRangeException>(() =>
            JoltShapeFactory.CreateTriangleMesh(
                vertices,
                indices,
                new Vector3(1.0f, 0.0f, 1.0f),
                Quaternion.Identity));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            JoltShapeFactory.CreateTriangleMesh(
                vertices,
                indices,
                new Vector3(1.0f, float.NaN, 1.0f),
                Quaternion.Identity));
    }
}
