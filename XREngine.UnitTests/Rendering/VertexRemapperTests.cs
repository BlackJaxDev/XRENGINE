using NUnit.Framework;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Scene.Transforms;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Assert = NUnit.Framework.Assert;

namespace XREngine.UnitTests;

[TestFixture]
public class VertexRemapperTests
{
    [Test]
    public void VertexEquality_StructurallyEquivalentVerticesMatch()
    {
        Transform boneA = new();
        Transform boneB = new(new Vector3(1.0f, 2.0f, 3.0f));

        Vertex left = CreateVertex(boneA, boneB, reverseWeightOrder: false);
        Vertex right = CreateVertex(boneA, boneB, reverseWeightOrder: true);

        Assert.That(left.Equals(right), Is.True);
        Assert.That(left.GetHashCode(), Is.EqualTo(right.GetHashCode()));
    }

    [Test]
    public void Remapper_DeduplicatesStructurallyEquivalentVertices()
    {
        Transform boneA = new();
        Transform boneB = new(new Vector3(1.0f, 2.0f, 3.0f));

        Vertex[] vertices =
        [
            CreateVertex(boneA, boneB, reverseWeightOrder: false),
            CreateVertex(boneA, boneB, reverseWeightOrder: true),
            CreateVertex(boneA, boneB, reverseWeightOrder: false, position: new Vector3(9.0f, 8.0f, 7.0f))
        ];

        Remapper remapper = new();
        remapper.Remap(vertices, null);

        Assert.That(remapper.ImplementationLength, Is.EqualTo(2));
        Assert.That(remapper.ImplementationTable, Is.EqualTo(new[] { 0, 2 }));
        Assert.That(remapper.RemapTable, Is.EqualTo(new[] { 0, 0, 1 }));
    }

    [Test]
    public void Remapper_DeduplicatesNullReferenceEntries()
    {
        string?[] values = [null, "alpha", null, "alpha", "beta"];

        Remapper remapper = new();
        remapper.Remap(values, null);

        Assert.That(remapper.ImplementationLength, Is.EqualTo(3));
        Assert.That(remapper.ImplementationTable, Is.EqualTo(new[] { 0, 1, 4 }));
        Assert.That(remapper.RemapTable, Is.EqualTo(new[] { 0, 1, 0, 1, 2 }));
    }

    private static Vertex CreateVertex(Transform boneA, Transform boneB, bool reverseWeightOrder, Vector3? position = null)
    {
        Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)> weights = reverseWeightOrder
            ? new Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>
            {
                [boneB] = (0.75f, Matrix4x4.CreateTranslation(4.0f, 5.0f, 6.0f)),
                [boneA] = (0.25f, Matrix4x4.Identity),
            }
            : new Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>
            {
                [boneA] = (0.25f, Matrix4x4.Identity),
                [boneB] = (0.75f, Matrix4x4.CreateTranslation(4.0f, 5.0f, 6.0f)),
            };

        return new Vertex
        {
            Position = position ?? new Vector3(1.0f, 2.0f, 3.0f),
            Normal = new Vector3(0.0f, 1.0f, 0.0f),
            Tangent = new Vector3(1.0f, 0.0f, 0.0f),
            TextureCoordinateSets = [new Vector2(0.1f, 0.2f), new Vector2(0.3f, 0.4f)],
            ColorSets = [new Vector4(0.1f, 0.2f, 0.3f, 1.0f), new Vector4(0.4f, 0.5f, 0.6f, 1.0f)],
            Weights = weights,
            Blendshapes =
            [
                ("Smile", new VertexData
                {
                    Position = new Vector3(10.0f, 11.0f, 12.0f),
                    Normal = new Vector3(0.0f, 0.0f, 1.0f),
                    Tangent = new Vector3(1.0f, 1.0f, 0.0f),
                    TextureCoordinateSets = [new Vector2(0.5f, 0.6f)],
                    ColorSets = [new Vector4(0.9f, 0.8f, 0.7f, 1.0f)],
                })
            ]
        };
    }
}