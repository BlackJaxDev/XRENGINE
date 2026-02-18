using System;
using System.Buffers;
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
using XREngine.Rendering.Vulkan;

namespace XREngine.Editor.AssetEditors;

public sealed class RenderPipelineInspector : IXRAssetInspector
{
    #region Constants & Fields

    private static readonly Vector4 DirtyBadgeColor = new(0.95f, 0.55f, 0.2f, 1f);
    private static readonly Vector4 CleanBadgeColor = new(0.5f, 0.8f, 0.5f, 1f);
    private static readonly Vector4 WarningColor = new(0.95f, 0.45f, 0.45f, 1f);

    private readonly ConditionalWeakTable<RenderPipeline, EditorState> _stateCache = new();

    #endregion

    #region Public API

    public void DrawInspector(EditorImGuiUI.InspectorTargetSet targets, HashSet<object> visitedObjects)
    {
        var pipelines = targets.Targets.OfType<RenderPipeline>().Cast<object>().ToList();
        if (pipelines.Count == 0)
        {
            foreach (var asset in targets.Targets.OfType<XRAsset>())
                EditorImGuiUI.DrawDefaultAssetInspector(asset, visitedObjects);
            return;
        }

        if (targets.HasMultipleTargets)
        {
            EditorImGuiUI.DrawDefaultAssetInspector(new EditorImGuiUI.InspectorTargetSet(pipelines, targets.CommonType), visitedObjects);
            return;
        }

        var pipeline = (RenderPipeline)pipelines[0];

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

        DrawAutoExposureMeteringPreview(selectedInstance);
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

    private static void DrawAutoExposureMeteringPreview(XRRenderPipelineInstance instance)
    {
        ImGui.TextUnformatted("Auto Exposure (Metering Preview)");

        if (!instance.Resources.TryGetTexture(DefaultRenderPipeline.HDRSceneTextureName, out XRTexture? hdrTex) || hdrTex is null)
        {
            ImGui.TextDisabled($"Texture '{DefaultRenderPipeline.HDRSceneTextureName}' not found on this instance.");
            return;
        }

        if (!TryGetColorGradingSettings(instance, out ColorGradingSettings? grading))
            grading = null;

        var mode = grading?.AutoExposureMetering ?? ColorGradingSettings.AutoExposureMeteringMode.Average;
        int targetSize = grading?.AutoExposureMeteringTargetSize ?? 16;
        float ignoreTopPercent = grading?.AutoExposureIgnoreTopPercent ?? 0.02f;
        float centerStrength = grading?.AutoExposureCenterWeightStrength ?? 1.0f;
        float centerPower = grading?.AutoExposureCenterWeightPower ?? 2.0f;
        Vector3 luminanceWeights = grading?.AutoExposureLuminanceWeights ?? Engine.Rendering.Settings.DefaultLuminance;

        if (!TryGetTextureBaseDimensions(hdrTex, out int baseWidth, out int baseHeight, out int smallestAllowedMip))
        {
            ImGui.TextDisabled($"Unsupported HDR scene texture type: {hdrTex.GetType().Name}");
            return;
        }

        int smallestMip = XRTexture.GetSmallestMipmapLevel((uint)baseWidth, (uint)baseHeight, smallestAllowedMip);
        int meteringMip = mode == ColorGradingSettings.AutoExposureMeteringMode.Average
            ? smallestMip
            : ComputeMeteringMip(baseWidth, baseHeight, targetSize, smallestMip);

        GetMipDimensions(baseWidth, baseHeight, meteringMip, out int mipW, out int mipH);
        ImGui.TextDisabled($"Mode: {mode} | Metering Mip: {meteringMip} ({mipW}x{mipH}) | Smallest Mip: {smallestMip}");

        // This panel intentionally avoids ImGui.Image for HDR mip previews (it tends to appear black and is redundant).
        // Instead, we compute mode-specific swatches from the same sample strategy as the GPU auto-exposure shader.
        if (mode == ColorGradingSettings.AutoExposureMeteringMode.Average)
        {
            if (TryReadBackRgbaFloat(hdrTex, smallestMip, 0, out Vector4 rgba, out string failure))
            {
                ImGui.TextDisabled($"Smallest mip RGBA (float): {rgba.X:0.####}, {rgba.Y:0.####}, {rgba.Z:0.####}, {rgba.W:0.####}");
                DrawTonemappedSwatch("##AutoExposure_AverageSwatch", new Vector3(rgba.X, rgba.Y, rgba.Z), new Vector2(160f, 160f));
            }
            else
            {
                ImGui.TextDisabled(failure);
            }
            return;
        }

        if (!TryReadBackMipRgbaFloat(hdrTex, meteringMip, 0, out float[]? rgbaFloats, out int width, out int height, out string readbackFailure))
        {
            ImGui.TextDisabled(readbackFailure);
            return;
        }

        float[] rgbaBuffer = rgbaFloats!;

        try
        {
            int total = Math.Max(1, width * height);
            int sampleCount = Math.Min(256, total);
            int stride = Math.Max(1, total / sampleCount);

            Vector3 meanRgb = Vector3.Zero;
            float meanLum = 0.0f;

            // For sorting-based metering.
            float[]? lums = mode == ColorGradingSettings.AutoExposureMeteringMode.IgnoreTopPercent
                ? ArrayPool<float>.Shared.Rent(sampleCount)
                : null;
            Vector3[]? rgbs = mode == ColorGradingSettings.AutoExposureMeteringMode.IgnoreTopPercent
                ? ArrayPool<Vector3>.Shared.Rent(sampleCount)
                : null;

            float sumLogLum = 0.0f;
            float weightedLumSum = 0.0f;
            float weightSum = 0.0f;
            Vector3 weightedRgbSum = Vector3.Zero;

            float invW = 1.0f / Math.Max(1.0f, width);
            float invH = 1.0f / Math.Max(1.0f, height);

            for (int i = 0; i < sampleCount; i++)
            {
                int idx = i * stride;
                int x = idx % width;
                int y = idx / width;
                if (y >= height)
                    y = height - 1;

                Vector3 rgb = FetchRgbFromRgbaFloatBuffer(rgbaBuffer, width, x, y);
                float lum = ComputeLuminance(rgb, luminanceWeights);

                meanRgb += rgb;
                meanLum += lum;

                if (mode == ColorGradingSettings.AutoExposureMeteringMode.LogAverage)
                {
                    sumLogLum += MathF.Log(MathF.Max(lum, 1e-6f));
                }
                else if (mode == ColorGradingSettings.AutoExposureMeteringMode.CenterWeighted)
                {
                    float u = (x + 0.5f) * invW;
                    float v = (y + 0.5f) * invH;
                    float dx = u - 0.5f;
                    float dy = v - 0.5f;
                    float r = MathF.Sqrt(dx * dx + dy * dy) / 0.70710678f;
                    float center = MathF.Pow(Math.Clamp(1.0f - r, 0.0f, 1.0f), MathF.Max(0.1f, centerPower));
                    float wgt = Lerp(1.0f, center, Math.Clamp(centerStrength, 0.0f, 1.0f));
                    weightedLumSum += lum * wgt;
                    weightedRgbSum += rgb * wgt;
                    weightSum += wgt;
                }
                else if (mode == ColorGradingSettings.AutoExposureMeteringMode.IgnoreTopPercent)
                {
                    lums![i] = lum;
                    rgbs![i] = rgb;
                }
            }

            meanRgb /= Math.Max(1, sampleCount);
            meanLum /= Math.Max(1, sampleCount);

            Vector3 meteredRgb;
            float meteredLum;
            string meteredLabel;

            switch (mode)
            {
                case ColorGradingSettings.AutoExposureMeteringMode.LogAverage:
                    meteredLum = MathF.Exp(sumLogLum / Math.Max(1, sampleCount));
                    meteredRgb = meanRgb;
                    meteredLabel = $"LogAvg lum: {meteredLum:0.####} | Mean lum: {meanLum:0.####}";
                    break;

                case ColorGradingSettings.AutoExposureMeteringMode.CenterWeighted:
                    meteredLum = weightedLumSum / MathF.Max(weightSum, 1e-6f);
                    meteredRgb = weightedRgbSum / MathF.Max(weightSum, 1e-6f);
                    meteredLabel = $"Center-weighted lum: {meteredLum:0.####} | Mean lum: {meanLum:0.####}";
                    break;

                case ColorGradingSettings.AutoExposureMeteringMode.IgnoreTopPercent:
                    {
                        int dropCount = (int)MathF.Floor(Math.Clamp(ignoreTopPercent, 0.0f, 0.5f) * sampleCount);

                        // Sort samples by luminance (ascending) and drop the brightest tail.
                        // Simple insertion sort is fine at <= 256.
                        for (int i = 1; i < sampleCount; i++)
                        {
                            float lumKey = lums![i];
                            Vector3 rgbKey = rgbs![i];
                            int j = i - 1;
                            while (j >= 0 && lums[j] > lumKey)
                            {
                                lums[j + 1] = lums[j];
                                rgbs[j + 1] = rgbs[j];
                                j--;
                            }
                            lums[j + 1] = lumKey;
                            rgbs[j + 1] = rgbKey;
                        }

                        int keep = Math.Clamp(sampleCount - dropCount, 1, sampleCount);
                        float sumLum = 0.0f;
                        Vector3 sumRgb = Vector3.Zero;
                        for (int i = 0; i < keep; i++)
                        {
                            sumLum += lums![i];
                            sumRgb += rgbs![i];
                        }

                        meteredLum = sumLum / keep;
                        meteredRgb = sumRgb / keep;
                        meteredLabel = $"Ignore top {ignoreTopPercent:P1} → kept {keep}/{sampleCount} | Lum: {meteredLum:0.####} | Mean lum: {meanLum:0.####}";
                        break;
                    }

                default:
                    meteredLum = meanLum;
                    meteredRgb = meanRgb;
                    meteredLabel = $"Mean lum: {meteredLum:0.####}";
                    break;
            }

            ImGui.TextDisabled(meteredLabel);

            // Stack the swatches so it's clear which one is top vs bottom.
            ImGui.BeginGroup();
            DrawLabeledTonemappedSwatch(
                "##AutoExposure_MeteredColor",
                "Metered Color",
                meteredRgb,
                new Vector2(180f, 90f));
            ImGui.Dummy(new Vector2(0f, 6f));
            DrawLabeledTonemappedSwatch(
                "##AutoExposure_MeteredLum",
                "Metered Luminance",
                new Vector3(meteredLum, meteredLum, meteredLum),
                new Vector2(180f, 90f));
            ImGui.EndGroup();
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rgbaBuffer);
        }
    }

