using System.Numerics;
using XREngine.Components;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class EditorUnitTests
{
    private static SceneNode AddOcclusionCullingRig(
        SceneNode parentNode,
        EOcclusionCullingMode mode,
        EMeshSubmissionStrategy submissionStrategy,
        MathIntersectionsWorldControllerComponent? controller)
    {
        SceneNode rigNode = parentNode.NewChild($"{mode} Occlusion Test");
        Transform rigTransform = rigNode.SetTransform<Transform>();

        XRMesh boxMesh = XRMesh.Shapes.SolidBox(new Vector3(-0.5f), new Vector3(0.5f));
        AABB boxBounds = new(new Vector3(-0.5f), new Vector3(0.5f));
        XRMaterial occluderMaterial = CreateOcclusionTestMaterial(
            $"{mode} Occluder Material",
            new ColorF4(0.12f, 0.38f, 0.72f, 1.0f));
        // Hardware-query occluders must remain visible so an old query result cannot
        // remove the depth source that makes later target queries meaningful. SOC still
        // needs the wall eligible for its software occluder-selection pass.
        occluderMaterial.RenderOptions.ExcludeFromCpuOcclusion =
            mode == EOcclusionCullingMode.CpuQueryAsync;
        XRMaterial hiddenMaterial = CreateOcclusionTestMaterial(
            $"{mode} Hidden Target Material",
            new ColorF4(0.92f, 0.08f, 0.10f, 1.0f));
        XRMaterial visibleMaterial = CreateOcclusionTestMaterial(
            $"{mode} Visible Sentinel Material",
            new ColorF4(0.08f, 0.82f, 0.92f, 1.0f));
        XRMaterial revealMaterial = CreateOcclusionTestMaterial(
            $"{mode} Reveal Target Material",
            new ColorF4(1.0f, 0.42f, 0.04f, 1.0f));

        AddOcclusionTestBox(
            rigNode,
            "Occluder Wall",
            new Vector3(0.0f, 2.5f, -1.25f),
            new Vector3(7.0f, 5.0f, 0.55f),
            boxMesh,
            boxBounds,
            occluderMaterial);

        const int targetColumns = 4;
        const int targetRows = 3;
        for (int row = 0; row < targetRows; row++)
        for (int column = 0; column < targetColumns; column++)
        {
            float x = (column - (targetColumns - 1) * 0.5f) * 1.55f;
            float y = 1.0f + row * 1.45f;
            AddOcclusionTestBox(
                rigNode,
                $"Hidden Target {row * targetColumns + column + 1}",
                new Vector3(x, y, 1.35f),
                new Vector3(0.72f),
                boxMesh,
                boxBounds,
                hiddenMaterial);
        }

        AddOcclusionTestBox(
            rigNode,
            "Left Visible Sentinel",
            new Vector3(-5.1f, 1.2f, 1.1f),
            new Vector3(0.85f),
            boxMesh,
            boxBounds,
            visibleMaterial);
        AddOcclusionTestBox(
            rigNode,
            "Right Visible Sentinel",
            new Vector3(5.1f, 3.7f, 1.1f),
            new Vector3(0.85f),
            boxMesh,
            boxBounds,
            visibleMaterial);
        SceneNode revealTarget = AddOcclusionTestBox(
            rigNode,
            "Moving Disocclusion Target",
            new Vector3(0.0f, 2.55f, 1.55f),
            new Vector3(0.95f),
            boxMesh,
            boxBounds,
            revealMaterial);

        AddOcclusionTestLight(rigNode, mode);

        MathOcclusionCullingTestComponent test =
            rigNode.AddComponent<MathOcclusionCullingTestComponent>()!;
        test.Configure(
            mode,
            submissionStrategy,
            revealTarget.GetTransformAs<Transform>(false)!);

        if (controller?.IsSpawningBenchmarkInstances != true)
        {
            CustomUIComponent controls = rigNode.AddComponent<CustomUIComponent>()!;
            test.RegisterControls(controls);
        }

        controller?.RegisterSubLabel(
            rigNode,
            rigTransform,
            mode switch
            {
                EOcclusionCullingMode.CpuQueryAsync => "CPU hardware queries + async temporal decisions",
                EOcclusionCullingMode.CpuSoftwareOcclusion => "CPU masked depth rasterization + AABB tests",
                _ => "GPU two-pass qualification: zero readback + GPU BVH",
            },
            6.4f);

        return rigNode;
    }

    private static SceneNode AddOcclusionTestBox(
        SceneNode parentNode,
        string name,
        in Vector3 translation,
        in Vector3 size,
        XRMesh mesh,
        in AABB bounds,
        XRMaterial material)
    {
        SceneNode node = parentNode.NewChild(name);
        Transform transform = node.SetTransform<Transform>();
        transform.Translation = translation;
        transform.Scale = size;

        ModelComponent model = node.AddComponent<ModelComponent>()!;
        model.Name = $"{name} Model";
        model.Model = new Model(
        [
            new SubMesh(mesh, material)
            {
                CullingBounds = bounds,
            },
        ]);
        return node;
    }

    private static XRMaterial CreateOcclusionTestMaterial(string name, in ColorF4 color)
    {
        XRMaterial material = XRMaterial.CreateLitColorMaterial(color, deferred: true);
        material.Name = name;
        material.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
        material.RenderOptions.CullMode = ECullMode.None;
        material.RenderOptions.ExcludeFromCpuOcclusion = false;
        return material;
    }

    private static void AddOcclusionTestLight(SceneNode rigNode, EOcclusionCullingMode mode)
    {
        SceneNode lightNode = rigNode.NewChild("Occlusion Test Light");
        Transform lightTransform = lightNode.SetTransform<Transform>();
        lightTransform.Translation = new Vector3(0.0f, 6.5f, -4.5f);

        PointLightComponent light = lightNode.AddComponent<PointLightComponent>()!;
        light.Name = $"{mode} Occlusion Test Light";
        light.Color = new ColorF3(1.0f, 0.96f, 0.90f);
        light.DiffuseIntensity = 1.0f;
        light.Brightness = 36.0f;
        light.Radius = 18.0f;
        light.CastsShadows = false;
    }
}
