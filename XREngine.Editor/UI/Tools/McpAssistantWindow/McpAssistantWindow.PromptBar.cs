using ImGuiNET;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Editor.Mcp;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;
using XREngine.Rendering.UI;


namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    // ── Prompt Bar ───────────────────────────────────────────────────────

    private void DrawPromptBar()
    {
        // Button row
        bool canSend = !_isBusy && _prompt.Trim().Length > 0;

        if (!canSend)
            ImGui.BeginDisabled();
        bool sendClicked = ImGui.Button("Send (Enter)");
        if (!canSend)
            ImGui.EndDisabled();

        if (!_isBusy && _canContinueAfterRepromptLimit && !string.IsNullOrWhiteSpace(_lastPromptForContinue))
        {
            ImGui.SameLine();
            if (ImGui.Button("Continue (Refresh Auto Re-prompts)"))
            {
                if (_lastProviderIndexForContinue >= 0)
                    ProviderIndex = _lastProviderIndexForContinue;
                AttachMcpServer = _lastAttachMcpForContinue;
                _ = ContinuePromptAsync();
            }
        }

        ImGui.SameLine();

        if (_isBusy)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.2f, 0.2f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.3f, 0.3f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.9f, 0.1f, 0.1f, 1f));
            if (ImGui.Button("Cancel"))
                _cts?.Cancel();
            ImGui.PopStyleColor(3);
        }

        // Char count
        ImGui.SameLine();
        ImGui.TextColored(ColorTimestamp, $"{_prompt.Length:N0} chars");

        // Screenshot toggle
        ImGui.SameLine();
        bool attachScreenshot = AttachViewportScreenshot;
        if (ImGui.Checkbox("Screenshot", ref attachScreenshot))
            AttachViewportScreenshot = attachScreenshot;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Capture and attach the current editor viewport\nscreenshot as visual context with your next message.");

        // Right-aligned copy button
        if (_history.Count > 0)
        {
            float copyWidth = ImGui.CalcTextSize("Copy Last").X + 16f;
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - copyWidth + ImGui.GetCursorPosX());
            if (ImGui.SmallButton("Copy Last"))
                CopyLastResponse();
        }

        // Prompt input (fills width and grows with content)
        float promptInputHeight = GetPromptInputHeight();

        // Force-clear ImGui's internal text buffer after a send.
        // When the InputTextMultiline widget is active (focused), ImGui maintains
        // its own internal buffer and ignores external changes to the ref parameter.
        // To work around this, we render the widget with a temporary ID for one frame.
        // ImGui sees a brand-new widget and initializes it from the empty _prompt.
        // Next frame the normal ID is used again and picks up the cleared value.
        bool clearingThisFrame = _pendingPromptClear;
        if (clearingThisFrame)
        {
            _prompt = string.Empty;
            _pendingPromptClear = false;
        }

        // Snapshot the prompt before the widget so we can detect newline insertion.
        string promptBefore = _prompt;

        ImGui.InputTextMultiline(
            clearingThisFrame ? "##McpPromptClr" : "##McpPromptInput",
            ref _prompt,
            1024 * 128,
            new Vector2(-1f, promptInputHeight),
            ImGuiInputTextFlags.AllowTabInput);
        ImGuiTextFieldHelper.DrawTextFieldContextMenu("ctx_Prompt", ref _prompt);

        // Detect Enter: the multiline widget inserts '\n' for every Enter press.
        // Compare the prompt before/after to detect a newline was just typed.
        bool newlineJustInserted = _prompt.Length > promptBefore.Length
            && _prompt.Length > 0
            && _prompt[^1] == '\n';

        // Shift detection — try every method since backends vary.
        var io = ImGui.GetIO();
        bool shiftHeld = io.KeyShift
            || ImGui.IsKeyDown(ImGuiKey.ModShift)
            || ImGui.IsKeyDown(ImGuiKey.LeftShift)
            || ImGui.IsKeyDown(ImGuiKey.RightShift);

        bool enterSend = newlineJustInserted && !shiftHeld;

        if (enterSend)
            StripOneTrailingNewline(ref _prompt);

        bool canSendNow = !_isBusy && _prompt.Trim().Length > 0;
        if ((sendClicked || enterSend) && canSendNow)
            _ = SendPromptAsync();
    }

    private float GetPromptInputHeight()
    {
        float width = Math.Max(120f, ImGui.GetContentRegionAvail().X - 8f);
        int explicitLines = 1;
        for (int i = 0; i < _prompt.Length; i++)
        {
            if (_prompt[i] == '\n')
                explicitLines++;
        }

        int wrappedLines = EstimateWrappedLineCount(_prompt, width);
        int lines = Math.Clamp(Math.Max(explicitLines, wrappedLines), 2, 12);
        return lines * ImGui.GetTextLineHeightWithSpacing() + 10f;
    }

    private static int EstimateWrappedLineCount(string text, float maxWidth)
    {
        if (string.IsNullOrEmpty(text))
            return 1;

        int totalLines = 0;
        string[] rawLines = text.Split('\n');
        for (int i = 0; i < rawLines.Length; i++)
        {
            string line = rawLines[i];
            float lineWidth = ImGui.CalcTextSize(string.IsNullOrEmpty(line) ? " " : line).X;
            int wraps = Math.Max(1, (int)Math.Ceiling(lineWidth / Math.Max(1.0f, maxWidth)));
            totalLines += wraps;
        }

        return Math.Max(1, totalLines);
    }

    private static void StripOneTrailingNewline(ref string text)
    {
        if (text.EndsWith("\r\n", StringComparison.Ordinal))
        {
            text = text[..^2];
            return;
        }

        if (text.EndsWith("\n", StringComparison.Ordinal) || text.EndsWith("\r", StringComparison.Ordinal))
            text = text[..^1];
    }

    // ── Clipboard Helpers ────────────────────────────────────────────────

    private void CopyLastResponse()
    {
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_history[i].Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                ImGui.SetClipboardText(_history[i].Content);
                SetStatus("Copied.", ColorDone);
                return;
            }
        }
    }

    private void CopyFullHistory()
    {
        var sb = new StringBuilder();
        foreach (ChatMessage msg in _history)
        {
            sb.AppendLine($"[{msg.Timestamp:yyyy-MM-dd HH:mm:ss}] {msg.Role}:");
            sb.AppendLine(msg.Content);
            sb.AppendLine();
        }

        ImGui.SetClipboardText(sb.ToString());
        SetStatus("History copied.", ColorDone);
    }
}
