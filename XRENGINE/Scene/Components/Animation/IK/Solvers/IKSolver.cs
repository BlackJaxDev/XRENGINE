using MathNet.Numerics;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Components.Animation
{
    [System.Serializable]
    public abstract partial class IKSolver : XRBase
    {
        /// <summary>
        /// Determines whether this instance is valid or not.
        /// </summary>
        public bool IsValid()
        {
            string message = string.Empty;
            return IsValid(ref message);
        }

        /// <summary>
        /// Determines whether this instance is valid or not. If returns false, also fills in an error message.
        /// </summary>
        public abstract bool IsValid(ref string message);

        /// <summary>
        /// Initiate the solver with specified root Transform. Use only if this %IKSolver is not a member of an %IK component.
        /// </summary>
        public void Initialize(Transform? root)
        {
            if (Initialized)
                return;

            OnPreInitialize?.Invoke();

            if (root is null) 
                Debug.LogWarning("Initiating IKSolver with null root Transform.");

            _root = root;
            Initialized = false;

            string message = string.Empty;
            if (!IsValid(ref message))
            {
                Debug.LogWarning(message);
                return;
            }

            OnInitialize();
            StoreDefaultLocalState();
            Initialized = true;
            _firstInit = false;

            OnPostInitialize?.Invoke();
        }

        /// <summary>
        /// Updates the %IK solver. Use only if this %IKSolver is not a member of an %IK component or the %IK component has been disabled and you intend to manually control the updating.
        /// </summary>
        public void Update()
        {
            OnPreUpdate?.Invoke();

            if (_firstInit)
                Initialize(_root); // when the IK component has been disabled in Awake, this will initiate it.

            if (!Initialized)
                return;

            OnUpdate();

            OnPostUpdate?.Invoke();
        }

        protected Vector3 _rawIKPosition;
        public Vector3 RawIKPosition
        {
            get => _rawIKPosition;
            set => SetField(ref _rawIKPosition, value);
        }

        public virtual Vector3 GetWorldIKPosition()
        {
            float weight = IKPositionWeight;

            if (weight.AlmostEqual(0f))
                return Vector3.Zero;

            var worldPosition = GetWorldIKPositionUnweighted();

            if (weight.AlmostEqual(1f))
                return worldPosition;

            return Vector3.Lerp(Vector3.Zero, worldPosition, weight);
        }

        protected Vector3 GetWorldIKPositionUnweighted()
            => TargetIKTransform?.WorldTranslation ?? _root?.TransformPoint(RawIKPosition) ?? RawIKPosition;

        protected float _ikPositionWeight = 1f;
        [Range(0f, 1f)]
        public virtual float IKPositionWeight
        {
            get => _ikPositionWeight;
            set => SetField(ref _ikPositionWeight, value);
        }

        protected TransformBase? _targetIKTransform;
        public TransformBase? TargetIKTransform
        {
            get => _targetIKTransform;
            set => SetField(ref _targetIKTransform, value);
        }

        /// <summary>
        /// Gets a value indicating whether this <see cref="IKSolver"/> has successfully initiated.
        /// </summary>
        public bool Initialized { get; private set; }

        /// <summary>
        /// Gets all the points used by the solver.
        /// </summary>
        public abstract IKPoint[]? GetPoints();

        /// <summary>
        /// Gets the point with the specified Transform.
        /// </summary>
        public abstract IKPoint? GetPoint(Transform transform);

        /// <summary>
        /// Fixes all the Transforms used by the solver to their initial state.
        /// </summary>
        public abstract void ResetTransformToDefault();

        /// <summary>
        /// Stores the default local state for the bones used by the solver.
        /// </summary>
        public abstract void StoreDefaultLocalState();

        /// <summary>
        /// Delegates solver update events.
        /// </summary>
        public delegate void UpdateDelegate();
        /// <summary>
        /// Delegates solver iteration events.
        /// </summary>
        public delegate void IterationDelegate(int i);

        /// <summary>
        /// Called before initiating the solver.
        /// </summary>
        public event UpdateDelegate? OnPreInitialize;
        /// <summary>
        /// Called after initiating the solver.
        /// </summary>
        public event UpdateDelegate? OnPostInitialize;
        /// <summary>
        /// Called before updating.
        /// </summary>
        public event UpdateDelegate? OnPreUpdate;
        /// <summary>
        /// Called after writing the solved pose
        /// </summary>
        public event UpdateDelegate? OnPostUpdate;

        protected abstract void OnInitialize();
        protected abstract void OnUpdate();

        protected bool _firstInit = true;
        protected Transform? _root;

        /// <summary>
        /// Checks if an array of objects contains any duplicates.
        /// </summary>
        public static Transform? ContainsDuplicateBone(IKBone[] bones)
        {
            for (int i = 0; i < bones.Length; i++)
                for (int i2 = 0; i2 < bones.Length; i2++)
                    if (i != i2 && bones[i]._transform == bones[i2]._transform)
                        return bones[i]._transform;
                        
            return null;
        }

        /// <summary>
        /// Checks if the hierarchy of bones is valid.
        /// </summary>
        /// <param name="bones"></param>
        /// <returns></returns>
        public static bool HierarchyIsValid(IKSolver.IKBone[] bones)
        {
            for (int i = 1; i < bones.Length; i++)
            {
                // If parent bone is not an ancestor of bone, the hierarchy is invalid
                if (!IsAncestor(bones[i]._transform, bones[i - 1]._transform))
                    return false;
            }
            return true;
        }

        private static bool IsAncestor(TransformBase? descendant, TransformBase? potentialAncestor)
        {
            if (descendant == null || potentialAncestor == null)
                return false;

            if (descendant == potentialAncestor)
                return true;

            return IsAncestor(descendant.Parent, potentialAncestor);
        }

        // Calculates bone lengths and axes, returns the length of the entire chain
        protected static float PreSolve(ref IKBone[] bones)
        {
            float length = 0;

            for (int i = 0; i < bones.Length; i++)
            {
                var bone = bones[i];
                var transform = bone._transform;
                if (transform != null)
                {
                    bone._defaultLocalPosition = transform.Translation;
                    bone._defaultLocalRotation = transform.Rotation;
                }
            }

            for (int i = 0; i < bones.Length; i++)
            {
                var bone = bones[i];
                if (i < bones.Length - 1)
                {
                    var nextBone = bones[i + 1];

                    bone._lengthSquared = (nextBone._solverPosition - bone._solverPosition).LengthSquared();
                    bone._length = MathF.Sqrt(bone._lengthSquared);
                    bone._axis = Vector3.Transform(nextBone._solverPosition - bone._solverPosition, Quaternion.Inverse(bone._solverRotation));

                    length += bone._length;
                }
                else
                {
                    bones[i]._lengthSquared = 0f;
                    bones[i]._length = 0f;
                }
            }

            return length;
        }
    }
}
