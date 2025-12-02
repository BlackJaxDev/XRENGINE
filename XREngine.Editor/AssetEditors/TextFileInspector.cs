using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using ImGuiNET;
using XREngine;
using XREngine.Core.Files;

namespace XREngine.Editor.AssetEditors;

public sealed class TextFileInspector : IXRAssetInspector
{
    private const int MinInitialCapacity = 4 * 1024;
    private const int MaxInitialCapacity = 8 * 1024 * 1024;

    private static readonly (string Label, Encoding Encoding)[] s_commonEncodings =
    {
        ("UTF-8", Encoding.UTF8),
        ("Unicode (UTF-16 LE)", Encoding.Unicode),
        ("UTF-16 BE", Encoding.BigEndianUnicode),
        ("UTF-32", Encoding.UTF32),
        ("ASCII", Encoding.ASCII)
    };

    private readonly ConditionalWeakTable<TextFile, EditorState> _stateCache = new();

    public void DrawInspector(XRAsset asset, HashSet<object> visitedObjects)
    {
        if (asset is not TextFile textFile)
        {
            UnitTestingWorld.UserInterface.DrawDefaultAssetInspector(asset, visitedObjects);
            return;
        }

        var state = _stateCache.GetValue(textFile, _ => new EditorState());
        SyncState(textFile, state);

        DrawHeader(textFile);
        DrawStatus(state);
        DrawToolbar(textFile, state);

        ImGui.Separator();
        DrawEncodingControls(textFile, state);

        ImGui.Separator();
        DrawTextEditor(textFile, state);

        ImGui.Separator();
        DrawDefaultInspector(textFile, visitedObjects);
    }

    private static void DrawHeader(TextFile textFile)
    {
        string displayName = !string.IsNullOrWhiteSpace(textFile.Name)
            ? textFile.Name!
            : (textFile.FilePath is not null ? Path.GetFileName(textFile.FilePath) : "Text File");

        ImGui.TextUnformatted(displayName);
        string path = textFile.FilePath ?? "<unsaved asset>";
        ImGui.TextDisabled(path);
    }

    private static void DrawStatus(EditorState state)
    {
        if (string.IsNullOrEmpty(state.StatusMessage))
            return;

        Vector4 color = state.StatusKind == StatusKind.Error
            ? new Vector4(0.95f, 0.45f, 0.45f, 1f)
            : new Vector4(0.5f, 0.8f, 0.5f, 1f);

        ImGui.TextColored(color, state.StatusMessage);
    }

