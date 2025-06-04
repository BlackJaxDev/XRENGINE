using SharpFont;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Core;

namespace XREngine.Components.Animation
{
    public partial class IKSolverVR
    {
        /// <summary>
        /// A base class for all IKSolverVR body parts.
        /// </summary>
        [Serializable]
        public abstract class BodyPart : XRBase
        {
            protected abstract void OnRead(SolverTransforms transforms);

            public abstract void PreSolve(float scale);
            public abstract void ApplyOffsets(float scale);
            public abstract void ResetOffsets();

            public float LengthSquared { get; private set; }
            public float Length { get; private set; }

            [HideInInspector]
            public VirtualBone[] _bones = [];
            protected bool _initialized = false;
            protected Vector3 _rootPosition = Vector3.Zero;
            protected Quaternion _rootRotation = Quaternion.Identity;

            protected EQuality _quality = EQuality.Full;
            public EQuality Quality
            {
                get => _quality;
                set => SetField(ref _quality, value);
            }

            /// <summary>
            /// Sets initial solver positions and rotations from the arrays, which have been read from the skeleton.
            /// </summary>
            /// <param name="positions"></param>
            /// <param name="rotations"></param>
            /// <param name="hasChest"></param>
            /// <param name="hasNeck"></param>
            /// <param name="hasShoulders"></param>
            /// <param name="hasToes"></param>
            /// <param name="hasLegs"></param>
            /// <param name="rootIndex"></param>
            /// <param name="index"></param>
            public void Read(SolverTransforms transforms, int rootIndex)
            {
                var root = transforms[rootIndex];
                _rootPosition = root.Input.Translation;
                _rootRotation = root.Input.Rotation;

                OnRead(transforms);

                Length = VirtualBone.PreSolve(ref _bones);
                LengthSquared = Length * Length;

                _initialized = true;
            }

            public void MovePosition(Vector3 position)
            {
                Vector3 delta = position - _bones[0].SolverPosition;
                if (delta.LengthSquared() < float.Epsilon)
                    return; // No movement, skip

                foreach (VirtualBone bone in _bones)
                    bone.SolverPosition += delta;
            }

            public void MoveRotation(Quaternion rotation)
            {
                Quaternion delta = XRMath.FromToRotation(_bones[0].SolverRotation, rotation);
                VirtualBone.RotateAroundPoint(_bones, 0, _bones[0].SolverPosition, delta);
            }

            public void Translate(Vector3 position, Quaternion rotation)
            {
                MovePosition(position);
                MoveRotation(rotation);
            }

            /// <summary>
            /// Updates the root position and rotation of this body part.
            /// </summary>
            /// <param name="newRootPos"></param>
            /// <param name="newRootRot"></param>
            public void TranslateRoot(Vector3 newRootPos, Quaternion newRootRot)
            {
                Vector3 deltaPosition = newRootPos - _rootPosition;
                _rootPosition = newRootPos;

                if (deltaPosition.LengthSquared() >= float.Epsilon)
                    foreach (VirtualBone bone in _bones)
                        bone.SolverPosition += deltaPosition;
                
                Quaternion deltaRotation = XRMath.FromToRotation(_rootRotation, newRootRot);
                _rootRotation = newRootRot;

                VirtualBone.RotateAroundPoint(_bones, 0, newRootPos, deltaRotation);
            }

            public void RotateTo(VirtualBone bone, Quaternion rotation, float weight = 1.0f)
            {
                if (weight <= 0.0f)
                    return;

                Quaternion q = XRMath.FromToRotation(bone.SolverRotation, rotation);

                if (weight < 1.0f)
                    q = Quaternion.Slerp(Quaternion.Identity, q, weight);

                for (int i = 0; i < _bones.Length; i++)
                {
                    if (_bones[i] != bone)
                        continue;
                    
                    VirtualBone.RotateAroundPoint(_bones, i, _bones[i].SolverPosition, q);
                    break;
                }
            }

            public virtual void Visualize(ColorF4 color)
            {
                for (int i = 0; i < _bones.Length - 1; i++)
                    Engine.Rendering.Debug.RenderLine(_bones[i].SolverPosition, _bones[i + 1].SolverPosition, color);
            }

            public void Visualize()
                => Visualize(ColorF4.Magenta);
        }
    }
}