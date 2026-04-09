using ImGuiNET;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Rendering;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static bool _showTextureStreaming;
    private static bool _textureStreamingFrozen;
    private static ImportedTextureStreamingTelemetry _frozenTelemetry;
    private static IReadOnlyList<ImportedTextureStreamingTextureTelemetry>? _frozenTextureTelemetry;

    private static void DrawTextureStreamingPanel()
    {
        if (!_showTextureStreaming) return;
        if (!ImGui.Begin("Texture Streaming", ref _showTextureStreaming, ImGuiWindowFlags.MenuBar))
        {
            ImGui.End();
            return;
        }

        if (ImGui.BeginMenuBar())
        {
            if (ImGui.MenuItem(_textureStreamingFrozen ? "Unfreeze" : "Freeze"))
                _textureStreamingFrozen = !_textureStreamingFrozen;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Freeze stops updating the panel with live data so you can inspect a snapshot.");
            ImGui.EndMenuBar();
        }

        ImportedTextureStreamingTelemetry telemetry;
        IReadOnlyList<ImportedTextureStreamingTextureTelemetry> textures;

        if (_textureStreamingFrozen)
        {
            telemetry = _frozenTelemetry;
            textures = _frozenTextureTelemetry ?? [];
        }
        else
        {
            telemetry = XRTexture2D.GetImportedTextureStreamingTelemetry();
            textures = XRTexture2D.GetImportedTextureStreamingTextureTelemetry();
            _frozenTelemetry = telemetry;
            _frozenTextureTelemetry = textures;
        }

        DrawTextureStreamingSummary(telemetry);
        ImGui.Separator();
        DrawTextureStreamingTable(textures, telemetry.LastFrameId);

        ImGui.End();
    }

    private static void DrawTextureStreamingSummary(ImportedTextureStreamingTelemetry t)
    {
        if (!ImGui.CollapsingHeader("Summary", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.Indent();

        ImGui.Text("Backend:");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.6f, 0.9f, 1.0f, 1.0f), t.BackendName);

        ImGui.Text("Promotions:");
        ImGui.SameLine();
        if (t.PromotionsBlocked)
            ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.2f, 1.0f), "BLOCKED (import in progress)");
        else
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "allowed");

        ImGui.Text($"Tracked textures:   {t.TrackedTextureCount}");
        ImGui.Text($"Active import scopes: {t.ActiveImportScopes}");
        ImGui.Text($"Pending transitions: {t.PendingTransitionCount}");
        ImGui.Text($"Queued this frame:  {t.QueuedTransitionsThisFrame}");
        ImGui.Text($"Active decodes:     {t.ActiveDecodeCount}");
        ImGui.Text($"Queued decodes:     {t.QueuedDecodeCount}");

        ImGui.Spacing();

        float budgetMB = t.AvailableManagedBytes == long.MaxValue ? float.PositiveInfinity : t.AvailableManagedBytes / (1024f * 1024f);
        float currentMB = t.CurrentManagedBytes / (1024f * 1024f);
        float assignedMB = t.AssignedManagedBytes / (1024f * 1024f);
        float uploadMB = t.UploadBytesScheduledThisFrame / (1024f * 1024f);

        ImGui.Text($"VRAM budget:        {(float.IsInfinity(budgetMB) ? "unlimited" : $"{budgetMB:F1} MB")}");
        ImGui.Text($"Current managed:    {currentMB:F1} MB");
        ImGui.Text($"Assigned managed:   {assignedMB:F1} MB");
        ImGui.Text($"Upload this frame:  {uploadMB:F2} MB");

        if (t.AvailableManagedBytes != long.MaxValue && t.AvailableManagedBytes > 0)
        {
            float fraction = Math.Clamp((float)t.CurrentManagedBytes / t.AvailableManagedBytes, 0f, 1f);
            Vector4 barColor = fraction > 0.9f
                ? new Vector4(1.0f, 0.3f, 0.2f, 1.0f)
                : fraction > 0.7f
                    ? new Vector4(1.0f, 0.8f, 0.2f, 1.0f)
                    : new Vector4(0.3f, 0.8f, 0.3f, 1.0f);
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, barColor);
            ImGui.ProgressBar(fraction, new Vector2(-1, 0), $"{currentMB:F1} / {budgetMB:F1} MB");
            ImGui.PopStyleColor();
        }

        ImGui.Unindent();
    }

    private static void DrawTextureStreamingTable(
        IReadOnlyList<ImportedTextureStreamingTextureTelemetry> textures,
        long currentFrameId)
    {
        if (!ImGui.CollapsingHeader($"Textures ({textures.Count})", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGuiTableFlags flags =
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.Reorderable |
            ImGuiTableFlags.Hideable |
            ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.BordersOuter |
            ImGuiTableFlags.BordersV |
            ImGuiTableFlags.SizingStretchProp;

        float tableHeight = ImGui.GetContentRegionAvail().Y - ImGui.GetTextLineHeightWithSpacing();
        if (!ImGui.BeginTable("TextureStreamingTable", 12, flags, new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Name",         ImGuiTableColumnFlags.None,      180f);
        ImGui.TableSetupColumn("Source",       ImGuiTableColumnFlags.None,       70f);
        ImGui.TableSetupColumn("Resident",     ImGuiTableColumnFlags.None,       65f);
        ImGui.TableSetupColumn("Desired",      ImGuiTableColumnFlags.None,       60f);
        ImGui.TableSetupColumn("Pending",      ImGuiTableColumnFlags.None,       60f);
        ImGui.TableSetupColumn("Committed",    ImGuiTableColumnFlags.None,       70f);
        ImGui.TableSetupColumn("PxSpan",       ImGuiTableColumnFlags.None,       65f);
        ImGui.TableSetupColumn("Pages",        ImGuiTableColumnFlags.None,       70f);
        ImGui.TableSetupColumn("UV",           ImGuiTableColumnFlags.None,       45f);
        ImGui.TableSetupColumn("Distance",     ImGuiTableColumnFlags.None,       60f);
        ImGui.TableSetupColumn("Visibility",   ImGuiTableColumnFlags.None,       80f);
        ImGui.TableSetupColumn("Preview",      ImGuiTableColumnFlags.None,       55f);
        ImGui.TableHeadersRow();

        for (int i = 0; i < textures.Count; i++)
        {
            ImportedTextureStreamingTextureTelemetry tex = textures[i];
            bool visibleThisFrame = tex.LastVisibleFrameId == currentFrameId;
            long framesSinceVisible = tex.LastVisibleFrameId < 0
                ? long.MaxValue
                : Math.Max(0L, currentFrameId - tex.LastVisibleFrameId);

            ImGui.TableNextRow();

            // Name
            ImGui.TableSetColumnIndex(0);
            string displayName = !string.IsNullOrWhiteSpace(tex.TextureName) ? tex.TextureName! : "(unnamed)";
            ImGui.TextUnformatted(displayName);
            if (ImGui.IsItemHovered())
            {
                string tooltip = !string.IsNullOrWhiteSpace(tex.FilePath) ? tex.FilePath! : displayName;
                if (!string.IsNullOrWhiteSpace(tex.SamplerName))
                    tooltip = $"{tooltip}\nSampler: {tex.SamplerName}\nScreen: {tex.MaxScreenCoverage * 100.0f:F1}%";
                ImGui.SetTooltip(tooltip);
            }

            // Source size
            ImGui.TableSetColumnIndex(1);
            if (tex.SourceWidth == 0 && tex.SourceHeight == 0)
                ImGui.TextDisabled("?");
            else
                ImGui.Text($"{tex.SourceWidth}x{tex.SourceHeight}");

            // Resident size
            ImGui.TableSetColumnIndex(2);
            if (tex.ResidentMaxDimension == 0)
                ImGui.TextDisabled("none");
            else
                ImGui.Text($"{tex.ResidentMaxDimension}");

            // Desired size
            ImGui.TableSetColumnIndex(3);
            if (tex.DesiredResidentMaxDimension == 0)
                ImGui.TextDisabled("none");
            else if (tex.DesiredResidentMaxDimension > tex.ResidentMaxDimension)
                ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), $"{tex.DesiredResidentMaxDimension}");
            else if (tex.DesiredResidentMaxDimension < tex.ResidentMaxDimension)
                ImGui.TextColored(new Vector4(1.0f, 0.6f, 0.3f, 1.0f), $"{tex.DesiredResidentMaxDimension}");
            else
                ImGui.Text($"{tex.DesiredResidentMaxDimension}");

            // Pending size
            ImGui.TableSetColumnIndex(4);
            if (tex.PendingResidentMaxDimension == 0)
                ImGui.TextDisabled("-");
            else
                ImGui.TextColored(new Vector4(1.0f, 1.0f, 0.4f, 1.0f), $"{tex.PendingResidentMaxDimension}");

            // Committed bytes
            ImGui.TableSetColumnIndex(5);
            float committedKB = tex.CurrentCommittedBytes / 1024f;
            ImGui.Text(committedKB >= 1024f ? $"{committedKB / 1024f:F1} MB" : $"{committedKB:F0} KB");

            // Projected pixel span
            ImGui.TableSetColumnIndex(6);
            if (tex.MaxProjectedPixelSpan <= 0.0f)
                ImGui.TextDisabled("-");
            else
                ImGui.Text($"{tex.MaxProjectedPixelSpan:F0}");

            // Page coverage
            ImGui.TableSetColumnIndex(7);
            string pageText = $"{tex.CurrentPageCoverage * 100.0f:F0}%";
            if (MathF.Abs(tex.DesiredPageCoverage - tex.CurrentPageCoverage) > 0.01f)
                pageText = $"{pageText}->{tex.DesiredPageCoverage * 100.0f:F0}%";
            ImGui.Text(pageText);

            // UV density hint
            ImGui.TableSetColumnIndex(8);
            ImGui.Text($"{tex.UvDensityHint:F2}");

            // Distance
            ImGui.TableSetColumnIndex(9);
            if (!visibleThisFrame)
                ImGui.TextDisabled("-");
            else if (float.IsInfinity(tex.MinVisibleDistance))
                ImGui.TextDisabled("inf");
            else
                ImGui.Text($"{tex.MinVisibleDistance:F1}");

            // Visibility
            ImGui.TableSetColumnIndex(10);
            if (visibleThisFrame)
                ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "visible");
            else if (framesSinceVisible <= 12)
                ImGui.TextColored(new Vector4(1.0f, 1.0f, 0.5f, 1.0f), $"-{framesSinceVisible}f");
            else
                ImGui.TextDisabled("hidden");

            // Preview ready
            ImGui.TableSetColumnIndex(11);
            if (tex.PreviewReady)
                ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1.0f), "ready");
            else
                ImGui.TextColored(new Vector4(1.0f, 0.6f, 0.3f, 1.0f), "loading");
        }

        ImGui.EndTable();
    }
}
