using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using XREngine;
using XREngine.Data.Rendering;
using XREngine.Core.Files;
using XREngine.Editor.RenderPipelineWorkbench;
using XREngine.Editor.Services;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Vulkan;

namespace XREngine.Editor.AssetEditors;

public sealed class RenderPipelineInspector : IXRAssetInspector
{
    #region Constants & Fields

    private static readonly Vector4 DirtyBadgeColor = new(0.95f, 0.55f, 0.2f, 1f);
    private static readonly Vector4 CleanBadgeColor = new(0.5f, 0.8f, 0.5f, 1f);
    private static readonly Vector4 WarningColor = new(0.95f, 0.45f, 0.45f, 1f);
    private const long AutoExposurePreviewRefreshIntervalMs = 250;

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

        bool drewAdvancedCapabilities = DrawAdvancedCapabilitiesSection(pipeline);
        if (drewAdvancedCapabilities)
            ImGui.Separator();

        bool drewInstances = DrawInstancesSection(pipeline);
        if (drewInstances)
            ImGui.Separator();

        DrawWorkbenchSummary(pipeline);
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
        if (ImGui.SmallButton("Open in Workbench##RenderPipeline"))
            EditorImGuiUI.OpenRenderPipelineWorkbench(pipeline);

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

    #region Section Drawing - Advanced Capabilities

