using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Components.Mesh.Shapes;
using XREngine.Data.Geometry;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

[TestFixture]
public sealed class ShapeMeshComponentYamlSerializationTests
{
    [Test]
    public void YamlSerializer_RoundTrips_BoxMesh_WithoutDerivedShapeAlias()
    {
        SceneNode original = new("LitBox", new Transform());
        BoxMeshComponent box = original.AddComponent<BoxMeshComponent>()!;
        box.Box = new AABB(
            new Vector3(-3.0f, -0.25f, -2.0f),
            new Vector3(3.0f, 0.25f, 2.0f));

        string yaml = AssetManager.Serializer.Serialize(original);

        yaml.ShouldContain("Box:");
        yaml.ShouldNotContain("Shape:");

        SceneNode cloneNode = AssetManager.Deserializer
            .Deserialize<SceneNode>(yaml)
            .ShouldNotBeNull();
        BoxMeshComponent clone = cloneNode
            .GetComponent<BoxMeshComponent>()
            .ShouldNotBeNull();

        clone.Box.Min.ShouldBe(box.Box.Min);
        clone.Box.Max.ShouldBe(box.Box.Max);
        clone.Shape.ShouldBeOfType<AABB>();
    }
}