    private static bool TryGetColorGradingSettings(XRRenderPipelineInstance instance, out ColorGradingSettings? grading)
    {
        grading = null;

        // RenderPipelineInstance.RenderState.SceneCamera is only set during the render pass scope
        // (RenderState.PushMainAttributes). When the inspector draws, this is often null.
        // Fall back to the global render-state camera / viewport active camera.
        XRCamera? camera =
            instance.RenderState.SceneCamera
            ?? instance.RenderState.RenderingCamera
            ?? instance.LastSceneCamera
            ?? instance.LastRenderingCamera
            ?? Engine.Rendering.State.RenderingPipelineState?.SceneCamera
            ?? Engine.Rendering.State.CurrentRenderingPipeline?.RenderState.SceneCamera
            ?? Engine.Rendering.State.CurrentRenderingPipeline?.RenderState.RenderingCamera
            ?? Engine.Rendering.State.RenderingPipelineState?.WindowViewport?.ActiveCamera
            ?? Engine.State.MainPlayer?.Viewport?.ActiveCamera;

        var stage = camera?.GetPostProcessStageState<ColorGradingSettings>();
        if (stage?.TryGetBacking(out ColorGradingSettings? g) != true)
            return false;

        grading = g;
        return true;
    }

    private static bool TryGetTextureBaseDimensions(XRTexture texture, out int width, out int height, out int smallestAllowedMip)
    {
        switch (texture)
        {
            case XRTexture2D t2d:
                width = (int)t2d.Width;
                height = (int)t2d.Height;
                smallestAllowedMip = t2d.SmallestAllowedMipmapLevel;
                return true;
            case XRTexture2DArray t2da:
                width = (int)t2da.Width;
                height = (int)t2da.Height;
                smallestAllowedMip = t2da.SmallestAllowedMipmapLevel;
                return true;
            default:
                width = 0;
                height = 0;
                smallestAllowedMip = 0;
                return false;
        }
    }

