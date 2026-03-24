using System.Linq;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderableMeshStartupMatrixTests
{
    private static readonly MethodInfo s_processPendingRenderMatrixUpdates = typeof(RenderableMesh).GetMethod(
        "ProcessPendingRenderMatrixUpdates",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;

    private static readonly FieldInfo s_renderCommandField = typeof(RenderableMesh).GetField(
        "_rc",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Test]
    public void ModelComponent_NewRenderableMesh_SeedsRenderCommandFromRenderMatrix()
    {
        SceneNode node = new("RenderableRoot");
        Transform transform = node.SetTransform<Transform>();
        transform.Translation = new Vector3(1.0f, 2.0f, 3.0f);
        transform.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

        Matrix4x4 worldMatrix = transform.WorldMatrix;
        Matrix4x4 renderMatrix = Matrix4x4.CreateTranslation(new Vector3(10.0f, 20.0f, 30.0f));
        transform.SetRenderMatrix(renderMatrix, recalcAllChildRenderMatrices: false).Wait();

        ModelComponent component = node.AddComponent<ModelComponent>()!;
        component.Model = new Model(
            new SubMesh(
                XRMesh.Shapes.SolidSphere(Vector3.Zero, 0.5f, 8u),
                new XRMaterial()));

        RenderableMesh mesh = component.Meshes.Single();
        RenderCommandMesh3D renderCommand = (RenderCommandMesh3D)s_renderCommandField.GetValue(mesh)!;

        renderCommand.WorldMatrix.ShouldBe(renderMatrix);
        renderCommand.WorldMatrix.ShouldNotBe(worldMatrix);

        s_processPendingRenderMatrixUpdates.Invoke(null, null);

        renderCommand.WorldMatrix.ShouldBe(renderMatrix);
        renderCommand.WorldMatrix.ShouldNotBe(worldMatrix);
    }
}
