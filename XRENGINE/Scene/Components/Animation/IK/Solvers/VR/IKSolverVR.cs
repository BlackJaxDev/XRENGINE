using Extensions;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering.Physics.Physx;
using Transform = XREngine.Scene.Transforms.Transform;

namespace XREngine.Scene.Components.Animation
{
    /// <summary>
    /// Hybrid %IK solver designed for mapping a character to a VR headset and 2 hand controllers 
    /// </summary>
    [System.Serializable]
    public partial class IKSolverVR : IKSolver
    {
        public AnimStateMachineComponent? Animator { get; private set; }

        /// <summary>
        /// Sets this VRIK up to the specified bone references.
        /// </summary>
        public void SetToReferences(HumanoidComponent? humanoid)
        {
            Animator = humanoid?.SceneNode?.GetComponent<AnimStateMachineComponent>();

            _solverTransforms = GetTransforms(humanoid);

            _hasChest = humanoid?.Chest.Node != null;
            _hasNeck = humanoid?.Neck.Node != null;
            _hasShoulders = humanoid?.Left.Shoulder.Node != null || humanoid?.Right.Shoulder.Node != null;
            _hasToes = humanoid?.Left.Toes.Node != null || humanoid?.Right.Toes.Node != null;
            _hasLegs = humanoid?.Left.Leg.Node != null || humanoid?.Right.Leg.Node != null;
            _hasArms = humanoid?.Left.Arm.Node != null || humanoid?.Right.Arm.Node != null;

            _readPositions = new Vector3[_solverTransforms.Length];
            _readRotations = new Quaternion[_solverTransforms.Length];

            //DefaultAnimationCurves();
            GuessHandOrientations(humanoid, true);
        }

        private static Transform[] GetTransforms(HumanoidComponent? humanoid) =>
        [
            humanoid?.SceneNode?.GetTransformAs<Transform>(true)!,
            humanoid?.Hips.Node?.GetTransformAs<Transform>(true)!,
            humanoid?.Spine.Node?.GetTransformAs<Transform>(true)!,
            humanoid?.Chest.Node?.GetTransformAs<Transform>(true)!,
            humanoid?.Neck.Node?.GetTransformAs<Transform>(true)!,
            humanoid?.Head.Node?.GetTransformAs<Transform>(true)!,
            humanoid?.Left.Shoulder.Node?.GetTransformAs<Transform>(true)!,
            humanoid?.Left.Arm.Node?.GetTransformAs<Transform>(true)!,
            humanoid?.Left.Elbow.Node?.GetTransformAs<Transform>(true)!,
            humanoid?.Left.Wrist.Node?.GetTransformAs<Transform>(true)!,
            humanoid?.Right.Shoulder.Node?.GetTransformAs<Transform>(true)!,
            humanoid?.Right.Arm.Node?.GetTransformAs<Transform>(true)!,
            humanoid?.Right.Elbow.Node?.GetTransformAs<Transform>(true)!,
            humanoid?.Right.Wrist.Node?.GetTransformAs<Transform>(true)!,
            humanoid?.Left.Leg.Node?.GetTransformAs<Transform>(true)!,
            humanoid?.Left.Knee.Node?.GetTransformAs<Transform>(true)!,
            humanoid?.Left.Foot.Node?.GetTransformAs<Transform>(true)!,
            humanoid?.Left.Toes.Node?.GetTransformAs<Transform>(true)!,
            humanoid?.Right.Leg.Node?.GetTransformAs<Transform>(true)!,
            humanoid?.Right.Knee.Node?.GetTransformAs<Transform>(true)!,
            humanoid?.Right.Foot.Node?.GetTransformAs<Transform>(true)!,
            humanoid?.Right.Toes.Node?.GetTransformAs<Transform>(true)!
        ];

