using ImGuiNET;
using System.Numerics;

namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    private readonly Dictionary<ChatMessage, ChatMessageView> _chatViews = [];
    private float _chatLayoutWidth;
    private float _chatLayoutFontSize;
    private float _lastChatScrollY;
    private bool _followChat = true;
    private bool _measureAllChatRows;

    /// <summary>Draws conversation rows without moving a reader away from older messages.</summary>
    private void DrawChatLog()
    {
        if (_history.Count < _lastChatHistoryCount || _history.Count == 0)
        {
            _chatRowHeightCache.Clear();
            _chatViews.Clear();
            _followChat = true;
        }
        _lastChatHistoryCount = _history.Count;

        float promptBarHeight = GetPromptInputHeight() + GetComposerActionsHeight()
            + GetSelectedContextHeight() + ImGui.GetFrameHeightWithSpacing()
            + ImGui.GetStyle().ItemSpacing.Y * 2f;
        float available = Math.Max(80f, ImGui.GetContentRegionAvail().Y - promptBarHeight - ImGui.GetFrameHeightWithSpacing());
        ImGui.PushStyleColor(ImGuiCol.ChildBg, ColorChatBg);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f, 12f));
        ImGui.BeginChild("##ChatLog", new Vector2(-1f, available), ImGuiChildFlags.Borders | ImGuiChildFlags.AlwaysUseWindowPadding);

        float width = ImGui.GetContentRegionAvail().X;
        float fontSize = ImGui.GetFontSize();
        _measureAllChatRows = Math.Abs(width - _chatLayoutWidth) > 0.5f || fontSize != _chatLayoutFontSize || _scrollToBottom;
        if (_measureAllChatRows)
        {
            _chatRowHeightCache.Clear();
            _chatLayoutWidth = width;
            _chatLayoutFontSize = fontSize;
        }

        float scrollY = ImGui.GetScrollY();
        float threshold = ImGui.GetTextLineHeightWithSpacing() * 1.5f;
        bool nearBottom = ImGui.GetScrollMaxY() - scrollY <= threshold;
        bool scrolledUp = ImGui.IsWindowHovered() && ImGui.GetIO().MouseWheel > 0f
            || (ImGui.IsMouseDown(ImGuiMouseButton.Left) && scrollY < _lastChatScrollY - 1f)
            || (ImGui.IsWindowFocused() && (ImGui.IsKeyPressed(ImGuiKey.PageUp, false)
                || ImGui.IsKeyPressed(ImGuiKey.Home, false) || ImGui.IsKeyDown(ImGuiKey.UpArrow)));
        if (scrolledUp && !_scrollToBottom)
            _followChat = false;
        else if (nearBottom && !_followChat && scrollY > _lastChatScrollY + 1f)
            _followChat = true;

        if (_history.Count == 0)
        {
            ImGui.TextColored(ColorPlaceholder, "What would you like to create?");
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0f);
            ImGui.TextDisabled("Ask about your scene, or describe a change.");
            ImGui.PopTextWrapPos();
        }
        else
            DrawVirtualizedChatRows();

        if (_scrollToBottom || (AutoScroll && _followChat))
        {
            ImGui.Dummy(new Vector2(1f, 0f));
            ImGui.SetScrollHereY(1f);
            _followChat = true;
            _scrollToBottom = false;
        }
        _lastChatScrollY = scrollY;
        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        // Keep navigation outside the scrolling child so it cannot extend its content bounds.
        if (!_followChat && !nearBottom)
        {
            if (ImGui.Button("Jump to latest"))
                _scrollToBottom = true;
        }
        else
            ImGui.Dummy(new Vector2(1f, ImGui.GetFrameHeight()));
    }

    private void DrawVirtualizedChatRows()
    {
        float lineHeight = ImGui.GetTextLineHeightWithSpacing();
        float visibleMin = Math.Max(0f, ImGui.GetScrollY() - lineHeight * 8f);
        float visibleMax = ImGui.GetScrollY() + ImGui.GetWindowHeight() + lineHeight * 8f;
        float skippedHeight = 0f;
        for (int i = 0; i < _history.Count; i++)
        {
            ChatMessage message = _history[i];
            float height = GetEstimatedChatRowHeight(message, lineHeight);
            float top = ImGui.GetCursorPosY() + skippedHeight;
            bool visible = top + height >= visibleMin && top <= visibleMax;
            // Measure the active tail while following so new text cannot remain below a spacer.
            bool measureTail = _followChat && i >= _history.Count - 2;
            if (!_measureAllChatRows && !visible && !measureTail)
            {
                skippedHeight += height;
                continue;
            }
            if (skippedHeight > 0f)
            {
                ImGui.Dummy(new Vector2(1f, Math.Max(0f, skippedHeight - ImGui.GetStyle().ItemSpacing.Y)));
                skippedHeight = 0f;
            }
            float rowStart = ImGui.GetCursorPosY();
            ImGui.PushID(i);
            DrawChatMessageRow(message, i);
            ImGui.PopID();
            _chatRowHeightCache[message] = Math.Max(ImGui.GetCursorPosY() - rowStart, lineHeight * 3f);
        }
        if (skippedHeight > 0f)
            ImGui.Dummy(new Vector2(1f, Math.Max(0f, skippedHeight - ImGui.GetStyle().ItemSpacing.Y)));
    }

    private float GetEstimatedChatRowHeight(ChatMessage message, float lineHeight)
    {
        if (_chatRowHeightCache.TryGetValue(message, out float height))
            return height;
        int columns = Math.Max(12, (int)(_chatLayoutWidth / Math.Max(1f, ImGui.CalcTextSize("M").X)));
        return lineHeight * (Math.Max(1, message.Content.Length / columns) + 5f);
    }

    private ChatMessageView GetChatView(ChatMessage message)
    {
        if (!_chatViews.TryGetValue(message, out ChatMessageView? view))
        {
            view = new ChatMessageView(message);
            _chatViews.Add(message, view);
        }
        lock (message.SegmentsSyncRoot)
        {
            bool changed = view.Segments.Length != message.Segments.Count;
            for (int i = 0; !changed && i < view.Segments.Length; i++)
                changed = !ReferenceEquals(view.Segments[i], message.Segments[i]);
            if (changed)
                view.Segments = [.. message.Segments];
        }
        lock (message.ToolCallsSyncRoot)
        {
            if (view.Tools.Length != message.ToolCalls.Count)
                view.Tools = [.. message.ToolCalls];
        }
        return view;
    }

    private void DrawChatMessageRow(ChatMessage message, int index)
    {
        ChatMessageView view = GetChatView(message);
        if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            DrawUserMessage(message, view);
        else
            DrawAssistantMessage(message, view, index);
        ImGui.Dummy(new Vector2(1f, ImGui.GetTextLineHeight() * 0.8f));
    }

    private static void DrawUserMessage(ChatMessage message, ChatMessageView view)
    {
        float available = ImGui.GetContentRegionAvail().X;
        float width = Math.Min(available, Math.Max(100f, available * 0.86f));
        float inset = Math.Max(0f, available - width);
        ImGui.Indent(inset);
        ImGui.BeginGroup();
        ImGui.TextColored(ColorTimestamp, "You");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(view.Timestamp);
        ImGui.Spacing();
        Vector2 start = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);
        ImGui.Indent(10f);
        ImGui.Dummy(new Vector2(0f, 3f));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Max(20f, width - 20f));
        ImGui.TextUnformatted(message.Content);
        ImGui.PopTextWrapPos();
        ImGui.Dummy(new Vector2(0f, 3f));
        ImGui.Unindent(10f);
        float bottom = ImGui.GetCursorScreenPos().Y;
        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(start, new Vector2(start.X + width, bottom), ImGui.ColorConvertFloat4ToU32(ColorUserBubble), 7f);
        drawList.ChannelsMerge();
        ImGui.EndGroup();
        ImGui.Unindent(inset);
    }

    private void DrawAssistantMessage(ChatMessage message, ChatMessageView view, int index)
    {
        ImGui.TextUnformatted("Assistant");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(view.Timestamp);
        DrawMessageStatus(message, view);
        ImGui.Spacing();
        if (view.Segments.Length == 0)
        {
            RenderRichContentCached(message, 0, message.Content);
            DrawMessageActivity(message, view, null, -1);
        }
        else
        {
            for (int i = 0; i < view.Segments.Length; i++)
            {
                ContentSegment segment = view.Segments[i];
                if (segment.Kind == ContentSegment.SegmentKind.Text)
                    RenderRichContentCached(message, segment.UiId, segment.Text);
                else
                    DrawMessageActivity(message, view, segment, segment.UiId);
                ImGui.Spacing();
            }
        }

        if (!string.IsNullOrWhiteSpace(message.StatusDetail))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, message.Status == ChatMessageStatus.Failed ? ColorError : ColorTimestamp);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(message.StatusDetail);
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
        }
        if (message.Status == ChatMessageStatus.Stopped && view.Tools.Length > 0)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextDisabled("Completed actions remain in the scene.");
            ImGui.PopTextWrapPos();
        }

        // Artifacts remain reachable when the activity group is collapsed.
        for (int i = 0; i < view.Tools.Length; i++)
        {
            ToolCallEntry call = view.Tools[i];
            if (string.IsNullOrWhiteSpace(call.ResultFilePath))
                continue;
            bool duplicate = false;
            for (int previous = 0; previous < i; previous++)
            {
                if (string.Equals(view.Tools[previous].ResultFilePath, call.ResultFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    duplicate = true;
                    break;
                }
            }
            if (duplicate)
                continue;
            DrawFilePathLink(call.ResultFilePath, index, i);
            if (IsImageFilePath(call.ResultFilePath))
                DrawImagePreview(call.ResultFilePath, index, i);
        }
        if (!message.IsStreaming && !string.IsNullOrWhiteSpace(message.Content))
        {
            ImGui.Spacing();
            if (ImGui.SmallButton("Copy reply"))
                ImGui.SetClipboardText(GetDisplayContent(message.Content));
            DrawExplanationActions(message);
        }
    }

    private static void DrawMessageStatus(ChatMessage message, ChatMessageView view)
    {
        bool errors = false;
        for (int i = 0; i < view.Tools.Length; i++)
            errors |= view.Tools[i].IsError;
        string label = message.IsStreaming ? "Working..." : message.Status switch
        {
            ChatMessageStatus.Failed => "Failed",
            ChatMessageStatus.Stopped => "Stopped",
            ChatMessageStatus.Incomplete => "More work remains",
            _ => errors ? "Finished with tool errors" : "Completed"
        };
        float needed = ImGui.CalcTextSize(label).X + ImGui.GetStyle().ItemSpacing.X;
        if (ImGui.GetContentRegionAvail().X > needed + 85f)
            ImGui.SameLine();
        ImGui.TextColored(message.IsStreaming ? ColorBusy : errors || message.Status == ChatMessageStatus.Failed ? ColorError : ColorTimestamp, label);
    }

    private void DrawMessageActivity(ChatMessage message, ChatMessageView view, ContentSegment? segment, int key)
    {
        if (!view.Activities.TryGetValue(key, out ChatActivityView? activity))
        {
            activity = new ChatActivityView();
            view.Activities.Add(key, activity);
        }
        if (segment is null)
            activity.Tools = view.Tools;
        else
        {
            lock (message.SegmentsSyncRoot)
            {
                if (activity.Tools.Length != segment.ToolCalls.Count)
                    activity.Tools = [.. segment.ToolCalls];
            }
        }
        if (activity.Tools.Length == 0)
            return;
        int completed = 0;
        int errors = 0;
        for (int i = 0; i < activity.Tools.Length; i++)
        {
            completed += activity.Tools[i].IsComplete ? 1 : 0;
            errors += activity.Tools[i].IsError ? 1 : 0;
        }
        bool running = message.IsStreaming && completed < activity.Tools.Length;
        if (activity.Completed != completed || activity.Errors != errors || activity.Total != activity.Tools.Length || activity.Running != running)
        {
            activity.Completed = completed;
            activity.Errors = errors;
            activity.Total = activity.Tools.Length;
            activity.Running = running;
            string summary = running ? $"Working - {completed} of {activity.Total} actions finished"
                : completed != activity.Total ? $"Activity - {completed} of {activity.Total} actions finished"
                : $"{activity.Total} action{(activity.Total == 1 ? string.Empty : "s")} finished";
            activity.Label = (errors > 0 ? $"{summary} - {errors} failed" : summary) + "###Activity";
        }
        ImGui.PushID(key);
        ImGui.PushStyleColor(ImGuiCol.Header, ColorToolSection);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, ColorToolSectionHover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, ColorToolSectionHover);
        bool open = ImGui.TreeNodeEx(activity.Label, ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.Framed);
        if (ImGui.IsItemToggledOpen())
            _followChat = false;
        ImGui.PopStyleColor(3);
        if (open)
        {
            string? currentCategory = null;
            for (int i = 0; i < activity.Tools.Length; i++)
            {
                ToolCallEntry call = activity.Tools[i];
                if (!string.Equals(currentCategory, call.Category, StringComparison.Ordinal))
                {
                    currentCategory = call.Category;
                    ImGui.Spacing();
                    ImGui.TextDisabled(currentCategory);
                }
                ImGui.PushID(i);
                string state = call.IsError ? "Failed" : call.IsComplete ? "Finished" : message.IsStreaming ? "Running" : "Not finished";
                ImGui.TextColored(call.IsError ? ColorError : call.IsComplete ? ColorTimestamp : ColorBusy, state);
                ImGui.SameLine();
                ImGui.PushTextWrapPos(0f);
                ImGui.TextUnformatted(call.DisplayName);
                ImGui.PopTextWrapPos();
                if (call.IsComplete && !string.IsNullOrWhiteSpace(call.OutcomeSummary))
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, call.IsError ? ColorError : ColorTimestamp);
                    ImGui.PushTextWrapPos(0f);
                    ImGui.TextUnformatted(call.OutcomeSummary);
                    ImGui.PopTextWrapPos();
                    ImGui.PopStyleColor();
                }
                if (!string.IsNullOrWhiteSpace(call.VerificationSummary))
                {
                    ImGui.PushTextWrapPos(0f);
                    ImGui.TextDisabled(call.VerificationSummary);
                    ImGui.PopTextWrapPos();
                }
                bool detailsOpen = ImGui.TreeNodeEx("Details", ImGuiTreeNodeFlags.SpanAvailWidth);
                if (ImGui.IsItemToggledOpen())
                    _followChat = false;
                if (detailsOpen)
                {
                    ImGui.PushTextWrapPos(0f);
                    ImGui.TextDisabled(call.ToolName);
                    if (!string.IsNullOrWhiteSpace(call.ArgsSummary))
                    {
                        ImGui.TextDisabled("Input");
                        ImGui.TextUnformatted(call.ArgsSummary);
                    }
                    if (!string.IsNullOrWhiteSpace(call.ResultSummary))
                    {
                        ImGui.TextDisabled("Result");
                        ImGui.TextUnformatted(call.ResultSummary);
                    }
                    ImGui.PopTextWrapPos();
                    ImGui.TreePop();
                }
                ImGui.PopID();
            }
            ImGui.TreePop();
        }
        ImGui.PopID();
    }
}
