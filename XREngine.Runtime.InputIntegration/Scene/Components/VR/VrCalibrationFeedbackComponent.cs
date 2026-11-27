using System.Numerics;
using XREngine.Components.Animation;
using XREngine.Components.Lights;
using XREngine.Data.Colors;
using XREngine.Data.Components.Scene;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.VR;

/// <summary>World-space calibration instructions, assignment labels, stance guides, and a hip-aligned calibration mirror visible to both XR eyes.</summary>
public sealed class VrCalibrationFeedbackComponent : XRComponent
{
    private const float CalibrationMirrorDistanceMeters = 1.45f;
    private const float CalibrationMirrorWidthMeters = 1.45f;
    private const float CalibrationMirrorHeightMeters = 1.35f;
    private const uint CalibrationMirrorResolution = 1024u;
    private const uint CalibrationMirrorMsaaSamples = 1u;

    private static readonly string[] SlotLabels =
        ["Head", "Hips", "Left hand", "Right hand", "Left foot", "Right foot", "Left upper arm", "Right upper arm", "Left knee", "Right knee", "Chest"];
    private readonly (SceneNode Node, Transform Transform, UITextComponent Label)[] _markers = new (SceneNode, Transform, UITextComponent)[16];
    private readonly (VRTrackerTransform? Tracker, SceneNode? SphereNode, bool SeenThisTick)[] _trackerSpheres =
        new (VRTrackerTransform?, SceneNode?, bool)[16];
    private VRPlayerCharacterComponent? _player;
    private IHumanoidVrCalibrationRig? _rig;
    private VRTrackerCollectionComponent? _trackers;
    private SceneNode? _messageRoot, _leftFootprint, _rightFootprint, _calibrationMirrorNode;
    private Transform? _calibrationMirrorTransform;
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
        XREngine.Input.RuntimeVrStateServices.LateXrPoseUpdated += RefreshCalibrationFeedbackForLateXr;
        RegisterTick(ETickGroup.Late, (int)ETickOrder.Scene + 2, UpdateFeedback);
    }
    protected override void OnComponentDeactivated()
    {
        XREngine.Input.RuntimeVrStateServices.LateXrPoseUpdated -= RefreshCalibrationFeedbackForLateXr;
        UnregisterTick(ETickGroup.Late, (int)ETickOrder.Scene + 2, UpdateFeedback);
        if (_messageRoot is not null) _messageRoot.IsActiveSelf = false;
        SetGuidesVisible(false);
        base.OnComponentDeactivated();
    }

    protected override void OnDestroying()
    {
        DestroyTrackerSpheres();
        base.OnDestroying();
    }

    private void EnsureVisuals()
    {
        if (_messageRoot is not null) return;
        (_messageRoot, _, _message) = CreateLabel("VR Calibration Instructions", 1000, 180, .001f);
        for (int index = 0; index < _markers.Length; ++index)
        {
            // Keep the billboard text label 7 cm above the tracker origin. Each cyan pose sphere
            // is parented directly to its VRTrackerTransform so RecalcMatrixOnDraw updates it in
            // lockstep with the tracker model without locomotion lag.
            _markers[index] = CreateLabel($"VR Tracker Label {index}", 240, 50, .0015f, verticalOffset: .07f);
            _markers[index].Node.IsActiveSelf = false;
        }
        _leftFootprint = CreateFootprint("Calibration Left Footprint");
        _rightFootprint = CreateFootprint("Calibration Right Footprint");
        (_calibrationMirrorNode, _calibrationMirrorTransform) = CreateCalibrationMirror();
    }

    private (SceneNode, Transform) CreateCalibrationMirror()
    {
        SceneNode node = SceneNode.NewChild("VR Calibration Mirror");
        node.IsActiveSelf = false;
        Transform transform = node.Transform as Transform ?? node.SetTransform<Transform>();
        _ = node.AddComponent(() => new MirrorCaptureComponent
        {
            CaptureMode = EMirrorCaptureMode.InPlaceStencil,
            UseAdvancedCapturePipeline = true,
            TransparentBackground = true,
            RenderTransparents = true,
            CaptureCullingMask = XREngine.Components.Scene.Transforms.DefaultLayers.CharactersMask,
            TextureWidthOverride = CalibrationMirrorResolution,
            TextureHeightOverride = CalibrationMirrorResolution,
            MsaaSampleCount = CalibrationMirrorMsaaSamples
        });
        return (node, transform);
    }

    private (SceneNode, Transform, UITextComponent) CreateLabel(string name, float width, float height, float scale, float verticalOffset = 0.0f)
    {
        SceneNode anchor = SceneNode.NewChild(name);
        Transform transform = anchor.Transform as Transform ?? anchor.SetTransform<Transform>();
        SceneNode canvasNode = anchor.NewChild("World Canvas");
        UICanvasComponent canvas = canvasNode.AddComponent<UICanvasComponent>()!;
        canvas.PreferOffscreenRenderingForNonScreenSpaces = false;
        UICanvasTransform canvasTransform = canvas.CanvasTransform;
        canvasTransform.DrawSpace = ECanvasDrawSpace.World;
        canvasTransform.Width = width;
        canvasTransform.Height = height;
        canvasTransform.Scale = new(scale);
        canvasTransform.Translation = new(-width * scale * .5f, verticalOffset);
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
        if (node.Transform is not XREngine.Scene.Transforms.Transform)
            node.SetTransform<XREngine.Scene.Transforms.Transform>();
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
            _message.Text = _lastMessage + "\nStand in the footprints. Hold both triggers to capture. Left A starts calibration. Right B cancels and restores the previous rig.";
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
        float messageVerticalOffset = player.IsCalibrating ? .5f : -.15f;
        Vector3 position = headset.WorldTranslation + forward * 1.4f + Vector3.UnitY * messageVerticalOffset;
        Matrix4x4 messageWorld = Matrix4x4.CreateWorld(position, -forward, Vector3.UnitY);
        VrSpectatorFollowState.ApplyWorldPose(messageTransform, messageWorld);
        if (!player.IsCalibrating || _rig is null)
        {
            SetGuidesVisible(false);
            return;
        }
        UpdateCalibrationMirror(headset.WorldTranslation, forward);
        for (int index = 0; index < _trackerSpheres.Length; ++index)
            _trackerSpheres[index].SeenThisTick = false;
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
        HideUnseenTrackerSpheres();
        UpdateFootprint(_leftFootprint, _rig.LeftFootNode, player);
        UpdateFootprint(_rightFootprint, _rig.RightFootNode, player);
    }

    private void UpdateCalibrationMirror(Vector3 headsetWorldPosition, Vector3 horizontalForward)
    {
        if (_calibrationMirrorNode is null || _calibrationMirrorTransform is null)
            return;

        _calibrationMirrorNode.IsActiveSelf = true;
        float feetY = ResolveAvatarFeetY(headsetWorldPosition.Y, useRenderMatrix: false);
        Matrix4x4 mirrorWorld = BuildCalibrationMirrorWorldMatrix(headsetWorldPosition, horizontalForward, feetY);
        VrSpectatorFollowState.ApplyWorldPose(_calibrationMirrorTransform, mirrorWorld);
    }

    /// <summary>
    /// Refreshes the calibration mirror and instruction banner render matrices on the render
    /// thread from the OpenXR late headset pose so both track the headset without update lag.
    /// </summary>
    private void RefreshCalibrationFeedbackForLateXr()
    {
        if (!XREngine.Input.RuntimeVrStateServices.IsOpenXRActive ||
            Player is not { IsCalibrating: true, Headset: { } headset })
            return;

        if (!XREngine.Input.RuntimeVrStateServices.TryGetHeadLocalPose(
                XREngine.Input.RuntimeVrPoseTiming.Late,
                out Matrix4x4 lateHeadLocal))
            return;

        Matrix4x4 lateHeadWorld = lateHeadLocal * headset.ParentRenderMatrix;
        Vector3 headsetPos = lateHeadWorld.Translation;
        Vector3 forward = new(-lateHeadWorld.M31, 0.0f, -lateHeadWorld.M33);
        if (forward.LengthSquared() < 1e-8f)
            forward = -Vector3.UnitZ;
        else
            forward = Vector3.Normalize(forward);

        if (_calibrationMirrorNode is { IsActiveSelf: true } && _calibrationMirrorTransform is not null)
        {
            float feetY = ResolveAvatarFeetY(headsetPos.Y, useRenderMatrix: true);
            Matrix4x4 mirrorWorld = BuildCalibrationMirrorWorldMatrix(headsetPos, forward, feetY);
            _calibrationMirrorTransform.SetRenderMatrixHierarchyImmediate(mirrorWorld);
        }

        if (_messageRoot is { IsActiveSelf: true, Transform: Transform messageTransform })
        {
            Vector3 messagePos = headsetPos + forward * 1.4f + Vector3.UnitY * 0.5f;
            Matrix4x4 messageWorld = Matrix4x4.CreateWorld(messagePos, -forward, Vector3.UnitY);
            messageTransform.SetRenderMatrixHierarchyImmediate(messageWorld);
        }
    }

    private static Matrix4x4 BuildCalibrationMirrorWorldMatrix(
        Vector3 headsetWorldPosition,
        Vector3 horizontalForward,
        float feetY)
    {
        float headTopY = headsetWorldPosition.Y + 0.16f;
        // By reflection on a vertical plane, rays from the headset eyes to the reflected feet and
        // head top intersect the mirror at the midpoints between eye height and those endpoints.
        float mirrorBottomY = 0.5f * (headsetWorldPosition.Y + feetY);
        float mirrorTopY = 0.5f * (headsetWorldPosition.Y + headTopY);
        float mirrorCenterY = 0.5f * (mirrorBottomY + mirrorTopY);

        Vector3 mirrorPosition = new(
            headsetWorldPosition.X + horizontalForward.X * CalibrationMirrorDistanceMeters,
            mirrorCenterY,
            headsetWorldPosition.Z + horizontalForward.Z * CalibrationMirrorDistanceMeters);
        // MirrorCaptureComponent faces +Z (-Transform.RenderForward). Matrix4x4.CreateWorld
        // maps -Z to horizontalForward, so +Z faces directly toward the user.
        return Matrix4x4.CreateScale(CalibrationMirrorWidthMeters, CalibrationMirrorHeightMeters, 1.0f) *
            Matrix4x4.CreateWorld(mirrorPosition, horizontalForward, Vector3.UnitY);
    }

    private float ResolveAvatarFeetY(float headsetY, bool useRenderMatrix)
    {
        float? leftY = useRenderMatrix
            ? _rig?.LeftFootNode?.Transform.RenderTranslation.Y
            : _rig?.LeftFootNode?.Transform.WorldTranslation.Y;
        float? rightY = useRenderMatrix
            ? _rig?.RightFootNode?.Transform.RenderTranslation.Y
            : _rig?.RightFootNode?.Transform.WorldTranslation.Y;
        float candidate;
        if (leftY.HasValue && float.IsFinite(leftY.Value) &&
            rightY.HasValue && float.IsFinite(rightY.Value))
            candidate = MathF.Min(leftY.Value, rightY.Value) - 0.12f;
        else if (leftY.HasValue && float.IsFinite(leftY.Value))
            candidate = leftY.Value - 0.12f;
        else if (rightY.HasValue && float.IsFinite(rightY.Value))
            candidate = rightY.Value - 0.12f;
        else if ((useRenderMatrix
            ? _rig?.HipsNode?.Transform.RenderTranslation.Y
            : _rig?.HipsNode?.Transform.WorldTranslation.Y) is float hipsY && float.IsFinite(hipsY))
            candidate = hipsY - 0.90f;
        else
            candidate = headsetY - 1.65f;

        return Math.Clamp(candidate, headsetY - 2.4f, headsetY - 0.6f);
    }

    private void UpdateMarker(VRTrackerTransform tracker, ref int count, Vector3 viewer)
    {
        if (!tracker.PoseCurrentlyUsable || count >= _markers.Length || _rig is null) return;
        EnsureTrackerSphereVisible(tracker);
        var marker = _markers[count++];
        marker.Node.IsActiveSelf = true;
        marker.Label.Text = DescribeBinding(_rig, tracker);
        Matrix4x4 renderMatrix = tracker.RenderMatrix;
        Vector3 position = !renderMatrix.Equals(Matrix4x4.Identity)
            ? renderMatrix.Translation
            : tracker.WorldTranslation;
        Quaternion rotation = VrSpectatorFollowState.LookAt(position, viewer, Quaternion.Identity);
        Matrix4x4 world = Matrix4x4.CreateFromQuaternion(rotation);
        world.Translation = position;
        VrSpectatorFollowState.ApplyWorldPose(marker.Transform, world);
    }

    /// <summary>
    /// Attaches or activates a cyan pose sphere directly under <paramref name="tracker"/> so
    /// render-thread pose updates move the sphere together with the tracker model without lag.
    /// </summary>
    private void EnsureTrackerSphereVisible(VRTrackerTransform tracker)
    {
        SceneNode? trackerNode = tracker.SceneNode;
        if (trackerNode is null || trackerNode.IsDestroyed || trackerNode.IsDestroyQueued)
            return;

        int freeIndex = -1;
        for (int index = 0; index < _trackerSpheres.Length; ++index)
        {
            var entry = _trackerSpheres[index];
            if (ReferenceEquals(entry.Tracker, tracker))
            {
                if (entry.SphereNode is { IsDestroyed: false, IsDestroyQueued: false } existingSphere)
                {
                    if (!existingSphere.IsActiveSelf)
                        existingSphere.IsActiveSelf = true;
                    _trackerSpheres[index].SeenThisTick = true;
                    return;
                }

                freeIndex = index;
                break;
            }

            if (freeIndex < 0 && (entry.Tracker is null || entry.Tracker.IsDestroyed ||
                entry.SphereNode is null || entry.SphereNode.IsDestroyed || entry.SphereNode.IsDestroyQueued))
            {
                freeIndex = index;
            }
        }

        if (freeIndex < 0)
            return;

        SceneNode sphereNode = trackerNode.NewChild("VR Calibration Tracker Sphere");
        if (sphereNode.Transform is not XREngine.Scene.Transforms.Transform)
            _ = sphereNode.SetTransform<XREngine.Scene.Transforms.Transform>();
        _ = sphereNode.AddComponent<VrCalibrationTrackerMarkerComponent>()!;
        sphereNode.IsActiveSelf = true;
        _trackerSpheres[freeIndex] = (tracker, sphereNode, true);
    }

    private void HideUnseenTrackerSpheres()
    {
        for (int index = 0; index < _trackerSpheres.Length; ++index)
        {
            var entry = _trackerSpheres[index];
            if (entry.SphereNode is null)
                continue;

            if (entry.Tracker is null || entry.Tracker.IsDestroyed ||
                entry.SphereNode.IsDestroyed || entry.SphereNode.IsDestroyQueued)
            {
                _trackerSpheres[index] = default;
                continue;
            }

            if (!entry.SeenThisTick && entry.SphereNode.IsActiveSelf)
                entry.SphereNode.IsActiveSelf = false;
        }
    }

    private void DestroyTrackerSpheres()
    {
        for (int index = 0; index < _trackerSpheres.Length; ++index)
        {
            SceneNode? sphereNode = _trackerSpheres[index].SphereNode;
            _trackerSpheres[index] = default;
            if (sphereNode is { IsDestroyed: false, IsDestroyQueued: false })
                sphereNode.Destroy();
        }
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
        for (int index = 0; index < _trackerSpheres.Length; ++index)
            if (_trackerSpheres[index].SphereNode is { IsDestroyed: false, IsDestroyQueued: false } sphere)
                sphere.IsActiveSelf = visible;
        if (_leftFootprint is not null) _leftFootprint.IsActiveSelf = visible;
        if (_rightFootprint is not null) _rightFootprint.IsActiveSelf = visible;
        if (_calibrationMirrorNode is not null) _calibrationMirrorNode.IsActiveSelf = visible;
    }
}
