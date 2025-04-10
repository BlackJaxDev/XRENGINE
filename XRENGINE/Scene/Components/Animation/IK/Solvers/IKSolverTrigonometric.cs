using Extensions;
using MathNet.Numerics;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Components.Animation
{
    [Serializable]
    public partial class IKSolverTrigonometric : IKSolver
    {
        private TransformBase? _ikTargetTransform;
        public TransformBase? IKTargetTransform
        {
            get => _ikTargetTransform;
            set => SetField(ref _ikTargetTransform, value);
        }

        public TransformBase? RelativeIKSpaceTransform { get; set; }

        private float _ikRotationWeight = 1f;
        [Range(0f, 1f)]
        public float IKRotationWeight
        {
            get => _ikRotationWeight;
            set => SetField(ref _ikRotationWeight, value);
        }

        private Quaternion _rawIKRotation = Quaternion.Identity;
        public Quaternion RawIKRotation
        {
            get => _rawIKRotation;
            set => SetField(ref _rawIKRotation, value);
        }

        public Quaternion GetWorldIKRotation()
        {
            var weight = IKRotationWeight;

            if (weight.AlmostEqual(0.0f))
                return _bone3._transform?.WorldRotation ?? Quaternion.Identity;

            var worldRotation = GetUnweightedWorldIKRotation();

            if (weight.AlmostEqual(1.0f))
                return worldRotation;

            return Quaternion.Slerp(_bone3._transform?.WorldRotation ?? Quaternion.Identity, worldRotation, weight);
        }

        protected Quaternion GetUnweightedWorldIKRotation()
            => TargetIKTransform?.WorldRotation ?? _root?.TransformRotation(_rawIKRotation) ?? _rawIKRotation;

        private Vector3 _bendNormal = Globals.Right;
        public Vector3 BendNormal
        {
            get => _bendNormal;
            set => SetField(ref _bendNormal, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(BendNormal):
                    if (float.IsNaN(BendNormal.X) ||
                        float.IsNaN(BendNormal.Y) ||
                        float.IsNaN(BendNormal.Z))
                    {
                        BendNormal = Globals.Right;
                    }
                    break;
            }
        }

        /// <summary>
        /// The first bone (upper arm or thigh).
        /// </summary>
        public TrigonometricBone _bone1 = new();
        /// <summary>
        /// The second bone (forearm or calf).
        /// </summary>
        public TrigonometricBone _bone2 = new();
        /// <summary>
        /// The third bone (hand or foot).
        /// </summary>
        public TrigonometricBone _bone3 = new();

        /// <summary>
        /// Sets the bend goal position.
        /// </summary>
        /// <param name='goalPosition'>
        /// Goal position.
        /// </param>
        public void SetBendGoalPosition(Vector3 goalPosition, float weight)
        {
            if (!Initialized)
                return;

            if (weight <= 0f)
                return;

            if (_bone1._transform == null ||
                _bone2._transform == null ||
                _bone3._transform == null)
                return;

            Vector3 normal = Vector3.Cross(
                goalPosition - _bone1._transform.WorldTranslation,
                GetWorldIKPosition() - _bone1._transform.WorldTranslation);

            if (normal == Vector3.Zero)
                return;

            BendNormal = weight >= 1.0f
                ? normal
                : Vector3.Lerp(BendNormal, normal, weight);
        }

        /// <summary>
        /// Sets the bend plane to match current bone rotations.
        /// </summary>
        public void SetBendPlaneToCurrent()
        {
            if (!Initialized)
                return;

            if (_bone1._transform == null ||
                _bone2._transform == null ||
                _bone3._transform == null)
                return;

            Vector3 normal = Vector3.Cross(
                _bone2._transform.WorldTranslation - _bone1._transform.WorldTranslation,
                _bone3._transform.WorldTranslation - _bone2._transform.WorldTranslation);

            if (normal != Vector3.Zero)
                BendNormal = normal;
        }

        public override IKPoint[] GetPoints()
            => [_bone1, _bone2, _bone3];

        public override IKPoint? GetPoint(Transform transform)
        {
            if (_bone1._transform == transform)
                return _bone1;
            if (_bone2._transform == transform)
                return _bone2;
            if (_bone3._transform == transform)
                return _bone3;
            return null;
        }

        public override void StoreDefaultLocalState()
        {
            _bone1.StoreDefaultLocalState();
            _bone2.StoreDefaultLocalState();
            _bone3.StoreDefaultLocalState();
        }

        public override void ResetTransformToDefault()
        {
            if (!Initialized)
                return;

            _bone1.ResetTransformToDefault();
            _bone2.ResetTransformToDefault();
            _bone3.ResetTransformToDefault();
        }

        public override bool IsValid(ref string message)
        {
            if (_bone1._transform == null || _bone2._transform == null || _bone3._transform == null)
            {
                message = "Please assign all Bones to the IK solver.";
                return false;
            }

            Transform? duplicate = ContainsDuplicate([_bone1._transform, _bone2._transform, _bone3._transform]);
            if (duplicate != null)
            {
                message = $"{duplicate.Name} is represented multiple times in the Bones.";
                return false;
            }

            if (_bone1._transform.WorldTranslation == _bone2._transform.WorldTranslation)
            {
                message = "first bone position is the same as second bone position.";
                return false;
            }
            if (_bone2._transform.WorldTranslation == _bone3._transform.WorldTranslation)
            {
                message = "second bone position is the same as third bone position.";
                return false;
            }

            return true;
        }

        private static Transform? ContainsDuplicate(Transform[] transforms)
        {
            for (int i = 0; i < transforms.Length; i++)
                for (int j = 0; j < transforms.Length; j++)
                    if (i != j && transforms[i] == transforms[j])
                        return transforms[i];
            return null;
        }

        /// <summary>
        /// Reinitiate the solver with new bone Transforms.
        /// </summary>
        /// <returns>
        /// Returns true if the new chain is valid.
        /// </returns>
        public bool SetChain(Transform? bone1, Transform? bone2, Transform? bone3, Transform? root)
        {
            this._bone1._transform = bone1;
            this._bone2._transform = bone2;
            this._bone3._transform = bone3;

            Initialize(root);
            return Initialized;
        }

        /// <summary>
        /// Solve the bone chain.
        /// </summary>
        public static void Solve(Transform bone1, Transform bone2, Transform bone3, Vector3 targetPosition, Vector3 bendNormal, float weight)
        {
            if (weight <= 0f)
                return;

            // Direction of the limb in solver
            targetPosition = Vector3.Lerp(bone3.WorldTranslation, targetPosition, weight);

            Vector3 dir = targetPosition - bone1.WorldTranslation;

            // Distance between the first and the last node solver positions
            float length = dir.Length();
            if (length == 0f)
                return;

            float sqrMag1 = (bone2.WorldTranslation - bone1.WorldTranslation).LengthSquared();
            float sqrMag2 = (bone3.WorldTranslation - bone2.WorldTranslation).LengthSquared();

            // Get the general world space bending direction
            Vector3 bendDir = Vector3.Cross(dir, bendNormal);

            // Get the direction to the trigonometrically solved position of the second node
            Vector3 toBendPoint = GetDirectionToBendPoint(dir, length, bendDir, sqrMag1, sqrMag2);

            // Position the second node
            Quaternion q1 = XRMath.RotationBetweenVectors(bone2.WorldTranslation - bone1.WorldTranslation, toBendPoint);
            if (weight < 1f)
                q1 = Quaternion.Lerp(Quaternion.Identity, q1, weight);

            bone1.SetWorldRotation(q1 * bone1.WorldRotation);

            Quaternion q2 = XRMath.RotationBetweenVectors(bone3.WorldTranslation - bone2.WorldTranslation, targetPosition - bone2.WorldTranslation);
            if (weight < 1f)
                q2 = Quaternion.Lerp(Quaternion.Identity, q2, weight);

            bone2.SetWorldRotation(q2 * bone2.WorldRotation);
        }

        //Calculates the bend direction based on the law of cosines. NB! Magnitude of the returned vector does not equal to the length of the first bone!
        private static Vector3 GetDirectionToBendPoint(Vector3 direction, float directionMag, Vector3 bendDirection, float sqrMag1, float sqrMag2)
        {
            float x = ((directionMag * directionMag) + (sqrMag1 - sqrMag2)) / 2f / directionMag;
            float y = (float)Math.Sqrt((sqrMag1 - x * x).Clamp(0, float.PositiveInfinity));
            return direction == Vector3.Zero
                ? Vector3.Zero
                : Vector3.Transform(new Vector3(0f, y, x), XRMath.LookRotation(direction, bendDirection));
        }

        protected override void OnInitialize()
        {
            if (BendNormal == Vector3.Zero)
                BendNormal = Globals.Right;

            if (_bone3._transform is null)
                return;

            PreInitialize();

            RawIKPosition = _bone3._transform.WorldTranslation;
            RawIKRotation = _bone3._transform.WorldRotation;

            // Initiating bones
            InitiateBones();

            _directHierarchy = IsDirectHierarchy();
        }

        // Are the bones parented directly to each other?
        private bool IsDirectHierarchy()
        {
            if (_bone3._transform == null)
                return false;
            if (_bone2._transform == null)
                return false;

            if (_bone3._transform.Parent != _bone2._transform)
                return false;
            if (_bone2._transform.Parent != _bone1._transform)
                return false;

            return true;
        }

        // Set the defaults for the bones
        public void InitiateBones()
        {
            if (_bone2._transform == null || _bone3._transform == null)
                return;

            _bone1.Initialize(_bone2._transform.WorldTranslation, BendNormal);
            _bone2.Initialize(_bone3._transform.WorldTranslation, BendNormal);

            SetBendPlaneToCurrent();
        }

        public override Vector3 GetWorldIKPosition()
        {
            var weight = IKPositionWeight;

            if (weight.AlmostEqual(0.0f))
                return _bone3._transform?.WorldTranslation ?? Vector3.Zero;

            var worldPos = GetWorldIKPositionUnweighted();

            if (weight.AlmostEqual(1.0f))
                return worldPos;

            return Vector3.Lerp(_bone3._transform?.WorldTranslation ?? Vector3.Zero, worldPos, weight);
        }

        protected override void OnUpdate()
        {
            if (_bone1._transform == null || _bone2._transform == null || _bone3._transform == null)
                return;

            PreUpdate();

            float posWeight = IKPositionWeight;
            if (posWeight > 0.0f)
            {
                // Reinitializing the bones when the hierarchy is not direct. This allows for skipping animated bones in the hierarchy.
                if (!_directHierarchy)
                {
                    _bone1.Initialize(_bone2._transform.WorldTranslation, BendNormal);
                    _bone2.Initialize(_bone3._transform.WorldTranslation, BendNormal);
                }

                // Find out if bone lengths should be updated
                _bone1._lengthSquared = (_bone2._transform.WorldTranslation - _bone1._transform.WorldTranslation).LengthSquared();
                _bone2._lengthSquared = (_bone3._transform.WorldTranslation - _bone2._transform.WorldTranslation).LengthSquared();

                if (BendNormal == Vector3.Zero)
                    Debug.LogWarning("IKSolverTrigonometric Bend Normal is Vector3.zero.");

                var weightedWorldPos = GetWorldIKPosition();

                // Interpolating bend normal
                Vector3 currentBendNormal = Vector3.Lerp(_bone1.GetBendNormalFromCurrentRotation(), BendNormal, posWeight);

                Vector3 bone1ToBone2 = _bone2._transform.WorldTranslation - _bone1._transform.WorldTranslation;

                // Calculating and interpolating bend direction
                Vector3 bendDirection = Vector3.Lerp(
                    bone1ToBone2,
                    GetBendDirection(weightedWorldPos, currentBendNormal),
                    posWeight);

                if (bendDirection == Vector3.Zero)
                    bendDirection = bone1ToBone2;

                // Rotating bone1
                _bone1._transform.SetWorldRotation(_bone1.GetRotation(bendDirection, currentBendNormal));

                // Rotating bone 2
                var bone2ToIK = weightedWorldPos - _bone2._transform.WorldTranslation;
                _bone2._transform.SetWorldRotation(_bone2.GetRotation(bone2ToIK, _bone2.GetBendNormalFromCurrentRotation()));
            }

            // Rotating bone3
            float rotationWeight = IKRotationWeight;
            if (!rotationWeight.AlmostEqual(0.0f))
            {
                Quaternion endIKRot = GetWorldIKRotation();
                _bone3._transform.SetWorldRotation(rotationWeight.AlmostEqual(1.0f) ? endIKRot : Quaternion.Slerp(_bone3._transform.WorldRotation, endIKRot, rotationWeight));
            }

            PostSolve();
        }

        protected virtual void PreInitialize() { }
        protected virtual void PreUpdate() { }
        protected virtual void PostSolve() { }
        protected bool _directHierarchy = true;

        /// <summary>
        /// Calculates the bending direction of the limb based on the law of cosines.
        /// </summary>
        /// <param name="IKPosition"></param>
        /// <param name="bendNormal"></param>
        /// <returns></returns>
        protected Vector3 GetBendDirection(Vector3 IKPosition, Vector3 bendNormal)
        {
            if (_bone1._transform == null || _bone2._transform == null)
                return Vector3.Zero;

            Vector3 direction = IKPosition - _bone1._transform.WorldTranslation;
            if (direction == Vector3.Zero)
                return Vector3.Zero;

            float directionSqrMag = direction.LengthSquared();
            float directionMagnitude = (float)Math.Sqrt(directionSqrMag);

            float x = (directionSqrMag + _bone1._lengthSquared - _bone2._lengthSquared) / 2.0f / directionMagnitude;
            float y = (float)Math.Sqrt((_bone1._lengthSquared - x * x).ClampMin(0));

            Vector3 yDirection = Vector3.Cross(direction / directionMagnitude, bendNormal);
            return Vector3.Transform(new Vector3(0.0f, y, x), XRMath.LookRotation(direction, yDirection));
        }
    }
}
