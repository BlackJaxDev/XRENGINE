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
using XREngine.Editor.Services;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;
using XREngine.Rendering.UI;


namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    // ── Chat Log ─────────────────────────────────────────────────────────

    private void DrawChatLog()
    {
        if (_history.Count < _lastChatHistoryCount)
            _chatRowHeightCache.Clear();
        _lastChatHistoryCount = _history.Count;

        // Reserve space for the prompt bar at the bottom.
        float promptBarHeight = GetPromptInputHeight() + 44f;
        float available = ImGui.GetContentRegionAvail().Y - promptBarHeight;
        if (available < 80f)
            available = 80f;

        ImGui.PushStyleColor(ImGuiCol.ChildBg, ColorChatBg);
        ImGui.BeginChild("##ChatLog", new Vector2(-1f, available), ImGuiChildFlags.Border);

        if (_history.Count == 0)
        {
            // Keep the placeholder pinned and high-contrast so it is always visible.
            ImGui.SetCursorPos(new Vector2(12f, 12f));
            ImGui.TextColored(ColorPlaceholder, "Send a prompt to begin a conversation.");
        }
        else
        {
            ImGui.Spacing();
            DrawVirtualizedChatRows();
        }

        // Auto-scroll when streaming or when a new message was just added.
        if (_scrollToBottom || (AutoScroll && _isBusy))
        {
            ImGui.Dummy(new Vector2(1.0f, 0.0f));
            ImGui.SetScrollHereY(1.0f);
            _scrollToBottom = false;
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private void DrawVirtualizedChatRows()
    {
        const float overscanLines = 8.0f;

        int messageCount = _history.Count;
        float lineHeight = ImGui.GetTextLineHeightWithSpacing();
        float overscanHeight = lineHeight * overscanLines;
        float visibleMin = Math.Max(0.0f, ImGui.GetScrollY() - overscanHeight);
        float visibleMax = ImGui.GetScrollY() + ImGui.GetWindowHeight() + overscanHeight;

        float topSpacerHeight = 0.0f;
        int startIndex = 0;
        for (; startIndex < messageCount; startIndex++)
        {
            float rowHeight = GetEstimatedChatRowHeight(_history[startIndex], lineHeight);
            if (topSpacerHeight + rowHeight >= visibleMin)
                break;

            topSpacerHeight += rowHeight;
        }

        float accumulatedHeight = topSpacerHeight;
        int endIndex = startIndex;
        for (; endIndex < messageCount; endIndex++)
        {
            float rowHeight = GetEstimatedChatRowHeight(_history[endIndex], lineHeight);
            if (accumulatedHeight > visibleMax && endIndex > startIndex)
                break;

            accumulatedHeight += rowHeight;
        }

        float bottomSpacerHeight = 0.0f;
        for (int i = endIndex; i < messageCount; i++)
            bottomSpacerHeight += GetEstimatedChatRowHeight(_history[i], lineHeight);

        if (topSpacerHeight > 0.0f)
            ImGui.Dummy(new Vector2(1.0f, topSpacerHeight));

        for (int i = startIndex; i < endIndex; i++)
        {
            ChatMessage msg = _history[i];
            float rowStart = ImGui.GetCursorPosY();
            DrawChatMessageRow(msg, i, messageCount);
            float measuredHeight = Math.Max(ImGui.GetCursorPosY() - rowStart, lineHeight * 3.0f);
            _chatRowHeightCache[msg] = measuredHeight;
        }

        if (bottomSpacerHeight > 0.0f)
            ImGui.Dummy(new Vector2(1.0f, bottomSpacerHeight));
    }

    private float GetEstimatedChatRowHeight(ChatMessage message, float lineHeight)
    {
        if (_chatRowHeightCache.TryGetValue(message, out float cachedHeight))
            return cachedHeight;

        int toolCallCount;
        lock (message.ToolCallsSyncRoot)
            toolCallCount = message.ToolCalls.Count;

        int estimatedLines = Math.Max(1, Math.Min(64, (message.Content.Length / 84) + 1));
        float estimatedHeight = (estimatedLines + 4.0f) * lineHeight;
        if (!string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            estimatedHeight += toolCallCount * lineHeight * 2.5f;

        return Math.Max(estimatedHeight, lineHeight * 5.0f);
    }

    private void DrawChatMessageRow(ChatMessage msg, int index, int messageCount)
    {
        bool isUser = string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase);

        if (isUser)
            DrawUserMessage(msg);
        else
            DrawAssistantMessage(msg, index);

        if (index >= messageCount - 1)
            return;

        ImGui.Spacing();
        var dl = ImGui.GetWindowDrawList();
        Vector2 p = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        dl.AddLine(p, p + new Vector2(w, 0), ImGui.ColorConvertFloat4ToU32(ColorSeparator));
        ImGui.Spacing();
        ImGui.Spacing();
    }

    // ── Copilot-Style Message Rendering ──────────────────────────────────

    private static void DrawUserMessage(ChatMessage msg)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        Vector2 startPos = ImGui.GetCursorScreenPos();
        float startY = ImGui.GetCursorPosY();

        ImGui.Indent(8f);

        // Header: "You" + timestamp.
        ImGui.TextColored(ColorUser, "You");
        ImGui.SameLine();
        ImGui.TextColored(ColorTimestamp, msg.Timestamp.ToString("HH:mm:ss"));

        ImGui.Spacing();

        // User message body.
        ImGui.PushTextWrapPos(0.0f);
        ImGui.TextUnformatted(msg.Content);
        ImGui.PopTextWrapPos();

        ImGui.Unindent(8f);
        ImGui.Spacing();

        // Draw subtle background bubble behind the rendered content.
        float endY = ImGui.GetCursorPosY();
        float height = endY - startY;
        float width = ImGui.GetContentRegionAvail().X + 16f;
        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(
            startPos - new Vector2(4f, 2f),
            startPos + new Vector2(width, height - 2f),
            ImGui.ColorConvertFloat4ToU32(ColorUserBubble),
            6f);

        drawList.ChannelsMerge();
    }

    private void DrawAssistantMessage(ChatMessage msg, int index)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        Vector2 startPos = ImGui.GetCursorScreenPos();
        float startY = ImGui.GetCursorPosY();

        ImGui.Indent(8f);

        // Header: "Assistant" + timestamp + streaming indicator.
        ImGui.TextColored(ColorAssistant, "Assistant");
        ImGui.SameLine();
        ImGui.TextColored(ColorTimestamp, msg.Timestamp.ToString("HH:mm:ss"));

        if (msg.IsStreaming)
        {
            ImGui.SameLine();
            int dots = ((int)(ImGui.GetTime() * 3.0)) % 4;
            ImGui.TextColored(ColorBusy, "Working" + new string('.', dots + 1));
        }

        ImGui.Spacing();

        // Render segments in chronological order when available.
        ContentSegment[] segments = SnapshotSegments(msg);
        if (segments.Length > 0)
        {
            for (int s = 0; s < segments.Length; s++)
            {
                var seg = segments[s];
                if (seg.Kind == ContentSegment.SegmentKind.Text)
                {
                    string text = NormalizeForUiDisplay(GetDisplayContent(seg.Text));
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        RenderRichContent(text);
                        ImGui.Spacing();
                        ImGui.Spacing();
                    }
                }
                else if (seg.Kind == ContentSegment.SegmentKind.ToolCallGroup)
                {
                    ToolCallEntry[] groupCalls;
                    lock (msg.SegmentsSyncRoot)
                        groupCalls = [.. seg.ToolCalls];

                    if (groupCalls.Length > 0)
                    {
                        int groupUiId = seg.UiId != 0 ? seg.UiId : (index * 100 + s);
                        DrawToolCallsSection(groupCalls, groupUiId);
                        ImGui.Spacing();
                    }
                }
            }
        }
        else
        {
            // Legacy fallback: text then tool calls.
            string displayContent = NormalizeForUiDisplay(GetDisplayContent(msg.Content));
            if (!string.IsNullOrWhiteSpace(displayContent))
                RenderRichContent(displayContent);

            ToolCallEntry[] toolCallsSnapshot = SnapshotToolCalls(msg);
            if (toolCallsSnapshot.Length > 0)
                DrawToolCallsSection(toolCallsSnapshot, index);
        }

        ImGui.Unindent(8f);
        ImGui.Spacing();
        ImGui.Spacing();

        // Draw subtle background bubble.
        float endY = ImGui.GetCursorPosY();
        float height = endY - startY;
        float width = ImGui.GetContentRegionAvail().X + 16f;
        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(
            startPos - new Vector2(4f, 2f),
            startPos + new Vector2(width, height - 2f),
            ImGui.ColorConvertFloat4ToU32(ColorAssistantBubble),
            6f);

        drawList.ChannelsMerge();
    }
    /// <summary>
    /// Renders the collapsible tool calls section, styled like Copilot Chat's
    /// expandable "Used N references" / "Working..." blocks.
    /// </summary>
    private void DrawToolCallsSection(IReadOnlyList<ToolCallEntry> toolCalls, int messageIndex)
    {
        int total = toolCalls.Count;
        int completed = 0;
        int errors = 0;

        for (int i = 0; i < total; i++)
        {
            ToolCallEntry call = toolCalls[i];
            if (call.IsComplete)
                completed++;
            if (call.IsError)
                errors++;
        }

        bool allDone = completed == total;

        // Section header with count badge + status icon.
        string statusIcon = allDone ? (errors > 0 ? "!" : "\u2714") : "\u2026"; // ✔ or … or !
        string headerLabel = allDone
            ? $"{statusIcon} {total} tool call{(total != 1 ? "s" : "")} completed"
            : $"{statusIcon} Running {total - completed} of {total} tool call{(total != 1 ? "s" : "")}...";

        if (errors > 0)
            headerLabel += $" ({errors} failed)";

        ImGui.PushStyleColor(ImGuiCol.Header, ColorToolSection);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, ColorToolSectionHover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, ColorToolSectionHover);

        bool open = ImGui.TreeNodeEx($"{headerLabel}##tools_{messageIndex}",
            ImGuiTreeNodeFlags.SpanAvailWidth | (allDone ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen));

        ImGui.PopStyleColor(3);

        if (!open)
            return;

        for (int i = 0; i < total; i++)
        {
            ToolCallEntry call = toolCalls[i];

            // Status icon with visual indicators.
            if (!call.IsComplete)
            {
                int dots = ((int)(ImGui.GetTime() * 2.5)) % 4;
                string spinner = dots switch { 0 => "|", 1 => "/", 2 => "-", _ => "\\" };
                ImGui.TextColored(ColorBusy, spinner);
            }
            else if (call.IsError)
                ImGui.TextColored(ColorError, "x");
            else
                ImGui.TextColored(ColorDone, "\u2713"); // ✓

            ImGui.SameLine();

            // Tool name in a distinct color.
            ImGui.TextColored(new Vector4(0.65f, 0.50f, 0.90f, 1f), call.ToolName);

            // Build a concise one-line description.
            if (call.IsComplete && !string.IsNullOrEmpty(call.ResultSummary))
            {
                ImGui.SameLine();
                var resultColor = call.IsError ? ColorError : new Vector4(0.55f, 0.78f, 0.55f, 1f);
                bool resultFirst = false;
                RenderColoredTextWithGuidLinks($"  {call.ResultSummary}", resultColor, ref resultFirst);
            }
            else if (!string.IsNullOrEmpty(call.ArgsSummary))
            {
                ImGui.SameLine();
                bool argsFirst = false;
                RenderColoredTextWithGuidLinks($"  ({call.ArgsSummary})", ColorTimestamp, ref argsFirst);
            }

            // File path link + image preview for tool results.
            if (!string.IsNullOrEmpty(call.ResultFilePath))
            {
                DrawFilePathLink(call.ResultFilePath, messageIndex, i);

                // Image preview thumbnail.
                if (IsImageFilePath(call.ResultFilePath) && File.Exists(call.ResultFilePath))
                    DrawImagePreview(call.ResultFilePath, messageIndex, i);
            }
        }

        ImGui.TreePop();
        ImGui.Spacing();
    }

    /// <summary>
    /// Renders a clickable file path link that opens the file location in Explorer,
    /// plus an "Open" button that launches the file with the default application.
    /// </summary>
    private static void DrawFilePathLink(string filePath, int messageIndex, int callIndex)
    {
        string fileName = Path.GetFileName(filePath);
        ImGui.Indent(20f);

        // Clickable file name link.
        ImGui.PushStyleColor(ImGuiCol.Text, ColorFilePath);
        bool clicked = ImGui.Selectable($"{fileName}##fp_{messageIndex}_{callIndex}", false,
            ImGuiSelectableFlags.None, new Vector2(ImGui.CalcTextSize(fileName).X + 4f, 0));
        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColorFilePathHover);
            ImGui.SetTooltip(filePath);
            ImGui.PopStyleColor();
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        if (clicked && File.Exists(filePath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
                {
                    UseShellExecute = true
                });
            }
            catch { /* Non-critical */ }
        }

        // "Open" button to launch with default application.
        ImGui.SameLine();
        if (ImGui.SmallButton($"Open##open_{messageIndex}_{callIndex}") && File.Exists(filePath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(filePath)
                {
                    UseShellExecute = true
                });
            }
            catch { /* Non-critical */ }
        }

        ImGui.Unindent(20f);
    }

    /// <summary>
    /// Renders a collapsible image thumbnail preview for a tool result image.
    /// Uses a cached XRTexture2D loaded from the file path.
    /// </summary>
    private void DrawImagePreview(string imagePath, int messageIndex, int callIndex)
    {
        ImGui.Indent(20f);

        bool previewOpen = ImGui.TreeNodeEx($"Preview##imgpreview_{messageIndex}_{callIndex}",
            ImGuiTreeNodeFlags.None);

        if (previewOpen)
        {
            // Lazy-load and cache the texture.
            if (!_imagePreviewCache.TryGetValue(imagePath, out XRTexture2D? texture))
            {
                try
                {
                    texture = new XRTexture2D(imagePath);
                    _imagePreviewCache[imagePath] = texture;
                }
                catch
                {
                    _imagePreviewCache[imagePath] = null;
                    texture = null;
                }
            }

            if (texture is not null)
            {
                const float maxPreviewEdge = 256f;
                nint handle = nint.Zero;

                if (Engine.IsRenderThread)
                    EditorTexturePreviewService.TryGetHandle(
                        texture,
                        out handle,
                        out _,
                        out _);

                if (handle != nint.Zero)
                {
                    float w = Math.Max(texture.Width, 1f);
                    float h = Math.Max(texture.Height, 1f);
                    float scale = Math.Min(maxPreviewEdge / w, maxPreviewEdge / h);
                    if (scale > 1f) scale = 1f;
                    var displaySize = new Vector2(w * scale, h * scale);

                    ImGui.Image(handle, displaySize);
                    ImGui.TextDisabled($"{(int)w} x {(int)h}");
                }
                else
                {
                    ImGui.TextDisabled("(preview loading...)");
                }
            }
            else
            {
                ImGui.TextDisabled("(preview unavailable)");
            }

            ImGui.TreePop();
        }

        ImGui.Unindent(20f);
    }

    private static ToolCallEntry[] SnapshotToolCalls(ChatMessage message)
    {
        lock (message.ToolCallsSyncRoot)
            return [.. message.ToolCalls];
    }

    private static void AddToolCall(ChatMessage message, ToolCallEntry entry)
    {
        lock (message.ToolCallsSyncRoot)
            message.ToolCalls.Add(entry);
    }

    /// <summary>
    /// Snapshots the segment list for UI rendering.
    /// </summary>
    private static ContentSegment[] SnapshotSegments(ChatMessage message)
    {
        lock (message.SegmentsSyncRoot)
            return [.. message.Segments];
    }

    /// <summary>
    /// Gets the current text segment or creates one. A new text segment is started
    /// after a tool-call-group segment so text and tool calls alternate.
    /// </summary>
    private static ContentSegment GetOrCreateTextSegment(ChatMessage message)
    {
        lock (message.SegmentsSyncRoot)
        {
            if (message.Segments.Count > 0 && message.Segments[^1].Kind == ContentSegment.SegmentKind.Text)
                return message.Segments[^1];

            var seg = new ContentSegment
            {
                UiId = Interlocked.Increment(ref s_nextContentSegmentUiId),
                Kind = ContentSegment.SegmentKind.Text
            };
            message.Segments.Add(seg);
            return seg;
        }
    }

    /// <summary>
    /// Starts a new tool-call-group segment (or returns the current one if the last
    /// segment is already a tool group).
    /// </summary>
    private static ContentSegment GetOrCreateToolCallGroupSegment(ChatMessage message)
    {
        lock (message.SegmentsSyncRoot)
        {
            if (message.Segments.Count > 0 && message.Segments[^1].Kind == ContentSegment.SegmentKind.ToolCallGroup)
                return message.Segments[^1];

            var seg = new ContentSegment
            {
                UiId = Interlocked.Increment(ref s_nextContentSegmentUiId),
                Kind = ContentSegment.SegmentKind.ToolCallGroup
            };
            message.Segments.Add(seg);
            return seg;
        }
    }

    /// <summary>
    /// Adds a tool call entry to both the flat list (legacy/copy) and the current
    /// tool-call-group segment.
    /// </summary>
    private static void AddToolCallSegmented(ChatMessage message, ToolCallEntry entry)
    {
        lock (message.ToolCallsSyncRoot)
            message.ToolCalls.Add(entry);

        var group = GetOrCreateToolCallGroupSegment(message);
        lock (message.SegmentsSyncRoot)
            group.ToolCalls.Add(entry);
    }

    /// <summary>
    /// Syncs the current StringBuilder content to the current text segment.
    /// Call this whenever sb changes during streaming so the segment stays current.
    /// </summary>
    /// <param name="message">The chat message whose segments to update.</param>
    /// <param name="sb">The full accumulated StringBuilder for the entire response.</param>
    /// <param name="startOffset">
    /// The offset into <paramref name="sb"/> where the current round's text begins.
    /// Only text from this offset onward is written to the current text segment,
    /// preventing duplication when multiple tool-call rounds each produce model text.
    /// Pass 0 for single-round or non-looped streaming (e.g. Realtime).
    /// </param>
    private static void SyncTextSegment(ChatMessage message, StringBuilder sb, int startOffset = 0)
    {
        int length = sb.Length - startOffset;
        if (length <= 0)
            return;

        var seg = GetOrCreateTextSegment(message);
        seg.Text = sb.ToString(startOffset, length);
    }

    /// <summary>
    /// Strips the inline "Tool calls:" text section from content since tool calls
    /// are rendered separately via the structured <see cref="ChatMessage.ToolCalls"/> list.
    /// Also strips trailing status markers like "[Executing tool calls...]".
    /// </summary>
    private static string GetDisplayContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        string text = content;

        // Strip protocol markers so they never show in the UI.
        text = text.Replace(AssistantDoneMarker, string.Empty, StringComparison.Ordinal);
        text = text.Replace(AssistantContinueMarker, string.Empty, StringComparison.Ordinal);

        // Remove trailing status markers like "[Executing tool calls...]".
        int bracketIdx = text.LastIndexOf('[');
        if (bracketIdx >= 0 && text.TrimEnd().EndsWith(']'))
        {
            string candidate = text[bracketIdx..];
            if (candidate.Contains("tool call", StringComparison.OrdinalIgnoreCase)
                || candidate.Contains("model response", StringComparison.OrdinalIgnoreCase)
                || candidate.Contains("Calling tool", StringComparison.OrdinalIgnoreCase)
                || candidate.Contains("tool result", StringComparison.OrdinalIgnoreCase)
                || candidate.Contains("Continuing", StringComparison.OrdinalIgnoreCase)
                || candidate.Contains("Sending", StringComparison.OrdinalIgnoreCase))
            {
                text = text[..bracketIdx].TrimEnd();
            }
        }

        return text.Trim();
    }

    private static string NormalizeForUiDisplay(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        string result = text
            .Replace("\u00A0", " ", StringComparison.Ordinal)
            .Replace("\u2013", "-", StringComparison.Ordinal)
            .Replace("\u2014", "-", StringComparison.Ordinal)
            .Replace("\u2018", "'", StringComparison.Ordinal)
            .Replace("\u2019", "'", StringComparison.Ordinal)
            .Replace("\u201C", "\"", StringComparison.Ordinal)
            .Replace("\u201D", "\"", StringComparison.Ordinal)
            .Replace("\u2026", "...", StringComparison.Ordinal)
            .Replace("\u2192", "->", StringComparison.Ordinal);

        // Fix missing space between sentences that got concatenated during
        // multi-round streaming (e.g. "frame it.Creating" -> "frame it. Creating").
        var sb = new StringBuilder(result.Length + 32);
        for (int i = 0; i < result.Length; i++)
        {
            char c = result[i];
            sb.Append(c);

            // After a sentence-ending punctuation followed immediately by an uppercase letter,
            // insert a space so the sentences are visually separated.
            if ((c == '.' || c == '!' || c == '?') && i + 1 < result.Length)
            {
                char next = result[i + 1];
                if (char.IsUpper(next))
                    sb.Append(' ');
            }
        }

        return sb.ToString();
    }
}
