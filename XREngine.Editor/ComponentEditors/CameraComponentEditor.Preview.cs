using ImGuiNET;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Data.Rendering;
using XREngine.Editor.Services;
using XREngine.Rendering;
using XREngine.Rendering.Resources;
using XREngine.Scene;

namespace XREngine.Editor.ComponentEditors;

public sealed partial class CameraComponentEditor
{
    private const float PreviewMaxEdge = 256.0f;
    private const float PreviewMinEdge = 96.0f;
    private const double PreviewDiscoveryRefreshSeconds = 0.125;
    private const int CascadePreviewsPerRow = 2;

    /// <summary>
    /// Caches only preview discovery metadata. GPU handles are deliberately resolved for every visible frame,
    /// because a render-target recreation may invalidate them without changing the texture object identity.
    /// </summary>
    private sealed class CameraPreviewState
    {
        public XRCamera? Camera;
        public XRFrameBuffer? DefaultRenderTarget;
        public XRViewport? PreferredViewport;
        public XRRenderPipelineInstance? PreferredInstance;
        public XRTexture? Texture;
        public Vector2 PixelSize;
        public string? SourceLabel;
        public string? Failure;
        public long NextDiscoveryTimestamp;
    }

    private static readonly ConditionalWeakTable<XRTexture2DArray, Dictionary<int, XRTexture2DArrayView>> CascadePreviewViews = new();
    private static readonly ConditionalWeakTable<XRCamera, CameraPreviewState> PreviewStates = new();

    /// <summary>
    /// Draws the camera's actual render output. The selected pipeline used by the editor is deliberately
    /// not an input to this lookup: editing another pipeline must never make the preview claim it rendered.
    /// </summary>
    private static void DrawPreviewSection(
        XRCamera camera,
        CameraComponent? component,
        RenderPipeline activePipeline,
        XRViewport? preferredViewport,
        XRRenderPipelineInstance? preferredInstance)
    {
        if (!Engine.GlobalEditorPreferences.ShowCameraPreviews)
            return;

        using var profilerScope = Engine.Profiler.Start("UI.ComponentEditor.CameraComponent.Preview");

        if (!Engine.IsRenderThread)
        {
            ImGui.TextDisabled("Preview is available only on the render thread.");
            return;
        }

        CameraPreviewState previewState = PreviewStates.GetOrCreateValue(camera);
        Vector2 reservedSize = CalculatePreviewSize(
            previewState.PixelSize.X > 0.0f && previewState.PixelSize.Y > 0.0f
                ? previewState.PixelSize
                : new Vector2(PreviewMaxEdge, PreviewMaxEdge));
        bool previewImageVisible = ImGui.IsRectVisible(reservedSize);
        bool hasPreviewImage = false;
        bool requiresVerticalFlip = false;
        nint handle = nint.Zero;
        XRTexture? resolvedTexture = null;
        string? handleFailure = null;

        if (previewImageVisible)
        {
            RefreshPreviewDiscovery(camera, component, activePipeline, preferredViewport, preferredInstance, previewState, force: false);
            if (previewState.Texture is XRTexture discoveredTexture
                && TryGetTextureHandle(discoveredTexture, out handle, out requiresVerticalFlip, out handleFailure))
            {
                resolvedTexture = discoveredTexture;
                hasPreviewImage = true;
            }
            else if (previewState.Texture is not null)
            {
                // The cached texture may have been recreated in-place. Re-discover once before reporting failure.
                RefreshPreviewDiscovery(camera, component, activePipeline, preferredViewport, preferredInstance, previewState, force: true);
                if (previewState.Texture is XRTexture refreshedTexture
                    && TryGetTextureHandle(refreshedTexture, out handle, out requiresVerticalFlip, out handleFailure))
                {
                    resolvedTexture = refreshedTexture;
                    hasPreviewImage = true;
                }
            }
        }

        Vector2 pixelSize = previewState.PixelSize;
        Vector2 displaySize = CalculatePreviewSize(pixelSize);
        // The preview backend owns texture orientation (OpenGL flips; Vulkan does not).
        Vector2 uv0 = requiresVerticalFlip ? new(0.0f, 1.0f) : Vector2.Zero;
        Vector2 uv1 = requiresVerticalFlip ? new(1.0f, 0.0f) : Vector2.One;
        bool openDialog = false;

        if (hasPreviewImage)
        {
            ImGui.Image(handle, displaySize, uv0, uv1);
            if (ImGui.IsItemHovered())
            {
                string formatLabel = resolvedTexture is XRTexture2D tex2D
                    ? tex2D.SizedInternalFormat.ToString()
                    : resolvedTexture!.GetType().Name;
                ImGui.SetTooltip($"{pixelSize.X:0} x {pixelSize.Y:0} | {formatLabel}");
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    openDialog = true;
            }
        }
        else if (previewImageVisible)
        {
            DrawPreviewFailurePlaceholder(
                "CameraPreviewFailure",
                reservedSize,
                handleFailure ?? previewState.Failure ?? "Failed to acquire GPU handle.");
        }
        else
        {
            // Keep the image slot in the scroll layout while avoiding discovery and handle resolution when clipped.
            ImGui.Dummy(reservedSize);
        }

        if (previewState.Texture is XRTexture metadataTexture)
        {
            ImGui.TextDisabled($"{pixelSize.X:0} x {pixelSize.Y:0} ({metadataTexture.GetType().Name})");
            if (!string.IsNullOrWhiteSpace(previewState.SourceLabel))
                ImGui.TextWrapped(previewState.SourceLabel);
            using (new ImGuiDisabledScope(!hasPreviewImage))
            {
                if (ImGui.Button("Open Preview Window"))
                    openDialog = true;
            }
        }
        else if (!previewImageVisible)
        {
            ImGui.TextDisabled(previewState.Failure ?? "Preview unavailable.");
        }

        if (openDialog && resolvedTexture is not null)
            ComponentEditorLayout.RequestPreviewDialog(component?.SceneNode?.Name ?? "Camera Preview", handle, pixelSize, flipVertically: requiresVerticalFlip, isCameraPreview: true);

        // Cascades have their own layout and must continue to submit independently of the main preview image.
        if (component is not null)
            DrawActiveCascadeDepthPreviews(component);
    }

