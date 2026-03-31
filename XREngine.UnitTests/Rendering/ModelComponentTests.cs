using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ModelComponentTests
{
    private static XRMesh CreateSkinnedMesh(Transform bone, string meshName)
    {
        XRMesh mesh = XRMesh.CreateTriangles(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);
        mesh.Name = meshName;
        mesh.UtilizedBones =
        [
            (bone, Matrix4x4.Identity)
        ];

        return mesh;
    }

    private static Model CreateSkinnedModel(Transform bone, string meshName, params float[] maxVisibleDistances)
    {
        float[] distances = maxVisibleDistances.Length > 0 ? maxVisibleDistances : [0.0f];
        XRMaterial material = new();
        SubMeshLOD[] lods = new SubMeshLOD[distances.Length];
        for (int i = 0; i < distances.Length; i++)
        {
            XRMesh mesh = CreateSkinnedMesh(bone, $"{meshName}_LOD{i}");
            lods[i] = new SubMeshLOD(material, mesh, distances[i]);
        }

        SubMesh subMesh = new(lods)
        {
            Name = meshName
        };

        return new Model(subMesh);
    }

    private static SubMesh CreateSkinnedSubMesh(Transform bone, string meshName, params float[] maxVisibleDistances)
        => CreateSkinnedModel(bone, meshName, maxVisibleDistances).Meshes.Single();

    [Test]
    public void ReplacingModel_DisposesRemovedRenderableMeshes()
    {
        var node = new SceneNode("ModelComponentTestNode");
        Transform bone = node.SetTransform<Transform>();
        var component = node.AddComponent<ModelComponent>()!;

        component.Model = CreateSkinnedModel(bone, "InitialMesh");

        component.Meshes.Count.ShouldBe(1);
        RenderableMesh removedRenderable = component.Meshes.Single();
        removedRenderable.CurrentLODRenderer.ShouldNotBeNull();

        component.Model = CreateSkinnedModel(bone, "ReplacementMesh");

        component.Meshes.Count.ShouldBe(1);
        component.Meshes.Single().ShouldNotBe(removedRenderable);
        removedRenderable.LODs.Count.ShouldBe(0);
    }

    [Test]
    public void EnumeratingRenderersDuringModelReplacement_UsesStableSnapshot()
    {
        var node = new SceneNode("ModelComponentEnumerationNode");
        Transform bone = node.SetTransform<Transform>();
        var component = node.AddComponent<ModelComponent>()!;
        component.Model = CreateSkinnedModel(bone, "InitialMesh", 0.0f, 10.0f);

        bool replaced = false;
        List<XRMeshRenderer>? renderers = null;

        Should.NotThrow(() =>
        {
            renderers = component.GetAllRenderersWhere(renderer =>
            {
                if (!replaced)
                {
                    replaced = true;
                    component.Model = CreateSkinnedModel(bone, "ReplacementMesh");
                }

                return renderer.Mesh?.HasSkinning == true;
            }).ToList();
        });

        replaced.ShouldBeTrue();
        renderers.ShouldNotBeNull();
        renderers.Count.ShouldBe(2);
    }

    [Test]
    public void CookedBinarySerializer_RoundTrips_ModelComponent_RebuildsRuntimeMeshes()
    {
        SceneNode original = new("ModelComponentSerializationNode", new Transform());
        Transform bone = (Transform)original.Transform;
        ModelComponent component = original.AddComponent<ModelComponent>()!;
        component.Model = CreateSkinnedModel(bone, "InitialMesh");

        byte[] bytes = CookedBinarySerializer.Serialize(original);
        bytes.Length.ShouldBeGreaterThan(0);

        SceneNode? cloneNode = CookedBinarySerializer.Deserialize(typeof(SceneNode), bytes) as SceneNode;
        cloneNode.ShouldNotBeNull();

        ModelComponent? cloneComponent = cloneNode!.GetComponent<ModelComponent>();
        cloneComponent.ShouldNotBeNull();
        cloneComponent!.Model.ShouldNotBeNull();
        cloneComponent.Meshes.Count.ShouldBe(1);
        cloneComponent.RenderedObjects.Length.ShouldBe(1);

        cloneComponent.Model!.Meshes.Add(CreateSkinnedSubMesh((Transform)cloneNode.Transform, "AddedMesh"));

        cloneComponent.Meshes.Count.ShouldBe(2);
        cloneComponent.RenderedObjects.Length.ShouldBe(2);
    }
}
