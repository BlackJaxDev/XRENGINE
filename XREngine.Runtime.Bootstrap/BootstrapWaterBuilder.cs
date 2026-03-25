using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Runtime.Bootstrap;

public static class BootstrapWaterBuilder
{
    private const float PreviewPlaneSize = 18.0f;
    private const float PreviewOffsetY = 0.85f;
    private const float PreviewOffsetZ = -10.0f;
    private const float UnderwaterFloorY = -1.25f;

    public static void AddDynamicWaterPreview(SceneNode rootNode)
    {
        var previewRoot = rootNode.NewChild("DynamicWaterPreviewRoot");
        var previewTransform = previewRoot.SetTransform<Transform>();
        previewTransform.Translation = new Vector3(0.0f, PreviewOffsetY, PreviewOffsetZ);

        AddUnderwaterFloor(previewRoot);
        AddReferenceSphere(previewRoot, new Vector3(-3.2f, -0.55f, 1.5f), 0.45f, new ColorF4(0.95f, 0.54f, 0.28f, 1.0f));
        AddReferenceSphere(previewRoot, new Vector3(2.4f, -0.80f, -2.0f), 0.65f, new ColorF4(0.23f, 0.77f, 0.95f, 1.0f));
        AddReferenceBox(previewRoot, new Vector3(0.8f, -0.40f, 2.8f), new Vector3(0.9f, 1.4f, 0.9f), new ColorF4(0.92f, 0.90f, 0.45f, 1.0f));

        XRTexture2D sceneColorGrab = XRTexture2D.CreateGrabPassTextureResized(
            0.75f,
            EReadBufferMode.Front,
            colorBit: true,
            depthBit: false,
            stencilBit: false,
            linearFilter: true);
        XRTexture2D sceneDepthGrab = XRTexture2D.CreateGrabPassTextureResized(
            1.0f,
            EReadBufferMode.Front,
            colorBit: false,
            depthBit: true,
            stencilBit: false,
            linearFilter: false);

        XRMaterial waterMaterial = XRMaterial.CreateDynamicWaterMaterialForward(
            sceneColorGrab,
            sceneDepthGrab,
            new ColorF4(0.10f, 0.52f, 0.70f, 1.0f),
            new ColorF4(0.01f, 0.08f, 0.18f, 1.0f));
        waterMaterial.RenderOptions.CullMode = ECullMode.None;
        waterMaterial.SetInt("WaterSubdivision", 12);
        waterMaterial.SetFloat("OceanWaveIntensity", 1.15f);
        waterMaterial.SetFloat("WaveScale", 0.20f);
        waterMaterial.SetFloat("WaveSpeed", 0.80f);
        waterMaterial.SetFloat("WaveHeight", 0.45f);
        waterMaterial.SetFloat("DepthBlurRadius", 1.6f);
        waterMaterial.SetFloat("FoamIntensity", 0.90f);
        waterMaterial.SetFloat("FoamThreshold", 0.34f);
        waterMaterial.SetFloat("FoamSoftness", 0.22f);
        waterMaterial.SetFloat("CausticIntensity", 0.95f);
        waterMaterial.SetFloat("CausticScale", 2.8f);
        waterMaterial.SetFloat("EddyIntensity", 1.2f);
        waterMaterial.SetFloat("EddyRadius", 0.95f);

        float halfSize = PreviewPlaneSize * 0.5f;
        var waterNode = previewRoot.NewChild("DynamicWaterPreviewPlane");
        var waterModel = waterNode.AddComponent<ModelComponent>()!;
        waterModel.Model = new Model(
        [
            new SubMesh(XRMesh.Create(VertexQuad.PosY(PreviewPlaneSize)), waterMaterial)
            {
                CullingBounds = new AABB(
                    new Vector3(-halfSize, -2.5f, -halfSize),
                    new Vector3(halfSize, 2.5f, halfSize)),
            }
        ]);

        var controller = previewRoot.AddComponent<DynamicWaterPreviewControllerComponent>()!;
        controller.WaterMaterial = waterMaterial;
    }

    private static void AddUnderwaterFloor(SceneNode previewRoot)
    {
        var floorNode = previewRoot.NewChild("DynamicWaterPreviewFloor");
        var floorTransform = floorNode.SetTransform<Transform>();
        floorTransform.Translation = new Vector3(0.0f, UnderwaterFloorY, 0.0f);

        XRMaterial floorMaterial = XRMaterial.CreateLitColorMaterial(new ColorF4(0.73f, 0.70f, 0.61f, 1.0f));
        floorMaterial.RenderOptions.CullMode = ECullMode.None;
        floorMaterial.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
        floorMaterial.Parameter<ShaderFloat>("Roughness")!.Value = 0.93f;
        floorMaterial.Parameter<ShaderFloat>("Metallic")!.Value = 0.03f;

        float floorSize = PreviewPlaneSize * 0.82f;
        float halfSize = floorSize * 0.5f;
        var floorModel = floorNode.AddComponent<ModelComponent>()!;
        floorModel.Model = new Model(
        [
            new SubMesh(XRMesh.Create(VertexQuad.PosY(floorSize)), floorMaterial)
            {
                CullingBounds = new AABB(
                    new Vector3(-halfSize, -0.1f, -halfSize),
                    new Vector3(halfSize, 0.1f, halfSize)),
            }
        ]);
    }

