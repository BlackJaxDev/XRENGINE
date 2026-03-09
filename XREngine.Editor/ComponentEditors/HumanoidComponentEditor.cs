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
    private static bool _showZeroMuscleValues = true;
    private static readonly (string Label, EHumanoidValue Left, EHumanoidValue Right)[] ArmOverrideChannels =
    [
        ("Arm Twist", EHumanoidValue.LeftArmTwistInOut, EHumanoidValue.RightArmTwistInOut),
        ("Arm Down-Up", EHumanoidValue.LeftArmDownUp, EHumanoidValue.RightArmDownUp),
        ("Arm Front-Back", EHumanoidValue.LeftArmFrontBack, EHumanoidValue.RightArmFrontBack),
        ("Forearm Twist", EHumanoidValue.LeftForearmTwistInOut, EHumanoidValue.RightForearmTwistInOut),
        ("Forearm Stretch", EHumanoidValue.LeftForearmStretch, EHumanoidValue.RightForearmStretch),
        ("Hand Down-Up", EHumanoidValue.LeftHandDownUp, EHumanoidValue.RightHandDownUp),
        ("Hand In-Out", EHumanoidValue.LeftHandInOut, EHumanoidValue.RightHandInOut),
    ];
    private static readonly (string Label, EHumanoidValue Left, EHumanoidValue Right)[] LegOverrideChannels =
    [
        ("Upper Leg Twist", EHumanoidValue.LeftUpperLegTwistInOut, EHumanoidValue.RightUpperLegTwistInOut),
        ("Upper Leg Front-Back", EHumanoidValue.LeftUpperLegFrontBack, EHumanoidValue.RightUpperLegFrontBack),
        ("Upper Leg In-Out", EHumanoidValue.LeftUpperLegInOut, EHumanoidValue.RightUpperLegInOut),
        ("Lower Leg Twist", EHumanoidValue.LeftLowerLegTwistInOut, EHumanoidValue.RightLowerLegTwistInOut),
        ("Lower Leg Stretch", EHumanoidValue.LeftLowerLegStretch, EHumanoidValue.RightLowerLegStretch),
        ("Foot Twist", EHumanoidValue.LeftFootTwistInOut, EHumanoidValue.RightFootTwistInOut),
        ("Foot Up-Down", EHumanoidValue.LeftFootUpDown, EHumanoidValue.RightFootUpDown),
        ("Toes Up-Down", EHumanoidValue.LeftToesUpDown, EHumanoidValue.RightToesUpDown),
    ];

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
        ImGui.SeparatorText("Bone Mapping");
        DrawBoneMappingSection(humanoid);
        ImGui.SeparatorText("Per-Muscle Settings");
        DrawPerMuscleSettingsSection(humanoid);
        ImGui.SeparatorText("Muscle Debug Overrides");
        DrawMuscleDebugSection(humanoid);
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
        if (ImGui.Button("Apply Neutral Preset"))
            RunSceneEdit(humanoid.ReloadNeutralPosePreset);

        ImGui.SameLine();
        if (ImGui.Button("Clear Neutral Pose"))
            RunSceneEdit(humanoid.ClearNeutralPoseOffsets);

        ImGui.Spacing();
    }

    private static void DrawGeneralSection(HumanoidComponent humanoid)
    {
        var previewMode = humanoid.PosePreviewMode;
        if (ImGui.BeginCombo("Pose Preview", previewMode.ToString()))
        {
            foreach (EHumanoidPosePreviewMode mode in Enum.GetValues<EHumanoidPosePreviewMode>())
            {
                bool selected = previewMode == mode;
                if (ImGui.Selectable(mode.ToString(), selected))
                {
                    using var _ = Undo.TrackChange("Change Pose Preview", humanoid);
                    EnqueueSceneEdit(() => humanoid.PosePreviewMode = mode);
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        var neutralPreset = humanoid.NeutralPosePreset;
        if (ImGui.BeginCombo("Neutral Pose Preset", neutralPreset.ToString()))
        {
            foreach (EHumanoidNeutralPosePreset preset in Enum.GetValues<EHumanoidNeutralPosePreset>())
            {
                bool selected = neutralPreset == preset;
                if (ImGui.Selectable(preset.ToString(), selected))
                {
                    using var _ = Undo.TrackChange("Change Neutral Pose Preset", humanoid);
                    EnqueueSceneEdit(() => humanoid.NeutralPosePreset = preset);
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        int neutralRotationCount = HumanoidNeutralPosePresets.GetRotationCount(neutralPreset);
        if (neutralPreset == EHumanoidNeutralPosePreset.None)
            ImGui.TextDisabled("Neutral pose preset disabled.");
        else if (neutralRotationCount == 0)
            ImGui.TextDisabled("Selected preset has no embedded rotations yet. Populate HumanoidNeutralPosePresets from the Unity exporter output.");
        else
            ImGui.TextWrapped($"Neutral preset rotations: {neutralRotationCount}");

        bool debugVisibility = humanoid.RenderInfo.IsVisible;
        if (ImGui.Checkbox("Show Debug Skeleton", ref debugVisibility))
        {
            using var _ = Undo.TrackChange("Toggle Debug Skeleton", humanoid);
            humanoid.RenderInfo.IsVisible = debugVisibility;
        }
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
        ImGui.TextDisabled("Range format: first value is used when the muscle is negative, second when it is positive.");
        ImGui.TextDisabled("Debug toggles: S flips both signs. V swaps magnitudes while preserving each slot's current sign.");
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
            ImGui.Spacing();
            DrawMirroredOverrideTable("Per-Side Overrides", s, ArmOverrideChannels);
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
            ImGui.Spacing();
            DrawMirroredOverrideTable("Per-Side Overrides", s, LegOverrideChannels);
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
                        DrawRangeInputWithDebugActions(
                            $"Override {channel}",
                            s,
                            ref r,
                            value => s.SetMuscleRotationDegRange(channel, value));

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
        ImGui.TextUnformatted($"{label} [-,+]");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("First value applies when the muscle value is below 0. Second value applies when it is above 0.");
        ImGui.SameLine(ImGui.GetContentRegionAvail().X * 0.55f);

        DrawRangeInputWithDebugActions(label, settings, ref range, setter);

        ImGui.PopID();
    }

    private static void DrawMirroredOverrideTable(
        string title,
        HumanoidSettings settings,
        IReadOnlyList<(string Label, EHumanoidValue Left, EHumanoidValue Right)> channels,
        float sliderMin = -180f,
        float sliderMax = 180f)
    {
        if (!ImGui.TreeNode(title))
            return;

        ImGui.TextDisabled("Enable an override to decouple one side from the shared default above.");
        ImGui.TextDisabled("Override range format is [-,+] = negative-side limit, positive-side limit.");
        ImGui.TextDisabled("Debug toggles: S flips both signs. V swaps magnitudes while preserving each slot's current sign.");

        if (!ImGui.BeginTable($"{title}_Overrides", 3,
            ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            ImGui.TreePop();
            return;
        }

        ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthStretch, 0.40f);
        ImGui.TableSetupColumn("Left", ImGuiTableColumnFlags.WidthStretch, 0.30f);
        ImGui.TableSetupColumn("Right", ImGuiTableColumnFlags.WidthStretch, 0.30f);
        ImGui.TableHeadersRow();

        foreach (var (label, left, right) in channels)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(label);

            ImGui.TableSetColumnIndex(1);
            DrawChannelOverrideCell("L", settings, left, sliderMin, sliderMax);

            ImGui.TableSetColumnIndex(2);
            DrawChannelOverrideCell("R", settings, right, sliderMin, sliderMax);
        }

        ImGui.EndTable();
        ImGui.TreePop();
    }

    private static void DrawChannelOverrideCell(
        string sideId,
        HumanoidSettings settings,
        EHumanoidValue channel,
        float sliderMin,
        float sliderMax)
    {
        ImGui.PushID($"{sideId}_{channel}");

        bool hasOverride = settings.TryGetMuscleRotationDegRange(channel, out Vector2 range);
        if (!hasOverride)
            range = settings.GetFallbackMuscleRotationDegRange(channel);

        bool overrideEnabled = hasOverride;
        if (ImGui.Checkbox("##Enabled", ref overrideEnabled))
        {
            using var _ = Undo.TrackChange($"{(overrideEnabled ? "Enable" : "Disable")} override {channel}", settings);
            if (overrideEnabled)
                settings.SetMuscleRotationDegRange(channel, range);
            else
                settings.MuscleRotationDegRanges.Remove(channel);
        }

        ImGui.SameLine();

        if (!overrideEnabled)
            ImGui.BeginDisabled();

        DrawRangeInputWithDebugActions(
            $"Override {channel}",
            settings,
            ref range,
            value => settings.SetMuscleRotationDegRange(channel, value));

        if (!overrideEnabled)
            ImGui.EndDisabled();

        ImGui.PopID();
    }

    private static void DrawRangeInputWithDebugActions(
        string undoLabel,
        HumanoidSettings settings,
        ref Vector2 range,
        Action<Vector2> setter)
    {
        float toggleWidth = ImGui.GetFrameHeight() + ImGui.CalcTextSize("V").X + ImGui.GetStyle().ItemInnerSpacing.X;
        float totalToggleWidth = (toggleWidth * 2.0f) + (ImGui.GetStyle().ItemSpacing.X * 2.0f);
        float inputWidth = MathF.Max(80.0f, ImGui.GetContentRegionAvail().X - totalToggleWidth);

        ImGui.SetNextItemWidth(inputWidth);
        if (ImGui.DragFloat2("##Range", ref range, 0.5f, 0.0f, 0.0f, "%.1f"))
        {
            using var _ = Undo.TrackChange(undoLabel, settings);
            setter(range);
        }
        ImGuiUndoHelper.TrackDragUndo(undoLabel, settings);

        ImGui.SameLine();
        if (DrawRangeDebugActionCheckbox("S", "Debug action: negate both signs for this range."))
            ApplyRangeDebugTransform(undoLabel, settings, ref range, setter, NegateRangeSigns);

        ImGui.SameLine();
        if (DrawRangeDebugActionCheckbox("V", "Debug action: swap the two magnitudes while preserving each slot's current sign."))
            ApplyRangeDebugTransform(undoLabel, settings, ref range, setter, SwapRangeValuesPreservingSigns);
    }

    private static bool DrawRangeDebugActionCheckbox(string label, string tooltip)
    {
        bool apply = false;
        bool clicked = ImGui.Checkbox(label, ref apply) && apply;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        return clicked;
    }

    private static void ApplyRangeDebugTransform(
        string undoLabel,
        HumanoidSettings settings,
        ref Vector2 range,
        Action<Vector2> setter,
        Func<Vector2, Vector2> transform)
    {
        range = transform(range);
        using var _ = Undo.TrackChange(undoLabel, settings);
        setter(range);
    }

    private static Vector2 NegateRangeSigns(Vector2 range)
        => new(-range.X, -range.Y);

    private static Vector2 SwapRangeValuesPreservingSigns(Vector2 range)
        => new(PreserveSlotSign(range.Y, range.X), PreserveSlotSign(range.X, range.Y));

    private static float PreserveSlotSign(float value, float signSource)
        => signSource < 0.0f ? -MathF.Abs(value) : signSource > 0.0f ? MathF.Abs(value) : 0.0f;

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

    private static void DrawMuscleDebugSection(HumanoidComponent humanoid)
    {
        ImGui.TextDisabled("Per-group sign overrides for rapid axis debugging. Changes are immediate, not serialized.");

        if (ImGui.Button("Reset All Overrides"))
        {
            humanoid.DebugShoulderSigns = HumanoidComponent.LimbSignOverrides.Default;
            humanoid.DebugArmSigns = HumanoidComponent.LimbSignOverrides.Default;
            humanoid.DebugForearmSigns = HumanoidComponent.LimbSignOverrides.Default;
            humanoid.DebugWristSigns = HumanoidComponent.LimbSignOverrides.Default;
            humanoid.DebugUpperLegSigns = HumanoidComponent.LimbSignOverrides.Default;
            humanoid.DebugKneeSigns = HumanoidComponent.LimbSignOverrides.Default;
            humanoid.DebugFootSigns = HumanoidComponent.LimbSignOverrides.Default;
            humanoid.DebugToesSigns = HumanoidComponent.LimbSignOverrides.Default;
        }

        DrawLimbOverrideGroup("Shoulder", ref humanoid.DebugShoulderSigns);
        DrawLimbOverrideGroup("Arm (upper)", ref humanoid.DebugArmSigns);
        DrawLimbOverrideGroup("Forearm", ref humanoid.DebugForearmSigns);
        DrawLimbOverrideGroup("Wrist", ref humanoid.DebugWristSigns);
        DrawLimbOverrideGroup("Upper Leg", ref humanoid.DebugUpperLegSigns);
        DrawLimbOverrideGroup("Knee", ref humanoid.DebugKneeSigns);
        DrawLimbOverrideGroup("Foot", ref humanoid.DebugFootSigns);
        DrawLimbOverrideGroup("Toes", ref humanoid.DebugToesSigns);
    }

    private static void DrawLimbOverrideGroup(string label, ref HumanoidComponent.LimbSignOverrides overrides)
    {
        if (!ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.None))
            return;

        ImGui.PushID(label);

        // Sign flip buttons: click to toggle between +1 and -1
        DrawSignFlipButton("Yaw (twist)", ref overrides.YawSign);
        ImGui.SameLine();
        DrawSignFlipButton("Pitch (F/B)", ref overrides.PitchSign);
        ImGui.SameLine();
        DrawSignFlipButton("Roll (L/R)", ref overrides.RollSign);

        ImGui.Checkbox("Skip negate yaw", ref overrides.SkipBlanketNegateYaw);
        ImGui.SameLine();
        ImGui.Checkbox("Skip negate pitch", ref overrides.SkipBlanketNegatePitch);
        ImGui.SameLine();
        ImGui.Checkbox("Skip negate roll", ref overrides.SkipBlanketNegateRoll);

        ImGui.Checkbox("Swap pitch/roll axes", ref overrides.SwapPitchRollAxes);

        ImGui.PopID();
        ImGui.TreePop();
    }

    private static void DrawSignFlipButton(string label, ref float sign)
    {
        string text = sign >= 0.0f ? $"+1 {label}" : $"-1 {label}";
        if (sign < 0.0f)
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.7f, 0.3f, 0.3f, 1.0f));

        if (ImGui.SmallButton(text))
            sign = -sign;

        if (sign < 0.0f)
            ImGui.PopStyleColor();
    }

    private static void RunSceneEdit(Action edit)
    {
        if (edit is null)
            return;

        edit();
        EnqueueSceneEdit(edit);
    }
}
