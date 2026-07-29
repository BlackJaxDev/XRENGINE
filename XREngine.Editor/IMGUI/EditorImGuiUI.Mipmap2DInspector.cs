using System;
using System.Numerics;
using ImGuiNET;
using XREngine.Data.Rendering;
using XREngine.Editor.UI;
using XREngine.Rendering;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private const string Mipmap2DReimportFileDialogId = "Mipmap2DReimportFileDialog";

    private sealed class Mipmap2DPreviewState
    {
        public XRTexture2D? PreviewTexture;
        public uint LastWidth;
        public uint LastHeight;
        public EPixelInternalFormat LastInternalFormat;
        public EPixelFormat LastPixelFormat;
        public EPixelType LastPixelType;
        public object? LastDataRef;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mipmap2D, Mipmap2DPreviewState> _mipmap2DPreviewState = new();
    private static Mipmap2D? _pendingMipmap2DReimportTarget;

    private static void DrawMipmap2DInspector(Mipmap2D mip)
    {
        if (ImGui.CollapsingHeader("Preview", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawMipmap2DPreview(mip);
            ImGui.Spacing();

            if (ImGui.Button("Reimport...##Mipmap2D"))
                OpenMipmap2DReimportDialog(mip);

            ImGui.SameLine();
            ImGui.TextDisabled("Replaces this mipmap's image data from a file.");
        }

        DrawMipmap2DReimportDialogHandler();
    }

    private static void DrawMipmap2DPreview(Mipmap2D mip)
    {
        ImGui.TextDisabled($"{mip.Width} x {mip.Height} | {mip.PixelFormat} / {mip.PixelType} | {mip.InternalFormat}");

        if (!mip.HasData())
        {
            ImGui.TextDisabled("No data.");
            return;
        }

        if (!Engine.IsRenderThread)
        {
            ImGui.TextDisabled("Preview unavailable off render thread.");
            return;
        }

        var state = _mipmap2DPreviewState.GetOrCreateValue(mip);
        bool needsRefresh = state.PreviewTexture is null
            || state.LastWidth != mip.Width
            || state.LastHeight != mip.Height
            || state.LastInternalFormat != mip.InternalFormat
            || state.LastPixelFormat != mip.PixelFormat
            || state.LastPixelType != mip.PixelType
            || !ReferenceEquals(state.LastDataRef, mip.Data);

        if (needsRefresh)
        {
            state.LastWidth = mip.Width;
            state.LastHeight = mip.Height;
            state.LastInternalFormat = mip.InternalFormat;
            state.LastPixelFormat = mip.PixelFormat;
            state.LastPixelType = mip.PixelType;
            state.LastDataRef = mip.Data;

            state.PreviewTexture = new XRTexture2D
            {
                Name = "Mipmap Preview",
                Resizable = true,
                AutoGenerateMipmaps = false,
                Mipmaps = [mip.Clone(cloneImage: true)],
            };
        }

        XRTexture2D? texture = state.PreviewTexture;
        if (texture is null)
        {
            ImGui.TextDisabled("Preview unavailable.");
            return;
        }

        var options = new RenderTexturePreviewOptions(UploadIfNeeded: true);
        if (!EditorTexturePreviewService.TryGetHandle(
                texture,
                in options,
                out nint handle,
                out bool requiresVerticalFlip,
                out string? failureReason))
        {
            ImGui.TextDisabled(failureReason ?? "Preview texture not available.");
            return;
        }

        Vector2 pixelSize = new(Math.Max(1u, mip.Width), Math.Max(1u, mip.Height));
        float maxEdge = MathF.Max(64f, MathF.Min(512f, ImGui.GetContentRegionAvail().X));
        Vector2 displaySize = GetPreviewSizeForEdge(pixelSize, maxEdge);

        Vector2 uv0 = requiresVerticalFlip ? new Vector2(0, 1) : Vector2.Zero;
        Vector2 uv1 = requiresVerticalFlip ? new Vector2(1, 0) : Vector2.One;

        ImGui.Image(handle, displaySize, uv0, uv1);
    }

    private static void OpenMipmap2DReimportDialog(Mipmap2D target)
    {
        _pendingMipmap2DReimportTarget = target;

        ImGuiFileBrowser.OpenFile(
            Mipmap2DReimportFileDialogId,
            "Reimport Texture",
            result =>
            {
                if (!result.Success || string.IsNullOrWhiteSpace(result.SelectedPath))
                {
                    _pendingMipmap2DReimportTarget = null;
                    return;
                }

                var mip = _pendingMipmap2DReimportTarget;
                _pendingMipmap2DReimportTarget = null;
                if (mip is null)
                    return;

                try
                {
                    using var img = new ImageMagick.MagickImage(result.SelectedPath);
                    mip.SetFromImage(img);
                    mip.Invalidate();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex, $"Failed to reimport mipmap from '{result.SelectedPath}'.");
                }
            },
            "Image Files (*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.tga;*.exr;*.hdr)|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.tga;*.exr;*.hdr|All Files (*.*)|*.*");
    }

    private static void DrawMipmap2DReimportDialogHandler()
    {
        // The file browser manages its own UI; this method exists to keep the call-site readable.
    }
}