    private static void AddReferenceSphere(SceneNode previewRoot, Vector3 localTranslation, float radius, ColorF4 color)
    {
        var sphereNode = previewRoot.NewChild("DynamicWaterPreviewSphere");
        var sphereTransform = sphereNode.SetTransform<Transform>();
        sphereTransform.Translation = localTranslation;

        XRMaterial sphereMaterial = XRMaterial.CreateLitColorMaterial(color);
        sphereMaterial.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
        sphereMaterial.Parameter<ShaderFloat>("Roughness")!.Value = 0.28f;
        sphereMaterial.Parameter<ShaderFloat>("Metallic")!.Value = 0.18f;

        var sphereModel = sphereNode.AddComponent<ModelComponent>()!;
        sphereModel.Model = new Model(
        [
            new SubMesh(XRMesh.Shapes.SolidSphere(Vector3.Zero, radius, 32), sphereMaterial)
            {
                CullingBounds = new AABB(new Vector3(-radius), new Vector3(radius)),
            }
        ]);
    }

    private static void AddReferenceBox(SceneNode previewRoot, Vector3 localTranslation, Vector3 size, ColorF4 color)
    {
        var boxNode = previewRoot.NewChild("DynamicWaterPreviewBox");
        var boxTransform = boxNode.SetTransform<Transform>();
        boxTransform.Translation = localTranslation;

        XRMaterial boxMaterial = XRMaterial.CreateLitColorMaterial(color);
        boxMaterial.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
        boxMaterial.Parameter<ShaderFloat>("Roughness")!.Value = 0.42f;
        boxMaterial.Parameter<ShaderFloat>("Metallic")!.Value = 0.08f;

        Vector3 halfSize = size * 0.5f;
        var boxModel = boxNode.AddComponent<ModelComponent>()!;
        boxModel.Model = new Model(
        [
            new SubMesh(XRMesh.Shapes.SolidBox(-halfSize, halfSize), boxMaterial)
            {
                CullingBounds = new AABB(-halfSize, halfSize),
            }
        ]);
    }

    public sealed class DynamicWaterPreviewControllerComponent : XRComponent
    {
        private XRMaterial? _waterMaterial;

        public XRMaterial? WaterMaterial
        {
            get => _waterMaterial;
            set => SetField(ref _waterMaterial, value);
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            RegisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateInteractors);
            UpdateInteractors();
        }

        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            UnregisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateInteractors);
        }

        private void UpdateInteractors()
        {
            XRMaterial? material = WaterMaterial;
            if (material is null)
                return;

            float time = (float)Engine.ElapsedTime;
            Vector3 center = Transform.WorldTranslation;

            Vector3 spherePosition = center + new Vector3(
                MathF.Cos(time * 0.85f) * 3.2f,
                0.06f + MathF.Sin(time * 1.70f) * 0.08f,
                MathF.Sin(time * 0.85f) * 2.1f);
            const float sphereRadius = 0.55f;

            Vector3 capsuleCenter = center + new Vector3(
                MathF.Sin(time * 0.47f + 1.2f) * 2.5f,
                0.38f + MathF.Sin(time * 0.31f) * 0.18f,
                MathF.Cos(time * 0.61f + 0.45f) * 3.0f);
            Vector3 capsuleAxis = Vector3.Normalize(new Vector3(
                MathF.Sin(time * 0.73f) * 0.28f,
                1.0f,
                MathF.Cos(time * 0.73f) * 0.22f));
            const float capsuleHalfHeight = 1.0f;
            const float capsuleRadius = 0.32f;
            Vector3 capsuleStart = capsuleCenter + capsuleAxis * capsuleHalfHeight;
            Vector3 capsuleEnd = capsuleCenter - capsuleAxis * capsuleHalfHeight;

            material.SetInt("InteractorSphereCount", 1);
            material.SetVector4("InteractorSphere0", new Vector4(spherePosition, sphereRadius));
            material.SetVector4("InteractorSphere1", Vector4.Zero);
            material.SetVector4("InteractorSphere2", Vector4.Zero);
            material.SetVector4("InteractorSphere3", Vector4.Zero);

            material.SetInt("InteractorCapsuleCount", 1);
            material.SetVector4("InteractorCapsuleStart0", new Vector4(capsuleStart, capsuleRadius));
            material.SetVector4("InteractorCapsuleEnd0", new Vector4(capsuleEnd, capsuleRadius));
            material.SetVector4("InteractorCapsuleStart1", Vector4.Zero);
            material.SetVector4("InteractorCapsuleEnd1", Vector4.Zero);
            material.SetVector4("InteractorCapsuleStart2", Vector4.Zero);
            material.SetVector4("InteractorCapsuleEnd2", Vector4.Zero);
            material.SetVector4("InteractorCapsuleStart3", Vector4.Zero);
            material.SetVector4("InteractorCapsuleEnd3", Vector4.Zero);
        }
    }
}
