using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using XREngine.Editor.Services;
using XREngine.Rendering;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Draws live previews of pipeline textures and framebuffer attachments.
/// The asset inspector and the Render Pipeline Workbench share this type.
/// Previews run only on the render thread, because they create texture views.
/// </summary>
public static class RenderTexturePreviewPanel
{
    public static void DrawFrameBufferInputs(XRFrameBuffer fbo, bool flipPreview)
    {
        var inputs = CollectFrameBufferInputs(fbo);
        if (inputs.Count == 0)
        {
            ImGui.TextDisabled("No input textures detected for this framebuffer.");
            return;
        }

        for (int i = 0; i < inputs.Count; i++)
        {
            XRTexture tex = inputs[i];
            ImGui.PushID(tex.GetHashCode());
            string label = tex.Name ?? tex.GetType().Name;
            ImGui.TextUnformatted(label);
            DrawTexturePreview(tex, flipPreview);
            ImGui.PopID();
            if (i < inputs.Count - 1)
                ImGui.Separator();
        }
    }

    public static void DrawFrameBufferOutputs(XRFrameBuffer fbo, bool flipPreview)
    {
        var targets = fbo.Targets;
        if (targets is null || targets.Length == 0)
        {
            ImGui.TextDisabled("Framebuffer has no attachments.");
            return;
        }

        for (int i = 0; i < targets.Length; i++)
        {
            var (target, attachment, mipLevel, layerIndex) = targets[i];
            ImGui.PushID(i);

            string attachmentLabel = $"[{i + 1}] {attachment}";
            if (mipLevel > 0)
                attachmentLabel += $" | Mip {mipLevel}";
            if (layerIndex >= 0)
                attachmentLabel += $" | Layer {layerIndex}";
            ImGui.TextUnformatted(attachmentLabel);

            if (target is XRTexture tex)
            {
                string texLabel = tex.Name ?? tex.GetType().Name;
                ImGui.TextDisabled(texLabel);
                DrawTexturePreview(tex, flipPreview);
            }
            else
            {
                ImGui.TextDisabled("Attachment is not a texture.");
            }

            ImGui.PopID();
            if (i < targets.Length - 1)
                ImGui.Separator();
        }
    }

    public static void DrawTexturePreview(XRTexture texture, bool flipPreview)
    {
        var previewState = GetPreviewState(texture);

        int layerCount = GetLayerCount(texture);
        int mipCount = GetMipCount(texture);

        DrawPreviewControls(previewState, layerCount, mipCount);

        if (TryGetTexturePreviewHandle(texture, 320f, previewState, previewState.Channel, out nint handle, out Vector2 displaySize, out Vector2 pixelSize, out string failure))
        {
            Vector2 uv0 = flipPreview ? new Vector2(0f, 1f) : Vector2.Zero;
            Vector2 uv1 = flipPreview ? new Vector2(1f, 0f) : Vector2.One;
            ImGui.Image(handle, displaySize, uv0, uv1);

            var infoParts = new List<string>
            {
                $"Size: {pixelSize.X} x {pixelSize.Y}",
                GetChannelLabel(previewState.Channel)
            };

            if (layerCount > 1)
                infoParts.Add($"Layer {previewState.Layer}");
            if (mipCount > 1)
                infoParts.Add($"Mip {previewState.Mip}");

            ImGui.TextDisabled(string.Join(" | ", infoParts));
        }
        else
        {
            ImGui.TextDisabled(failure);
        }

        ImGui.Spacing();
        DrawAuxiliaryChannelPreviews(texture, flipPreview, previewState);
    }

    private static void DrawAuxiliaryChannelPreviews(XRTexture texture, bool flipPreview, RenderTexturePreviewState previewState)
    {
        ImGui.TextDisabled("Alpha");
        DrawChannelPreview(texture, flipPreview, previewState, ETexturePreviewChannel.A, "##TexturePreviewAlpha", 112f);

        if (!previewState.ShowSeparateRgbChannels)
            return;

        if (ImGui.BeginTable("##TexturePreviewRgbChannels", 3, ImGuiTableFlags.SizingStretchSame))
        {
            DrawChannelPreviewColumn(texture, flipPreview, previewState, ETexturePreviewChannel.R, "##TexturePreviewRed");
            DrawChannelPreviewColumn(texture, flipPreview, previewState, ETexturePreviewChannel.G, "##TexturePreviewGreen");
            DrawChannelPreviewColumn(texture, flipPreview, previewState, ETexturePreviewChannel.B, "##TexturePreviewBlue");
            ImGui.EndTable();
        }
    }