        /// <summary>
        /// Guesses the hand bones orientations ('Wrist To Palm Axis' and "Palm To Thumb Axis" of the arms) based on the provided references. if onlyIfZero is true, will only guess an orientation axis if it is Vector3.zero.
        /// </summary>
        public void GuessHandOrientations(HumanoidComponent? humanoid, bool onlyIfZero)
        {
            if (humanoid is null)
            {
                Debug.LogWarning("VRIK: Humanoid is null, can not guess hand orientations.");
                return;
            }

            if (_leftArm._wristToPalmAxis == Vector3.Zero || !onlyIfZero)
                _leftArm._wristToPalmAxis = VRIKCalibrator.GuessWristToPalmAxis(
                    humanoid.Left.Wrist.Node?.GetTransformAs<Transform>(true), 
                    humanoid.Left.Elbow.Node?.GetTransformAs<Transform>(true));
            
            if (_leftArm._palmToThumbAxis == Vector3.Zero || !onlyIfZero)
                _leftArm._palmToThumbAxis = VRIKCalibrator.GuessPalmToThumbAxis(
                    humanoid.Left.Wrist.Node?.GetTransformAs<Transform>(true),
                    humanoid.Left.Elbow.Node?.GetTransformAs<Transform>(true));
            
            if (_rightArm._wristToPalmAxis == Vector3.Zero || !onlyIfZero)
                _rightArm._wristToPalmAxis = VRIKCalibrator.GuessWristToPalmAxis(
                    humanoid.Right.Wrist.Node?.GetTransformAs<Transform>(true),
                    humanoid.Right.Elbow.Node?.GetTransformAs<Transform>(true));
            
            if (_rightArm._palmToThumbAxis == Vector3.Zero || !onlyIfZero)
                _rightArm._palmToThumbAxis = VRIKCalibrator.GuessPalmToThumbAxis(
                    humanoid.Right.Wrist.Node?.GetTransformAs<Transform>(true),
                    humanoid.Right.Elbow.Node?.GetTransformAs<Transform>(true));
        }

        ///// <summary>
        ///// Set default values for the animation curves if they have no keys.
        ///// </summary>
        //public void DefaultAnimationCurves()
        //{
        //    if (_locomotion.stepHeight == null) 
        //        _locomotion.stepHeight = new AnimationCurve();

        //    if (_locomotion.heelHeight == null) 
        //        _locomotion.heelHeight = new AnimationCurve();

        //    if (_locomotion.stepHeight.keys.Length == 0)
        //        _locomotion.stepHeight.keys = GetSineKeyframes(0.03f);
            
        //    if (_locomotion.heelHeight.keys.Length == 0)
        //        _locomotion.heelHeight.keys = GetSineKeyframes(0.03f);
        //}

        /// <summary>
        /// Adds position offset to a body part. Position offsets add to the targets in VRIK.
        /// </summary>
        public void AddPositionOffset(EPositionOffset positionOffset, Vector3 value)
        {
            switch (positionOffset)
            {
                case EPositionOffset.Pelvis: _spine._pelvisPositionOffset += value; return;
                case EPositionOffset.Chest: _spine._chestPositionOffset += value; return;
                case EPositionOffset.Head: _spine._headPositionOffset += value; return;
                case EPositionOffset.LeftHand: _leftArm._handPositionOffset += value; return;
                case EPositionOffset.RightHand: _rightArm._handPositionOffset += value; return;
                case EPositionOffset.LeftFoot: _leftLeg._footPositionOffset += value; return;
                case EPositionOffset.RightFoot: _rightLeg._footPositionOffset += value; return;
                case EPositionOffset.LeftHeel: _leftLeg._heelPositionOffset += value; return;
                case EPositionOffset.RightHeel: _rightLeg._heelPositionOffset += value; return;
            }
        }

        /// <summary>
        /// Adds rotation offset to a body part. Rotation offsets add to the targets in VRIK
        /// </summary>
        public void AddRotationOffset(ERotationOffset rotationOffset, Vector3 value)
            => AddRotationOffset(rotationOffset, Quaternion.CreateFromYawPitchRoll(value.Y, value.X, value.Z));

        /// <summary>
        /// Adds rotation offset to a body part. Rotation offsets add to the targets in VRIK
        /// </summary>
        public void AddRotationOffset(ERotationOffset rotationOffset, Quaternion value)
        {
            switch (rotationOffset)
            {
                case ERotationOffset.Pelvis:
                    _spine._pelvisRotationOffset = value * _spine._pelvisRotationOffset;
                    return;
                case ERotationOffset.Chest:
                    _spine._chestRotationOffset = value * _spine._chestRotationOffset;
                    return;
                case ERotationOffset.Head:
                    _spine._headRotationOffset = value * _spine._headRotationOffset;
                    return;
            }
        }

        /// <summary>
        /// Call this in each Update if your avatar is standing on a moving platform
        /// </summary>
        public void AddPlatformMotion(Vector3 deltaPosition, Quaternion deltaRotation, Vector3 platformPivot)
        {
            _locomotion.AddDeltaPosition(deltaPosition);
            _raycastOriginPelvis += deltaPosition;

            _locomotion.AddDeltaRotation(deltaRotation, platformPivot);
            _spine._faceDirection = deltaRotation.Rotate(_spine._faceDirection);
        }