    private static void DrawActiveCascadeDepthPreviews(CameraComponent component)
    {
        if (!Engine.GlobalEditorPreferences.ShowCameraPreviews)
            return;

        if (component.DirectionalShadowRenderingMode != EDirectionalShadowRenderingMode.Cascaded)
            return;

        IRuntimeRenderWorld? world = component.WorldAs<IRuntimeRenderWorld>();
        if (world?.Lights is null)
            return;

        if (!ImGui.CollapsingHeader("Directional Shadow Cascades"))
            return;

        DirectionalLightComponent[] lights;
        try
        {
            lights = [.. world.Lights.DynamicDirectionalLights];
        }
        catch (InvalidOperationException)
        {
            ImGui.Separator();
            ImGui.TextDisabled("Cascade previews are updating.");
            return;
        }

        bool drewAny = false;
        foreach (DirectionalLightComponent light in lights)
        {
            if (!light.CastsShadows || !light.EnableCascadedShadows)
                continue;

            XRTexture2DArray? cascadeTexture = light.CascadedShadowPreviewTexture;
            int activeCascades = light.ActiveCascadeCount;
            if (cascadeTexture is null || activeCascades <= 0)
                continue;

            drewAny = true;

            DrawCascadePreviewGroup(light, cascadeTexture, activeCascades);
        }
        if (!drewAny)
            ImGui.TextDisabled("No active cascade previews.");
    }

