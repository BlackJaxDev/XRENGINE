using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

/// <summary>Owns one concrete IK target per VR slot and its frozen device-to-target offset.</summary>
internal sealed class VRIKCalibrationRig(VRIKSolverComponent owner)
{
    private sealed class Slot
    {
        public Transform? Target;
        public TransformBase? Device;
        public Matrix4x4 Offset = Matrix4x4.Identity;
        public string? Identity;
        public float Weight;
        public EVrCalibrationSource Source;
        public Matrix4x4 LastWorld = Matrix4x4.Identity;
        public bool HasWorld;
        public bool CreatedByRig;
    }

    private readonly Slot[] _slots = CreateSlots();
    private VRIKCalibrationSettings? _settings;

    private static Slot[] CreateSlots()
    {
        Slot[] slots = new Slot[11];
        for (int i = 0; i < slots.Length; i++)
            slots[i] = new Slot();
        return slots;
    }

    public VrCalibrationResult Capture(VrCalibrationRequest request)
    {
        if (request.Settings is not VRIKCalibrationSettings settings)
            return VrCalibrationResult.Failed("Calibration settings are unavailable.");
        if (!owner.EnsureInitializedForCalibration())
            return VrCalibrationResult.Failed("The avatar IK solver is not ready.");
        if (request.Slots.Length != _slots.Length || request.Offsets.Length != _slots.Length)
            return VrCalibrationResult.Failed("The calibration slot count is invalid.");

        HumanoidComponent human = owner.Humanoid;
        if (owner.Root is null || human.Head.Node is null)
            return VrCalibrationResult.Failed("The avatar is missing its root or head bone.");
        if (!IsFiniteAffine(owner.Root.WorldMatrix)
            || !Matrix4x4.Invert(owner.Root.WorldMatrix, out Matrix4x4 rootInverse)
            || !IsFiniteAffine(rootInverse))
            return VrCalibrationResult.Failed("The avatar root transform is singular or invalid.");
        if (request.Slots[(int)EHumanoidIKTarget.Head]?.Device is null
            || request.Slots[(int)EHumanoidIKTarget.LeftHand]?.Device is null
            || request.Slots[(int)EHumanoidIKTarget.RightHand]?.Device is null)
            return VrCalibrationResult.Failed("The headset and both controllers must be tracking.");
        VrCalibrationCapture headCapture = request.Slots[(int)EHumanoidIKTarget.Head]!.Value;
        Matrix4x4 headWorld = headCapture.DeviceWorld;
        if (!IsRigidPose(headWorld))
            return VrCalibrationResult.Failed("The headset pose is invalid.");
        if (request.RequireLevelHead)
        {
            float tolerance = request.HeadTiltTolerance ?? settings.CalibrationHeadTiltTolerance;
            if (!float.IsFinite(tolerance) || tolerance is < 0f or > 45f)
                return VrCalibrationResult.Failed("The head tilt tolerance is invalid.");
            Vector3 headUp = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, headWorld));
            float tilt = float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(headUp, Vector3.UnitY), -1f, 1f)));
            if (tilt > tolerance)
                return VrCalibrationResult.Failed("Look straight ahead and keep your head level, then pull both triggers again.");
        }

        Matrix4x4[] offsets = new Matrix4x4[_slots.Length];
        Matrix4x4[] targetWorld = new Matrix4x4[_slots.Length];
        for (int i = 0; i < _slots.Length; i++)
        {
            VrCalibrationCapture? capture = request.Slots[i];
            if (capture is null)
                continue;
            if (capture.Value.Device is not null && (capture.Value.SnapshotId != headCapture.SnapshotId ||
                capture.Value.SampleTime != headCapture.SampleTime))
                return VrCalibrationResult.Failed("Tracking samples changed during capture. Hold still and retry.");

            Matrix4x4 source = capture.Value.DeviceWorld;
            if (!IsRigidPose(source) || !Matrix4x4.Invert(source, out Matrix4x4 inverse))
                return VrCalibrationResult.Failed($"The {((EHumanoidIKTarget)i)} tracker pose is invalid.");

            EHumanoidIKTarget slot = (EHumanoidIKTarget)i;
            Matrix4x4 offset;
            if (request.Offsets[i] is Matrix4x4 restoredOffset)
                offset = restoredOffset;
            else if (slot == EHumanoidIKTarget.Head)
            {
                if (!TryGetHeadOffset(human, request.HeadsetToEyes, out offset))
                    return VrCalibrationResult.Failed("The avatar head bind pose or body facing basis is unavailable.");
            }
            else if (slot == EHumanoidIKTarget.LeftHand)
            {
                if (!TryGetWristOffset(human, human.Left.Wrist.Node?.Transform, request.LeftGripToWrist, out offset))
                    return VrCalibrationResult.Failed("The left wrist bind pose or body facing basis is unavailable.");
            }
            else if (slot == EHumanoidIKTarget.RightHand)
            {
                if (!TryGetWristOffset(human, human.Right.Wrist.Node?.Transform, request.RightGripToWrist, out offset))
                    return VrCalibrationResult.Failed("The right wrist bind pose or body facing basis is unavailable.");
            }
            else
            {
                TransformBase? bone = GetBone(human, slot);
                if (bone is null)
                    return VrCalibrationResult.Failed($"The avatar has no {slot} bone.");
                bone.RecalculateMatrices(true);
                offset = ToRigidPose(bone.WorldMatrix) * inverse;
            }

            Matrix4x4 world = offset * source;
            if (!IsRigidPose(offset) || !IsRigidPose(world))
                return VrCalibrationResult.Failed($"The {slot} calibration offset is invalid.");
            offsets[i] = offset;
            targetWorld[i] = world;
        }

        // Keep the previous published rig intact if scene publication fails.
        Slot[] saved = CreateSlots();
        (TransformBase? tfm, Matrix4x4 offset)[] bindings = new (TransformBase?, Matrix4x4)[_slots.Length];
        Matrix4x4[] savedWorld = new Matrix4x4[_slots.Length];
        for (int i = 0; i < _slots.Length; i++)
        {
            Slot state = _slots[i];
            saved[i].Target = state.Target;
            saved[i].Device = state.Device;
            saved[i].Offset = state.Offset;
            saved[i].Identity = state.Identity;
            saved[i].Weight = state.Weight;
            saved[i].Source = state.Source;
            saved[i].LastWorld = state.LastWorld;
            saved[i].HasWorld = state.HasWorld;
            saved[i].CreatedByRig = state.CreatedByRig;
            bindings[i] = human.GetIKTarget((EHumanoidIKTarget)i);
            savedWorld[i] = state.Target?.WorldMatrix ?? Matrix4x4.Identity;
        }
        VRIKCalibrationSettings? previousSettings = _settings;
        VRIKSolverWeightSnapshot previousWeights = new(owner.Solver);
        try
        {
            _settings = settings;
            for (int i = 0; i < _slots.Length; i++)
            {
                Slot state = _slots[i];
                EHumanoidIKTarget slot = (EHumanoidIKTarget)i;
                if (state.Target is null)
                {
                    TransformBase? previousTarget = human.GetIKTargetTransform(slot);
                    state.Target = human.EnsureOwnedVrIKTarget(slot);
                    state.CreatedByRig = !ReferenceEquals(previousTarget, state.Target);
                }
                human.SetIKTarget(slot, state.Target, Matrix4x4.Identity);
                if (request.Slots[i] is not VrCalibrationCapture capture)
                {
                    state.Device = null;
                    state.Identity = null;
                    state.Weight = 0f;
                    state.Source = EVrCalibrationSource.None;
                    state.HasWorld = false;
                    ApplyWeight(slot, 0f, settings);
                    continue;
                }

                state.Device = capture.Device;
                state.Identity = capture.Identity;
                state.Offset = offsets[i];
                state.Weight = capture.Device is null ? 0f : 1f;
                state.Source = capture.Device is null ? EVrCalibrationSource.None : EVrCalibrationSource.BoundTracker;
                SetTargetWorld(state.Target, targetWorld[i]);
                state.LastWorld = targetWorld[i];
                state.HasWorld = true;
                ApplyWeight(slot, state.Weight, settings);
            }

            owner.Solver.Spine.MinHeadHeight = 0f;
            owner.Solver.PlantFeet = false;
            VrCalibrationSlotState[] published = new VrCalibrationSlotState[_slots.Length];
            for (int i = 0; i < _slots.Length; i++)
            {
                Slot state = _slots[i];
                published[i] = new VrCalibrationSlotState(state.Device, state.Target!, state.Offset,
                    state.Identity, state.Weight, state.Source);
            }
            // The solver may have initialized for head-and-hands tracking before the
            // avatar entered its calibration pose. Its per-frame reset must use the
            // displayed pose that supplied these body offsets.
            if (request.RequireLevelHead)
                owner.Solver.StoreDefaultLocalState();
            return new VrCalibrationResult(true, Slots: published);
        }
        catch (Exception exception)
        {
            _settings = previousSettings;
            for (int i = 0; i < _slots.Length; i++)
            {
                Slot current = _slots[i];
                Slot previous = saved[i];
                if (current.Target is not null && !ReferenceEquals(current.Target, previous.Target))
                {
                    var node = current.Target.SceneNode;
                    node?.Parent?.RemoveChild(node);
                }
                current.Target = previous.Target;
                current.Device = previous.Device;
                current.Offset = previous.Offset;
                current.Identity = previous.Identity;
                current.Weight = previous.Weight;
                current.Source = previous.Source;
                current.LastWorld = previous.LastWorld;
                current.HasWorld = previous.HasWorld;
                current.CreatedByRig = previous.CreatedByRig;
                human.SetIKTarget((EHumanoidIKTarget)i, bindings[i].tfm, bindings[i].offset);
                if (previous.Target is not null)
                    SetTargetWorld(previous.Target, savedWorld[i]);
            }
            previousWeights.Restore(owner.Solver);
            return VrCalibrationResult.Failed($"Calibration could not be published: {exception.Message}");
        }
    }

    public bool TryGet(EHumanoidIKTarget slot, out VrCalibrationSlotState state)
    {
        if (!TryIndex(slot, out int index) || _slots[index].Target is not Transform target)
        {
            state = default;
            return false;
        }
        Slot source = _slots[index];
        state = new VrCalibrationSlotState(source.Device, target, source.Offset, source.Identity, source.Weight, source.Source);
        return true;
    }

    /// <summary>Release only target nodes this rig created, preserving targets owned by other systems.</summary>
    public void Dispose()
    {
        HumanoidComponent? human = owner.CalibrationHumanoid;
        for (int i = 0; i < _slots.Length; i++)
        {
            Slot state = _slots[i];
            Transform? target = state.Target;
            if (target is null)
                continue;
            EHumanoidIKTarget slot = (EHumanoidIKTarget)i;
            if (state.CreatedByRig && human is not null && ReferenceEquals(human.GetIKTargetTransform(slot), target))
                human.ClearIKTarget(slot);
            if (state.CreatedByRig && target.SceneNode is { } node)
                node.Parent?.RemoveChild(node);
            state.Target = null;
            state.Device = null;
            state.Identity = null;
            state.HasWorld = false;
            state.Weight = 0f;
            state.Source = EVrCalibrationSource.None;
        }
        _settings = null;
    }

    public bool Update(EHumanoidIKTarget slot, Matrix4x4 deviceWorld, float weight)
    {
        if (!TryIndex(slot, out int index) || !IsRigidPose(deviceWorld) || !IsValidWeight(weight))
            return false;
        Slot source = _slots[index];
        if (source.Target is null || source.Device is null)
            return false;
        Matrix4x4 targetWorld = source.Offset * deviceWorld;
        if (!IsValidPose(targetWorld))
            return false;
        SetTargetWorld(source.Target, targetWorld);
        source.LastWorld = targetWorld;
        source.HasWorld = true;
        source.Source = EVrCalibrationSource.BoundTracker;
        return SetWeight(slot, weight);
    }

    public bool UpdateEstimate(EHumanoidIKTarget slot, Matrix4x4 targetWorld, float weight)
    {
        if (!TryIndex(slot, out int index) || !IsRigidPose(targetWorld) || !IsValidWeight(weight))
            return false;
        Slot source = _slots[index];
        if (source.Target is null)
            return false;
        SetTargetWorld(source.Target, targetWorld);
        source.LastWorld = targetWorld;
        source.HasWorld = true;
        source.Source = EVrCalibrationSource.Estimator;
        return SetWeight(slot, weight);
    }

    public bool SetWeight(EHumanoidIKTarget slot, float weight)
    {
        if (!TryIndex(slot, out int index) || !IsValidWeight(weight))
            return false;
        Slot source = _slots[index];
        if (source.Target is null)
            return false;
        source.Weight = weight;
        if (weight == 0f)
            source.Source = EVrCalibrationSource.None;
        ApplyWeight(slot, weight, null);
        return true;
    }

    public bool Rebind(EHumanoidIKTarget slot, TransformBase device, string identity)
    {
        if (!TryIndex(slot, out int index) || string.IsNullOrWhiteSpace(identity))
            return false;
        Slot source = _slots[index];
        if (source.Target is null || !string.Equals(source.Identity, identity, StringComparison.Ordinal))
            return false;
        source.Device = device;
        return true;
    }

    /// <summary>Keep the last sampled world goal fixed while the scaled avatar root moves.</summary>
    public void RefreshTargets()
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            Slot slot = _slots[i];
            if (!slot.HasWorld || slot.Target is not Transform target)
                continue;
            Matrix4x4.Decompose(slot.LastWorld, out _, out Quaternion rotation, out Vector3 translation);
            if (Vector3.DistanceSquared(target.WorldTranslation, translation) < 1e-10f
                && MathF.Abs(Quaternion.Dot(target.WorldRotation, rotation)) > 0.999999f)
                continue;
            SetTargetWorld(target, slot.LastWorld);
        }
    }

    private void ApplyWeight(EHumanoidIKTarget slot, float weight, VRIKCalibrationSettings? settings)
    {
        settings ??= _settings;
        IKSolverVR solver = owner.Solver;
        switch (slot)
        {
            case EHumanoidIKTarget.Head:
                solver.Spine.PositionWeight = weight;
                solver.Spine.RotationWeight = weight;
                break;
            case EHumanoidIKTarget.Hips:
                solver.Spine.HipsPositionWeight = weight * (settings?.HipPositionWeight ?? 1f);
                solver.Spine.HipsRotationWeight = weight * (settings?.HipRotationWeight ?? 1f);
                break;
            case EHumanoidIKTarget.Chest:
                solver.Spine.ChestGoalWeight = weight;
                break;
            case EHumanoidIKTarget.LeftHand:
                solver.LeftArm.Settings.PositionWeight = weight;
                solver.LeftArm.Settings.RotationWeight = weight;
                break;
            case EHumanoidIKTarget.RightHand:
                solver.RightArm.Settings.PositionWeight = weight;
                solver.RightArm.Settings.RotationWeight = weight;
                break;
            case EHumanoidIKTarget.LeftFoot:
                solver.LeftLeg.PositionWeight = weight;
                solver.LeftLeg.RotationWeight = weight;
                break;
            case EHumanoidIKTarget.RightFoot:
                solver.RightLeg.PositionWeight = weight;
                solver.RightLeg.RotationWeight = weight;
                break;
            case EHumanoidIKTarget.LeftElbow:
                solver.LeftArm.Settings.BendGoalWeight = weight;
                solver.LeftArm.UpperArmGoalWeight = weight;
                break;
            case EHumanoidIKTarget.RightElbow:
                solver.RightArm.Settings.BendGoalWeight = weight;
                solver.RightArm.UpperArmGoalWeight = weight;
                break;
            case EHumanoidIKTarget.LeftKnee:
                solver.LeftLeg.KneeTargetWeight = weight;
                break;
            case EHumanoidIKTarget.RightKnee:
                solver.RightLeg.KneeTargetWeight = weight;
                break;
        }
    }

    private static bool TryGetHeadOffset(HumanoidComponent human, Matrix4x4? requested, out Matrix4x4 offset)
    {
        offset = Matrix4x4.Identity;
        if (human.Head.Node is null ||
            !human.TryGetVrBindBodyToEngine(out Matrix4x4 bindBodyToEngine))
            return false;
        TransformBase head = human.Head.Node.Transform;
        Matrix4x4 headInRootBind = head.BindMatrix * human.Transform.InverseBindMatrix;
        if (!Matrix4x4.Decompose(headInRootBind, out _, out Quaternion headRotation, out Vector3 headPosition))
            return false;

        Vector3 headsetToHead;
        if (requested is Matrix4x4 measuredOffset)
            headsetToHead = measuredOffset.Translation;
        else
        {
            TransformBase eyes = human.EyesTarget.Node?.Transform ?? head;
            Matrix4x4 eyesInRootBind = eyes.BindMatrix * human.Transform.InverseBindMatrix;
            if (!Matrix4x4.Decompose(human.Transform.WorldMatrix, out Vector3 rootScale, out _, out _))
                return false;
            headsetToHead = Vector3.Multiply(headPosition - eyesInRootBind.Translation, rootScale);
        }

        // Bind anatomy and eye displacement share the avatar-root basis. Convert
        // both before composing the target with the headset's tracked world pose.
        Matrix4x4 orientation = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(headRotation)) * bindBodyToEngine;
        orientation.Translation = Vector3.TransformNormal(headsetToHead, bindBodyToEngine);
        offset = orientation;
        return IsRigidPose(offset);
    }

    private static bool TryGetWristOffset(HumanoidComponent human, TransformBase? wrist, Matrix4x4 gripPreset, out Matrix4x4 offset)
    {
        offset = Matrix4x4.Identity;
        if (wrist is null || !human.TryGetVrBindBodyToEngine(out Matrix4x4 bindBodyToEngine))
            return false;
        Matrix4x4 wristInRootBind = wrist.BindMatrix * human.Transform.InverseBindMatrix;
        if (!Matrix4x4.Decompose(wristInRootBind, out _, out Quaternion wristRotation, out _) ||
            !Matrix4x4.Decompose(gripPreset, out _, out Quaternion presetRotation, out Vector3 presetTranslation))
            return false;

        // Keep the grip-space metric displacement authored by the profile or user,
        // while orienting the bind wrist toward the normalized body heading.
        offset = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(wristRotation))
            * bindBodyToEngine
            * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(presetRotation));
        offset.Translation = presetTranslation;
        return IsRigidPose(offset);
    }

    private static TransformBase? GetBone(HumanoidComponent human, EHumanoidIKTarget slot)
        => slot switch
        {
            EHumanoidIKTarget.Hips => human.Hips.Node?.Transform,
            EHumanoidIKTarget.Chest => (human.Chest.Node ?? human.UpperChest.Node)?.Transform,
            EHumanoidIKTarget.LeftFoot => (human.Left.Toes.Node ?? human.Left.Foot.Node)?.Transform,
            EHumanoidIKTarget.RightFoot => (human.Right.Toes.Node ?? human.Right.Foot.Node)?.Transform,
            EHumanoidIKTarget.LeftElbow => human.Left.Elbow.Node?.Transform,
            EHumanoidIKTarget.RightElbow => human.Right.Elbow.Node?.Transform,
            EHumanoidIKTarget.LeftKnee => human.Left.Knee.Node?.Transform,
            EHumanoidIKTarget.RightKnee => human.Right.Knee.Node?.Transform,
            _ => null,
        };

    private static void SetTargetWorld(Transform target, Matrix4x4 world)
    {
        // A newly created target root may not have published its inherited scale yet.
        // Resolve ancestors before converting this world pose into target-local space.
        RecalculateDirtyAncestors(target.Parent);
        Matrix4x4.Decompose(world, out _, out Quaternion rotation, out Vector3 translation);
        target.SetWorldTranslationRotation(translation, Quaternion.Normalize(rotation));
        target.RecalculateMatrices(true);
    }

    private static void RecalculateDirtyAncestors(TransformBase? ancestor)
    {
        if (ancestor is null)
            return;
        RecalculateDirtyAncestors(ancestor.Parent);
        if (ancestor.IsLocalMatrixDirty || ancestor.IsWorldMatrixDirty)
            ancestor.RecalculateMatrices();
    }

    private static bool TryIndex(EHumanoidIKTarget slot, out int index)
    {
        index = (int)slot;
        return (uint)index < 11u;
    }

    private static bool IsValidWeight(float weight) => float.IsFinite(weight) && weight is >= 0f and <= 1f;

    private static Matrix4x4 ToRigidPose(Matrix4x4 matrix)
    {
        Matrix4x4.Decompose(matrix, out _, out Quaternion rotation, out Vector3 translation);
        return Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation)) * Matrix4x4.CreateTranslation(translation);
    }

    internal static bool IsRigidPose(Matrix4x4 matrix)
    {
        if (!IsValidPose(matrix) || !Matrix4x4.Decompose(matrix, out Vector3 scale, out _, out _))
            return false;
        return MathF.Abs(scale.X - 1f) < 0.01f
            && MathF.Abs(scale.Y - 1f) < 0.01f
            && MathF.Abs(scale.Z - 1f) < 0.01f;
    }

    private static bool IsValidPose(Matrix4x4 matrix)
    {
        if (!IsFiniteAffine(matrix) || MathF.Abs(matrix.GetDeterminant()) < 1e-6f)
            return false;
        if (!Matrix4x4.Decompose(matrix, out Vector3 scale, out Quaternion rotation, out Vector3 translation))
            return false;
        return float.IsFinite(scale.X) && float.IsFinite(scale.Y) && float.IsFinite(scale.Z)
            && MathF.Abs(scale.X) > 1e-6f && MathF.Abs(scale.Y) > 1e-6f && MathF.Abs(scale.Z) > 1e-6f
            && float.IsFinite(rotation.X) && float.IsFinite(rotation.Y) && float.IsFinite(rotation.Z) && float.IsFinite(rotation.W)
            && rotation.LengthSquared() > 1e-8f
            && float.IsFinite(translation.X) && float.IsFinite(translation.Y) && float.IsFinite(translation.Z);
    }

    private static bool IsFiniteAffine(Matrix4x4 m)
        => float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14)
        && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24)
        && float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34)
        && float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44)
        && MathF.Abs(m.M14) < 1e-5f && MathF.Abs(m.M24) < 1e-5f
        && MathF.Abs(m.M34) < 1e-5f && MathF.Abs(m.M44 - 1f) < 1e-5f;
}
