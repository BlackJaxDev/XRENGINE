using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainMeshEnvelopeTests
{
    [TestCase("fast-motion", 0.0f, 100.0f)]
    [TestCase("interpolation", -4.0f, 6.0f)]
    [TestCase("teleport", -10000.0f, 10000.0f)]
    [TestCase("sleep", 12.0f, 12.0f)]
    [TestCase("offscreen-wake", 400.0f, 0.0f)]
    public void ThickWeightedMesh_RemainsInsideTheCurrentAndPreviousParticleEnvelope(
        string scenario, float previousX, float currentX)
    {
        Transform[] bones = [new(), new()];
        XRMesh mesh = CreateMesh(bones);
        PhysicsChainMeshEnvelope.TryGetVertexInfluenceRadius(mesh, out float radius).ShouldBeTrue();
        radius.ShouldBeGreaterThan(2.0f);
        Matrix4x4[] previous =
        [
            Matrix4x4.CreateScale(1.5f, 0.75f, 2.0f) * Matrix4x4.CreateRotationZ(-0.8f)
                * Matrix4x4.CreateTranslation(previousX, 0.0f, 0.0f),
            Matrix4x4.CreateScale(0.5f, 2.0f, 1.0f) * Matrix4x4.CreateRotationY(0.4f)
                * Matrix4x4.CreateTranslation(previousX, 3.0f, 0.0f),
        ];
        Matrix4x4[] current =
        [
            Matrix4x4.CreateScale(0.75f, 2.0f, 1.5f) * Matrix4x4.CreateRotationZ(1.3f)
                * Matrix4x4.CreateTranslation(currentX, -2.0f, 1.0f),
            Matrix4x4.CreateScale(2.0f, 1.0f, 0.5f) * Matrix4x4.CreateRotationY(-1.1f)
                * Matrix4x4.CreateTranslation(currentX, 4.0f, -1.0f),
        ];
        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        float stretch = 0.0f;
        for (int bone = 0; bone < bones.Length; ++bone)
        {
            minimum = Vector3.Min(minimum, Vector3.Min(previous[bone].Translation, current[bone].Translation));
            maximum = Vector3.Max(maximum, Vector3.Max(previous[bone].Translation, current[bone].Translation));
            stretch = MathF.Max(stretch, MathF.Max(
                PhysicsChainMeshEnvelope.MaximumLinearStretch(previous[bone]),
                PhysicsChainMeshEnvelope.MaximumLinearStretch(current[bone])));
        }
        Vector3 padding = new(radius * stretch);
        AABB bounds = new(minimum - padding, maximum + padding);
        XRMeshSkinningInfluenceReader.TryCreate(mesh, out var influences).ShouldBeTrue();
        Span<int> indices = stackalloc int[influences.MaxInfluenceCount];
        Span<float> weights = stackalloc float[influences.MaxInfluenceCount];
        for (int sample = 0; sample <= 20; ++sample)
        {
            float alpha = sample / 20.0f;
            for (int vertex = 0; vertex < mesh.VertexCount; ++vertex)
            {
                Vector3 skinned = Vector3.Zero;
                int count = influences.ReadInfluences(vertex, indices, weights);
                for (int influence = 0; influence < count; ++influence)
                {
                    int bone = indices[influence];
                    Matrix4x4 pose = PhysicsChainPaletteInterpolation.Interpolate(previous[bone], current[bone], alpha);
                    Matrix4x4 bind = (mesh.BindRootMatrix ?? Matrix4x4.Identity) * mesh.UtilizedBones[bone].invBindWorldMtx;
                    skinned += Vector3.Transform(mesh.GetPosition((uint)vertex), bind * pose) * weights[influence];
                }
                bounds.ContainsPoint(skinned).ShouldBeTrue($"{scenario}, vertex {vertex}, alpha {alpha}");
            }
        }
    }

    [Test]
    public void LinearStretch_ContainsShearedVectors()
    {
        Matrix4x4 shear = new(1, 4, -2, 0, 0, 2, 3, 0, -1, 0, 1, 0, 0, 0, 0, 1);
        float stretch = PhysicsChainMeshEnvelope.MaximumLinearStretch(shear);
        for (int x = -4; x <= 4; ++x)
            for (int y = -4; y <= 4; ++y)
                for (int z = -4; z <= 4; ++z)
                {
                    Vector3 direction = new(x, y, z);
                    if (direction == Vector3.Zero)
                        continue;
                    direction = Vector3.Normalize(direction);
                    Vector3.TransformNormal(direction, shear).Length().ShouldBeLessThanOrEqualTo(stretch);
                }
    }

    [Test]
    public void Envelope_RebuildsAfterGeometryAndBoneLayoutChanges()
    {
        XRMesh mesh = CreateMesh([new(), new()]);
        PhysicsChainMeshEnvelope.TryGetVertexInfluenceRadius(mesh, out float initial).ShouldBeTrue();
        mesh.SetPosition(0, new Vector3(30, 0, 0));
        PhysicsChainMeshEnvelope.TryGetVertexInfluenceRadius(mesh, out float changed).ShouldBeTrue();
        changed.ShouldBeGreaterThan(initial);
        var bones = mesh.UtilizedBones;
        mesh.UtilizedBones =
        [
            (bones[0].tfm, Matrix4x4.CreateTranslation(100, 0, 0)),
            bones[1],
        ];
        PhysicsChainMeshEnvelope.TryGetVertexInfluenceRadius(mesh, out float rebound).ShouldBeTrue();
        rebound.ShouldBeGreaterThan(changed);
    }

    [Test]
    public void Envelope_IncludesAuthoredBlendshapeWeightsAndInvalidatesItsCache()
    {
        XRMesh mesh = CreateMesh([new(), new()], blendshape: true);
        using XRMeshRenderer renderer = new(mesh, null);
        renderer.EnsureBlendshapeBuffers().ShouldBeTrue();
        PhysicsChainMeshEnvelope.TryGetVertexInfluenceRadius(renderer, out float initial).ShouldBeTrue();
        renderer.SetBlendshapeWeightNormalized(0u, 1.0f);
        PhysicsChainMeshEnvelope.TryGetVertexInfluenceRadius(renderer, out float expanded).ShouldBeTrue();
        expanded.ShouldBeGreaterThan(initial + 7.0f);
        renderer.SetBlendshapeWeightNormalized(0u, 0.0f);
        PhysicsChainMeshEnvelope.TryGetVertexInfluenceRadius(renderer, out float restored).ShouldBeTrue();
        restored.ShouldBe(initial);
    }

    [Test]
    public void Envelope_RejectsNonFiniteVertexData()
    {
        XRMesh mesh = CreateMesh([new(), new()]);
        mesh.SetPosition(0, new Vector3(float.NaN, 0, 0));
        PhysicsChainMeshEnvelope.TryGetVertexInfluenceRadius(mesh, out _).ShouldBeFalse();
    }

    private static XRMesh CreateMesh(Transform[] bones, bool blendshape = false)
    {
        Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)> influences = new()
        {
            [bones[0]] = (0.5f, Matrix4x4.Identity),
            [bones[1]] = (0.5f, Matrix4x4.CreateTranslation(0, -3, 0)),
        };
        Vertex[] vertices =
        [
            new(new Vector3(-2, 0, -2), influences),
            new(new Vector3(2, 0, 2), influences),
            new(new Vector3(0, 6, 0), influences),
        ];
        if (blendshape)
            foreach (Vertex vertex in vertices)
                vertex.Blendshapes = [("Expand", new VertexData { Position = vertex.Position + new Vector3(10, 0, 0) })];
        XRMesh mesh = new(vertices, new List<ushort> { 0, 1, 2 });
        mesh.RebuildSkinningBuffersFromVertices(vertices);
        if (blendshape)
        {
            mesh.BlendshapeNames = ["Expand"];
            mesh.RebuildBlendshapeBuffersFromVertices(vertices);
        }
        return mesh;
    }
}