        /// <summary>
        /// Resets all tweens, blendings and lerps. Call this after you have teleported the character.
        /// </summary>
        public void Reset()
        {
            if (!Initialized)
                return;

            UpdateSolverTransforms();
            Read(_readPositions, _readRotations, _hasChest, _hasNeck, _hasShoulders, _hasToes, _hasLegs, _hasArms);

            _spine._faceDirection = RootBone._readRotation.Rotate(Globals.Forward);

            if (_hasLegs)
            {
                _locomotion.Reset(_readPositions, _readRotations);
                _raycastOriginPelvis = _spine.Hips._readPosition;
            }
        }

        public override void StoreDefaultLocalState()
        {
            for (int i = 1; i < _solverTransforms.Length; i++)
            {
                if (_solverTransforms[i] != null)
                {
                    _defaultLocalPositions[i - 1] = _solverTransforms[i].Translation;
                    _defaultLocalRotations[i - 1] = _solverTransforms[i].Rotation;
                }
            }
        }

        public override void ResetTransformToDefault()
        {
            if (!Initialized)
                return;

            if (_lod >= 2)
                return;

            for (int i = 1; i < _solverTransforms.Length; i++)
            {
                if (_solverTransforms[i] != null)
                {
                    bool isPelvis = i == 1;

                    bool isArmStretchable = i == 8 || i == 9 || i == 12 || i == 13;
                    bool isLegStretchable = (i >= 15 && i <= 17) || (i >= 19 && i <= 21);

                    if (isPelvis || isArmStretchable || isLegStretchable)
                        _solverTransforms[i].Translation = _defaultLocalPositions[i - 1];
                    
                    _solverTransforms[i].Rotation = _defaultLocalRotations[i - 1];
                }
            }
        }

        public override IKSolver.IKPoint[]? GetPoints() => null;
        public override IKSolver.IKPoint? GetPoint(Transform transform) => null;

        public override bool IsValid(ref string message)
        {
            if (_solverTransforms == null || _solverTransforms.Length == 0)
            {
                message = "Trying to initialize IKSolverVR with invalid bone references.";
                return false;
            }

            if (_leftArm._wristToPalmAxis == Vector3.Zero)
            {
                message = "Left arm 'Wrist To Palm Axis' needs to be set in VRIK. " +
                    "Please select the hand bone, set it to the axis that points from the wrist towards the palm. " +
                    "If the arrow points away from the palm, axis must be negative.";
                return false;
            }

            if (_rightArm._wristToPalmAxis == Vector3.Zero)
            {
                message = "Right arm 'Wrist To Palm Axis' needs to be set in VRIK. " +
                    "Please select the hand bone, set it to the axis that points from the wrist towards the palm. " +
                    "If the arrow points away from the palm, axis must be negative.";
                return false;
            }

            if (_leftArm._palmToThumbAxis == Vector3.Zero)
            {
                message = "Left arm 'Palm To Thumb Axis' needs to be set in VRIK. " +
                    "Please select the hand bone, set it to the axis that points from the palm towards the thumb. " +
                    "If the arrow points away from the thumb, axis must be negative.";
                return false;
            }

            if (_rightArm._palmToThumbAxis == Vector3.Zero)
            {
                message = "Right arm 'Palm To Thumb Axis' needs to be set in VRIK. " +
                    "Please select the hand bone, set it to the axis that points from the palm towards the thumb. " +
                    "If the arrow points away from the thumb, axis must be negative.";
                return false;
            }

            return true;
        }

        private Transform[] _solverTransforms = [];
        private bool _hasChest, _hasNeck, _hasShoulders, _hasToes, _hasLegs, _hasArms;
        private Vector3[] _readPositions = [];
        private Quaternion[] _readRotations = [];
        private Vector3[] _solvedPositions = new Vector3[22];
        private Quaternion[] _solvedRotations = new Quaternion[22];
        //private Vector3 defaultPelvisLocalPosition;
        private Quaternion[] _defaultLocalRotations = new Quaternion[21];
        private Vector3[] _defaultLocalPositions = new Vector3[21];

        private Vector3 _rootV;
        private Vector3 _rootVelocity;
        private Vector3 _bodyOffset;
        private int _supportLegIndex;
        private int _lastLOD;

