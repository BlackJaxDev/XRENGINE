using System.ComponentModel.DataAnnotations;
using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation
{
    public abstract partial class IKSolver
    {
        /// <summary>
        /// The most basic element type in the %IK chain that all other types extend from.
        /// </summary>
        [System.Serializable]
        public class IKPoint
        {
            /// <summary>
            /// The transform.
            /// </summary>
            public Transform? _transform;
            /// <summary>
            /// The weight of this bone in the solver.
            /// </summary>
            [Range(0f, 1f)]
            public float _weight = 1f;
            /// <summary>
            /// Virtual position in the %IK solver.
            /// </summary>
            public Vector3 _solverPosition;
            /// <summary>
            /// Virtual rotation in the %IK solver.
            /// </summary>
            public Quaternion _solverRotation = Quaternion.Identity;
            /// <summary>
            /// The default local position of the Transform.
            /// </summary>
            public Vector3 _defaultLocalPosition;
            /// <summary>
            /// The default local rotation of the Transform.
            /// </summary>
            public Quaternion _defaultLocalRotation;

            /// <summary>
            /// Stores the default local state of the point.
            /// </summary>
            public void StoreDefaultLocalState()
            {
                if (_transform is null)
                    return;

                _defaultLocalPosition = _transform.Translation;
                _defaultLocalRotation = _transform.Rotation;
            }

            /// <summary>
            /// Fixes the transform to its default local state.
            /// </summary>
            public void ResetTransformToDefault()
            {
                if (_transform is null) 
                    return;

                if (_transform.Translation != _defaultLocalPosition)
                    _transform.Translation = _defaultLocalPosition;

                if (_transform.Rotation != _defaultLocalRotation)
                    _transform.Rotation = _defaultLocalRotation;
            }

            /// <summary>
            /// Updates the solverPosition (in world space).
            /// </summary>
            public void UpdateSolverPosition()
            {
                if (_transform is null)
                    return;

                _solverPosition = _transform.WorldTranslation;
            }

            /// <summary>
            /// Updates the solverPosition (in local space).
            /// </summary>
            public void UpdateSolverLocalPosition()
            {
                if (_transform is null)
                    return;

                _solverPosition = _transform.Translation;
            }

            /// <summary>
            /// Updates the solverPosition/Rotation (in world space).
            /// </summary>
            public void UpdateSolverState()
            {
                if (_transform is null)
                    return;

                _solverPosition = _transform.WorldTranslation;
                _solverRotation = _transform.WorldRotation;
            }

            /// <summary>
            /// Updates the solverPosition/Rotation (in local space).
            /// </summary>
            public void UpdateSolverLocalState()
            {
                if (_transform is null)
                    return;

                _solverPosition = _transform.Translation;
                _solverRotation = _transform.Rotation;
            }
        }
    }
}
