using Extensions;
using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation
{
    /// <summary>
    /// Contains methods common for all heuristic solvers.
    /// </summary>
    [System.Serializable]
    public class IKSolverHeuristic : IKSolver
    {
        /// <summary>
        /// Minimum distance from last reached position. Will stop solving if difference from previous reached position is less than tolerance. If tolerance is zero, will iterate until maxIterations.
        /// </summary>
        public float _tolerance = 0.001f;
        /// <summary>
        /// Max iterations per frame
        /// </summary>
        public int _maxIterations = 4;
        /// <summary>
        /// If true, rotation limits (if existing) will be applied on each iteration.
        /// </summary>
        public bool _useRotationLimits = true;
        /// <summary>
        /// Solve in 2D?
        /// </summary>
        public bool _solve2D = false;
        /// <summary>
        /// The hierarchy of bones.
        /// </summary>
        public IKBone[] _bones = [];

        /// <summary>
        /// Rebuild the bone hierarcy and reinitiate the solver.
        /// </summary>
        /// <returns>
        /// Returns true if the new chain is valid.
        /// </returns>
        public bool SetChain(Transform?[] hierarchy, Transform? root)
        {
            if (_bones == null || _bones.Length != hierarchy.Length)
                _bones = new IKBone[hierarchy.Length];

            for (int i = 0; i < hierarchy.Length; i++)
            {
                if (_bones[i] == null)
                    _bones[i] = new IKBone();

                _bones[i]._transform = hierarchy[i];
            }

            Initialize(root);

            return Initialized;
        }

        /// <summary>
        /// Adds a bone to the chain.
        /// </summary>
        public void AddBone(Transform bone)
        {
            Transform?[] newBones = new Transform?[_bones.Length + 1];

            for (int i = 0; i < _bones.Length; i++)
                newBones[i] = _bones[i]._transform;
            
            newBones[^1] = bone;
            SetChain(newBones, _root);
        }

        public override void StoreDefaultLocalState()
        {
            for (int i = 0; i < _bones.Length; i++)
                _bones[i].StoreDefaultLocalState();
        }

        public override void ResetTransformToDefault()
        {
            if (!Initialized)
                return;

            if (IKPositionWeight <= 0f)
                return;

            for (int i = 0; i < _bones.Length; i++)
                _bones[i].ResetTransformToDefault();
        }

        public override bool IsValid(ref string message)
        {
            if (_bones.Length == 0)
            {
                message = "IK chain has no Bones.";
                return false;
            }
            if (_bones.Length < MinimumBoneCount)
            {
                message = "IK chain has less than " + MinimumBoneCount + " Bones.";
                return false;
            }
            foreach (IKBone bone in _bones)
            {
                if (bone._transform == null)
                {
                    message = "One of the Bones is null.";
                    return false;
                }
            }

            Transform? duplicate = ContainsDuplicateBone(_bones);
            if (duplicate != null)
            {
                message = $"{duplicate.SceneNode?.Name} is represented multiple times in the Bones.";
                return false;
            }

            if (!AllowCommonParent && !HierarchyIsValid(_bones))
            {
                message = "Invalid bone hierarchy detected. IK requires for its bones to be parented to each other in descending order.";
                return false;
            }

            if (!CanLengthBeZero)
            {
                for (int i = 0; i < _bones.Length - 1; i++)
                {
                    float l = (_bones[i]._transform!.WorldTranslation - _bones[i + 1]._transform!.WorldTranslation).Length();
                    if (l == 0)
                    {
                        message = "Bone " + i + " length is zero.";
                        return false;
                    }
                }
            }
            return true;
        }

        public override IKSolver.IKPoint[] GetPoints() => _bones;

        public override IKSolver.IKPoint? GetPoint(Transform transform)
        {
            for (int i = 0; i < _bones.Length; i++)
                if (_bones[i]._transform == transform)
                    return _bones[i] as IKSolver.IKPoint;
            return null;
        }

        protected virtual int MinimumBoneCount => 2;
        protected virtual bool CanLengthBeZero => true;
        protected virtual bool AllowCommonParent => false;

        protected override void OnInitialize() { }
        protected override void OnUpdate() { }

        protected Vector3 _lastLocalDirection;
        protected float _chainLength;

        /*
		 * Initiates all bones to match their current state
		 * */
        protected void InitializeBones()
        {
            _chainLength = 0;

            for (int i = 0; i < _bones.Length; i++)
            {
                // Find out which local axis is directed at child/target position
                if (i < _bones.Length - 1)
                {
                    var parentTfm = _bones[i]._transform;
                    var childTfm = _bones[i + 1]._transform;
                    if (parentTfm is null)
                    {
                        Debug.LogWarning($"Bone {i} is null.");
                        continue;
                    }
                    if (childTfm is null)
                    {
                        Debug.LogWarning($"Bone {i + 1} is null.");
                        continue;
                    }

                    _bones[i]._length = (parentTfm.WorldTranslation - childTfm.WorldTranslation).Length();
                    _chainLength += _bones[i]._length;

                    Vector3 nextPosition = childTfm.WorldTranslation;
                    _bones[i]._axis = Vector3.Transform(nextPosition - parentTfm.WorldTranslation, Quaternion.Inverse(parentTfm.WorldRotation));

                    // Disable Rotation Limits from updating to take control of their execution order
                    var limitComp = _bones[i].RotationLimit;
                    if (limitComp != null)
                    {
                        if (_solve2D)
                        {
                            //if (bones[i].RotationLimit is not RotationLimitHinge)
                            //{
                            //    Debug.LogWarning("Only Hinge Rotation Limits should be used on 2D IK solvers.");
                            //}
                        }
                        limitComp.IsActive = false;
                    }
                }
                else
                {
                    var lastBoneTfm = _bones[^1]._transform;
                    var firstBoneTfm = _bones[0]._transform;
                    var thisBoneTfm = _bones[i]._transform;
                    if (lastBoneTfm is null)
                    {
                        Debug.LogWarning($"Bone {_bones.Length - 1} is null.");
                        continue;
                    }
                    if (firstBoneTfm is null)
                    {
                        Debug.LogWarning($"Bone {0} is null.");
                        continue;
                    }
                    if (thisBoneTfm is null)
                    {
                        Debug.LogWarning($"Bone {i} is null.");
                        continue;
                    }
                    _bones[i]._axis = Vector3.Transform((lastBoneTfm.WorldTranslation - firstBoneTfm.WorldTranslation), Quaternion.Inverse(thisBoneTfm.WorldRotation));
                }
            }
        }

        /*
		 * Gets the direction from last bone to first bone in first bone's local space.
		 * */
        protected virtual Vector3 LocalDirection
        {
            get
            {
                var firstBoneTfm = _bones[0]._transform;
                if (firstBoneTfm is null)
                    return Vector3.Zero;
                var lastBoneTfm = _bones[^1]._transform;
                if (lastBoneTfm is null)
                    return Vector3.Zero;
                Vector3 firstToLast = lastBoneTfm.WorldTranslation - firstBoneTfm.WorldTranslation;
                return _bones[0]._transform?.InverseTransformDirection(firstToLast) ?? Vector3.Zero;
            }
        }

        /*
		 * Gets the offset from last position of the last bone to its current position.
		 * */
        protected float PositionOffsetSquared
            => (LocalDirection - _lastLocalDirection).LengthSquared();

        /*
		 * Get target offset to break out of the linear singularity issue
		 * */
        protected Vector3 GetSingularityOffset()
        {
            if (!SingularityDetected())
                return Vector3.Zero;

            var tfm = _bones[0]._transform;
            if (tfm is null)
                return Vector3.Zero;

            Vector3 IKDirection = (GetWorldIKPosition() - tfm.WorldTranslation).Normalized();
            Vector3 secondaryDirection = new(IKDirection.Y, IKDirection.Z, IKDirection.X);

            // Avoiding getting locked by the Hinge Rotation Limit
            var bone = _bones[^2];
            if (_useRotationLimits && bone.RotationLimit != null && bone.RotationLimit is IKHingeConstraintComponent && bone._transform != null)
                secondaryDirection = bone._transform.WorldRotation.Rotate(bone.RotationLimit.Axis);
            
            return Vector3.Cross(IKDirection, secondaryDirection) * _bones[^2]._length * 0.5f;
        }

        /*
		 * Detects linear singularity issue when the direction from first bone to IKPosition matches the direction from first bone to the last bone.
		 * */
        private bool SingularityDetected()
        {
            if (!Initialized)
                return false;

            var lastBoneTfm = _bones[^1]._transform;
            if (lastBoneTfm is null)
                return false;

            var firstBoneTfm = _bones[0]._transform;
            if (firstBoneTfm is null)
                return false;

            Vector3 toLastBone = lastBoneTfm.WorldTranslation - firstBoneTfm.WorldTranslation;
            Vector3 toIKPosition = GetWorldIKPosition() - firstBoneTfm.WorldTranslation;

            float toLastBoneDistance = toLastBone.Length();
            float toIKPositionDistance = toIKPosition.Length();

            if (toLastBoneDistance < toIKPositionDistance)
                return false;

            if (toLastBoneDistance < _chainLength - (_bones[^2]._length * 0.1f))
                return false;

            if (toLastBoneDistance == 0)
                return false;

            if (toIKPositionDistance == 0)
                return false;

            if (toIKPositionDistance > toLastBoneDistance)
                return false;

            float dot = Vector3.Dot(toLastBone / toLastBoneDistance, toIKPosition / toIKPositionDistance);
            if (dot < 0.999f)
                return false;

            return true;
        }

    }
}