        private static Vector3 GetNormal(Transform[] transforms)
        {
            Vector3 normal = Vector3.Zero;
            Vector3 centroid = Vector3.Zero;
            for (int i = 0; i < transforms.Length; i++)
                centroid += transforms[i].WorldTranslation;
            centroid /= transforms.Length;

            for (int i = 0; i < transforms.Length - 1; i++)
                normal += Vector3.Cross(transforms[i].WorldTranslation - centroid, transforms[i + 1].WorldTranslation - centroid).Normalized();
            
            return normal;
        }

        private static FloatKeyframe[] GetSineKeyframes(float mag)
        {
            FloatKeyframe[] keys = new FloatKeyframe[3];

            keys[0].Second = 0f;
            keys[0].InValue = 0f;
            keys[0].OutValue = 0f;

            keys[1].Second = 0.5f;
            keys[1].InValue = mag;
            keys[1].OutValue = mag;

            keys[2].Second = 1f;
            keys[2].InValue = 0f;
            keys[2].OutValue = 0f;

            return keys;
        }

        private void UpdateSolverTransforms()
        {
            for (int i = 0; i < _solverTransforms.Length; i++)
            {
                if (_solverTransforms[i] != null)
                {
                    _readPositions[i] = _solverTransforms[i].WorldTranslation;
                    _readRotations[i] = _solverTransforms[i].WorldRotation;
                }
            }
        }

        protected override void OnInitialize()
        {
            UpdateSolverTransforms();
            Read(_readPositions, _readRotations, _hasChest, _hasNeck, _hasShoulders, _hasToes, _hasLegs, _hasArms);
        }

        protected override void OnUpdate()
        {
            if (IKPositionWeight > 0f)
            {
                if (_lod < 2)
                {
                    bool read = false;

                    if (_lastLOD != _lod)
                    {
                        if (_lastLOD == 2)
                        {
                            _spine._faceDirection = RootBone._readRotation.Rotate(Globals.Forward);

                            if (_hasLegs)
                            {
                                // Teleport to the current position/rotation if resuming from culled LOD with locomotion enabled
                                if (_locomotion._weight > 0f)
                                {
                                    if (_root is not null)
                                    {
                                        _root.SetWorldTranslation(new Vector3(_spine._headTarget.WorldTranslation.X, _root.WorldTranslation.Y, _spine._headTarget.WorldTranslation.Z));
                                        Vector3 forward = _spine._faceDirection;
                                        forward.Y = 0f;
                                        _root.SetWorldRotation(XRMath.LookRotation(forward, _root.WorldUp));
                                    }

                                    UpdateSolverTransforms();
                                    Read(_readPositions, _readRotations, _hasChest, _hasNeck, _hasShoulders, _hasToes, _hasLegs, _hasArms);
                                    read = true;

                                    _locomotion.Reset(_readPositions, _readRotations);
                                }

                                _raycastOriginPelvis = _spine.Hips._readPosition;
                            }
                        }
                    }

                    if (!read)
                    {
                        UpdateSolverTransforms();
                        Read(_readPositions, _readRotations, _hasChest, _hasNeck, _hasShoulders, _hasToes, _hasLegs, _hasArms);
                    }

                    Solve();
                    Write();

                    WriteTransforms();
                }
                else
                {
                    // Culled
                    if (_locomotion._weight > 0f)
                    {
                        _root.SetWorldTranslation(new Vector3(_spine._headTarget.WorldTranslation.X, _root.WorldTranslation.Y, _spine._headTarget.WorldTranslation.Z));
                        Vector3 forward = (_spine._headTarget.WorldRotation * _spine.AnchorRelativeToHead).Rotate(Globals.Forward);
                        forward.Y = 0f;
                        _root.SetWorldRotation(XRMath.LookRotation(forward, _root.WorldUp));
                    }
                }
            }

            _lastLOD = _lod;
        }