    private static void DrawCascadePreviewGroup(DirectionalLightComponent light, XRTexture2DArray cascadeTexture, int activeCascades)
    {
        string lightLabel = light.SceneNode?.Name ?? light.Name ?? light.GetType().Name;

        ImGui.PushID(light.GetHashCode());
        ImGui.TextDisabled($"{lightLabel}: {activeCascades} active cascade(s)");

        Vector2 pixelSize = GetPixelSize(cascadeTexture);
        Vector2 displaySize = CalculatePreviewSize(pixelSize);
        Vector2 reservedSize = new(displaySize.X, displaySize.Y + ImGui.GetFrameHeightWithSpacing() + 2.0f * ImGui.GetTextLineHeightWithSpacing());
        for (int cascadeIndex = 0; cascadeIndex < activeCascades; cascadeIndex++)
        {
            if (cascadeIndex > 0 && cascadeIndex % CascadePreviewsPerRow != 0)
                ImGui.SameLine();

            if (!ImGui.IsRectVisible(reservedSize))
            {
                // Each cascade reserves its complete group before allocating a layer view or resolving its handle.
                ImGui.Dummy(reservedSize);
                continue;
            }

            XRTexture2DArrayView cascadeView = GetOrCreateCascadePreviewView(cascadeTexture, cascadeIndex);
            if (!TryGetTextureHandle(cascadeView, out nint handle, out bool requiresVerticalFlip, out string? handleFailure))
            {
                ImGui.BeginGroup();
                DrawPreviewFailurePlaceholder(
                    $"CascadePreviewFailure{cascadeIndex}",
                    displaySize,
                    handleFailure ?? $"Cascade {cascadeIndex} preview unavailable.");
                ImGui.TextDisabled($"Cascade {cascadeIndex}");
                ImGui.TextDisabled($"Split {light.GetCascadeSplit(cascadeIndex):F1}");
                using (new ImGuiDisabledScope(true))
                    ImGui.SmallButton($"Open##Cascade{cascadeIndex}");
                ImGui.EndGroup();
                continue;
            }

            Vector2 uv0 = requiresVerticalFlip ? new(0.0f, 1.0f) : Vector2.Zero;
            Vector2 uv1 = requiresVerticalFlip ? new(1.0f, 0.0f) : Vector2.One;
            bool openDialog = false;

            ImGui.BeginGroup();
            ImGui.Image(handle, displaySize, uv0, uv1);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"{lightLabel} | Cascade {cascadeIndex}\n" +
                    $"{pixelSize.X:0} x {pixelSize.Y:0} | {cascadeTexture.SizedInternalFormat}\n" +
                    $"Split Far: {light.GetCascadeSplit(cascadeIndex):F1}");
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    openDialog = true;
            }

            ImGui.TextDisabled($"Cascade {cascadeIndex}");
            ImGui.TextDisabled($"Split {light.GetCascadeSplit(cascadeIndex):F1}");
            if (ImGui.SmallButton($"Open##Cascade{cascadeIndex}"))
                openDialog = true;

            if (openDialog)
                ComponentEditorLayout.RequestPreviewDialog($"{lightLabel} Cascade {cascadeIndex}", handle, pixelSize, flipVertically: requiresVerticalFlip, isCameraPreview: true);