    private static void DrawChannelPreviewColumn(XRTexture texture, bool flipPreview, RenderTexturePreviewState previewState, ETexturePreviewChannel channel, string id)
    {
        ImGui.TableNextColumn();
        ImGui.TextDisabled(GetChannelLabel(channel));
        DrawChannelPreview(texture, flipPreview, previewState, channel, id, 96f);
    }

    private static void DrawChannelPreview(
        XRTexture texture,
        bool flipPreview,
        RenderTexturePreviewState previewState,
        ETexturePreviewChannel channel,
        string imageId,
        float maxEdge)
    {
        if (!TryGetTexturePreviewHandle(texture, maxEdge, previewState, channel, out nint handle, out Vector2 displaySize, out _, out string failure))
        {
            ImGui.TextDisabled(failure);
            return;
        }

        Vector2 uv0 = flipPreview ? new Vector2(0f, 1f) : Vector2.Zero;
        Vector2 uv1 = flipPreview ? new Vector2(1f, 0f) : Vector2.One;
        ImGui.PushID(imageId);
        ImGui.Image(handle, displaySize, uv0, uv1);
        ImGui.PopID();
    }

    public static List<XRTexture> CollectFrameBufferInputs(XRFrameBuffer fbo)
    {
        if (fbo is XRMaterialFrameBuffer materialFbo && materialFbo.Material is { } material)
        {
            var unique = new List<XRTexture>();
            foreach (XRTexture? tex in material.Textures)
            {
                if (tex is null)
                    continue;

                if (unique.Any(existing => ReferenceEquals(existing, tex)))
                    continue;

                unique.Add(tex);
            }

            return unique;
        }

        return new List<XRTexture>();
    }


    public static bool TryDescribeTextureDimensions(XRTexture texture, out string sizeLabel)
    {
        switch (texture)
        {
            case XRTexture2D tex2D:
                sizeLabel = $"Size: {tex2D.Width} x {tex2D.Height}";
                return true;
            case XRTexture2DArray tex2DArray:
                sizeLabel = $"Size: {tex2DArray.Width} x {tex2DArray.Height} x {tex2DArray.Depth}";
                return true;
            case XRTextureViewBase view:
                sizeLabel = $"View Size: {view.WidthHeightDepth.X} x {view.WidthHeightDepth.Y} x {view.WidthHeightDepth.Z}";
                return true;
            default:
                sizeLabel = string.Empty;
                return false;
        }
    }



    private static bool TryGetTexturePreviewHandle(
        XRTexture texture,
        float maxEdge,
        RenderTexturePreviewState previewState,
        ETexturePreviewChannel channel,
        out nint handle,
        out Vector2 displaySize,
        out Vector2 pixelSize,
        out string failure)
    {
        handle = nint.Zero;
        displaySize = new Vector2(64f, 64f);
        pixelSize = displaySize;
        failure = string.Empty;

        if (!Engine.IsRenderThread)
        {
            failure = "Preview unavailable off render thread";
            return false;
        }

        XRTexture? previewTexture = GetPreviewTexture(texture, previewState, out int mipLevel, out int layerIndex, out string previewError);
        if (previewTexture is null)
        {
            failure = previewError;
            return false;
        }

        Vector2 fullSize = GetPixelSizeForPreview(texture, mipLevel);
        pixelSize = fullSize;
        float largest = MathF.Max(1f, MathF.Max(pixelSize.X, pixelSize.Y));
        float scale = largest > 0f ? MathF.Min(1f, maxEdge / largest) : 1f;
        displaySize = new Vector2(pixelSize.X * scale, pixelSize.Y * scale);

        previewTexture = GetIsolatedPreviewTexture(previewTexture, mipLevel, layerIndex, channel);
        var options = new RenderTexturePreviewOptions(
            ToPreviewChannel(channel),
            ForceBaseMipSampling: previewTexture is XRTextureViewBase);
        // Never modify GL state on raw (non-view) texture handles — swizzle/sampling
        // changes would leak into the live render pipeline.
        bool success = EditorTexturePreviewService.TryGetHandle(
            previewTexture,
            in options,
            out handle,
            out _,
            out string? previewFailure);
        failure = previewFailure ?? string.Empty;
        return success;
    }

