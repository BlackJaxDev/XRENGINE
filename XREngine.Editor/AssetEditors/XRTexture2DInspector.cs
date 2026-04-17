using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using XREngine;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;

namespace XREngine.Editor.AssetEditors;

public sealed class XRTexture2DInspector : IXRAssetInspector
{
    private const float PreviewMaxEdge = 256f;
    private const float PreviewFallbackEdge = 64f;

    private static readonly Vector4 DirtyBadgeColor = new(0.95f, 0.65f, 0.2f, 1f);
    private static readonly Vector4 SectionLabelColor = new(0.85f, 0.85f, 0.85f, 1f);

    public void DrawInspector(EditorImGuiUI.InspectorTargetSet targets, HashSet<object> visitedObjects)
    {
        var textures = targets.Targets.OfType<XRTexture2D>().Cast<object>().ToList();
        if (textures.Count == 0)
        {
            foreach (var asset in targets.Targets.OfType<XRAsset>())
                EditorImGuiUI.DrawDefaultAssetInspector(asset, visitedObjects);
            return;
        }

        if (targets.HasMultipleTargets)
        {
            EditorImGuiUI.DrawDefaultAssetInspector(new EditorImGuiUI.InspectorTargetSet(textures, targets.CommonType), visitedObjects);
            return;
        }

        var texture = (XRTexture2D)textures[0];

        DrawHeader(texture);
        DrawPreview(texture);
        DrawFormatSummary(texture);
        DrawSamplingControls(texture);
        DrawMipmapSummary(texture);
        DrawAdvancedSection(texture, visitedObjects);
    }

    private static void DrawHeader(XRTexture2D texture)
    {
        ImGui.TextUnformatted(GetDisplayName(texture));
        string path = texture.FilePath ?? "<unsaved asset>";
        ImGui.TextDisabled(path);

        if (texture.IsDirty)
        {
            ImGui.SameLine();
            ImGui.TextColored(DirtyBadgeColor, "Modified");
        }

        ImGui.Separator();
    }

    private static string GetDisplayName(XRTexture2D texture)
    {
        if (!string.IsNullOrWhiteSpace(texture.Name))
            return texture.Name!;
        if (!string.IsNullOrWhiteSpace(texture.FilePath))
            return Path.GetFileName(texture.FilePath) ?? texture.GetType().Name;
        return texture.GetType().Name;
    }

    private static void DrawPreview(XRTexture2D texture)
    {
        ImGui.TextColored(SectionLabelColor, "Preview");
        ImGui.Spacing();

        if (!TryGetPreviewHandle(texture, out nint handle, out Vector2 pixelSize, out string? reason))
        {
            ImGui.TextDisabled(reason ?? "Preview unavailable");
            ImGui.Separator();
            return;
        }

        Vector2 displaySize = FitPreview(pixelSize);
        ImGui.Image(handle, displaySize);
        ImGui.TextDisabled($"{(int)pixelSize.X} x {(int)pixelSize.Y}");
        ImGui.Separator();
    }

    private static void DrawFormatSummary(XRTexture2D texture)
    {
        ImGui.TextColored(SectionLabelColor, "Format");
        ImGui.Spacing();

        ImGui.TextUnformatted($"Size: {texture.Width} x {texture.Height}");
        ImGui.TextUnformatted($"Sized Internal Format: {texture.SizedInternalFormat}");
        ImGui.TextUnformatted($"Mipmap Levels: {texture.Mipmaps.Length}");
        ImGui.TextUnformatted($"Multi-sample: {(texture.MultiSample ? $"yes (x{texture.MultiSampleCount})" : "no")}");
        ImGui.TextUnformatted($"Rectangle: {(texture.Rectangle ? "yes" : "no")}");

        bool resizable = texture.Resizable;
        if (ImGui.Checkbox("Resizable", ref resizable))
            texture.Resizable = resizable;

        bool autoMips = texture.AutoGenerateMipmaps;
        if (ImGui.Checkbox("Auto-generate Mipmaps", ref autoMips))
            texture.AutoGenerateMipmaps = autoMips;

        ImGui.Separator();
    }

