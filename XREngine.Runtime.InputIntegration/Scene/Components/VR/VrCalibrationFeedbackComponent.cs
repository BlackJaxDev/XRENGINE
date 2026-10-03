using System.Numerics;
using System.Collections.Generic;
using XREngine.Components.Animation;
using XREngine.Data.Colors;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.VR;

/// <summary>Shows the live binding proposal and capture feedback inside the headset view.</summary>
public sealed class VrCalibrationFeedbackComponent : XRComponent
{
    private readonly List<SceneNode> _markers = [];
    private readonly List<UITextComponent> _markerLabels = [];
    private SceneNode? _ownerRoot;
    private VRPlayerCharacterComponent? _player;
    private IHumanoidVrCalibrationRig? _humanoid;
    private UITextComponent? _statusText;
    private SceneNode? _statusNode;
    private SceneNode? _leftFootprint;
    private SceneNode? _rightFootprint;
    private string? _lastMessage;

    public VRPlayerCharacterComponent? Player
    {
        get => _player;
        set => SetField(ref _player, value);
    }

    public IHumanoidVrCalibrationRig? Humanoid
    {
        get => _humanoid;
        set => SetField(ref _humanoid, value);
    }

    /// <summary>Creates reusable display nodes once, outside pose-update ticks.</summary>
    public void Initialize(SceneNode ownerRoot, SceneNode headsetNode)
    {
        if (_statusNode is not null)
            return;
        _ownerRoot = ownerRoot;

        SceneNode statusPlacement = headsetNode.NewChild("Calibration Status Placement");
        Transform statusPlacementTransform = statusPlacement.GetTransformAs<Transform>(true)!;
        statusPlacementTransform.Translation = new Vector3(0.0f, 0.0f, -1.2f);
        statusPlacementTransform.Scale = new Vector3(0.001f);
        _statusNode = CreateLabelCanvas(statusPlacement, "Calibration Status", 900.0f, 160.0f, out _statusText);
        _statusText!.Text = "Open calibration from the VR controller menu.";

        EnsureMarkerCapacity(8);

        _leftFootprint = CreateFootprint(ownerRoot, "Left Calibration Footprint", ColorF4.DarkTeal);
        _rightFootprint = CreateFootprint(ownerRoot, "Right Calibration Footprint", ColorF4.DarkTeal);
        _leftFootprint.IsActiveSelf = false;
        _rightFootprint.IsActiveSelf = false;
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateFeedback);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateFeedback);
        base.OnComponentDeactivated();
    }

    private void UpdateFeedback()
    {
        VRPlayerCharacterComponent? player = Player;
        if (player is null || _statusNode is null)
            return;

        string message = player.CalibrationMessage;
        if (!ReferenceEquals(message, _lastMessage) && message != _lastMessage)
        {
            _statusText!.Text = message;
            _lastMessage = message;
        }

        bool calibrating = player.CalibrationState == VrCalibrationState.Calibrating;
        bool calibratedNotice = player.CalibrationState == VrCalibrationState.Calibrated &&
            (message.Contains("tracker", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("differs", StringComparison.OrdinalIgnoreCase));
        _statusNode.IsActiveSelf = calibrating || calibratedNotice ||
            player.CalibrationState is VrCalibrationState.Failed or VrCalibrationState.Uncalibrated;
        if (!calibrating)
        {
            HideMarkersAndFootprints();
            return;
        }

        IReadOnlyList<VrTrackerBindingPreview> proposals = player.TrackerBindingPreview;
        EnsureMarkerCapacity(proposals.Count);
        int count = proposals.Count;
        for (int i = 0; i < _markers.Count; i++)
        {
            SceneNode marker = _markers[i];
            if (i >= count || !proposals[i].Tracker.TryGetCurrentWorldPose(out Matrix4x4 pose, out _, out _))
            {
                marker.IsActiveSelf = false;
                continue;
            }

            marker.IsActiveSelf = true;
            marker.GetTransformAs<Transform>(false)!.SetWorldTranslationRotation(pose.Translation, Quaternion.Identity);
            string label = GetSlotLabel(proposals[i].Slot);
            if (_markerLabels[i].Text != label)
                _markerLabels[i].Text = label;
        }

        UpdateFootprint(_leftFootprint, Humanoid?.LeftFootNode?.Transform, player);
        UpdateFootprint(_rightFootprint, Humanoid?.RightFootNode?.Transform, player);
    }

    private void HideMarkersAndFootprints()
    {
        for (int i = 0; i < _markers.Count; i++)
            _markers[i].IsActiveSelf = false;
        if (_leftFootprint is not null)
            _leftFootprint.IsActiveSelf = false;
        if (_rightFootprint is not null)
            _rightFootprint.IsActiveSelf = false;
    }

    private void EnsureMarkerCapacity(int count)
    {
        SceneNode? ownerRoot = _ownerRoot;
        if (ownerRoot is null) return;
        while (_markers.Count < count)
        {
            int number = _markers.Count + 1;
            SceneNode marker = ownerRoot.NewChild($"Calibration Tracker Marker {number}");
            marker.GetTransformAs<Transform>(true);
            marker.AddComponent<DebugDrawComponent>()!.AddSphere(0.045f, Vector3.Zero, ColorF4.DarkTeal, false);
            SceneNode placement = marker.NewChild("Tracker Label Placement");
            Transform placementTransform = placement.GetTransformAs<Transform>(true)!;
            placementTransform.Translation = new Vector3(0.0f, 0.12f, 0.0f);
            placementTransform.Scale = new Vector3(0.0006f);
            _ = CreateLabelCanvas(placement, "Tracker Slot Label", 320.0f, 70.0f, out UITextComponent? label);
            _markers.Add(marker);
            _markerLabels.Add(label!);
            marker.IsActiveSelf = false;
        }
    }

    private static void UpdateFootprint(SceneNode? footprint, TransformBase? bone, VRPlayerCharacterComponent player)
    {
        if (footprint is null)
            return;
        if (bone is null || player.PlayspaceRoot is null)
        {
            footprint.IsActiveSelf = false;
            return;
        }

        Vector3 position = bone.WorldTranslation;
        position.Y = player.PlayspaceRoot.WorldTranslation.Y + 0.005f;
        footprint.GetTransformAs<Transform>(false)!.SetWorldTranslationRotation(position, Quaternion.Identity);
        footprint.IsActiveSelf = true;
    }

    private static SceneNode CreateFootprint(SceneNode ownerRoot, string name, ColorF4 color)
    {
        SceneNode node = ownerRoot.NewChild(name);
        node.GetTransformAs<Transform>(true);
        node.AddComponent<DebugDrawComponent>()!.AddCircle(0.13f, Vector3.Zero, Globals.Up, color, false);
        return node;
    }

    private static SceneNode CreateLabelCanvas(SceneNode parent, string name, float width, float height, out UITextComponent? label)
    {
        SceneNode canvasNode = parent.NewChild(name);
        var canvas = canvasNode.AddComponent<UICanvasComponent>()!;
        canvas.PreferOffscreenRenderingForNonScreenSpaces = false;
        canvas.CanvasTransform.DrawSpace = ECanvasDrawSpace.World;
        canvas.CanvasTransform.SetSize(new Vector2(width, height));
        canvas.CanvasTransform.NormalizedPivot = new Vector2(0.5f);
        SceneNode textNode = canvasNode.NewChild("Text");
        label = textNode.AddComponent<UITextComponent>()!;
        label.FontSize = 48;
        label.HorizontalAlignment = EHorizontalAlignment.Center;
        label.VerticalAlignment = EVerticalAlignment.Center;
        var bounds = textNode.GetTransformAs<UIBoundableTransform>(true)!;
        bounds.Width = width;
        bounds.Height = height;
        bounds.NormalizedPivot = new Vector2(0.5f);
        return canvasNode;
    }

    private static string GetSlotLabel(EHumanoidIKTarget? slot)
        => slot switch
        {
            EHumanoidIKTarget.Hips => "Hips",
            EHumanoidIKTarget.Chest => "Chest",
            EHumanoidIKTarget.LeftFoot => "Left foot",
            EHumanoidIKTarget.RightFoot => "Right foot",
            EHumanoidIKTarget.LeftElbow => "Left upper arm",
            EHumanoidIKTarget.RightElbow => "Right upper arm",
            EHumanoidIKTarget.LeftKnee => "Left knee",
            EHumanoidIKTarget.RightKnee => "Right knee",
            _ => "Unassigned",
        };
}
