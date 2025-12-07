using System.Numerics;
using MemoryPack;
using XREngine.Core.Files;

namespace XREngine.Animation
{
    [MemoryPackable]
    public partial class TransformKeyCollection : XRAsset
    {
        public TransformKeyCollection() { }

        public float LengthInSeconds { get; private set; }
        public ETransformOrder TransformOrder { get; set; } = ETransformOrder.TRS;
        public bool AbsoluteTranslation { get; set; } = false;
        public bool AbsoluteRotation { get; set; } = false;

        [MemoryPackIgnore]
        public PropAnimFloat TranslationX { get; } = new PropAnimFloat() { DefaultValue = 0.0f };
        [MemoryPackIgnore]
        public PropAnimFloat TranslationY { get; } = new PropAnimFloat() { DefaultValue = 0.0f };
        [MemoryPackIgnore]
        public PropAnimFloat TranslationZ { get; } = new PropAnimFloat() { DefaultValue = 0.0f };
        [MemoryPackIgnore]
        public PropAnimFloat ScaleX { get; } = new PropAnimFloat() { DefaultValue = 0.0f };
        [MemoryPackIgnore]
        public PropAnimFloat ScaleY { get; } = new PropAnimFloat() { DefaultValue = 0.0f };
        [MemoryPackIgnore]
        public PropAnimFloat ScaleZ { get; } = new PropAnimFloat() { DefaultValue = 0.0f };
        [MemoryPackIgnore]
        public PropAnimQuaternion Rotation { get; } = new PropAnimQuaternion() { DefaultValue = Quaternion.Identity };

        public void SetLength(float seconds, bool stretchAnimation, bool notifyChanged = true)
        {
            LengthInSeconds = seconds;
            TranslationX.SetLength(seconds, stretchAnimation, notifyChanged);
            TranslationY.SetLength(seconds, stretchAnimation, notifyChanged);
            TranslationZ.SetLength(seconds, stretchAnimation, notifyChanged);
            ScaleX.SetLength(seconds, stretchAnimation, notifyChanged);
            ScaleY.SetLength(seconds, stretchAnimation, notifyChanged);
            ScaleZ.SetLength(seconds, stretchAnimation, notifyChanged);
            Rotation.SetLength(seconds, stretchAnimation, notifyChanged);
        }

        public void Progress(float delta)
        {
            TranslationX.Tick(delta);
            TranslationY.Tick(delta);
            TranslationZ.Tick(delta);
            ScaleX.Tick(delta);
            ScaleY.Tick(delta);
            ScaleZ.Tick(delta);
            Rotation.Tick(delta);
        }

        private static unsafe void GetTrackValue(float* animPtr, float* bindPtr, PropAnimFloat track, int index)
            => animPtr[index] = track.Keyframes.Count == 0 ? bindPtr[index] : track.CurrentPosition;
        private static unsafe void GetTrackValue(float* animPtr, float* bindPtr, PropAnimFloat track, int index, float time)
            => animPtr[index] = track.Keyframes.Count == 0 ? bindPtr[index] : track.GetValue(time);

        public unsafe void GetTransform(TransformState bindState, out Vector3 translation, out Quaternion rotation, out Vector3 scale)
        {
            Vector3 t, s;
            Vector3 bt = bindState.Translation;
            Vector3 bs = bindState.Scale;

            float* pt = (float*)&t;
            float* ps = (float*)&s;
            float* pbt = (float*)&bt;
            float* pbs = (float*)&bs;

            GetTrackValue(pt, pbt, TranslationX, 0);
            GetTrackValue(pt, pbt, TranslationY, 1);
            GetTrackValue(pt, pbt, TranslationZ, 2);

            GetTrackValue(ps, pbs, ScaleX, 0);
            GetTrackValue(ps, pbs, ScaleY, 1);
            GetTrackValue(ps, pbs, ScaleZ, 2);

            rotation = Rotation.GetValue(Rotation.CurrentTime);
            if (!AbsoluteRotation)
                rotation = bindState.Rotation * rotation;

            translation = t;
            scale = s;
        }
        public unsafe void GetTransform(TransformState bindState, out Vector3 translation, out Quaternion rotation, out Vector3 scale, float second)
        {
            Vector3 t, s;
            Vector3 bt = bindState.Translation;
            Vector3 bs = bindState.Scale;

            float* pt = (float*)&t;
            float* ps = (float*)&s;
            float* pbt = (float*)&bt;
            float* pbs = (float*)&bs;

            GetTrackValue(pt, pbt, TranslationX, 0, second);
            GetTrackValue(pt, pbt, TranslationY, 1, second);
            GetTrackValue(pt, pbt, TranslationZ, 2, second);

            GetTrackValue(ps, pbs, ScaleX, 0, second);
            GetTrackValue(ps, pbs, ScaleY, 1, second);
            GetTrackValue(ps, pbs, ScaleZ, 2, second);

            rotation = Rotation.GetValue(second);
            if (!AbsoluteRotation)
                rotation *= bindState.Rotation;

            translation = t;
            scale = s;
        }
        public unsafe void GetTransform(out Vector3 translation, out Quaternion rotation, out Vector3 scale)
        {
            translation = new Vector3(
                TranslationX.CurrentPosition,
                TranslationY.CurrentPosition,
                TranslationZ.CurrentPosition);

            rotation = Rotation.GetValue(Rotation.CurrentTime);

            scale = new Vector3(
                ScaleX.CurrentPosition,
                ScaleY.CurrentPosition,
                ScaleZ.CurrentPosition);
        }
        public unsafe void GetTransform(out Vector3 translation, out Quaternion rotation, out Vector3 scale, float second)
        {
            translation = new Vector3(
                TranslationX.GetValue(second),
                TranslationY.GetValue(second),
                TranslationZ.GetValue(second));

            rotation = Rotation.GetValue(second);

            scale = new Vector3(
                ScaleX.GetValue(second),
                ScaleY.GetValue(second),
                ScaleZ.GetValue(second));
        }
        public unsafe (Vector3 translation, Quaternion rotation, Vector3 scale) GetTransformParts()
        {
            GetTransform(out Vector3 t, out Quaternion r, out Vector3 s);
            return (t, r, s);
        }
        public unsafe (Vector3 translation, Quaternion rotation, Vector3 scale) GetTransformParts(float second)
        {
            GetTransform(out Vector3 t, out Quaternion r, out Vector3 s, second);
            return (t, r, s);
        }
        public void ResetKeys()
        {
            ResetTrack(TranslationX);
            ResetTrack(TranslationY);
            ResetTrack(TranslationZ);
            ResetTrack(ScaleX);
            ResetTrack(ScaleY);
            ResetTrack(ScaleZ);
            ResetTrack(Rotation);
        }

        private void ResetTrack(PropAnimFloat track)
        {
            track.Keyframes.Clear();
            track.SetLength(LengthInSeconds, false);
        }
        private void ResetTrack(PropAnimQuaternion track)
        {
            track.Keyframes.Clear();
            track.SetLength(LengthInSeconds, false);
        }
    }
}