        private void WriteTransforms()
        {
            for (int i = 0; i < _solverTransforms.Length; i++)
            {
                if (_solverTransforms[i] != null)
                {
                    bool isRootOrPelvis = i < 2;
                    bool isArmStretchable = i == 8 || i == 9 || i == 12 || i == 13;
                    bool isLegStretchable = (i >= 15 && i <= 17) || (i >= 19 && i <= 21);

                    if (_lod > 0)
                    {
                        isArmStretchable = false;
                        isLegStretchable = false;
                    }

                    if (isRootOrPelvis)
                    {
                        _solverTransforms[i].SetWorldTranslation(Interp.Lerp(_solverTransforms[i].WorldTranslation, GetPosition(i), IKPositionWeight));
                    }

                    if (isArmStretchable || isLegStretchable)
                    {
                        if (IKPositionWeight < 1f)
                        {
                            Vector3 localPosition = _solverTransforms[i].Translation;
                            _solverTransforms[i].SetWorldTranslation(XRMath.Lerp(_solverTransforms[i].WorldTranslation, GetPosition(i), IKPositionWeight));
                            _solverTransforms[i].Translation = XRMath.ProjectVector(_solverTransforms[i].Translation, localPosition);
                        }
                        else
                        {
                            _solverTransforms[i].SetWorldTranslation(XRMath.Lerp(_solverTransforms[i].WorldTranslation, GetPosition(i), IKPositionWeight));
                        }
                    }

                    _solverTransforms[i].SetWorldRotation(XRMath.Lerp(_solverTransforms[i].WorldRotation, GetRotation(i), IKPositionWeight));
                }
            }
        }

        private void Read(
            Vector3[] positions,
            Quaternion[] rotations,
            bool hasChest,
            bool hasNeck,
            bool hasShoulders,
            bool hasToes,
            bool hasLegs,
            bool hasArms)
        {
            if (RootBone is null)
                RootBone = new VirtualBone(positions[0], rotations[0]);
            else
                RootBone.Read(positions[0], rotations[0]);
            
            _spine.Read(positions, rotations, hasChest, hasNeck, hasShoulders, hasToes, hasLegs, 0, 1);

            if (hasArms)
            {
                _leftArm.Read(positions, rotations, hasChest, hasNeck, hasShoulders, hasToes, hasLegs, hasChest ? 3 : 2, 6);
                _rightArm.Read(positions, rotations, hasChest, hasNeck, hasShoulders, hasToes, hasLegs, hasChest ? 3 : 2, 10);
            }

            if (hasLegs)
            {
                _leftLeg.Read(positions, rotations, hasChest, hasNeck, hasShoulders, hasToes, hasLegs, 1, 14);
                _rightLeg.Read(positions, rotations, hasChest, hasNeck, hasShoulders, hasToes, hasLegs, 1, 18);
            }

            for (int i = 0; i < rotations.Length; i++)
            {
                _solvedPositions[i] = positions[i];
                _solvedRotations[i] = rotations[i];
            }

            if (!Initialized)
            {
                if (hasLegs)
                    _legs = [_leftLeg, _rightLeg];
                if (hasArms)
                    _arms = [_leftArm, _rightArm];

                if (hasLegs)
                    _locomotion.Initialize(Animator, positions, rotations, hasToes, _scale);

                _raycastOriginPelvis = _spine.Hips._readPosition;
                _spine._faceDirection = _readRotations[0].Rotate(Globals.Forward);
            }
        }