    private static int ComputeMeteringMip(int width, int height, int targetMaxDim, int smallestMip)
    {
        targetMaxDim = Math.Clamp(targetMaxDim, 1, 64);
        int mip = 0;
        while (mip < smallestMip)
        {
            GetMipDimensions(width, height, mip, out int mw, out int mh);
            if (Math.Max(mw, mh) <= targetMaxDim)
                break;
            mip++;
        }
        return Math.Clamp(mip, 0, smallestMip);
    }

    private static void GetMipDimensions(int width, int height, int mip, out int mipWidth, out int mipHeight)
    {
        mipWidth = Math.Max(1, width >> mip);
        mipHeight = Math.Max(1, height >> mip);
    }

    private static Vector3 FetchRgbFromRgbaFloatBuffer(float[] rgbaFloats, int width, int x, int y)
    {
        int baseIndex = (y * width + x) * 4;
        if ((uint)(baseIndex + 2) >= (uint)rgbaFloats.Length)
            return Vector3.Zero;

        float r = rgbaFloats[baseIndex + 0];
        float g = rgbaFloats[baseIndex + 1];
        float b = rgbaFloats[baseIndex + 2];
        if (!float.IsFinite(r)) r = 0f;
        if (!float.IsFinite(g)) g = 0f;
        if (!float.IsFinite(b)) b = 0f;
        return new Vector3(MathF.Max(0f, r), MathF.Max(0f, g), MathF.Max(0f, b));
    }

