using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Rendering.OpenGL;

namespace XREngine.Editor.ComponentEditors;

/// <summary>
/// Provides custom ImGui inspector editors for OpenGL rendering objects:
/// GLFrameBuffer, GLRenderBuffer, and GLTexture types.
/// These editors are registered via attributes for automatic discovery.
/// </summary>
public static class GLObjectEditors
{
    private const float TexturePreviewMaxEdge = 128.0f;
    private const float TexturePreviewFallbackEdge = 64.0f;
    private static readonly Vector4 HeaderColor = new(0.4f, 0.7f, 1.0f, 1.0f);
    private static readonly Vector4 DisabledTextColor = new(0.5f, 0.5f, 0.5f, 1.0f);
    private static readonly Vector4 WarningTextColor = new(1.0f, 0.8f, 0.3f, 1.0f);
    private static readonly Vector4 DepthColor = new(0.8f, 0.6f, 0.2f, 1.0f);
    private static readonly Vector4 StencilColor = new(0.2f, 0.8f, 0.6f, 1.0f);
    private static readonly Vector4 MsaaColor = new(0.6f, 0.4f, 0.9f, 1.0f);

    // Preview dialog state
    private static bool _showPreviewDialog;
    private static string _previewDialogTitle = "Texture Preview";
    private static uint _previewDialogTextureId;
    private static Vector2 _previewDialogTextureSize;
    private static bool _previewDialogIsDepth;
    private static string _previewDialogFormat = string.Empty;

    #region GLFrameBuffer Editor

    /// <summary>
    /// Draws an ImGui inspector for a GLFrameBuffer.
    /// </summary>
    [GLFrameBufferEditor]
    public static void DrawGLFrameBufferInspector(OpenGLRenderer.GLObjectBase glObject)
    {
        if (glObject is not GLFrameBuffer glFbo)
        {
            ImGui.TextColored(WarningTextColor, "Expected GLFrameBuffer but received different type.");
            return;
        }

        DrawGLObjectHeader(glFbo);

        XRFrameBuffer? fbo = glFbo.Data as XRFrameBuffer;
        if (fbo is null)
        {
            ImGui.TextColored(DisabledTextColor, "No XRFrameBuffer data available.");
            return;
        }

        if (ImGui.CollapsingHeader("Framebuffer Properties", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawFrameBufferProperties(fbo);
        }

        if (ImGui.CollapsingHeader("Attachments", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawFrameBufferAttachments(fbo, glFbo.Renderer);
        }

        if (ImGui.CollapsingHeader("Attachment Previews", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawFrameBufferAllPreviews(fbo, glFbo.Renderer);
        }

        // Draw the resizable preview dialog if open
        DrawPreviewDialog();
    }

    private static void DrawFrameBufferProperties(XRFrameBuffer fbo)
    {
        const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg;
        if (!ImGui.BeginTable("FBOProperties", 2, tableFlags))
            return;

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Dimensions");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted($"{fbo.Width} x {fbo.Height}");

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Texture Types");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(fbo.TextureTypes.ToString());

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Target Count");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted((fbo.Targets?.Length ?? 0).ToString(CultureInfo.InvariantCulture));

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Draw Buffers");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(FormatDrawBuffers(fbo.DrawBuffers));

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Bound for Reading");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(XRFrameBuffer.BoundForReading == fbo ? "Yes" : "No");

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Bound for Writing");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(XRFrameBuffer.BoundForWriting == fbo ? "Yes" : "No");

        ImGui.EndTable();
    }

    private static void DrawFrameBufferAttachments(XRFrameBuffer fbo, OpenGLRenderer renderer)
    {
        var targets = fbo.Targets;
        if (targets is null || targets.Length == 0)
        {
            ImGui.TextColored(DisabledTextColor, "No attachments.");
            return;
        }

        const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp 
            | ImGuiTableFlags.RowBg 
            | ImGuiTableFlags.BordersInnerV
            | ImGuiTableFlags.Resizable;

        if (!ImGui.BeginTable("FBOAttachments", 4, tableFlags))
            return;

        ImGui.TableSetupColumn("Attachment", ImGuiTableColumnFlags.WidthStretch, 0.25f);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthStretch, 0.25f);
        ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthStretch, 0.2f);
        ImGui.TableSetupColumn("Details", ImGuiTableColumnFlags.WidthStretch, 0.3f);
        ImGui.TableHeadersRow();

        for (int i = 0; i < targets.Length; i++)
        {
            var (target, attachment, mipLevel, layerIndex) = targets[i];

            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            string attachmentText = attachment.ToString();
            if (mipLevel > 0)
                attachmentText += $" (Mip {mipLevel})";
            if (layerIndex >= 0)
                attachmentText += $" [Layer {layerIndex}]";
            
            // Color code attachment types
            Vector4 attachmentColor = GetAttachmentColor(attachment);
            ImGui.TextColored(attachmentColor, attachmentText);

            ImGui.TableSetColumnIndex(1);
            string targetType = target switch
            {
                XRTexture2D => "Texture2D",
                XRTexture2DArray => "Texture2DArray",
                XRTextureCube => "TextureCube",
                XRTexture3D => "Texture3D",
                XRRenderBuffer => "RenderBuffer",
                XRTexture tex => tex.GetType().Name,
                _ => target.GetType().Name
            };
            ImGui.TextUnformatted(targetType);

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted($"{target.Width} x {target.Height}");

            ImGui.TableSetColumnIndex(3);
            DrawAttachmentDetails(target, renderer);
        }

        ImGui.EndTable();
    }

    private static Vector4 GetAttachmentColor(EFrameBufferAttachment attachment)
    {
        if (IsColorAttachment(attachment))
            return new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
        if (IsDepthAttachment(attachment))
            return DepthColor;
        if (IsStencilAttachment(attachment))
            return StencilColor;
        if (IsDepthStencilAttachment(attachment))
            return new Vector4(0.9f, 0.7f, 0.4f, 1.0f);
        return DisabledTextColor;
    }

    private static void DrawAttachmentDetails(IFrameBufferAttachement target, OpenGLRenderer renderer)
    {
        switch (target)
        {
            case XRTexture2D tex2D:
                ImGui.TextColored(DisabledTextColor, $"{tex2D.SizedInternalFormat}");
                break;
            case XRRenderBuffer rb:
                ImGui.TextColored(DisabledTextColor, $"{rb.Type}");
                if (rb.IsMultisample)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(DisabledTextColor, $"MSAA x{rb.MultisampleCount}");
                }
                break;
            case XRTexture tex:
                ImGui.TextColored(DisabledTextColor, tex.Name ?? "<unnamed>");
                break;
            default:
                ImGui.TextColored(DisabledTextColor, "--");
                break;
        }
    }

