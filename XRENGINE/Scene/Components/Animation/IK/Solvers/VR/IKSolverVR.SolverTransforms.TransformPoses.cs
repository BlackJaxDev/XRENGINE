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
                public PoseData InputWorld { get; } = new PoseData();
                /// <summary>
                /// The is the solver-set world pose that will be written back to the Transform after solving.
                /// </summary>
                public PoseData SolvedWorld { get; } = new PoseData();
                /// <summary>
                /// This is the default local pose of the Transform, which is used to reset the Solved pose.
                /// </summary>
                public PoseData DefaultLocal { get; } = new PoseData();

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
                {
                    var parent = _transform?.Parent;
                    SolvedWorld.Translation = parent is not null ? parent.TransformPoint(DefaultLocal.Translation) : DefaultLocal.Translation;
                }
                public void ResetSolvedRotation()
                {
                    var parent = _transform?.Parent;
                    SolvedWorld.Rotation = parent is not null ? parent.TransformRotation(DefaultLocal.Rotation) : DefaultLocal.Rotation;
                }

                public void ResetSolvedToDefault()
                {
                    ResetSolvedRotation();
                    if (IsTranslatable)
                        ResetSolvedTranslation();
                }

                private void TransformChanged()
                {
                    _transform?.RecalculateMatrices(true);
                    GetDefaultLocal();
                    GetInputAndSolved();
                }

                /// <summary>
                /// Retrieves the default local translation and rotation from the transform.
                /// Does not recalculate matrices.
                /// </summary>
                public void GetDefaultLocal()
                {
                    Vector3 localTranslation = _transform?.Translation ?? Vector3.Zero;
                    Quaternion localRotation = _transform?.Rotation ?? Quaternion.Identity;
                    DefaultLocal.Translation = localTranslation;
                    DefaultLocal.Rotation = localRotation;
                }

                /// <summary>
                /// Retrieves the input and solved translation and rotations from the transform.
                /// Does not recalculate matrices.
                /// </summary>
                public void GetInputAndSolved()
                {
                    Vector3 worldTranslation = _transform?.WorldTranslation ?? Vector3.Zero;
                    Quaternion worldRotation = _transform?.WorldRotation ?? Quaternion.Identity;
                    InputWorld.Translation = worldTranslation;
                    InputWorld.Rotation = worldRotation;
                    SolvedWorld.Translation = worldTranslation;
                    SolvedWorld.Rotation = worldRotation;
                }

                public void ReadInput()
                {
                    _transform?.RecalculateMatrices(true);
                    GetInputAndSolved();
                }

                public void WriteSolved(float weight)
                {
                    if (_transform is null)
                        return;

                    _transform.Parent?.RecalculateMatrices(true);
                    //if (IsTranslatable)
                    //    _transform.SetWorldTranslation(Vector3.Lerp(InputWorld.Translation, SolvedWorld.Translation, weight));
                    _transform.SetWorldRotation(Quaternion.Slerp(InputWorld.Rotation, SolvedWorld.Rotation, weight));
                    _transform.RecalculateMatrices(true);
                }

                public static implicit operator TransformPoses(Transform? tfm)
                    => new(tfm, false, false);
            }
        }
    }
}
