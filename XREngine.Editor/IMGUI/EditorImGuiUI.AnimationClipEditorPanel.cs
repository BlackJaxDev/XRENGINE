using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ImGuiNET;
using XREngine.Animation;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static AnimationClip? _animationClipEditorClip;
    private static readonly ConditionalWeakTable<AnimationClip, AnimationClipEditorState> _animationClipEditorStateCache = new();
    private static readonly Dictionary<Type, MethodInfo?> _animationClipGetValueMethodCache = new();
    private static readonly Vector4[] _animationClipChannelPalette =
    [
        new Vector4(0.93f, 0.39f, 0.32f, 1.0f),
        new Vector4(0.22f, 0.67f, 0.92f, 1.0f),
        new Vector4(0.37f, 0.80f, 0.49f, 1.0f),
        new Vector4(0.92f, 0.77f, 0.24f, 1.0f)
    ];

    public static void OpenAnimationClipEditor(AnimationClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        _animationClipEditorClip = clip;
        _showAnimationClipEditor = true;
    }

    private static void DrawAnimationClipEditorPanel()
    {
        if (!_showAnimationClipEditor)
            return;

        if (_animationClipEditorClip is null && _inspectorStandaloneTarget is AnimationClip inspectedClip)
            _animationClipEditorClip = inspectedClip;

        ImGui.SetNextWindowSize(new Vector2(1260.0f, 760.0f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Animation Clip Editor", ref _showAnimationClipEditor, ImGuiWindowFlags.MenuBar))
        {
            ImGui.End();
            return;
        }

        AnimationClip? clip = ResolveAnimationClipEditorClip();
        if (clip is null)
        {
            DrawAnimationClipEditorEmptyState();
            ImGui.End();
            return;
        }

        _animationClipEditorClip = clip;
        AnimationClipEditorState state = _animationClipEditorStateCache.GetValue(clip, static _ => new AnimationClipEditorState());
        EnsureAnimationClipSelectedMember(clip, state);

        DrawAnimationClipEditorHeader(clip, state);
        ImGui.Separator();

        List<AnimationClipMemberRow> visibleRows = new(64);
        Vector2 available = ImGui.GetContentRegionAvail();
        float leftWidth = MathF.Min(MathF.Max(300.0f, available.X * 0.28f), 440.0f);

        if (ImGui.BeginChild("AnimationClipEditorMemberTree", new Vector2(leftWidth, 0.0f), ImGuiChildFlags.Border | ImGuiChildFlags.ResizeX))
        {
            DrawAnimationClipEditorMemberSidebar(clip, state, visibleRows);
        }
        ImGui.EndChild();

        ImGui.SameLine();

        if (ImGui.BeginChild("AnimationClipEditorContent", Vector2.Zero, ImGuiChildFlags.Border))
        {
            DrawAnimationClipEditorContent(clip, state, visibleRows);
        }
        ImGui.EndChild();

        ImGui.End();
    }

    private static AnimationClip? ResolveAnimationClipEditorClip()
    {
        if (_animationClipEditorClip is not null)
            return _animationClipEditorClip;

        if (_inspectorStandaloneTarget is AnimationClip standaloneClip)
            return standaloneClip;

        return null;
    }

    private static void DrawAnimationClipEditorEmptyState()
    {
        ImGui.TextDisabled("No AnimationClip asset is currently open.");
        ImGui.Spacing();
        ImGui.TextWrapped("Open a clip from the asset inspector, the asset browser context menu, or inspect an AnimationClip asset and then reopen this panel.");

        if (_inspectorStandaloneTarget is AnimationClip inspectorClip)
        {
            ImGui.Spacing();
            if (ImGui.Button("Use Inspector Selection"))
                OpenAnimationClipEditor(inspectorClip);
        }
    }

    private static void DrawAnimationClipEditorHeader(AnimationClip clip, AnimationClipEditorState state)
    {
        string displayName = !string.IsNullOrWhiteSpace(clip.Name)
            ? clip.Name!
            : (!string.IsNullOrWhiteSpace(clip.FilePath)
                ? System.IO.Path.GetFileNameWithoutExtension(clip.FilePath)
                : "Animation Clip");

        ImGui.TextUnformatted(displayName);
        ImGui.TextDisabled(clip.FilePath ?? "<unsaved asset>");

        if (!string.IsNullOrWhiteSpace(clip.FilePath))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy Path##AnimationClipEditor"))
                ImGui.SetClipboardText(clip.FilePath);
        }

        if (_inspectorStandaloneTarget is AnimationClip inspectedClip && !ReferenceEquals(inspectedClip, clip))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Use Inspector Clip##AnimationClipEditor"))
                OpenAnimationClipEditor(inspectedClip);
        }

        ImGui.Separator();

        bool lanesSelected = state.ViewMode == AnimationClipEditorViewMode.Lanes;
        if (ImGui.RadioButton("Lane View", lanesSelected))
            state.ViewMode = AnimationClipEditorViewMode.Lanes;

        ImGui.SameLine();
        bool graphSelected = state.ViewMode == AnimationClipEditorViewMode.Graph;
        if (ImGui.RadioButton("Curve Graph", graphSelected))
            state.ViewMode = AnimationClipEditorViewMode.Graph;

        ImGui.SameLine();
        ImGui.TextDisabled($"Length: {clip.LengthInSeconds:0.###} s | Sample Rate: {clip.SampleRate} fps | Traversal: {clip.TraversalMethod}");
    }

    private static void DrawAnimationClipEditorMemberSidebar(AnimationClip clip, AnimationClipEditorState state, List<AnimationClipMemberRow> visibleRows)
    {
        ImGui.TextUnformatted("Animation Member Tree");
        ImGui.Separator();

        if (clip.RootMember is null)
        {
            ImGui.TextDisabled("This clip does not have a root animation member.");
            return;
        }

        DrawAnimationClipEditorMemberTreeNode(clip.RootMember, state, "0", clip.RootMember.MemberName, depth: 0, visibleRows);
    }

    private static void DrawAnimationClipEditorContent(AnimationClip clip, AnimationClipEditorState state, List<AnimationClipMemberRow> visibleRows)
    {
        TryFindAnimationClipSelectedMember(clip.RootMember, state.SelectedMemberId, "0", clip.RootMember?.MemberName, out AnimationMember? selectedMember, out string selectedPath);

        float spacing = ImGui.GetStyle().ItemSpacing.Y;
        float detailsHeight = 220.0f;
        float topHeight = MathF.Max(200.0f, ImGui.GetContentRegionAvail().Y - detailsHeight - spacing);

        if (ImGui.BeginChild("AnimationClipEditorCanvas", new Vector2(-1.0f, topHeight), ImGuiChildFlags.Border))
        {
            if (state.ViewMode == AnimationClipEditorViewMode.Lanes)
                DrawAnimationClipLaneTimeline(clip, state, visibleRows);
            else
                DrawAnimationClipGraphView(clip, state, selectedMember, selectedPath);
        }
        ImGui.EndChild();

        ImGui.Dummy(new Vector2(0.0f, spacing));

        if (ImGui.BeginChild("AnimationClipEditorDetails", Vector2.Zero, ImGuiChildFlags.Border))
        {
            DrawAnimationClipSelectionDetails(selectedMember, selectedPath);
        }
        ImGui.EndChild();
    }

    private static void DrawAnimationClipLaneTimeline(AnimationClip clip, AnimationClipEditorState state, List<AnimationClipMemberRow> visibleRows)
    {
        if (visibleRows.Count == 0)
        {
            ImGui.TextDisabled("No visible members to draw.");
            return;
        }

        float pixelsPerSecond = state.LanePixelsPerSecond;
        ImGui.SetNextItemWidth(180.0f);
        if (ImGui.SliderFloat("Zoom", ref pixelsPerSecond, 24.0f, 480.0f, "%.0f px/s"))
            state.LanePixelsPerSecond = pixelsPerSecond;

        ImGui.SameLine();
        if (ImGui.Button("Fit Timeline"))
        {
            float usableWidth = MathF.Max(240.0f, ImGui.GetContentRegionAvail().X - 120.0f);
            state.LanePixelsPerSecond = clip.LengthInSeconds <= 0.0f
                ? 120.0f
                : Math.Clamp(usableWidth / clip.LengthInSeconds, 24.0f, 480.0f);
        }

        ImGui.SameLine();
        ImGui.TextDisabled("Visible tree rows map directly to timeline lanes.");

        const float rowHeight = 28.0f;
        const float headerHeight = 28.0f;
        const float timePadding = 18.0f;

        if (!ImGui.BeginChild("AnimationClipLaneTimelineChild", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar))
            return;

        Vector2 origin = ImGui.GetCursorScreenPos();
        Vector2 available = ImGui.GetContentRegionAvail();
        float timelineWidth = MathF.Max(available.X, timePadding * 2.0f + MathF.Max(clip.LengthInSeconds, 0.001f) * state.LanePixelsPerSecond);
        float timelineHeight = MathF.Max(available.Y, headerHeight + (visibleRows.Count * rowHeight) + 4.0f);
        Vector2 canvasSize = new(timelineWidth, timelineHeight);

        ImGui.InvisibleButton("##AnimationClipLaneTimelineCanvas", canvasSize);

        bool hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            float wheel = ImGui.GetIO().MouseWheel;
            if (MathF.Abs(wheel) > 0.0001f)
                state.LanePixelsPerSecond = Math.Clamp(state.LanePixelsPerSecond * MathF.Pow(1.12f, wheel), 24.0f, 480.0f);

            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                Vector2 mouse = ImGui.GetMousePos();
                float localY = mouse.Y - origin.Y - headerHeight;
                if (localY >= 0.0f)
                {
                    int rowIndex = (int)(localY / rowHeight);
                    if (rowIndex >= 0 && rowIndex < visibleRows.Count)
                        state.SelectedMemberId = visibleRows[rowIndex].Id;
                }
            }
        }

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        uint bgColor = ImGui.GetColorU32(new Vector4(0.08f, 0.09f, 0.11f, 1.0f));
        uint gridColor = ImGui.GetColorU32(new Vector4(0.22f, 0.24f, 0.30f, 1.0f));
        uint rowAltColor = ImGui.GetColorU32(new Vector4(1.0f, 1.0f, 1.0f, 0.03f));
        uint rowSelectedColor = ImGui.GetColorU32(new Vector4(0.32f, 0.48f, 0.78f, 0.18f));
        uint keyColor = ImGui.GetColorU32(new Vector4(0.93f, 0.77f, 0.27f, 1.0f));
        uint missingColor = ImGui.GetColorU32(new Vector4(0.90f, 0.36f, 0.34f, 1.0f));
        uint dimTextColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);

        drawList.AddRectFilled(origin, origin + canvasSize, bgColor, 4.0f);

        float majorStep = GetAnimationClipTimelineStep(state.LanePixelsPerSecond);
        int stepCount = Math.Max(1, (int)MathF.Ceiling(MathF.Max(clip.LengthInSeconds, majorStep) / majorStep));
        for (int stepIndex = 0; stepIndex <= stepCount; stepIndex++)
        {
            float second = stepIndex * majorStep;
            if (second > clip.LengthInSeconds && stepIndex > 0)
                second = clip.LengthInSeconds;

            float x = origin.X + timePadding + (second * state.LanePixelsPerSecond);
            drawList.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + canvasSize.Y), gridColor, 1.0f);
            string label = clip.SampleRate > 0
                ? $"{second:0.##} s / {(int)Math.Round(second * clip.SampleRate)} f"
                : $"{second:0.##} s";
            drawList.AddText(new Vector2(x + 4.0f, origin.Y + 6.0f), dimTextColor, label);

            if (second >= clip.LengthInSeconds)
                break;
        }

        int hoveredRow = -1;
        Keyframe? hoveredKeyframe = null;
        AnimationClipMemberRow hoveredMember = default;
        float hoveredDistance = float.MaxValue;
        Vector2 mousePos = ImGui.GetMousePos();

        for (int rowIndex = 0; rowIndex < visibleRows.Count; rowIndex++)
        {
            AnimationClipMemberRow row = visibleRows[rowIndex];
            float rowMinY = origin.Y + headerHeight + (rowIndex * rowHeight);
            float rowMaxY = rowMinY + rowHeight;

            if ((rowIndex & 1) != 0)
                drawList.AddRectFilled(new Vector2(origin.X, rowMinY), new Vector2(origin.X + canvasSize.X, rowMaxY), rowAltColor);

            if (string.Equals(state.SelectedMemberId, row.Id, StringComparison.Ordinal))
                drawList.AddRectFilled(new Vector2(origin.X, rowMinY), new Vector2(origin.X + canvasSize.X, rowMaxY), rowSelectedColor);

            drawList.AddLine(new Vector2(origin.X, rowMaxY), new Vector2(origin.X + canvasSize.X, rowMaxY), gridColor, 1.0f);

            if (row.Member.Animation is null)
            {
                drawList.AddText(new Vector2(origin.X + 8.0f, rowMinY + 6.0f), dimTextColor, "No animation bound to this member.");
                continue;
            }

            bool hasAnyKey = false;
            foreach (Keyframe keyframe in EnumerateAnimationKeyframes(row.Member.Animation))
            {
                hasAnyKey = true;
                float x = origin.X + timePadding + (keyframe.Second * state.LanePixelsPerSecond);
                float centerY = rowMinY + (rowHeight * 0.5f);
                uint drawColor = row.Member.MemberNotFound ? missingColor : keyColor;
                DrawAnimationClipDiamond(drawList, new Vector2(x, centerY), 5.0f, drawColor);

                if (!hovered)
                    continue;

                float dx = MathF.Abs(mousePos.X - x);
                float dy = MathF.Abs(mousePos.Y - centerY);
                if (dx <= 8.0f && dy <= 8.0f)
                {
                    float distSq = (dx * dx) + (dy * dy);
                    if (distSq < hoveredDistance)
                    {
                        hoveredDistance = distSq;
                        hoveredRow = rowIndex;
                        hoveredKeyframe = keyframe;
                        hoveredMember = row;
                    }
                }
            }

            if (!hasAnyKey)
                drawList.AddText(new Vector2(origin.X + 8.0f, rowMinY + 6.0f), dimTextColor, "Animation has no keyframes.");
        }

        if (hovered && hoveredKeyframe is not null && hoveredRow >= 0)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(hoveredMember.DisplayPath) ? hoveredMember.Member.MemberName ?? "<unnamed member>" : hoveredMember.DisplayPath);
            ImGui.TextDisabled($"Time: {hoveredKeyframe.Second:0.###} s");
            if (hoveredKeyframe.AuthoredFrameIndex >= 0)
                ImGui.TextDisabled($"Frame: {hoveredKeyframe.AuthoredFrameIndex}");
            ImGui.TextDisabled($"Value: {FormatAnimationClipValue(GetAnimationClipKeyframeDisplayValue(hoveredKeyframe))}");
            ImGui.EndTooltip();
        }

        ImGui.EndChild();
    }

    private static void DrawAnimationClipGraphView(AnimationClip clip, AnimationClipEditorState state, AnimationMember? selectedMember, string selectedPath)
    {
        if (selectedMember is null)
        {
            ImGui.TextDisabled("Select an animation member to graph its keyframe interpolation.");
            return;
        }

        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(selectedPath) ? selectedMember.MemberName ?? "<unnamed member>" : selectedPath);
        ImGui.TextDisabled(BuildAnimationClipMemberActionSummary(selectedMember));

        if (selectedMember.Animation is null)
        {
            ImGui.Separator();
            ImGui.TextDisabled("The selected member does not have an attached animation to graph.");
            return;
        }

        if (!string.Equals(state.LastGraphedMemberId, state.SelectedMemberId, StringComparison.Ordinal))
        {
            state.LastGraphedMemberId = state.SelectedMemberId;
            state.PendingGraphAutoFrame = true;
        }

        ImGui.Separator();
        DrawAnimationClipGraphToolbar(clip, state);

        if (!ImGui.BeginChild("AnimationClipGraphCanvasHost", Vector2.Zero, ImGuiChildFlags.None))
            return;

        Vector2 canvasPos = ImGui.GetCursorScreenPos();
        Vector2 canvasSize = ImGui.GetContentRegionAvail();
        canvasSize.X = MathF.Max(canvasSize.X, 160.0f);
        canvasSize.Y = MathF.Max(canvasSize.Y, 180.0f);

        ImGui.InvisibleButton("##AnimationClipGraphCanvas", canvasSize, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        bool hovered = ImGui.IsItemHovered();
        bool active = ImGui.IsItemActive();

        if (!TryBuildAnimationClipGraphData(selectedMember.Animation, clip, state.XAxisMode, canvasSize, out List<AnimationClipGraphChannel> channels, out AnimationClipGraphBounds bounds))
        {
            ImDrawListPtr emptyDrawList = ImGui.GetWindowDrawList();
            emptyDrawList.AddRectFilled(canvasPos, canvasPos + canvasSize, ImGui.GetColorU32(new Vector4(0.08f, 0.09f, 0.11f, 1.0f)), 4.0f);
            emptyDrawList.AddText(new Vector2(canvasPos.X + 12.0f, canvasPos.Y + 12.0f), ImGui.GetColorU32(ImGuiCol.TextDisabled), "This animation does not expose numeric keyframe data that can be graphed.");
            ImGui.EndChild();
            return;
        }

        if (state.PendingGraphAutoFrame)
        {
            FrameAnimationClipGraph(bounds, canvasSize, state);
            state.PendingGraphAutoFrame = false;
        }

        HandleAnimationClipGraphInput(state, bounds, canvasPos, canvasSize, hovered, active);
        DrawAnimationClipGraphCanvas(clip, state, channels, bounds, canvasPos, canvasSize, hovered);
        ImGui.EndChild();
    }

    private static void DrawAnimationClipGraphToolbar(AnimationClip clip, AnimationClipEditorState state)
    {
        int xMode = state.XAxisMode == AnimationClipGraphXAxisMode.Seconds || clip.SampleRate <= 0 ? 0 : 1;
        ImGui.SetNextItemWidth(140.0f);
        if (ImGui.Combo("X Axis", ref xMode, clip.SampleRate > 0 ? "Seconds\0Frames\0" : "Seconds\0"))
            state.XAxisMode = xMode == 1 && clip.SampleRate > 0 ? AnimationClipGraphXAxisMode.Frames : AnimationClipGraphXAxisMode.Seconds;

        ImGui.SameLine();
        if (ImGui.Button("Frame Graph"))
            state.PendingGraphAutoFrame = true;

        ImGui.SameLine();
        ImGui.TextDisabled("Pan: RMB/MMB drag | Zoom X: wheel | Zoom Y: Ctrl+wheel");
    }

    private static void DrawAnimationClipSelectionDetails(AnimationMember? selectedMember, string selectedPath)
    {
        if (selectedMember is null)
        {
            ImGui.TextDisabled("Select a member to inspect its properties and attached animation.");
            return;
        }

        string displayName = string.IsNullOrWhiteSpace(selectedMember.MemberName) ? "<unnamed member>" : selectedMember.MemberName;
        ImGui.TextUnformatted(displayName);
        ImGui.TextDisabled(selectedPath);

        if (selectedMember.MemberNotFound)
            ImGui.TextColored(new Vector4(0.95f, 0.45f, 0.45f, 1.0f), "This member was not resolved at runtime.");

        HashSet<object> visited = new();
        EditorImGuiUI.DrawRuntimeObjectInspector("Member Properties", selectedMember, visited, defaultOpen: true);

        if (selectedMember.MemberType == EAnimationMemberType.Method)
        {
            ImGui.Separator();
            DrawAnimationClipMethodArgumentSummary(selectedMember);
        }

        ImGui.Separator();
        if (selectedMember.Animation is null)
        {
            ImGui.TextDisabled("No animation asset is attached to this member.");
        }
        else
        {
            EditorImGuiUI.DrawRuntimeObjectInspector("Attached Animation", selectedMember.Animation, visited, defaultOpen: true);
        }
    }

    private static void DrawAnimationClipMethodArgumentSummary(AnimationMember member)
    {
        if (!ImGui.CollapsingHeader("Method Arguments", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        object?[] args = member.MethodArguments ?? Array.Empty<object?>();
        if (args.Length == 0)
        {
            ImGui.TextDisabled("This method member has no arguments configured.");
            return;
        }

        if (!ImGui.BeginTable("AnimationClipEditorMethodArgs", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("Index", ImGuiTableColumnFlags.WidthFixed, 48.0f);
        ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, 120.0f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        for (int index = 0; index < args.Length; index++)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(index.ToString(CultureInfo.InvariantCulture));

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(index == member.AnimatedMethodArgumentIndex ? "Animated" : "Static");

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(FormatAnimationClipValue(args[index]));
        }

        ImGui.EndTable();
    }

    private static void DrawAnimationClipEditorMemberTreeNode(AnimationMember member, AnimationClipEditorState state, string memberId, string? displayPath, int depth, List<AnimationClipMemberRow> visibleRows)
    {
        bool hasChildren = member.Children.Count > 0;
        ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.DefaultOpen;
        if (!hasChildren)
            flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;

        if (string.Equals(state.SelectedMemberId, memberId, StringComparison.Ordinal))
            flags |= ImGuiTreeNodeFlags.Selected;

        string label = BuildAnimationClipTreeLabel(member);
        bool open;
        if (hasChildren)
            open = ImGui.TreeNodeEx(memberId, flags, label);
        else
        {
            ImGui.TreeNodeEx(memberId, flags, label);
            open = false;
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            state.SelectedMemberId = memberId;

        visibleRows.Add(new AnimationClipMemberRow(member, memberId, displayPath ?? string.Empty, depth));

        if (hasChildren && open)
        {
            for (int childIndex = 0; childIndex < member.Children.Count; childIndex++)
            {
                AnimationMember child = member.Children[childIndex];
                string childId = $"{memberId}/{childIndex}";
                string childPath = AppendAnimationClipDisplayPath(displayPath, child.MemberName);
                DrawAnimationClipEditorMemberTreeNode(child, state, childId, childPath, depth + 1, visibleRows);
            }

            ImGui.TreePop();
        }
    }

    private static void EnsureAnimationClipSelectedMember(AnimationClip clip, AnimationClipEditorState state)
    {
        if (clip.RootMember is null)
        {
            state.SelectedMemberId = null;
            return;
        }

        if (!string.IsNullOrWhiteSpace(state.SelectedMemberId)
            && TryFindAnimationClipSelectedMember(clip.RootMember, state.SelectedMemberId, "0", clip.RootMember.MemberName, out _, out _))
        {
            return;
        }

        state.SelectedMemberId = "0";
    }

    private static bool TryFindAnimationClipSelectedMember(AnimationMember? member, string? selectedId, string currentId, string? currentPath, out AnimationMember? selectedMember, out string displayPath)
    {
        if (member is null)
        {
            selectedMember = null;
            displayPath = string.Empty;
            return false;
        }

        if (string.Equals(selectedId, currentId, StringComparison.Ordinal))
        {
            selectedMember = member;
            displayPath = currentPath ?? string.Empty;
            return true;
        }

        for (int childIndex = 0; childIndex < member.Children.Count; childIndex++)
        {
            AnimationMember child = member.Children[childIndex];
            string childId = $"{currentId}/{childIndex}";
            string childPath = AppendAnimationClipDisplayPath(currentPath, child.MemberName);
            if (TryFindAnimationClipSelectedMember(child, selectedId, childId, childPath, out selectedMember, out displayPath))
                return true;
        }

        selectedMember = null;
        displayPath = string.Empty;
        return false;
    }

    private static string AppendAnimationClipDisplayPath(string? parentPath, string? memberName)
    {
        string segment = string.IsNullOrWhiteSpace(memberName) ? "<unnamed member>" : memberName;
        return string.IsNullOrWhiteSpace(parentPath) ? segment : $"{parentPath}.{segment}";
    }

    private static string BuildAnimationClipTreeLabel(AnimationMember member)
    {
        string name = string.IsNullOrWhiteSpace(member.MemberName) ? "<unnamed member>" : member.MemberName;
        string action = member.MemberType switch
        {
            EAnimationMemberType.Property => "sets property",
            EAnimationMemberType.Field => "writes field",
            EAnimationMemberType.Method => $"invokes arg {member.AnimatedMethodArgumentIndex}",
            EAnimationMemberType.Group => "groups members",
            _ => "animates member"
        };

        string label = $"{name} [{member.MemberType}] -> {action}";
        if (member.Animation is not null)
            label += $" ({member.Animation.GetType().Name})";
        else if (member.MemberType != EAnimationMemberType.Group)
            label += " (no animation)";

        if (member.MemberNotFound)
            label += " [missing]";

        return label;
    }

    private static string BuildAnimationClipMemberActionSummary(AnimationMember member)
    {
        return member.MemberType switch
        {
            EAnimationMemberType.Property when member.Animation is not null
                => $"Property writes values from {member.Animation.GetType().Name}.",
            EAnimationMemberType.Field when member.Animation is not null
                => $"Field writes values from {member.Animation.GetType().Name}.",
            EAnimationMemberType.Method when member.Animation is not null
                => $"Method argument {member.AnimatedMethodArgumentIndex} is driven by {member.Animation.GetType().Name}.",
            EAnimationMemberType.Method
                => $"Method argument {member.AnimatedMethodArgumentIndex} uses its configured static value.",
            EAnimationMemberType.Group
                => "Group node used to organize nested animated members.",
            _
                => "No animation asset is attached to this member."
        };
    }

    private static IEnumerable<Keyframe> EnumerateAnimationKeyframes(object animation)
    {
        if (animation is IEnumerable enumerable)
        {
            foreach (object? item in enumerable)
            {
                if (item is Keyframe keyframe)
                    yield return keyframe;
            }
        }
    }

    private static object? GetAnimationClipKeyframeDisplayValue(Keyframe keyframe)
    {
        if (keyframe is IPlanarKeyframe planar)
            return planar.OutValue;

        if (keyframe is QuaternionKeyframe quaternionKeyframe)
            return quaternionKeyframe.OutValue;

        PropertyInfo? valueProperty = keyframe.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        return valueProperty?.CanRead == true ? valueProperty.GetValue(keyframe) : null;
    }

    private static string FormatAnimationClipValue(object? value)
        => value switch
        {
            null => "<null>",
            float scalar => scalar.ToString("0.###", CultureInfo.InvariantCulture),
            double scalar => scalar.ToString("0.###", CultureInfo.InvariantCulture),
            Vector2 vector2 => $"({vector2.X:0.###}, {vector2.Y:0.###})",
            Vector3 vector3 => $"({vector3.X:0.###}, {vector3.Y:0.###}, {vector3.Z:0.###})",
            Vector4 vector4 => $"({vector4.X:0.###}, {vector4.Y:0.###}, {vector4.Z:0.###}, {vector4.W:0.###})",
            Quaternion quaternion => $"({quaternion.X:0.###}, {quaternion.Y:0.###}, {quaternion.Z:0.###}, {quaternion.W:0.###})",
            string text when string.IsNullOrWhiteSpace(text) => "<empty>",
            string text => text,
            _ => value.ToString() ?? value.GetType().Name
        };

    private static float GetAnimationClipTimelineStep(float pixelsPerSecond)
    {
        float[] candidateSteps = [0.033333335f, 0.1f, 0.25f, 0.5f, 1.0f, 2.0f, 5.0f, 10.0f];
        foreach (float candidate in candidateSteps)
        {
            if (candidate * pixelsPerSecond >= 80.0f)
                return candidate;
        }

        return 10.0f;
    }

    private static void DrawAnimationClipDiamond(ImDrawListPtr drawList, Vector2 center, float radius, uint color)
    {
        Vector2 top = new(center.X, center.Y - radius);
        Vector2 right = new(center.X + radius, center.Y);
        Vector2 bottom = new(center.X, center.Y + radius);
        Vector2 left = new(center.X - radius, center.Y);
        drawList.AddQuadFilled(top, right, bottom, left, color);
    }

    private static bool TryBuildAnimationClipGraphData(object animation, AnimationClip clip, AnimationClipGraphXAxisMode xAxisMode, Vector2 canvasSize, out List<AnimationClipGraphChannel> channels, out AnimationClipGraphBounds bounds)
    {
        channels = [];
        bounds = default;

        List<Keyframe> keyframes = EnumerateAnimationKeyframes(animation).ToList();
        if (keyframes.Count == 0)
            return false;

        object? firstValue = GetAnimationClipKeyframeDisplayValue(keyframes[0]);
        if (!TryDecomposeAnimationClipValue(firstValue, out string[] componentLabels, out _))
            return false;

        MethodInfo? getValueMethod = ResolveAnimationClipGetValueMethod(animation.GetType());
        if (getValueMethod is null)
            return false;

        List<List<Vector2>> sampledPoints = new(componentLabels.Length);
        List<List<AnimationClipGraphKeyframe>> keyframePoints = new(componentLabels.Length);
        for (int index = 0; index < componentLabels.Length; index++)
        {
            sampledPoints.Add(new List<Vector2>(192));
            keyframePoints.Add(new List<AnimationClipGraphKeyframe>(keyframes.Count));
        }

        float minX = 0.0f;
        float maxX = ConvertAnimationClipXUnit(clip.LengthInSeconds, clip, xAxisMode);
        float minY = float.PositiveInfinity;
        float maxY = float.NegativeInfinity;

        foreach (Keyframe keyframe in keyframes)
        {
            if (!TryDecomposeAnimationClipValue(GetAnimationClipKeyframeDisplayValue(keyframe), out _, out float[] keyValues) || keyValues.Length != componentLabels.Length)
                continue;

            float keyX = ConvertAnimationClipXUnit(keyframe.Second, clip, xAxisMode, keyframe.AuthoredFrameIndex);
            for (int componentIndex = 0; componentIndex < componentLabels.Length; componentIndex++)
            {
                float keyY = keyValues[componentIndex];
                keyframePoints[componentIndex].Add(new AnimationClipGraphKeyframe(keyX, keyY, keyframe));
                minY = MathF.Min(minY, keyY);
                maxY = MathF.Max(maxY, keyY);
            }
        }

        int sampleCount = Math.Clamp((int)MathF.Round(MathF.Max(96.0f, canvasSize.X * 0.35f)), 96, 320);
        object?[] invokeArgs = [0.0f];
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            float t = sampleCount <= 1 ? 0.0f : sampleIndex / (sampleCount - 1.0f);
            float second = clip.LengthInSeconds <= 0.0f ? 0.0f : clip.LengthInSeconds * t;
            invokeArgs[0] = second;

            object? sampledValue;
            try
            {
                sampledValue = getValueMethod.Invoke(animation, invokeArgs);
            }
            catch
            {
                return false;
            }

            if (!TryDecomposeAnimationClipValue(sampledValue, out _, out float[] sampledComponents) || sampledComponents.Length != componentLabels.Length)
                return false;

            float sampleX = ConvertAnimationClipXUnit(second, clip, xAxisMode);
            for (int componentIndex = 0; componentIndex < componentLabels.Length; componentIndex++)
            {
                float sampleY = sampledComponents[componentIndex];
                sampledPoints[componentIndex].Add(new Vector2(sampleX, sampleY));
                minY = MathF.Min(minY, sampleY);
                maxY = MathF.Max(maxY, sampleY);
            }
        }

        if (!float.IsFinite(minY) || !float.IsFinite(maxY))
            return false;

        if (MathF.Abs(maxX - minX) < 0.0001f)
            maxX = minX + 1.0f;

        if (MathF.Abs(maxY - minY) < 0.0001f)
        {
            minY -= 1.0f;
            maxY += 1.0f;
        }

        for (int componentIndex = 0; componentIndex < componentLabels.Length; componentIndex++)
        {
            Vector4 color = _animationClipChannelPalette[componentIndex % _animationClipChannelPalette.Length];
            channels.Add(new AnimationClipGraphChannel(componentLabels[componentIndex], color, sampledPoints[componentIndex], keyframePoints[componentIndex]));
        }

        bounds = new AnimationClipGraphBounds(minX, maxX, minY, maxY);
        return true;
    }

    private static MethodInfo? ResolveAnimationClipGetValueMethod(Type animationType)
    {
        if (_animationClipGetValueMethodCache.TryGetValue(animationType, out MethodInfo? cached))
            return cached;

        MethodInfo? method = animationType.GetMethod("GetValue", BindingFlags.Public | BindingFlags.Instance, binder: null, types: [typeof(float)], modifiers: null);
        _animationClipGetValueMethodCache[animationType] = method;
        return method;
    }

    private static bool TryDecomposeAnimationClipValue(object? value, out string[] componentLabels, out float[] componentValues)
    {
        switch (value)
        {
            case null:
                componentLabels = Array.Empty<string>();
                componentValues = Array.Empty<float>();
                return false;
            case float scalar:
                componentLabels = ["Value"];
                componentValues = [scalar];
                return true;
            case double scalar:
                componentLabels = ["Value"];
                componentValues = [(float)scalar];
                return true;
            case int scalar:
                componentLabels = ["Value"];
                componentValues = [scalar];
                return true;
            case long scalar:
                componentLabels = ["Value"];
                componentValues = [scalar];
                return true;
            case short scalar:
                componentLabels = ["Value"];
                componentValues = [scalar];
                return true;
            case byte scalar:
                componentLabels = ["Value"];
                componentValues = [scalar];
                return true;
            case bool boolean:
                componentLabels = ["Value"];
                componentValues = [boolean ? 1.0f : 0.0f];
                return true;
            case Vector2 vector2:
                componentLabels = ["X", "Y"];
                componentValues = [vector2.X, vector2.Y];
                return true;
            case Vector3 vector3:
                componentLabels = ["X", "Y", "Z"];
                componentValues = [vector3.X, vector3.Y, vector3.Z];
                return true;
            case Vector4 vector4:
                componentLabels = ["X", "Y", "Z", "W"];
                componentValues = [vector4.X, vector4.Y, vector4.Z, vector4.W];
                return true;
            case Quaternion quaternion:
                componentLabels = ["X", "Y", "Z", "W"];
                componentValues = [quaternion.X, quaternion.Y, quaternion.Z, quaternion.W];
                return true;
            default:
                componentLabels = Array.Empty<string>();
                componentValues = Array.Empty<float>();
                return false;
        }
    }

    private static float ConvertAnimationClipXUnit(float second, AnimationClip clip, AnimationClipGraphXAxisMode mode, int authoredFrameIndex = -1)
    {
        if (mode == AnimationClipGraphXAxisMode.Frames && clip.SampleRate > 0)
        {
            if (authoredFrameIndex >= 0)
                return authoredFrameIndex;

            return second * clip.SampleRate;
        }

        return second;
    }

    private static void FrameAnimationClipGraph(AnimationClipGraphBounds bounds, Vector2 canvasSize, AnimationClipEditorState state)
    {
        const float leftMargin = 60.0f;
        const float rightMargin = 20.0f;
        const float topMargin = 16.0f;
        const float bottomMargin = 34.0f;

        float usableWidth = MathF.Max(80.0f, canvasSize.X - leftMargin - rightMargin);
        float usableHeight = MathF.Max(80.0f, canvasSize.Y - topMargin - bottomMargin);
        float rangeX = MathF.Max(0.001f, bounds.MaxX - bounds.MinX);
        float rangeY = MathF.Max(0.001f, bounds.MaxY - bounds.MinY);

        state.GraphZoom = new Vector2(usableWidth / rangeX, usableHeight / rangeY);
        state.GraphOffset = new Vector2(-bounds.MinX * state.GraphZoom.X, bounds.MinY * state.GraphZoom.Y);
    }

    private static void HandleAnimationClipGraphInput(AnimationClipEditorState state, AnimationClipGraphBounds bounds, Vector2 canvasPos, Vector2 canvasSize, bool hovered, bool active)
    {
        if (!hovered)
            return;

        if ((ImGui.IsMouseDragging(ImGuiMouseButton.Right) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle)) && active)
            state.GraphOffset += ImGui.GetIO().MouseDelta;

        float wheel = ImGui.GetIO().MouseWheel;
        if (MathF.Abs(wheel) <= 0.0001f)
            return;

        const float leftMargin = 60.0f;
        const float bottomMargin = 34.0f;
        Vector2 basePos = new(canvasPos.X + leftMargin, canvasPos.Y + canvasSize.Y - bottomMargin);
        Vector2 mouse = ImGui.GetMousePos();
        float worldX = (mouse.X - basePos.X - state.GraphOffset.X) / MathF.Max(0.001f, state.GraphZoom.X);
        float worldY = (basePos.Y + state.GraphOffset.Y - mouse.Y) / MathF.Max(0.001f, state.GraphZoom.Y);
        float zoomFactor = MathF.Pow(1.12f, wheel);

        if (ImGui.GetIO().KeyCtrl)
        {
            state.GraphZoom.Y = Math.Clamp(state.GraphZoom.Y * zoomFactor, 8.0f, 2000.0f);
            state.GraphOffset.Y = (mouse.Y - basePos.Y) + (worldY * state.GraphZoom.Y);
        }
        else
        {
            state.GraphZoom.X = Math.Clamp(state.GraphZoom.X * zoomFactor, 12.0f / MathF.Max(1.0f, bounds.MaxX - bounds.MinX), 2000.0f);
            state.GraphOffset.X = (mouse.X - basePos.X) - (worldX * state.GraphZoom.X);
        }
    }

    private static void DrawAnimationClipGraphCanvas(AnimationClip clip, AnimationClipEditorState state, List<AnimationClipGraphChannel> channels, AnimationClipGraphBounds bounds, Vector2 canvasPos, Vector2 canvasSize, bool hovered)
    {
        const float leftMargin = 60.0f;
        const float rightMargin = 20.0f;
        const float topMargin = 16.0f;
        const float bottomMargin = 34.0f;

        Vector2 min = canvasPos;
        Vector2 max = canvasPos + canvasSize;
        Vector2 basePos = new(canvasPos.X + leftMargin, canvasPos.Y + canvasSize.Y - bottomMargin);
        Vector2 plotMin = new(canvasPos.X + leftMargin, canvasPos.Y + topMargin);
        Vector2 plotMax = new(canvasPos.X + canvasSize.X - rightMargin, canvasPos.Y + canvasSize.Y - bottomMargin);

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.08f, 0.09f, 0.11f, 1.0f)), 4.0f);
        drawList.AddRect(plotMin, plotMax, ImGui.GetColorU32(new Vector4(0.22f, 0.24f, 0.30f, 1.0f)));

        DrawAnimationClipGraphGrid(drawList, clip, state, bounds, canvasPos, canvasSize, plotMin, plotMax);

        AnimationClipGraphKeyframe? hoveredKeyframe = null;
        string? hoveredChannelLabel = null;
        float hoveredDistance = float.MaxValue;
        Vector2 mouse = ImGui.GetMousePos();

        for (int channelIndex = 0; channelIndex < channels.Count; channelIndex++)
        {
            AnimationClipGraphChannel channel = channels[channelIndex];
            uint color = ImGui.GetColorU32(channel.Color);

            if (channel.Samples.Count >= 2)
            {
                for (int sampleIndex = 0; sampleIndex < channel.Samples.Count - 1; sampleIndex++)
                {
                    Vector2 first = GraphWorldToScreen(basePos, state, channel.Samples[sampleIndex]);
                    Vector2 second = GraphWorldToScreen(basePos, state, channel.Samples[sampleIndex + 1]);
                    drawList.AddLine(first, second, color, 2.0f);
                }
            }

            foreach (AnimationClipGraphKeyframe keyframe in channel.Keyframes)
            {
                Vector2 point = GraphWorldToScreen(basePos, state, new Vector2(keyframe.X, keyframe.Y));
                drawList.AddCircleFilled(point, 4.0f, color);
                drawList.AddCircle(point, 5.0f, ImGui.GetColorU32(new Vector4(0.05f, 0.05f, 0.06f, 1.0f)), 0, 1.0f);

                if (!hovered)
                    continue;

                float dx = mouse.X - point.X;
                float dy = mouse.Y - point.Y;
                float distSq = (dx * dx) + (dy * dy);
                if (distSq <= 81.0f && distSq < hoveredDistance)
                {
                    hoveredDistance = distSq;
                    hoveredKeyframe = keyframe;
                    hoveredChannelLabel = channel.Label;
                }
            }
        }

        float legendX = plotMin.X + 10.0f;
        float legendY = plotMin.Y + 8.0f;
        foreach (AnimationClipGraphChannel channel in channels)
        {
            uint color = ImGui.GetColorU32(channel.Color);
            drawList.AddRectFilled(new Vector2(legendX, legendY + 4.0f), new Vector2(legendX + 12.0f, legendY + 16.0f), color, 2.0f);
            drawList.AddText(new Vector2(legendX + 18.0f, legendY), ImGui.GetColorU32(ImGuiCol.Text), channel.Label);
            legendY += 20.0f;
        }

        if (hovered && hoveredKeyframe is not null)
        {
            Keyframe sourceKeyframe = hoveredKeyframe.Value.Keyframe;
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(hoveredChannelLabel ?? "Channel");
            if (state.XAxisMode == AnimationClipGraphXAxisMode.Frames && clip.SampleRate > 0)
            {
                ImGui.TextDisabled($"Frame: {hoveredKeyframe.Value.X:0.###}");
                ImGui.TextDisabled($"Time: {sourceKeyframe.Second:0.###} s");
            }
            else
            {
                ImGui.TextDisabled($"Time: {hoveredKeyframe.Value.X:0.###} s");
                if (sourceKeyframe.AuthoredFrameIndex >= 0)
                    ImGui.TextDisabled($"Frame: {sourceKeyframe.AuthoredFrameIndex}");
            }
            ImGui.TextDisabled($"Value: {hoveredKeyframe.Value.Y:0.###}");
            ImGui.EndTooltip();
        }
    }

    private static void DrawAnimationClipGraphGrid(ImDrawListPtr drawList, AnimationClip clip, AnimationClipEditorState state, AnimationClipGraphBounds bounds, Vector2 canvasPos, Vector2 canvasSize, Vector2 plotMin, Vector2 plotMax)
    {
        const float leftMargin = 60.0f;
        const float bottomMargin = 34.0f;
        Vector2 basePos = new(canvasPos.X + leftMargin, canvasPos.Y + canvasSize.Y - bottomMargin);
        uint gridColor = ImGui.GetColorU32(new Vector4(0.22f, 0.24f, 0.30f, 1.0f));
        uint axisColor = ImGui.GetColorU32(new Vector4(0.52f, 0.56f, 0.64f, 1.0f));
        uint textColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);

        float xStep = GetAnimationClipGraphGridStep(state.GraphZoom.X);
        int xLineCount = Math.Max(1, (int)MathF.Ceiling((bounds.MaxX - bounds.MinX) / xStep));
        for (int index = 0; index <= xLineCount; index++)
        {
            float xValue = bounds.MinX + (index * xStep);
            if (xValue > bounds.MaxX && index > 0)
                xValue = bounds.MaxX;

            Vector2 start = GraphWorldToScreen(basePos, state, new Vector2(xValue, bounds.MinY));
            Vector2 end = GraphWorldToScreen(basePos, state, new Vector2(xValue, bounds.MaxY));
            drawList.AddLine(new Vector2(start.X, plotMin.Y), new Vector2(end.X, plotMax.Y), gridColor, 1.0f);
            drawList.AddText(new Vector2(start.X + 4.0f, plotMax.Y + 4.0f), textColor, xValue.ToString("0.##", CultureInfo.InvariantCulture));

            if (xValue >= bounds.MaxX)
                break;
        }

        float yStep = GetAnimationClipGraphGridStep(state.GraphZoom.Y);
        int yLineCount = Math.Max(1, (int)MathF.Ceiling((bounds.MaxY - bounds.MinY) / yStep));
        for (int index = 0; index <= yLineCount; index++)
        {
            float yValue = bounds.MinY + (index * yStep);
            if (yValue > bounds.MaxY && index > 0)
                yValue = bounds.MaxY;

            Vector2 left = GraphWorldToScreen(basePos, state, new Vector2(bounds.MinX, yValue));
            Vector2 right = GraphWorldToScreen(basePos, state, new Vector2(bounds.MaxX, yValue));
            drawList.AddLine(new Vector2(plotMin.X, left.Y), new Vector2(plotMax.X, right.Y), gridColor, 1.0f);
            drawList.AddText(new Vector2(canvasPos.X + 6.0f, left.Y - 8.0f), textColor, yValue.ToString("0.##", CultureInfo.InvariantCulture));

            if (yValue >= bounds.MaxY)
                break;
        }

        Vector2 xAxisLeft = GraphWorldToScreen(basePos, state, new Vector2(bounds.MinX, 0.0f));
        Vector2 xAxisRight = GraphWorldToScreen(basePos, state, new Vector2(bounds.MaxX, 0.0f));
        if (xAxisLeft.Y >= plotMin.Y && xAxisLeft.Y <= plotMax.Y)
            drawList.AddLine(new Vector2(plotMin.X, xAxisLeft.Y), new Vector2(plotMax.X, xAxisRight.Y), axisColor, 1.4f);

        Vector2 yAxisTop = GraphWorldToScreen(basePos, state, new Vector2(0.0f, bounds.MaxY));
        Vector2 yAxisBottom = GraphWorldToScreen(basePos, state, new Vector2(0.0f, bounds.MinY));
        if (yAxisTop.X >= plotMin.X && yAxisTop.X <= plotMax.X)
            drawList.AddLine(new Vector2(yAxisTop.X, plotMin.Y), new Vector2(yAxisBottom.X, plotMax.Y), axisColor, 1.4f);
    }

    private static Vector2 GraphWorldToScreen(Vector2 basePos, AnimationClipEditorState state, Vector2 world)
        => new(
            basePos.X + state.GraphOffset.X + (world.X * state.GraphZoom.X),
            basePos.Y + state.GraphOffset.Y - (world.Y * state.GraphZoom.Y));

    private static float GetAnimationClipGraphGridStep(float pixelsPerUnit)
    {
        float[] candidateSteps = [0.01f, 0.05f, 0.1f, 0.25f, 0.5f, 1.0f, 2.0f, 5.0f, 10.0f, 25.0f, 50.0f];
        foreach (float candidate in candidateSteps)
        {
            if (candidate * pixelsPerUnit >= 64.0f)
                return candidate;
        }

        return 100.0f;
    }

    private enum AnimationClipEditorViewMode
    {
        Lanes,
        Graph
    }

    private enum AnimationClipGraphXAxisMode
    {
        Seconds,
        Frames
    }

    private sealed class AnimationClipEditorState
    {
        public string? SelectedMemberId;
        public AnimationClipEditorViewMode ViewMode = AnimationClipEditorViewMode.Lanes;
        public AnimationClipGraphXAxisMode XAxisMode = AnimationClipGraphXAxisMode.Seconds;
        public float LanePixelsPerSecond = 120.0f;
        public Vector2 GraphZoom = new(120.0f, 80.0f);
        public Vector2 GraphOffset = new(0.0f, 0.0f);
        public string? LastGraphedMemberId;
        public bool PendingGraphAutoFrame = true;
    }

    private readonly record struct AnimationClipMemberRow(AnimationMember Member, string Id, string DisplayPath, int Depth);
    private readonly record struct AnimationClipGraphBounds(float MinX, float MaxX, float MinY, float MaxY);
    private sealed class AnimationClipGraphChannel(string label, Vector4 color, List<Vector2> samples, List<AnimationClipGraphKeyframe> keyframes)
    {
        public string Label { get; } = label;
        public Vector4 Color { get; } = color;
        public List<Vector2> Samples { get; } = samples;
        public List<AnimationClipGraphKeyframe> Keyframes { get; } = keyframes;
    }

    private readonly record struct AnimationClipGraphKeyframe(float X, float Y, Keyframe Keyframe);
}