using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Rendering;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static bool _showTextureStreaming;
    private static bool _textureStreamingFrozen;
    private static bool _textureStreamingFilterVisibleOnly;
    private static bool _textureStreamingFilterPendingOnly;
    private static bool _textureStreamingFilterSlowOnly;
    private static bool _textureStreamingFilterPressureOnly;
    private static bool _textureStreamingFilterValidationOnly;
    private static bool _textureStreamingSortDescending = true;
    private static TextureStreamingSortMode _textureStreamingSortMode = TextureStreamingSortMode.CommittedBytes;
    private static ImportedTextureStreamingTelemetry _frozenTelemetry;
    private static IReadOnlyList<ImportedTextureStreamingTextureTelemetry>? _frozenTextureTelemetry;

    private enum TextureStreamingSortMode
    {
        CommittedBytes,
        QueueWait,
        UploadTime,
        Priority,
    }

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
            if (ImGui.MenuItem("Dump Summary"))
                XRTexture2D.DumpImportedTextureStreamingSummary();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Write an immediate Texture.VramSummary event to log_textures.txt.");
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
        ImGui.Text($"Queued promote/demote: {t.QueuedPromotionTransitionsThisFrame}/{t.QueuedDemotionTransitionsThisFrame}");
        ImGui.Text($"Active decodes:     {t.ActiveDecodeCount}");
        ImGui.Text($"Queued decodes:     {t.QueuedDecodeCount}");
        ImGui.Text($"Active uploads:     {t.ActiveGpuUploadCount}");

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

        ImGui.Checkbox("Visible", ref _textureStreamingFilterVisibleOnly);
        ImGui.SameLine();
        ImGui.Checkbox("Pending", ref _textureStreamingFilterPendingOnly);
        ImGui.SameLine();
        ImGui.Checkbox("Slow", ref _textureStreamingFilterSlowOnly);
        ImGui.SameLine();
        ImGui.Checkbox("Pressure", ref _textureStreamingFilterPressureOnly);
        ImGui.SameLine();
        ImGui.Checkbox("Validation", ref _textureStreamingFilterValidationOnly);

        string sortLabel = _textureStreamingSortMode switch
        {
            TextureStreamingSortMode.QueueWait => "Queue wait",
            TextureStreamingSortMode.UploadTime => "Upload time",
            TextureStreamingSortMode.Priority => "Priority",
            _ => "Committed bytes",
        };
        ImGui.SetNextItemWidth(150f);
        if (ImGui.BeginCombo("Sort", sortLabel))
        {
            DrawTextureStreamingSortOption(TextureStreamingSortMode.CommittedBytes, "Committed bytes");
            DrawTextureStreamingSortOption(TextureStreamingSortMode.QueueWait, "Queue wait");
            DrawTextureStreamingSortOption(TextureStreamingSortMode.UploadTime, "Upload time");
            DrawTextureStreamingSortOption(TextureStreamingSortMode.Priority, "Priority");
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.Checkbox("Descending", ref _textureStreamingSortDescending);

        List<ImportedTextureStreamingTextureTelemetry> rows = new(textures.Count);
        for (int i = 0; i < textures.Count; i++)
        {
            ImportedTextureStreamingTextureTelemetry tex = textures[i];
            if (PassesTextureStreamingFilters(tex))
                rows.Add(tex);
        }
        rows.Sort(CompareTextureStreamingRows);

        ImGuiTableFlags flags =
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.Reorderable |
            ImGuiTableFlags.Hideable |
            ImGuiTableFlags.Sortable |
            ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.BordersOuter |
            ImGuiTableFlags.BordersV |
            ImGuiTableFlags.SizingStretchProp;

        float tableHeight = ImGui.GetContentRegionAvail().Y - ImGui.GetTextLineHeightWithSpacing();
        if (!ImGui.BeginTable("TextureStreamingTable", 16, flags, new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Name",         ImGuiTableColumnFlags.None,      180f);
        ImGui.TableSetupColumn("Backend",      ImGuiTableColumnFlags.None,       80f);
        ImGui.TableSetupColumn("Source",       ImGuiTableColumnFlags.None,       70f);
        ImGui.TableSetupColumn("Resident",     ImGuiTableColumnFlags.None,       65f);
        ImGui.TableSetupColumn("Desired",      ImGuiTableColumnFlags.None,       60f);
        ImGui.TableSetupColumn("Pending",      ImGuiTableColumnFlags.None,       60f);
        ImGui.TableSetupColumn("Committed",    ImGuiTableColumnFlags.None,       70f);
        ImGui.TableSetupColumn("Priority",     ImGuiTableColumnFlags.None,       65f);
        ImGui.TableSetupColumn("Queue",        ImGuiTableColumnFlags.None,       55f);
        ImGui.TableSetupColumn("Upload",       ImGuiTableColumnFlags.None,       55f);
        ImGui.TableSetupColumn("PxSpan",       ImGuiTableColumnFlags.None,       65f);
        ImGui.TableSetupColumn("Pages",        ImGuiTableColumnFlags.None,       70f);
        ImGui.TableSetupColumn("UV",           ImGuiTableColumnFlags.None,       45f);
        ImGui.TableSetupColumn("Distance",     ImGuiTableColumnFlags.None,       60f);
        ImGui.TableSetupColumn("Visibility",   ImGuiTableColumnFlags.None,       80f);
        ImGui.TableSetupColumn("Flags",        ImGuiTableColumnFlags.None,       95f);
        ImGui.TableHeadersRow();

        for (int i = 0; i < rows.Count; i++)
        {
            ImportedTextureStreamingTextureTelemetry tex = rows[i];
            bool visibleThisFrame = tex.IsVisible || tex.LastVisibleFrameId == currentFrameId;
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

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(tex.BackendName);

            ImGui.TableSetColumnIndex(2);
            if (tex.SourceWidth == 0 && tex.SourceHeight == 0)
                ImGui.TextDisabled("?");
            else
                ImGui.Text($"{tex.SourceWidth}x{tex.SourceHeight}");

            ImGui.TableSetColumnIndex(3);
            if (tex.ResidentMaxDimension == 0)
                ImGui.TextDisabled("none");
            else
                ImGui.Text($"{tex.ResidentMaxDimension}");

            ImGui.TableSetColumnIndex(4);
            if (tex.DesiredResidentMaxDimension == 0)
                ImGui.TextDisabled("none");
            else if (tex.DesiredResidentMaxDimension > tex.ResidentMaxDimension)
                ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), $"{tex.DesiredResidentMaxDimension}");
            else if (tex.DesiredResidentMaxDimension < tex.ResidentMaxDimension)
                ImGui.TextColored(new Vector4(1.0f, 0.6f, 0.3f, 1.0f), $"{tex.DesiredResidentMaxDimension}");
            else
                ImGui.Text($"{tex.DesiredResidentMaxDimension}");

            ImGui.TableSetColumnIndex(5);
            if (!tex.HasPendingTransition)
                ImGui.TextDisabled("-");
            else
                ImGui.TextColored(new Vector4(1.0f, 1.0f, 0.4f, 1.0f), $"{tex.PendingResidentMaxDimension}");

            ImGui.TableSetColumnIndex(6);
            DrawTextureStreamingBytes(tex.CurrentCommittedBytes);

            ImGui.TableSetColumnIndex(7);
            ImGui.Text($"{tex.PriorityScore:F0}");

            ImGui.TableSetColumnIndex(8);
            DrawTextureStreamingDuration(tex.OldestQueueWaitMilliseconds, tex.IsSlow);

            ImGui.TableSetColumnIndex(9);
            DrawTextureStreamingDuration(tex.LastUploadMilliseconds, tex.IsSlow);

            ImGui.TableSetColumnIndex(10);
            if (tex.MaxProjectedPixelSpan <= 0.0f)
                ImGui.TextDisabled("-");
            else
                ImGui.Text($"{tex.MaxProjectedPixelSpan:F0}");

            ImGui.TableSetColumnIndex(11);
            string pageText = $"{tex.CurrentPageCoverage * 100.0f:F0}%";
            if (MathF.Abs(tex.DesiredPageCoverage - tex.CurrentPageCoverage) > 0.01f)
                pageText = $"{pageText}->{tex.DesiredPageCoverage * 100.0f:F0}%";
            ImGui.Text(pageText);

            ImGui.TableSetColumnIndex(12);
            ImGui.Text($"{tex.UvDensityHint:F2}");

            ImGui.TableSetColumnIndex(13);
            if (!visibleThisFrame)
                ImGui.TextDisabled("-");
            else if (float.IsInfinity(tex.MinVisibleDistance))
                ImGui.TextDisabled("inf");
            else
                ImGui.Text($"{tex.MinVisibleDistance:F1}");

            ImGui.TableSetColumnIndex(14);
            if (visibleThisFrame)
                ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "visible");
            else if (framesSinceVisible <= 12)
                ImGui.TextColored(new Vector4(1.0f, 1.0f, 0.5f, 1.0f), $"-{framesSinceVisible}f");
            else
                ImGui.TextDisabled("hidden");

            ImGui.TableSetColumnIndex(15);
            string flagsText = BuildTextureStreamingFlags(tex);
            if (tex.HasValidationFailure)
                ImGui.TextColored(new Vector4(1.0f, 0.35f, 0.25f, 1.0f), flagsText);
            else if (tex.WasPressureDemoted)
                ImGui.TextColored(new Vector4(1.0f, 0.65f, 0.25f, 1.0f), flagsText);
            else if (!tex.PreviewReady)
                ImGui.TextColored(new Vector4(1.0f, 0.6f, 0.3f, 1.0f), flagsText);
            else
                ImGui.TextUnformatted(flagsText);
        }

        ImGui.EndTable();
    }

    private static void DrawTextureStreamingSortOption(TextureStreamingSortMode mode, string label)
    {
        bool selected = _textureStreamingSortMode == mode;
        if (ImGui.Selectable(label, selected))
            _textureStreamingSortMode = mode;
        if (selected)
            ImGui.SetItemDefaultFocus();
    }

    private static bool PassesTextureStreamingFilters(ImportedTextureStreamingTextureTelemetry tex)
    {
        if (_textureStreamingFilterVisibleOnly && !tex.IsVisible)
            return false;
        if (_textureStreamingFilterPendingOnly && !tex.HasPendingTransition)
            return false;
        if (_textureStreamingFilterSlowOnly && !tex.IsSlow)
            return false;
        if (_textureStreamingFilterPressureOnly && !tex.WasPressureDemoted)
            return false;
        if (_textureStreamingFilterValidationOnly && !tex.HasValidationFailure)
            return false;

        return true;
    }

    private static int CompareTextureStreamingRows(ImportedTextureStreamingTextureTelemetry left, ImportedTextureStreamingTextureTelemetry right)
    {
        int result = _textureStreamingSortMode switch
        {
            TextureStreamingSortMode.QueueWait => left.OldestQueueWaitMilliseconds.CompareTo(right.OldestQueueWaitMilliseconds),
            TextureStreamingSortMode.UploadTime => left.LastUploadMilliseconds.CompareTo(right.LastUploadMilliseconds),
            TextureStreamingSortMode.Priority => left.PriorityScore.CompareTo(right.PriorityScore),
            _ => left.CurrentCommittedBytes.CompareTo(right.CurrentCommittedBytes),
        };

        return _textureStreamingSortDescending ? -result : result;
    }

    private static void DrawTextureStreamingBytes(long bytes)
    {
        float committedKB = bytes / 1024f;
        ImGui.Text(committedKB >= 1024f ? $"{committedKB / 1024f:F1} MB" : $"{committedKB:F0} KB");
    }

    private static void DrawTextureStreamingDuration(double milliseconds, bool highlight)
    {
        if (milliseconds <= 0.0)
        {
            ImGui.TextDisabled("-");
            return;
        }

        string text = milliseconds >= 1000.0
            ? $"{milliseconds / 1000.0:F1}s"
            : $"{milliseconds:F1}ms";
        if (highlight)
            ImGui.TextColored(new Vector4(1.0f, 0.55f, 0.2f, 1.0f), text);
        else
            ImGui.Text(text);
    }

    private static string BuildTextureStreamingFlags(ImportedTextureStreamingTextureTelemetry tex)
    {
        string flags = tex.PreviewReady ? "ready" : "loading";
        if (tex.HasPendingTransition)
            flags += ",pending";
        if (tex.IsSlow)
            flags += ",slow";
        if (tex.WasPressureDemoted)
            flags += ",pressure";
        if (tex.HasValidationFailure)
            flags += $",invalid:{tex.ValidationFailureCount}";
        return flags;
    }
}