    private static void DrawFrameBufferAllPreviews(XRFrameBuffer fbo, OpenGLRenderer renderer)
    {
        var targets = fbo.Targets;
        if (targets is null || targets.Length == 0)
        {
            ImGui.TextColored(DisabledTextColor, "No attachments to preview.");
            return;
        }

        if (!Engine.IsRenderThread)
        {
            ImGui.TextColored(WarningTextColor, "Previews unavailable (not on render thread).");
            return;
        }

        if (renderer is null)
        {
            ImGui.TextColored(DisabledTextColor, "No renderer available.");
            return;
        }

        // Group attachments by type
        List<(IFrameBufferAttachement target, EFrameBufferAttachment attachment, int index)> colorAttachments = new();
        List<(IFrameBufferAttachement target, EFrameBufferAttachment attachment, int index)> depthAttachments = new();
        List<(IFrameBufferAttachement target, EFrameBufferAttachment attachment, int index)> stencilAttachments = new();

        for (int i = 0; i < targets.Length; i++)
        {
            var (target, attachment, _, _) = targets[i];
            if (IsColorAttachment(attachment))
                colorAttachments.Add((target, attachment, i));
            else if (IsDepthAttachment(attachment) || IsDepthStencilAttachment(attachment))
                depthAttachments.Add((target, attachment, i));
            else if (IsStencilAttachment(attachment))
                stencilAttachments.Add((target, attachment, i));
        }

        // Draw color attachment previews
        if (colorAttachments.Count > 0)
        {
            ImGui.TextUnformatted("Color Attachments:");
            ImGui.Indent();
            foreach (var (target, attachment, index) in colorAttachments)
            {
                DrawAttachmentPreview(target, attachment, renderer, false, $"Color{index}");
            }
            ImGui.Unindent();
            ImGui.Spacing();
        }

        // Draw depth attachment previews
        if (depthAttachments.Count > 0)
        {
            ImGui.TextColored(DepthColor, "Depth Attachments:");
            ImGui.Indent();
            foreach (var (target, attachment, index) in depthAttachments)
            {
                DrawAttachmentPreview(target, attachment, renderer, true, $"Depth{index}");
            }
            ImGui.Unindent();
            ImGui.Spacing();
        }

        // Draw stencil attachment previews
        if (stencilAttachments.Count > 0)
        {
            ImGui.TextColored(StencilColor, "Stencil Attachments:");
            ImGui.Indent();
            foreach (var (target, attachment, index) in stencilAttachments)
            {
                DrawAttachmentPreview(target, attachment, renderer, false, $"Stencil{index}");
            }
            ImGui.Unindent();
        }

        if (colorAttachments.Count == 0 && depthAttachments.Count == 0 && stencilAttachments.Count == 0)
        {
            ImGui.TextColored(DisabledTextColor, "No previewable attachments found.");
        }
    }

