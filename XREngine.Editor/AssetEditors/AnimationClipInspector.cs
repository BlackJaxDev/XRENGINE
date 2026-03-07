using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using ImGuiNET;
using XREngine.Animation;
using XREngine.Core.Files;

namespace XREngine.Editor.AssetEditors;

public sealed class AnimationClipInspector : IXRAssetInspector
{
    private readonly ConditionalWeakTable<AnimationClip, EditorState> _stateCache = new();

    public void DrawInspector(EditorImGuiUI.InspectorTargetSet targets, HashSet<object> visitedObjects)
    {
        var clips = targets.Targets.OfType<AnimationClip>().Cast<object>().ToList();
        if (clips.Count == 0)
        {
            foreach (var asset in targets.Targets.OfType<XRAsset>())
                EditorImGuiUI.DrawDefaultAssetInspector(asset, visitedObjects);
            return;
        }

        if (targets.HasMultipleTargets)
        {
            EditorImGuiUI.DrawDefaultAssetInspector(new EditorImGuiUI.InspectorTargetSet(clips, targets.CommonType), visitedObjects);
            return;
        }

        var clip = (AnimationClip)clips[0];
        var state = _stateCache.GetValue(clip, static _ => new EditorState());

        EnsureSelectedMember(clip, state);

        DrawHeader(clip);
        ImGui.Separator();
        DrawClipSummary(clip);
        ImGui.Separator();
        DrawMemberBrowser(clip, state, visitedObjects);
    }