    private static bool DrawAdvancedCapabilitiesSection(RenderPipeline pipeline)
    {
        if (pipeline is not IAdvancedRenderPipelineCapabilitySource source)
            return false;

        if (!ImGui.CollapsingHeader(
                "Advanced Pipeline Capabilities",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            return true;
        }

        AdvancedRenderPipelineCapabilityResult result = source.CapabilityResult;
        AdvancedRenderPipelineCapabilities capabilities = result.Capabilities;
        Vector4 statusColor = result.IsSupported ? CleanBadgeColor : WarningColor;

        ImGui.TextColored(statusColor, result.IsSupported ? "Supported" : "Rejected");
        ImGui.SameLine();
        ImGui.TextDisabled($"Backend: {capabilities.Backend}");
        ImGui.TextWrapped(result.Diagnostic);
        ImGui.TextDisabled($"Encodings: {capabilities.DescribeSelectedEncodings()}");
        ImGui.TextDisabled($"Rejection: {result.RejectionReason}");
        ImGui.TextDisabled(
            $"Required: integer targets {capabilities.SupportsIntegerRenderTargets}, " +
            $"compute {capabilities.SupportsComputeShaders}, storage buffers {capabilities.SupportsStorageBuffers}, " +
            $"frame slots {capabilities.SupportsFrameSlotStorage}, stereo arrays {capabilities.SupportsStereoArrayResources}");
        ImGui.TextDisabled(
            $"Optional: buffer address {capabilities.SupportsBufferDeviceAddress}, " +
            $"descriptor indexing {capabilities.SupportsDescriptorIndexing}, descriptor heap {capabilities.SupportsDescriptorHeap}, " +
            $"subgroups {capabilities.SupportsSubgroupOperations}, mesh shaders {capabilities.SupportsMeshShaders}, " +
            $"async compute {capabilities.SupportsAsyncCompute}, timeline semaphores {capabilities.SupportsTimelineSemaphores}");
        return true;
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

            var activeInstance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
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
                if (ImGui.SmallButton($"Workbench##RPInstanceGraph{instance.GetHashCode():X8}"))
                    EditorImGuiUI.OpenRenderPipelineWorkbench(pipeline, instance);
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

    /// <summary>
    /// Draws the auto exposure metering preview for one live instance. The resource browser moved to the Resources
    /// view of the Render Pipeline Workbench.
    /// </summary>
    private static void DrawDebugViews(RenderPipeline pipeline, EditorState state)
    {
        if (!ImGui.CollapsingHeader("Auto Exposure (Metering Preview)"))
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
        if (ImGui.BeginCombo("Instance##RenderPipelineAutoExposure", currentInstanceLabel))
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

        XRRenderPipelineInstance selectedInstance = instances[state.SelectedInstanceIndex];
        if (ImGui.SmallButton("Browse resources in Workbench##RenderPipelineAutoExposure"))
            EditorImGuiUI.OpenRenderPipelineWorkbench(pipeline, selectedInstance);

        DrawAutoExposureMeteringPreview(selectedInstance);
    }

    private static void DrawAutoExposureMeteringPreview(XRRenderPipelineInstance instance)
    {
        if (!instance.Resources.TryGetTexture(DefaultRenderPipeline.HDRSceneTextureName, out XRTexture? hdrTex) || hdrTex is null)
        {
            ImGui.TextDisabled($"Texture '{DefaultRenderPipeline.HDRSceneTextureName}' not found on this instance.");
            return;
        }

        if (!TryGetColorGradingSettings(instance, out ColorGradingSettings? grading))
            grading = null;

        var mode = grading?.AutoExposureMetering ?? ColorGradingSettings.AutoExposureMeteringMode.LogAverage;
        int targetSize = grading?.AutoExposureMeteringTargetSize ?? 16;
        float ignoreTopPercent = grading?.AutoExposureIgnoreTopPercent ?? 0.02f;
        float centerStrength = grading?.AutoExposureCenterWeightStrength ?? 1.0f;
        float centerPower = grading?.AutoExposureCenterWeightPower ?? 2.0f;
        Vector3 luminanceWeights = grading?.AutoExposureLuminanceWeights ?? RuntimeEngine.Rendering.Settings.DefaultLuminance;

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

        string requestKey = $"{instance.GetHashCode():X8}:{mode}:{meteringMip}:{smallestMip}:{targetSize}:{ignoreTopPercent:0.####}:{centerStrength:0.####}:{centerPower:0.####}:{luminanceWeights.X:0.####}:{luminanceWeights.Y:0.####}:{luminanceWeights.Z:0.####}";
        var previewState = GetOrCreateAutoExposurePreviewState(instance);

        int readbackMip = mode == ColorGradingSettings.AutoExposureMeteringMode.Average ? smallestMip : meteringMip;
        bool settingsChanged = !string.Equals(previewState.RequestKey, requestKey, StringComparison.Ordinal);
        bool refreshDue = unchecked(Environment.TickCount64 - previewState.LastRequestTick) >= AutoExposurePreviewRefreshIntervalMs;
        if (!previewState.HasPendingRequest && (settingsChanged || refreshDue))
            RequestAutoExposurePreview(instance, hdrTex, previewState, requestKey, readbackMip);

        if (previewState.HasPendingRequest)
            ImGui.TextDisabled("Preview readback pending...");

        if (!string.IsNullOrWhiteSpace(previewState.Failure))
        {
            ImGui.TextDisabled(previewState.Failure);
            return;
        }

        if (previewState.RgbaFloats is null)
            return;

        // This panel intentionally avoids ImGui.Image for HDR mip previews (it tends to appear black and is redundant).
        // Instead, we compute mode-specific swatches from the same sample strategy as the GPU auto-exposure shader.
        if (mode == ColorGradingSettings.AutoExposureMeteringMode.Average)
        {
            float[] rgbaFloats = previewState.RgbaFloats;
            if (rgbaFloats.Length < 4)
            {
                ImGui.TextDisabled("Async readback returned insufficient data.");
                return;
            }

            Vector4 rgba = new(rgbaFloats[0], rgbaFloats[1], rgbaFloats[2], rgbaFloats[3]);
            ImGui.TextDisabled($"Smallest mip RGBA (float): {rgba.X:0.####}, {rgba.Y:0.####}, {rgba.Z:0.####}, {rgba.W:0.####}");
            DrawTonemappedSwatch("##AutoExposure_AverageSwatch", new Vector3(rgba.X, rgba.Y, rgba.Z), new Vector2(160f, 160f));
            return;
        }

        float[] rgbaBuffer = previewState.RgbaFloats;
        int width = previewState.Width;
        int height = previewState.Height;
        float[]? lums = null;
        Vector3[]? rgbs = null;

        try
        {
            int total = Math.Max(1, width * height);
            int sampleCount = Math.Min(256, total);
            int stride = Math.Max(1, total / sampleCount);

            Vector3 meanRgb = Vector3.Zero;
            float meanLum = 0.0f;

            // For sorting-based metering.
            lums = mode == ColorGradingSettings.AutoExposureMeteringMode.IgnoreTopPercent
                ? ArrayPool<float>.Shared.Rent(sampleCount)
                : null;
            rgbs = mode == ColorGradingSettings.AutoExposureMeteringMode.IgnoreTopPercent
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
            if (lums is not null)
                ArrayPool<float>.Shared.Return(lums);
            if (rgbs is not null)
                ArrayPool<Vector3>.Shared.Return(rgbs);
        }
    }

    private static AutoExposurePreviewState GetOrCreateAutoExposurePreviewState(XRRenderPipelineInstance instance)
        => AutoExposurePreviewStates.GetValue(instance, _ => new AutoExposurePreviewState());

    private static void RequestAutoExposurePreview(
        XRRenderPipelineInstance instance,
        XRTexture texture,
        AutoExposurePreviewState previewState,
        string requestKey,
        int mipLevel)
    {
        previewState.RequestKey = requestKey;
        previewState.LastRequestTick = Environment.TickCount64;
        previewState.HasPendingRequest = true;
        previewState.Failure = null;

        void FallbackReadback()
        {
            string failure = string.Empty;
            bool success = false;
            float[]? rgbaFloats = null;
            int width = 0;
            int height = 0;

            if (AbstractRenderer.Current is { } renderer)
            {
                success = renderer.TryReadTextureMipRgbaFloat(texture, mipLevel, 0, out rgbaFloats, out width, out height, out failure);
            }
            else
            {
                failure = "Readback requires an active renderer.";
            }

            previewState.HasPendingRequest = false;
            previewState.Failure = success ? null : failure;
            previewState.RgbaFloats = success ? rgbaFloats : null;
            previewState.Width = success ? width : 0;
            previewState.Height = success ? height : 0;
        }

        if (!Engine.InvokeOnMainThread(FallbackReadback, "RenderPipelineInspector.AutoExposureReadback", true))
            FallbackReadback();
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
            ?? RuntimeEngine.Rendering.State.RenderingPipelineState?.SceneCamera
            ?? RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState.SceneCamera
            ?? RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState.RenderingCamera
            ?? RuntimeEngine.Rendering.State.RenderingPipelineState?.WindowViewport?.ActiveCamera
            ?? (Engine.State.MainPlayer?.Viewport as XRViewport)?.ActiveCamera;

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
            weights = RuntimeEngine.Rendering.Settings.DefaultLuminance;
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

    #endregion

    #region Section Drawing - Workbench Summary

    /// <summary>
    /// Shows command and pass counts. The command flow, the pass graph, and the resources live in the
    /// Render Pipeline Workbench, so the asset inspector does not repeat them.
    /// </summary>
    private static void DrawWorkbenchSummary(RenderPipeline pipeline)
    {
        if (!ImGui.CollapsingHeader("Structure", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var structure = pipeline.StructureSnapshot;
        ImGui.TextDisabled($"Commands: {structure.Commands.Count}  Passes: {pipeline.PassMetadata?.Count ?? 0}  Schema stages: {pipeline.PostProcessSchema.StagesByKey.Count}");
        if (structure.GpuCommandsWithoutPasses.Count > 0)
            ImGui.TextColored(WarningColor, $"{structure.GpuCommandsWithoutPasses.Count} commands do GPU work but declare no pass.");

        if (ImGui.Button("Open in Workbench##RenderPipelineSummary"))
            EditorImGuiUI.OpenRenderPipelineWorkbench(pipeline);
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


    #region String Utilities

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
        public int SelectedInstanceIndex;
    }

    private sealed class AutoExposurePreviewState
    {
        public string? RequestKey;
        public long LastRequestTick;
        public bool HasPendingRequest;
        public string? Failure;
        public float[]? RgbaFloats;
        public int Width;
        public int Height;
    }

    private static readonly ConditionalWeakTable<XRRenderPipelineInstance, AutoExposurePreviewState> AutoExposurePreviewStates = new();

    #endregion
}