    private static XRTexture GetIsolatedPreviewTexture(XRTexture texture, int mipLevel, int layerIndex, ETexturePreviewChannel channel)
    {
        switch (texture)
        {
            case XRTexture2D tex2D:
                if (tex2D.Resizable)
                    return tex2D; // Mutable storage: glTextureView requires immutable storage, fall back to raw texture.
                return mipLevel > 0 || channel != ETexturePreviewChannel.RGBA
                    ? GetOrCreate2DView(tex2D, mipLevel, channel)
                    : tex2D;

            case XRTexture2DArray tex2DArray:
                if (tex2DArray.Resizable)
                    return texture;
                return GetOrCreateArrayView(tex2DArray, layerIndex, mipLevel, channel);

            case XRTextureViewBase:
                return texture;

            default:
                return texture;
        }
    }






    private static readonly Dictionary<object, RenderTexturePreviewState> PreviewStates = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<object, Dictionary<(int mip, int layer, ETexturePreviewChannel channel), XRTextureViewBase>> PreviewViews = new(ReferenceEqualityComparer.Instance);

    private static RenderTexturePreviewState GetPreviewState(XRTexture texture)
    {
        if (!PreviewStates.TryGetValue(texture, out var state))
        {
            state = new RenderTexturePreviewState();
            PreviewStates[texture] = state;
        }

        int maxLayers = GetLayerCount(texture);
        if (state.Layer >= maxLayers)
            state.Layer = Math.Max(0, maxLayers - 1);

        int maxMips = GetMipCount(texture);
        if (state.Mip >= maxMips)
            state.Mip = Math.Max(0, maxMips - 1);

        return state;
    }