        private void Solve()
        {
            if (_scale <= 0f)
            {
                Debug.LogWarning("VRIK solver scale <= 0, can not solve!");
                return;
            }

            if (_hasLegs && _lastLocomotionWeight <= 0f && _locomotion._weight > 0f)
                _locomotion.Reset(_readPositions, _readRotations);

            _spine.LOD = _lod;

            if (_hasArms)
                foreach (Arm arm in _arms)
                    arm.LOD = _lod;

            if (_hasLegs)
                foreach (Leg leg in _legs)
                    leg.LOD = _lod;

            // Pre-Solving
            _spine.PreSolve(_scale);

            if (_hasArms)
                foreach (Arm arm in _arms)
                    arm.PreSolve(_scale);
            if (_hasLegs)
                foreach (Leg leg in _legs)
                    leg.PreSolve(_scale);

            // Applying spine and arm offsets
            if (_hasArms)
                foreach (Arm arm in _arms)
                    arm.ApplyOffsets(_scale);

            _spine.ApplyOffsets(_scale);

            // Spine
            _spine.Solve(Animator, RootBone, _legs, _arms, _scale);

            if (_hasLegs && _spine._pelvisPositionWeight > 0f && _plantFeet)
                Debug.LogWarning("If VRIK 'Pelvis Position Weight' is > 0, 'Plant Feet' should be disabled to improve performance and stability.");
            
            float deltaTime = Engine.Delta;

            // Locomotion
            if (_hasLegs)
            {
                if (_locomotion._weight > 0f)
                {
                    //switch (locomotion.mode)
                    //{
                    //    case Locomotion.Mode.Procedural:
                    //        Vector3 leftFootPosition = Vector3.Zero;
                    //        Vector3 rightFootPosition = Vector3.zero;
                    //        Quaternion leftFootRotation = Quaternion.identity;
                    //        Quaternion rightFootRotation = Quaternion.identity;
                    //        float leftFootOffset = 0f;
                    //        float rightFootOffset = 0f;
                    //        float leftHeelOffset = 0f;
                    //        float rightHeelOffset = 0f;

                    //        locomotion.Solve_Procedural(rootBone, spine, leftLeg, rightLeg, leftArm, rightArm, supportLegIndex, out leftFootPosition, out rightFootPosition, out leftFootRotation, out rightFootRotation, out leftFootOffset, out rightFootOffset, out leftHeelOffset, out rightHeelOffset, scale, deltaTime);

                    //        leftFootPosition += root.up * leftFootOffset;
                    //        rightFootPosition += root.up * rightFootOffset;

                    //        leftLeg.footPositionOffset += (leftFootPosition - leftLeg.lastBone.solverPosition) * IKPositionWeight * (1f - leftLeg.positionWeight) * locomotion.weight;
                    //        rightLeg.footPositionOffset += (rightFootPosition - rightLeg.lastBone.solverPosition) * IKPositionWeight * (1f - rightLeg.positionWeight) * locomotion.weight;

                    //        leftLeg.heelPositionOffset += root.up * leftHeelOffset * locomotion.weight;
                    //        rightLeg.heelPositionOffset += root.up * rightHeelOffset * locomotion.weight;

                    //        Quaternion rotationOffsetLeft = QuaTools.FromToRotation(leftLeg.lastBone.solverRotation, leftFootRotation);
                    //        Quaternion rotationOffsetRight = QuaTools.FromToRotation(rightLeg.lastBone.solverRotation, rightFootRotation);

                    //        rotationOffsetLeft = Quaternion.Lerp(Quaternion.identity, rotationOffsetLeft, IKPositionWeight * (1f - leftLeg.rotationWeight) * locomotion.weight);
                    //        rotationOffsetRight = Quaternion.Lerp(Quaternion.identity, rotationOffsetRight, IKPositionWeight * (1f - rightLeg.rotationWeight) * locomotion.weight);

                    //        leftLeg.footRotationOffset = rotationOffsetLeft * leftLeg.footRotationOffset;
                    //        rightLeg.footRotationOffset = rotationOffsetRight * rightLeg.footRotationOffset;

                    //        Vector3 footPositionC = Vector3.Lerp(leftLeg.position + leftLeg.footPositionOffset, rightLeg.position + rightLeg.footPositionOffset, 0.5f);
                    //        footPositionC = XRMath.ProjectPointToPlane(footPositionC, rootBone.solverPosition, root.up);

                    //        Vector3 p = rootBone.solverPosition + rootVelocity * deltaTime * 2f * locomotion.weight;
                    //        p = Vector3.Lerp(p, footPositionC, deltaTime * locomotion.rootSpeed * locomotion.weight);
                    //        rootBone.solverPosition = p;

                    //        rootVelocity += (footPositionC - rootBone.solverPosition) * deltaTime * 10f;
                    //        Vector3 rootVelocityV = XRMath.ExtractVertical(rootVelocity, root.up, 1f);
                    //        rootVelocity -= rootVelocityV;

                    //        float bodyYOffset = MathF.Min(leftFootOffset + rightFootOffset, locomotion.maxBodyYOffset * scale);
                    //        bodyOffset = Vector3.Lerp(bodyOffset, root.up * bodyYOffset, deltaTime * 3f);
                    //        bodyOffset = Vector3.Lerp(Vector3.Zero, bodyOffset, locomotion.weight);

                    //        break;
                    //    case Locomotion.Mode.Animated:
                    if (_lastLocomotionWeight <= 0f)
                        _locomotion.Reset_Animated(_readPositions);
                    _locomotion.Solve_Animated(this, _scale, deltaTime);
                    //        break;
                    //}
                }
                else
                {
                    if (_lastLocomotionWeight > 0f)
                        _locomotion.Reset_Animated(_readPositions);
                }
            }

            _lastLocomotionWeight = _locomotion._weight;

            // Legs
            if (_hasLegs)
            {
                foreach (Leg leg in _legs)
                    leg.ApplyOffsets(_scale);
                
                if (!_plantFeet || _lod > 0)
                {
                    _spine.InverseTranslateToHead(_legs, false, false, _bodyOffset, 1f);

                    foreach (Leg leg in _legs)
                        leg.TranslateRoot(_spine.Hips._solverPosition, _spine.Hips._solverRotation);
                    foreach (Leg leg in _legs)
                        leg.Solve(true);
                }
                else
                {
                    for (int i = 0; i < 2; i++)
                    {
                        _spine.InverseTranslateToHead(_legs, true, true, _bodyOffset, 1f);

                        foreach (Leg leg in _legs)
                            leg.TranslateRoot(_spine.Hips._solverPosition, _spine.Hips._solverRotation);
                        foreach (Leg leg in _legs)
                            leg.Solve(i == 0);
                    }
                }
            }
            else
            {
                _spine.InverseTranslateToHead(_legs, false, false, _bodyOffset, 1f);
            }

            // Arms
            if (_hasArms)
            {
                for (int i = 0; i < _arms.Length; i++)
                    _arms[i].TranslateRoot(_spine.Chest._solverPosition, _spine.Chest._solverRotation);
                
                for (int i = 0; i < _arms.Length; i++)
                    _arms[i].Solve(i == 0);
            }

            // Reset offsets
            _spine.ResetOffsets();

            if (_hasLegs)
                foreach (Leg leg in _legs)
                    leg.ResetOffsets();

            if (_hasArms)
                foreach (Arm arm in _arms)
                    arm.ResetOffsets();

            if (_hasLegs)
            {
                _spine._pelvisPositionOffset += GetPelvisOffset(deltaTime);
                _spine._chestPositionOffset += _spine._pelvisPositionOffset;
                //spine.headPositionOffset += spine.pelvisPositionOffset;
            }

            Write();

            // Find the support leg
            if (_hasLegs)
            {
                _supportLegIndex = -1;
                float shortestMag = float.PositiveInfinity;
                for (int i = 0; i < _legs.Length; i++)
                {
                    float mag = (_legs[i].LastBone._solverPosition - _legs[i]._bones[0]._solverPosition).LengthSquared();
                    if (mag < shortestMag)
                    {
                        _supportLegIndex = i;
                        shortestMag = mag;
                    }
                }
            }
        }

