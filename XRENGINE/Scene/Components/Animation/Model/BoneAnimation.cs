using System.ComponentModel;
using System.Numerics;
using XREngine.Components;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Scene.Components.Animation;

namespace XREngine.Animation
{
    public class BoneAnimation : XRBase
    {
        public BoneAnimation() { }
        public BoneAnimation(SkeletalAnimation parent, string name)
        {
            Name = name;
            Parent = parent;
        }

        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Determines which method to use, baked or keyframed.
        /// Keyframed takes up less memory and calculates in-between frames on the fly, which allows for time dilation.
        /// Baked takes up more memory but requires no calculations. However, the animation cannot be sped up at all, nor slowed down without artifacts.
        /// </summary>
        public bool UseKeyframes
        {
            get => _useKeyframes;
            set
            {
                _useKeyframes = value;
                UseKeyframesChanged();
            }
        }

        protected virtual void UseKeyframesChanged() { }

        public float LengthInSeconds => _tracks.LengthInSeconds;

        public void SetLength(float seconds, bool stretchAnimation, bool notifyChanged = true)
            => _tracks.SetLength(seconds, stretchAnimation, notifyChanged);

        internal SkeletalAnimation? Parent { get; set; }

        private bool _useKeyframes = true;

        private readonly TransformKeyCollection _tracks = new();

        [Category("Bone Animation")]
        public PropAnimFloat TranslationX => _tracks.TranslationX;
        [Category("Bone Animation")]
        public PropAnimFloat TranslationY => _tracks.TranslationY;
        [Category("Bone Animation")]
        public PropAnimFloat TranslationZ => _tracks.TranslationZ;

        [Category("Bone Animation")]
        public PropAnimQuaternion Rotation => _tracks.Rotation;

        [Category("Bone Animation")]
        public PropAnimFloat ScaleX => _tracks.ScaleX;
        [Category("Bone Animation")]
        public PropAnimFloat ScaleY => _tracks.ScaleY;
        [Category("Bone Animation")]
        public PropAnimFloat ScaleZ => _tracks.ScaleZ;

        public void Progress(float delta)
            => _tracks.Progress(delta);

        //TODO: pool bone frames
        public BoneFrame GetFrame()
            => new(Name, _tracks);
        public BoneFrame GetFrame(float second)
            => new(Name, _tracks, second);

        //public void SetValue(Matrix4 transform, float frameIndex, PlanarInterpType planar, RadialInterpType radial)
        //{
        //    FrameState state = FrameState.DeriveTRS(transform);
        //    _translation.Add(new Vector3Keyframe(frameIndex, state.Translation, planar));
        //    _rotation.Add(new QuatKeyframe(frameIndex, state.Quaternion, radial));
        //    _scale.Add(new Vector3Keyframe(frameIndex, state.Scale, planar));
        //}
        private readonly HashSet<string> _boneNotFoundCache = [];
        public void UpdateSkeleton(HumanoidComponent skeleton)
        {
            var bone = skeleton.GetBoneByName(Name);
            if (bone != null)
                UpdateState(bone.FrameState, bone.BindState);
            else if (!_boneNotFoundCache.Contains(Name))
            {
                _boneNotFoundCache.Add(Name);
                Debug.Out($"Bone '{Name}' not found in skeleton '{skeleton}'.");
            }
        }
        public void UpdateState(TransformState frameState, TransformState bindState)
        {
            GetTransform(bindState, out Vector3 translation, out Quaternion rotation, out Vector3 scale);
            frameState.SetAll(translation, rotation, scale);
        }
        public void UpdateState(TransformState frameState, TransformState bindState, float second)
        {
            GetTransform(bindState, second, out Vector3 translation, out Quaternion rotation, out Vector3 scale);
            frameState.SetAll(translation, rotation, scale);
        }

        /// <summary>
        /// Retrieves the parts of the transform for this bone at the current frame second.
        /// </summary>
        public unsafe void GetTransform(TransformState bindState, out Vector3 translation, out Quaternion rotation, out Vector3 scale)
            => _tracks.GetTransform(bindState, out translation, out rotation, out scale);
        /// <summary>
        /// Retrieves the parts of the transform for this bone at the requested frame second.
        /// </summary>
        public unsafe void GetTransform(TransformState bindState, float second, out Vector3 translation, out Quaternion rotation, out Vector3 scale)
            => _tracks.GetTransform(bindState, out translation, out rotation, out scale, second);
        public void UpdateStateBlended(TransformState frameState, TransformState bindState, BoneAnimation otherBoneAnim, float otherWeight, EAnimBlendType blendType)
            => UpdateStateBlended(frameState, bindState, otherBoneAnim, Parent?.CurrentTime ?? 0.0f, otherBoneAnim.Parent?.CurrentTime ?? 0.0f, otherWeight, blendType);
        public void UpdateStateBlended(
            TransformState frameState,
            TransformState bindState,
            BoneAnimation otherBoneAnim,
            float thisSecond,
            float otherSecond,
            float otherWeight,
            EAnimBlendType blendType)
        {
            GetTransform(bindState, thisSecond, out Vector3 t1, out Quaternion r1, out Vector3 s1);
            otherBoneAnim.GetTransform(bindState, otherSecond, out Vector3 t2, out Quaternion r2, out Vector3 s2);

            otherWeight = TimeModifier(otherWeight, blendType);

            Vector3 t = Vector3.Lerp(t1, t2, otherWeight);
            Quaternion r = Quaternion.Slerp(r1, r2, otherWeight);
            Vector3 s = Vector3.Lerp(s1, s2, otherWeight);

            frameState.SetAll(t, r, s);
        }

        private static float TimeModifier(float otherWeight, EAnimBlendType blendType)
            => blendType switch
            {
                EAnimBlendType.CosineEaseInOut => Interp.Cosine(0.0f, 1.0f, otherWeight),
                EAnimBlendType.QuadraticEaseStart => Interp.QuadraticEaseStart(0.0f, 1.0f, otherWeight),
                EAnimBlendType.QuadraticEaseEnd => Interp.QuadraticEaseEnd(0.0f, 1.0f, otherWeight),
                EAnimBlendType.Linear => otherWeight,
                _ => otherWeight,
            };

        public void UpdateSkeletonBlended(
            HumanoidComponent skeleton,
            BoneAnimation otherBoneAnim,
            float otherWeight,
            EAnimBlendType blendType)
        {
            var bone = skeleton.GetBoneByName(Name);
            if (bone != null)
                UpdateStateBlended(bone.FrameState, bone.BindState, otherBoneAnim, otherWeight, blendType);
        }
        public BoneFrame BlendedWith(float second, BoneFrame? other, float otherWeight)
            => GetFrame(second).BlendedWith(other, otherWeight);

    }
}
