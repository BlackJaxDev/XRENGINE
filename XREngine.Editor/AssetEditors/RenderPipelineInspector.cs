using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using ImGuiNET;
using Silk.NET.OpenGL;
using XREngine;
using XREngine.Data.Rendering;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Editor.AssetEditors;

public sealed class RenderPipelineInspector : IXRAssetInspector
{
    #region Constants & Fields

    private static readonly Vector4 DirtyBadgeColor = new(0.95f, 0.55f, 0.2f, 1f);
    private static readonly Vector4 CleanBadgeColor = new(0.5f, 0.8f, 0.5f, 1f);
    private static readonly Vector4 WarningColor = new(0.95f, 0.45f, 0.45f, 1f);

    private readonly ConditionalWeakTable<RenderPipeline, EditorState> _stateCache = new();

    private static XRTexture2D? _autoExposureSmallestMipCopyTex;

    #endregion

    #region Public API

    public void DrawInspector(XRAsset asset, HashSet<object> visitedObjects)
    {
        if (asset is not RenderPipeline pipeline)
        {
            EditorImGuiUI.DrawDefaultAssetInspector(asset, visitedObjects);
            return;
        }

        var state = _stateCache.GetValue(pipeline, _ => new EditorState());

        DrawHeader(pipeline);

        bool drewInstances = DrawInstancesSection(pipeline);
        if (drewInstances)
            ImGui.Separator();

        bool drewPasses = DrawPassMetadataSection(pipeline, state);
        if (drewPasses)
            ImGui.Separator();

        DrawCommandChainSection(pipeline, state, visitedObjects);
        ImGui.Separator();

        DrawDebugViews(pipeline, state);
        ImGui.Separator();

        DrawRawInspector(pipeline, visitedObjects);
    }

    #endregion

    #region Section Drawing - Header

    private static void DrawHeader(RenderPipeline pipeline)
    {
        string displayName = GetDisplayName(pipeline);
        ImGui.TextUnformatted(displayName);

        ImGui.SameLine();
        if (ImGui.SmallButton("Open Graph##RenderPipeline"))
            EditorImGuiUI.OpenRenderPipelineGraph(pipeline);

        string path = pipeline.FilePath ?? "<unsaved asset>";
        ImGui.TextDisabled(path);
        if (!string.IsNullOrWhiteSpace(pipeline.FilePath))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy Path##RenderPipeline"))
                ImGui.SetClipboardText(pipeline.FilePath);
        }

        Vector4 statusColor = pipeline.IsDirty ? DirtyBadgeColor : CleanBadgeColor;
        ImGui.TextColored(statusColor, pipeline.IsDirty ? "Modified" : "Saved");

        bool isShadow = pipeline.IsShadowPass;
        if (ImGui.Checkbox("Shadow Pass", ref isShadow))
            pipeline.IsShadowPass = isShadow;

        ImGui.SameLine();
        ImGui.TextDisabled($"Instances: {pipeline.Instances.Count}");

        ImGui.SameLine();
        ImGui.TextDisabled($"Passes: {pipeline.PassMetadata?.Count ?? 0}");

        if (pipeline.CommandChain is null)
        {
            ImGui.TextColored(WarningColor, "Command chain is not initialized.");
            return;
        }

