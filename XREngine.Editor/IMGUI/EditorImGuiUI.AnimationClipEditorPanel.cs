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
using XREngine.Components;
using XREngine.Data.Animation;
using XREngine.Scene;
using XREngine.Scene.Transforms;

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

        DrawAnimationClipEditorClipSettings(clip, state);
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

    private static void DrawAnimationClipEditorClipSettings(AnimationClip clip, AnimationClipEditorState state)
    {
        ImGui.TextUnformatted("Clip Settings");

        float length = MathF.Max(0.0f, clip.LengthInSeconds);
        ImGui.SetNextItemWidth(140.0f);
        if (ImGui.DragFloat("Length (s)", ref length, 0.01f, 0.0f, 3600.0f, "%.3f"))
            SetAnimationClipLength(clip, MathF.Max(0.0f, length), state.StretchCurvesWhenChangingLength);

        ImGui.SameLine();
        int sampleRate = Math.Max(1, clip.SampleRate);
        ImGui.SetNextItemWidth(120.0f);
        if (ImGui.DragInt("FPS", ref sampleRate, 1.0f, 1, 960))
            clip.SampleRate = Math.Clamp(sampleRate, 1, 960);

        ImGui.SameLine();
        bool looped = clip.Looped;
        if (ImGui.Checkbox("Loop", ref looped))
        {
            clip.Looped = looped;
            foreach (BaseAnimation animation in EnumerateAnimationClipAnimations(clip.RootMember))
                animation.Looped = looped;
        }

        ImGui.SameLine();
        bool stretch = state.StretchCurvesWhenChangingLength;
        if (ImGui.Checkbox("Stretch Keys", ref stretch))
            state.StretchCurvesWhenChangingLength = stretch;

        DrawEnumCombo(nameof(AnimationClip.TraversalMethod), clip.TraversalMethod, value => clip.TraversalMethod = value, "Animation Clip Traversal", clip);
        ImGui.SameLine();
        DrawEnumCombo(nameof(AnimationClip.ClipKind), clip.ClipKind, value => clip.ClipKind = value, "Animation Clip Kind", clip);
    }

    private static void DrawAnimationClipEditorMemberSidebar(AnimationClip clip, AnimationClipEditorState state, List<AnimationClipMemberRow> visibleRows)
    {
        DrawAnimationClipScenePathAuthoring(clip, state);
        ImGui.Separator();

        ImGui.TextUnformatted("Animation Member Tree");
        ImGui.Separator();

        if (clip.RootMember is not null && !string.IsNullOrWhiteSpace(state.SelectedMemberId))
        {
            if (ImGui.SmallButton("Remove Selected Path"))
            {
                RemoveAnimationClipSelectedMember(clip, state);
                EnsureAnimationClipSelectedMember(clip, state);
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Normalize Root"))
            {
                EnsureAnimationClipRootGroup(clip);
                EnsureAnimationClipSelectedMember(clip, state);
            }
        }

        if (clip.RootMember is null)
        {
            ImGui.TextDisabled("This clip does not have a root animation member.");
            return;
        }

        DrawAnimationClipEditorMemberTreeNode(clip.RootMember, state, "0", clip.RootMember.MemberName, depth: 0, visibleRows);
    }

    private static void DrawAnimationClipScenePathAuthoring(AnimationClip clip, AnimationClipEditorState state)
    {
        ImGui.TextUnformatted("Scene Root");

        SceneNode[] selectedNodes = Selection.SceneNodes;
        SceneNode? firstSelectedNode = selectedNodes.Length > 0 ? selectedNodes[0] : null;
        string rootLabel = state.AuthoringRootNode is not null
            ? GetAnimationClipSceneNodeLabel(state.AuthoringRootNode)
            : "<no scene node>";

        if (ImGui.BeginCombo("Root Node", rootLabel))
        {
            if (firstSelectedNode is not null)
            {
                for (int index = 0; index < selectedNodes.Length; index++)
                {
                    SceneNode node = selectedNodes[index];
                    bool selected = ReferenceEquals(node, state.AuthoringRootNode);
                    if (ImGui.Selectable($"{GetAnimationClipSceneNodeLabel(node)}##AnimationClipSceneSelection{index}", selected))
                        SetAnimationClipAuthoringRoot(state, node);
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
            }
            else
            {
                ImGui.TextDisabled("Select a node in the hierarchy to populate this list.");
            }
            ImGui.EndCombo();
        }

        if (firstSelectedNode is not null)
        {
            if (ImGui.SmallButton("Use Scene Selection"))
                SetAnimationClipAuthoringRoot(state, firstSelectedNode);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Rescan"))
            RebuildAnimationClipPathCandidates(state);

        ImGui.SetNextItemWidth(-1.0f);
        if (ImGui.InputText("Search Paths", ref state.CandidateSearch, 128))
            state.SelectedCandidateId = null;

        EnsureAnimationClipCandidateCache(state);

        if (state.AuthoringRootNode is null)
        {
            ImGui.TextDisabled("Choose a scene node to list animatable paths.");
            return;
        }

        List<AnimationClipPathCandidate> candidates = state.CandidateCache;
        string filter = state.CandidateSearch.Trim();
        int visibleCount = 0;

        if (!ImGui.BeginChild("AnimationClipPathCandidates", new Vector2(0.0f, 190.0f), ImGuiChildFlags.Border))
            return;

        for (int index = 0; index < candidates.Count; index++)
        {
            AnimationClipPathCandidate candidate = candidates[index];
            if (!string.IsNullOrWhiteSpace(filter)
                && candidate.DisplayPath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                && candidate.TargetTypeName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            visibleCount++;
            bool selected = string.Equals(state.SelectedCandidateId, candidate.Id, StringComparison.Ordinal);
            string label = $"{candidate.DisplayPath}  [{candidate.ValueType.Name}]##Candidate{index}";
            if (ImGui.Selectable(label, selected, ImGuiSelectableFlags.AllowDoubleClick))
            {
                state.SelectedCandidateId = candidate.Id;
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    AddAnimationClipCandidatePath(clip, state, candidate);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(candidate.DisplayPath);
                ImGui.TextDisabled(candidate.TargetTypeName);
                ImGui.TextDisabled($"{candidate.LeafMemberType} {candidate.ValueType.Name}");
                if (candidate.DefaultValue is not null)
                    ImGui.TextDisabled($"Default: {FormatAnimationClipValue(candidate.DefaultValue)}");
                ImGui.EndTooltip();
            }
        }

        if (visibleCount == 0)
            ImGui.TextDisabled("No matching animatable paths.");

        ImGui.EndChild();

        AnimationClipPathCandidate? selectedCandidate = candidates.FirstOrDefault(c => string.Equals(c.Id, state.SelectedCandidateId, StringComparison.Ordinal));
        bool canAdd = selectedCandidate is not null;
        if (!canAdd)
            ImGui.BeginDisabled();
        if (ImGui.Button("Add Path") && selectedCandidate is AnimationClipPathCandidate candidateToAdd)
            AddAnimationClipCandidatePath(clip, state, candidateToAdd);
        if (!canAdd)
            ImGui.EndDisabled();
    }

    private static string GetAnimationClipSceneNodeLabel(SceneNode node)
        => string.IsNullOrWhiteSpace(node.Name) ? SceneNode.DefaultName : node.Name!;

    private static void SetAnimationClipAuthoringRoot(AnimationClipEditorState state, SceneNode node)
    {
        if (ReferenceEquals(state.AuthoringRootNode, node))
            return;

        state.AuthoringRootNode = node;
        state.SelectedCandidateId = null;
        RebuildAnimationClipPathCandidates(state);
    }

    private static void EnsureAnimationClipCandidateCache(AnimationClipEditorState state)
    {
        SceneNode? root = state.AuthoringRootNode;
        if (root is null)
        {
            state.CandidateCache.Clear();
            state.CandidateCacheRootNode = null;
            state.CandidateCacheComponentCount = -1;
            return;
        }

        int componentCount = root.GetComponents<XRComponent>().Count();
        if (!ReferenceEquals(state.CandidateCacheRootNode, root) || state.CandidateCacheComponentCount != componentCount)
            RebuildAnimationClipPathCandidates(state);
    }

    private static void RebuildAnimationClipPathCandidates(AnimationClipEditorState state)
    {
        state.CandidateCache.Clear();
        state.SelectedCandidateId = null;

        SceneNode? root = state.AuthoringRootNode;
        if (root is null)
        {
            state.CandidateCacheRootNode = null;
            state.CandidateCacheComponentCount = -1;
            return;
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        AddAnimationClipTargetPathCandidates(state.CandidateCache, ids, root, root.GetType(), "SceneNode", []);

        AnimationClipPathSegment transformSegment = new("Transform", EAnimationMemberType.Property, null, -1, false);
        AddAnimationClipTargetPathCandidates(
            state.CandidateCache,
            ids,
            root.Transform,
            root.Transform.GetType(),
            "Transform",
            [transformSegment]);

        int componentIndex = 0;
        foreach (XRComponent component in root.GetComponents<XRComponent>())
        {
            Type componentType = component.GetType();
            AnimationClipPathSegment componentSegment = new(
                "GetComponent",
                EAnimationMemberType.Method,
                [componentType.Name],
                -1,
                true);

            string displayPrefix = $"{componentType.Name}";
            if (!string.IsNullOrWhiteSpace(component.Name))
                displayPrefix += $" ({component.Name})";
            displayPrefix += $" #{componentIndex++}";

            AddAnimationClipTargetPathCandidates(
                state.CandidateCache,
                ids,
                component,
                componentType,
                displayPrefix,
                [componentSegment]);
        }

        state.CandidateCache.Sort(static (left, right) => string.Compare(left.DisplayPath, right.DisplayPath, StringComparison.OrdinalIgnoreCase));
        state.CandidateCacheRootNode = root;
        state.CandidateCacheComponentCount = root.GetComponents<XRComponent>().Count();
    }

    private static void AddAnimationClipTargetPathCandidates(
        List<AnimationClipPathCandidate> candidates,
        HashSet<string> ids,
        object target,
        Type targetType,
        string displayPrefix,
        IReadOnlyList<AnimationClipPathSegment> prefixSegments)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (PropertyInfo property in targetType.GetProperties(flags))
        {
            if (!IsAnimationClipAnimatableProperty(property, out Type? valueType))
                continue;

            object? defaultValue = TryGetAnimationClipPropertyValue(target, property);
            AddAnimationClipPathCandidate(
                candidates,
                ids,
                displayPrefix,
                prefixSegments,
                new AnimationClipPathSegment(property.Name, EAnimationMemberType.Property, null, -1, false),
                EAnimationMemberType.Property,
                valueType,
                defaultValue,
                targetType);
        }

        foreach (FieldInfo field in targetType.GetFields(flags))
        {
            if (!IsAnimationClipAnimatableField(field, out Type? valueType))
                continue;

            object? defaultValue = TryGetAnimationClipFieldValue(target, field);
            AddAnimationClipPathCandidate(
                candidates,
                ids,
                displayPrefix,
                prefixSegments,
                new AnimationClipPathSegment(field.Name, EAnimationMemberType.Field, null, -1, false),
                EAnimationMemberType.Field,
                valueType,
                defaultValue,
                targetType);
        }

        foreach (MethodInfo method in targetType.GetMethods(flags))
        {
            if (!IsAnimationClipCandidateMethod(method))
                continue;

            ParameterInfo[] parameters = method.GetParameters();
            object?[] methodArgs = new object?[parameters.Length];
            bool defaultsAvailable = true;
            for (int paramIndex = 0; paramIndex < parameters.Length; paramIndex++)
            {
                ParameterInfo parameter = parameters[paramIndex];
                Type parameterType = UnwrapAnimationClipValueType(parameter.ParameterType);
                methodArgs[paramIndex] = parameter.HasDefaultValue && parameter.DefaultValue is not null
                    ? parameter.DefaultValue
                    : CreateAnimationClipDefaultValue(parameterType);

                if (methodArgs[paramIndex] is null)
                {
                    defaultsAvailable = false;
                    break;
                }
            }

            if (!defaultsAvailable)
                continue;

            for (int animatedIndex = 0; animatedIndex < parameters.Length; animatedIndex++)
            {
                Type parameterType = UnwrapAnimationClipValueType(parameters[animatedIndex].ParameterType);
                if (!IsAnimationClipSupportedValueType(parameterType))
                    continue;

                object?[] argsForCandidate = [.. methodArgs];
                object? defaultValue = CreateAnimationClipDefaultValue(parameterType);
                argsForCandidate[animatedIndex] = defaultValue;

                string parameterName = string.IsNullOrWhiteSpace(parameters[animatedIndex].Name)
                    ? $"arg{animatedIndex}"
                    : parameters[animatedIndex].Name!;

                AddAnimationClipPathCandidate(
                    candidates,
                    ids,
                    displayPrefix,
                    prefixSegments,
                    new AnimationClipPathSegment(method.Name, EAnimationMemberType.Method, argsForCandidate, animatedIndex, false),
                    EAnimationMemberType.Method,
                    parameterType,
                    defaultValue,
                    targetType,
                    $"({parameterName})");
            }
        }
    }

    private static void AddAnimationClipPathCandidate(
        List<AnimationClipPathCandidate> candidates,
        HashSet<string> ids,
        string displayPrefix,
        IReadOnlyList<AnimationClipPathSegment> prefixSegments,
        AnimationClipPathSegment leafSegment,
        EAnimationMemberType leafType,
        Type valueType,
        object? defaultValue,
        Type targetType,
        string? suffix = null)
    {
        List<AnimationClipPathSegment> segments = new(prefixSegments.Count + 1);
        segments.AddRange(prefixSegments);
        segments.Add(leafSegment);

        string path = $"{displayPrefix}.{leafSegment.MemberName}{suffix}";
        string id = string.Join("/", segments.Select(segment => segment.IdPart));
        if (!ids.Add(id))
            return;

        candidates.Add(new AnimationClipPathCandidate(
            id,
            path,
            targetType.Name,
            [.. segments],
            leafType,
            valueType,
            defaultValue));
    }

    private static bool IsAnimationClipAnimatableProperty(PropertyInfo property, out Type valueType)
    {
        valueType = UnwrapAnimationClipValueType(property.PropertyType);
        if (!IsAnimationClipSupportedValueType(valueType))
            return false;
        if (property.GetIndexParameters().Length != 0)
            return false;
        if (property.SetMethod is null && property.GetSetMethod(true) is null)
            return false;
        if (property.GetMethod is null && property.GetGetMethod(true) is null)
            return false;
        if (property.GetMethod?.IsStatic == true || property.SetMethod?.IsStatic == true)
            return false;
        return true;
    }

    private static bool IsAnimationClipAnimatableField(FieldInfo field, out Type valueType)
    {
        valueType = UnwrapAnimationClipValueType(field.FieldType);
        if (!IsAnimationClipSupportedValueType(valueType))
            return false;
        if (field.IsInitOnly || field.IsLiteral || field.IsStatic)
            return false;
        if (field.Name.Contains('<', StringComparison.Ordinal))
            return false;
        return true;
    }

    private static bool IsAnimationClipCandidateMethod(MethodInfo method)
    {
        if (method.IsStatic || method.IsSpecialName || method.IsGenericMethodDefinition || method.ContainsGenericParameters)
            return false;
        if (method.DeclaringType == typeof(object))
            return false;

        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length == 0)
            return false;

        return parameters.Any(parameter => IsAnimationClipSupportedValueType(UnwrapAnimationClipValueType(parameter.ParameterType)));
    }

    private static Type UnwrapAnimationClipValueType(Type type)
    {
        if (type.IsByRef || type.IsPointer)
            type = type.GetElementType() ?? type;
        return type;
    }

    private static bool IsAnimationClipSupportedValueType(Type type)
        => type == typeof(float)
            || type == typeof(bool)
            || type == typeof(Vector2)
            || type == typeof(Vector3)
            || type == typeof(Vector4)
            || type == typeof(Quaternion);

    private static object? TryGetAnimationClipPropertyValue(object target, PropertyInfo property)
    {
        try
        {
            return property.GetValue(target);
        }
        catch
        {
            return CreateAnimationClipDefaultValue(UnwrapAnimationClipValueType(property.PropertyType));
        }
    }

    private static object? TryGetAnimationClipFieldValue(object target, FieldInfo field)
    {
        try
        {
            return field.GetValue(target);
        }
        catch
        {
            return CreateAnimationClipDefaultValue(UnwrapAnimationClipValueType(field.FieldType));
        }
    }

    private static void AddAnimationClipCandidatePath(AnimationClip clip, AnimationClipEditorState state, AnimationClipPathCandidate candidate)
    {
        AnimationMember root = EnsureAnimationClipRootGroup(clip);
        AnimationMember current = root;

        foreach (AnimationClipPathSegment segment in candidate.Segments)
        {
            AnimationMember? existing = current.Children.FirstOrDefault(child => AnimationClipPathSegmentMatches(child, segment));
            if (existing is null)
            {
                existing = CreateAnimationClipMember(segment);
                current.Children.Add(existing);
            }

            current = existing;
        }

        current.Animation ??= CreateAnimationClipAnimation(candidate.ValueType, clip, candidate.DefaultValue);
        current.DefaultValue = candidate.DefaultValue ?? CreateAnimationClipDefaultValue(candidate.ValueType);
        current.Animation.LengthInSeconds = clip.LengthInSeconds;
        current.Animation.Looped = clip.Looped;

        SyncAnimationClipStats(clip);
        if (TryFindAnimationClipMemberId(clip.RootMember, current, "0", out string? id))
            state.SelectedMemberId = id;
        state.SelectedKeyframe = null;
        state.SelectedKeyframeMemberId = null;
    }

    private static AnimationMember EnsureAnimationClipRootGroup(AnimationClip clip)
    {
        if (clip.RootMember is null)
        {
            clip.RootMember = new AnimationMember("Root", EAnimationMemberType.Group);
            return clip.RootMember;
        }

        if (clip.RootMember.MemberType == EAnimationMemberType.Group)
            return clip.RootMember;

        AnimationMember previousRoot = clip.RootMember;
        AnimationMember group = new("Root", EAnimationMemberType.Group);
        group.Children.Add(previousRoot);
        clip.RootMember = group;
        return group;
    }

    private static AnimationMember CreateAnimationClipMember(AnimationClipPathSegment segment)
    {
        AnimationMember member = new(segment.MemberName, segment.MemberType)
        {
            CacheReturnValue = segment.CacheReturnValue
        };

        if (segment.MemberType == EAnimationMemberType.Method)
        {
            member.MethodArguments = segment.MethodArguments is null ? [] : [.. segment.MethodArguments];
            member.AnimatedMethodArgumentIndex = segment.AnimatedMethodArgumentIndex;
        }

        return member;
    }

    private static bool AnimationClipPathSegmentMatches(AnimationMember member, AnimationClipPathSegment segment)
    {
        if (!string.Equals(member.MemberName, segment.MemberName, StringComparison.Ordinal)
            || member.MemberType != segment.MemberType
            || member.CacheReturnValue != segment.CacheReturnValue)
        {
            return false;
        }

        if (segment.MemberType != EAnimationMemberType.Method)
            return true;

        if (member.AnimatedMethodArgumentIndex != segment.AnimatedMethodArgumentIndex)
            return false;

        object?[] left = member.MethodArguments ?? [];
        object?[] right = segment.MethodArguments ?? [];
        if (left.Length != right.Length)
            return false;

        for (int index = 0; index < left.Length; index++)
        {
            if (!Equals(left[index], right[index]))
                return false;
        }

        return true;
    }

    private static BasePropAnim CreateAnimationClipAnimation(Type valueType, AnimationClip clip, object? defaultValue)
    {
        BasePropAnim animation = valueType == typeof(float)
            ? new PropAnimFloat(clip.LengthInSeconds, clip.Looped, true)
            : valueType == typeof(Vector2)
                ? new PropAnimVector2(clip.LengthInSeconds, clip.Looped, true)
                : valueType == typeof(Vector3)
                    ? new PropAnimVector3(clip.LengthInSeconds, clip.Looped, true)
                    : valueType == typeof(Vector4)
                        ? new PropAnimVector4(clip.LengthInSeconds, clip.Looped, true)
                        : valueType == typeof(Quaternion)
                            ? new PropAnimQuaternion(clip.LengthInSeconds, clip.Looped, true)
                            : valueType == typeof(bool)
                                ? new PropAnimBool(clip.LengthInSeconds, clip.Looped, true)
                                : new PropAnimFloat(clip.LengthInSeconds, clip.Looped, true);

        SetAnimationClipDefaultValue(animation, defaultValue ?? CreateAnimationClipDefaultValue(valueType));
        return animation;
    }

    private static void SetAnimationClipDefaultValue(BasePropAnim animation, object? defaultValue)
    {
        switch (animation)
        {
            case PropAnimFloat floatAnimation when defaultValue is float scalar:
                floatAnimation.DefaultValue = scalar;
                break;
            case PropAnimVector2 vector2Animation when defaultValue is Vector2 vector2:
                vector2Animation.DefaultValue = vector2;
                break;
            case PropAnimVector3 vector3Animation when defaultValue is Vector3 vector3:
                vector3Animation.DefaultValue = vector3;
                break;
            case PropAnimVector4 vector4Animation when defaultValue is Vector4 vector4:
                vector4Animation.DefaultValue = vector4;
                break;
            case PropAnimQuaternion quaternionAnimation when defaultValue is Quaternion quaternion:
                quaternionAnimation.DefaultValue = quaternion;
                break;
            case PropAnimBool boolAnimation when defaultValue is bool boolean:
                boolAnimation.DefaultValue = boolean;
                break;
        }
    }

    private static object? CreateAnimationClipDefaultValue(Type valueType)
        => valueType == typeof(float)
            ? 0.0f
            : valueType == typeof(Vector2)
                ? Vector2.Zero
                : valueType == typeof(Vector3)
                    ? Vector3.Zero
                    : valueType == typeof(Vector4)
                        ? Vector4.Zero
                        : valueType == typeof(Quaternion)
                            ? Quaternion.Identity
                            : valueType == typeof(bool)
                                ? false
                                : null;

    private static void RemoveAnimationClipSelectedMember(AnimationClip clip, AnimationClipEditorState state)
    {
        if (clip.RootMember is null || string.IsNullOrWhiteSpace(state.SelectedMemberId))
            return;

        if (state.SelectedMemberId == "0")
        {
            clip.RootMember = null;
            state.SelectedMemberId = null;
            state.SelectedKeyframe = null;
            state.SelectedKeyframeMemberId = null;
            SyncAnimationClipStats(clip);
            return;
        }

        string[] parts = state.SelectedMemberId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1 || parts[0] != "0")
            return;

        AnimationMember? parent = clip.RootMember;
        for (int index = 1; index < parts.Length - 1; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int childIndex)
                || parent is null
                || childIndex < 0
                || childIndex >= parent.Children.Count)
            {
                return;
            }

            parent = parent.Children[childIndex];
        }

        if (!int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int removeIndex)
            || parent is null
            || removeIndex < 0
            || removeIndex >= parent.Children.Count)
        {
            return;
        }

        parent.Children.RemoveAt(removeIndex);
        state.SelectedMemberId = "0";
        state.SelectedKeyframe = null;
        state.SelectedKeyframeMemberId = null;
        PruneEmptyAnimationClipGroups(clip.RootMember);
        SyncAnimationClipStats(clip);
    }

    private static bool PruneEmptyAnimationClipGroups(AnimationMember member)
    {
        for (int index = member.Children.Count - 1; index >= 0; index--)
        {
            AnimationMember child = member.Children[index];
            if (PruneEmptyAnimationClipGroups(child))
                member.Children.RemoveAt(index);
        }

        return member.MemberType == EAnimationMemberType.Group
            && member.Animation is null
            && member.Children.Count == 0;
    }

    private static void SyncAnimationClipStats(AnimationClip clip)
        => clip.TotalAnimCount = CountAnimationClipAnimations(clip.RootMember);

    private static int CountAnimationClipAnimations(AnimationMember? member)
    {
        if (member is null)
            return 0;

        int count = member.Animation is null ? 0 : 1;
        foreach (AnimationMember child in member.Children)
            count += CountAnimationClipAnimations(child);
        return count;
    }

    private static IEnumerable<BaseAnimation> EnumerateAnimationClipAnimations(AnimationMember? member)
    {
        if (member is null)
            yield break;

        if (member.Animation is BaseAnimation animation)
            yield return animation;

        foreach (AnimationMember child in member.Children)
        {
            foreach (BaseAnimation childAnimation in EnumerateAnimationClipAnimations(child))
                yield return childAnimation;
        }
    }

    private static void SetAnimationClipLength(AnimationClip clip, float lengthInSeconds, bool stretchAnimation)
    {
        clip.LengthInSeconds = lengthInSeconds;
        foreach (BaseAnimation animation in EnumerateAnimationClipAnimations(clip.RootMember))
            animation.SetLength(lengthInSeconds, stretchAnimation);
    }

    private static bool TryFindAnimationClipMemberId(AnimationMember? current, AnimationMember target, string currentId, out string? id)
    {
        if (current is null)
        {
            id = null;
            return false;
        }

        if (ReferenceEquals(current, target))
        {
            id = currentId;
            return true;
        }

        for (int childIndex = 0; childIndex < current.Children.Count; childIndex++)
        {
            if (TryFindAnimationClipMemberId(current.Children[childIndex], target, $"{currentId}/{childIndex}", out id))
                return true;
        }

        id = null;
        return false;
    }

    private static bool TryAddAnimationClipKeyframe(AnimationMember member, float second, AnimationClip clip, out Keyframe? keyframe)
    {
        keyframe = null;
        if (member.Animation is null)
            return false;

        float clampedSecond = Math.Clamp(second, 0.0f, MathF.Max(clip.LengthInSeconds, 0.0f));
        object? defaultValue = member.DefaultValue ?? GetAnimationClipDefaultValue(member.Animation);

        switch (member.Animation)
        {
            case PropAnimFloat floatAnimation:
                {
                    float value = defaultValue is float scalar ? scalar : floatAnimation.DefaultValue;
                    FloatKeyframe newKey = new(clampedSecond, value, 0.0f, EVectorInterpType.Linear);
                    floatAnimation.Keyframes.Add(newKey);
                    keyframe = newKey;
                    return true;
                }
            case PropAnimVector2 vector2Animation:
                {
                    Vector2 value = defaultValue is Vector2 vector2 ? vector2 : vector2Animation.DefaultValue;
                    Vector2Keyframe newKey = new(clampedSecond, value, Vector2.Zero, EVectorInterpType.Smooth);
                    vector2Animation.Keyframes.Add(newKey);
                    keyframe = newKey;
                    return true;
                }
            case PropAnimVector3 vector3Animation:
                {
                    Vector3 value = defaultValue is Vector3 vector3 ? vector3 : vector3Animation.DefaultValue;
                    Vector3Keyframe newKey = new(clampedSecond, value, Vector3.Zero, EVectorInterpType.Smooth);
                    vector3Animation.Keyframes.Add(newKey);
                    keyframe = newKey;
                    return true;
                }
            case PropAnimVector4 vector4Animation:
                {
                    Vector4 value = defaultValue is Vector4 vector4 ? vector4 : vector4Animation.DefaultValue;
                    Vector4Keyframe newKey = new(clampedSecond, value, Vector4.Zero, EVectorInterpType.Smooth);
                    vector4Animation.Keyframes.Add(newKey);
                    keyframe = newKey;
                    return true;
                }
            case PropAnimQuaternion quaternionAnimation:
                {
                    Quaternion value = defaultValue is Quaternion quaternion ? quaternion : quaternionAnimation.DefaultValue;
                    QuaternionKeyframe newKey = new(clampedSecond, value, Quaternion.Identity, ERadialInterpType.Smooth);
                    quaternionAnimation.Keyframes.Add(newKey);
                    keyframe = newKey;
                    return true;
                }
            case PropAnimBool boolAnimation:
                {
                    bool value = defaultValue is bool boolean ? boolean : boolAnimation.DefaultValue;
                    BoolKeyframe newKey = new(clampedSecond, value);
                    boolAnimation.Keyframes.Add(newKey);
                    keyframe = newKey;
                    return true;
                }
            default:
                return false;
        }
    }

    private static object? GetAnimationClipDefaultValue(BasePropAnim animation)
        => animation switch
        {
            PropAnimFloat floatAnimation => floatAnimation.DefaultValue,
            PropAnimVector2 vector2Animation => vector2Animation.DefaultValue,
            PropAnimVector3 vector3Animation => vector3Animation.DefaultValue,
            PropAnimVector4 vector4Animation => vector4Animation.DefaultValue,
            PropAnimQuaternion quaternionAnimation => quaternionAnimation.DefaultValue,
            PropAnimBool boolAnimation => boolAnimation.DefaultValue,
            _ => null
        };

    private static void DrawAnimationClipEditorContent(AnimationClip clip, AnimationClipEditorState state, List<AnimationClipMemberRow> visibleRows)
    {
        TryFindAnimationClipSelectedMember(clip.RootMember, state.SelectedMemberId, "0", clip.RootMember?.MemberName, out AnimationMember? selectedMember, out string selectedPath);

        float spacing = ImGui.GetStyle().ItemSpacing.Y;
        float detailsHeight = 320.0f;
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
            DrawAnimationClipSelectionDetails(clip, state, selectedMember, selectedPath);
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
        }

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        uint bgColor = ImGui.GetColorU32(new Vector4(0.08f, 0.09f, 0.11f, 1.0f));
        uint gridColor = ImGui.GetColorU32(new Vector4(0.22f, 0.24f, 0.30f, 1.0f));
        uint rowAltColor = ImGui.GetColorU32(new Vector4(1.0f, 1.0f, 1.0f, 0.03f));
        uint rowSelectedColor = ImGui.GetColorU32(new Vector4(0.32f, 0.48f, 0.78f, 0.18f));
        uint keyColor = ImGui.GetColorU32(new Vector4(0.93f, 0.77f, 0.27f, 1.0f));
        uint selectedKeyColor = ImGui.GetColorU32(new Vector4(1.0f, 1.0f, 1.0f, 1.0f));
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
                if (ReferenceEquals(state.SelectedKeyframe, keyframe))
                    DrawAnimationClipDiamond(drawList, new Vector2(x, centerY), 7.0f, selectedKeyColor);

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

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (hoveredKeyframe is not null)
            {
                state.SelectedMemberId = hoveredMember.Id;
                state.SelectedKeyframe = hoveredKeyframe;
                state.SelectedKeyframeMemberId = hoveredMember.Id;
                state.NewKeySecond = hoveredKeyframe.Second;
            }
            else
            {
                Vector2 mouse = ImGui.GetMousePos();
                float localY = mouse.Y - origin.Y - headerHeight;
                if (localY >= 0.0f)
                {
                    int rowIndex = (int)(localY / rowHeight);
                    if (rowIndex >= 0 && rowIndex < visibleRows.Count)
                    {
                        state.SelectedMemberId = visibleRows[rowIndex].Id;
                        state.SelectedKeyframe = null;
                        state.SelectedKeyframeMemberId = null;
                    }
                }
            }
        }

        if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            Vector2 mouse = ImGui.GetMousePos();
            float localY = mouse.Y - origin.Y - headerHeight;
            int rowIndex = localY >= 0.0f ? (int)(localY / rowHeight) : -1;
            if (rowIndex >= 0 && rowIndex < visibleRows.Count)
            {
                AnimationClipMemberRow row = visibleRows[rowIndex];
                float second = Math.Clamp((mouse.X - origin.X - timePadding) / MathF.Max(1.0f, state.LanePixelsPerSecond), 0.0f, MathF.Max(clip.LengthInSeconds, 0.0f));
                if (TryAddAnimationClipKeyframe(row.Member, second, clip, out Keyframe? keyframe))
                {
                    state.SelectedMemberId = row.Id;
                    state.SelectedKeyframe = keyframe;
                    state.SelectedKeyframeMemberId = row.Id;
                    state.NewKeySecond = second;
                }
            }
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

    private static void DrawAnimationClipSelectionDetails(AnimationClip clip, AnimationClipEditorState state, AnimationMember? selectedMember, string selectedPath)
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

        if (selectedMember.Animation is not null)
        {
            ImGui.Separator();
            DrawAnimationClipKeyframeEditor(clip, state, selectedMember);
        }

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

    private static void DrawAnimationClipKeyframeEditor(AnimationClip clip, AnimationClipEditorState state, AnimationMember member)
    {
        if (member.Animation is null)
            return;

        ImGui.TextUnformatted("Keyframes");
        ImGui.SetNextItemWidth(120.0f);
        float newKeySecond = Math.Clamp(state.NewKeySecond, 0.0f, MathF.Max(clip.LengthInSeconds, 0.0f));
        if (ImGui.DragFloat("New Time", ref newKeySecond, 0.01f, 0.0f, MathF.Max(clip.LengthInSeconds, 0.0f), "%.3f"))
            state.NewKeySecond = newKeySecond;

        ImGui.SameLine();
        if (ImGui.Button("Add Key"))
        {
            if (TryAddAnimationClipKeyframe(member, state.NewKeySecond, clip, out Keyframe? keyframe))
            {
                state.SelectedKeyframe = keyframe;
                state.SelectedKeyframeMemberId = state.SelectedMemberId;
            }
        }

        ImGui.SameLine();
        bool canRemove = state.SelectedKeyframe is not null && string.Equals(state.SelectedKeyframeMemberId, state.SelectedMemberId, StringComparison.Ordinal);
        if (!canRemove)
            ImGui.BeginDisabled();
        if (ImGui.Button("Remove Key") && state.SelectedKeyframe is not null)
        {
            state.SelectedKeyframe.Remove();
            state.SelectedKeyframe = null;
            state.SelectedKeyframeMemberId = null;
        }
        if (!canRemove)
            ImGui.EndDisabled();

        List<Keyframe> keyframes = EnumerateAnimationKeyframes(member.Animation).OrderBy(static key => key.Second).ToList();
        if (keyframes.Count == 0)
        {
            ImGui.TextDisabled("This animation has no keyframes yet.");
            return;
        }

        if (!ImGui.BeginTable("AnimationClipKeyframeEditorTable", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY, new Vector2(0.0f, 160.0f)))
            return;

        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 28.0f);
        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 82.0f);
        ImGui.TableSetupColumn("Frame", ImGuiTableColumnFlags.WidthFixed, 64.0f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Tangents", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Interpolation", ImGuiTableColumnFlags.WidthFixed, 130.0f);
        ImGui.TableHeadersRow();

        for (int index = 0; index < keyframes.Count; index++)
        {
            Keyframe keyframe = keyframes[index];
            ImGui.PushID(keyframe.GetHashCode());
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            bool selected = ReferenceEquals(state.SelectedKeyframe, keyframe);
            if (ImGui.Selectable("##SelectKey", selected, ImGuiSelectableFlags.SpanAllColumns))
            {
                state.SelectedKeyframe = keyframe;
                state.SelectedKeyframeMemberId = state.SelectedMemberId;
                state.NewKeySecond = keyframe.Second;
            }

            ImGui.TableSetColumnIndex(1);
            float second = keyframe.Second;
            ImGui.SetNextItemWidth(-1.0f);
            if (ImGui.DragFloat("##Second", ref second, 0.01f, 0.0f, MathF.Max(clip.LengthInSeconds, 0.0f), "%.3f"))
            {
                keyframe.Second = Math.Clamp(second, 0.0f, MathF.Max(clip.LengthInSeconds, 0.0f));
                state.NewKeySecond = keyframe.Second;
            }

            ImGui.TableSetColumnIndex(2);
            int authoredFrame = keyframe.AuthoredFrameIndex >= 0
                ? keyframe.AuthoredFrameIndex
                : (int)MathF.Round(keyframe.Second * Math.Max(1, clip.SampleRate));
            ImGui.SetNextItemWidth(-1.0f);
            if (ImGui.DragInt("##Frame", ref authoredFrame, 1.0f, 0, int.MaxValue))
            {
                keyframe.AuthoredFrameIndex = authoredFrame;
                keyframe.Second = authoredFrame / (float)Math.Max(1, clip.SampleRate);
            }

            ImGui.TableSetColumnIndex(3);
            DrawAnimationClipKeyframeValueEditor(keyframe);

            ImGui.TableSetColumnIndex(4);
            DrawAnimationClipKeyframeTangentEditor(keyframe);

            ImGui.TableSetColumnIndex(5);
            DrawAnimationClipKeyframeInterpolationEditor(keyframe);

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private static void DrawAnimationClipKeyframeValueEditor(Keyframe keyframe)
    {
        if (keyframe is IPlanarKeyframe planar)
        {
            object inValue = planar.InValue;
            object outValue = planar.OutValue;
            if (DrawAnimationClipValueEditor("In", keyframe.ValueType, ref inValue, 0.01f))
                planar.InValue = inValue;
            if (DrawAnimationClipValueEditor("Out", keyframe.ValueType, ref outValue, 0.01f))
                planar.OutValue = outValue;
            return;
        }

        if (keyframe is QuaternionKeyframe quaternionKeyframe)
        {
            object inValue = quaternionKeyframe.InValue;
            object outValue = quaternionKeyframe.OutValue;
            if (DrawAnimationClipValueEditor("In", typeof(Quaternion), ref inValue, 0.01f))
                quaternionKeyframe.InValue = (Quaternion)inValue;
            if (DrawAnimationClipValueEditor("Out", typeof(Quaternion), ref outValue, 0.01f))
                quaternionKeyframe.OutValue = (Quaternion)outValue;
            return;
        }

        if (keyframe is BoolKeyframe boolKeyframe)
        {
            bool value = boolKeyframe.Value;
            if (ImGui.Checkbox("Value", ref value))
                boolKeyframe.Value = value;
        }
    }

    private static void DrawAnimationClipKeyframeTangentEditor(Keyframe keyframe)
    {
        if (keyframe is IPlanarKeyframe planar)
        {
            object inTangent = planar.InTangent;
            object outTangent = planar.OutTangent;
            if (DrawAnimationClipValueEditor("In Tan", keyframe.ValueType, ref inTangent, 0.01f))
                planar.InTangent = inTangent;
            if (DrawAnimationClipValueEditor("Out Tan", keyframe.ValueType, ref outTangent, 0.01f))
                planar.OutTangent = outTangent;

            if (ImGui.SmallButton("Auto"))
            {
                planar.MakeInLinear();
                planar.MakeOutLinear();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Unify"))
                planar.UnifyKeyframe(EUnifyBias.Average);
            return;
        }

        if (keyframe is QuaternionKeyframe quaternionKeyframe)
        {
            object inTangent = quaternionKeyframe.InTangent;
            object outTangent = quaternionKeyframe.OutTangent;
            if (DrawAnimationClipValueEditor("In Tan", typeof(Quaternion), ref inTangent, 0.01f))
                quaternionKeyframe.InTangent = (Quaternion)inTangent;
            if (DrawAnimationClipValueEditor("Out Tan", typeof(Quaternion), ref outTangent, 0.01f))
                quaternionKeyframe.OutTangent = (Quaternion)outTangent;
            return;
        }

        ImGui.TextDisabled("Step");
    }

    private static void DrawAnimationClipKeyframeInterpolationEditor(Keyframe keyframe)
    {
        if (keyframe is IPlanarKeyframe planar)
        {
            EVectorInterpType outType = planar.InterpolationTypeOut;
            if (DrawEnumCombo("Out", outType, value => planar.InterpolationTypeOut = value, "Keyframe Out Interpolation", keyframe))
            {
            }

            PropertyInfo? inProperty = keyframe.GetType().GetProperty("InterpolationTypeIn", BindingFlags.Public | BindingFlags.Instance);
            if (inProperty?.GetValue(keyframe) is EVectorInterpType inType)
                DrawEnumCombo("In", inType, value => inProperty.SetValue(keyframe, value), "Keyframe In Interpolation", keyframe);
            return;
        }

        if (keyframe is QuaternionKeyframe quaternionKeyframe)
        {
            DrawEnumCombo("Out", quaternionKeyframe.InterpolationTypeOut, value => quaternionKeyframe.InterpolationTypeOut = value, "Quaternion Keyframe Out Interpolation", keyframe);
            DrawEnumCombo("In", quaternionKeyframe.InterpolationTypeIn, value => quaternionKeyframe.InterpolationTypeIn = value, "Quaternion Keyframe In Interpolation", keyframe);
            return;
        }

        ImGui.TextDisabled("Step");
    }

    private static bool DrawAnimationClipValueEditor(string label, Type valueType, ref object value, float speed)
    {
        switch (value)
        {
            case float scalar:
                ImGui.SetNextItemWidth(-1.0f);
                if (ImGui.DragFloat(label, ref scalar, speed))
                {
                    value = scalar;
                    return true;
                }
                return false;
            case Vector2 vector2:
                ImGui.SetNextItemWidth(-1.0f);
                if (ImGui.DragFloat2(label, ref vector2, speed))
                {
                    value = vector2;
                    return true;
                }
                return false;
            case Vector3 vector3:
                ImGui.SetNextItemWidth(-1.0f);
                if (ImGui.DragFloat3(label, ref vector3, speed))
                {
                    value = vector3;
                    return true;
                }
                return false;
            case Vector4 vector4:
                ImGui.SetNextItemWidth(-1.0f);
                if (ImGui.DragFloat4(label, ref vector4, speed))
                {
                    value = vector4;
                    return true;
                }
                return false;
            case Quaternion quaternion:
                Vector4 vector = new(quaternion.X, quaternion.Y, quaternion.Z, quaternion.W);
                ImGui.SetNextItemWidth(-1.0f);
                if (ImGui.DragFloat4(label, ref vector, speed))
                {
                    value = new Quaternion(vector.X, vector.Y, vector.Z, vector.W);
                    return true;
                }
                return false;
            default:
                if (valueType == typeof(bool))
                {
                    bool boolean = value is bool b && b;
                    if (ImGui.Checkbox(label, ref boolean))
                    {
                        value = boolean;
                        return true;
                    }
                }
                else
                {
                    ImGui.TextDisabled(FormatAnimationClipValue(value));
                }
                return false;
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
                if (ReferenceEquals(state.SelectedKeyframe, keyframe.Keyframe))
                    drawList.AddCircle(point, 8.0f, ImGui.GetColorU32(new Vector4(1.0f, 1.0f, 1.0f, 1.0f)), 0, 2.0f);

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
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                state.SelectedKeyframe = sourceKeyframe;
                state.SelectedKeyframeMemberId = state.SelectedMemberId;
                state.NewKeySecond = sourceKeyframe.Second;
            }

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
        public Keyframe? SelectedKeyframe;
        public string? SelectedKeyframeMemberId;
        public AnimationClipEditorViewMode ViewMode = AnimationClipEditorViewMode.Lanes;
        public AnimationClipGraphXAxisMode XAxisMode = AnimationClipGraphXAxisMode.Seconds;
        public SceneNode? AuthoringRootNode;
        public SceneNode? CandidateCacheRootNode;
        public int CandidateCacheComponentCount = -1;
        public List<AnimationClipPathCandidate> CandidateCache { get; } = [];
        public string CandidateSearch = string.Empty;
        public string? SelectedCandidateId;
        public float NewKeySecond;
        public bool StretchCurvesWhenChangingLength;
        public float LanePixelsPerSecond = 120.0f;
        public Vector2 GraphZoom = new(120.0f, 80.0f);
        public Vector2 GraphOffset = new(0.0f, 0.0f);
        public string? LastGraphedMemberId;
        public bool PendingGraphAutoFrame = true;
    }

    private readonly record struct AnimationClipMemberRow(AnimationMember Member, string Id, string DisplayPath, int Depth);
    private sealed record AnimationClipPathCandidate(
        string Id,
        string DisplayPath,
        string TargetTypeName,
        AnimationClipPathSegment[] Segments,
        EAnimationMemberType LeafMemberType,
        Type ValueType,
        object? DefaultValue);

    private sealed record AnimationClipPathSegment(
        string MemberName,
        EAnimationMemberType MemberType,
        object?[]? MethodArguments,
        int AnimatedMethodArgumentIndex,
        bool CacheReturnValue)
    {
        public string IdPart
        {
            get
            {
                string args = MethodArguments is null || MethodArguments.Length == 0
                    ? string.Empty
                    : $"({string.Join(",", MethodArguments.Select(arg => arg?.ToString() ?? "<null>"))})";
                return $"{MemberType}:{MemberName}:{AnimatedMethodArgumentIndex}:{CacheReturnValue}:{args}";
            }
        }
    }

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