    private static float ComputeLuminance(Vector3 rgb, Vector3 weights)
    {
        static float Sanitize(float v) => float.IsFinite(v) ? MathF.Max(0.0f, v) : 0.0f;

        rgb = new Vector3(Sanitize(rgb.X), Sanitize(rgb.Y), Sanitize(rgb.Z));
        weights = new Vector3(Sanitize(weights.X), Sanitize(weights.Y), Sanitize(weights.Z));
        float sum = weights.X + weights.Y + weights.Z;
        if (!(sum > 0.0f) || float.IsNaN(sum) || float.IsInfinity(sum))
            weights = Engine.Rendering.Settings.DefaultLuminance;
        else
            weights /= sum;

        return Vector3.Dot(rgb, weights);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static void DrawTonemappedSwatch(string id, Vector3 hdrRgb, Vector2 size)
    {
        Vector3 tonemapped = TonemapReinhard(hdrRgb);
        ImGui.ColorButton(id, new Vector4(tonemapped, 1f), ImGuiColorEditFlags.NoTooltip, size);
    }

    private static void DrawLabeledTonemappedSwatch(string id, string overlayText, Vector3 hdrRgb, Vector2 size)
    {
        DrawTonemappedSwatch(id, hdrRgb, size);

        // Overlay label text onto the swatch (theme-driven colors).
        var drawList = ImGui.GetWindowDrawList();
        Vector2 min = ImGui.GetItemRectMin();

        // Padding inside the swatch.
        Vector2 pos = new(min.X + 6f, min.Y + 6f);

        uint shadow = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        uint text = ImGui.GetColorU32(ImGuiCol.Text);

        // Simple drop-shadow for readability without hard-coded colors.
        drawList.AddText(new Vector2(pos.X + 1f, pos.Y + 1f), shadow, overlayText);
        drawList.AddText(pos, text, overlayText);
    }

    private static bool TryReadBackMipRgbaFloat(
        XRTexture texture,
        int mipLevel,
        int layerIndex,
        out float[]? rgbaFloats,
        out int width,
        out int height,
        out string failure)
    {
        rgbaFloats = null;
        width = 0;
        height = 0;
        failure = string.Empty;

        if (!Engine.IsRenderThread)
        {
            failure = "Readback unavailable off render thread";
            return false;
        }

        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (renderer is null)
        {
            failure = "Readback requires an active renderer";
            return false;
        }

        return renderer.TryReadTextureMipRgbaFloat(texture, mipLevel, layerIndex, out rgbaFloats, out width, out height, out failure);
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

    private static bool TryReadBackRgbaFloat(XRTexture texture, int mipLevel, int layerIndex, out Vector4 rgba, out string failure)
    {
        rgba = Vector4.Zero;
        failure = string.Empty;

        if (!Engine.IsRenderThread)
        {
            failure = "Readback unavailable off render thread";
            return false;
        }

        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (renderer is null)
        {
            failure = "Readback requires an active renderer";
            return false;
        }

        return renderer.TryReadTexturePixelRgbaFloat(texture, mipLevel, layerIndex, out rgba, out failure);
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

        XRTexture? previewTexture = GetPreviewTexture(texture, previewState, out int mipLevel, out _, out string previewError);
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

        if (AbstractRenderer.Current is VulkanRenderer vkRenderer)
        {
            IntPtr textureId = vkRenderer.RegisterImGuiTexture(previewTexture);
            if (textureId == IntPtr.Zero)
            {
                failure = "Texture not uploaded";
                return false;
            }

            handle = (nint)textureId;
            return true;
        }

        if (AbstractRenderer.Current is not OpenGLRenderer renderer)
        {
            failure = "Preview requires OpenGL or Vulkan renderer";
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
