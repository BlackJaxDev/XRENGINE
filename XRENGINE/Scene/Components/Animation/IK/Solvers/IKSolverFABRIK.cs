using Extensions;
using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Scene.Components.Animation
{
    /// <summary>
    /// Forward and Backward Reaching Inverse Kinematics solver.
    /// 
    /// This class is based on the "FABRIK: A fast, iterative solver for the inverse kinematics problem." paper by Aristidou, A., Lasenby, J.
    /// </summary>
    [System.Serializable]
    public class IKSolverFABRIK : IKSolverHeuristic
    {
        /// <summary>
        /// Solving stage 1 of the %FABRIK algorithm.
        /// </summary>
        public void SolveForward(Vector3 position)
        {
            if (!Initialized)
            {
                Debug.LogWarning("Trying to solve uninitiated FABRIK chain.");
                return;
            }
            PreSolve();
            ForwardReach(position);
        }

        /// <summary>
        /// Solving stage 2 of the %FABRIK algorithm.
        /// </summary>
        public void SolveBackward(Vector3 position)
        {
            if (!Initialized)
            {
                Debug.LogWarning("Trying to solve uninitiated FABRIK chain.");
                return;
            }
            BackwardReach(position);
            PostSolve();
        }

        /// <summary>
        /// Called before each iteration of the solver.
        /// </summary>
        public IterationDelegate? OnPreIteration;

        private bool[] _limitedBones = [];
        private Vector3[] _solverLocalPositions = [];

        protected override void OnInitialize()
        {
            if (_firstInit)
                RawIKPosition = _bones[^1]._transform?.WorldTranslation ?? Vector3.Zero;

            for (int i = 0; i < _bones.Length; i++)
            {
                var tfm = _bones[i]._transform;
                if (tfm is null)
                    continue;

                _bones[i]._solverPosition = tfm.WorldTranslation;
                _bones[i]._solverRotation = tfm.WorldRotation;
            }

            _limitedBones = new bool[_bones.Length];
            _solverLocalPositions = new Vector3[_bones.Length];

            InitializeBones();

            for (int i = 0; i < _bones.Length; i++)
            {
                var tfm = _bones[i]._transform;
                if (tfm is null)
                    continue;

                _solverLocalPositions[i] = Vector3.Transform(tfm.WorldTranslation - GetParentSolverPosition(i), Quaternion.Inverse(GetParentSolverRotation(i)));
            }
        }

        protected override void OnUpdate()
        {
            if (IKPositionWeight <= 0)
                return;

            IKPositionWeight = IKPositionWeight.Clamp(0f, 1f);

            PreSolve();

            var ikPosWorld = GetWorldIKPosition();

            if (_solve2D)
                ikPosWorld.Z = _bones[0]._transform?.WorldTranslation.Z ?? 0.0f;

            Vector3 singularityOffset = _maxIterations > 1 ? GetSingularityOffset() : Vector3.Zero;

            // Iterating the solver
            for (int i = 0; i < _maxIterations; i++)
            {
                // Optimizations
                if (singularityOffset == Vector3.Zero && i >= 1 && _tolerance > 0 && PositionOffsetSquared < _tolerance * _tolerance)
                    break;

                _lastLocalDirection = LocalDirection;

                OnPreIteration?.Invoke(i);

                Solve(ikPosWorld + (i == 0 ? singularityOffset : Vector3.Zero));
            }

            PostSolve();
        }

        /*
		 * If true, the solver will work with 0 length bones
		 * */
        protected override bool CanLengthBeZero => false;  // Returning false here also ensures that the bone lengths will be calculated

        /*
		 * Interpolates the joint position to match the bone's length
		*/
        private Vector3 SolveJoint(Vector3 pos1, Vector3 pos2, float length)
        {
            if (_solve2D)
                pos1.Z = pos2.Z;

            return pos2 + (pos1 - pos2).Normalized() * length;
        }

        /*
		 * Check if bones have moved from last solved positions
		 * */
        private void PreSolve()
        {
            _chainLength = 0;

            for (int i = 0; i < _bones.Length; i++)
            {
                var b = _bones[i];

                var tfm = b._transform;
                if (tfm is null)
                    continue;

                b._solverPosition = tfm.WorldTranslation;
                b._solverRotation = tfm.WorldRotation;

                if (i < _bones.Length - 1)
                {
                    var nextTfm = _bones[i + 1]._transform;
                    if (nextTfm is null)
                        continue;

                    b._length = (tfm.WorldTranslation - nextTfm.WorldTranslation).LengthSquared();
                    b._axis = Vector3.Transform(nextTfm.WorldTranslation - tfm.WorldTranslation, Quaternion.Inverse(tfm.WorldRotation));
                    _chainLength += _bones[i]._length;
                }

                if (_useRotationLimits)
                    _solverLocalPositions[i] = Vector3.Transform(tfm.WorldTranslation - GetParentSolverPosition(i), Quaternion.Inverse(GetParentSolverRotation(i)));
            }
        }

        private void PostSolve()
        {
            // Rotating bones to match the solver positions
            if (!_useRotationLimits)
                MapToSolverPositions();
            else
                MapToSolverPositionsLimited();

            _lastLocalDirection = LocalDirection;
        }

        private void Solve(Vector3 targetPosition)
        {
            // Forward reaching
            ForwardReach(targetPosition);

            // Backward reaching
            var tfm = _bones[0]._transform;
            if (tfm != null)
                BackwardReach(tfm.WorldTranslation);
        }

        private void ForwardReach(Vector3 position)
        {
            // Lerp last bone's solverPosition to position
            _bones[^1]._solverPosition = Vector3.Lerp(_bones[^1]._solverPosition, position, IKPositionWeight);

            for (int i = 0; i < _limitedBones.Length; i++)
                _limitedBones[i] = false;

            for (int i = _bones.Length - 2; i > -1; i--)
            {
                // Finding joint positions
                _bones[i]._solverPosition = SolveJoint(
                    _bones[i]._solverPosition,
                    _bones[i + 1]._solverPosition,
                    _bones[i]._length);

                // Limiting bone rotation forward
                LimitForward(i, i + 1);
            }

            // Limiting the first bone's rotation
            LimitForward(0, 0);
        }

        private void SolverMove(int index, Vector3 offset)
        {
            for (int i = index; i < _bones.Length; i++)
                _bones[i]._solverPosition += offset;
        }

        private void SolverRotate(int index, Quaternion rotation, bool recursive)
        {
            for (int i = index; i < _bones.Length; i++)
            {
                _bones[i]._solverRotation = rotation * _bones[i]._solverRotation;
                if (!recursive)
                    return;
            }
        }

        private void SolverRotateChildren(int index, Quaternion rotation)
        {
            for (int i = index + 1; i < _bones.Length; i++)
                _bones[i]._solverRotation = rotation * _bones[i]._solverRotation;
        }

        private void SolverMoveChildrenAroundPoint(int index, Quaternion rotation)
        {
            for (int i = index + 1; i < _bones.Length; i++)
            {
                Vector3 dir = _bones[i]._solverPosition - _bones[index]._solverPosition;
                _bones[i]._solverPosition = _bones[index]._solverPosition + Vector3.Transform(dir, rotation);
            }
        }

        private Quaternion GetParentSolverRotation(int index)
        {
            if (index > 0)
                return _bones[index - 1]._solverRotation;

            var tfmParent = _bones[0]._transform?.Parent;
            return tfmParent is null ? Quaternion.Identity : tfmParent.WorldRotation;
        }

        private Vector3 GetParentSolverPosition(int index)
        {
            if (index > 0)
                return _bones[index - 1]._solverPosition;

            var tfmParent = _bones[0]._transform?.Parent;
            return tfmParent is null ? Vector3.Zero : tfmParent.WorldTranslation;
        }

        private Quaternion GetLimitedRotation(int index, Quaternion q, out bool changed)
        {
            changed = false;

            Quaternion parentRotation = GetParentSolverRotation(index);
            Quaternion localRotation = Quaternion.Inverse(parentRotation) * q;

            Quaternion limitedLocalRotation = _bones[index].RotationLimit?.GetLimitedLocalRotation(localRotation, out changed) ?? localRotation;

            return !changed ? q : parentRotation * limitedLocalRotation;
        }

        private void LimitForward(int rotateBone, int limitBone)
        {
            if (!_useRotationLimits)
                return;

            if (_bones[limitBone].RotationLimit is null)
                return;

            // Storing last bone's position before applying the limit
            Vector3 lastBoneBeforeLimit = _bones[^1]._solverPosition;

            // Moving and rotating this bone and all its children to their solver positions
            for (int i = rotateBone; i < _bones.Length - 1; i++)
            {
                if (_limitedBones[i])
                    break;

                var b = _bones[i];
                var nb = _bones[i + 1];

                var from = Vector3.Transform(b._axis, b._solverRotation);
                var to = nb._solverPosition - b._solverPosition;

                Quaternion fromTo = XRMath.RotationBetweenVectors(from, to);

                SolverRotate(i, fromTo, false);
            }

            // Limit the bone's rotation
            Quaternion afterLimit = GetLimitedRotation(limitBone, _bones[limitBone]._solverRotation, out bool changed);

            if (changed)
            {
                // Rotating and positioning the hierarchy so that the last bone's position is maintained
                if (limitBone < _bones.Length - 1)
                {
                    Quaternion change = XRMath.FromToRotation(_bones[limitBone]._solverRotation, afterLimit);
                    _bones[limitBone]._solverRotation = afterLimit;
                    SolverRotateChildren(limitBone, change);
                    SolverMoveChildrenAroundPoint(limitBone, change);

                    // Rotating to compensate for the limit
                    Quaternion fromTo = XRMath.RotationBetweenVectors(
                        _bones[^1]._solverPosition - _bones[rotateBone]._solverPosition,
                        lastBoneBeforeLimit - _bones[rotateBone]._solverPosition);

                    SolverRotate(rotateBone, fromTo, true);
                    SolverMoveChildrenAroundPoint(rotateBone, fromTo);

                    // Moving the bone so that last bone maintains its initial position
                    SolverMove(rotateBone, lastBoneBeforeLimit - _bones[^1]._solverPosition);
                }
                else
                {
                    // last bone
                    _bones[limitBone]._solverRotation = afterLimit;
                }
            }

            _limitedBones[limitBone] = true;
        }

        private void BackwardReach(Vector3 position)
        {
            if (_useRotationLimits)
                BackwardReachLimited(position);
            else
                BackwardReachUnlimited(position);
        }

        private void BackwardReachUnlimited(Vector3 position)
        {
            // Move first bone to position
            _bones[0]._solverPosition = position;

            // Finding joint positions
            for (int i = 1; i < _bones.Length; i++)
                _bones[i]._solverPosition = SolveJoint(
                    _bones[i]._solverPosition,
                    _bones[i - 1]._solverPosition,
                    _bones[i - 1]._length);
        }

        private void BackwardReachLimited(Vector3 position)
        {
            // Move first bone to position
            _bones[0]._solverPosition = position;

            // Applying rotation limits bone by bone
            for (int i = 0; i < _bones.Length - 1; i++)
            {
                // Rotating bone to look at the solved joint position
                Vector3 nextPosition = SolveJoint(_bones[i + 1]._solverPosition, _bones[i]._solverPosition, _bones[i]._length);

                Quaternion swing = XRMath.RotationBetweenVectors(
                    Vector3.Transform(_bones[i]._axis, _bones[i]._solverRotation),
                    nextPosition - _bones[i]._solverPosition);

                Quaternion targetRotation = swing * _bones[i]._solverRotation;

                // Rotation Constraints
                if (_bones[i].RotationLimit != null)
                    targetRotation = GetLimitedRotation(i, targetRotation, out _);
                
                Quaternion fromTo = XRMath.FromToRotation(_bones[i]._solverRotation, targetRotation);
                _bones[i]._solverRotation = targetRotation;
                SolverRotateChildren(i, fromTo);

                // Positioning the next bone to its default local position
                _bones[i + 1]._solverPosition = _bones[i]._solverPosition + _bones[i]._solverRotation.Rotate(_solverLocalPositions[i + 1]);
            }

            // Reconstruct solver rotations to protect from invalid Quaternions
            for (int i = 0; i < _bones.Length; i++)
            {
                _bones[i]._solverRotation = XRMath.LookRotation(
                    _bones[i]._solverRotation.Rotate(Globals.Forward), 
                    _bones[i]._solverRotation.Rotate(Globals.Up));
            }
        }

        /// <summary>
        /// Rotates each bone to look at the solver position of the next bone.
        /// </summary>
        private void MapToSolverPositions()
        {
            _bones[0]._transform?.SetWorldTranslation(_bones[0]._solverPosition);

            for (int i = 0; i < _bones.Length - 1; i++)
                if (_solve2D)
                    _bones[i].Swing2D(_bones[i + 1]._solverPosition);
                else
                    _bones[i].Swing(_bones[i + 1]._solverPosition);
        }

        /// <summary>
        /// Rotates each bone to look at the solver position of the next bone, limited by rotation limits.
        /// </summary>
        private void MapToSolverPositionsLimited()
        {
            _bones[0]._transform?.SetWorldTranslation(_bones[0]._solverPosition);

            for (int i = 0; i < _bones.Length; i++)
                if (i < _bones.Length - 1)
                    _bones[i]._transform?.SetWorldRotation(_bones[i]._solverRotation);
        }
    }
}
