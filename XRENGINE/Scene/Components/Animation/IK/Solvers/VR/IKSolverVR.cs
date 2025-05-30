using Extensions;
using System.Numerics;
using XREngine.Animation;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Rendering.Physics.Physx;
using Transform = XREngine.Scene.Transforms.Transform;

namespace XREngine.Components.Animation
{
    /// <summary>
    /// Hybrid IK solver designed for mapping a character to a VR headset and 2 hand controllers 
    /// </summary>
    [Serializable]
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

            DefaultAnimationCurves();
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

            if (_leftArm.WristToPalmAxis == Vector3.Zero || !onlyIfZero)
                _leftArm.WristToPalmAxis = VRIKCalibrator.GuessWristToPalmAxis(
                    humanoid.Left.Wrist.Node?.GetTransformAs<Transform>(true), 
                    humanoid.Left.Elbow.Node?.GetTransformAs<Transform>(true));
            
            if (_leftArm.PalmToThumbAxis == Vector3.Zero || !onlyIfZero)
                _leftArm.PalmToThumbAxis = VRIKCalibrator.GuessPalmToThumbAxis(
                    humanoid.Left.Wrist.Node?.GetTransformAs<Transform>(true),
                    humanoid.Left.Elbow.Node?.GetTransformAs<Transform>(true));
            
            if (_rightArm.WristToPalmAxis == Vector3.Zero || !onlyIfZero)
                _rightArm.WristToPalmAxis = VRIKCalibrator.GuessWristToPalmAxis(
                    humanoid.Right.Wrist.Node?.GetTransformAs<Transform>(true),
                    humanoid.Right.Elbow.Node?.GetTransformAs<Transform>(true));
            
