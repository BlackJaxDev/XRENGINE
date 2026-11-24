using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using ImGuiNET;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static WeakReference<AnimStateMachineComponent>? _animStateMachinePinnedComponent;
    private static readonly ConditionalWeakTable<AnimLayer, AnimStateMachineLayerViewState> _animStateMachineLayerViews = new();
    private static readonly List<AnimStateMachineComponent> _animStateMachineWorldScratch = [];

    private sealed class AnimStateMachineLayerViewState
    {
        public Vector2 Pan = new(80.0f, 80.0f);
        public float Zoom = 1.0f;
        public Vector2 AnyStatePosition = new(40.0f, 40.0f);
        public readonly Dictionary<AnimState, Vector2> StatePositions = new(ReferenceEqualityComparer.Instance);
        public bool HasLayout;
        public AnimStateBase? SelectedNode;
        public AnimStateTransition? SelectedTransition;
        public AnimStateBase? PendingTransitionSource;
        public string NewParameterName = "NewParam";
        public int NewParameterTypeIndex;
    }

    private readonly record struct AnimGraphEdgeRenderInfo(
        AnimStateBase Source,
        AnimStateTransition Transition,
        Vector2 StartScreen,
        Vector2 EndScreen,
        bool IsActive,
        bool IsSelected);

    private static int _animStateMachineSelectedLayerIndex;

    public static void OpenAnimStateMachineGraph(AnimStateMachineComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        _animStateMachinePinnedComponent = new WeakReference<AnimStateMachineComponent>(component);
        _animStateMachineSelectedLayerIndex = 0;
        _showAnimStateMachineGraph = true;
    }

    private static void DrawAnimStateMachineGraphPanel()
    {
        if (!_showAnimStateMachineGraph)
            return;

        ImGui.SetNextWindowSize(new Vector2(1280.0f, 760.0f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Animation State Machine", ref _showAnimStateMachineGraph, ImGuiWindowFlags.MenuBar))
        {
            ImGui.End();
            return;
        }

        AnimStateMachineComponent? component = ResolveAnimStateMachineComponent();
        DrawAnimStateMachineMenuBar(component);
        DrawAnimStateMachineTargetBar(ref component);
        ImGui.Separator();

        if (component is null)
        {
            DrawAnimStateMachineEmptyState();
            ImGui.End();
            return;
        }

        AnimStateMachine stateMachine = component.StateMachine;
        if (stateMachine.Layers.Count == 0)
        {
            ImGui.TextDisabled("This state machine has no layers.");
            if (ImGui.Button("Add Base Layer"))
            {
                stateMachine.Layers.Add(new AnimLayer());
                _animStateMachineSelectedLayerIndex = 0;
                NotifyInspectorValueEdited(component);
            }

            ImGui.End();
            return;
        }

        _animStateMachineSelectedLayerIndex = Math.Clamp(_animStateMachineSelectedLayerIndex, 0, stateMachine.Layers.Count - 1);
        AnimLayer activeLayer = stateMachine.Layers[_animStateMachineSelectedLayerIndex];
        AnimStateMachineLayerViewState view = GetOrCreateAnimLayerViewState(activeLayer);
        EnsureAnimLayerLayout(activeLayer, view);

        Vector2 avail = ImGui.GetContentRegionAvail();
        float leftWidth = Math.Clamp(avail.X * 0.22f, 240.0f, 340.0f);
        float rightWidth = Math.Clamp(avail.X * 0.28f, 280.0f, 420.0f);
        float centerWidth = MathF.Max(220.0f, avail.X - leftWidth - rightWidth - ImGui.GetStyle().ItemSpacing.X * 2.0f);

        if (ImGui.BeginChild("AnimSMLeftSidebar", new Vector2(leftWidth, 0.0f), ImGuiChildFlags.Borders))
            DrawAnimStateMachineLeftSidebar(component, stateMachine, ref activeLayer, view);
        ImGui.EndChild();

        ImGui.SameLine();

        if (ImGui.BeginChild("AnimSMCenterCanvas", new Vector2(centerWidth, 0.0f), ImGuiChildFlags.Borders))
            DrawAnimStateMachineCanvasArea(component, stateMachine, activeLayer, view);
        ImGui.EndChild();

        ImGui.SameLine();

        if (ImGui.BeginChild("AnimSMRightInspector", new Vector2(0.0f, 0.0f), ImGuiChildFlags.Borders))
            DrawAnimStateMachineDetailInspector(component, stateMachine, activeLayer, view);
        ImGui.EndChild();

        ImGui.End();
    }

    private static void DrawAnimStateMachineMenuBar(AnimStateMachineComponent? component)
    {
        if (!ImGui.BeginMenuBar())
            return;

        AnimLayer? activeLayer = component is not null && component.StateMachine.Layers.Count > 0
            ? component.StateMachine.Layers[Math.Clamp(_animStateMachineSelectedLayerIndex, 0, component.StateMachine.Layers.Count - 1)]
            : null;
        AnimStateMachineLayerViewState? view = activeLayer is not null
            ? GetOrCreateAnimLayerViewState(activeLayer)
            : null;

        if (ImGui.MenuItem("Auto Layout", null, false, activeLayer is not null && view is not null))
            AutoLayoutAnimLayer(activeLayer!, view!);

        if (ImGui.MenuItem("Reset View", null, false, view is not null))
        {
            view!.Pan = new Vector2(80.0f, 80.0f);
            view.Zoom = 1.0f;
        }

        if (ImGui.MenuItem("Rebuild Playback", null, false, component is not null))
            RebuildAnimStateMachinePlayback(component!);

        if (ImGui.MenuItem("Dependencies & Builds..."))
            _showDependenciesAndBuildsPanel = true;

        ImGui.EndMenuBar();
    }

    private static void DrawAnimStateMachineTargetBar(ref AnimStateMachineComponent? current)
    {
        CollectWorldAnimStateMachines(_animStateMachineWorldScratch);

        string currentLabel = current is not null
            ? FormatAnimStateMachineLabel(current)
            : "<Select AnimStateMachineComponent>";

        ImGui.SetNextItemWidth(320.0f);
        if (ImGui.BeginCombo("##AnimSMTargetCombo", currentLabel))
        {
            for (int i = 0; i < _animStateMachineWorldScratch.Count; i++)
            {
                AnimStateMachineComponent candidate = _animStateMachineWorldScratch[i];
                bool selected = ReferenceEquals(candidate, current);
                if (ImGui.Selectable($"{FormatAnimStateMachineLabel(candidate)}##AnimSMCandidate{i}", selected))
                {
                    _animStateMachinePinnedComponent = new WeakReference<AnimStateMachineComponent>(candidate);
                    _animStateMachineSelectedLayerIndex = 0;
                    current = candidate;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            if (_animStateMachineWorldScratch.Count == 0)
                ImGui.TextDisabled("No AnimStateMachineComponent found in the active world.");

            ImGui.EndCombo();
        }

        bool isPinned = _animStateMachinePinnedComponent is not null
            && _animStateMachinePinnedComponent.TryGetTarget(out AnimStateMachineComponent? pinned)
            && pinned is not null
            && ReferenceEquals(pinned, current);

        ImGui.SameLine();
        if (current is not null)
        {
            if (isPinned)
            {
                if (ImGui.SmallButton("Unpin"))
                    _animStateMachinePinnedComponent = null;
            }
            else if (ImGui.SmallButton("Pin"))
            {
                _animStateMachinePinnedComponent = new WeakReference<AnimStateMachineComponent>(current);
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Rebuild Playback"))
                RebuildAnimStateMachinePlayback(current);

            if (!string.IsNullOrWhiteSpace(current.PlaybackCapabilityDiagnostic))
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1.0f, 0.72f, 0.25f, 1.0f), current.PlaybackCapabilityDiagnostic);
            }
        }
    }

    private static void DrawAnimStateMachineEmptyState()
    {
        ImGui.TextDisabled("No AnimStateMachineComponent is selected.");
        ImGui.Spacing();
        ImGui.TextWrapped("Select a SceneNode with an AnimStateMachineComponent in the Hierarchy, pick one from the dropdown above, or attach one to the currently selected node.");

        SceneNode? selectedNode = Selection.LastSceneNode;
        if (selectedNode is not null)
        {
            ImGui.Spacing();
            if (ImGui.Button($"Add AnimStateMachineComponent to '{selectedNode.Name}'"))
            {
                AnimStateMachineComponent? created = selectedNode.AddComponent<AnimStateMachineComponent>();
                if (created is not null)
                {
                    created.Humanoid ??= selectedNode.GetComponentInHierarchy(nameof(HumanoidComponent)) as HumanoidComponent;
                    if (created.StateMachine.Layers.Count == 0)
                        created.StateMachine.Layers.Add(new AnimLayer());
                    OpenAnimStateMachineGraph(created);
                    NotifyInspectorValueEdited(created);
                }
            }
        }
    }

    private static void DrawAnimStateMachineLeftSidebar(
        AnimStateMachineComponent component,
        AnimStateMachine stateMachine,
        ref AnimLayer activeLayer,
        AnimStateMachineLayerViewState view)
    {
        if (ImGui.BeginTabBar("##AnimSMSidebarTabs"))
        {
            if (ImGui.BeginTabItem("Layers"))
            {
                if (ImGui.SmallButton("+ Layer"))
                {
                    stateMachine.Layers.Add(new AnimLayer());
                    _animStateMachineSelectedLayerIndex = stateMachine.Layers.Count - 1;
                    activeLayer = stateMachine.Layers[_animStateMachineSelectedLayerIndex];
                    NotifyInspectorValueEdited(component);
                }

                ImGui.Separator();

                for (int i = 0; i < stateMachine.Layers.Count; i++)
                {
                    AnimLayer layer = stateMachine.Layers[i];
                    bool selected = i == _animStateMachineSelectedLayerIndex;
                    string stateSummary = layer.CurrentState?.Name ?? layer.InitialState?.Name ?? "<empty>";
                    ImGui.PushID(i);

                    if (ImGui.Selectable($"Layer {i} ({layer.ApplyType}) - {stateSummary}", selected))
                    {
                        _animStateMachineSelectedLayerIndex = i;
                        activeLayer = layer;
                    }

                    if (selected)
                    {
                        ImGui.Indent();
                        float weight = layer.Weight;
                        ImGui.SetNextItemWidth(-1.0f);
                        if (ImGui.SliderFloat("Weight##LayerWeight", ref weight, 0.0f, 1.0f))
                        {
                            layer.Weight = weight;
                            NotifyInspectorValueEdited(component);
                        }

                        int applyTypeIdx = (int)layer.ApplyType;
                        string[] applyNames = Enum.GetNames<AnimLayer.EApplyType>();
                        ImGui.SetNextItemWidth(-1.0f);
                        if (ImGui.Combo("Apply##LayerApply", ref applyTypeIdx, applyNames, applyNames.Length))
                        {
                            layer.ApplyType = (AnimLayer.EApplyType)applyTypeIdx;
                            NotifyInspectorValueEdited(component);
                        }

                        DrawAnimLayerAvatarMaskEditor(component, layer);

                        if (stateMachine.Layers.Count > 1 && ImGui.SmallButton("Remove Layer"))
                        {
                            stateMachine.Layers.RemoveAt(i);
                            _animStateMachineSelectedLayerIndex = Math.Clamp(_animStateMachineSelectedLayerIndex, 0, stateMachine.Layers.Count - 1);
                            activeLayer = stateMachine.Layers[_animStateMachineSelectedLayerIndex];
                            NotifyInspectorValueEdited(component);
                            ImGui.Unindent();
                            ImGui.PopID();
                            break;
                        }

                        ImGui.Unindent();
                    }

                    ImGui.PopID();
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Parameters"))
            {
                DrawAnimStateMachineParametersTab(component, stateMachine, view);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private static readonly (EHumanoidBodyMask Flag, string Label)[] HumanoidBodyMaskFlags =
    [
        (EHumanoidBodyMask.Root, "Root Motion"),
        (EHumanoidBodyMask.Body, "Body (Spine/Chest)"),
        (EHumanoidBodyMask.Head, "Head / Neck"),
        (EHumanoidBodyMask.Eyes, "Eyes"),
        (EHumanoidBodyMask.Jaw, "Jaw"),
        (EHumanoidBodyMask.LeftArm, "Left Arm"),
        (EHumanoidBodyMask.RightArm, "Right Arm"),
        (EHumanoidBodyMask.LeftFingers, "Left Fingers"),
        (EHumanoidBodyMask.RightFingers, "Right Fingers"),
        (EHumanoidBodyMask.LeftLeg, "Left Leg"),
        (EHumanoidBodyMask.RightLeg, "Right Leg"),
        (EHumanoidBodyMask.NonHumanoid, "Non-Humanoid"),
    ];

    private static string DescribeAvatarMask(EHumanoidBodyMask mask)
        => mask switch
        {
            EHumanoidBodyMask.All => "All",
            EHumanoidBodyMask.UpperBody => "Upper Body",
            EHumanoidBodyMask.LowerBody => "Lower Body",
            EHumanoidBodyMask.HandsOnly => "Hands",
            EHumanoidBodyMask.FaceOnly => "Face",
            EHumanoidBodyMask.None => "None",
            _ => "Custom",
        };

    private static void DrawAnimLayerAvatarMaskEditor(AnimStateMachineComponent component, AnimLayer layer)
    {
        EHumanoidBodyMask mask = layer.AvatarMask;
        if (!ImGui.TreeNode($"Mask: {DescribeAvatarMask(mask)}###LayerAvatarMask"))
            return;

        if (ImGui.SmallButton("All"))
        {
            layer.AvatarMask = EHumanoidBodyMask.All;
            NotifyInspectorValueEdited(component);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Upper"))
        {
            layer.AvatarMask = EHumanoidBodyMask.UpperBody;
            NotifyInspectorValueEdited(component);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Lower"))
        {
            layer.AvatarMask = EHumanoidBodyMask.LowerBody;
            NotifyInspectorValueEdited(component);
        }

        if (ImGui.SmallButton("Hands"))
        {
            layer.AvatarMask = EHumanoidBodyMask.HandsOnly;
            NotifyInspectorValueEdited(component);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Face"))
        {
            layer.AvatarMask = EHumanoidBodyMask.FaceOnly;
            NotifyInspectorValueEdited(component);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("None"))
        {
            layer.AvatarMask = EHumanoidBodyMask.None;
            NotifyInspectorValueEdited(component);
        }

        mask = layer.AvatarMask;
        for (int i = 0; i < HumanoidBodyMaskFlags.Length; i++)
        {
            (EHumanoidBodyMask flag, string label) = HumanoidBodyMaskFlags[i];
            bool enabled = (mask & flag) != 0;
            if (ImGui.Checkbox(label, ref enabled))
            {
                layer.AvatarMask = enabled ? (mask | flag) : (mask & ~flag);
                mask = layer.AvatarMask;
                NotifyInspectorValueEdited(component);
            }
        }

        ImGui.TreePop();
    }

    private static void DrawAnimStateMachineParametersTab(
        AnimStateMachineComponent component,
        AnimStateMachine stateMachine,
        AnimStateMachineLayerViewState view)
    {
        ImGui.SetNextItemWidth(120.0f);
        ImGui.InputTextWithHint("##NewParamName", "Name", ref view.NewParameterName, 128u);
        ImGui.SameLine();

        string[] paramTypes = ["Float", "Int", "Bool"];
        ImGui.SetNextItemWidth(68.0f);
        ImGui.Combo("##NewParamType", ref view.NewParameterTypeIndex, paramTypes, paramTypes.Length);
        ImGui.SameLine();

        if (ImGui.SmallButton("+"))
        {
            string trimmed = view.NewParameterName.Trim();
            if (!string.IsNullOrEmpty(trimmed) && !stateMachine.Variables.ContainsKey(trimmed))
            {
                AnimVar created = view.NewParameterTypeIndex switch
                {
                    1 => new AnimInt(trimmed, 0),
                    2 => new AnimBool(trimmed, false),
                    _ => new AnimFloat(trimmed, 0.0f),
                };
                stateMachine.Variables[trimmed] = created;
                NotifyInspectorValueEdited(component);
            }
        }

        MotionBricksLocomotionMotion? motionBricks = FindFirstMotionBricksMotion(stateMachine);
        if (motionBricks is not null)
        {
            if (ImGui.SmallButton("Ensure MotionBricks Parameters"))
            {
                EnsureMotionBricksParameters(stateMachine, motionBricks);
                NotifyInspectorValueEdited(component);
            }
        }

        ImGui.Separator();

        if (stateMachine.Variables.Count == 0)
        {
            ImGui.TextDisabled("No parameters defined.");
            return;
        }

        string? keyToRemove = null;
        int paramIdx = 0;
        foreach (KeyValuePair<string, AnimVar> kvp in stateMachine.Variables)
        {
            ImGui.PushID(paramIdx++);
            AnimVar variable = kvp.Value;
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(kvp.Key);
            ImGui.SameLine(MathF.Max(110.0f, ImGui.GetContentRegionAvail().X * 0.45f));

            if (variable is AnimFloat floatVar)
            {
                float v = floatVar.Value;
                ImGui.SetNextItemWidth(-28.0f);
                if (ImGui.DragFloat("##FloatVal", ref v, 0.02f))
                    floatVar.Value = v;
            }
            else if (variable is AnimInt intVar)
            {
                int v = intVar.Value;
                ImGui.SetNextItemWidth(-28.0f);
                if (ImGui.DragInt("##IntVal", ref v, 1.0f))
                    intVar.Value = v;
            }
            else if (variable is AnimBool boolVar)
            {
                bool v = boolVar.Value;
                if (ImGui.Checkbox("##BoolVal", ref v))
                    boolVar.Value = v;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("X"))
                keyToRemove = kvp.Key;

            ImGui.PopID();
        }

        if (keyToRemove is not null)
        {
            stateMachine.Variables.Remove(keyToRemove);
            NotifyInspectorValueEdited(component);
        }
    }

    private static void DrawAnimStateMachineCanvasArea(
        AnimStateMachineComponent component,
        AnimStateMachine stateMachine,
        AnimLayer layer,
        AnimStateMachineLayerViewState view)
    {
        if (ImGui.SmallButton("+ Add State"))
            ImGui.OpenPopup("AddAnimStateMotionPopup");

        if (ImGui.BeginPopup("AddAnimStateMotionPopup"))
        {
            DrawAddStateMotionPopupItems(component, stateMachine, layer, view, spawnWorldPos: null);
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Auto Layout"))
            AutoLayoutAnimLayer(layer, view);

        ImGui.SameLine();
        if (ImGui.SmallButton("Reset View"))
        {
            view.Pan = new Vector2(80.0f, 80.0f);
            view.Zoom = 1.0f;
        }

        if (view.PendingTransitionSource is not null)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.30f, 1.0f), "Click destination state (Esc to cancel)...");
            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
                view.PendingTransitionSource = null;
        }

        ImGui.Separator();

        ImGui.BeginChild("##AnimSMGraphCanvas", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        Vector2 canvasPos = ImGui.GetCursorScreenPos();
        Vector2 canvasSize = ImGui.GetContentRegionAvail();
        if (canvasSize.X < 1.0f || canvasSize.Y < 1.0f)
        {
            ImGui.EndChild();
            return;
        }

        ImGuiIOPtr io = ImGui.GetIO();
        ImGui.InvisibleButton("##AnimSMCanvasHit", canvasSize,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle | ImGuiButtonFlags.MouseButtonRight);
        bool canvasHovered = ImGui.IsItemHovered();

        if (canvasHovered
            && (ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 0.0f)
                || (ImGui.IsKeyDown(ImGuiKey.Space) && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0.0f))))
        {
            view.Pan += io.MouseDelta / MathF.Max(1.0e-6f, view.Zoom);
        }

        if (canvasHovered && MathF.Abs(io.MouseWheel) > 1.0e-6f)
        {
            float prevZoom = view.Zoom;
            float zoomBase = io.KeyCtrl ? 1.15f : 1.07f;
            float nextZoom = Math.Clamp(view.Zoom * MathF.Pow(zoomBase, io.MouseWheel), 0.35f, 2.25f);
            if (MathF.Abs(nextZoom - prevZoom) > 1.0e-6f)
            {
                Vector2 mouseLocal = io.MousePos - canvasPos;
                Vector2 worldBefore = (mouseLocal / prevZoom) - view.Pan;
                view.Zoom = nextZoom;
                Vector2 worldAfter = (mouseLocal / nextZoom) - view.Pan;
                view.Pan += worldAfter - worldBefore;
            }
        }

        DrawGrid(drawList, canvasPos, canvasSize, view.Pan, view.Zoom);

        const float nodeWidth = 210.0f;
        const float nodeHeight = 72.0f;

        List<AnimGraphEdgeRenderInfo> edges = CollectAnimGraphEdges(layer, view, canvasPos, nodeWidth, nodeHeight);
        DrawAnimGraphEdges(drawList, edges, view.Zoom);

        if (view.PendingTransitionSource is not null)
        {
            Vector2 srcWorld = GetAnimNodeWorldPosition(view.PendingTransitionSource, view);
            Vector2 srcCenter = AnimWorldToScreen(canvasPos, view, srcWorld + new Vector2(nodeWidth * 0.5f, nodeHeight * 0.5f));
            DrawArrowSegment(drawList, srcCenter, io.MousePos, ImGui.GetColorU32(new Vector4(1.0f, 0.85f, 0.30f, 0.95f)), 2.5f * view.Zoom);
        }

        bool clickedNode = false;
        DrawAnimAnyStateNode(drawList, canvasPos, nodeWidth, 56.0f, layer, view, ref clickedNode);
        DrawAnimStateNodes(drawList, canvasPos, nodeWidth, nodeHeight, component, stateMachine, layer, view, ref clickedNode);

        if (!clickedNode && canvasHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            AnimStateTransition? hitTransition = HitTestAnimGraphEdge(edges, io.MousePos, thresholdPixels: 8.0f);
            if (hitTransition is not null)
            {
                view.SelectedTransition = hitTransition;
                view.SelectedNode = null;
                view.PendingTransitionSource = null;
            }
            else if (view.PendingTransitionSource is not null)
            {
                view.PendingTransitionSource = null;
            }
        }

        if (!clickedNode && canvasHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            view.PendingTransitionSource = null;
            ImGui.OpenPopup("AnimSMCanvasContextMenu");
        }

        if (ImGui.BeginPopup("AnimSMCanvasContextMenu"))
        {
            Vector2 popupWorldPos = ((ImGui.GetMousePosOnOpeningCurrentPopup() - canvasPos) / MathF.Max(1.0e-6f, view.Zoom)) - view.Pan;
            DrawAddStateMotionPopupItems(component, stateMachine, layer, view, popupWorldPos);
            ImGui.EndPopup();
        }

        ImGui.EndChild();
    }

    private static void DrawAddStateMotionPopupItems(
        AnimStateMachineComponent component,
        AnimStateMachine stateMachine,
        AnimLayer layer,
        AnimStateMachineLayerViewState view,
        Vector2? spawnWorldPos)
    {
        ImGui.TextDisabled("Create State (AOT Motion Factory)");
        ImGui.Separator();

        if (ImGui.MenuItem("Empty State"))
            AddAnimStateToLayer(component, stateMachine, layer, view, motionType: null, spawnWorldPos);

        Type[] motionTypes = MotionFactoryRegistry.GetRegisteredTypes();
        if (motionTypes.Length > 0)
            ImGui.Separator();

        for (int i = 0; i < motionTypes.Length; i++)
        {
            Type motionType = motionTypes[i];
            string displayName = MotionFactoryRegistry.GetDisplayName(motionType);
            if (ImGui.MenuItem($"{displayName}##AddMotionState{i}"))
                AddAnimStateToLayer(component, stateMachine, layer, view, motionType, spawnWorldPos);
        }
    }

    private static void AddAnimStateToLayer(
        AnimStateMachineComponent component,
        AnimStateMachine stateMachine,
        AnimLayer layer,
        AnimStateMachineLayerViewState view,
        Type? motionType,
        Vector2? spawnWorldPos)
    {
        MotionBase? motion = null;
        string baseName = "New State";
        if (motionType is not null && MotionFactoryRegistry.TryCreate(motionType, out MotionBase? created))
        {
            motion = created;
            baseName = MotionFactoryRegistry.GetDisplayName(motionType);
            motion!.Name = baseName;
        }

        string uniqueName = MakeUniqueAnimStateName(layer, baseName);
        AnimState state = motion is not null ? new AnimState(motion, uniqueName) : new AnimState(uniqueName);
        layer.States.Add(state);
        if (layer.InitialState is null)
            layer.InitialState = state;

        Vector2 position = spawnWorldPos ?? new Vector2(300.0f + (layer.States.Count - 1) * 260.0f, 120.0f);
        view.StatePositions[state] = position;
        view.SelectedNode = state;
        view.SelectedTransition = null;

        if (motion is MotionBricksLocomotionMotion motionBricks)
            EnsureMotionBricksParameters(stateMachine, motionBricks);

        NotifyInspectorValueEdited(component);
        RebuildAnimStateMachinePlayback(component);
    }

    private static void DrawAnimAnyStateNode(
        ImDrawListPtr drawList,
        Vector2 canvasPos,
        float nodeWidth,
        float nodeHeight,
        AnimLayer layer,
        AnimStateMachineLayerViewState view,
        ref bool clickedNode)
    {
        Vector2 pMin = AnimWorldToScreen(canvasPos, view, view.AnyStatePosition);
        Vector2 size = new Vector2(nodeWidth, nodeHeight) * view.Zoom;
        Vector2 pMax = pMin + size;

        bool selected = ReferenceEquals(view.SelectedNode, layer.AnyState);
        uint bg = ImGui.GetColorU32(new Vector4(0.18f, 0.25f, 0.34f, 0.94f));
        uint header = ImGui.GetColorU32(new Vector4(0.22f, 0.44f, 0.56f, 0.96f));
        uint border = selected
            ? ImGui.GetColorU32(new Vector4(0.45f, 0.85f, 1.0f, 1.0f))
            : ImGui.GetColorU32(new Vector4(0.32f, 0.52f, 0.65f, 0.90f));

        float rounding = 7.0f * view.Zoom;
        drawList.AddRectFilled(pMin, pMax, bg, rounding);
        drawList.AddRectFilled(pMin, new Vector2(pMax.X, pMin.Y + 24.0f * view.Zoom), header, rounding, ImDrawFlags.RoundCornersTop);
        drawList.AddRect(pMin, pMax, border, rounding, ImDrawFlags.None, selected ? 2.5f : 1.5f);

        drawList.AddText(pMin + new Vector2(10.0f, 5.0f) * view.Zoom, ImGui.GetColorU32(ImGuiCol.Text), "Any State");
        drawList.AddText(
            pMin + new Vector2(10.0f, 32.0f) * view.Zoom,
            ImGui.GetColorU32(ImGuiCol.TextDisabled),
            $"{layer.AnyState.Transitions.Count} transition(s)");

        ImGui.SetCursorScreenPos(pMin);
        ImGui.InvisibleButton("##AnimAnyStateNode", size);
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            clickedNode = true;
            view.SelectedNode = layer.AnyState;
            view.SelectedTransition = null;
        }

        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0.0f))
            view.AnyStatePosition += ImGui.GetIO().MouseDelta / MathF.Max(1.0e-6f, view.Zoom);

        if (ImGui.BeginPopupContextItem("##AnimAnyStateCtx"))
        {
            if (ImGui.MenuItem("Make Transition"))
                view.PendingTransitionSource = layer.AnyState;
            ImGui.EndPopup();
        }
    }

    private static void DrawAnimStateNodes(
        ImDrawListPtr drawList,
        Vector2 canvasPos,
        float nodeWidth,
        float nodeHeight,
        AnimStateMachineComponent component,
        AnimStateMachine stateMachine,
        AnimLayer layer,
        AnimStateMachineLayerViewState view,
        ref bool clickedNode)
    {
        float rounding = 7.0f * view.Zoom;

        for (int i = 0; i < layer.States.Count; i++)
        {
            AnimState state = layer.States[i];
            if (!view.StatePositions.TryGetValue(state, out Vector2 worldPos))
            {
                worldPos = new Vector2(300.0f + i * 260.0f, 120.0f);
                view.StatePositions[state] = worldPos;
            }

            Vector2 pMin = AnimWorldToScreen(canvasPos, view, worldPos);
            Vector2 size = new Vector2(nodeWidth, nodeHeight) * view.Zoom;
            Vector2 pMax = pMin + size;

            bool isCurrent = ReferenceEquals(state, layer.CurrentState);
            bool isNext = ReferenceEquals(state, layer.NextState);
            bool isInitial = ReferenceEquals(state, layer.InitialState);
            bool isSelected = ReferenceEquals(view.SelectedNode, state);

            Vector4 headerCol = isCurrent
                ? new Vector4(0.16f, 0.56f, 0.30f, 0.96f)
                : isNext
                    ? new Vector4(0.72f, 0.48f, 0.14f, 0.96f)
                    : isInitial
                        ? new Vector4(0.76f, 0.42f, 0.16f, 0.94f)
                        : new Vector4(0.24f, 0.27f, 0.34f, 0.96f);

            Vector4 borderCol = isSelected
                ? new Vector4(0.42f, 0.82f, 1.0f, 1.0f)
                : isCurrent
                    ? new Vector4(0.28f, 0.88f, 0.44f, 1.0f)
                    : isNext
                        ? new Vector4(1.0f, 0.72f, 0.24f, 1.0f)
                        : new Vector4(0.42f, 0.46f, 0.54f, 0.85f);

            uint bg = ImGui.GetColorU32(new Vector4(0.14f, 0.16f, 0.20f, 0.95f));
            drawList.AddRectFilled(pMin, pMax, bg, rounding);
            drawList.AddRectFilled(pMin, new Vector2(pMax.X, pMin.Y + 24.0f * view.Zoom), ImGui.GetColorU32(headerCol), rounding, ImDrawFlags.RoundCornersTop);
            drawList.AddRect(pMin, pMax, ImGui.GetColorU32(borderCol), rounding, ImDrawFlags.None, isSelected || isCurrent ? 2.5f : 1.5f);

            string title = string.IsNullOrWhiteSpace(state.Name) ? $"State {i}" : state.Name;
            if (isInitial)
                title = $"[Entry] {title}";
            string motionLabel = state.Motion is not null
                ? MotionFactoryRegistry.GetDisplayName(state.Motion.GetType())
                : "Empty";

            drawList.AddText(pMin + new Vector2(10.0f, 5.0f) * view.Zoom, ImGui.GetColorU32(ImGuiCol.Text), title);
            drawList.AddText(pMin + new Vector2(10.0f, 30.0f) * view.Zoom, ImGui.GetColorU32(ImGuiCol.TextDisabled), motionLabel);

            // Show live normalized playback bar when executing.
            if (isCurrent || isNext)
            {
                float progress = (float)(state.NormalizedPlaybackTime - Math.Floor(state.NormalizedPlaybackTime));
                if (!float.IsFinite(progress))
                    progress = 0.0f;
                Vector2 barMin = pMin + new Vector2(10.0f, nodeHeight - 14.0f) * view.Zoom;
                Vector2 barMax = new Vector2(pMax.X - 10.0f * view.Zoom, pMax.Y - 8.0f * view.Zoom);
                drawList.AddRectFilled(barMin, barMax, ImGui.GetColorU32(new Vector4(0.08f, 0.10f, 0.12f, 0.90f)), 2.0f);
                float fillWidth = MathF.Max(2.0f, (barMax.X - barMin.X) * Math.Clamp(progress, 0.0f, 1.0f));
                uint barFill = ImGui.GetColorU32(isCurrent ? new Vector4(0.28f, 0.88f, 0.44f, 0.95f) : new Vector4(1.0f, 0.72f, 0.24f, 0.95f));
                drawList.AddRectFilled(barMin, new Vector2(barMin.X + fillWidth, barMax.Y), barFill, 2.0f);
            }

            ImGui.SetCursorScreenPos(pMin);
            ImGui.PushID(i);
            ImGui.InvisibleButton("##AnimStateNode", size);

            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                clickedNode = true;
                if (view.PendingTransitionSource is not null)
                {
                    CreateAnimStateTransition(component, view.PendingTransitionSource, state, view);
                    view.PendingTransitionSource = null;
                }
                else
                {
                    view.SelectedNode = state;
                    view.SelectedTransition = null;
                }
            }

            if (view.PendingTransitionSource is null && ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0.0f))
                view.StatePositions[state] = worldPos + (ImGui.GetIO().MouseDelta / MathF.Max(1.0e-6f, view.Zoom));

            if (ImGui.BeginPopupContextItem("##AnimStateNodeCtx"))
            {
                if (ImGui.MenuItem("Make Transition"))
                    view.PendingTransitionSource = state;

                if (ImGui.MenuItem("Set as Initial State", null, false, !isInitial))
                {
                    layer.InitialState = state;
                    NotifyInspectorValueEdited(component);
                }

                if (state.Motion is AnimationClip clip && ImGui.MenuItem("Open in Animation Clip Editor"))
                    OpenAnimationClipEditor(clip);

                ImGui.Separator();
                if (ImGui.MenuItem("Delete State"))
                {
                    DeleteAnimState(component, layer, state, view);
                    ImGui.EndPopup();
                    ImGui.PopID();
                    break;
                }

                ImGui.EndPopup();
            }

            ImGui.PopID();
        }
    }

    private static void DrawAnimStateMachineDetailInspector(
        AnimStateMachineComponent component,
        AnimStateMachine stateMachine,
        AnimLayer layer,
        AnimStateMachineLayerViewState view)
    {
        if (view.SelectedTransition is not null)
        {
            DrawAnimTransitionInspector(component, stateMachine, layer, view.SelectedTransition, view);
            return;
        }

        if (view.SelectedNode is AnimState selectedState)
        {
            DrawAnimStateInspector(component, stateMachine, layer, selectedState, view);
            return;
        }

        if (ReferenceEquals(view.SelectedNode, layer.AnyState))
        {
            ImGui.TextUnformatted("Any State");
            ImGui.Separator();
            if (ImGui.SmallButton("Make Transition"))
                view.PendingTransitionSource = layer.AnyState;

            ImGui.Spacing();
            DrawOutgoingTransitionsList(component, layer, layer.AnyState, view);
            return;
        }

        ImGui.TextDisabled("Select a state or transition in the graph to inspect and edit it.");
    }

    private static void DrawAnimStateInspector(
        AnimStateMachineComponent component,
        AnimStateMachine stateMachine,
        AnimLayer layer,
        AnimState state,
        AnimStateMachineLayerViewState view)
    {
        ImGui.TextUnformatted("State");
        ImGui.Separator();

        string name = state.Name ?? string.Empty;
        if (ImGui.InputText("Name##AnimStateName", ref name, 128u))
        {
            state.Name = name;
            NotifyInspectorValueEdited(component);
        }

        float startSec = state.StartSecond;
        if (ImGui.DragFloat("Start (s)##AnimStateStart", ref startSec, 0.01f, 0.0f, 3600.0f))
        {
            state.StartSecond = MathF.Max(0.0f, startSec);
            NotifyInspectorValueEdited(component);
        }

        float endSec = state.EndSecond;
        if (ImGui.DragFloat("End (s)##AnimStateEnd", ref endSec, 0.01f, 0.0f, 3600.0f))
        {
            state.EndSecond = MathF.Max(0.0f, endSec);
            NotifyInspectorValueEdited(component);
        }

        bool isInitial = ReferenceEquals(layer.InitialState, state);
        if (!isInitial && ImGui.SmallButton("Set as Initial State"))
        {
            layer.InitialState = state;
            NotifyInspectorValueEdited(component);
        }

        if (!isInitial)
            ImGui.SameLine();
        if (ImGui.SmallButton("Make Transition"))
            view.PendingTransitionSource = state;

        ImGui.SameLine();
        if (ImGui.SmallButton("Delete State"))
        {
            DeleteAnimState(component, layer, state, view);
            return;
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Motion");

        string motionPreview = state.Motion is not null
            ? MotionFactoryRegistry.GetDisplayName(state.Motion.GetType())
            : "<None>";
        if (ImGui.BeginCombo("Motion Type##AnimStateMotionCombo", motionPreview))
        {
            if (ImGui.Selectable("<None>", state.Motion is null))
            {
                state.Motion = null;
                NotifyInspectorValueEdited(component);
                RebuildAnimStateMachinePlayback(component);
            }

            Type[] registeredMotions = MotionFactoryRegistry.GetRegisteredTypes();
            for (int i = 0; i < registeredMotions.Length; i++)
            {
                Type motionType = registeredMotions[i];
                bool isCurrentType = state.Motion?.GetType() == motionType;
                string label = MotionFactoryRegistry.GetDisplayName(motionType);
                if (ImGui.Selectable($"{label}##MotionTypeOpt{i}", isCurrentType))
                {
                    if (MotionFactoryRegistry.TryCreate(motionType, out MotionBase? created) && created is not null)
                    {
                        created.Name = string.IsNullOrWhiteSpace(state.Name) ? label : state.Name;
                        state.Motion = created;
                        if (created is MotionBricksLocomotionMotion motionBricks)
                            EnsureMotionBricksParameters(stateMachine, motionBricks);
                        else if (created is ProceduralStrideMotion strideMotion)
                            EnsureProceduralStrideParameters(stateMachine, strideMotion);
                        else if (created is HumanoidLookAtAimMotion lookAtMotion)
                            EnsureLookAtAimParameters(stateMachine, lookAtMotion);
                        else if (created is ProceduralSecondaryMotion secondaryMotion)
                            EnsureProceduralSecondaryParameters(stateMachine, secondaryMotion);
                        NotifyInspectorValueEdited(component);
                        RebuildAnimStateMachinePlayback(component);
                    }
                }
            }

            ImGui.EndCombo();
        }

        if (state.Motion is AnimationClip clip)
        {
            if (ImGui.SmallButton("Open in Animation Clip Editor"))
                OpenAnimationClipEditor(clip);
        }
        else if (state.Motion is MotionBricksLocomotionMotion motionBricks)
        {
            bool ready = motionBricks.ActiveDriver is { IsReady: true };
            Vector4 statusColor = ready
                ? new Vector4(0.32f, 0.88f, 0.44f, 1.0f)
                : new Vector4(1.0f, 0.65f, 0.25f, 1.0f);
            ImGui.TextColored(statusColor, ready ? "MotionBricks Ready" : "MotionBricks Not Ready");
            if (!string.IsNullOrWhiteSpace(motionBricks.RuntimeDiagnosticStatus))
                ImGui.TextWrapped(motionBricks.RuntimeDiagnosticStatus);

            if (ImGui.SmallButton("Ensure Parameters"))
            {
                EnsureMotionBricksParameters(stateMachine, motionBricks);
                NotifyInspectorValueEdited(component);
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Prewarm TensorRT"))
                MotionBricksLocomotionRuntimeServices.Prewarm();

            if (!ready)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Setup Dependencies..."))
                    _showDependenciesAndBuildsPanel = true;
            }
        }
        else if (state.Motion is ProceduralStrideMotion strideMotion)
        {
            if (ImGui.SmallButton("Ensure Stride Parameters"))
            {
                EnsureProceduralStrideParameters(stateMachine, strideMotion);
                NotifyInspectorValueEdited(component);
            }
        }
        else if (state.Motion is HumanoidLookAtAimMotion lookAtMotion)
        {
            if (ImGui.SmallButton("Ensure Look-At Parameters"))
            {
                EnsureLookAtAimParameters(stateMachine, lookAtMotion);
                NotifyInspectorValueEdited(component);
            }
        }
        else if (state.Motion is ProceduralSecondaryMotion secondaryMotion)
        {
            if (ImGui.SmallButton("Ensure Secondary Parameters"))
            {
                EnsureProceduralSecondaryParameters(stateMachine, secondaryMotion);
                NotifyInspectorValueEdited(component);
            }
        }
        else if (state.Motion is MotionWarpingMotion warpingMotion)
        {
            if (ImGui.SmallButton("+ Add Warp Window"))
            {
                warpingMotion.WarpWindows.Add(new MotionWarpWindow
                {
                    TargetName = $"Target{warpingMotion.WarpWindows.Count + 1}",
                });
                NotifyInspectorValueEdited(component);
            }
        }

        if (state.Motion is not null)
        {
            ImGui.Spacing();
            if (ImGui.CollapsingHeader("Motion Properties", ImGuiTreeNodeFlags.DefaultOpen))
            {
                HashSet<object> visited = new(ReferenceEqualityComparer.Instance);
                DrawSettingsProperties(GetInspectorTargets(state.Motion), visited);
            }
        }

        ImGui.Separator();
        DrawOutgoingTransitionsList(component, layer, state, view);
    }

    private static void DrawOutgoingTransitionsList(
        AnimStateMachineComponent component,
        AnimLayer layer,
        AnimStateBase source,
        AnimStateMachineLayerViewState view)
    {
        ImGui.TextUnformatted($"Outgoing Transitions ({source.Transitions.Count})");
        if (source.Transitions.Count == 0)
        {
            ImGui.TextDisabled("No outgoing transitions.");
            return;
        }

        for (int i = 0; i < source.Transitions.Count; i++)
        {
            AnimStateTransition transition = source.Transitions[i];
            string destName = transition.DestinationState?.Name ?? "<None>";
            ImGui.PushID(i);
            if (ImGui.Selectable($"-> {destName} ({transition.Conditions.Count} cond)", ReferenceEquals(view.SelectedTransition, transition)))
            {
                view.SelectedTransition = transition;
                view.SelectedNode = null;
            }
            ImGui.PopID();
        }
    }

    private static void DrawAnimTransitionInspector(
        AnimStateMachineComponent component,
        AnimStateMachine stateMachine,
        AnimLayer layer,
        AnimStateTransition transition,
        AnimStateMachineLayerViewState view)
    {
        ImGui.TextUnformatted("Transition");
        ImGui.Separator();

        string transitionName = transition.Name ?? string.Empty;
        if (ImGui.InputText("Name##TransitionName", ref transitionName, 128u))
        {
            transition.Name = transitionName;
            NotifyInspectorValueEdited(component);
        }

        string destPreview = transition.DestinationState?.Name ?? "<Select Destination>";
        if (ImGui.BeginCombo("Destination##TransitionDest", destPreview))
        {
            for (int i = 0; i < layer.States.Count; i++)
            {
                AnimState candidate = layer.States[i];
                bool selected = ReferenceEquals(candidate, transition.DestinationState);
                if (ImGui.Selectable($"{candidate.Name}##DestState{i}", selected))
                {
                    transition.DestinationState = candidate;
                    NotifyInspectorValueEdited(component);
                }
            }
            ImGui.EndCombo();
        }

        float blendDuration = transition.BlendDuration;
        if (ImGui.DragFloat("Blend Duration", ref blendDuration, 0.01f, 0.0f, 30.0f))
        {
            transition.BlendDuration = MathF.Max(0.0f, blendDuration);
            NotifyInspectorValueEdited(component);
        }

        bool fixedDuration = transition.FixedDuration;
        if (ImGui.Checkbox("Fixed Duration (seconds)", ref fixedDuration))
        {
            transition.FixedDuration = fixedDuration;
            NotifyInspectorValueEdited(component);
        }

        int blendTypeIdx = (int)transition.BlendType;
        string[] blendTypeNames = Enum.GetNames<EAnimBlendType>();
        if (ImGui.Combo("Blend Type", ref blendTypeIdx, blendTypeNames, blendTypeNames.Length))
        {
            transition.BlendType = (EAnimBlendType)blendTypeIdx;
            NotifyInspectorValueEdited(component);
        }

        bool hasExitTime = transition.HasExitTime;
        if (ImGui.Checkbox("Has Exit Time", ref hasExitTime))
        {
            transition.HasExitTime = hasExitTime;
            NotifyInspectorValueEdited(component);
        }

        if (transition.HasExitTime)
        {
            float exitTime = transition.ExitTime;
            if (ImGui.DragFloat("Exit Time", ref exitTime, 0.01f, 0.0f, 10.0f))
            {
                transition.ExitTime = MathF.Max(0.0f, exitTime);
                NotifyInspectorValueEdited(component);
            }
        }

        int priority = transition.Priority;
        if (ImGui.DragInt("Priority", ref priority, 1.0f))
        {
            transition.Priority = priority;
            NotifyInspectorValueEdited(component);
        }

        int interruptIdx = (int)transition.InterruptionSource;
        string[] interruptNames = Enum.GetNames<ETransitionInterruptionSource>();
        if (ImGui.Combo("Interruption", ref interruptIdx, interruptNames, interruptNames.Length))
        {
            transition.InterruptionSource = (ETransitionInterruptionSource)interruptIdx;
            NotifyInspectorValueEdited(component);
        }

        bool canSelf = transition.CanTransitionToSelf;
        if (ImGui.Checkbox("Can Transition To Self", ref canSelf))
        {
            transition.CanTransitionToSelf = canSelf;
            NotifyInspectorValueEdited(component);
        }

        if (ImGui.SmallButton("Delete Transition"))
        {
            transition.Owner?.Transitions.Remove(transition);
            view.SelectedTransition = null;
            NotifyInspectorValueEdited(component);
            return;
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Conditions");
        ImGui.SameLine();
        if (ImGui.SmallButton("+ Condition"))
        {
            string defaultParam = string.Empty;
            foreach (string key in stateMachine.Variables.Keys)
            {
                defaultParam = key;
                break;
            }

            transition.Conditions.Add(new AnimTransitionCondition
            {
                ParameterName = defaultParam,
                Comparison = AnimTransitionCondition.EComparison.IsTrue,
            });
            NotifyInspectorValueEdited(component);
        }

        string[] comparisonNames = Enum.GetNames<AnimTransitionCondition.EComparison>();
        for (int i = 0; i < transition.Conditions.Count; i++)
        {
            AnimTransitionCondition cond = transition.Conditions[i];
            ImGui.PushID(i);

            string paramPreview = string.IsNullOrWhiteSpace(cond.ParameterName) ? "<Param>" : cond.ParameterName;
            ImGui.SetNextItemWidth(115.0f);
            if (ImGui.BeginCombo("##CondParam", paramPreview))
            {
                foreach (string key in stateMachine.Variables.Keys)
                {
                    if (ImGui.Selectable(key, string.Equals(key, cond.ParameterName, StringComparison.Ordinal)))
                    {
                        cond.ParameterName = key;
                        NotifyInspectorValueEdited(component);
                    }
                }
                ImGui.EndCombo();
            }

            ImGui.SameLine();
            int compIdx = (int)cond.Comparison;
            ImGui.SetNextItemWidth(110.0f);
            if (ImGui.Combo("##CondOp", ref compIdx, comparisonNames, comparisonNames.Length))
            {
                cond.Comparison = (AnimTransitionCondition.EComparison)compIdx;
                NotifyInspectorValueEdited(component);
            }

            bool needsThreshold = cond.Comparison is not (AnimTransitionCondition.EComparison.IsTrue or AnimTransitionCondition.EComparison.IsFalse);
            if (needsThreshold)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(65.0f);
                if (stateMachine.Variables.TryGetValue(cond.ParameterName, out AnimVar? boundVar))
                {
                    if (boundVar is AnimInt)
                    {
                        int v = cond.ComparisonInt;
                        if (ImGui.DragInt("##CondInt", ref v, 1.0f))
                        {
                            cond.ComparisonInt = v;
                            NotifyInspectorValueEdited(component);
                        }
                    }
                    else if (boundVar is AnimBool)
                    {
                        bool v = cond.ComparisonBool;
                        if (ImGui.Checkbox("##CondBool", ref v))
                        {
                            cond.ComparisonBool = v;
                            NotifyInspectorValueEdited(component);
                        }
                    }
                    else
                    {
                        float v = cond.ComparisonFloat;
                        if (ImGui.DragFloat("##CondFloat", ref v, 0.05f))
                        {
                            cond.ComparisonFloat = v;
                            NotifyInspectorValueEdited(component);
                        }
                    }
                }
                else
                {
                    float v = cond.ComparisonFloat;
                    if (ImGui.DragFloat("##CondFloatFallback", ref v, 0.05f))
                    {
                        cond.ComparisonFloat = v;
                        NotifyInspectorValueEdited(component);
                    }
                }
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("X"))
            {
                transition.Conditions.RemoveAt(i);
                NotifyInspectorValueEdited(component);
                ImGui.PopID();
                break;
            }

            ImGui.PopID();
        }
    }

    private static void CreateAnimStateTransition(
        AnimStateMachineComponent component,
        AnimStateBase source,
        AnimState destination,
        AnimStateMachineLayerViewState view)
    {
        var transition = new AnimStateTransition
        {
            Name = $"{(source is AnimState srcState ? srcState.Name : "Any")} -> {destination.Name}",
            DestinationState = destination,
            BlendDuration = 0.2f,
            FixedDuration = true,
            HasExitTime = false,
        };
        source.Transitions.Add(transition);
        view.SelectedTransition = transition;
        view.SelectedNode = null;
        NotifyInspectorValueEdited(component);
    }

    private static void DeleteAnimState(
        AnimStateMachineComponent component,
        AnimLayer layer,
        AnimState state,
        AnimStateMachineLayerViewState view)
    {
        RemoveTransitionsToState(layer.AnyState, state);
        for (int i = 0; i < layer.States.Count; i++)
            RemoveTransitionsToState(layer.States[i], state);

        bool wasInitial = ReferenceEquals(layer.InitialState, state);
        layer.States.Remove(state);
        view.StatePositions.Remove(state);
        if (wasInitial)
            layer.InitialState = layer.States.Count > 0 ? layer.States[0] : null;
        if (ReferenceEquals(view.SelectedNode, state))
            view.SelectedNode = null;
        if (ReferenceEquals(view.PendingTransitionSource, state))
            view.PendingTransitionSource = null;
        if (view.SelectedTransition is not null && ReferenceEquals(view.SelectedTransition.DestinationState, state))
            view.SelectedTransition = null;

        NotifyInspectorValueEdited(component);
        RebuildAnimStateMachinePlayback(component);
    }

    private static void RemoveTransitionsToState(AnimStateBase source, AnimState target)
    {
        for (int i = source.Transitions.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(source.Transitions[i].DestinationState, target))
                source.Transitions.RemoveAt(i);
        }
    }

    private static List<AnimGraphEdgeRenderInfo> CollectAnimGraphEdges(
        AnimLayer layer,
        AnimStateMachineLayerViewState view,
        Vector2 canvasPos,
        float nodeWidth,
        float nodeHeight)
    {
        var result = new List<AnimGraphEdgeRenderInfo>();
        AppendEdgesFromSource(layer.AnyState, layer, view, canvasPos, nodeWidth, nodeHeight, isAnyState: true, result);
        for (int i = 0; i < layer.States.Count; i++)
            AppendEdgesFromSource(layer.States[i], layer, view, canvasPos, nodeWidth, nodeHeight, isAnyState: false, result);
        return result;
    }

    private static void AppendEdgesFromSource(
        AnimStateBase source,
        AnimLayer layer,
        AnimStateMachineLayerViewState view,
        Vector2 canvasPos,
        float nodeWidth,
        float nodeHeight,
        bool isAnyState,
        List<AnimGraphEdgeRenderInfo> destination)
    {
        Vector2 srcWorld = GetAnimNodeWorldPosition(source, view);
        Vector2 srcCenterWorld = srcWorld + new Vector2(nodeWidth * 0.5f, (isAnyState ? 56.0f : nodeHeight) * 0.5f);

        for (int i = 0; i < source.Transitions.Count; i++)
        {
            AnimStateTransition transition = source.Transitions[i];
            if (transition.DestinationState is not AnimState dest || !view.StatePositions.TryGetValue(dest, out Vector2 destWorld))
                continue;

            Vector2 destCenterWorld = destWorld + new Vector2(nodeWidth * 0.5f, nodeHeight * 0.5f);
            Vector2 dir = destCenterWorld - srcCenterWorld;
            float len = dir.Length();
            if (len < 1.0f)
                continue;

            dir /= len;
            Vector2 perp = new(-dir.Y, dir.X);
            bool hasReverse = HasReverseTransition(dest, source);
            float lateralOffset = hasReverse ? 10.0f : 0.0f;

            Vector2 startScreen = AnimWorldToScreen(canvasPos, view, srcCenterWorld + dir * (nodeWidth * 0.35f) + perp * lateralOffset);
            Vector2 endScreen = AnimWorldToScreen(canvasPos, view, destCenterWorld - dir * (nodeWidth * 0.42f) + perp * lateralOffset);
            destination.Add(new AnimGraphEdgeRenderInfo(
                source,
                transition,
                startScreen,
                endScreen,
                IsActive: ReferenceEquals(layer.CurrentTransition, transition),
                IsSelected: ReferenceEquals(view.SelectedTransition, transition)));
        }
    }

    private static bool HasReverseTransition(AnimState destination, AnimStateBase source)
    {
        if (source is not AnimState sourceState)
            return false;

        for (int i = 0; i < destination.Transitions.Count; i++)
        {
            if (ReferenceEquals(destination.Transitions[i].DestinationState, sourceState))
                return true;
        }

        return false;
    }

    private static void DrawAnimGraphEdges(ImDrawListPtr drawList, List<AnimGraphEdgeRenderInfo> edges, float zoom)
    {
        for (int i = 0; i < edges.Count; i++)
        {
            AnimGraphEdgeRenderInfo edge = edges[i];
            Vector4 color = edge.IsSelected
                ? new Vector4(0.42f, 0.85f, 1.0f, 1.0f)
                : edge.IsActive
                    ? new Vector4(1.0f, 0.78f, 0.24f, 1.0f)
                    : new Vector4(0.74f, 0.78f, 0.86f, 0.85f);
            float thickness = (edge.IsSelected || edge.IsActive ? 3.0f : 2.0f) * zoom;
            DrawArrowSegment(drawList, edge.StartScreen, edge.EndScreen, ImGui.GetColorU32(color), thickness);
        }
    }

    private static void DrawArrowSegment(ImDrawListPtr drawList, Vector2 start, Vector2 end, uint color, float thickness)
    {
        drawList.AddLine(start, end, color, thickness);
        Vector2 delta = end - start;
        float len = delta.Length();
        if (len < 6.0f)
            return;

        Vector2 dir = delta / len;
        Vector2 perp = new(-dir.Y, dir.X);
        float headLen = Math.Clamp(thickness * 4.2f, 7.0f, 14.0f);
        float headHalfWidth = headLen * 0.55f;
        Vector2 mid = (start + end) * 0.5f;
        Vector2 tip = mid + dir * (headLen * 0.5f);
        Vector2 baseCenter = tip - dir * headLen;
        drawList.AddTriangleFilled(tip, baseCenter + perp * headHalfWidth, baseCenter - perp * headHalfWidth, color);
    }

    private static AnimStateTransition? HitTestAnimGraphEdge(List<AnimGraphEdgeRenderInfo> edges, Vector2 point, float thresholdPixels)
    {
        float bestDistSq = thresholdPixels * thresholdPixels;
        AnimStateTransition? hit = null;
        for (int i = 0; i < edges.Count; i++)
        {
            AnimGraphEdgeRenderInfo edge = edges[i];
            float distSq = DistancePointToSegmentSquared(point, edge.StartScreen, edge.EndScreen);
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                hit = edge.Transition;
            }
        }

        return hit;
    }

    private static float DistancePointToSegmentSquared(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float abLenSq = ab.LengthSquared();
        if (abLenSq <= 1.0e-6f)
            return Vector2.DistanceSquared(p, a);

        float t = Math.Clamp(Vector2.Dot(p - a, ab) / abLenSq, 0.0f, 1.0f);
        Vector2 proj = a + ab * t;
        return Vector2.DistanceSquared(p, proj);
    }

    private static AnimStateMachineLayerViewState GetOrCreateAnimLayerViewState(AnimLayer layer)
        => _animStateMachineLayerViews.GetValue(layer, static _ => new AnimStateMachineLayerViewState());

    private static void EnsureAnimLayerLayout(AnimLayer layer, AnimStateMachineLayerViewState view)
    {
        if (!view.HasLayout)
        {
            AutoLayoutAnimLayer(layer, view);
            view.HasLayout = true;
            return;
        }

        for (int i = 0; i < layer.States.Count; i++)
        {
            AnimState state = layer.States[i];
            if (!view.StatePositions.ContainsKey(state))
                view.StatePositions[state] = new Vector2(300.0f + i * 260.0f, 140.0f);
        }
    }

    private static void AutoLayoutAnimLayer(AnimLayer layer, AnimStateMachineLayerViewState view)
    {
        view.AnyStatePosition = new Vector2(40.0f, 40.0f);
        view.StatePositions.Clear();

        int columns = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(Math.Max(1, layer.States.Count))));
        for (int i = 0; i < layer.States.Count; i++)
        {
            int col = i % columns;
            int row = i / columns;
            view.StatePositions[layer.States[i]] = new Vector2(300.0f + col * 260.0f, 60.0f + row * 130.0f);
        }
    }

    private static Vector2 GetAnimNodeWorldPosition(AnimStateBase node, AnimStateMachineLayerViewState view)
    {
        if (node is AnimState state && view.StatePositions.TryGetValue(state, out Vector2 pos))
            return pos;
        return view.AnyStatePosition;
    }

    private static Vector2 AnimWorldToScreen(Vector2 canvasPos, AnimStateMachineLayerViewState view, Vector2 worldPos)
        => canvasPos + (worldPos + view.Pan) * view.Zoom;

    private static AnimStateMachineComponent? ResolveAnimStateMachineComponent()
    {
        if (_animStateMachinePinnedComponent is not null
            && _animStateMachinePinnedComponent.TryGetTarget(out AnimStateMachineComponent? pinned)
            && pinned is not null
            && !pinned.IsDestroyed)
        {
            return pinned;
        }

        SceneNode? selected = Selection.LastSceneNode;
        if (selected is not null)
        {
            AnimStateMachineComponent? onSelected = selected.GetComponent<AnimStateMachineComponent>()
                ?? selected.GetComponentInHierarchy(nameof(AnimStateMachineComponent)) as AnimStateMachineComponent;
            if (onSelected is not null && !onSelected.IsDestroyed)
                return onSelected;
        }

        CollectWorldAnimStateMachines(_animStateMachineWorldScratch);
        return _animStateMachineWorldScratch.Count > 0 ? _animStateMachineWorldScratch[0] : null;
    }

    private static void CollectWorldAnimStateMachines(List<AnimStateMachineComponent> destination)
    {
        destination.Clear();
        RuntimeWorld? world = Selection.LastSceneNode?.World as RuntimeWorld ?? TryGetActiveWorldInstance();
        if (world is null)
            return;

        for (int i = 0; i < world.RootNodes.Count; i++)
        {
            SceneNode? root = world.RootNodes[i];
            if (root is not null)
                CollectAnimStateMachinesRecursive(root, destination);
        }
    }

    private static void CollectAnimStateMachinesRecursive(SceneNode node, List<AnimStateMachineComponent> destination)
    {
        for (int i = 0; i < node.Components.Count; i++)
        {
            if (node.Components[i] is AnimStateMachineComponent comp && !comp.IsDestroyed)
                destination.Add(comp);
        }

        IReadOnlyList<TransformBase> children = node.Transform.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i]?.SceneNode is SceneNode childNode)
                CollectAnimStateMachinesRecursive(childNode, destination);
        }
    }

    private static string FormatAnimStateMachineLabel(AnimStateMachineComponent component)
    {
        string nodeName = component.SceneNode?.Name ?? "<Detached>";
        string compName = string.IsNullOrWhiteSpace(component.Name) ? nameof(AnimStateMachineComponent) : component.Name;
        return $"{nodeName} / {compName}";
    }

    private static string MakeUniqueAnimStateName(AnimLayer layer, string baseName)
    {
        string candidate = baseName;
        int suffix = 1;
        while (HasStateWithName(layer, candidate))
            candidate = $"{baseName} {suffix++}";
        return candidate;
    }

    private static bool HasStateWithName(AnimLayer layer, string name)
    {
        for (int i = 0; i < layer.States.Count; i++)
        {
            if (string.Equals(layer.States[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static MotionBricksLocomotionMotion? FindFirstMotionBricksMotion(AnimStateMachine stateMachine)
    {
        for (int l = 0; l < stateMachine.Layers.Count; l++)
        {
            AnimLayer layer = stateMachine.Layers[l];
            for (int s = 0; s < layer.States.Count; s++)
            {
                if (layer.States[s].Motion is MotionBricksLocomotionMotion mb)
                    return mb;
            }
        }

        return null;
    }

    private static void EnsureMotionBricksParameters(AnimStateMachine stateMachine, MotionBricksLocomotionMotion motion)
    {
        EnsureFloatParam(stateMachine, motion.MoveXParameter, 0.0f);
        EnsureFloatParam(stateMachine, motion.MoveYParameter, 0.0f);
        EnsureFloatParam(stateMachine, motion.DesiredFacingYawParameter, 0.0f);
        EnsureBoolParam(stateMachine, motion.StrafeModeParameter, false);
        EnsureIntParam(stateMachine, motion.StyleParameter, 0);
        EnsureBoolParam(stateMachine, motion.IsGroundedParameter, true);
        EnsureFloatParam(stateMachine, motion.VerticalVelocityParameter, 0.0f);
    }

    private static void EnsureProceduralStrideParameters(AnimStateMachine stateMachine, ProceduralStrideMotion motion)
    {
        EnsureFloatParam(stateMachine, motion.MoveXParameter, 0.0f);
        EnsureFloatParam(stateMachine, motion.MoveYParameter, 0.0f);
        EnsureFloatParam(stateMachine, motion.DesiredFacingYawParameter, 0.0f);
        EnsureBoolParam(stateMachine, motion.StrafeModeParameter, false);
        EnsureIntParam(stateMachine, motion.StyleParameter, 0);
        EnsureBoolParam(stateMachine, motion.IsGroundedParameter, true);
    }

    private static void EnsureLookAtAimParameters(AnimStateMachine stateMachine, HumanoidLookAtAimMotion motion)
    {
        EnsureFloatParam(stateMachine, motion.AimYawParameter, 0.0f);
        EnsureFloatParam(stateMachine, motion.AimPitchParameter, 0.0f);
        EnsureFloatParam(stateMachine, motion.AimWeightParameter, 1.0f);
        EnsureFloatParam(stateMachine, motion.ArmAimWeightParameter, 0.0f);
    }

    private static void EnsureProceduralSecondaryParameters(AnimStateMachine stateMachine, ProceduralSecondaryMotion motion)
    {
        EnsureFloatParam(stateMachine, motion.MoveXParameter, 0.0f);
        EnsureFloatParam(stateMachine, motion.MoveYParameter, 0.0f);
        EnsureFloatParam(stateMachine, motion.DesiredFacingYawParameter, 0.0f);
        EnsureFloatParam(stateMachine, motion.ExertionParameter, 0.0f);
    }

    private static void EnsureFloatParam(AnimStateMachine stateMachine, string name, float defaultValue)
    {
        if (!string.IsNullOrWhiteSpace(name) && !stateMachine.Variables.ContainsKey(name))
            stateMachine.Variables[name] = new AnimFloat(name, defaultValue);
    }

    private static void EnsureIntParam(AnimStateMachine stateMachine, string name, int defaultValue)
    {
        if (!string.IsNullOrWhiteSpace(name) && !stateMachine.Variables.ContainsKey(name))
            stateMachine.Variables[name] = new AnimInt(name, defaultValue);
    }

    private static void EnsureBoolParam(AnimStateMachine stateMachine, string name, bool defaultValue)
    {
        if (!string.IsNullOrWhiteSpace(name) && !stateMachine.Variables.ContainsKey(name))
            stateMachine.Variables[name] = new AnimBool(name, defaultValue);
    }

    private static void RebuildAnimStateMachinePlayback(AnimStateMachineComponent component)
    {
        if (!component.IsActive)
            return;

        component.IsActive = false;
        component.SetSuspendedByClip(false);
        component.IsActive = true;
    }
}