        private float _lastLocomotionWeight;

        private Vector3 GetPosition(int index)
            => _solvedPositions[index];
        private Quaternion GetRotation(int index)
            => _solvedRotations[index];

        /// <summary>
        /// LOD 0: Full quality solving. 
        /// LOD 1: Shoulder solving, stretching plant feet disabled, spine solving quality reduced. 
        /// This provides about 30% of performance gain. 
        /// LOD 2: Culled, but updating root position and rotation if locomotion is enabled.
        /// </summary>
        [Range(0, 2)]
        public int _lod = 0;

        /// <summary>
        /// Scale of the character. Value of 1 means normal adult human size.
        /// </summary>
        public float _scale = 1f;

        /// <summary>
        /// If true, will keep the toes planted even if head target is out of reach, so this can cause the camera to exit the head if it is too high for the model to reach. Enabling this increases the cost of the solver as the legs will have to be solved multiple times.
        /// </summary>
        public bool _plantFeet = true;

        /// <summary>
        /// Gets the root bone.
        /// </summary>
        [HideInInspector]
        public VirtualBone? RootBone { get; private set; }

        /// <summary>
        /// The spine solver.
        /// </summary>
        public Spine _spine = new();

        /// <summary>
        /// The left arm solver.
        /// </summary>
        public Arm _leftArm = new();

        /// <summary>
        /// The right arm solver.
        /// </summary>
        public Arm _rightArm = new();

        /// <summary>
        /// The left leg solver.
        /// </summary>
        public Leg _leftLeg = new();

        /// <summary>
        /// The right leg solver.
        /// </summary>
        public Leg _rightLeg = new();

        /// <summary>
        /// Procedural leg shuffling for stationary VR games.
        /// Not designed for roomscale and thumbstick locomotion.
        /// For those it would be better to use a strafing locomotion blend tree to make the character follow the horizontal direction towards the HMD by root motion or script.
        /// </summary>
        public Locomotion _locomotion = new();