    private static void DrawToolbar(TextFile textFile, EditorState state)
    {
        bool hasPath = !string.IsNullOrWhiteSpace(textFile.FilePath);

        using (new ImGuiDisabledScope(!hasPath))
        {
            if (ImGui.Button("Reload") && hasPath)
                TryReload(textFile, state);
        }

        ImGui.SameLine();

        using (new ImGuiDisabledScope(!hasPath))
        {
            if (ImGui.Button("Save") && hasPath)
                TrySave(textFile, state);
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy Path") && hasPath)
        {
            ImGui.SetClipboardText(textFile.FilePath!);
            SetStatus(state, "Path copied to clipboard.", StatusKind.Info);
        }

        ImGui.SameLine();
        ImGui.TextDisabled(textFile.IsDirty ? "Dirty" : "Saved");
    }

    private static void DrawEncodingControls(TextFile textFile, EditorState state)
    {
        string label = $"{textFile.Encoding.EncodingName} ({textFile.Encoding.CodePage})";
        ImGui.TextUnformatted("Encoding:");
        ImGui.SameLine();
        ImGui.TextDisabled(label);

        ImGui.SameLine();
        if (ImGui.BeginCombo("##TextFileEncodingPreset", "Presets"))
        {
            foreach (var preset in s_commonEncodings)
            {
                bool selected = preset.Encoding.CodePage == textFile.Encoding.CodePage;
                string presetLabel = $"{preset.Label} ({preset.Encoding.CodePage})";
                if (ImGui.Selectable(presetLabel, selected) && !selected)
                {
                    textFile.Encoding = preset.Encoding;
                    SetStatus(state, $"Encoding set to {preset.Label}.", StatusKind.Info);
                }
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        int codePage = textFile.Encoding.CodePage;
        ImGui.SetNextItemWidth(110f);
        if (ImGui.InputInt("Code Page", ref codePage))
            TryApplyCodePage(textFile, state, codePage);

        if (!string.IsNullOrEmpty(state.EncodingError))
        {
            Vector4 warning = new(0.95f, 0.65f, 0.35f, 1f);
            ImGui.TextColored(warning, state.EncodingError);
        }
    }

    private static void DrawTextEditor(TextFile textFile, EditorState state)
    {
        Vector2 size = new(-1f, Math.Max(220f, ImGui.GetTextLineHeightWithSpacing() * 18f));
        uint capacity = CalculateInitialCapacity(state.Buffer);
        if (ImGui.InputTextMultiline("##TextFileContent", ref state.Buffer, capacity, size, ImGuiInputTextFlags.AllowTabInput))
        {
            ApplyBuffer(textFile, state);
        }

        var metrics = GetMetrics(state.Buffer);
        int byteCount = textFile.Encoding.GetByteCount(state.Buffer);
        ImGui.TextDisabled($"Lines: {metrics.LineCount}    Characters: {metrics.CharacterCount}    Bytes: {byteCount}");
    }

    private static void DrawDefaultInspector(TextFile textFile, HashSet<object> visited)
    {
        if (!ImGui.CollapsingHeader("Additional Properties", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.PushID("TextFileDefaultInspector");
        UnitTestingWorld.UserInterface.DrawDefaultAssetInspector(textFile, visited);
        ImGui.PopID();
    }

    private static void SyncState(TextFile textFile, EditorState state, bool force = false)
    {
        string? assetText = textFile.Text;
        if (!force && ReferenceEquals(state.LastSyncedText, assetText))
            return;

        state.Buffer = assetText ?? string.Empty;
        state.LastSyncedText = assetText;
    }

    private static void ApplyBuffer(TextFile textFile, EditorState state)
    {
        string nextValue = state.Buffer ?? string.Empty;
        textFile.Text = nextValue;
        state.LastSyncedText = textFile.Text;
    }

    private static void TryReload(TextFile textFile, EditorState state)
    {
        try
        {
            if (textFile.FilePath is null)
                return;

            textFile.Reload();
            SyncState(textFile, state, force: true);
            SetStatus(state, "Reloaded from disk.", StatusKind.Info);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, $"Failed to reload '{textFile.FilePath}'.");
            SetStatus(state, $"Reload failed: {ex.Message}", StatusKind.Error);
        }
    }

    private static void TrySave(TextFile textFile, EditorState state)
    {
        try
        {
            if (textFile.FilePath is null)
                return;

            textFile.SaveTo(textFile.FilePath);
            textFile.ClearDirty();
            SetStatus(state, $"Saved to {Path.GetFileName(textFile.FilePath)}.", StatusKind.Info);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, $"Failed to save '{textFile.FilePath}'.");
            SetStatus(state, $"Save failed: {ex.Message}", StatusKind.Error);
        }
    }

    private static void TryApplyCodePage(TextFile textFile, EditorState state, int codePage)
    {
        try
        {
            textFile.Encoding = Encoding.GetEncoding(codePage);
            state.EncodingError = null;
            SetStatus(state, $"Encoding changed to {textFile.Encoding.EncodingName}.", StatusKind.Info);
        }
        catch (Exception ex)
        {
            state.EncodingError = ex.Message;
            SetStatus(state, $"Encoding change failed: {ex.Message}", StatusKind.Error);
        }
    }

    private static void SetStatus(EditorState state, string message, StatusKind kind)
    {
        state.StatusMessage = message;
        state.StatusKind = kind;
    }

    private static uint CalculateInitialCapacity(string text)
    {
        int desired = text.Length + 1024;
        desired = Math.Clamp(desired, MinInitialCapacity, MaxInitialCapacity);
        return (uint)desired;
    }

    private static (int LineCount, int CharacterCount) GetMetrics(string text)
    {
        if (string.IsNullOrEmpty(text))
            return (0, 0);

        int lines = 1;
        foreach (char c in text)
        {
            if (c == '\n')
                lines++;
        }

        return (lines, text.Length);
    }

    private sealed class EditorState
    {
        public string Buffer = string.Empty;
        public string? LastSyncedText;
        public string? StatusMessage;
        public StatusKind StatusKind = StatusKind.Info;
        public string? EncodingError;
    }

    private enum StatusKind
    {
        Info,
        Error
    }

    private readonly struct ImGuiDisabledScope : IDisposable
    {
        private readonly bool _isDisabled;

        public ImGuiDisabledScope(bool disabled)
        {
            _isDisabled = disabled;
            if (disabled)
                ImGui.BeginDisabled();
        }

        public void Dispose()
        {
            if (_isDisabled)
                ImGui.EndDisabled();
        }
    }
}