            ImGui.EndGroup();
        }

        ImGui.PopID();
    }

    private static XRTexture2DArrayView GetOrCreateCascadePreviewView(XRTexture2DArray texture, int layerIndex)
    {
        var views = CascadePreviewViews.GetOrCreateValue(texture);
        if (views.TryGetValue(layerIndex, out XRTexture2DArrayView? existing))
        {
            existing.MinLevel = 0u;
            existing.NumLevels = 1u;
            existing.MinLayer = (uint)layerIndex;
            existing.NumLayers = 1u;
            return existing;
        }

        var view = new XRTexture2DArrayView(texture, 0u, 1u, (uint)layerIndex, 1u, texture.SizedInternalFormat, false, texture.MultiSample)
        {
            Name = $"{texture.Name ?? "CascadeShadow"}_Layer{layerIndex}_Preview",
            MinFilter = ETexMinFilter.Linear,
            MagFilter = ETexMagFilter.Linear,
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.ClampToEdge,
        };

        views[layerIndex] = view;
        return view;
    }

    private static bool TryResolvePreviewTexture(
        XRCamera camera,
        CameraComponent? component,
        RenderPipeline activePipeline,
        XRViewport? preferredViewport,
        XRRenderPipelineInstance? preferredInstance,
        out XRTexture? texture,
        out Vector2 pixelSize,
        out string? sourceLabel,
        out string? failure)
    {
        if (preferredInstance is not null
            && TryResolvePipelineTexture(preferredInstance, preferredInstance.Pipeline ?? activePipeline, out texture))
        {
            pixelSize = GetPixelSize(texture);
            sourceLabel = $"Rendered by {(preferredInstance.Pipeline ?? activePipeline).DebugName}";
            failure = null;
            return true;
        }

        if (preferredViewport is not null && TryResolveViewportPreviewTexture(preferredViewport, activePipeline, out texture, out pixelSize, out sourceLabel))
        {
            failure = null;
            return true;
        }

        if (component is not null && TryExtractTextureFromFbo(component.DefaultRenderTarget, out texture, out pixelSize))
        {
            sourceLabel = "Camera output target";
            failure = null;
            return true;
        }

        // Take a snapshot of the viewports collection to avoid "Collection was modified" exceptions
        // during play mode transitions when viewports may be added/removed concurrently.
        XRViewport[] viewportsSnapshot;
        try
        {
            viewportsSnapshot = [.. camera.Viewports];
        }
        catch (InvalidOperationException)
        {
            // Collection was modified during snapshot - return failure gracefully
            texture = null;
            pixelSize = Vector2.Zero;
            sourceLabel = null;
            failure = "Viewports collection is being modified.";
            return false;
        }

        foreach (var viewport in viewportsSnapshot)
        {
            if (viewport is null || !ReferenceEquals(viewport.ActiveCamera, camera))
                continue;

            if (TryResolveViewportPreviewTexture(viewport, activePipeline, out texture, out pixelSize, out sourceLabel))
            {
                failure = null;
                return true;
            }
        }

        texture = null;
        pixelSize = Vector2.Zero;
        sourceLabel = null;
        failure = "No render target or viewport texture is available.";
        return false;
    }

    private static bool TryResolveViewportPreviewTexture(
        XRViewport viewport,
        RenderPipeline fallbackPipeline,
        out XRTexture? texture,
        out Vector2 pixelSize,
        out string? sourceLabel)
    {
        RenderPipeline pipeline = viewport.RenderPipeline
            ?? viewport.RenderPipelineInstance.Pipeline
            ?? fallbackPipeline;
        if (TryResolvePipelineTexture(viewport.RenderPipelineInstance, pipeline, out texture))
        {
            pixelSize = GetPixelSize(texture);
            sourceLabel = $"Rendered by {pipeline.DebugName}";
            return true;
        }

        pixelSize = Vector2.Zero;
        sourceLabel = null;
        return false;
    }

    private static void RefreshPreviewDiscovery(
        XRCamera camera,
        CameraComponent? component,
        RenderPipeline activePipeline,
        XRViewport? preferredViewport,
        XRRenderPipelineInstance? preferredInstance,
        CameraPreviewState state,
        bool force)
    {
        long now = Stopwatch.GetTimestamp();
        bool sourceChanged = !ReferenceEquals(state.Camera, camera)
            || !ReferenceEquals(state.DefaultRenderTarget, component?.DefaultRenderTarget)
            || !ReferenceEquals(state.PreferredViewport, preferredViewport)
            || !ReferenceEquals(state.PreferredInstance, preferredInstance);
        if (!force && !sourceChanged && now < state.NextDiscoveryTimestamp)
            return;

        state.Camera = camera;
        state.DefaultRenderTarget = component?.DefaultRenderTarget;
        state.PreferredViewport = preferredViewport;
        state.PreferredInstance = preferredInstance;
        if (TryResolvePreviewTexture(camera, component, activePipeline, preferredViewport, preferredInstance, out XRTexture? texture, out Vector2 pixelSize, out string? sourceLabel, out string? failure))
        {
            state.Texture = texture;
            state.PixelSize = pixelSize;
            state.SourceLabel = sourceLabel;
            state.Failure = null;
        }
        else
        {
            state.Texture = null;
            state.PixelSize = Vector2.Zero;
            state.SourceLabel = null;
            state.Failure = failure;
        }

        state.NextDiscoveryTimestamp = now + (long)(Stopwatch.Frequency * PreviewDiscoveryRefreshSeconds);
    }
    private static bool TryExtractTextureFromFbo(XRFrameBuffer? fbo, out XRTexture? texture, out Vector2 pixelSize)
    {
        texture = null;
        pixelSize = Vector2.Zero;
        if (fbo?.Targets is null)
            return false;

        foreach (var (target, attachment, _, _) in fbo.Targets)
        {
            if (!IsColorAttachment(attachment) || target is not XRTexture tex)
                continue;

            texture = tex;
            pixelSize = GetPixelSize(tex);
            return true;
        }

        return false;
    }

    private static bool TryResolvePipelineTexture(
        XRRenderPipelineInstance pipelineInstance,
        RenderPipeline? pipeline,
        [NotNullWhen(true)] out XRTexture? texture)
    {
        texture = null;
        RenderResourceRegistry resources = pipelineInstance.Resources;

        pipeline ??= pipelineInstance.Pipeline;

        IReadOnlyList<string> preferredTextures = pipeline?.PreferredPreviewTextureNames
            ?? Array.Empty<string>();

        foreach (string name in preferredTextures)
        {
            if (resources.TryGetTexture(name, out XRTexture? candidate) && candidate is XRTexture2D)
            {
                texture = candidate!;
                return true;
            }
        }

        IReadOnlyList<string> preferredFbos = pipeline?.PreferredPreviewFrameBufferNames
            ?? Array.Empty<string>();

        foreach (string fboName in preferredFbos)
        {
            if (resources.TryGetFrameBuffer(fboName, out XRFrameBuffer? fbo) && TryExtractTextureFromFbo(fbo, out XRTexture? candidate, out _))
            {
                texture = candidate!;
                return true;
            }
        }

        foreach (XRFrameBuffer fbo in resources.EnumerateFrameBufferInstances())
        {
            if (TryExtractTextureFromFbo(fbo, out XRTexture? candidate, out _))
            {
                texture = candidate!;
                return true;
            }
        }

        texture = resources.EnumerateTextureInstances()
            .OfType<XRTexture2D>()
            .OrderByDescending(GetPixelArea)
            .FirstOrDefault();

        return texture is not null;
    }

    private static float GetPixelArea(XRTexture texture)
    {
        Vector3 dims = texture.WidthHeightDepth;
        return MathF.Max(1.0f, dims.X) * MathF.Max(1.0f, dims.Y);
    }

    private static bool TryGetTextureHandle(XRTexture texture, out nint handle, out bool requiresVerticalFlip, out string? failure)
        => EditorTexturePreviewService.TryGetHandle(
            texture,
            out handle,
            out requiresVerticalFlip,
            out failure);

    private static Vector2 CalculatePreviewSize(Vector2 pixelSize)
    {
        float width = MathF.Max(1.0f, pixelSize.X);
        float height = MathF.Max(1.0f, pixelSize.Y);

        float scale = MathF.Min(PreviewMaxEdge / width, PreviewMaxEdge / height);
        scale = MathF.Min(scale, 1.0f);

        width = MathF.Max(PreviewMinEdge, width * scale);
        height = MathF.Max(PreviewMinEdge, height * scale);
        return new Vector2(width, height);
    }

    private static void DrawPreviewFailurePlaceholder(string id, Vector2 size, string failure)
    {
        if (ImGui.BeginChild(id, size, ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            ImGui.TextWrapped(failure);
        ImGui.EndChild();
    }
    private static Vector2 GetPixelSize(XRTexture texture)
    {
        Vector3 dims = texture.WidthHeightDepth;
        return new Vector2(MathF.Max(1.0f, dims.X), MathF.Max(1.0f, dims.Y));
    }


    private static bool IsColorAttachment(EFrameBufferAttachment attachment)
    {
        return attachment >= EFrameBufferAttachment.ColorAttachment0 && attachment <= EFrameBufferAttachment.ColorAttachment31;
    }
}