        ImGui.TextDisabled($"Commands: {pipeline.CommandChain.Count}");
        ImGui.SameLine();
        ImGui.TextDisabled($"Branch Resources: {pipeline.CommandChain.BranchResources}");
    }

    #endregion

    #region Section Drawing - Instances

    private static bool DrawInstancesSection(RenderPipeline pipeline)
    {
        if (!ImGui.CollapsingHeader("Active Instances", ImGuiTreeNodeFlags.DefaultOpen))
            return false;

        var instances = pipeline.Instances;
        if (instances.Count == 0)
        {
            ImGui.TextDisabled("Pipeline has no live runtime instances.");
            return true;
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("RenderPipelineInstances", 4, flags))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 26f);
            ImGui.TableSetupColumn("Descriptor");
            ImGui.TableSetupColumn("Resources", ImGuiTableColumnFlags.WidthFixed, 180f);
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 210f);
            ImGui.TableHeadersRow();

            var activeInstance = Engine.Rendering.State.CurrentRenderingPipeline;
            for (int i = 0; i < instances.Count; i++)
            {
                var instance = instances[i];
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted((i + 1).ToString());

                ImGui.TableSetColumnIndex(1);
                string descriptor = instance.DebugDescriptor;
                ImGui.TextWrapped(descriptor);
                if (ReferenceEquals(activeInstance, instance))
                {
                    ImGui.SameLine();
                    ImGui.TextColored(CleanBadgeColor, "[Active]");
                }

                ImGui.TableSetColumnIndex(2);
                int texCount = instance.Resources.TextureRecords.Count;
                int fboCount = instance.Resources.FrameBufferRecords.Count;
                ImGui.TextDisabled($"{texCount} textures\n{fboCount} FBOs");

                ImGui.TableSetColumnIndex(3);
                if (ImGui.SmallButton($"Purge##RPInstance{instance.GetHashCode():X8}"))
                    instance.DestroyCache();
                ImGui.SameLine();
                if (ImGui.SmallButton($"Graph##RPInstanceGraph{instance.GetHashCode():X8}"))
                    EditorImGuiUI.OpenRenderPipelineGraph(pipeline);
                ImGui.SameLine();
                if (ImGui.SmallButton($"Copy##RPInstanceCopy{instance.GetHashCode():X8}"))
                    ImGui.SetClipboardText(descriptor);
            }

            ImGui.EndTable();
        }

        return true;
    }

    #endregion

    #region Section Drawing - Debug Views

    private static void DrawDebugViews(RenderPipeline pipeline, EditorState state)
    {
        if (!ImGui.CollapsingHeader("Debug Views (Framebuffers)", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (!Engine.IsRenderThread)
        {
            ImGui.TextDisabled("Preview unavailable off render thread.");
            return;
        }

        var instances = pipeline.Instances;
        if (instances.Count == 0)
        {
            ImGui.TextDisabled("No live pipeline instances to preview.");
            return;
        }

        state.SelectedInstanceIndex = Math.Clamp(state.SelectedInstanceIndex, 0, Math.Max(0, instances.Count - 1));
        string currentInstanceLabel = instances[state.SelectedInstanceIndex].DebugDescriptor;

        ImGui.SetNextItemWidth(360f);
        if (ImGui.BeginCombo("Instance##RenderPipelineFbo", currentInstanceLabel))
        {
            for (int i = 0; i < instances.Count; i++)
            {
                string label = instances[i].DebugDescriptor;
                bool selected = i == state.SelectedInstanceIndex;
                if (ImGui.Selectable(label, selected))
                    state.SelectedInstanceIndex = i;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        var selectedInstance = instances[state.SelectedInstanceIndex];

        bool flipPreview = state.FlipPreview;

        DrawAutoExposureSmallestMipPreview(selectedInstance, flipPreview);
        ImGui.Separator();

        var fboRecords = selectedInstance.Resources.FrameBufferRecords
            .Where(pair => pair.Value.Instance is not null)
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (fboRecords.Count == 0)
        {
            ImGui.TextDisabled("Instance has no framebuffers to preview.");
            return;
        }

        if (string.IsNullOrWhiteSpace(state.SelectedFboName)
            || !fboRecords.Any(pair => pair.Key.Equals(state.SelectedFboName, StringComparison.OrdinalIgnoreCase)))
        {
            state.SelectedFboName = fboRecords[0].Key;
        }

        string currentFboLabel = state.SelectedFboName ?? fboRecords[0].Key;
        ImGui.SetNextItemWidth(260f);
        if (ImGui.BeginCombo("Framebuffer##RenderPipelineFbo", currentFboLabel))
        {
            foreach (var pair in fboRecords)
            {
                bool selected = pair.Key.Equals(state.SelectedFboName, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(pair.Key, selected))
                    state.SelectedFboName = pair.Key;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.Checkbox("Flip Preview Vertically", ref state.FlipPreview);

        var selectedRecord = fboRecords.First(pair => pair.Key.Equals(state.SelectedFboName, StringComparison.OrdinalIgnoreCase));
        XRFrameBuffer fbo = selectedRecord.Value.Instance!;

        ImGui.TextDisabled($"Dimensions: {fbo.Width} x {fbo.Height}");
        ImGui.TextDisabled($"Targets: {fbo.Targets?.Length ?? 0}");
        ImGui.TextDisabled($"Texture Types: {fbo.TextureTypes}");

        const ImGuiTableFlags tableFlags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame;
        if (ImGui.BeginTable("RenderPipelineFboIO", 2, tableFlags))
        {
            ImGui.TableSetupColumn("Inputs");
            ImGui.TableSetupColumn("Outputs");
            ImGui.TableHeadersRow();

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            DrawFboInputs(fbo, flipPreview);

            ImGui.TableSetColumnIndex(1);
            DrawFboOutputs(fbo, flipPreview);

            ImGui.EndTable();
        }
    }

    private static void DrawAutoExposureSmallestMipPreview(XRRenderPipelineInstance instance, bool flipPreview)
    {
        ImGui.TextUnformatted("Auto Exposure (Smallest Mip Preview)");

        if (!instance.Resources.TryGetTexture(DefaultRenderPipeline.HDRSceneTextureName, out XRTexture? hdrTex) || hdrTex is null)
        {
            ImGui.TextDisabled($"Texture '{DefaultRenderPipeline.HDRSceneTextureName}' not found on this instance.");
            return;
        }

        XRTexture? previewTex = null;
        int smallestMip = 0;
        string details;

        // Read back the smallest mip directly from the source texture.
        // (GL texture views require immutable storage; HDRSceneTex is typically resizable/mutable.)
        bool hasReadback = false;
        Vector4 smallestMipRgba = Vector4.Zero;
        string readbackFailure = string.Empty;

        switch (hdrTex)
        {
            case XRTexture2D tex2D:
                smallestMip = XRTexture.GetSmallestMipmapLevel(tex2D.Width, tex2D.Height, tex2D.SmallestAllowedMipmapLevel);
                // Avoid GL texture views here: HDRSceneTex is typically mutable/resizable, and GL texture views require immutable storage.
                previewTex = TryCopyMipTo1x1Texture(tex2D, smallestMip, 0, out string copyFailure)
                    ? _autoExposureSmallestMipCopyTex
                    : tex2D;
                details = $"Source: {tex2D.Width}x{tex2D.Height} | Mip {smallestMip}";
                hasReadback = TryReadBackRgbaFloat(tex2D, smallestMip, 0, out smallestMipRgba, out readbackFailure);
                if (!string.IsNullOrWhiteSpace(copyFailure))
                    ImGui.TextDisabled(copyFailure);
                break;

            case XRTexture2DArray tex2DArray:
                smallestMip = XRTexture.GetSmallestMipmapLevel(tex2DArray.Width, tex2DArray.Height, tex2DArray.SmallestAllowedMipmapLevel);
                previewTex = TryCopyMipTo1x1Texture(tex2DArray, smallestMip, 0, out string copyFailureArray)
                    ? _autoExposureSmallestMipCopyTex
                    : tex2DArray;
                details = $"Source: {tex2DArray.Width}x{tex2DArray.Height} | Mip {smallestMip} | Layer 0";
                hasReadback = TryReadBackRgbaFloat(tex2DArray, smallestMip, 0, out smallestMipRgba, out readbackFailure);
                if (!string.IsNullOrWhiteSpace(copyFailureArray))
                    ImGui.TextDisabled(copyFailureArray);
                break;

            default:
                ImGui.TextDisabled($"Unsupported HDR scene texture type: {hdrTex.GetType().Name}");
                return;
        }

        // Show a UI-friendly swatch of the 1x1 value even if the raw HDR image draw looks black.
        if (hasReadback)
        {
            ImGui.TextDisabled($"RGBA (float): {smallestMipRgba.X:0.####}, {smallestMipRgba.Y:0.####}, {smallestMipRgba.Z:0.####}, {smallestMipRgba.W:0.####}");
            Vector3 tonemapped = TonemapReinhard(new Vector3(smallestMipRgba.X, smallestMipRgba.Y, smallestMipRgba.Z));
            var swatch = new Vector4(tonemapped, 1f);
            ImGui.ColorButton("##AutoExposureSmallestMipSwatch", swatch, ImGuiColorEditFlags.NoTooltip, new Vector2(160f, 160f));
        }
        else
        {
            ImGui.TextDisabled(readbackFailure);
        }

        var previewState = new TexturePreviewState { Channel = TextureChannelView.RGBA, Layer = 0, Mip = 0 };
        if (TryGetTexturePreviewHandle(previewTex, 320f, previewState, out nint handle, out Vector2 displaySize, out Vector2 pixelSize, out string failure))
        {
            Vector2 uv0 = flipPreview ? new Vector2(0f, 1f) : Vector2.Zero;
            Vector2 uv1 = flipPreview ? new Vector2(1f, 0f) : Vector2.One;

            // A 1x1 smallest mip is useful but invisible at native size.
            // Scale up tiny mips in this dedicated panel only.
            const float minPreviewEdge = 96f;
            const float maxPreviewEdge = 320f;
            float largestPixelEdge = MathF.Max(1f, MathF.Max(pixelSize.X, pixelSize.Y));
            float scaleUp = MathF.Max(1f, minPreviewEdge / largestPixelEdge);
            scaleUp = MathF.Min(scaleUp, maxPreviewEdge / largestPixelEdge);
            Vector2 scaledDisplaySize = scaleUp > 1f
                ? new Vector2(pixelSize.X * scaleUp, pixelSize.Y * scaleUp)
                : displaySize;

            ImGui.Image(handle, scaledDisplaySize, uv0, uv1);

            string mipSize = $"{pixelSize.X} x {pixelSize.Y}";
            ImGui.TextDisabled($"{details} | Mip Size: {mipSize}");
        }
        else
        {
            ImGui.TextDisabled(failure);
        }
    }

    private static bool TryCopyMipTo1x1Texture(XRTexture texture, int mipLevel, int layerIndex, out string failure)
    {
        failure = string.Empty;

        if (!Engine.IsRenderThread)
        {
            failure = "Preview copy unavailable off render thread";
            return false;
        }

        if (AbstractRenderer.Current is not OpenGLRenderer renderer)
        {
            failure = "Preview copy requires OpenGL renderer";
            return false;
        }

        if (texture is XRTexture2D t2d && t2d.MultiSample)
        {
            failure = "Cannot preview multisampled HDRSceneTex";
            return false;
        }
        if (texture is XRTexture2DArray t2da && t2da.MultiSample)
        {
            failure = "Cannot preview multisampled HDRSceneTex";
            return false;
        }

        // Allocate a cached immutable 1x1 RGBA16f texture for display.
        _autoExposureSmallestMipCopyTex ??= CreateAutoExposureSmallestMipCopyTexture();

        var srcObj = renderer.GetOrCreateAPIRenderObject(texture) as OpenGLRenderer.GLObjectBase;
        var dstObj = renderer.GetOrCreateAPIRenderObject(_autoExposureSmallestMipCopyTex) as OpenGLRenderer.GLObjectBase;
        if (srcObj is null || dstObj is null)
        {
            failure = "Preview copy: texture not uploaded";
            return false;
        }

        uint srcId = srcObj.BindingId;
        uint dstId = dstObj.BindingId;
        if (srcId == 0 || dstId == 0 || srcId == OpenGLRenderer.GLObjectBase.InvalidBindingId || dstId == OpenGLRenderer.GLObjectBase.InvalidBindingId)
        {
            failure = "Preview copy: GL texture not ready";
            return false;
        }

        var gl = renderer.RawGL;

        // CopyImageSubData works across compatible formats. We copy one texel from the chosen mip.
        if (texture is XRTexture2DArray array)
        {
            int clampedLayer = Math.Clamp(layerIndex, 0, Math.Max(0, (int)array.Depth - 1));
            gl.CopyImageSubData(
                srcId, GLEnum.Texture2DArray, mipLevel, 0, 0, clampedLayer,
                dstId, GLEnum.Texture2D, 0, 0, 0, 0,
                1, 1, 1);
        }
        else
        {
            gl.CopyImageSubData(
                srcId, GLEnum.Texture2D, mipLevel, 0, 0, 0,
                dstId, GLEnum.Texture2D, 0, 0, 0, 0,
                1, 1, 1);
        }

        return true;
    }

    private static XRTexture2D CreateAutoExposureSmallestMipCopyTexture()
    {
        var t = XRTexture2D.CreateFrameBufferTexture(
            1u,
            1u,
            EPixelInternalFormat.Rgba16f,
            EPixelFormat.Rgba,
            EPixelType.HalfFloat);
        t.Resizable = false;
        t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
        t.MinFilter = ETexMinFilter.Nearest;
        t.MagFilter = ETexMagFilter.Nearest;
        t.UWrap = ETexWrapMode.ClampToEdge;
        t.VWrap = ETexWrapMode.ClampToEdge;
        t.Name = "AutoExposureSmallestMipCopy";
        t.SamplerName = "AutoExposureSmallestMipCopy";
        t.AutoGenerateMipmaps = false;
        return t;
    }

    private static Vector3 TonemapReinhard(Vector3 hdr)
    {
        static float Saturate(float v) => Math.Clamp(v, 0f, 1f);

        Vector3 safe = new(
            float.IsFinite(hdr.X) ? MathF.Max(0f, hdr.X) : 0f,
            float.IsFinite(hdr.Y) ? MathF.Max(0f, hdr.Y) : 0f,
            float.IsFinite(hdr.Z) ? MathF.Max(0f, hdr.Z) : 0f);

        // Simple Reinhard + gamma 2.2 for a UI-friendly swatch.
        Vector3 mapped = safe / (Vector3.One + safe);
        const float invGamma = 1f / 2.2f;
        mapped = new Vector3(
            MathF.Pow(mapped.X, invGamma),
            MathF.Pow(mapped.Y, invGamma),
            MathF.Pow(mapped.Z, invGamma));
        return new Vector3(Saturate(mapped.X), Saturate(mapped.Y), Saturate(mapped.Z));
    }

    private static unsafe bool TryReadBackRgbaFloat(XRTexture texture, int mipLevel, int layerIndex, out Vector4 rgba, out string failure)
    {
        rgba = Vector4.Zero;
        failure = string.Empty;

        if (!Engine.IsRenderThread)
        {
            failure = "Readback unavailable off render thread";
            return false;
        }

        if (AbstractRenderer.Current is not OpenGLRenderer renderer)
        {
            failure = "Readback requires OpenGL renderer";
            return false;
        }

        if (texture is XRTexture2D tex2D && tex2D.MultiSample)
        {
            failure = "Multisample textures do not support mip readback";
            return false;
        }
        if (texture is XRTexture2DArray tex2DArray && tex2DArray.MultiSample)
        {
            failure = "Multisample textures do not support mip readback";
            return false;
        }

        var apiRenderObject = renderer.GetOrCreateAPIRenderObject(texture);
        if (apiRenderObject is not OpenGLRenderer.GLObjectBase apiObject)
        {
            failure = "Texture not uploaded";
            return false;
        }

        uint binding = apiObject.BindingId;
        if (binding == OpenGLRenderer.GLObjectBase.InvalidBindingId || binding == 0)
        {
            failure = "Texture not ready";
            return false;
        }

        var gl = renderer.RawGL;

        // Read from the requested mip level. For array textures, GetTextureImage returns all layers;
        // we read layer 0 by taking the first texel.
        if (texture is XRTexture2DArray array)
        {
            int layers = Math.Max(1, (int)array.Depth);
            int floats = 4 * layers;
            float* tmp = stackalloc float[floats];
            gl.GetTextureImage(binding, mipLevel, GLEnum.Rgba, GLEnum.Float, (uint)(sizeof(float) * floats), tmp);

            int clampedLayer = Math.Clamp(layerIndex, 0, layers - 1);
            int o = clampedLayer * 4;
            rgba = new Vector4(tmp[o + 0], tmp[o + 1], tmp[o + 2], tmp[o + 3]);
            return true;
        }
        else
        {
            Vector4 tmp = Vector4.Zero;
            gl.GetTextureImage(binding, mipLevel, GLEnum.Rgba, GLEnum.Float, (uint)sizeof(Vector4), &tmp);
            rgba = tmp;
            return true;
        }
    }

    private static void DrawFboInputs(XRFrameBuffer fbo, bool flipPreview)
    {
        var inputs = CollectFboInputs(fbo);
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

    private static void DrawFboOutputs(XRFrameBuffer fbo, bool flipPreview)
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

    private static void DrawTexturePreview(XRTexture texture, bool flipPreview)
    {
        var previewState = GetPreviewState(texture);

        int layerCount = GetLayerCount(texture);
        int mipCount = GetMipCount(texture);

        DrawPreviewControls(previewState, layerCount, mipCount);

        if (TryGetTexturePreviewHandle(texture, 320f, previewState, out nint handle, out Vector2 displaySize, out Vector2 pixelSize, out string failure))
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
    }

    private static List<XRTexture> CollectFboInputs(XRFrameBuffer fbo)
    {
        if (fbo is XRMaterialFrameBuffer materialFbo && materialFbo.Material is { } material)
        {
            var unique = new List<XRTexture>();
            foreach (XRTexture? tex in material.Textures)
            {
                if (tex is null || tex.FrameBufferAttachment is not null)
                    continue;

                if (unique.Any(existing => ReferenceEquals(existing, tex)))
                    continue;

                unique.Add(tex);
            }

            return unique;
        }

        return new List<XRTexture>();
    }

    #endregion

    #region Section Drawing - Render Passes

    private static bool DrawPassMetadataSection(RenderPipeline pipeline, EditorState state)
    {
        if (!ImGui.CollapsingHeader("Render Passes", ImGuiTreeNodeFlags.DefaultOpen))
            return false;

        var passes = pipeline.PassMetadata ?? Array.Empty<RenderPassMetadata>();
        if (passes.Count == 0)
        {
            ImGui.TextDisabled("Pass metadata is empty. Trigger a render to populate it.");
            return true;
        }

        ImGui.SetNextItemWidth(260f);
        ImGui.InputTextWithHint("##RenderPassSearch", "Filter passes...", ref state.PassSearch, 128u);
        string filter = state.PassSearch ?? string.Empty;

        var filtered = FilterPasses(passes, filter).ToList();
        if (filtered.Count == 0)
        {
            ImGui.TextDisabled("No passes matched the filter.");
            return true;
        }

        foreach (var pass in filtered)
        {
            string label = $"[{pass.PassIndex}] {pass.Name}##RenderPass{pass.PassIndex}";
            bool open = ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen);
            ImGui.SameLine();
            ImGui.TextDisabled(pass.Stage.ToString());

            if (!open)
                continue;

            string dependencies = pass.ExplicitDependencies.Count == 0
                ? "<none>"
                : string.Join(", ", pass.ExplicitDependencies);
            ImGui.TextDisabled($"Depends On: {dependencies}");

            if (pass.ResourceUsages.Count == 0)
            {
                ImGui.TextDisabled("No resource usage declared.");
            }
            else if (ImGui.BeginTable($"RenderPassResources{pass.PassIndex}", 5, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn("Resource");
                ImGui.TableSetupColumn("Type");
                ImGui.TableSetupColumn("Access", ImGuiTableColumnFlags.WidthFixed, 80f);
                ImGui.TableSetupColumn("Load", ImGuiTableColumnFlags.WidthFixed, 70f);
                ImGui.TableSetupColumn("Store", ImGuiTableColumnFlags.WidthFixed, 70f);
                ImGui.TableHeadersRow();

                foreach (var usage in pass.ResourceUsages)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(usage.ResourceName);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextDisabled(usage.ResourceType.ToString());
                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextDisabled(usage.Access.ToString());
                    ImGui.TableSetColumnIndex(3);
                    ImGui.TextDisabled(usage.LoadOp.ToString());
                    ImGui.TableSetColumnIndex(4);
                    ImGui.TextDisabled(usage.StoreOp.ToString());
                }

                ImGui.EndTable();
            }

            ImGui.TreePop();
        }

        return true;
    }

    #endregion

    #region Section Drawing - Command Chain

    private static void DrawCommandChainSection(RenderPipeline pipeline, EditorState state, HashSet<object> visited)
    {
        if (!ImGui.CollapsingHeader("Command Chain", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var chain = pipeline.CommandChain;
        if (chain is null)
        {
            ImGui.TextDisabled("Pipeline does not define a command chain.");
            return;
        }

        if (chain.Count == 0)
        {
            ImGui.TextDisabled("Pipeline has no commands.");
            return;
        }

        ImGui.SetNextItemWidth(260f);
        ImGui.InputTextWithHint("##RenderPipelineCommandSearch", "Filter commands...", ref state.CommandSearch, 128u);
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear##RenderPipelineCommandSearch"))
            state.CommandSearch = string.Empty;

        ImGui.Spacing();

        var treeRoot = BuildCommandTree(chain, "Command Chain", "root", out var nodeMap);
        UpdateTreeVisibility(treeRoot, state.CommandSearch);
        EnsureSelectedCommand(state, nodeMap, treeRoot);

        Vector2 avail = ImGui.GetContentRegionAvail();
        float treeHeight = MathF.Max(260f, avail.Y);
        Vector2 treeSize = new(MathF.Max(280f, avail.X * 0.45f), treeHeight);

        using (new ImGuiChildScope("RenderPipelineCommandTree", treeSize))
        {
            bool drewAny = DrawCommandTree(treeRoot, state);
            if (!drewAny)
                ImGui.TextDisabled("No commands matched the filter.");
        }

        ImGui.SameLine();

        using (new ImGuiChildScope("RenderPipelineCommandDetails", new Vector2(0f, treeSize.Y)))
        {
            ViewportRenderCommand? selected = null;
            if (!string.IsNullOrEmpty(state.SelectedCommandPath)
                && nodeMap.TryGetValue(state.SelectedCommandPath, out var selectedNode))
            {
                selected = selectedNode.Command;
            }

            if (selected is null)
            {
                ImGui.TextDisabled("Select a command to inspect.");
                return;
            }

            ImGui.TextUnformatted(selected.GetType().Name);
            var badges = GetCommandBadges(selected);
            if (badges.Count > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, CleanBadgeColor);
                ImGui.TextDisabled(string.Join("  |  ", badges));
                ImGui.PopStyleColor();
            }

            ImGui.TextDisabled($"Executes In Shadow Pass: {selected.ExecuteInShadowPass}");
            ImGui.TextDisabled($"Collect Visible Hook: {selected.NeedsCollecVisible}");

            ImGui.Separator();
            EditorImGuiUI.DrawRuntimeObjectInspector("Command Properties", selected, visited, defaultOpen: true);
        }
    }

    #endregion

    #region Section Drawing - Raw Inspector

    private static void DrawRawInspector(RenderPipeline pipeline, HashSet<object> visited)
    {
        if (!ImGui.CollapsingHeader("Raw Properties"))
            return;

        ImGui.PushID("RenderPipelineRawInspector");
        EditorImGuiUI.DrawDefaultAssetInspector(pipeline, visited);
        ImGui.PopID();
    }

    #endregion

    #region Pass Filtering

    private static IEnumerable<RenderPassMetadata> FilterPasses(IReadOnlyCollection<RenderPassMetadata> passes, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return passes;

        string needle = filter.Trim();
        return passes.Where(pass => PassMatches(pass, needle));
    }

    private static bool PassMatches(RenderPassMetadata pass, string filter)
    {
        if (Contains(pass.Name, filter) || Contains(pass.Stage.ToString(), filter))
            return true;

        foreach (var dep in pass.ExplicitDependencies)
            if (dep.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
                return true;

        foreach (var usage in pass.ResourceUsages)
        {
            if (Contains(usage.ResourceName, filter))
                return true;
            if (Contains(usage.ResourceType.ToString(), filter))
                return true;
        }

        return false;
    }

    #endregion

    #region Command Tree - Building & Population

    private static CommandTreeNode BuildCommandTree(ViewportRenderCommandContainer container, string label, string path, out Dictionary<string, CommandTreeNode> nodeMap)
    {
        var root = new CommandTreeNode(label, path, null);
        nodeMap = new Dictionary<string, CommandTreeNode>(StringComparer.Ordinal);
        var visited = new HashSet<ViewportRenderCommandContainer>(ReferenceComparer<ViewportRenderCommandContainer>.Instance);
        PopulateCommandTree(container, root, path, nodeMap, visited);
        return root;
    }

    private static bool PopulateCommandTree(ViewportRenderCommandContainer container, CommandTreeNode parentNode, string parentPath, Dictionary<string, CommandTreeNode> nodeMap, HashSet<ViewportRenderCommandContainer> visited)
    {
        if (container is null)
            return false;

        if (!visited.Add(container))
            return false;

        nodeMap[parentNode.Path] = parentNode;

        for (int i = 0; i < container.Count; i++)
        {
            var command = container[i];
            string commandLabel = $"[{i:000}] {command.GetType().Name}";
            string commandPath = $"{parentPath}/cmd{i:000}";
            var commandNode = new CommandTreeNode(commandLabel, commandPath, command);
            nodeMap[commandPath] = commandNode;

            foreach (var child in EnumerateChildContainers(command, commandPath, container))
            {
                if (!PopulateCommandTree(child.Container, child.Node, child.Node.Path, nodeMap, visited))
                    continue;
                commandNode.Children.Add(child.Node);
            }

            parentNode.Children.Add(commandNode);
        }
        return true;
    }

    private static IEnumerable<ChildContainerInfo> EnumerateChildContainers(ViewportRenderCommand command, string parentPath, ViewportRenderCommandContainer owner)
    {
        var type = command.GetType();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
                continue;

            object? value;
            try
            {
                value = property.GetValue(command);
            }
            catch
            {
                continue;
            }

            if (value is null)
                continue;

            if (value is ViewportRenderCommandContainer container)
            {
                if (ReferenceEquals(container, owner) || container.Count == 0)
                    continue;
                yield return CreateChild(property.Name, null, container, parentPath);
                continue;
            }

            if (value is string)
                continue;

            if (value is IEnumerable enumerable)
            {
                int sequenceIndex = 0;
                foreach (var entry in enumerable)
                {
                    switch (entry)
                    {
                        case ViewportRenderCommandContainer nested:
                            if (nested is null || ReferenceEquals(nested, owner) || nested.Count == 0)
                                break;
                            yield return CreateChild(property.Name, sequenceIndex++, nested, parentPath);
                            break;
                        case DictionaryEntry dictEntry when dictEntry.Value is ViewportRenderCommandContainer dictContainer:
                            if (dictContainer is null || ReferenceEquals(dictContainer, owner) || dictContainer.Count == 0)
                                break;
                            yield return CreateChild(property.Name, dictEntry.Key ?? sequenceIndex++, dictContainer, parentPath);
                            break;
                        default:
                            var entryType = entry?.GetType();
                            if (entryType is not null
                                && entryType.IsGenericType
                                && entryType.GetGenericArguments().Length == 2)
                            {
                                object? childContainer = entryType.GetProperty("Value")?.GetValue(entry);
                                object? key = entryType.GetProperty("Key")?.GetValue(entry);
                                if (childContainer is ViewportRenderCommandContainer valueContainer
                                    && !ReferenceEquals(valueContainer, owner)
                                    && valueContainer.Count > 0)
                                    yield return CreateChild(property.Name, key ?? sequenceIndex, valueContainer, parentPath);
                            }
                            sequenceIndex++;
                            break;
                    }
                }
            }
        }

        static ChildContainerInfo CreateChild(string propertyName, object? key, ViewportRenderCommandContainer container, string parentPath)
        {
            string label = FormatContainerLabel(propertyName, key);
            string pathSegment = MakePathSegment(propertyName, key);
            string childPath = $"{parentPath}/{pathSegment}";
            var node = new CommandTreeNode(label, childPath, null);
            return new ChildContainerInfo(node, container);
        }
    }

    private static string FormatContainerLabel(string propertyName, object? key)
    {
        string baseLabel = propertyName switch
        {
            "TrueCommands" => "True Branch",
            "FalseCommands" => "False Branch",
            "DefaultCase" => "Default Case",
            _ => SplitPascalCase(propertyName).Trim()
        };

        if (baseLabel.EndsWith("Commands", StringComparison.OrdinalIgnoreCase))
            baseLabel = baseLabel[..^"Commands".Length].Trim();
        if (baseLabel.Length == 0)
            baseLabel = propertyName;

        if (key is not null)
            baseLabel = string.Concat(baseLabel, " [", key, "]");

        return baseLabel;
    }

    private static string SplitPascalCase(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var builder = new StringBuilder(input.Length * 2);
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (i > 0 && char.IsUpper(c) && !char.IsWhiteSpace(input[i - 1]))
                builder.Append(' ');
            builder.Append(c);
        }
        return builder.ToString();
    }

    private static string MakePathSegment(string propertyName, object? key)
    {
        string baseSegment = key is null
            ? propertyName
            : $"{propertyName}_{key}";

        var builder = new StringBuilder(baseSegment.Length);
        foreach (char c in baseSegment)
            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
        return builder.ToString();
    }

    #endregion

    #region Command Tree - Visibility & Selection

    private static void EnsureSelectedCommand(EditorState state, Dictionary<string, CommandTreeNode> nodeMap, CommandTreeNode root)
    {
        var firstVisible = FindFirstVisibleCommandNode(root);
        if (firstVisible is null)
        {
            state.SelectedCommandPath = null;
            return;
        }

        if (!string.IsNullOrEmpty(state.SelectedCommandPath)
            && nodeMap.TryGetValue(state.SelectedCommandPath, out var current)
            && current.Command is not null
            && current.IsVisible)
            return;

        state.SelectedCommandPath = firstVisible.Path;
    }

    private static CommandTreeNode? FindFirstVisibleCommandNode(CommandTreeNode node)
    {
        foreach (var child in node.Children)
        {
            if (!child.IsVisible)
                continue;

            if (child.Command is not null)
                return child;

            var nested = FindFirstVisibleCommandNode(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private static void UpdateTreeVisibility(CommandTreeNode node, string filter)
    {
        bool matches = string.IsNullOrWhiteSpace(filter)
            || node.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (node.Command is not null && CommandMatches(node.Command, filter));

        bool childVisible = false;
        foreach (var child in node.Children)
        {
            UpdateTreeVisibility(child, filter);
            childVisible |= child.IsVisible;
        }

        node.IsVisible = matches || childVisible;
    }

    #endregion

    #region Command Tree - Drawing

    private static bool DrawCommandTree(CommandTreeNode root, EditorState state)
    {
        bool any = false;
        foreach (var child in root.Children)
            any |= DrawCommandTreeNode(child, state);
        return any;
    }

    private static bool DrawCommandTreeNode(CommandTreeNode node, EditorState state)
    {
        if (!node.IsVisible)
            return false;

        bool hasVisibleChildren = node.Children.Any(child => child.IsVisible);
        ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.SpanAvailWidth;
        if (!hasVisibleChildren)
            flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;

        if (node.Command is not null && state.SelectedCommandPath == node.Path)
            flags |= ImGuiTreeNodeFlags.Selected;

        bool open = true;
        if (hasVisibleChildren)
            open = ImGui.TreeNodeEx(node.Path, flags, node.Label);
        else
            ImGui.TreeNodeEx(node.Path, flags, node.Label);

        if (node.Command is not null && ImGui.IsItemClicked(ImGuiMouseButton.Left))
            state.SelectedCommandPath = node.Path;

        if (hasVisibleChildren && open)
        {
            foreach (var child in node.Children)
                DrawCommandTreeNode(child, state);
            ImGui.TreePop();
        }

        return true;
    }

    #endregion

    #region Command Badges & Inspection

    private static List<string> GetCommandBadges(ViewportRenderCommand command)
    {
        var badges = new List<string>(4);
        badges.Add(command.ExecuteInShadowPass ? "Shadow" : "Main");
        if (command.NeedsCollecVisible)
            badges.Add("Collect Visible");
        if (command is ViewportStateRenderCommandBase)
            badges.Add("State Scope");
        if (HasNestedContainers(command))
            badges.Add("Branch");
        return badges;
    }

    private static bool HasNestedContainers(ViewportRenderCommand command)
    {
        var properties = command.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
        foreach (var property in properties)
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
                continue;

            if (typeof(ViewportRenderCommandContainer).IsAssignableFrom(property.PropertyType))
            {
                if (property.GetValue(command) is ViewportRenderCommandContainer container && container.Count > 0)
                    return true;
            }
            else if (typeof(IEnumerable<ViewportRenderCommandContainer>).IsAssignableFrom(property.PropertyType))
            {
                if (property.GetValue(command) is IEnumerable<ViewportRenderCommandContainer> collection)
                {
                    foreach (var nested in collection)
                    {
                        if (nested is not null && nested.Count > 0)
                            return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool CommandMatches(ViewportRenderCommand command, string filter)
    {
        if (Contains(command.GetType().Name, filter))
            return true;

        foreach (var badge in GetCommandBadges(command))
            if (Contains(badge, filter))
                return true;

        return false;
    }

    #endregion

    #region Texture Preview

    private static bool TryGetTexturePreviewHandle(
        XRTexture texture,
        float maxEdge,
        TexturePreviewState previewState,
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

        if (AbstractRenderer.Current is not OpenGLRenderer renderer)
        {
            failure = "Preview requires OpenGL renderer";
            return false;
        }

        XRTexture previewTexture = GetPreviewTexture(texture, previewState, out int mipLevel, out _, out string previewError);
        if (previewTexture is null)
        {
            failure = previewError;
            return false;
        }

        var apiRenderObject = renderer.GetOrCreateAPIRenderObject(previewTexture);
        if (apiRenderObject is not IGLTexture || apiRenderObject is not OpenGLRenderer.GLObjectBase apiObject)
        {
            failure = "Texture not uploaded";
            return false;
        }

        uint binding = apiObject.BindingId;
        if (binding == OpenGLRenderer.GLObjectBase.InvalidBindingId || binding == 0)
        {
            failure = "Texture not ready";
            return false;
        }

        Vector2 fullSize = GetPixelSizeForPreview(texture, mipLevel);
        pixelSize = fullSize;
        float largest = MathF.Max(1f, MathF.Max(pixelSize.X, pixelSize.Y));
        float scale = largest > 0f ? MathF.Min(1f, maxEdge / largest) : 1f;
        displaySize = new Vector2(pixelSize.X * scale, pixelSize.Y * scale);
        bool previewUsesView = previewTexture is XRTextureViewBase;
        if (previewUsesView || previewState.Channel != TextureChannelView.RGBA)
            ApplyChannelSwizzle(renderer, binding, previewState.Channel);
        if (previewUsesView)
            ApplyPreviewSamplingState(renderer, binding);
        handle = (nint)binding;
        return true;
    }

    #endregion

    #region Texture Preview Helpers

    private enum TextureChannelView
    {
        RGBA,
        R,
        G,
        B,
        A,
        Luminance,
    }

    private sealed class TexturePreviewState
    {
        public int Layer;
        public int Mip;
        public TextureChannelView Channel = TextureChannelView.RGBA;
    }

    private sealed class TextureViewCacheKeyComparer : IEqualityComparer<XRTexture>
    {
        public bool Equals(XRTexture? x, XRTexture? y) => ReferenceEquals(x, y);
        public int GetHashCode(XRTexture obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private static readonly Dictionary<XRTexture, TexturePreviewState> PreviewStates = new(new TextureViewCacheKeyComparer());
    private static readonly Dictionary<XRTexture, Dictionary<(int mip, int layer), XRTextureViewBase>> PreviewViews = new(new TextureViewCacheKeyComparer());

    private static TexturePreviewState GetPreviewState(XRTexture texture)
    {
        if (!PreviewStates.TryGetValue(texture, out var state))
        {
            state = new TexturePreviewState();
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

    private static void DrawPreviewControls(TexturePreviewState state, int layerCount, int mipCount)
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
            foreach (TextureChannelView view in Enum.GetValues<TextureChannelView>())
            {
                bool selected = view == state.Channel;
                if (ImGui.Selectable(GetChannelLabel(view), selected))
                    state.Channel = view;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
    }

    private static string GetChannelLabel(TextureChannelView channel)
        => channel switch
        {
            TextureChannelView.R => "Red",
            TextureChannelView.G => "Green",
            TextureChannelView.B => "Blue",
            TextureChannelView.A => "Alpha",
            TextureChannelView.Luminance => "Luma",
            _ => "RGBA",
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

    private static XRTexture? GetPreviewTexture(XRTexture texture, TexturePreviewState state, out int mipLevel, out int layerIndex, out string failure)
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
                return GetOrCreateArrayView(arrayTex, clampedLayer, clampedMip);

            case XRTexture2D tex2D when state.Mip > 0 || state.Channel != TextureChannelView.RGBA:
                int clamped2DMip = Math.Min(mipLevel, Math.Max(0, GetMipCount(tex2D) - 1));
                state.Mip = clamped2DMip;
                return GetOrCreate2DView(tex2D, clamped2DMip);

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

    private static XRTexture2DView GetOrCreate2DView(XRTexture2D texture, int mip)
    {
        if (!PreviewViews.TryGetValue(texture, out var views))
        {
            views = new Dictionary<(int mip, int layer), XRTextureViewBase>();
            PreviewViews[texture] = views;
        }

        var key = (mip, 0);
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

    private static XRTexture2DArrayView GetOrCreateArrayView(XRTexture2DArray texture, int layer, int mip)
    {
        if (!PreviewViews.TryGetValue(texture, out var views))
        {
            views = new Dictionary<(int mip, int layer), XRTextureViewBase>();
            PreviewViews[texture] = views;
        }

        var key = (mip, layer);
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

    private static void ApplyPreviewSamplingState(OpenGLRenderer renderer, uint binding)
    {
        // Texture views often have only 1 mip level; using a mipmap min filter makes them incomplete and they sample as black.
        var gl = renderer.RawGL;

        int linear = (int)GLEnum.Linear;
        int baseLevel = 0;
        int maxLevel = 0;
        int clamp = (int)GLEnum.ClampToEdge;

        gl.TextureParameterI(binding, GLEnum.TextureMinFilter, in linear);
        gl.TextureParameterI(binding, GLEnum.TextureMagFilter, in linear);
        gl.TextureParameterI(binding, GLEnum.TextureBaseLevel, in baseLevel);
        gl.TextureParameterI(binding, GLEnum.TextureMaxLevel, in maxLevel);
        gl.TextureParameterI(binding, GLEnum.TextureWrapS, in clamp);
        gl.TextureParameterI(binding, GLEnum.TextureWrapT, in clamp);
    }

    private static void ApplyChannelSwizzle(OpenGLRenderer renderer, uint binding, TextureChannelView channel)
    {
        var gl = renderer.RawGL;

        int r = (int)GLEnum.Red;
        int g = (int)GLEnum.Green;
        int b = (int)GLEnum.Blue;
        int a = (int)GLEnum.Alpha;

        switch (channel)
        {
            case TextureChannelView.R:
                g = b = r;
                a = (int)GLEnum.One;
                break;
            case TextureChannelView.G:
                r = b = g;
                a = (int)GLEnum.One;
                break;
            case TextureChannelView.B:
                r = g = b;
                a = (int)GLEnum.One;
                break;
            case TextureChannelView.A:
                r = g = b = a;
                a = (int)GLEnum.One;
                break;
            case TextureChannelView.Luminance:
                r = g = b = (int)GLEnum.Red;
                a = (int)GLEnum.One;
                break;
        }

        gl.TextureParameterI(binding, GLEnum.TextureSwizzleR, in r);
        gl.TextureParameterI(binding, GLEnum.TextureSwizzleG, in g);
        gl.TextureParameterI(binding, GLEnum.TextureSwizzleB, in b);
        gl.TextureParameterI(binding, GLEnum.TextureSwizzleA, in a);
    }

    #endregion

    #region String Utilities

    private static bool Contains(string? haystack, string needle)
        => !string.IsNullOrWhiteSpace(haystack)
           && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string GetDisplayName(RenderPipeline pipeline)
    {
        if (!string.IsNullOrWhiteSpace(pipeline.Name))
            return pipeline.Name!;
        return pipeline.GetType().Name;
    }

    #endregion

    #region Nested Types

    private sealed class EditorState
    {
        public string PassSearch = string.Empty;
        public string CommandSearch = string.Empty;
        public string? SelectedCommandPath;
        public int SelectedInstanceIndex;
        public string? SelectedFboName;
        public bool FlipPreview = true;
    }

    private sealed class CommandTreeNode(string label, string path, ViewportRenderCommand? command)
    {
        public string Label { get; } = label;
        public string Path { get; } = path;
        public ViewportRenderCommand? Command { get; } = command;
        public List<CommandTreeNode> Children { get; } = new();
        public bool IsVisible { get; set; }
    }

    private readonly record struct ChildContainerInfo(CommandTreeNode Node, ViewportRenderCommandContainer Container);

    private sealed class ReferenceComparer<T> : IEqualityComparer<T>
        where T : class
    {
        public static ReferenceComparer<T> Instance { get; } = new();

        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private readonly struct ImGuiChildScope : IDisposable
    {
        public ImGuiChildScope(string id, Vector2 size)
        {
            ImGui.BeginChild(id, size, ImGuiChildFlags.Border);
        }

        public void Dispose()
        {
            ImGui.EndChild();
        }
    }

    #endregion
}