    private static void DrawAttachmentPreview(IFrameBufferAttachement target, EFrameBufferAttachment attachment, OpenGLRenderer renderer, bool isDepth, string uniqueId)
    {
        uint bindingId = 0;
        int width = (int)target.Width;
        int height = (int)target.Height;
        string formatStr = string.Empty;
        string targetTypeName = target.GetType().Name;

        switch (target)
        {
            case XRTexture2D tex2D:
                var apiTex = renderer.GenericToAPI<GLTexture2D>(tex2D);
                if (apiTex is not null)
                {
                    bindingId = apiTex.BindingId;
                    formatStr = tex2D.SizedInternalFormat.ToString();
                }
                break;
            case XRRenderBuffer rb:
                var apiRb = renderer.GenericToAPI<GLRenderBuffer>(rb);
                if (apiRb is not null)
                {
                    bindingId = apiRb.BindingId;
                    formatStr = rb.Type.ToString();
                    if (rb.IsMultisample)
                        formatStr += $" (MSAA x{rb.MultisampleCount})";
                }
                break;
        }

        string label = $"{attachment} ({targetTypeName})";
        ImGui.TextUnformatted(label);

        if (bindingId == 0 || bindingId == OpenGLRenderer.GLObjectBase.InvalidBindingId)
        {
            ImGui.SameLine();
            ImGui.TextColored(DisabledTextColor, "- Not ready");
            return;
        }

        // For renderbuffers, we can't directly preview them like textures
        // but we can show info about them
        if (target is XRRenderBuffer)
        {
            ImGui.SameLine();
            ImGui.TextColored(DisabledTextColor, $"- RenderBuffer ID: {bindingId}");
            ImGui.TextColored(DisabledTextColor, $"  {width}x{height} {formatStr}");
            ImGui.TextColored(WarningTextColor, "  (RenderBuffer direct preview requires blit to texture)");
            return;
        }

        Vector2 pixelSize = new(width, height);
        Vector2 displaySize = GetPreviewSize(pixelSize);

        // Draw preview with correct UV orientation (flip V to fix upside-down)
        Vector2 uv0 = new(0, 1); // Bottom-left of texture
        Vector2 uv1 = new(1, 0); // Top-right of texture
        
        nint handle = (nint)bindingId;
        ImGui.Image(handle, displaySize, uv0, uv1);

        // Context menu and tooltip
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted($"{attachment}");
            ImGui.TextUnformatted($"{width} x {height}");
            ImGui.TextUnformatted($"Format: {formatStr}");
            if (isDepth)
                ImGui.TextColored(DepthColor, "Depth buffer");
            ImGui.TextUnformatted("Right-click for options");
            ImGui.EndTooltip();
        }

        // Context menu for preview options
        if (ImGui.BeginPopupContextItem($"PreviewContext{uniqueId}"))
        {
            if (ImGui.MenuItem("Open in Resizable Window"))
            {
                _showPreviewDialog = true;
                _previewDialogTitle = $"{attachment} - {width}x{height}";
                _previewDialogTextureId = bindingId;
                _previewDialogTextureSize = new Vector2(width, height);
                _previewDialogIsDepth = isDepth;
                _previewDialogFormat = formatStr;
            }
            ImGui.EndPopup();
        }

