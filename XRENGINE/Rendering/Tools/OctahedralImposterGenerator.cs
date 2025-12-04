using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Diagnostics;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Types;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.Tools;

/// <summary>
/// Utility for baking 3-view-blended octahedral impostor sheets from an existing <see cref="ModelComponent"/>.
/// The capture process follows the approach described in https://shaderbits.com/blog/octahedral-impostors.
/// </summary>
public sealed class OctahedralImposterGenerator
{
    private static readonly Vector3[] s_captureAxes =
    [
        Vector3.UnitX,
        Vector3.UnitY,
        Vector3.UnitZ,
    ];

    /// <summary>
    /// Configuration values for the capture process.
    /// </summary>
    public sealed record Settings(uint SheetSize = 1024, float CapturePadding = 1.15f, bool CaptureDepth = true);

    /// <summary>
    /// Result textures from a bake.
    /// </summary>
    public sealed record Result(XRTexture2D Sheet, XRTexture2D[] Views, XRTexture2D? Depth, AABB LocalBounds);

    /// <summary>
    /// Generates a new impostor sheet for the supplied model component.
    /// </summary>
    /// <param name="component">Component that owns the model to capture.</param>
    /// <param name="settings">Capture settings such as resolution.</param>
    /// <returns>A <see cref="Result"/> containing the finished textures or <c>null</c> when capture failed.</returns>
    public Result? Generate(ModelComponent component, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(component);

        Model? model = component.Model;
        if (model is null)
        {
            Debug.LogWarning("Cannot generate impostor: no model assigned.");
            return null;
        }

        AABB bounds = CalculateCombinedBounds(model);
        if (!bounds.IsValid)
        {
            Debug.LogWarning("Cannot generate impostor: model has no valid bounds.");
            return null;
        }

        XRWorldInstance captureWorld = new();
        SceneNode captureNode = new(captureWorld, "OctahedralImpostor");
        Transform captureTransform = captureNode.Transform as Transform;
        if (captureTransform is not null)
        {
            captureTransform.Translation = Vector3.Zero;
            captureTransform.Rotation = component.Transform.Rotation;
            captureTransform.Scale = component.Transform.Scale;
        }

        ModelComponent captureComponent = captureNode.AddComponent<ModelComponent>()!;
        captureComponent.Model = model;

        XRTexture2D[] viewTextures = new XRTexture2D[s_captureAxes.Length];
        XRTexture2D? depthTexture = settings.CaptureDepth
            ? new XRTexture2D(settings.SheetSize, settings.SheetSize, EPixelInternalFormat.DepthComponent32f, EPixelFormat.DepthComponent, EPixelType.Float, false)
            {
                FrameBufferAttachment = EFrameBufferAttachment.DepthAttachment,
                Name = "ImpostorDepth"
            }
            : null;

        for (int i = 0; i < s_captureAxes.Length; i++)
        {
            viewTextures[i] = CaptureView(captureWorld, bounds, settings.SheetSize, settings.CapturePadding, s_captureAxes[i], depthTexture);
            if (viewTextures[i] is null)
                return null;
        }

        XRTexture2D sheetTexture = new(settings.SheetSize, settings.SheetSize, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.Float, false)
        {
            FrameBufferAttachment = EFrameBufferAttachment.ColorAttachment0,
            Name = "OctahedralImpostorSheet"
        };

        if (!ComposeOctahedralSheet(viewTextures, sheetTexture))
            return null;

        return new Result(sheetTexture, viewTextures, depthTexture, bounds);
    }

    private static AABB CalculateCombinedBounds(Model model)
    {
        AABB total = new();
        foreach (SubMesh mesh in model.Meshes)
            total = AABB.Union(total, mesh.CullingBounds ?? mesh.Bounds);
        return total;
    }

    private static XRTexture2D CaptureView(
        XRWorldInstance world,
        AABB bounds,
        uint resolution,
        float padding,
        Vector3 axis,
        XRTexture2D? sharedDepth)
    {
        float maxExtent = MathF.Max(bounds.Size.X, MathF.Max(bounds.Size.Y, bounds.Size.Z));
        float extent = maxExtent * padding * 0.5f;
        Vector3 center = bounds.Center;

        XRTexture2D colorAttachment = new(resolution, resolution, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.Float, false)
        {
            FrameBufferAttachment = EFrameBufferAttachment.ColorAttachment0,
            Name = $"ImpostorView_{axis}"
        };

        XRFrameBuffer fbo = sharedDepth is null
            ? new XRFrameBuffer((colorAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
            : new XRFrameBuffer(
                (colorAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (sharedDepth, EFrameBufferAttachment.DepthAttachment, 0, -1));

        Transform cameraTransform = new(center + axis * (extent + maxExtent), Quaternion.Identity);
        cameraTransform.LookAt(center);

        XROrthographicCameraParameters cameraParameters = new(extent * 2f, extent * 2f, 0.01f, extent * 4f);
        XRCamera camera = new(cameraTransform, cameraParameters)
        {
            PostProcessing = new PostProcessingSettings()
        };

        XRViewport viewport = new(null, (int)resolution, (int)resolution)
        {
            Camera = camera,
            RenderPipeline = new DefaultRenderPipeline(),
            SetRenderPipelineFromCamera = false,
            AllowUIRender = false,
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            CullWithFrustum = true,
            WorldInstanceOverride = world
        };

        viewport.CollectVisible(false, world, camera);
        viewport.Render(fbo, world, camera);
        viewport.SwapBuffers();

        return colorAttachment;
    }

    private static bool ComposeOctahedralSheet(IReadOnlyList<XRTexture2D> views, XRTexture2D sheet)
    {
        XRShader? blendShader = Engine.Assets.LoadEngineAsset<XRShader>("Shaders", "Tools", "OctahedralImposterBlend.fs");
        XRShader? fullscreenShader = Engine.Assets.LoadEngineAsset<XRShader>("Shaders", "FullscreenTri.vs");

        if (blendShader is null || fullscreenShader is null)
        {
            Debug.LogWarning("Unable to load octahedral impostor shaders.");
            return false;
        }

        XRMaterial material = new([.. views], fullscreenShader, blendShader)
        {
            RenderOptions =
            {
                Blend = BlendMode.Opaque,
                DepthTest = new DepthTest { Enabled = ERenderParamUsage.Disabled }
            }
        };

        XRFrameBuffer targetFbo = new((sheet, EFrameBufferAttachment.ColorAttachment0, 0, -1));
        XRQuadFrameBuffer quad = new(material);
        quad.Render(targetFbo);
        return true;
    }
}
