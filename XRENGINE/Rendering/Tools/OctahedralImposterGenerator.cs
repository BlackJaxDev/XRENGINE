using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Diagnostics;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.PostProcessing;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.Tools;

/// <summary>
/// Utility for baking 26-view octahedral impostor sheets from an existing <see cref="ModelComponent"/>.
/// The capture set covers axes, mid-axes, and elevated diagonals so that the runtime shader can blend the
/// closest three views from the billboard's facing direction.
/// </summary>
public sealed class OctahedralImposterGenerator
{
    private static readonly Vector3[] s_captureDirections = BuildCaptureDirections();

    /// <summary>
    /// Configuration values for the capture process.
    /// </summary>
    public sealed record Settings(uint SheetSize = 1024, float CapturePadding = 1.15f, bool CaptureDepth = true);

    /// <summary>
    /// Result textures from a bake.
    /// </summary>
    public sealed record Result(
        XRTexture2D Sheet,
        XRTexture2D[] Views,
        XRTexture2D? Depth,
        AABB LocalBounds,
        IReadOnlyList<Vector3> CaptureDirections);

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

        Transform sourceTransform = component.GetForcedDefaultTransform();

        XRWorldInstance captureWorld = new();
        SceneNode captureNode = new(captureWorld, "OctahedralImpostor");
        Transform captureTransform = captureNode.GetTransformAs<Transform>(true)!;
        captureTransform.Translation = Vector3.Zero;
        captureTransform.Rotation = sourceTransform.Rotation;
        captureTransform.Scale = sourceTransform.Scale;

        ModelComponent captureComponent = captureNode.AddComponent<ModelComponent>()!;
        captureComponent.Model = model;

        AABB captureBounds = TransformBounds(bounds, sourceTransform);

        XRTexture2D[] viewTextures = new XRTexture2D[s_captureDirections.Length];
        XRTexture2D? depthTexture = settings.CaptureDepth
            ? new XRTexture2D(settings.SheetSize, settings.SheetSize, EPixelInternalFormat.DepthComponent32f, EPixelFormat.DepthComponent, EPixelType.Float, false)
            {
                FrameBufferAttachment = EFrameBufferAttachment.DepthAttachment,
                Name = "ImpostorDepth"
            }
            : null;

        for (int i = 0; i < s_captureDirections.Length; i++)
        {
            viewTextures[i] = CaptureView(
                captureWorld,
                captureBounds,
                settings.SheetSize,
                settings.CapturePadding,
                s_captureDirections[i],
                i,
                depthTexture);
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

        return new Result(sheetTexture, viewTextures, depthTexture, bounds, s_captureDirections);
    }

    private static AABB CalculateCombinedBounds(Model model)
    {
        AABB total = new();
        foreach (SubMesh mesh in model.Meshes)
            total = AABB.Union(total, mesh.CullingBounds ?? mesh.Bounds);
        return total;
    }

    private static AABB TransformBounds(AABB bounds, TransformBase transform)
    {
        Matrix4x4 renderMatrix = transform.RenderMatrix;
        renderMatrix.Translation = Vector3.Zero;

        return bounds.Transformed(point => Vector3.Transform(point, renderMatrix));
    }

    private static XRTexture2D CaptureView(
        XRWorldInstance world,
        AABB bounds,
        uint resolution,
        float padding,
        Vector3 axis,
        int viewIndex,
        XRTexture2D? sharedDepth)
    {
        float maxExtent = MathF.Max(bounds.Size.X, MathF.Max(bounds.Size.Y, bounds.Size.Z));
        float extent = maxExtent * padding * 0.5f;
        Vector3 center = bounds.Center;

        XRTexture2D colorAttachment = new(resolution, resolution, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.Float, false)
        {
            FrameBufferAttachment = EFrameBufferAttachment.ColorAttachment0,
            Name = $"ImpostorView_{viewIndex:00}"
        };

        XRFrameBuffer fbo = sharedDepth is null
            ? new XRFrameBuffer((colorAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
            : new XRFrameBuffer(
                (colorAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (sharedDepth, EFrameBufferAttachment.DepthAttachment, 0, -1));

        Transform cameraTransform = new(center + axis * (extent + maxExtent), Quaternion.Identity);
        cameraTransform.LookAt(center);

        XROrthographicCameraParameters cameraParameters = new(extent * 2f, extent * 2f, 0.01f, extent * 4f);
        XRCamera camera = new(cameraTransform, cameraParameters);
        camera.PostProcessStates = new CameraPostProcessStateCollection();

        XRViewport viewport = new(null, resolution, resolution)
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

    private static Vector3[] BuildCaptureDirections()
    {
        List<Vector3> directions = new(26);

        // Ordering matters: keep in sync with Build/CommonAssets/Shaders/Tools/OctahedralImposterBlend.fs.
        // The runtime shader relies on this exact layout when selecting sampler bindings.
        // 1. World axes (6)
        // 2. Mid-axes (12) – 45° between the primary axes
        // 3. Elevated diagonals (8) – 45° above/below the edge directions

        directions.Add(Vector3.UnitX);
        directions.Add(-Vector3.UnitX);
        directions.Add(Vector3.UnitY);
        directions.Add(-Vector3.UnitY);
        directions.Add(Vector3.UnitZ);
        directions.Add(-Vector3.UnitZ);

        Vector3[] edgeSamples =
        [
            new(1, 1, 0),
            new(1, -1, 0),
            new(-1, 1, 0),
            new(-1, -1, 0),
            new(1, 0, 1),
            new(1, 0, -1),
            new(-1, 0, 1),
            new(-1, 0, -1),
            new(0, 1, 1),
            new(0, 1, -1),
            new(0, -1, 1),
            new(0, -1, -1)
        ];

        foreach (Vector3 raw in edgeSamples)
            directions.Add(Vector3.Normalize(raw));

        for (int xSign = -1; xSign <= 1; xSign += 2)
        {
            for (int ySign = -1; ySign <= 1; ySign += 2)
            {
                for (int zSign = -1; zSign <= 1; zSign += 2)
                {
                    directions.Add(Vector3.Normalize(new Vector3(xSign, ySign, zSign)));
                }
            }
        }

        return directions.ToArray();
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
                BlendModeAllDrawBuffers = BlendMode.EnabledOpaque(),
                DepthTest = new DepthTest { Enabled = ERenderParamUsage.Disabled }
            }
        };

        XRFrameBuffer targetFbo = new((sheet, EFrameBufferAttachment.ColorAttachment0, 0, -1));
        XRQuadFrameBuffer quad = new(material);
        quad.Render(targetFbo);
        return true;
    }
}
