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
using XREngine.Rendering.Vulkan;
using XREngine.Scene;
using XREngine.Rendering.UI;


namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    private static readonly Vector4 SendButtonColor = new(0.08f, 0.44f, 0.42f, 1f);
    private static readonly Vector4 SendButtonHoveredColor = new(0.10f, 0.53f, 0.50f, 1f);
    private static readonly Vector4 SendButtonActiveColor = new(0.06f, 0.36f, 0.35f, 1f);
    private string? _promptHeightText;
    private float _promptHeightWidth;
    private float _promptHeightFontSize;
    private float _promptHeight;

    /// <summary>Draws a message field with explicit context choices and a send action.</summary>
    private void DrawPromptBar()
    {
        DrawInteractionMode();
        DrawSelectedContext();
        float promptInputHeight = GetPromptInputHeight();

        // Give the editor a new widget ID for one frame so it reads the cleared value.
        bool clearingThisFrame = _pendingPromptClear;
        if (clearingThisFrame)
        {
            _prompt = string.Empty;
            _pendingPromptClear = false;
        }

        string promptBefore = _prompt;

        ImGui.InputTextMultiline(
            clearingThisFrame ? "##McpPromptClr" : _refreshHelpDraft ? "##McpHelpDraft" : "##McpPromptInput",
            ref _prompt,
            1024 * 128,
            new Vector2(-1f, promptInputHeight),
            ImGuiInputTextFlags.AllowTabInput);
        _refreshHelpDraft = false;
        bool inputActive = ImGui.IsItemActive();
        if (_prompt.Length == 0 && !ImGui.IsItemActive())
            ImGui.GetWindowDrawList().AddText(
                ImGui.GetItemRectMin() + new Vector2(9f, 7f),
                ImGui.GetColorU32(ImGuiCol.TextDisabled),
                "Ask a question or describe a change...");
        ImGuiTextFieldHelper.DrawTextFieldContextMenu("ctx_Prompt", ref _prompt);

        var io = ImGui.GetIO();
        bool shiftHeld = io.KeyShift
            || ImGui.IsKeyDown(ImGuiKey.ModShift)
            || ImGui.IsKeyDown(ImGuiKey.LeftShift)
            || ImGui.IsKeyDown(ImGuiKey.RightShift);

        bool enterSend = inputActive && !shiftHeld && !_isBusy
            && (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false));

        if (enterSend)
            _prompt = promptBefore;

        bool canSendNow = !_isBusy && !string.IsNullOrWhiteSpace(_prompt) && CanSendWithSelectedModel();
        DrawComposerActions(canSendNow, out bool sendClicked);
        if ((sendClicked || enterSend) && canSendNow)
            _ = SendPromptAsync();
    }

    private void DrawSelectedContext()
    {
        if (!AttachViewportScreenshot && !AttachMcpServer)
            return;

        if (_isBusy)
            ImGui.BeginDisabled();
        if (AttachViewportScreenshot)
        {
            if (ImGui.SmallButton("Viewport screenshot  x##RemoveScreenshot"))
                AttachViewportScreenshot = false;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Remove the viewport screenshot from the next message.");
        }
        if (AttachMcpServer)
        {
            if (AttachViewportScreenshot && !SelectedContextNeedsTwoRows())
                ImGui.SameLine();
            if (ImGui.SmallButton("Editor tools  x##RemoveSceneTools"))
                AttachMcpServer = false;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Remove editor scene tools from the next message.");
        }
        if (_isBusy)
            ImGui.EndDisabled();
    }

    /// <summary>Reserves space for context chips after they wrap in a narrow panel.</summary>
    private float GetSelectedContextHeight()
    {
        if (!AttachViewportScreenshot && !AttachMcpServer)
            return 0f;
        bool wrapped = AttachViewportScreenshot && AttachMcpServer && SelectedContextNeedsTwoRows();
        return ImGui.GetFrameHeightWithSpacing() * (wrapped ? 2f : 1f);
    }

    private static bool SelectedContextNeedsTwoRows()
    {
        float required = ImGui.CalcTextSize("Viewport screenshot  x").X
            + ImGui.CalcTextSize("Editor tools  x").X
            + ImGui.GetStyle().FramePadding.X * 4f + ImGui.GetStyle().ItemSpacing.X;
        return required > ImGui.GetContentRegionAvail().X;
    }

    private void DrawComposerActions(bool canSend, out bool sendClicked)
    {
        float rowStart = ImGui.GetCursorPosX();
        float rowWidth = ImGui.GetContentRegionAvail().X;
        if (ShowContinueAction)
        {
            if (ImGui.Button("Continue"))
            {
                if (_lastProviderIndexForContinue >= 0)
                    ProviderIndex = _lastProviderIndexForContinue;
                AttachMcpServer = _lastAttachMcpForContinue;
                _ = ContinuePromptAsync();
            }
        }
        if (_isBusy)
            ImGui.BeginDisabled();
        if (ImGui.Button("+ Add context"))
            ImGui.OpenPopup("##AssistantAddContext");
        if (ImGui.BeginPopup("##AssistantAddContext"))
        {
            if (ImGui.MenuItem("Viewport screenshot", null, AttachViewportScreenshot))
                AttachViewportScreenshot = !AttachViewportScreenshot;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Send a capture of the current editor viewport.");

            if (ImGui.MenuItem("Editor tools", null, AttachMcpServer))
                AttachMcpServer = !AttachMcpServer;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Let the assistant inspect or change the editor scene.");
            ImGui.EndPopup();
        }
        if (_isBusy)
            ImGui.EndDisabled();

        const float actionWidth = 88f;
        if (!ComposerActionsNeedTwoRows())
            ImGui.SameLine(rowStart + Math.Max(0f, rowWidth - actionWidth));
        if (_isBusy)
        {
            if (ImGui.Button("Stop", new Vector2(actionWidth, 0f)))
                _cts?.Cancel();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Stop this reply. Actions that already completed remain applied.");
            sendClicked = false;
            return;
        }

        if (!canSend)
            ImGui.BeginDisabled();
        ImGui.PushStyleColor(ImGuiCol.Button, SendButtonColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, SendButtonHoveredColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, SendButtonActiveColor);
        sendClicked = ImGui.Button("Send", new Vector2(actionWidth, 0f));
        ImGui.PopStyleColor(3);
        if (!canSend)
            ImGui.EndDisabled();
    }

    private bool ShowContinueAction => !_isBusy && _canContinueAfterRepromptLimit
        && !string.IsNullOrWhiteSpace(_lastPromptForContinue) && CanSendWithSelectedModel();

    private static bool ComposerActionsNeedTwoRows()
        => ImGui.CalcTextSize("+ Add context").X + ImGui.GetStyle().FramePadding.X * 2f
            + ImGui.GetStyle().ItemSpacing.X + 88f > ImGui.GetContentRegionAvail().X;

    private float GetComposerActionsHeight()
        => ImGui.GetFrameHeightWithSpacing() * ((ShowContinueAction ? 1f : 0f)
            + (ComposerActionsNeedTwoRows() ? 2f : 1f));

    private float GetPromptInputHeight()
    {
        float width = Math.Max(120f, ImGui.GetContentRegionAvail().X - 8f);
        float fontSize = ImGui.GetFontSize();
        if (ReferenceEquals(_promptHeightText, _prompt)
            && width == _promptHeightWidth
            && fontSize == _promptHeightFontSize)
            return _promptHeight;

        int wrappedLines = EstimateWrappedLineCount(_prompt, width);
        _promptHeightText = _prompt;
        _promptHeightWidth = width;
        _promptHeightFontSize = fontSize;
        _promptHeight = Math.Clamp(wrappedLines, 3, 8) * ImGui.GetTextLineHeightWithSpacing() + 10f;
        return _promptHeight;
    }

    private static int EstimateWrappedLineCount(string text, float maxWidth)
    {
        if (string.IsNullOrEmpty(text))
            return 1;

        float glyphWidth = Math.Max(1f, ImGui.CalcTextSize("M").X);
        int charactersPerLine = Math.Max(8, (int)(maxWidth / glyphWidth));
        int totalLines = 0;
        int lineLength = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                lineLength++;
                continue;
            }

            totalLines += Math.Max(1, (lineLength + charactersPerLine - 1) / charactersPerLine);
            lineLength = 0;
        }
        return totalLines + Math.Max(1, (lineLength + charactersPerLine - 1) / charactersPerLine);
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