            if (_rightArm.PalmToThumbAxis == Vector3.Zero || !onlyIfZero)
                _rightArm.PalmToThumbAxis = VRIKCalibrator.GuessPalmToThumbAxis(
                    humanoid.Right.Wrist.Node?.GetTransformAs<Transform>(true),
                    humanoid.Right.Elbow.Node?.GetTransformAs<Transform>(true));
        }

        /// <summary>
        /// Set default values for the animation curves if they have no keys.
        /// </summary>
        public void DefaultAnimationCurves()
        {
            //if (_locomotion._stepHeight == null)
            //    _locomotion._stepHeight = new AnimationCurve();

            //if (_locomotion._heelHeight == null)
            //    _locomotion._heelHeight = new AnimationCurve();

            //if (_locomotion._stepHeight.keys.Length == 0)
            //    _locomotion._stepHeight.keys = GetSineKeyframes(0.03f);

            //if (_locomotion._heelHeight.keys.Length == 0)
            //    _locomotion._heelHeight.keys = GetSineKeyframes(0.03f);
        }

        /// <summary>
        /// Adds position offset to a body part. Position offsets add to the targets in VRIK.
        /// </summary>
        public void AddPositionOffset(EPositionOffset positionOffset, Vector3 value)
        {
            switch (positionOffset)
            {
                case EPositionOffset.Pelvis:
                    _spine.HipsPositionOffset += value;
                    return;
                case EPositionOffset.Chest:
                    _spine.ChestPositionOffset += value;
                    return;
                case EPositionOffset.Head:
                    _spine.HeadPositionOffset += value;
                    return;
                case EPositionOffset.LeftHand:
                    _leftArm.HandPositionOffset += value;
                    return;
                case EPositionOffset.RightHand:
                    _rightArm.HandPositionOffset += value;
                    return;
                case EPositionOffset.LeftFoot:
                    _leftLeg.FootPositionOffset += value;
                    return;
                case EPositionOffset.RightFoot:
                    _rightLeg.FootPositionOffset += value;
                    return;
                case EPositionOffset.LeftHeel:
                    _leftLeg.HeelPositionOffset += value;
                    return;
                case EPositionOffset.RightHeel:
                    _rightLeg.HeelPositionOffset += value;
                    return;
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
                    _spine.HipsRotationOffset = value * _spine.HipsRotationOffset;
                    return;
                case ERotationOffset.Chest:
                    _spine.ChestRotationOffset = value * _spine.ChestRotationOffset;
                    return;
                case ERotationOffset.Head:
                    _spine.HeadRotationOffset = value * _spine.HeadRotationOffset;
                    return;
            }
        }

        ///// <summary>
        ///// Call this in each Update if your avatar is standing on a moving platform
        ///// </summary>
        //public void AddPlatformMotion(Vector3 deltaPosition, Quaternion deltaRotation, Vector3 platformPivot)
        //{
        //    _locomotion.AddDeltaPosition(deltaPosition);
        //    _raycastOriginPelvis += deltaPosition;

        //    _locomotion.AddDeltaRotation(deltaRotation, platformPivot);
        //    _spine._faceDirection = deltaRotation.Rotate(_spine._faceDirection);
        //}

        /// <summary>
        /// Resets all tweens, blendings and lerps. Call this after you have teleported the character.
        /// </summary>
        public void Reset()
        {
            if (!Initialized)
                return;

            UpdateSolverTransforms();
            Read(_readPositions, _readRotations, _hasChest, _hasNeck, _hasShoulders, _hasToes, _hasLegs, _hasArms);

            _spine.FaceDirection = RootBone?.ReadRotation.Rotate(Globals.Forward) ?? Globals.Forward;

            //if (_hasLegs)
            //{
            //    _locomotion.Reset(_readPositions, _readRotations);
            //    _raycastOriginPelvis = _spine.Hips.ReadPosition;
            //}
        }

        public override void StoreDefaultLocalState()
        {
            for (int i = 1; i < _solverTransforms.Length; i++)
            {
                var tfm = _solverTransforms[i];
                if (tfm is null)
                    continue;
                
                _defaultLocalPoses[i - 1] = new PoseData
                {
                    Position = tfm.Translation,
                    Rotation = tfm.Rotation
                };
            }
        }

        public override void ResetTransformToDefault()
        {
            if (!Initialized || _quality >= EQuality.Culled)
                return;

            for (int i = 1; i < _solverTransforms.Length; i++)
            {
                if (_solverTransforms[i] is null)
                    continue;
                
                bool isPelvis = i == 1;

                bool isArmStretchable = i == 8 || i == 9 || i == 12 || i == 13;
                bool isLegStretchable = (i >= 15 && i <= 17) || (i >= 19 && i <= 21);

                if (isPelvis || isArmStretchable || isLegStretchable)
                    _solverTransforms[i].Translation = _defaultLocalPoses[i - 1].Position;

                _solverTransforms[i].Rotation = _defaultLocalPoses[i - 1].Rotation;
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

            if (_leftArm.WristToPalmAxis == Vector3.Zero)
            {
                message = "Left arm 'Wrist To Palm Axis' needs to be set in VRIK. " +
                    "Please select the hand bone, set it to the axis that points from the wrist towards the palm. " +
                    "If the arrow points away from the palm, axis must be negative.";
                return false;
            }

            if (_rightArm.WristToPalmAxis == Vector3.Zero)
            {
                message = "Right arm 'Wrist To Palm Axis' needs to be set in VRIK. " +
                    "Please select the hand bone, set it to the axis that points from the wrist towards the palm. " +
                    "If the arrow points away from the palm, axis must be negative.";
                return false;
            }

            if (_leftArm.PalmToThumbAxis == Vector3.Zero)
            {
                message = "Left arm 'Palm To Thumb Axis' needs to be set in VRIK. " +
                    "Please select the hand bone, set it to the axis that points from the palm towards the thumb. " +
                    "If the arrow points away from the thumb, axis must be negative.";
                return false;
            }

            if (_rightArm.PalmToThumbAxis == Vector3.Zero)
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
        private PoseData[] _defaultLocalPoses = new PoseData[21];
        private struct PoseData
        {
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private Vector3 _rootV;
        private Vector3 _rootVelocity;
        private Vector3 _bodyOffset;
        private int _supportLegIndex;
        private EQuality _lastQuality;

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
                var tfm = _solverTransforms[i];
                if (tfm is null)
                    continue;
                
                tfm.RecalculateMatrices(true);
                _readPositions[i] = tfm.WorldTranslation;
                _readRotations[i] = tfm.WorldRotation;
            }
        }

        protected override void OnInitialize()
        {
            UpdateSolverTransforms();
            Read(
                _readPositions,
                _readRotations,
                _hasChest,
                _hasNeck,
                _hasShoulders,
                _hasToes,
                _hasLegs,
                _hasArms);
        }

        protected override void OnUpdate()
        {
            if (IKPositionWeight > 0.0f)
            {
                //if (_quality < EQuality.Culled)
                    UpdateNormal();
                //else
                //    UpdateCulled();
            }
            _lastQuality = _quality;
        }

        private void UpdateNormal()
        {
            bool read = false;

            if (_lastQuality != _quality)
                QualityChanged(ref read);
            
            if (!read)
            {
                UpdateSolverTransforms();
                Read(_readPositions, _readRotations, _hasChest, _hasNeck, _hasShoulders, _hasToes, _hasLegs, _hasArms);
            }

            Solve();
            ApplyTransforms();
        }

		//private void UpdateCulled()
  //      {
  //          if (_locomotion._weight <= 0.0f || _root is null || _spine._headTarget is null)
		//		return;

		//	Vector3 forward = (_spine._headTarget.WorldRotation * _spine.AnchorRelativeToHead).Rotate(Globals.Forward);
		//	forward.Y = 0f;

		//	Vector3 worldPos = new(_spine._headTarget.WorldTranslation.X, _root.WorldTranslation.Y, _spine._headTarget.WorldTranslation.Z);
  //          Quaternion worldRot = XRMath.LookRotation(forward, _root.WorldUp);

		//	_root.SetWorldTranslation(worldPos);
  //          _root.SetWorldRotation(worldRot);
  //      }

        private void QualityChanged(ref bool read)
        {
			//Only need to read the transforms if the quality has changed off of culled
			if (_lastQuality != EQuality.Culled)
                return;

            _spine.FaceDirection = RootBone?.ReadRotation.Rotate(Globals.Forward) ?? Globals.Forward;

            if (!_hasLegs)
                return;
            
            //Teleport to the current position/rotation if resuming from culled LOD with locomotion enabled
            //if (_locomotion._weight > 0.0f)
            //{
            //    if (_root is not null)
            //    {
            //        _root.SetWorldTranslation(new Vector3(_spine._headTarget!.WorldTranslation.X, _root.WorldTranslation.Y, _spine._headTarget.WorldTranslation.Z));
            //        Vector3 forward = _spine._faceDirection;
            //        forward.Y = 0f;
            //        _root.SetWorldRotation(XRMath.LookRotation(forward, _root.WorldUp));
            //    }

            //    UpdateSolverTransforms();
            //    Read(
            //        _readPositions,
            //        _readRotations,
            //        _hasChest,
            //        _hasNeck,
            //        _hasShoulders,
            //        _hasToes,
            //        _hasLegs,
            //        _hasArms);

            //    read = true;

            //    _locomotion.Reset(_readPositions, _readRotations);
            //}

            _raycastOriginPelvis = _spine.Hips.ReadPosition;
        }

        private void ApplyTransforms()
        {
            for (int i = 0; i < _solverTransforms.Length; i++)
            {
                var tfm = _solverTransforms[i];
                if (tfm is null)
                    continue;

                tfm.RecalculateMatrices(true);
                var prevWorldPos = tfm.WorldTranslation;
                var currWorldPos = GetPosition(i);
                var prevWorldRot = tfm.WorldRotation;
                var currWorldRot = GetRotation(i);

                GetTransformDetails(i, out bool isRootOrHips, out bool isArmStretchable, out bool isLegStretchable);

                if (isRootOrHips)
                    tfm.SetWorldTranslation(Interp.Lerp(prevWorldPos, currWorldPos, IKPositionWeight));
                else if (isArmStretchable || isLegStretchable)
                {
                    if (IKPositionWeight < 1.0f)
                    {
                        Vector3 localPosition = tfm.Translation;
                        tfm.SetWorldTranslation(XRMath.Lerp(prevWorldPos, currWorldPos, IKPositionWeight));
                        tfm.Translation = XRMath.ProjectVector(tfm.Translation, localPosition);
                    }
                    else
                        tfm.SetWorldTranslation(XRMath.Lerp(prevWorldPos, currWorldPos, IKPositionWeight));
                }

                tfm.SetWorldRotation(XRMath.Lerp(prevWorldRot, currWorldRot, IKPositionWeight));
            }
        }

        private void GetTransformDetails(int i, out bool isRootOrHips, out bool isArmStretchable, out bool isLegStretchable)
        {
            isRootOrHips = i < 2;
            isArmStretchable = i == 8 || i == 9 || i == 12 || i == 13;
            isLegStretchable = (i >= 15 && i <= 17) || (i >= 19 && i <= 21);

            if (_quality <= 0)
                return;
            
            isArmStretchable = false;
            isLegStretchable = false;
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

                //if (hasLegs)
                //    _locomotion.Initialize(Animator, positions, rotations, hasToes, _scale);

                _raycastOriginPelvis = _spine.Hips.ReadPosition;
                _spine.FaceDirection = _readRotations[0].Rotate(Globals.Forward);
            }
        }

        private void Solve()
        {
            if (_scale <= 0.0f)
            {
                Debug.LogWarning("VRIK solver scale <= 0, can not solve!");
                return;
            }

            //if (_hasLegs && _lastLocomotionWeight <= 0f && _locomotion._weight > 0f)
            //    _locomotion.Reset(_readPositions, _readRotations);

            float dt = Engine.Delta;

            PreSolve();
            ApplyOffsets();
            SubSolve(dt);
            ResetOffsets(dt);
            Apply();
            DetermineSupportLeg();
        }

        public void Visualize()
            => Visualize(ColorF4.Magenta);
        public void Visualize(ColorF4 color)
        {
            if (_hasLegs)
            {
                _leftLeg.Visualize(color);
                _rightLeg.Visualize(color);
            }
            if (_hasArms)
            {
                _leftArm.Visualize(color);
                _rightArm.Visualize(color);
            }
            _spine.Visualize(color);
        }

        private void DetermineSupportLeg()
        {
            _supportLegIndex = -1;
            if (!_hasLegs)
                return;
            
            float shortestMag = float.PositiveInfinity;
            for (int i = 0; i < _legs.Length; i++)
            {
                float mag = (_legs[i].LastBone.SolverPosition - _legs[i]._bones[0].SolverPosition).LengthSquared();
                if (mag >= shortestMag)
                    continue;

                _supportLegIndex = i;
                shortestMag = mag;
            }
        }

        private float SubSolve(float dt)
        {
            //_spine.Solve(Animator, RootBone!, _legs, _arms, _scale);

            if (_hasLegs && _spine.HipsPositionWeight > 0f && _plantFeet)
                Debug.LogWarning("If VRIK 'Pelvis Position Weight' is > 0, 'Plant Feet' should be disabled to improve performance and stability.");

            //if (_hasLegs)
            //    AnimateLocomotion(dt);

            //_lastLocomotionWeight = _locomotion._weight;

            //if (_hasLegs)
            //    SolveLegs();
            //else
            //    _spine.InverseTranslateToHead(_legs, false, false, _bodyOffset, 1.0f);

            //if (_hasArms)
            //    SolveArms();

            return dt;
        }

        private void ResetOffsets(float dt)
        {
            _spine.ResetOffsets();

            if (_hasLegs)
                foreach (LegSolver leg in _legs)
                    leg.ResetOffsets();

            if (_hasArms)
                foreach (ArmSolver arm in _arms)
                    arm.ResetOffsets();

            if (_hasLegs)
            {
                _spine.HipsPositionOffset += GetPelvisOffset(dt);
                _spine.ChestPositionOffset += _spine.HipsPositionOffset;
                _spine.HeadPositionOffset += _spine.HipsPositionOffset;
            }
        }

        private void ApplyOffsets()
        {
            if (_hasArms)
                foreach (ArmSolver arm in _arms)
                    arm.ApplyOffsets(_scale);

            _spine.ApplyOffsets(_scale);
        }

        private void PreSolve()
        {
            _spine.PreSolve(_scale);

            if (_hasArms)
                foreach (ArmSolver arm in _arms)
                    arm.PreSolve(_scale);
            if (_hasLegs)
                foreach (LegSolver leg in _legs)
                    leg.PreSolve(_scale);
        }

        //private void AnimateLocomotion(float deltaTime)
        //{
        //    if (_locomotion._weight > 0f)
        //    {
        //        switch (_locomotion._mode)
        //        {
        //            case Locomotion.Mode.Procedural:
        //                //Vector3 leftFootPosition = Vector3.Zero;
        //                //Vector3 rightFootPosition = Vector3.Zero;
        //                //Quaternion leftFootRotation = Quaternion.Identity;
        //                //Quaternion rightFootRotation = Quaternion.Identity;
        //                //float leftFootOffset = 0f;
        //                //float rightFootOffset = 0f;
        //                //float leftHeelOffset = 0f;
        //                //float rightHeelOffset = 0f;

        //                //_locomotion.Solve_Procedural(RootBone, _spine, _leftLeg, _rightLeg, _leftArm, _rightArm, _supportLegIndex, out leftFootPosition, out rightFootPosition, out leftFootRotation, out rightFootRotation, out leftFootOffset, out rightFootOffset, out leftHeelOffset, out rightHeelOffset, _scale, deltaTime);

        //                //leftFootPosition += _root.WorldUp * leftFootOffset;
        //                //rightFootPosition += _root.WorldUp * rightFootOffset;

        //                //_leftLeg._footPositionOffset += (leftFootPosition - _leftLeg.LastBone._solverPosition) * IKPositionWeight * (1f - _leftLeg._positionWeight) * _locomotion._weight;
        //                //_rightLeg._footPositionOffset += (rightFootPosition - _rightLeg.LastBone._solverPosition) * IKPositionWeight * (1f - _rightLeg._positionWeight) * _locomotion._weight;

        //                //_leftLeg._heelPositionOffset += _root.WorldUp * leftHeelOffset * _locomotion._weight;
        //                //_rightLeg._heelPositionOffset += _root.WorldUp * rightHeelOffset * _locomotion._weight;

        //                //Quaternion rotationOffsetLeft = XRMath.FromToRotation(_leftLeg.LastBone._solverRotation, leftFootRotation);
        //                //Quaternion rotationOffsetRight = XRMath.FromToRotation(_rightLeg.LastBone._solverRotation, rightFootRotation);

        //                //rotationOffsetLeft = Quaternion.Lerp(Quaternion.Identity, rotationOffsetLeft, IKPositionWeight * (1f - _leftLeg._rotationWeight) * _locomotion._weight);
        //                //rotationOffsetRight = Quaternion.Lerp(Quaternion.Identity, rotationOffsetRight, IKPositionWeight * (1f - _rightLeg._rotationWeight) * _locomotion._weight);

        //                //_leftLeg._footRotationOffset = rotationOffsetLeft * _leftLeg._footRotationOffset;
        //                //_rightLeg._footRotationOffset = rotationOffsetRight * _rightLeg._footRotationOffset;

        //                //Vector3 footPositionC = Vector3.Lerp(_leftLeg.Position + _leftLeg._footPositionOffset, _rightLeg.Position + _rightLeg._footPositionOffset, 0.5f);
        //                //footPositionC = XRMath.ProjectPointToPlane(footPositionC, RootBone._solverPosition, _root.WorldUp);

        //                //Vector3 p = RootBone._solverPosition + _rootVelocity * deltaTime * 2f * _locomotion._weight;
        //                //p = Vector3.Lerp(p, footPositionC, deltaTime * _locomotion._rootSpeed * _locomotion._weight);
        //                //RootBone._solverPosition = p;

        //                //_rootVelocity += (footPositionC - RootBone._solverPosition) * deltaTime * 10f;
        //                //Vector3 rootVelocityV = XRMath.ExtractVertical(_rootVelocity, _root.WorldUp, 1f);
        //                //_rootVelocity -= rootVelocityV;

        //                //float bodyYOffset = MathF.Min(leftFootOffset + rightFootOffset, _locomotion._maxBodyYOffset * _scale);
        //                //_bodyOffset = Vector3.Lerp(_bodyOffset, _root.up * bodyYOffset, deltaTime * 3f);
        //                //_bodyOffset = Vector3.Lerp(Vector3.Zero, _bodyOffset, _locomotion._weight);

        //                break;
        //            case Locomotion.Mode.Animated:
        //                if (_lastLocomotionWeight <= 0f)
        //                    _locomotion.Reset_Animated(_readPositions);
        //                _locomotion.Solve_Animated(this, _scale, deltaTime);
        //                break;
        //        }
        //    }
        //    else
        //    {
        //        if (_lastLocomotionWeight > 0f)
        //            _locomotion.Reset_Animated(_readPositions);
        //    }
        //}

        private void SolveArms()
        {
            for (int i = 0; i < _arms.Length; i++)
                _arms[i].TranslateRoot(_spine.Chest.SolverPosition, _spine.Chest.SolverRotation);

            for (int i = 0; i < _arms.Length; i++)
                _arms[i].Solve(i == 0);
        }

        private void SolveLegs()
        {
            foreach (LegSolver leg in _legs)
                leg.ApplyOffsets(_scale);

            if (!_plantFeet || _quality > 0)
            {
                //_spine.InverseTranslateToHead(_legs, false, false, _bodyOffset, 1f);

                foreach (LegSolver leg in _legs)
                    leg.TranslateRoot(_spine.Hips.SolverPosition, _spine.Hips.SolverRotation);
                foreach (LegSolver leg in _legs)
                    leg.Solve(true);
            }
            else
            {
                for (int i = 0; i < 2; i++)
                {
                    //_spine.InverseTranslateToHead(_legs, true, true, _bodyOffset, 1f);

                    foreach (LegSolver leg in _legs)
                        leg.TranslateRoot(_spine.Hips.SolverPosition, _spine.Hips.SolverRotation);
                    foreach (LegSolver leg in _legs)
                        leg.Solve(i == 0);
                }
            }
        }

        private float _lastLocomotionWeight = 0.0f;

        private Vector3 GetPosition(int index)
            => _solvedPositions[index];
        private Quaternion GetRotation(int index)
            => _solvedRotations[index];

        public EQuality _quality = EQuality.Full;
        public EQuality Quality
        {
            get => _quality;
            set => SetField(ref _quality, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Quality):
                    _spine.Quality = _quality;
                    if (_hasArms)
                        foreach (ArmSolver arm in _arms)
                            arm.Quality = _quality;
                    if (_hasLegs)
                        foreach (LegSolver leg in _legs)
                            leg.Quality = _quality;
                    break;
            }
        }

        public enum EQuality
        {
			/// <summary>
			/// Full quality solving.
			/// </summary>
			Full,
			/// <summary>
			/// Shoulder solving, stretching plant feet disabled, spine solving quality reduced.
			/// </summary>
			Semi,
			/// <summary>
			/// Culled, but updating root position and rotation if locomotion is enabled.
			/// </summary>
			Culled
		}

        private float _scale = 1.0f;
		/// <summary>
		/// Scale of the character. Value of 1 means normal adult human size.
		/// </summary>
		public float Scale
        {
            get => _scale;
            set => SetField(ref _scale, value);
		}

		private bool _plantFeet = true;
		/// <summary>
		/// If true, will keep the toes planted even if head target is out of reach, so this can cause the camera to exit the head if it is too high for the model to reach. Enabling this increases the cost of the solver as the legs will have to be solved multiple times.
		/// </summary>
		public bool PlantFeet
        {
            get => _plantFeet;
            set => SetField(ref _plantFeet, value);
		}

        private VirtualBone? _rootBone;
		[HideInInspector]
        public VirtualBone? RootBone
        {
            get => _rootBone;
            set => SetField(ref _rootBone, value);
        }

        private SpineSolver _spine = new();
        public SpineSolver Spine
        {
            get => _spine;
            set => SetField(ref _spine, value);
        }

        private ArmSolver _leftArm = new();
        public ArmSolver LeftArm
        {
            get => _leftArm;
            set => SetField(ref _leftArm, value);
        }

        private ArmSolver _rightArm = new();
        public ArmSolver RightArm
        {
            get => _rightArm;
            set => SetField(ref _rightArm, value);
        }

        private LegSolver _leftLeg = new();
        public LegSolver LeftLeg
        {
            get => _leftLeg;
            set => SetField(ref _leftLeg, value);
        }

        private LegSolver _rightLeg = new();
        public LegSolver RightLeg
        {
            get => _rightLeg;
            set => SetField(ref _rightLeg, value);
        }

        /// <summary>
        /// Procedural leg shuffling for stationary VR games.
        /// Not designed for roomscale and thumbstick locomotion.
        /// For those it would be better to use a strafing locomotion blend tree to make the character follow the horizontal direction towards the HMD by root motion or script.
        /// </summary>
        //public Locomotion _locomotion = new();

        private LegSolver[] _legs = new LegSolver[2];
        private ArmSolver[] _arms = new ArmSolver[2];
        private Vector3 _headPosition;
        private Vector3 _headDeltaPosition;
        private Vector3 _raycastOriginPelvis;
        private Vector3 _lastOffset;

        private void Apply()
        {
            _solvedPositions[0] = RootBone?.SolverPosition ?? Vector3.Zero;
            _solvedRotations[0] = RootBone?.SolverRotation ?? Quaternion.Identity;
            _spine.Write(ref _solvedPositions, ref _solvedRotations);

            if (_hasLegs)
            {
                foreach (LegSolver leg in _legs)
                    leg.Write(ref _solvedPositions, ref _solvedRotations);
            }
            if (_hasArms)
            {
                foreach (ArmSolver arm in _arms)
                    arm.Write(ref _solvedPositions, ref _solvedRotations);
            }
        }

        private SortedDictionary<float, List<(XRComponent? item, object? data)>> _raycastResults = [];

        private PhysxScene.PhysxQueryFilter _queryFilter = new()
        {
            
        };

        private Vector3 GetPelvisOffset(float deltaTime)
        {
            //if (_locomotion._weight <= 0f || _locomotion._blockingLayers == -1)
                return Vector3.Zero;

            //var physicsScene = Animator?.World?.PhysicsScene;
            //if (physicsScene is null)
            //    return Vector3.Zero;

            //// Origin to pelvis transform position
            //Vector3 sampledOrigin = _raycastOriginPelvis;
            //sampledOrigin.Y = _spine.Hips.SolverPosition.Y;
            //Vector3 origin = _spine.Hips.ReadPosition;
            //origin.Y = _spine.Hips.SolverPosition.Y;
            //Vector3 direction = origin - sampledOrigin;

            ////debugPos4 = sampledOrigin;

            //_raycastResults.Clear();
            ////if (_locomotion._raycastRadius <= 0f)
            ////{
            ////    if (physicsScene.RaycastSingleAsync(
            ////        new Segment(sampledOrigin, sampledOrigin + new Vector3(direction.Length() * 1.1f)),
            ////        _locomotion._blockingLayers,
            ////        _queryFilter,
            ////        _raycastResults))
            ////    {
            ////        var rh = (RaycastHit)_raycastResults.First().Value.First().data!;
            ////        origin = rh.Position;
            ////    }
            ////}
            ////else
            ////{
            ////    IPhysicsGeometry geometry = new IPhysicsGeometry.Sphere(_locomotion._raycastRadius * 1.1f);
            ////    (Vector3 position, Quaternion rotation) pose = (sampledOrigin, Quaternion.Identity);
            ////    if (physicsScene.SweepSingle(
            ////        geometry,
            ////        pose,
            ////        direction.Normalized(),
            ////        direction.Length(),
            ////        _locomotion._blockingLayers,
            ////        _queryFilter,
            ////        _raycastResults))
            ////    {
            ////        var sh = (SweepHit)_raycastResults.First().Value.First().data!;
            ////        origin = sampledOrigin + direction.Normalized() * sh.Distance / 1.1f;
            ////    }
            ////}

            //Vector3 position = _spine.Hips.SolverPosition;
            //direction = position - origin;

            ////debugPos1 = origin;
            ////debugPos2 = position;

            //if (_locomotion._raycastRadius <= 0f)
            //{
            //    //if (physicsScene.RaycastSingleAsync(
            //    //    new Segment(origin, origin + new Vector3(direction.Length())),
            //    //    _locomotion._blockingLayers,
            //    //    _queryFilter,
            //    //    _raycastResults))
            //    //{
            //    //    var rh = (RaycastHit)_raycastResults.First().Value.First().data!;
            //    //    position = rh.Position;
            //    //}
            //}
            //else
            //{
            //    IPhysicsGeometry geometry = new IPhysicsGeometry.Sphere(_locomotion._raycastRadius);
            //    (Vector3 position, Quaternion rotation) pose = (origin, Quaternion.Identity);
            //    if (physicsScene.SweepSingle(
            //        geometry,
            //        pose,
            //        direction.Normalized(),
            //        direction.Length(),
            //        _locomotion._blockingLayers,
            //        _queryFilter,
            //        _raycastResults))
            //    {
            //        var sh = (SweepHit)_raycastResults.First().Value.First().data!;
            //        position = origin + direction.Normalized() * sh.Distance;
            //    }
            //}

            //_lastOffset = Vector3.Lerp(_lastOffset, Vector3.Zero, deltaTime * 3f);
            //position += _lastOffset.ClampMagnitude(0.75f);
            //position.Y = _spine.Hips.SolverPosition.Y;

            ////debugPos3 = position;

            //_lastOffset = Vector3.Lerp(_lastOffset, position - _spine.Hips.SolverPosition, deltaTime * 15f);
            //return _lastOffset;
        }
    }
}