    private static void DrawSamplingControls(XRTexture2D texture)
    {
        ImGui.TextColored(SectionLabelColor, "Sampling");
        ImGui.Spacing();

        DrawEnumCombo("Min Filter", texture.MinFilter, v => texture.MinFilter = v);
        DrawEnumCombo("Mag Filter", texture.MagFilter, v => texture.MagFilter = v);
        DrawEnumCombo("U Wrap", texture.UWrap, v => texture.UWrap = v);
        DrawEnumCombo("V Wrap", texture.VWrap, v => texture.VWrap = v);

        float lodBias = texture.LodBias;
        if (ImGui.DragFloat("LOD Bias", ref lodBias, 0.05f, -16f, 16f))
            texture.LodBias = lodBias;

        bool compare = texture.EnableComparison;
        if (ImGui.Checkbox("Enable Comparison (PCF)", ref compare))
            texture.EnableComparison = compare;

        if (compare)
            DrawEnumCombo("Compare Func", texture.CompareFunc, v => texture.CompareFunc = v);

        ImGui.Separator();
    }

    private static void DrawMipmapSummary(XRTexture2D texture)
    {
        var mips = texture.Mipmaps;
        if (mips.Length == 0)
            return;

        if (!ImGui.CollapsingHeader($"Mipmaps ({mips.Length})"))
            return;

        for (int i = 0; i < mips.Length; i++)
        {
            var m = mips[i];
            if (m is null)
            {
                ImGui.BulletText($"[{i}] <null>");
                continue;
            }

            ImGui.BulletText($"[{i}] {m.Width} x {m.Height}   {m.InternalFormat} / {m.PixelFormat} / {m.PixelType}");
        }
    }

    private static void DrawAdvancedSection(XRTexture2D texture, HashSet<object> visitedObjects)
    {
        if (!ImGui.CollapsingHeader("Raw Properties"))
            return;

        ImGui.PushID("XRTexture2DRawProperties");
        EditorImGuiUI.DrawDefaultAssetInspector(texture, visitedObjects);
        ImGui.PopID();
    }

    private static void DrawEnumCombo<TEnum>(string label, TEnum current, Action<TEnum> apply) where TEnum : struct, Enum
    {
        string preview = current.ToString();
        if (!ImGui.BeginCombo(label, preview))
            return;

        foreach (TEnum value in Enum.GetValues<TEnum>())
        {
            bool selected = EqualityComparer<TEnum>.Default.Equals(value, current);
            if (ImGui.Selectable(value.ToString(), selected) && !selected)
                apply(value);
            if (selected)
                ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
    }

    private static bool TryGetPreviewHandle(XRTexture2D texture, out nint handle, out Vector2 pixelSize, out string? failureReason)
    {
        handle = 0;
        pixelSize = new Vector2(texture.Width, texture.Height);
        failureReason = null;

        if (!Engine.IsRenderThread)
        {
            failureReason = "Preview unavailable off render thread";
            return false;
        }

        if (AbstractRenderer.Current is VulkanRenderer vkRenderer)
        {
            IntPtr textureId = vkRenderer.RegisterImGuiTexture(texture);
            if (textureId == IntPtr.Zero)
            {
                failureReason = "Texture not uploaded";
                return false;
            }
            handle = (nint)textureId;
            return true;
        }

        if (AbstractRenderer.Current is OpenGLRenderer glRenderer)
        {
            var apiTexture = glRenderer.GenericToAPI<GLTexture2D>(texture);
            if (apiTexture is null)
            {
                failureReason = "Texture not uploaded";
                return false;
            }

            uint binding = apiTexture.BindingId;
            if (binding == OpenGLRenderer.GLObjectBase.InvalidBindingId || binding == 0)
            {
                failureReason = "Texture not ready";
                return false;
            }

            handle = (nint)binding;
            return true;
        }

        failureReason = "Preview requires OpenGL or Vulkan renderer";
        return false;
    }

    private static Vector2 FitPreview(Vector2 pixelSize)
    {
        if (pixelSize.X <= 0 || pixelSize.Y <= 0)
            return new Vector2(PreviewFallbackEdge, PreviewFallbackEdge);

        float maxDim = MathF.Max(pixelSize.X, pixelSize.Y);
        if (maxDim <= PreviewMaxEdge)
            return pixelSize;

        float scale = PreviewMaxEdge / maxDim;
        return new Vector2(pixelSize.X * scale, pixelSize.Y * scale);
    }
}
