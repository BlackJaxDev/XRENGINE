using System.Numerics;
using XREngine.Components.Animation;
using XREngine.Data.Colors;
using XREngine.Data.Components.Scene;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.VR;

/// <summary>World-space calibration instructions, assignment labels, and stance guides visible to both XR eyes.</summary>
public sealed class VrCalibrationFeedbackComponent : XRComponent
{
    private static readonly string[] SlotLabels =
        ["Head", "Hips", "Left hand", "Right hand", "Left foot", "Right foot", "Left upper arm", "Right upper arm", "Left knee", "Right knee", "Chest"];
    private readonly (SceneNode Node, Transform Transform, UITextComponent Label)[] _markers = new (SceneNode, Transform, UITextComponent)[16];
    private VRPlayerCharacterComponent? _player;
    private IHumanoidVrCalibrationRig? _rig;
    private VRTrackerCollectionComponent? _trackers;
    private SceneNode? _messageRoot, _leftFootprint, _rightFootprint;
    private UITextComponent? _message;
    private string? _lastMessage;
    private float _noticeSeconds;
    private long _discontinuityVersion = -1;
    public VRPlayerCharacterComponent? Player
    {
        get => _player;
        set
        {
            if (!SetField(ref _player, value)) return;
            _rig = value?.GetHumanoid();
            _trackers = value?.GetTrackerCollection();
        }
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        EnsureVisuals();
        RegisterTick(ETickGroup.Late, (int)ETickOrder.Scene + 2, UpdateFeedback);
    }
    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Late, (int)ETickOrder.Scene + 2, UpdateFeedback);
        if (_messageRoot is not null) _messageRoot.IsActiveSelf = false;
        SetGuidesVisible(false);
        base.OnComponentDeactivated();
    }

    private void EnsureVisuals()
    {
        if (_messageRoot is not null) return;
        (_messageRoot, _, _message) = CreateLabel("VR Calibration Instructions", 1000, 180, .001f);
        for (int index = 0; index < _markers.Length; ++index)
        {
            _markers[index] = CreateLabel($"VR Tracker Label {index}", 240, 50, .0015f);
            DebugDrawComponent marker = _markers[index].Node.AddComponent<DebugDrawComponent>()!;
            marker.AddSphere(.025f, Vector3.Zero, ColorF4.Cyan, false);
            _markers[index].Node.IsActiveSelf = false;
        }
        _leftFootprint = CreateFootprint("Calibration Left Footprint");
        _rightFootprint = CreateFootprint("Calibration Right Footprint");
    }

    private (SceneNode, Transform, UITextComponent) CreateLabel(string name, float width, float height, float scale)
    {
        SceneNode anchor = SceneNode.NewChild(name);
        Transform transform = anchor.SetTransform<Transform>();
        SceneNode canvasNode = anchor.NewChild("World Canvas");
        UICanvasComponent canvas = canvasNode.AddComponent<UICanvasComponent>()!;
        canvas.PreferOffscreenRenderingForNonScreenSpaces = false;
        UICanvasTransform canvasTransform = canvas.CanvasTransform;
        canvasTransform.DrawSpace = ECanvasDrawSpace.World;
        canvasTransform.Width = width;
        canvasTransform.Height = height;
        canvasTransform.Scale = new(scale);
        canvasTransform.Translation = new(-width * scale * .5f, 0);
        SceneNode textNode = canvasNode.NewChild("Text");
        UITextComponent text = textNode.AddComponent<UITextComponent>()!;
        UIBoundableTransform textTransform = textNode.GetTransformAs<UIBoundableTransform>(true)!;
        textTransform.Width = width;
        textTransform.Height = height;
        text.FontSize = 28;
        text.HorizontalAlignment = EHorizontalAlignment.Center;
        text.VerticalAlignment = EVerticalAlignment.Center;
        text.WrapMode = FontGlyphSet.EWrapMode.Character;
        text.Color = ColorF4.White;
        text.OutlineColor = ColorF4.Black;
        text.OutlineThickness = 1.5f;
        return (anchor, transform, text);
    }

    private SceneNode CreateFootprint(string name)
    {
        SceneNode node = SceneNode.NewChild(name);
        node.SetTransform<Transform>();
        DebugDrawComponent draw = node.AddComponent<DebugDrawComponent>()!;
        draw.AddBox(new(.075f, .003f, .14f), Vector3.Zero, ColorF4.Cyan, false);
        node.IsActiveSelf = false;
        return node;
    }

    private void UpdateFeedback()
    {
        VRPlayerCharacterComponent? player = Player;
        if (player?.Headset is not { } headset || _messageRoot?.Transform is not Transform messageTransform || _message is null)
            return;
        long version = XREngine.Input.RuntimeVrDiscontinuityServices.Version;
        if (version != _discontinuityVersion)
        {
            _discontinuityVersion = version;
            _rig = player.GetHumanoid();
            _trackers = player.GetTrackerCollection();
        }
        if (!string.Equals(_lastMessage, player.CalibrationMessage, StringComparison.Ordinal))
        {
            _lastMessage = player.CalibrationMessage;
            _message.Text = _lastMessage + "\nStand in the footprints. Pull both triggers to capture. Use Calibration Cancel to restore the previous rig.";
            _noticeSeconds = 5;
            _rig = player.GetHumanoid();
            _trackers = player.GetTrackerCollection();
        }
        _noticeSeconds = Math.Max(0, _noticeSeconds - (RuntimeTransformServices.Current?.UndilatedUpdateDeltaSeconds ?? 0));
        _messageRoot.IsActiveSelf = player.IsCalibrating || _noticeSeconds > 0;
        Vector3 forward = headset.WorldForward;
        forward.Y = 0;
        if (forward.LengthSquared() < 1e-8f) forward = -Vector3.UnitZ;
        forward = Vector3.Normalize(forward);
        Vector3 position = headset.WorldTranslation + forward * 1.4f - Vector3.UnitY * .15f;
        Matrix4x4 messageWorld = Matrix4x4.CreateWorld(position, -forward, Vector3.UnitY);
        VrSpectatorFollowState.ApplyWorldPose(messageTransform, messageWorld);
        if (!player.IsCalibrating || _rig is null)
        {
            SetGuidesVisible(false);
            return;
        }
        int count = 0;
        if (_trackers is not null)
        {
            foreach (VRTrackerTransform tracker in _trackers.OpenXrTrackers.Values)
                UpdateMarker(tracker, ref count, headset.WorldTranslation);
            if (!XREngine.Input.RuntimeVrStateServices.IsOpenXRActive)
                foreach (var entry in _trackers.Trackers.Values)
                    UpdateMarker(entry.Item2, ref count, headset.WorldTranslation);
        }
        for (int index = count; index < _markers.Length; ++index)
            _markers[index].Node.IsActiveSelf = false;
        UpdateFootprint(_leftFootprint, _rig.LeftFootNode, player);
        UpdateFootprint(_rightFootprint, _rig.RightFootNode, player);
    }

    private void UpdateMarker(VRTrackerTransform tracker, ref int count, Vector3 viewer)
    {
        if (!tracker.PoseCurrentlyUsable || count >= _markers.Length || _rig is null) return;
        var marker = _markers[count++];
        marker.Node.IsActiveSelf = true;
        marker.Label.Text = DescribeBinding(_rig, tracker);
        Vector3 position = tracker.WorldTranslation + Vector3.UnitY * .07f;
        Quaternion rotation = VrSpectatorFollowState.LookAt(position, viewer, Quaternion.Identity);
        Matrix4x4 world = Matrix4x4.CreateFromQuaternion(rotation);
        world.Translation = position;
        VrSpectatorFollowState.ApplyWorldPose(marker.Transform, world);
    }

    /// <summary>Reads the assignment preview used by capture; it does not run an independent matcher.</summary>
    public static string DescribeBinding(IHumanoidVrCalibrationRig rig, TransformBase tracker)
    {
        for (int slot = 0; slot < SlotLabels.Length; ++slot)
            if (slot is not (0 or 2 or 3) && ReferenceEquals(rig.GetIKTarget((EHumanoidIKTarget)slot).tfm, tracker))
                return SlotLabels[slot];
        return "Unassigned";
    }

    private void UpdateFootprint(SceneNode? node, SceneNode? foot, VRPlayerCharacterComponent player)
    {
        if (node is null) return;
        node.IsActiveSelf = foot is not null;
        if (foot is null || node.Transform is not Transform transform) return;
        Vector3 position = foot.Transform.WorldTranslation;
        position.Y = (player.PlayspaceRoot?.WorldTranslation.Y ?? _rig!.RootTransform.WorldTranslation.Y) + .015f;
        Vector3 forward = _rig!.RootTransform.WorldForward;
        forward.Y = 0;
        if (forward.LengthSquared() < 1e-8f) forward = -Vector3.UnitZ;
        VrSpectatorFollowState.ApplyWorldPose(transform, Matrix4x4.CreateWorld(position, Vector3.Normalize(forward), Vector3.UnitY));
    }

    private void SetGuidesVisible(bool visible)
    {
        foreach (var marker in _markers)
            if (marker.Node is not null) marker.Node.IsActiveSelf = visible;
        if (_leftFootprint is not null) _leftFootprint.IsActiveSelf = visible;
        if (_rightFootprint is not null) _rightFootprint.IsActiveSelf = visible;
    }
}