        // Also allow double-click to open preview
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            _showPreviewDialog = true;
            _previewDialogTitle = $"{attachment} - {width}x{height}";
            _previewDialogTextureId = bindingId;
            _previewDialogTextureSize = new Vector2(width, height);
            _previewDialogIsDepth = isDepth;
            _previewDialogFormat = formatStr;
        }

        ImGui.SameLine();
        ImGui.TextColored(DisabledTextColor, $"{width}x{height}");
    }

    private static void DrawPreviewDialog()
    {
        if (!_showPreviewDialog || _previewDialogTextureId == 0)
            return;

        ImGui.SetNextWindowSize(new Vector2(400, 400), ImGuiCond.FirstUseEver);
        
        ImGuiWindowFlags windowFlags = ImGuiWindowFlags.None;
        
        if (ImGui.Begin(_previewDialogTitle, ref _showPreviewDialog, windowFlags))
        {
            // Info bar
            ImGui.TextUnformatted($"Size: {_previewDialogTextureSize.X}x{_previewDialogTextureSize.Y}");
            ImGui.SameLine();
            ImGui.TextColored(DisabledTextColor, $"| Format: {_previewDialogFormat}");
            if (_previewDialogIsDepth)
            {
                ImGui.SameLine();
                ImGui.TextColored(DepthColor, "| Depth");
            }
            
            ImGui.Separator();

            // Calculate available space for the image
            Vector2 availableSize = ImGui.GetContentRegionAvail();
            
            // Maintain aspect ratio
            float aspectRatio = _previewDialogTextureSize.X / MathF.Max(_previewDialogTextureSize.Y, 1f);
            Vector2 imageSize;
            
            if (availableSize.X / aspectRatio <= availableSize.Y)
            {
                imageSize = new Vector2(availableSize.X, availableSize.X / aspectRatio);
            }
            else
            {
                imageSize = new Vector2(availableSize.Y * aspectRatio, availableSize.Y);
            }

            // Center the image
            float offsetX = (availableSize.X - imageSize.X) * 0.5f;
            if (offsetX > 0)
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);

            // Draw with flipped UVs to correct orientation
            Vector2 uv0 = new(0, 1);
            Vector2 uv1 = new(1, 0);
            
            nint handle = (nint)_previewDialogTextureId;
            ImGui.Image(handle, imageSize, uv0, uv1);

            // Show actual vs display size
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted($"Actual: {_previewDialogTextureSize.X}x{_previewDialogTextureSize.Y}");
                ImGui.TextUnformatted($"Display: {imageSize.X:F0}x{imageSize.Y:F0}");
                ImGui.EndTooltip();
            }
        }
        ImGui.End();

        // Clear dialog state if closed
        if (!_showPreviewDialog)
        {
            _previewDialogTextureId = 0;
        }
    }

    #endregion

    #region GLRenderBuffer Editor

    /// <summary>
    /// Draws an ImGui inspector for a GLRenderBuffer.
    /// </summary>
    [GLRenderBufferEditor]
    public static void DrawGLRenderBufferInspector(OpenGLRenderer.GLObjectBase glObject)
    {
        if (glObject is not GLRenderBuffer glRb)
        {
            ImGui.TextColored(WarningTextColor, "Expected GLRenderBuffer but received different type.");
            return;
        }

        DrawGLObjectHeader(glRb);

        if (glRb.Data is not XRRenderBuffer rb)
        {
            ImGui.TextColored(DisabledTextColor, "No XRRenderBuffer data available.");
            return;
        }

        if (ImGui.CollapsingHeader("Render Buffer Properties", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawRenderBufferProperties(rb, glRb);
        }

        // Draw preview dialog if open
        DrawPreviewDialog();
    }

    private static void DrawRenderBufferProperties(XRRenderBuffer rb, GLRenderBuffer glRb)
    {
        const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg;
        if (!ImGui.BeginTable("RBProperties", 2, tableFlags))
            return;

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Dimensions");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted($"{rb.Width} x {rb.Height}");

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Storage Format");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(rb.Type.ToString());

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Multisample");
        ImGui.TableSetColumnIndex(1);
        if (rb.IsMultisample)
            ImGui.TextUnformatted($"Yes (x{rb.MultisampleCount})");
        else
            ImGui.TextUnformatted("No");

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("FBO Attachment");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(rb.FrameBufferAttachment?.ToString() ?? "<None>");

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Invalidated");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(glRb.Invalidated ? "Yes" : "No");

        ImGui.EndTable();

        // Note about renderbuffer preview limitations
        ImGui.Spacing();
        ImGui.TextColored(WarningTextColor, "Note: RenderBuffers cannot be directly previewed.");
        ImGui.TextColored(DisabledTextColor, "They must be blitted to a texture for visualization.");
    }

    #endregion

    #region GLTexture Editor

    /// <summary>
    /// Draws an ImGui inspector for any GLTexture type.
    /// </summary>
    [GLTextureEditor]
    public static void DrawGLTextureInspector(OpenGLRenderer.GLObjectBase glObject)
    {
        if (glObject is not IGLTexture glTexture)
        {
            ImGui.TextColored(WarningTextColor, "Expected GLTexture but received different type.");
            return;
        }

        DrawGLObjectHeader(glObject);

        // Dispatch to specific texture type handler
        switch (glObject)
        {
            case GLTexture2D glTex2D:
                DrawGLTexture2DInspector(glTex2D);
                break;
            case GLTexture3D glTex3D:
                DrawGLTexture3DInspector(glTex3D);
                break;
            case GLTextureCube glTexCube:
                DrawGLTextureCubeInspector(glTexCube);
                break;
            case GLTexture2DArray glTex2DArray:
                DrawGLTexture2DArrayInspector(glTex2DArray);
                break;
            default:
                DrawGenericGLTextureInspector(glTexture, glObject);
                break;
        }

        // Draw preview dialog if open
        DrawPreviewDialog();
    }

    private static void DrawGLTexture2DInspector(GLTexture2D glTex)
    {
        var data = glTex.Data;
        if (data is null)
        {
            ImGui.TextColored(DisabledTextColor, "No texture data available.");
            return;
        }

        if (ImGui.CollapsingHeader("Texture2D Properties", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawTexture2DProperties(data, glTex);
        }

        if (ImGui.CollapsingHeader("Sampling Parameters", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawTextureSamplingParams(data);
        }

        if (ImGui.CollapsingHeader("Mipmaps", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawTexture2DMipmaps(data, glTex);
        }

        if (ImGui.CollapsingHeader("Preview", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawTexturePreviewWithDialog(data, glTex);
        }
    }

    private static void DrawTexture2DProperties(XRTexture2D data, GLTexture2D glTex)
    {
        const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg;
        if (!ImGui.BeginTable("Tex2DProps", 2, tableFlags))
            return;

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Dimensions");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted($"{data.Width} x {data.Height}");

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Internal Format");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(data.SizedInternalFormat.ToString());

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Multisample");
        ImGui.TableSetColumnIndex(1);
        if (data.MultiSample)
            ImGui.TextUnformatted($"Yes (x{data.MultiSampleCount})");
        else
            ImGui.TextUnformatted("No");

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Resizable");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(data.Resizable ? "Yes" : "No");

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Auto Generate Mipmaps");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(data.AutoGenerateMipmaps ? "Yes" : "No");

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("FBO Attachment");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(data.FrameBufferAttachment?.ToString() ?? "<None>");

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Storage Set");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(glTex.StorageSet ? "Yes" : "No");

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Is Pushing");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(glTex.IsPushing ? "Yes" : "No");

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Is Invalidated");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(glTex.IsInvalidated ? "Yes" : "No");

        ImGui.EndTable();
    }

    private static void DrawTextureSamplingParams(XRTexture2D data)
    {
        const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg;
        if (!ImGui.BeginTable("TexSampling", 2, tableFlags))
            return;

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Mag Filter");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(data.MagFilter.ToString());

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Min Filter");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(data.MinFilter.ToString());

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("U Wrap");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(data.UWrap.ToString());

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("V Wrap");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(data.VWrap.ToString());

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("LOD Bias");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(data.LodBias.ToString("F2", CultureInfo.InvariantCulture));

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("LOD Range");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted($"{data.MinLOD} to {data.MaxLOD}");

        ImGui.EndTable();
    }

    private static void DrawTexture2DMipmaps(XRTexture2D data, GLTexture2D glTex)
    {
        var mipmaps = data.Mipmaps;
        if (mipmaps is null || mipmaps.Length == 0)
        {
            ImGui.TextColored(DisabledTextColor, "No mipmaps.");
            return;
        }

        ImGui.TextUnformatted($"Mipmap Count: {mipmaps.Length}");

        const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp 
            | ImGuiTableFlags.RowBg 
            | ImGuiTableFlags.BordersInnerV;

        if (!ImGui.BeginTable("Mipmaps", 4, tableFlags))
            return;

        ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed, 50.0f);
        ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthStretch, 0.3f);
        ImGui.TableSetupColumn("Format", ImGuiTableColumnFlags.WidthStretch, 0.35f);
        ImGui.TableSetupColumn("Has Data", ImGuiTableColumnFlags.WidthStretch, 0.2f);
        ImGui.TableHeadersRow();

        for (int i = 0; i < mipmaps.Length; i++)
        {
            var mip = mipmaps[i];
            if (mip is null)
                continue;

            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(i.ToString(CultureInfo.InvariantCulture));

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted($"{mip.Width} x {mip.Height}");

            ImGui.TableSetColumnIndex(2);
            ImGui.TextColored(DisabledTextColor, $"{mip.PixelFormat} / {mip.PixelType}");

            ImGui.TableSetColumnIndex(3);
            bool hasData = mip.Data is not null && mip.Data.Length > 0;
            ImGui.TextUnformatted(hasData ? "Yes" : "No");
        }

        ImGui.EndTable();
    }

    private static void DrawTexturePreviewWithDialog(XRTexture2D texture, GLTexture2D glTex)
    {
        if (!Engine.IsRenderThread)
        {
            ImGui.TextColored(WarningTextColor, "Preview unavailable (not on render thread).");
            return;
        }

        var renderer = glTex.Renderer;
        if (renderer is null)
        {
            ImGui.TextColored(DisabledTextColor, "No renderer available.");
            return;
        }

        uint bindingId = glTex.BindingId;
        if (bindingId == OpenGLRenderer.GLObjectBase.InvalidBindingId || bindingId == 0)
        {
            ImGui.TextColored(DisabledTextColor, "Texture not ready.");
            return;
        }

        // Check for MSAA texture - can't directly preview
        if (texture.MultiSample)
        {
            ImGui.TextColored(MsaaColor, $"MSAA Texture ({texture.MultiSampleCount}x)");
            ImGui.TextColored(WarningTextColor, "MSAA textures cannot be directly previewed.");
            ImGui.TextColored(DisabledTextColor, "They must be resolved to a non-MSAA texture first.");
            ImGui.TextColored(DisabledTextColor, $"Size: {texture.Width} x {texture.Height}");
            ImGui.TextColored(DisabledTextColor, $"Format: {texture.SizedInternalFormat}");
            return;
        }

        bool isDepth = IsDepthFormat(texture.SizedInternalFormat);
        string formatStr = texture.SizedInternalFormat.ToString();

        Vector2 pixelSize = new(texture.Width, texture.Height);
        Vector2 displaySize = GetPreviewSize(pixelSize);

        // Draw with flipped UVs to correct orientation
        Vector2 uv0 = new(0, 1);
        Vector2 uv1 = new(1, 0);

        nint handle = (nint)bindingId;
        ImGui.Image(handle, displaySize, uv0, uv1);

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted($"{texture.Name ?? "<unnamed>"}");
            ImGui.TextUnformatted($"{texture.Width} x {texture.Height}");
            ImGui.TextUnformatted($"Format: {formatStr}");
            if (isDepth)
                ImGui.TextColored(DepthColor, "Depth texture");
            ImGui.TextUnformatted("Double-click or right-click for larger view");
            ImGui.EndTooltip();
        }

        // Context menu
        if (ImGui.BeginPopupContextItem("TexturePreviewContext"))
        {
            if (ImGui.MenuItem("Open in Resizable Window"))
            {
                OpenTexturePreviewDialog(texture.Name ?? "Texture", bindingId, pixelSize, isDepth, formatStr);
            }
            ImGui.EndPopup();
        }

        // Double-click to open
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            OpenTexturePreviewDialog(texture.Name ?? "Texture", bindingId, pixelSize, isDepth, formatStr);
        }

        ImGui.TextColored(DisabledTextColor, $"Preview: {texture.Width} x {texture.Height}");
    }

    private static void OpenTexturePreviewDialog(string name, uint bindingId, Vector2 size, bool isDepth, string format)
    {
        _showPreviewDialog = true;
        _previewDialogTitle = $"{name} - {size.X}x{size.Y}";
        _previewDialogTextureId = bindingId;
        _previewDialogTextureSize = size;
        _previewDialogIsDepth = isDepth;
        _previewDialogFormat = format;
    }

    private static bool IsDepthFormat(ESizedInternalFormat format)
    {
        string formatName = format.ToString();
        return formatName.Contains("Depth", StringComparison.OrdinalIgnoreCase);
    }

    private static void DrawGLTexture3DInspector(GLTexture3D glTex)
    {
        var data = glTex.Data;
        if (data is null)
        {
            ImGui.TextColored(DisabledTextColor, "No texture data available.");
            return;
        }

        if (ImGui.CollapsingHeader("Texture3D Properties", ImGuiTreeNodeFlags.DefaultOpen))
        {
            const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg;
            if (ImGui.BeginTable("Tex3DProps", 2, tableFlags))
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted("Dimensions");
                ImGui.TableSetColumnIndex(1);
                var size = data.WidthHeightDepth;
                ImGui.TextUnformatted($"{size.X} x {size.Y} x {size.Z}");

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted("Auto Generate Mipmaps");
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(data.AutoGenerateMipmaps ? "Yes" : "No");

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted("Storage Set");
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(glTex.StorageSet ? "Yes" : "No");

                ImGui.EndTable();
            }
        }

        ImGui.TextColored(DisabledTextColor, "3D texture preview not available.");
    }

    private static void DrawGLTextureCubeInspector(GLTextureCube glTex)
    {
        var data = glTex.Data;
        if (data is null)
        {
            ImGui.TextColored(DisabledTextColor, "No texture data available.");
            return;
        }

        if (ImGui.CollapsingHeader("TextureCube Properties", ImGuiTreeNodeFlags.DefaultOpen))
        {
            const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg;
            if (ImGui.BeginTable("TexCubeProps", 2, tableFlags))
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted("Size");
                ImGui.TableSetColumnIndex(1);
                var size = data.WidthHeightDepth;
                ImGui.TextUnformatted($"{size.X} x {size.Y}");

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted("Mipmap Count");
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(glTex.Mipmaps.Length.ToString(CultureInfo.InvariantCulture));

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted("Auto Generate Mipmaps");
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(data.AutoGenerateMipmaps ? "Yes" : "No");

                ImGui.EndTable();
            }
        }

        if (ImGui.CollapsingHeader("Cube Faces", ImGuiTreeNodeFlags.DefaultOpen))
        {
            string[] faceNames = ["Positive X", "Negative X", "Positive Y", "Negative Y", "Positive Z", "Negative Z"];
            for (int i = 0; i < 6; i++)
            {
                ImGui.BulletText(faceNames[i]);
            }
        }

        ImGui.TextColored(DisabledTextColor, "Cube texture face preview not fully implemented.");
    }

    private static void DrawGLTexture2DArrayInspector(GLTexture2DArray glTex)
    {
        var data = glTex.Data;
        if (data is null)
        {
            ImGui.TextColored(DisabledTextColor, "No texture data available.");
            return;
        }

        if (ImGui.CollapsingHeader("Texture2DArray Properties", ImGuiTreeNodeFlags.DefaultOpen))
        {
            const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg;
            if (ImGui.BeginTable("Tex2DArrayProps", 2, tableFlags))
            {
                var size = data.WidthHeightDepth;

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted("Dimensions");
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted($"{size.X} x {size.Y}");

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted("Layer Count");
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(((int)size.Z).ToString(CultureInfo.InvariantCulture));

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted("Auto Generate Mipmaps");
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(data.AutoGenerateMipmaps ? "Yes" : "No");

                ImGui.EndTable();
            }
        }

        ImGui.TextColored(DisabledTextColor, "2D array texture layer preview not fully implemented.");
    }

    private static void DrawGenericGLTextureInspector(IGLTexture glTexture, OpenGLRenderer.GLObjectBase glObject)
    {
        if (ImGui.CollapsingHeader("Texture Properties", ImGuiTreeNodeFlags.DefaultOpen))
        {
            const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg;
            if (ImGui.BeginTable("GenTexProps", 2, tableFlags))
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted("Texture Target");
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(glTexture.TextureTarget.ToString());

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted("Dimensions");
                ImGui.TableSetColumnIndex(1);
                var size = glTexture.WidthHeightDepth;
                ImGui.TextUnformatted($"{size.X} x {size.Y} x {size.Z}");

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted("Is Invalidated");
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(glTexture.IsInvalidated ? "Yes" : "No");

                ImGui.EndTable();
            }
        }
    }

    #endregion

    #region Common Drawing Helpers

    private static void DrawGLObjectHeader(OpenGLRenderer.GLObjectBase glObject)
    {
        ImGui.TextColored(HeaderColor, glObject.GetDescribingName());
        ImGui.Separator();

        const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg;
        if (!ImGui.BeginTable("GLObjectHeader", 2, tableFlags))
            return;

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("GL Object Type");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(glObject.Type.ToString());

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Binding ID");
        ImGui.TableSetColumnIndex(1);
        if (glObject.TryGetBindingId(out uint bindingId) && bindingId != OpenGLRenderer.GLObjectBase.InvalidBindingId)
            ImGui.TextUnformatted(bindingId.ToString(CultureInfo.InvariantCulture));
        else
            ImGui.TextColored(DisabledTextColor, "<Ungenerated>");

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Is Generated");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(glObject.IsGenerated ? "Yes" : "No");

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Renderer");
        ImGui.TableSetColumnIndex(1);
        var windowTitle = glObject.Renderer?.XRWindow?.Window?.Title;
        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(windowTitle) ? "<Unknown>" : windowTitle);

        ImGui.EndTable();
        ImGui.Spacing();
    }

    private static Vector2 GetPreviewSize(Vector2 pixelSize)
    {
        float width = pixelSize.X;
        float height = pixelSize.Y;

        if (width <= 0f || height <= 0f)
            return new Vector2(TexturePreviewFallbackEdge, TexturePreviewFallbackEdge);

        float maxDimension = MathF.Max(width, height);
        if (maxDimension <= TexturePreviewMaxEdge)
            return new Vector2(width, height);

        float scale = TexturePreviewMaxEdge / maxDimension;
        return new Vector2(width * scale, height * scale);
    }

    private static string FormatDrawBuffers(EDrawBuffersAttachment[]? drawBuffers)
    {
        if (drawBuffers is null || drawBuffers.Length == 0)
            return "<None>";

        return string.Join(", ", drawBuffers);
    }

    private static bool IsColorAttachment(EFrameBufferAttachment attachment)
        => attachment >= EFrameBufferAttachment.ColorAttachment0
        && attachment <= EFrameBufferAttachment.ColorAttachment31;

    private static bool IsDepthAttachment(EFrameBufferAttachment attachment)
        => attachment == EFrameBufferAttachment.DepthAttachment;

    private static bool IsStencilAttachment(EFrameBufferAttachment attachment)
        => attachment == EFrameBufferAttachment.StencilAttachment;

    private static bool IsDepthStencilAttachment(EFrameBufferAttachment attachment)
        => attachment == EFrameBufferAttachment.DepthStencilAttachment;

    #endregion

    #region Registration

    /// <summary>
    /// Dispatches to the appropriate editor based on the GL object type.
    /// </summary>
    public static void DrawGLObjectInspector(OpenGLRenderer.GLObjectBase glObject)
    {
        if (glObject is null)
        {
            ImGui.TextColored(DisabledTextColor, "No GL object selected.");
            return;
        }

        switch (glObject)
        {
            case GLFrameBuffer:
                DrawGLFrameBufferInspector(glObject);
                break;
            case GLRenderBuffer:
                DrawGLRenderBufferInspector(glObject);
                break;
            case IGLTexture:
                DrawGLTextureInspector(glObject);
                break;
            default:
                ImGui.TextColored(DisabledTextColor, $"No custom editor for {glObject.GetType().Name}.");
                DrawGLObjectHeader(glObject);
                break;
        }
    }

    #endregion
}
