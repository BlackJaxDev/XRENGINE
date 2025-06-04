using System.Numerics;
using Transform = XREngine.Scene.Transforms.Transform;

namespace XREngine.Components.Animation
{
    public partial class IKSolverVR
    {
        public partial class SolverTransforms
        {
            public class TransformPoses
            {
                /// <summary>
                /// This is the world pose that's read from the Transform before solving.
                /// </summary>
                public PoseData Input;
                /// <summary>
                /// The is the solver-set world pose that will be written back to the Transform after solving.
                /// </summary>
                public PoseData Solved;
                /// <summary>
                /// This is the default local pose of the Transform, which is used to reset the Solved pose.
                /// </summary>
                public PoseData DefaultLocal;

                private Transform? _transform;

                public TransformPoses(Transform? tfm, bool isTranslatable, bool isStretchable)
                {
                    _transform = tfm;
                    TransformChanged();
                    IsTranslatable = isTranslatable;
                    IsStretchable = isStretchable;
                }

                public Transform? Transform
                {
                    get => _transform;
                    set
                    {
                        _transform = value;
                        TransformChanged();
                    }
                }

                public bool IsTranslatable { get; }
                public bool IsStretchable { get; }

                public void ResetSolvedTranslation()
                    => Solved.Translation = DefaultLocal.Translation;
                public void ResetSolvedRotation()
                    => Solved.Rotation = DefaultLocal.Rotation;
                public void ResetSolvedToDefault()
                {
                    ResetSolvedRotation();
                    if (IsTranslatable)
                        ResetSolvedTranslation();
                }

                private void TransformChanged()
                    => DefaultLocal = Input = Solved = new PoseData(_transform?.Translation ?? Vector3.Zero, _transform?.Rotation ?? Quaternion.Identity);

                public void ReadInput()
                {
                    _transform?.RecalculateMatrices(true);
                    Input.Translation = Solved.Translation = _transform?.WorldTranslation ?? Vector3.Zero;
                    Input.Rotation = Solved.Rotation = _transform?.WorldRotation ?? Quaternion.Identity;
                }
                public void WriteSolved(float weight)
                {
                    if (_transform is null)
                        return;

                    _transform.Parent?.RecalculateMatrices(true);
                    //if (IsTranslatable)
                    //    _transform.SetWorldTranslation(Vector3.Lerp(Input.Translation, Solved.Translation, weight));
                    _transform.SetWorldRotation(Quaternion.Slerp(Input.Rotation, Solved.Rotation, weight));
                    _transform.RecalculateMatrices(true);
                }

                public static implicit operator TransformPoses(Transform? tfm)
                    => new(tfm, false, false);
            }
        }
    }
}