    private static void DrawHeader(AnimationClip clip)
    {
        string displayName = !string.IsNullOrWhiteSpace(clip.Name)
            ? clip.Name!
            : (!string.IsNullOrWhiteSpace(clip.FilePath)
                ? Path.GetFileNameWithoutExtension(clip.FilePath)
                : "Animation Clip");

        ImGui.TextUnformatted(displayName);

        string path = clip.FilePath ?? "<unsaved asset>";
        ImGui.TextDisabled(path);
        if (!string.IsNullOrWhiteSpace(clip.FilePath))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy Path##AnimationClip"))
                ImGui.SetClipboardText(clip.FilePath);
        }
    }

    private static void DrawClipSummary(AnimationClip clip)
    {
        if (!ImGui.CollapsingHeader("Clip Summary", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var stats = CountMembers(clip.RootMember);

        if (ImGui.BeginTable("AnimationClipSummary", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            DrawSummaryRow("Traversal", clip.TraversalMethod.ToString());
            DrawSummaryRow("Kind", clip.ClipKind.ToString());
            DrawSummaryRow("Length", $"{clip.LengthInSeconds:0.###} s");
            DrawSummaryRow("Looped", clip.Looped ? "Yes" : "No");
            DrawSummaryRow("Sample Rate", clip.SampleRate.ToString());
            DrawSummaryRow("Members", stats.TotalCount.ToString());
            DrawSummaryRow("Animated Members", stats.AnimatedCount.ToString());
            DrawSummaryRow("Method Members", stats.MethodCount.ToString());
            DrawSummaryRow("Root Motion", clip.HasRootMotion ? "Present" : "None");
            DrawSummaryRow("Muscle Channels", clip.HasMuscleChannels ? "Present" : "None");
            DrawSummaryRow("IK Goals", clip.HasIKGoals ? "Present" : "None");
            ImGui.EndTable();
        }

        if (clip.RootMember is null)
            ImGui.TextDisabled("This clip does not currently have a root animation member.");
    }

    private static void DrawSummaryRow(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(label);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(value);
    }

    private static void DrawMemberBrowser(AnimationClip clip, EditorState state, HashSet<object> visitedObjects)
    {
        ImGui.TextUnformatted("Member Tree");

        Vector2 available = ImGui.GetContentRegionAvail();
        float panelHeight = Math.Max(280.0f, available.Y);

        if (ImGui.BeginChild("AnimationClipMemberTree", new Vector2(360.0f, panelHeight), ImGuiChildFlags.Border | ImGuiChildFlags.ResizeX))
        {
            if (clip.RootMember is null)
            {
                ImGui.TextDisabled("No members available.");
            }
            else
            {
                DrawMemberTreeNode(clip.RootMember, state, "0", clip.RootMember.MemberName);
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();

        if (ImGui.BeginChild("AnimationClipMemberDetails", Vector2.Zero, ImGuiChildFlags.Border))
        {
            if (clip.RootMember is null)
            {
                ImGui.TextDisabled("Select or import animation members to inspect them here.");
            }
            else if (TryFindSelectedMember(clip.RootMember, state.SelectedMemberId, "0", clip.RootMember.MemberName, out var selectedMember, out var displayPath))
            {
                DrawSelectedMemberDetails(selectedMember, displayPath, visitedObjects);
            }
            else
            {
                ImGui.TextDisabled("Select a member from the tree to inspect its properties.");
            }
        }
        ImGui.EndChild();
    }

    private static void DrawMemberTreeNode(AnimationMember member, EditorState state, string memberId, string? displayPath)
    {
        bool hasChildren = member.Children.Count > 0;
        ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.DefaultOpen;
        if (!hasChildren)
            flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;

        if (string.Equals(state.SelectedMemberId, memberId, StringComparison.Ordinal))
            flags |= ImGuiTreeNodeFlags.Selected;

        string label = BuildTreeLabel(member);
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

        if (hasChildren && open)
        {
            for (int i = 0; i < member.Children.Count; i++)
            {
                AnimationMember child = member.Children[i];
                string childId = $"{memberId}/{i}";
                string childPath = AppendDisplayPath(displayPath, child.MemberName);
                DrawMemberTreeNode(child, state, childId, childPath);
            }

            ImGui.TreePop();
        }
    }

    private static void DrawSelectedMemberDetails(AnimationMember member, string displayPath, HashSet<object> visitedObjects)
    {
        string displayName = string.IsNullOrWhiteSpace(member.MemberName) ? "<unnamed member>" : member.MemberName;
        ImGui.TextUnformatted(displayName);
        ImGui.TextDisabled(displayPath);
        ImGui.TextDisabled(BuildMemberActionSummary(member));

        if (member.MemberNotFound)
        {
            Vector4 warning = new(0.95f, 0.45f, 0.45f, 1.0f);
            ImGui.TextColored(warning, "This member was not resolved at runtime.");
        }

        ImGui.Separator();
        EditorImGuiUI.DrawRuntimeObjectInspector("Member Properties", member, visitedObjects, defaultOpen: true);

        if (member.MemberType == EAnimationMemberType.Method)
        {
            ImGui.Separator();
            DrawMethodArgumentSummary(member);
        }

        ImGui.Separator();
        if (member.Animation is null)
        {
            ImGui.TextDisabled("No animation asset is attached to this member.");
        }
        else
        {
            EditorImGuiUI.DrawRuntimeObjectInspector("Attached Animation", member.Animation, visitedObjects, defaultOpen: true);
        }
    }

    private static void DrawMethodArgumentSummary(AnimationMember member)
    {
        if (!ImGui.CollapsingHeader("Method Arguments", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        object?[] args = member.MethodArguments ?? Array.Empty<object?>();
        if (args.Length == 0)
        {
            ImGui.TextDisabled("This method member has no arguments configured.");
            return;
        }

        if (!ImGui.BeginTable("AnimationClipMethodArgs", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("Index", ImGuiTableColumnFlags.WidthFixed, 48.0f);
        ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, 120.0f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        for (int i = 0; i < args.Length; i++)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(i.ToString());

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(i == member.AnimatedMethodArgumentIndex ? "Animated" : "Static");

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(FormatValue(args[i]));
        }

        ImGui.EndTable();
    }

    private static void EnsureSelectedMember(AnimationClip clip, EditorState state)
    {
        if (clip.RootMember is null)
        {
            state.SelectedMemberId = null;
            return;
        }

        if (!string.IsNullOrWhiteSpace(state.SelectedMemberId)
            && TryFindSelectedMember(clip.RootMember, state.SelectedMemberId, "0", clip.RootMember.MemberName, out _, out _))
        {
            return;
        }

        state.SelectedMemberId = "0";
    }

    private static bool TryFindSelectedMember(
        AnimationMember member,
        string? selectedId,
        string currentId,
        string? currentPath,
        out AnimationMember selectedMember,
        out string displayPath)
    {
        if (string.Equals(selectedId, currentId, StringComparison.Ordinal))
        {
            selectedMember = member;
            displayPath = currentPath ?? string.Empty;
            return true;
        }

        for (int i = 0; i < member.Children.Count; i++)
        {
            AnimationMember child = member.Children[i];
            string childId = $"{currentId}/{i}";
            string childPath = AppendDisplayPath(currentPath, child.MemberName);
            if (TryFindSelectedMember(child, selectedId, childId, childPath, out selectedMember, out displayPath))
                return true;
        }

        selectedMember = null!;
        displayPath = string.Empty;
        return false;
    }

    private static string AppendDisplayPath(string? parentPath, string? memberName)
    {
        string segment = string.IsNullOrWhiteSpace(memberName) ? "<unnamed member>" : memberName;
        return string.IsNullOrWhiteSpace(parentPath) ? segment : $"{parentPath}.{segment}";
    }

    private static string BuildTreeLabel(AnimationMember member)
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

    private static string BuildMemberActionSummary(AnimationMember member)
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

    private static string FormatValue(object? value)
        => value switch
        {
            null => "<null>",
            string text when string.IsNullOrWhiteSpace(text) => "<empty>",
            string text => text,
            _ => value.ToString() ?? value.GetType().Name
        };

    private static MemberStats CountMembers(AnimationMember? member)
    {
        if (member is null)
            return default;

        MemberStats stats = new()
        {
            TotalCount = 1,
            AnimatedCount = member.Animation is null ? 0 : 1,
            MethodCount = member.MemberType == EAnimationMemberType.Method ? 1 : 0
        };

        foreach (AnimationMember child in member.Children)
        {
            MemberStats childStats = CountMembers(child);
            stats.TotalCount += childStats.TotalCount;
            stats.AnimatedCount += childStats.AnimatedCount;
            stats.MethodCount += childStats.MethodCount;
        }

        return stats;
    }

    private sealed class EditorState
    {
        public string? SelectedMemberId;
    }

    private struct MemberStats
    {
        public int TotalCount;
        public int AnimatedCount;
        public int MethodCount;
    }
}