using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Editor;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using static XREngine.Editor.EditorImGuiUI;

namespace XREngine.Editor.ComponentEditors;

public sealed class HumanoidComponentEditor : IXRComponentEditor
{
    private static readonly Vector4 MissingColor = new(0.90f, 0.40f, 0.40f, 1.00f);
    private static readonly Vector4 AssignedColor = new(0.60f, 0.85f, 0.60f, 1.00f);
    private static bool _showZeroMuscleValues = false;

    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not HumanoidComponent humanoid)
        {
            DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(humanoid, visited, "Humanoid Editor"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        DrawActionButtons(humanoid);
        ImGui.SeparatorText("General");
        DrawGeneralSection(humanoid);
        ImGui.SeparatorText("IK Chains");
        DrawIkSection(humanoid);
        ImGui.SeparatorText("Targets");
        DrawTargetSection(humanoid);
        ImGui.SeparatorText("Bone Mapping");
        DrawBoneMappingSection(humanoid);
        ImGui.SeparatorText("Per-Muscle Settings");
        DrawPerMuscleSettingsSection(humanoid);
        ImGui.SeparatorText("Muscle Values");
        DrawMuscleValuesSection(humanoid);

        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawActionButtons(HumanoidComponent humanoid)
    {
        if (ImGui.Button("Reset Bind Pose"))
            RunSceneEdit(humanoid.ResetAllTransformsToBindPose);

        ImGui.SameLine();
        if (ImGui.Button("Reset Pose"))
            RunSceneEdit(humanoid.ResetPose);

        ImGui.SameLine();
        if (ImGui.Button("Auto Detect Rig"))
            RunSceneEdit(humanoid.SetFromNode);

        ImGui.SameLine();
        if (ImGui.Button("Clear IK Targets"))
            RunSceneEdit(humanoid.ClearIKTargets);

        ImGui.Spacing();
    }

    private static void DrawGeneralSection(HumanoidComponent humanoid)
    {
        ImGui.TextDisabled("Humanoid IK playback now runs through HumanoidIKSolverComponent.");

        bool debugVisibility = humanoid.RenderInfo.IsVisible;
        if (ImGui.Checkbox("Show Debug Skeleton", ref debugVisibility))
        {
            using var _ = Undo.TrackChange("Toggle Debug Skeleton", humanoid);
            humanoid.RenderInfo.IsVisible = debugVisibility;
        }
    }

    private static void DrawIkSection(HumanoidComponent humanoid)
    {
        if (!ImGui.BeginTable("HumanoidIkToggles", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("Chain", ImGuiTableColumnFlags.WidthStretch, 0.4f);
        ImGui.TableSetupColumn("Left", ImGuiTableColumnFlags.WidthStretch, 0.3f);
        ImGui.TableSetupColumn("Right", ImGuiTableColumnFlags.WidthStretch, 0.3f);
        ImGui.TableHeadersRow();

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Arms");
        ImGui.TableSetColumnIndex(1);
        DrawIkToggle("LeftArm", humanoid.LeftArmIKEnabled, value => humanoid.LeftArmIKEnabled = value, humanoid);
        ImGui.TableSetColumnIndex(2);
        DrawIkToggle("RightArm", humanoid.RightArmIKEnabled, value => humanoid.RightArmIKEnabled = value, humanoid);

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Legs");
        ImGui.TableSetColumnIndex(1);
        DrawIkToggle("LeftLeg", humanoid.LeftLegIKEnabled, value => humanoid.LeftLegIKEnabled = value, humanoid);
        ImGui.TableSetColumnIndex(2);
        DrawIkToggle("RightLeg", humanoid.RightLegIKEnabled, value => humanoid.RightLegIKEnabled = value, humanoid);

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Spine");
        ImGui.TableSetColumnIndex(1);
        DrawIkToggle("HipToHead", humanoid.HipToHeadIKEnabled, value => humanoid.HipToHeadIKEnabled = value, humanoid);
        ImGui.TableSetColumnIndex(2);
        ImGui.TextDisabled("--");

        ImGui.EndTable();
    }

    private static void DrawIkToggle(string id, bool value, Action<bool> setter, HumanoidComponent humanoid)
    {
        ImGui.PushID(id);
        bool toggle = value;
        if (ImGui.Checkbox("##Toggle", ref toggle))
        {
            using var _ = Undo.TrackChange($"Toggle {id} IK", humanoid);
            setter(toggle);
        }
        ImGui.PopID();
    }

    private static void DrawTargetSection(HumanoidComponent humanoid)
    {
        if (!ImGui.BeginTable("HumanoidTargets", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
            return;

        ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthStretch, 0.25f);
        ImGui.TableSetupColumn("Node", ImGuiTableColumnFlags.WidthStretch, 0.30f);
        ImGui.TableSetupColumn("Offset", ImGuiTableColumnFlags.WidthStretch, 0.25f);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthStretch, 0.20f);
        ImGui.TableHeadersRow();

        DrawTargetRow("Head", humanoid.HeadTarget, value => humanoid.HeadTarget = value, humanoid);
        DrawTargetRow("Chest", humanoid.ChestTarget, value => humanoid.ChestTarget = value, humanoid);
        DrawTargetRow("Hips", humanoid.HipsTarget, value => humanoid.HipsTarget = value, humanoid);
        DrawTargetRow("Left Hand", humanoid.LeftHandTarget, value => humanoid.LeftHandTarget = value, humanoid);
        DrawTargetRow("Right Hand", humanoid.RightHandTarget, value => humanoid.RightHandTarget = value, humanoid);
        DrawTargetRow("Left Foot", humanoid.LeftFootTarget, value => humanoid.LeftFootTarget = value, humanoid);
        DrawTargetRow("Right Foot", humanoid.RightFootTarget, value => humanoid.RightFootTarget = value, humanoid);
        DrawTargetRow("Left Elbow", humanoid.LeftElbowTarget, value => humanoid.LeftElbowTarget = value, humanoid);
        DrawTargetRow("Right Elbow", humanoid.RightElbowTarget, value => humanoid.RightElbowTarget = value, humanoid);
        DrawTargetRow("Left Knee", humanoid.LeftKneeTarget, value => humanoid.LeftKneeTarget = value, humanoid);
        DrawTargetRow("Right Knee", humanoid.RightKneeTarget, value => humanoid.RightKneeTarget = value, humanoid);

        ImGui.EndTable();
    }

    private static void DrawTargetRow(string label, (TransformBase? tfm, Matrix4x4 offset) target, Action<(TransformBase? tfm, Matrix4x4 offset)> setter, HumanoidComponent humanoid)
    {
        ImGui.PushID(label);
        ImGui.TableNextRow();

        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(label);

        ImGui.TableSetColumnIndex(1);
        string nodeName = target.tfm?.SceneNode?.Name ?? "<unassigned>";
        Vector4 nodeColor = target.tfm is null ? MissingColor : AssignedColor;
        ImGui.TextColored(nodeColor, nodeName);
        Vector2 nodeRectMin = ImGui.GetItemRectMin();
        Vector2 nodeRectMax = ImGui.GetItemRectMax();
        TryHandleSceneNodeDrop(sceneNode =>
        {
            using var _ = Undo.TrackChange($"Set {label} Target", humanoid);
            setter((sceneNode.Transform, target.offset));
            return true;
        }, out bool targetPreviewActive);
        if (targetPreviewActive)
        {
            DrawDropHighlight(nodeRectMin, nodeRectMax);
            ImGui.SetTooltip("Drop a scene node to assign this target.");
        }

        ImGui.TableSetColumnIndex(2);
        Vector3 translation = target.offset.Translation;
        ImGui.SetNextItemWidth(-1f);
        bool offsetChanged = ImGui.DragFloat3("##Offset", ref translation, 0.01f);
        ImGuiUndoHelper.TrackDragUndo($"{label} Offset", humanoid);

        ImGui.TableSetColumnIndex(3);
        bool selectionAvailable = Selection.SceneNode?.Transform is not null;
        if (!selectionAvailable)
            ImGui.BeginDisabled();
        if (ImGui.Button("Use Selected"))
        {
            var selectedTransform = Selection.SceneNode?.Transform;
            if (selectedTransform is not null)
            {
                using var _ = Undo.TrackChange($"Set {label} Target", humanoid);
                setter((selectedTransform, target.offset));
            }
        }
        if (!selectionAvailable)
            ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Clear"))
        {
            using var _ = Undo.TrackChange($"Clear {label} Target", humanoid);
            setter((null, Matrix4x4.Identity));
        }

        if (offsetChanged)
        {
            var offset = target.offset;
            offset.Translation = translation;
            setter((target.tfm, offset));
        }

        ImGui.PopID();
    }

    private static void DrawBoneMappingSection(HumanoidComponent humanoid)
    {
        DrawBoneGroup("Core", [("Hips", humanoid.Hips), ("Spine", humanoid.Spine), ("Chest", humanoid.Chest), ("Neck", humanoid.Neck), ("Head", humanoid.Head), ("Jaw", humanoid.Jaw)]);

        if (ImGui.TreeNodeEx("Eyes", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawBoneDef("Eyes Target", humanoid.EyesTarget);
            DrawBoneDef("Left Eye", humanoid.Left.Eye);
            DrawBoneDef("Right Eye", humanoid.Right.Eye);
            ImGui.TreePop();
        }

        DrawBodySide("Left Side", humanoid.Left);
        DrawBodySide("Right Side", humanoid.Right);
    }

    // ── Per-Muscle Settings (degree ranges) ─────────────────────────

    private static void DrawPerMuscleSettingsSection(HumanoidComponent humanoid)
    {
        var s = humanoid.Settings;

        // ── Global controls ─────────────────────────────────────────
        float scale = s.MuscleInputScale;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.DragFloat("Muscle Input Scale", ref scale, 0.01f, 0.01f, 5.0f, "%.2f"))
        {
            using var _ = Undo.TrackChange("Muscle Input Scale", s);
            s.MuscleInputScale = scale;
        }

        ImGui.SameLine();
        if (ImGui.Button("Negate All Ranges"))
        {
            using var _ = Undo.TrackChange("Negate All Ranges", s);
            s.NegateAllRanges();
        }

        ImGui.TextDisabled($"Profile: {s.ProfileSource ?? "none"}  Confidence: {s.ProfileConfidence:P0}");
        ImGui.Spacing();

        // ── Body (Spine / Chest / Upper Chest) ──────────────────────
        if (ImGui.TreeNodeEx("Body", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawMuscleRange("Spine Front-Back", s, () => s.SpineFrontBackDegRange, v => s.SpineFrontBackDegRange = v);
            DrawMuscleRange("Spine Left-Right", s, () => s.SpineLeftRightDegRange, v => s.SpineLeftRightDegRange = v);
            DrawMuscleRange("Spine Twist L-R", s, () => s.SpineTwistLeftRightDegRange, v => s.SpineTwistLeftRightDegRange = v);
            DrawMuscleRange("Chest Front-Back", s, () => s.ChestFrontBackDegRange, v => s.ChestFrontBackDegRange = v);
            DrawMuscleRange("Chest Left-Right", s, () => s.ChestLeftRightDegRange, v => s.ChestLeftRightDegRange = v);
            DrawMuscleRange("Chest Twist L-R", s, () => s.ChestTwistLeftRightDegRange, v => s.ChestTwistLeftRightDegRange = v);
            DrawMuscleRange("UpperChest Front-Back", s, () => s.UpperChestFrontBackDegRange, v => s.UpperChestFrontBackDegRange = v);
            DrawMuscleRange("UpperChest Left-Right", s, () => s.UpperChestLeftRightDegRange, v => s.UpperChestLeftRightDegRange = v);
            DrawMuscleRange("UpperChest Twist L-R", s, () => s.UpperChestTwistLeftRightDegRange, v => s.UpperChestTwistLeftRightDegRange = v);
            ImGui.TreePop();
        }

        // ── Head / Neck ─────────────────────────────────────────────
        if (ImGui.TreeNodeEx("Head & Neck", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawMuscleRange("Neck Nod Down-Up", s, () => s.NeckNodDownUpDegRange, v => s.NeckNodDownUpDegRange = v);
            DrawMuscleRange("Neck Turn L-R", s, () => s.NeckTurnLeftRightDegRange, v => s.NeckTurnLeftRightDegRange = v);
            DrawMuscleRange("Neck Tilt L-R", s, () => s.NeckTiltLeftRightDegRange, v => s.NeckTiltLeftRightDegRange = v);
            DrawMuscleRange("Head Nod Down-Up", s, () => s.HeadNodDownUpDegRange, v => s.HeadNodDownUpDegRange = v);
            DrawMuscleRange("Head Turn L-R", s, () => s.HeadTurnLeftRightDegRange, v => s.HeadTurnLeftRightDegRange = v);
            DrawMuscleRange("Head Tilt L-R", s, () => s.HeadTiltLeftRightDegRange, v => s.HeadTiltLeftRightDegRange = v);
            ImGui.TreePop();
        }

        // ── Jaw ─────────────────────────────────────────────────────
        if (ImGui.TreeNode("Jaw"))
        {
            DrawMuscleRange("Jaw Left-Right", s, () => s.JawLeftRightDegRange, v => s.JawLeftRightDegRange = v);
            DrawMuscleRange("Jaw Close", s, () => s.JawCloseDegRange, v => s.JawCloseDegRange = v);
            ImGui.TreePop();
        }

        // ── Eyes ────────────────────────────────────────────────────
        if (ImGui.TreeNode("Eyes"))
        {
            DrawMuscleRange("Left Eye Down-Up", s, () => s.LeftEyeDownUpRange, v => s.LeftEyeDownUpRange = v, -10f, 10f);
            DrawMuscleRange("Left Eye In-Out", s, () => s.LeftEyeInOutRange, v => s.LeftEyeInOutRange = v, -10f, 10f);
            DrawMuscleRange("Right Eye Down-Up", s, () => s.RightEyeDownUpRange, v => s.RightEyeDownUpRange = v, -10f, 10f);
            DrawMuscleRange("Right Eye In-Out", s, () => s.RightEyeInOutRange, v => s.RightEyeInOutRange = v, -10f, 10f);
            ImGui.TreePop();
        }

        // ── Shoulder ────────────────────────────────────────────────
        if (ImGui.TreeNode("Shoulder"))
        {
            DrawMuscleRange("Shoulder Down-Up", s, () => s.ShoulderDownUpDegRange, v => s.ShoulderDownUpDegRange = v);
            DrawMuscleRange("Shoulder Front-Back", s, () => s.ShoulderFrontBackDegRange, v => s.ShoulderFrontBackDegRange = v);
            ImGui.TreePop();
        }

        // ── Arm (Upper Arm + Forearm + Hand) ────────────────────────
        if (ImGui.TreeNode("Arm"))
        {
            DrawMuscleRange("Arm Twist", s, () => s.ArmTwistDegRange, v => s.ArmTwistDegRange = v);
            DrawMuscleRange("Arm Down-Up", s, () => s.ArmDownUpDegRange, v => s.ArmDownUpDegRange = v);
            DrawMuscleRange("Arm Front-Back", s, () => s.ArmFrontBackDegRange, v => s.ArmFrontBackDegRange = v);
            ImGui.Spacing();
            DrawMuscleRange("Forearm Twist", s, () => s.ForearmTwistDegRange, v => s.ForearmTwistDegRange = v);
            DrawMuscleRange("Forearm Stretch", s, () => s.ForearmStretchDegRange, v => s.ForearmStretchDegRange = v);
            ImGui.Spacing();
            DrawMuscleRange("Hand Down-Up", s, () => s.HandDownUpDegRange, v => s.HandDownUpDegRange = v);
            DrawMuscleRange("Hand In-Out", s, () => s.HandInOutDegRange, v => s.HandInOutDegRange = v);
            ImGui.TreePop();
        }

        // ── Leg (Upper Leg + Lower Leg + Foot + Toes) ───────────────
        if (ImGui.TreeNode("Leg"))
        {
            DrawMuscleRange("Upper Leg Twist", s, () => s.UpperLegTwistDegRange, v => s.UpperLegTwistDegRange = v);
            DrawMuscleRange("Upper Leg Front-Back", s, () => s.UpperLegFrontBackDegRange, v => s.UpperLegFrontBackDegRange = v);
            DrawMuscleRange("Upper Leg In-Out", s, () => s.UpperLegInOutDegRange, v => s.UpperLegInOutDegRange = v);
            ImGui.Spacing();
            DrawMuscleRange("Lower Leg Twist", s, () => s.LowerLegTwistDegRange, v => s.LowerLegTwistDegRange = v);
            DrawMuscleRange("Lower Leg Stretch", s, () => s.LowerLegStretchDegRange, v => s.LowerLegStretchDegRange = v);
            ImGui.Spacing();
            DrawMuscleRange("Foot Twist", s, () => s.FootTwistDegRange, v => s.FootTwistDegRange = v);
            DrawMuscleRange("Foot Up-Down", s, () => s.FootUpDownDegRange, v => s.FootUpDownDegRange = v);
            DrawMuscleRange("Toes Up-Down", s, () => s.ToesUpDownDegRange, v => s.ToesUpDownDegRange = v);
            ImGui.TreePop();
        }

        // ── Fingers ─────────────────────────────────────────────────
        if (ImGui.TreeNode("Fingers"))
        {
            DrawFingerRanges("Thumb", s,
                () => s.ThumbSpreadDegRange, v => s.ThumbSpreadDegRange = v,
                () => s.Thumb1StretchedDegRange, v => s.Thumb1StretchedDegRange = v,
                () => s.Thumb2StretchedDegRange, v => s.Thumb2StretchedDegRange = v,
                () => s.Thumb3StretchedDegRange, v => s.Thumb3StretchedDegRange = v);

            DrawFingerRanges("Index", s,
                () => s.IndexSpreadDegRange, v => s.IndexSpreadDegRange = v,
                () => s.Index1StretchedDegRange, v => s.Index1StretchedDegRange = v,
                () => s.Index2StretchedDegRange, v => s.Index2StretchedDegRange = v,
                () => s.Index3StretchedDegRange, v => s.Index3StretchedDegRange = v);

            DrawFingerRanges("Middle", s,
                () => s.MiddleSpreadDegRange, v => s.MiddleSpreadDegRange = v,
                () => s.Middle1StretchedDegRange, v => s.Middle1StretchedDegRange = v,
                () => s.Middle2StretchedDegRange, v => s.Middle2StretchedDegRange = v,
                () => s.Middle3StretchedDegRange, v => s.Middle3StretchedDegRange = v);

            DrawFingerRanges("Ring", s,
                () => s.RingSpreadDegRange, v => s.RingSpreadDegRange = v,
                () => s.Ring1StretchedDegRange, v => s.Ring1StretchedDegRange = v,
                () => s.Ring2StretchedDegRange, v => s.Ring2StretchedDegRange = v,
                () => s.Ring3StretchedDegRange, v => s.Ring3StretchedDegRange = v);

            DrawFingerRanges("Little", s,
                () => s.LittleSpreadDegRange, v => s.LittleSpreadDegRange = v,
                () => s.Little1StretchedDegRange, v => s.Little1StretchedDegRange = v,
                () => s.Little2StretchedDegRange, v => s.Little2StretchedDegRange = v,
                () => s.Little3StretchedDegRange, v => s.Little3StretchedDegRange = v);

            ImGui.TreePop();
        }

        // ── IK Goal Policy ──────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var policyNames = Enum.GetNames<EHumanoidIKGoalPolicy>();
        int policyIndex = (int)s.IKGoalPolicy;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("IK Goal Policy", ref policyIndex, policyNames, policyNames.Length))
        {
            using var _ = Undo.TrackChange("IK Goal Policy", s);
            s.IKGoalPolicy = (EHumanoidIKGoalPolicy)policyIndex;
        }

        bool isIKCalibrated = s.IsIKCalibrated;
        if (ImGui.Checkbox("IK Calibrated", ref isIKCalibrated))
        {
            using var _ = Undo.TrackChange("Toggle IK Calibrated", s);
            s.IsIKCalibrated = isIKCalibrated;
        }

        // ── Per-Channel Overrides ───────────────────────────────────
        ImGui.Spacing();
        if (ImGui.TreeNode("Per-Channel Overrides"))
        {
            ImGui.TextDisabled("Per-channel degree range overrides (takes priority over defaults above).");
            ImGui.TextDisabled($"{s.MuscleRotationDegRanges.Count} override(s) active.");

            if (s.MuscleRotationDegRanges.Count > 0)
            {
                if (!ImGui.BeginTable("MuscleOverrides", 3,
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
                {
                    ImGui.TreePop();
                }
                else
                {
                    ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthStretch, 0.5f);
                    ImGui.TableSetupColumn("Range", ImGuiTableColumnFlags.WidthStretch, 0.35f);
                    ImGui.TableSetupColumn("##Del", ImGuiTableColumnFlags.WidthFixed, 40f);
                    ImGui.TableHeadersRow();

                    EHumanoidValue? toRemove = null;
                    foreach (var (channel, range) in s.MuscleRotationDegRanges)
                    {
                        ImGui.PushID((int)channel);
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        ImGui.TextUnformatted(channel.ToString());

                        ImGui.TableSetColumnIndex(1);
                        var r = range;
                        ImGui.SetNextItemWidth(-1f);
                        if (ImGui.DragFloat2("##Range", ref r, 0.5f, -180f, 180f, "%.1f"))
                        {
                            using var _ = Undo.TrackChange($"Override {channel}", s);
                            s.SetMuscleRotationDegRange(channel, r);
                        }
                        ImGuiUndoHelper.TrackDragUndo($"Override {channel}", s);

                        ImGui.TableSetColumnIndex(2);
                        if (ImGui.SmallButton("X"))
                            toRemove = channel;

                        ImGui.PopID();
                    }

                    ImGui.EndTable();

                    if (toRemove is { } key)
                    {
                        using var _ = Undo.TrackChange($"Remove override {key}", s);
                        s.MuscleRotationDegRanges.Remove(key);
                    }
                }
            }

            ImGui.TreePop();
        }
    }

    private static void DrawFingerRanges(
        string fingerName,
        HumanoidSettings s,
        Func<Vector2> getSpread, Action<Vector2> setSpread,
        Func<Vector2> get1, Action<Vector2> set1,
        Func<Vector2> get2, Action<Vector2> set2,
        Func<Vector2> get3, Action<Vector2> set3)
    {
        if (!ImGui.TreeNode(fingerName))
            return;

        DrawMuscleRange($"{fingerName} Spread", s, getSpread, setSpread);
        DrawMuscleRange($"{fingerName} 1 Stretched", s, get1, set1);
        DrawMuscleRange($"{fingerName} 2 Stretched", s, get2, set2);
        DrawMuscleRange($"{fingerName} 3 Stretched", s, get3, set3);
        ImGui.TreePop();
    }

    /// <summary>
    /// Draws a labeled min/max degree range editor for a single muscle channel.
    /// </summary>
    private static void DrawMuscleRange(
        string label,
        HumanoidSettings settings,
        Func<Vector2> getter,
        Action<Vector2> setter,
        float sliderMin = -180f,
        float sliderMax = 180f)
    {
        ImGui.PushID(label);
        var range = getter();

        // Two-column layout: label + drag fields
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        ImGui.SameLine(ImGui.GetContentRegionAvail().X * 0.55f);

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.DragFloat2("##Range", ref range, 0.5f, sliderMin, sliderMax, "%.1f"))
        {
            using var _ = Undo.TrackChange(label, settings);
            setter(range);
        }
        ImGuiUndoHelper.TrackDragUndo(label, settings);

        ImGui.PopID();
    }

    private static void DrawMuscleValuesSection(HumanoidComponent humanoid)
    {
        bool showZeroes = _showZeroMuscleValues;
        if (ImGui.Checkbox("Show zero values", ref showZeroes))
            _showZeroMuscleValues = showZeroes;

        if (!ImGui.BeginTable("HumanoidMuscleValues", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
            return;

        ImGui.TableSetupColumn("Muscle", ImGuiTableColumnFlags.WidthStretch, 0.7f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 0.3f);
        ImGui.TableHeadersRow();

        foreach (EHumanoidValue value in Enum.GetValues<EHumanoidValue>())
        {
            float amount = humanoid.TryGetMuscleValue(value, out var v) ? v : 0.0f;
            if (!showZeroes && MathF.Abs(amount) < 0.0001f)
                continue;

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(value.ToString());
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted($"{amount:0.###}");
        }

        ImGui.EndTable();
    }

    private static void DrawBoneGroup(string label, (string name, HumanoidComponent.BoneDef def)[] bones)
    {
        if (!ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen))
            return;

        foreach (var (name, def) in bones)
            DrawBoneDef(name, def);

        ImGui.TreePop();
    }

    private static void DrawBoneDef(string label, HumanoidComponent.BoneDef def)
    {
        ImGui.PushID($"{label}_{def.GetHashCode()}");

        string nodeName = def.Node?.Name ?? "<unassigned>";
        Vector4 color = def.Node is null ? MissingColor : AssignedColor;
        ImGui.TextColored(color, $"{label}: {nodeName}");
        Vector2 labelRectMin = ImGui.GetItemRectMin();
        Vector2 labelRectMax = ImGui.GetItemRectMax();
        TryHandleSceneNodeDrop(sceneNode =>
        {
            var captured = sceneNode;
            RunSceneEdit(() => def.Node = captured);
            return true;
        }, out bool bonePreviewActive);
        if (bonePreviewActive)
        {
            DrawDropHighlight(labelRectMin, labelRectMax);
            ImGui.SetTooltip("Drop a scene node to assign this bone.");
        }

        ImGui.SameLine();
        bool selectionAvailable = Selection.SceneNode is not null;
        if (!selectionAvailable)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Use Selected"))
        {
            var selectedNode = Selection.SceneNode;
            if (selectedNode is not null)
            {
                var capturedNode = selectedNode;
                RunSceneEdit(() => def.Node = capturedNode);
            }
        }
        if (!selectionAvailable)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
            RunSceneEdit(() => def.Node = null);

        ImGui.PopID();
    }

    private static void DrawBodySide(string label, HumanoidComponent.BodySide side)
    {
        if (!ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawBoneDef("Shoulder", side.Shoulder);
        DrawBoneDef("Arm", side.Arm);
        DrawBoneDef("Elbow", side.Elbow);
        DrawBoneDef("Wrist", side.Wrist);
        DrawBoneDef("Leg", side.Leg);
        DrawBoneDef("Knee", side.Knee);
        DrawBoneDef("Foot", side.Foot);
        DrawBoneDef("Toes", side.Toes);

        if (ImGui.TreeNodeEx("Fingers", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawFinger("Thumb", side.Hand.Thumb);
            DrawFinger("Index", side.Hand.Index);
            DrawFinger("Middle", side.Hand.Middle);
            DrawFinger("Ring", side.Hand.Ring);
            DrawFinger("Pinky", side.Hand.Pinky);
            ImGui.TreePop();
        }

        ImGui.TreePop();
    }

    private static void DrawFinger(string label, HumanoidComponent.BodySide.Fingers.Finger finger)
    {
        if (!ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawBoneDef("Proximal", finger.Proximal);
        DrawBoneDef("Intermediate", finger.Intermediate);
        DrawBoneDef("Distal", finger.Distal);

        ImGui.TreePop();
    }

    private static bool TryHandleSceneNodeDrop(Func<SceneNode, bool> onDrop, out bool previewActive)
    {
        previewActive = false;
        if (!ImGui.BeginDragDropTarget())
            return false;

        bool handled = false;
        if (ImGuiSceneNodeDragDrop.Accept(peekOnly: true) is not null)
            previewActive = true;

        if (ImGuiSceneNodeDragDrop.Accept() is SceneNode dropped && onDrop(dropped))
            handled = true;

        ImGui.EndDragDropTarget();
        return handled;
    }

    private static void DrawDropHighlight(Vector2 min, Vector2 max)
    {
        var drawList = ImGui.GetWindowDrawList();
        Vector4 baseColor = ImGui.GetStyle().Colors[(int)ImGuiCol.DragDropTarget];
        Vector4 fillColor = baseColor;
        fillColor.W *= 0.25f;
        uint fill = ImGui.ColorConvertFloat4ToU32(fillColor);
        uint border = ImGui.ColorConvertFloat4ToU32(baseColor);
        drawList.AddRectFilled(min, max, fill, 4.0f);
        drawList.AddRect(min, max, border, 4.0f, ImDrawFlags.None, 1.5f);
    }

    private static void RunSceneEdit(Action edit)
    {
        if (edit is null)
            return;

        edit();
        EnqueueSceneEdit(edit);
    }
}
