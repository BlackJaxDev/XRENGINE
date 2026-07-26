using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class PoiyomiPassSubmissionRuntimeTests
{
    private static readonly FieldInfo s_primaryCommandField = typeof(RenderableMesh).GetField(
        "_rc",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly FieldInfo s_outlineCommandField = typeof(RenderableMesh).GetField(
        "_materialOutlineCommand",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo s_syncPassCommands = typeof(RenderableMesh).GetMethod(
        "SyncMaterialPassCommands",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    private IRuntimeShaderServices? _previousShaderServices;
    private IRuntimeRenderingHostServices? _previousRenderingServices;

    [SetUp]
    public void SetUp()
    {
        _previousShaderServices = RuntimeShaderServices.Current;
        _previousRenderingServices = RuntimeRenderingHostServices.Current;
        RuntimeShaderServices.Current = new PoiyomiRuntimeShaderServices();
        RuntimeRenderingHostServices.Current = RuntimeRenderingBootstrap.CreateEngineHostServices();
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeShaderServices.Current = _previousShaderServices;
        RuntimeRenderingHostServices.Current = _previousRenderingServices!;
    }

    [Test]
    public void RenderableMesh_SubmitsEnabledOutlineAsIndependentCpuCompanionDraw()
    {
        RenderingParameters outlineOptions = new()
        {
            CullMode = ECullMode.Front,
        };
        XRMaterial material = new()
        {
            RenderPass = (int)EDefaultRenderPass.OpaqueForward,
            PassSet = new MaterialPassSet
            {
                Passes =
                [
                    new MaterialPassDefinition
                    {
                        Identity = EMaterialPassIdentity.Base,
                        Order = 500,
                        RenderPass = (int)EDefaultRenderPass.OpaqueForward,
                    },
                    new MaterialPassDefinition
                    {
                        Identity = EMaterialPassIdentity.Shadow,
                        Order = 300,
                        RenderPass = (int)EDefaultRenderPass.PreRender,
                    },
                    new MaterialPassDefinition
                    {
                        Identity = EMaterialPassIdentity.Outline,
                        Order = 600,
                        RenderPass = (int)EDefaultRenderPass.OpaqueForward,
                        RenderOptions = outlineOptions,
                        VariantMacros = ["XRENGINE_OUTLINE_PASS"],
                    },
                ],
            },
        };
        material.SetShader(EShaderType.Fragment, ShaderHelper.UberFragForward(), coerceShaderType: true);

        SceneNode node = new("PoiyomiPassSubmission");
        node.SetTransform<Transform>();
        ModelComponent component = node.AddComponent<ModelComponent>()!;
        component.Model = new Model(
            new SubMesh(
                XRMesh.Shapes.SolidSphere(Vector3.Zero, 0.5f, 8u),
                material));

        RenderableMesh mesh = component.Meshes.Single();
        XRMeshRenderer renderer = mesh.CurrentLODRenderer.ShouldNotBeNull();
        RenderCommandMesh3D primary = (RenderCommandMesh3D)s_primaryCommandField.GetValue(mesh)!;
        RenderCommandMesh3D outline = (RenderCommandMesh3D)s_outlineCommandField.GetValue(mesh)!;

        s_syncPassCommands.Invoke(mesh, [renderer, material, false]);

        primary.Enabled.ShouldBeTrue();
        outline.Enabled.ShouldBeTrue();
        outline.ForceCpuRendering.ShouldBeTrue();
        outline.Mesh.ShouldBeSameAs(renderer);
        outline.MaterialOverride.ShouldBeSameAs(material.OutlinePassVariant);
        outline.RenderOptionsOverride.ShouldBeSameAs(outlineOptions);
        mesh.RenderInfo.RenderCommands.ShouldContain(outline);

        s_syncPassCommands.Invoke(mesh, [renderer, material, true]);
        outline.Enabled.ShouldBeFalse();
        primary.Enabled.ShouldBeTrue();
    }
}
