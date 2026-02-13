using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Silk.NET.OpenGL;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Diagnostics;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.OpenGL;
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
        XRTexture2DArray Views,
        XRTexture2D? Depth,
        AABB LocalBounds,
        IReadOnlyList<Vector3> CaptureDirections);

    /// <summary>
    /// Generates a new impostor sheet for the supplied model component.
    /// This method dispatches rendering work to the main thread and blocks until complete.
    /// </summary>
    /// <param name="component">Component that owns the model to capture.</param>
    /// <param name="settings">Capture settings such as resolution.</param>
    /// <returns>A <see cref="Result"/> containing the finished textures or <c>null</c> when capture failed.</returns>
    public static Result? Generate(ModelComponent component, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(component);

        Model? model = component.Model;
        if (model is null)
        {
            Debug.LogWarning("Cannot generate impostor: no model assigned.");
            return null;
        }

        // Build world-space bounds, preferring skinned bounds from live renderable meshes.
        AABB captureBounds = CalculateCombinedWorldBounds(component);
        if (!captureBounds.IsValid)
        {
            // Fallback to static model bounds transformed by the component.
            AABB modelBounds = CalculateCombinedModelBounds(model);
            captureBounds = TransformBoundsToWorld(modelBounds, component.Transform);
        }

        if (!captureBounds.IsValid)
        {
            Debug.LogWarning("Cannot generate impostor: model has no valid bounds.");
            return null;
        }

        // Use the component's existing world - it is already activated and visible
        XRWorldInstance? world = component.World;
        if (world is null)
        {
            Debug.LogWarning("Cannot generate impostor: component has no world.");
            return null;
        }

        Vector3 captureCenter = captureBounds.Center;

        Result? result = null;

        // If we're already on the main/render thread, execute directly
        if (Engine.IsRenderThread)
        {
            result = ExecuteCapture(component, world, captureCenter, captureBounds, settings);
        }
        else
        {
            // Dispatch to main thread and wait for completion
            using ManualResetEventSlim completionEvent = new(false);
            Engine.EnqueueMainThreadTask(() =>
            {
                try
                {
                    result = ExecuteCapture(component, world, captureCenter, captureBounds, settings);
                }
                finally
                {
                    completionEvent.Set();
                }
            });
            completionEvent.Wait();
        }

        return result;
    }

    /// <summary>
    /// Executes the actual capture work on the main/render thread.
    /// </summary>
    private static Result? ExecuteCapture(
        ModelComponent component,
        XRWorldInstance world,
        Vector3 captureCenter,
        AABB captureBounds,
        Settings settings)
    {
        // Save original layers and switch to Gizmos layer for isolated capture
        Dictionary<RenderInfo, int> originalLayers = [];
        foreach (RenderInfo ri in component.RenderedObjects)
        {
            if (ri is RenderInfo3D ri3d)
            {
                originalLayers[ri] = ri3d.Layer;
                ri3d.Layer = DefaultLayers.GizmosIndex;
            }
        }

        try
        {
            XRTexture2DArray viewArray = XRTexture2DArray.CreateFrameBufferTexture(
                (uint)s_captureDirections.Length,
                settings.SheetSize,
                settings.SheetSize,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.Float,
                EFrameBufferAttachment.ColorAttachment0);
            viewArray.Name = "OctahedralImpostorViews";

            // Match sampling defaults for captures
            viewArray.MinFilter = ETexMinFilter.Linear;
            viewArray.MagFilter = ETexMagFilter.Linear;
            viewArray.UWrap = ETexWrapMode.ClampToEdge;
            viewArray.VWrap = ETexWrapMode.ClampToEdge;
            viewArray.SizedInternalFormat = viewArray.Textures[0].SizedInternalFormat;

            // Allocate GPU storage up front so FBO attachments are complete
            viewArray.PushData();

            bool useDepthTexture = settings.CaptureDepth && AbstractRenderer.Current is OpenGLRenderer;
            XRTexture2D? depthTexture = useDepthTexture
                ? new XRTexture2D(settings.SheetSize, settings.SheetSize, EPixelInternalFormat.DepthComponent32f, EPixelFormat.DepthComponent, EPixelType.Float, false)
                {
                    FrameBufferAttachment = EFrameBufferAttachment.DepthAttachment,
                    Name = "ImpostorDepth"
                }
                : null;

            XRRenderBuffer? depthBuffer = settings.CaptureDepth && !useDepthTexture
                ? new XRRenderBuffer(settings.SheetSize, settings.SheetSize, ERenderBufferStorage.Depth24Stencil8)
                {
                    Name = "ImpostorDepth"
                }
                : null;

            depthTexture?.PushData();

            for (int i = 0; i < s_captureDirections.Length; i++)
            {
                bool ok = CaptureViewLayer(
                    world,
                    captureCenter,
                    captureBounds,
                    settings.SheetSize,
                    settings.CapturePadding,
                    s_captureDirections[i],
                    i,
                    viewArray,
                    depthTexture,
                    depthBuffer);
                if (!ok)
                    return null;
            }

            return new Result(viewArray, depthTexture, captureBounds, s_captureDirections);
        }
        finally
        {
            // Restore original layers
            foreach (var kvp in originalLayers)
            {
                if (kvp.Key is RenderInfo3D ri3d)
                    ri3d.Layer = kvp.Value;
            }
        }
    }

    private static AABB CalculateCombinedModelBounds(Model model)
    {
        AABB total = new();
        foreach (SubMesh mesh in model.Meshes)
            total = AABB.Union(total, mesh.CullingBounds ?? mesh.Bounds);
        return total;
    }

    private static AABB CalculateCombinedWorldBounds(ModelComponent component)
    {
        AABB total = new();
        foreach (RenderableMesh mesh in component.Meshes)
        {
            if (mesh.TryGetWorldBounds(out AABB worldBounds))
                total = AABB.Union(total, worldBounds);
        }
        return total;
    }

    private static AABB TransformBoundsToWorld(AABB bounds, TransformBase transform)
    {
        Matrix4x4 renderMatrix = transform.RenderMatrix;
        return bounds.Transformed(point => Vector3.Transform(point, renderMatrix));
    }

    private static bool CaptureViewLayer(
        XRWorldInstance world,
        Vector3 captureCenter,
        AABB bounds,
        uint resolution,
        float padding,
        Vector3 axis,
        int viewIndex,
        XRTexture2DArray colorArray,
        XRTexture2D? sharedDepthTexture,
        XRRenderBuffer? sharedDepthBuffer)
    {
        Vector3 halfExtents = bounds.Size * 0.5f;
        float maxHalfExtent = MathF.Max(halfExtents.X, MathF.Max(halfExtents.Y, halfExtents.Z));
        float paddedHalfExtent = maxHalfExtent * padding;

        XRTexture2D? previewLayer = (viewIndex >= 0 && viewIndex < colorArray.Textures.Length)
            ? colorArray.Textures[viewIndex]
            : null;

        XRFrameBuffer fbo = sharedDepthTexture is null && sharedDepthBuffer is null
            ? new XRFrameBuffer((colorArray, EFrameBufferAttachment.ColorAttachment0, 0, viewIndex))
            : sharedDepthTexture is not null
                ? new XRFrameBuffer(
                    (colorArray, EFrameBufferAttachment.ColorAttachment0, 0, viewIndex),
                    (sharedDepthTexture, EFrameBufferAttachment.DepthAttachment, 0, -1))
                : new XRFrameBuffer(
                    (colorArray, EFrameBufferAttachment.ColorAttachment0, 0, viewIndex),
                    (sharedDepthBuffer!, EFrameBufferAttachment.DepthStencilAttachment, 0, -1));

        Vector3 normalizedAxis = Vector3.Normalize(axis);
        float eyeDistance = paddedHalfExtent + maxHalfExtent; // sit just outside the padded bounds
        Vector3 eye = captureCenter + normalizedAxis * eyeDistance;

        // Avoid degenerate look-at when the capture direction aligns with the global up vector.
        Vector3 up = MathF.Abs(Vector3.Dot(normalizedAxis, Vector3.UnitY)) > 0.95f
            ? Vector3.UnitZ
            : Vector3.UnitY;

        Matrix4x4 viewOrientation = Matrix4x4.CreateLookAt(eye, captureCenter, up);
        if (!Matrix4x4.Invert(viewOrientation, out Matrix4x4 invView))
            invView = Matrix4x4.Identity;

        Quaternion rotation = Quaternion.CreateFromRotationMatrix(invView);
        Transform cameraTransform = new(eye, rotation);
        cameraTransform.RecalculateMatrices(true, true);

        XROrthographicCameraParameters cameraParameters = new(paddedHalfExtent * 2f, paddedHalfExtent * 2f, 0.01f, eyeDistance + paddedHalfExtent * 2f);
        cameraParameters.SetOriginCentered();
        XRCamera camera = new(cameraTransform, cameraParameters)
        {
            PostProcessStates = new CameraPostProcessStateCollection(),
            // Only capture the Gizmos layer (where we temporarily moved the model)
            CullingMask = 1 << DefaultLayers.GizmosIndex
        };

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

        Engine.Rendering.State.IsSceneCapturePass = true;
        try
        {
            viewport.CollectVisible(false, world, camera);
            viewport.SwapBuffers(); // match scene capture flow: swap between collect and render
            viewport.Render(fbo, world, camera);

            // After rendering into the array layer, copy that slice into the matching 2D texture
            // so editor previews (which consume XRTexture2D) have pixels without a second render.
            if (previewLayer is not null)
                CopyArrayLayerToSlice(colorArray, viewIndex, previewLayer);
        }
        finally
        {
            Engine.Rendering.State.IsSceneCapturePass = false;
        }

        return true;
    }

    private static Vector3[] BuildCaptureDirections()
    {
        List<Vector3> directions = new(26)
        {
            // Ordering matters: keep in sync with Build/CommonAssets/Shaders/Tools/OctahedralImposterBlend.fs.
            // The runtime shader relies on this exact layout when selecting sampler bindings.
            // 1. World axes (6)
            // 2. Mid-axes (12) � 45� between the primary axes
            // 3. Elevated diagonals (8) � 45� above/below the edge directions

            Vector3.UnitX,
            -Vector3.UnitX,
            Vector3.UnitY,
            -Vector3.UnitY,
            Vector3.UnitZ,
            -Vector3.UnitZ
        };

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

        return [.. directions];
    }

    private static void CopyArrayLayerToSlice(XRTexture2DArray sourceArray, int layer, XRTexture2D slice)
    {
        if (AbstractRenderer.Current is not OpenGLRenderer gl)
            return;

        var glArray = gl.GenericToAPI<GLTexture2DArray>(sourceArray);
        var glSlice = gl.GenericToAPI<GLTexture2D>(slice);
        if (glArray is null || glSlice is null)
            return;

        // Ensure destination slice matches source format and is allocated on GPU.
        slice.SizedInternalFormat = sourceArray.SizedInternalFormat;
        if (slice.Width != sourceArray.Width || slice.Height != sourceArray.Height)
            slice.Resize(sourceArray.Width, sourceArray.Height);
        glSlice.PushData();

        uint srcId = glArray.BindingId;
        uint dstId = glSlice.BindingId;
        var api = gl.RawGL;
        if (!api.IsTexture(srcId) || !api.IsTexture(dstId))
            return;

        api.CopyImageSubData(
            srcId, CopyImageSubDataTarget.Texture2DArray, 0, 0, 0, layer,
            dstId, CopyImageSubDataTarget.Texture2D, 0, 0, 0, 0,
            slice.Width, slice.Height, 1);
    }
}