        private Leg[] _legs = new Leg[2];
        private Arm[] _arms = new Arm[2];
        private Vector3 _headPosition;
        private Vector3 _headDeltaPosition;
        private Vector3 _raycastOriginPelvis;
        private Vector3 _lastOffset;
        private Vector3 debugPos1;
        private Vector3 debugPos2;
        private Vector3 debugPos3;
        private Vector3 debugPos4;

        private void Write()
        {
            _solvedPositions[0] = RootBone._solverPosition;
            _solvedRotations[0] = RootBone._solverRotation;
            _spine.Write(ref _solvedPositions, ref _solvedRotations);

            if (_hasLegs)
            {
                foreach (Leg leg in _legs)
                    leg.Write(ref _solvedPositions, ref _solvedRotations);
            }
            if (_hasArms)
            {
                foreach (Arm arm in _arms)
                    arm.Write(ref _solvedPositions, ref _solvedRotations);
            }
        }

        private SortedDictionary<float, List<(XRComponent? item, object? data)>> _raycastResults = [];

        private PhysxScene.PhysxQueryFilter _queryFilter = new PhysxScene.PhysxQueryFilter()
        {
            
        };

        private Vector3 GetPelvisOffset(float deltaTime)
        {
            if (_locomotion._weight <= 0f || _locomotion._blockingLayers == -1)
                return Vector3.Zero;

            var physicsScene = Animator?.World?.PhysicsScene;
            if (physicsScene is null)
                return Vector3.Zero;

            // Origin to pelvis transform position
            Vector3 sampledOrigin = _raycastOriginPelvis;
            sampledOrigin.Y = _spine.Hips._solverPosition.Y;
            Vector3 origin = _spine.Hips._readPosition;
            origin.Y = _spine.Hips._solverPosition.Y;
            Vector3 direction = origin - sampledOrigin;

            //debugPos4 = sampledOrigin;

            _raycastResults.Clear();
            if (_locomotion._raycastRadius <= 0f)
            {
                if (physicsScene.RaycastSingle(
                    new Segment(sampledOrigin, sampledOrigin + new Vector3(direction.Length() * 1.1f)),
                    _locomotion._blockingLayers,
                    _queryFilter,
                    _raycastResults))
                {
                    var rh = (RaycastHit)_raycastResults.First().Value.First().data!;
                    origin = rh.Position;
                }
            }
            else
            {
                IPhysicsGeometry geometry = new IPhysicsGeometry.Sphere(_locomotion._raycastRadius * 1.1f);
                (Vector3 position, Quaternion rotation) pose = (sampledOrigin, Quaternion.Identity);
                if (physicsScene.SweepSingle(
                    geometry,
                    pose,
                    direction.Normalized(),
                    direction.Length(),
                    _locomotion._blockingLayers,
                    _queryFilter,
                    _raycastResults))
                {
                    var sh = (SweepHit)_raycastResults.First().Value.First().data!;
                    origin = sampledOrigin + direction.Normalized() * sh.Distance / 1.1f;
                }
            }

            Vector3 position = _spine.Hips._solverPosition;
            direction = position - origin;

            //debugPos1 = origin;
            //debugPos2 = position;

            if (_locomotion._raycastRadius <= 0f)
            {
                if (physicsScene.RaycastSingle(
                    new Segment(origin, origin + new Vector3(direction.Length())),
                    _locomotion._blockingLayers,
                    _queryFilter,
                    _raycastResults))
                {
                    var rh = (RaycastHit)_raycastResults.First().Value.First().data!;
                    position = rh.Position;
                }
            }
            else
            {
                IPhysicsGeometry geometry = new IPhysicsGeometry.Sphere(_locomotion._raycastRadius);
                (Vector3 position, Quaternion rotation) pose = (origin, Quaternion.Identity);
                if (physicsScene.SweepSingle(
                    geometry,
                    pose,
                    direction.Normalized(),
                    direction.Length(),
                    _locomotion._blockingLayers,
                    _queryFilter,
                    _raycastResults))
                {
                    var sh = (SweepHit)_raycastResults.First().Value.First().data!;
                    position = origin + direction.Normalized() * sh.Distance;
                }
            }

            _lastOffset = Vector3.Lerp(_lastOffset, Vector3.Zero, deltaTime * 3f);
            position += _lastOffset.ClampMagnitude(0.75f);
            position.Y = _spine.Hips._solverPosition.Y;

            //debugPos3 = position;

            _lastOffset = Vector3.Lerp(_lastOffset, position - _spine.Hips._solverPosition, deltaTime * 15f);
            return _lastOffset;
        }
    }
}
