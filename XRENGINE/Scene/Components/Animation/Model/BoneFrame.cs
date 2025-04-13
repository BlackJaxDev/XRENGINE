using System.Numerics;
using XREngine.Core;
using XREngine.Data;
using XREngine.Scene.Components.Animation;
using XREngine.Scene.Transforms;

namespace XREngine.Animation
{
    public class BoneFrame(string name) : IPoolable
    {
        public string _name = name;

        public Vector3Weight Translation;
        public Vector3Weight Scale;
        public QuaternionWeight Rotation;

        public Vector3 GetTranslation(Vector3 bindTranslation)
            => Vector3.Lerp(bindTranslation, Translation.Value, Translation.Weight);
        public Quaternion GetRotation(Quaternion bindRotation)
            => Quaternion.Slerp(bindRotation, Rotation.Value, Rotation.Weight);
        public Vector3 GetScale(Vector3 bindScale)
            => Vector3.Lerp(bindScale, Scale.Value, Scale.Weight);

        public Vector3 GetUnweightedTranslation()
            => Translation.Value;
        public Quaternion GetUnweightedRotation()
            => Rotation.Value;
        public Vector3 GetUnweightedScale()
            => Scale.Value;

        public BoneFrame(string name, Vector3 translation, Quaternion rotation, Vector3 scale) : this(name)
        {
            Translation = new Vector3Weight(translation, 1.0f);
            Rotation = new QuaternionWeight(rotation, 1.0f);
            Scale = new Vector3Weight(scale, 1.0f);
        }
        public BoneFrame(string name, (Vector3 translation, Quaternion rotation, Vector3 scale) parts)
            : this(name, parts.translation, parts.rotation, parts.scale) { }
        public BoneFrame(string name, TransformKeyCollection keys)
            : this(name, keys.GetTransformParts()) { }
        public BoneFrame(string name, TransformKeyCollection keys, float second)
            : this(name, keys.GetTransformParts(second)) { }

        //public BoneFrame(string name, Vector3 translation, Rotator rotation, Vector3 scale, params float[] weights)
        //{
        //    _name = name;

        //    _values = new FrameValueWeight[9];

        //    _values[0].Value = translation.X;
        //    _values[0].Weight = weights[0];
        //    _values[1].Value = translation.Y;
        //    _values[1].Weight = weights[1];
        //    _values[2].Value = translation.Z;
        //    _values[2].Weight = weights[2];

        //    _values[3].Value = rotation.Pitch;
        //    _values[3].Weight = weights[3];
        //    _values[4].Value = rotation.Yaw;
        //    _values[4].Weight = weights[4];
        //    _values[5].Value = rotation.Roll;
        //    _values[5].Weight = weights[5];

        //    _values[6].Value = scale.X;
        //    _values[6].Weight = weights[6];
        //    _values[7].Value = scale.Y;
        //    _values[7].Weight = weights[7];
        //    _values[8].Value = scale.Z;
        //    _values[8].Weight = weights[8];
        //}

        public void UpdateSkeleton(HumanoidComponent? skeleton)
        {
            if (skeleton is null)
                return;

            Transform? bone = skeleton.GetBoneByName(_name);
            if (bone != null)
                UpdateState(bone.FrameState, bone.BindState);
        }
        public void UpdateState(TransformState frameState, TransformState bindState)
        {
            Vector3 t = GetTranslation(bindState.Translation);
            Quaternion r = GetRotation(bindState.Rotation);
            Vector3 s = GetScale(bindState.Scale);
            frameState.SetAll(t, r, s);
        }
        public void UpdateSkeletonBlended(HumanoidComponent skeleton, BoneFrame otherBoneFrame, float otherWeight)
        {
            Transform? bone = skeleton.GetBoneByName(_name);
            if (bone != null)
                UpdateStateBlended(bone.FrameState, bone.BindState, otherBoneFrame, otherWeight);
        }
        public void UpdateStateBlended(
            TransformState frameState,
            TransformState bindState,
            BoneFrame otherBoneFrame,
            float otherWeight)
        {
            Vector3 t;
            Vector3 s;
            Quaternion r;

            if (otherBoneFrame is null)
            {
                otherWeight = 1.0f - otherWeight;

                Translation.Weight *= otherWeight;
                Rotation.Weight *= otherWeight;
                Scale.Weight *= otherWeight;

                t = GetTranslation(bindState.Translation);
                r = GetRotation(bindState.Rotation);
                s = GetScale(bindState.Scale);

                frameState.SetAll(t, r, s);
            }
            else
            {
                Vector3 t1 = GetTranslation(bindState.Translation);
                Vector3 t2 = otherBoneFrame.GetTranslation(bindState.Translation);
                t = Vector3.Lerp(t1, t2, otherWeight);

                Quaternion r1 = GetRotation(bindState.Rotation);
                Quaternion r2 = otherBoneFrame.GetRotation(bindState.Rotation);
                r = Quaternion.Slerp(r1, r2, otherWeight);

                Vector3 s1 = GetScale(bindState.Scale);
                Vector3 s2 = otherBoneFrame.GetScale(bindState.Scale);
                s = Vector3.Lerp(s1, s2, otherWeight);

                frameState.SetAll(t, r, s);
            }
        }
        public BoneFrame BlendedWith(BoneFrame? otherBoneFrame, float otherWeight)
        {
            BoneFrame frame = new(_name);
            frame.BlendWith(otherBoneFrame, otherWeight);
            return frame;
        }

        public BoneFrame BlendedWith(BoneAnimation? other, float frameIndex, float otherWeight)
            => BlendedWith(other?.GetFrame(frameIndex), otherWeight);

        public void BlendWith(BoneFrame? otherBoneFrame, float otherWeight)
        {
            if (otherBoneFrame is null)
                return;
            Translation.Value = Interp.Lerp(Translation.Value, otherBoneFrame.Translation.Value, otherWeight);
            Translation.Weight = Interp.Lerp(Translation.Weight, otherBoneFrame.Translation.Weight, otherWeight);

            Rotation.Value = Quaternion.Slerp(Rotation.Value, otherBoneFrame.Rotation.Value, otherWeight);
            Rotation.Weight = Interp.Lerp(Rotation.Weight, otherBoneFrame.Rotation.Weight, otherWeight);

            Scale.Value = Interp.Lerp(Scale.Value, otherBoneFrame.Scale.Value, otherWeight);
            Scale.Weight = Interp.Lerp(Scale.Weight, otherBoneFrame.Scale.Weight, otherWeight);
        }
        public void BlendWith(BoneAnimation other, float frameIndex, float otherWeight)
        {
            BlendWith(other.GetFrame(frameIndex), otherWeight);
        }

        public void OnPoolableReset()
        {

        }

        public void OnPoolableReleased()
        {

        }

        public void OnPoolableDestroyed()
        {

        }
    }
}
