using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Core;

namespace XREngine.Scene.Components.Animation
{
    public partial class IKSolverVR
    {
        /// <summary>
        /// A base class for all IKSolverVR body parts.
        /// </summary>
        [System.Serializable]
        public abstract class BodyPart
        {
            protected abstract void OnRead(
                Vector3[] positions,
                Quaternion[] rotations,
                bool hasChest,
                bool hasNeck,
                bool hasShoulders,
                bool hasToes,
                bool hasLegs,
                int rootIndex,
                int index);

            public abstract void PreSolve(float scale);
            public abstract void Write(ref Vector3[] solvedPositions, ref Quaternion[] solvedRotations);
            public abstract void ApplyOffsets(float scale);
            public abstract void ResetOffsets();

            public float LengthSquared { get; private set; }
            public float Length { get; private set; }

            [HideInInspector]
            public VirtualBone[] _bones = [];
            protected bool _initialized;
            protected Vector3 _rootPosition;
            protected Quaternion _rootRotation = Quaternion.Identity;
            protected int _index = -1;

            protected int _lod;
            public int LOD
            {
                get => _lod;
                set => _lod = value;
            }

            public void Read(Vector3[] positions, Quaternion[] rotations, bool hasChest, bool hasNeck, bool hasShoulders, bool hasToes, bool hasLegs, int rootIndex, int index)
            {
                _index = index;
                _rootPosition = positions[rootIndex];
                _rootRotation = rotations[rootIndex];

                OnRead(positions, rotations, hasChest, hasNeck, hasShoulders, hasToes, hasLegs, rootIndex, index);

                Length = VirtualBone.PreSolve(ref _bones);
                LengthSquared = Length * Length;

                _initialized = true;
            }

            public void MovePosition(Vector3 position)
            {
                Vector3 delta = position - _bones[0]._solverPosition;
                foreach (VirtualBone bone in _bones) bone._solverPosition += delta;
            }

            public void MoveRotation(Quaternion rotation)
            {
                Quaternion delta = XRMath.FromToRotation(_bones[0]._solverRotation, rotation);
                VirtualBone.RotateAroundPoint(_bones, 0, _bones[0]._solverPosition, delta);
            }

            public void Translate(Vector3 position, Quaternion rotation)
            {
                MovePosition(position);
                MoveRotation(rotation);
            }

            public void TranslateRoot(Vector3 newRootPos, Quaternion newRootRot)
            {
                Vector3 deltaPosition = newRootPos - _rootPosition;
                _rootPosition = newRootPos;
                foreach (VirtualBone bone in _bones)
                    bone._solverPosition += deltaPosition;

                Quaternion deltaRotation = XRMath.FromToRotation(_rootRotation, newRootRot);
                _rootRotation = newRootRot;
                VirtualBone.RotateAroundPoint(_bones, 0, newRootPos, deltaRotation);
            }

            public void RotateTo(VirtualBone bone, Quaternion rotation, float weight = 1f)
            {
                if (weight <= 0f)
                    return;

                Quaternion q = XRMath.FromToRotation(bone._solverRotation, rotation);

                if (weight < 1f)
                    q = Quaternion.Slerp(Quaternion.Identity, q, weight);

                for (int i = 0; i < _bones.Length; i++)
                {
                    if (_bones[i] != bone)
                        continue;
                    
                    VirtualBone.RotateAroundPoint(_bones, i, _bones[i]._solverPosition, q);
                    break;
                }
            }

            public void Visualize(ColorF4 color)
            {
                for (int i = 0; i < _bones.Length - 1; i++)
                    Engine.Rendering.Debug.RenderLine(_bones[i]._solverPosition, _bones[i + 1]._solverPosition, color);
            }

            public void Visualize()
                => Visualize(ColorF4.White);
        }
    }
}