    private static void DrawPreviewControls(RenderTexturePreviewState state, int layerCount, int mipCount)
    {
        if (layerCount > 1)
        {
            int layer = state.Layer;
            if (ImGui.SliderInt("Layer##TexturePreview", ref layer, 0, layerCount - 1))
                state.Layer = layer;
        }

        if (mipCount > 1)
        {
            int mip = state.Mip;
            if (ImGui.SliderInt("Mip##TexturePreview", ref mip, 0, mipCount - 1))
                state.Mip = mip;
        }

        string channelLabel = GetChannelLabel(state.Channel);
        if (ImGui.BeginCombo("Channel##TexturePreview", channelLabel))
        {
            foreach (ETexturePreviewChannel view in Enum.GetValues<ETexturePreviewChannel>())
            {
                bool selected = view == state.Channel;
                if (ImGui.Selectable(GetChannelLabel(view), selected))
                    state.Channel = view;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.Checkbox("Show Separate RGB Channels##TexturePreview", ref state.ShowSeparateRgbChannels);
    }

    private static string GetChannelLabel(ETexturePreviewChannel channel)
        => channel switch
        {
            ETexturePreviewChannel.R => "Red",
            ETexturePreviewChannel.G => "Green",
            ETexturePreviewChannel.B => "Blue",
            ETexturePreviewChannel.A => "Alpha",
            ETexturePreviewChannel.Luminance => "Luma",
            _ => "RGBA",
        };

    private static RenderTexturePreviewChannel ToPreviewChannel(ETexturePreviewChannel channel)
        => channel switch
        {
            ETexturePreviewChannel.R => RenderTexturePreviewChannel.Red,
            ETexturePreviewChannel.G => RenderTexturePreviewChannel.Green,
            ETexturePreviewChannel.B => RenderTexturePreviewChannel.Blue,
            ETexturePreviewChannel.A => RenderTexturePreviewChannel.Alpha,
            ETexturePreviewChannel.Luminance => RenderTexturePreviewChannel.Luminance,
            _ => RenderTexturePreviewChannel.Rgba,
        };

    private static int GetLayerCount(XRTexture texture)
        => texture switch
        {
            XRTexture2DArray array => (int)Math.Max(1u, array.Depth),
            XRTexture2DArrayView view => (int)Math.Max(1u, view.NumLayers),
            _ => 1,
        };

    private static int GetMipCount(XRTexture texture)
    {
        switch (texture)
        {
            case XRTexture2D tex2D when tex2D.Mipmaps is { Length: > 0 } mips:
                return mips.Length;
            case XRTexture2D tex2D:
                return Math.Max(1, tex2D.SmallestMipmapLevel + 1);
            case XRTexture2DArray array when array.Mipmaps is { Length: > 0 } arrayMips:
                return arrayMips.Length;
            case XRTexture2DArray array:
                return Math.Max(1, array.SmallestMipmapLevel + 1);
            case XRTextureViewBase view:
                return (int)Math.Max(1u, view.NumLevels);
            default:
                return 1;
        }
    }

    private static XRTexture? GetPreviewTexture(XRTexture texture, RenderTexturePreviewState state, out int mipLevel, out int layerIndex, out string failure)
    {
        mipLevel = Math.Max(0, state.Mip);
        layerIndex = Math.Max(0, state.Layer);
        failure = string.Empty;

        switch (texture)
        {
            case XRTexture2DArray arrayTex:
                int clampedLayer = Math.Min(layerIndex, Math.Max(0, GetLayerCount(arrayTex) - 1));
                int clampedMip = Math.Min(mipLevel, Math.Max(0, GetMipCount(arrayTex) - 1));
                state.Layer = clampedLayer;
                state.Mip = clampedMip;
                return GetOrCreateArrayView(arrayTex, clampedLayer, clampedMip, ETexturePreviewChannel.RGBA);

            case XRTexture2D tex2D when state.Mip > 0:
                int clamped2DMip = Math.Min(mipLevel, Math.Max(0, GetMipCount(tex2D) - 1));
                state.Mip = clamped2DMip;
                return GetOrCreate2DView(tex2D, clamped2DMip, ETexturePreviewChannel.RGBA);

            case XRTexture2D tex2D:
                state.Mip = 0;
                state.Layer = 0;
                return tex2D;

            case XRTextureViewBase view:
                return view;

            default:
                failure = "Only 2D and 2D array textures supported";
                return null;
        }
    }

    private static XRTexture2DView GetOrCreate2DView(XRTexture2D texture, int mip, ETexturePreviewChannel channel)
    {
        if (!PreviewViews.TryGetValue(texture, out var views))
        {
            views = new Dictionary<(int mip, int layer, ETexturePreviewChannel channel), XRTextureViewBase>();
            PreviewViews[texture] = views;
        }

        var key = (mip, 0, channel);
        if (views.TryGetValue(key, out var existing) && existing is XRTexture2DView cachedView)
        {
            cachedView.MinLevel = (uint)mip;
            cachedView.NumLevels = 1;
            return cachedView;
        }

        var view = new XRTexture2DView(texture, (uint)mip, 1u, texture.SizedInternalFormat, false, texture.MultiSample);
        views[key] = view;
        return view;
    }

    private static XRTexture2DArrayView GetOrCreateArrayView(XRTexture2DArray texture, int layer, int mip, ETexturePreviewChannel channel)
    {
        if (!PreviewViews.TryGetValue(texture, out var views))
        {
            views = new Dictionary<(int mip, int layer, ETexturePreviewChannel channel), XRTextureViewBase>();
            PreviewViews[texture] = views;
        }

        var key = (mip, layer, channel);
        if (views.TryGetValue(key, out var existing) && existing is XRTexture2DArrayView cachedView)
        {
            cachedView.MinLevel = (uint)mip;
            cachedView.NumLevels = 1;
            cachedView.MinLayer = (uint)layer;
            cachedView.NumLayers = 1;
            return cachedView;
        }

        var view = new XRTexture2DArrayView(texture, (uint)mip, 1u, (uint)layer, 1u, texture.SizedInternalFormat, false, texture.MultiSample);
        views[key] = view;
        return view;
    }

    private static Vector2 GetPixelSizeForPreview(XRTexture texture, int mipLevel)
    {
        static uint Shifted(uint value, int mip)
            => Math.Max(1u, value >> mip);

        return texture switch
        {
            XRTexture2D tex2D => new Vector2(Shifted(tex2D.Width, mipLevel), Shifted(tex2D.Height, mipLevel)),
            XRTexture2DArray array => new Vector2(Shifted(array.Width, mipLevel), Shifted(array.Height, mipLevel)),
            // Views report the *source* dimensions; account for the view's base mip.
            XRTextureViewBase view => new Vector2(
                Shifted((uint)view.WidthHeightDepth.X, mipLevel + (int)view.MinLevel),
                Shifted((uint)view.WidthHeightDepth.Y, mipLevel + (int)view.MinLevel)),
            _ => new Vector2(1f, 1f),
        };
    }

}
