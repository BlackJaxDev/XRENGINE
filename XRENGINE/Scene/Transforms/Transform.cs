using Extensions;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using XREngine.Components;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Core.TypeConverters;
using XREngine.Data.Transforms.Rotations;
using YamlDotNet.Serialization;

namespace XREngine.Scene.Transforms
{
    /// <summary>
    /// This is the default derived transform class for scene nodes.
    /// Can transform the node in any order of translation, scale and rotation.
    /// T-R-S is default (translation, rotated at that point, and then scaled in that coordinate system).
    /// </summary>
    [Serializable]
    public class Transform : TransformBase
    {
        public override string ToString()
            => $"{Name} | T:[{Translation}], R:[{Rotation}], S:[{Scale}]";

        public enum EOrder
        {
            TRS,
            RST,
            STR,
            TSR,
            SRT,
            RTS,
        }

        public Transform()
            : this(Vector3.Zero, Quaternion.Identity) { }
        public Transform(Vector3 scale, Vector3 translation, Quaternion rotation, TransformBase? parent = null, EOrder order = EOrder.TRS)
            : base(parent)
        {
            Scale = scale;
            Translation = translation;
            Rotation = rotation;
            Order = order;
        }
        public Transform(Vector3 scale, Vector3 translation, Rotator rotation, TransformBase? parent = null, EOrder order = EOrder.TRS)
            : base(parent)
        {
            Scale = scale;
            Translation = translation;
            Rotator = rotation;
            Order = order;
        }
        public Transform(Vector3 translation, Quaternion rotation, TransformBase? parent = null, EOrder order = EOrder.TRS)
            : this(Vector3.One, translation, rotation, parent, order) { }
        public Transform(Vector3 translation, Rotator rotation, TransformBase? parent = null, EOrder order = EOrder.TRS)
            : this(Vector3.One, translation, rotation, parent, order) { }
        public Transform(Quaternion rotation, TransformBase? parent = null, EOrder order = EOrder.TRS)
            : this(Vector3.Zero, rotation, parent, order) { }
        public Transform(Rotator rotation, TransformBase? parent = null, EOrder order = EOrder.TRS)
            : this(Vector3.Zero, rotation, parent, order) { }
        public Transform(Vector3 translation, TransformBase? parent = null, EOrder order = EOrder.TRS)
            : this(translation, Quaternion.Identity, parent, order) { }
        public Transform(TransformBase? parent = null, EOrder order = EOrder.TRS)
            : this(Quaternion.Identity, parent, order) { }

        private Vector3 _scale = Vector3.One;
        //[TypeConverter(typeof(Vector3TypeConverter))]
        //[DefaultValue(typeof(Vector3), "1 1 1")]
        /// <summary>
        /// The local scale of this transform relative to its parent.
        /// </summary>
        public Vector3 Scale
        {
            get => _scale;
            set => SetField(ref _scale, value);
        }

        private Vector3 _translation = Vector3.Zero;
        /// <summary>
        /// The local translation of this transform relative to its parent.
        /// </summary>
        public Vector3 Translation
        {
            get => _translation;
            set => SetField(ref _translation, value);
        }

        /// <summary>
        /// The local rotation of this transform relative to its parent, as a rotator (yaw, pitch and roll are separated).
        /// </summary>
        [YamlIgnore]
        public Rotator Rotator
        {
            get => Rotator.FromQuaternion(Rotation);
            set => Rotation = value.ToQuaternion();
        }

        private Quaternion _rotation = Quaternion.Identity;
        //[DefaultValue(typeof(Quaternion), "0 0 0 1")]
        //[TypeConverter(typeof(QuaternionTypeConverter))]
        /// <summary>
        /// The local rotation of this transform relative to its parent.
        /// </summary>
        public Quaternion Rotation
        {
            get => _rotation;
            set => SetField(ref _rotation, value);
        }

        private EOrder _order = EOrder.TRS;
        /// <summary>
        /// The order of operations to calculate the final local matrix from translation, rotation and scale.
        /// </summary>
        public EOrder Order
        {
            get => _order;
            set => SetField(ref _order, value);
        }

        private float _smoothingSpeed = 0.4f;
        /// <summary>
        /// How fast to interpolate to the target values.
        /// </summary>
        [DefaultValue(0.4f)]
        public float SmoothingSpeed
        {
            get => _smoothingSpeed;
            set => SetField(ref _smoothingSpeed, value);
        }

        private Vector3? _targetScale = null;
        /// <summary>
        /// If set, the transform will interpolate to this scale at the specified smoothing speed.
        /// Used for network replication.
        /// </summary>
        [YamlIgnore]
        public Vector3? TargetScale
        {
            get => _targetScale;
            set => SetField(ref _targetScale, value);
        }
        private Vector3? _targetTranslation = null;
        /// <summary>
        /// If set, the transform will interpolate to this translation at the specified smoothing speed.
        /// Used for network replication.
        /// </summary>
        [YamlIgnore]
        public Vector3? TargetTranslation
        {
            get => _targetTranslation;
            set => SetField(ref _targetTranslation, value);
        }
        private Quaternion? _targetRotation = null;
        /// <summary>
        /// If set, the transform will interpolate to this rotation at the specified smoothing speed.
        /// Used for network replication.
        /// </summary>
        [YamlIgnore]
        public Quaternion? TargetRotation
        {
            get => _targetRotation;
            set => SetField(ref _targetRotation, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Scale):
                case nameof(Translation):
                case nameof(Rotation):
                    MarkLocalModified();
                    break;
                case nameof(TargetScale):
                case nameof(TargetTranslation):
                case nameof(TargetRotation):
                    VerifySmoothingTick();
                    break;
                case nameof(Order):
                    _localMatrixGen = _order switch
                    {
                        EOrder.RST => RST,
                        EOrder.STR => STR,
                        EOrder.TSR => TSR,
                        EOrder.SRT => SRT,
                        EOrder.RTS => RTS,
                        _ => TRS,
                    };
                    MarkLocalModified();
                    break;
            }
        }

        private bool _isInterpolating = false;
        protected virtual void VerifySmoothingTick()
        {
            bool nowInterpolating = TargetScale.HasValue || TargetTranslation.HasValue || TargetRotation.HasValue;
            if (_isInterpolating == nowInterpolating)
                return;

            if (_isInterpolating = nowInterpolating)
                RegisterTick(ETickGroup.Normal, ETickOrder.Scene, InterpolateToTarget);
            else
                UnregisterTick(ETickGroup.Normal, ETickOrder.Scene, InterpolateToTarget);
        }

        private void InterpolateToTarget()
        {
            float delta = SmoothingSpeed * Engine.Time.Timer.TargetUpdateFrequency * Engine.Time.Timer.Update.SmoothedDilatedDelta;
            if (TargetScale.HasValue)
            {
                Scale = Vector3.Lerp(Scale, TargetScale.Value, delta);
                if (Vector3.DistanceSquared(Scale, TargetScale.Value) < 0.0001f)
                    TargetScale = null;
            }
            if (TargetTranslation.HasValue)
            {
                Translation = Vector3.Lerp(Translation, TargetTranslation.Value, delta);
                if (Vector3.DistanceSquared(Translation, TargetTranslation.Value) < 0.0001f)
                    TargetTranslation = null;
            }
            if (TargetRotation.HasValue)
            {
                Rotation = Quaternion.Slerp(Rotation, TargetRotation.Value, delta);
                if (Quaternion.Dot(Rotation, TargetRotation.Value) > 0.9999f)
                    TargetRotation = null;
            }
        }

        public void ApplyRotation(Quaternion rotation)
            => Rotation = Quaternion.Normalize(Rotation * rotation);
        public void ApplyTranslation(Vector3 translation)
            => Translation += translation;
        public void ApplyScale(Vector3 scale)
            => Scale *= scale;

        private Func<Matrix4x4>? _localMatrixGen;
        protected override Matrix4x4 CreateLocalMatrix()
            => _localMatrixGen?.Invoke() ?? Matrix4x4.Identity;

        protected virtual Matrix4x4 STR() =>
            Matrix4x4.CreateFromQuaternion(Rotation) *
            Matrix4x4.CreateTranslation(Translation) *
            Matrix4x4.CreateScale(Scale);

        protected virtual Matrix4x4 TRS() =>
            Matrix4x4.CreateScale(Scale) *
            Matrix4x4.CreateFromQuaternion(Rotation) *
            Matrix4x4.CreateTranslation(Translation);

        protected virtual Matrix4x4 RST() =>
            Matrix4x4.CreateTranslation(Translation) *
            Matrix4x4.CreateScale(Scale) *
            Matrix4x4.CreateFromQuaternion(Rotation);

        protected virtual Matrix4x4 RTS() =>
            Matrix4x4.CreateScale(Scale) *
            Matrix4x4.CreateTranslation(Translation) *
            Matrix4x4.CreateFromQuaternion(Rotation);

        protected virtual Matrix4x4 TSR() =>
            Matrix4x4.CreateFromQuaternion(Rotation) *
            Matrix4x4.CreateScale(Scale) *
            Matrix4x4.CreateTranslation(Translation);

        protected virtual Matrix4x4 SRT() =>
            Matrix4x4.CreateTranslation(Translation) *
            Matrix4x4.CreateFromQuaternion(Rotation) *
            Matrix4x4.CreateScale(Scale);

        /// <summary>
        /// Transforms the position in the direction of the local forward vector.
        /// </summary>
        /// <param name="v1"></param>
        /// <param name="v2"></param>
        /// <param name="v3"></param>
        /// <exception cref="NotImplementedException"></exception>
        public void TranslateRelative(float x, float y, float z)
            => Translation += Vector3.Transform(new Vector3(x, y, z), Matrix4x4.CreateFromQuaternion(Rotation));

        public void LookAt(Vector3 worldSpaceTarget)
            => Rotation = Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateLookAt(Translation, Vector3.Transform(worldSpaceTarget, ParentInverseWorldMatrix), Globals.Up));

        public override void DeriveLocalMatrix(Matrix4x4 value, bool networkSmoothed = false)
        {
            Order = EOrder.TRS;

            if (!Matrix4x4.Decompose(value, out Vector3 scale, out Quaternion rotation, out Vector3 translation))
                Debug.Out("Failed to decompose matrix.");

            if (networkSmoothed)
            {
                TargetScale = scale;
                TargetTranslation = translation;
                TargetRotation = rotation;
            }
            else
            {
                Scale = scale;
                Translation = translation;
                Rotation = rotation;
            }
        }

        private Vector3 _prevScale = Vector3.One;
        private Vector3 _prevTranslation = Vector3.Zero;
        private Quaternion _prevRotation = Quaternion.Identity;

        public override byte[] EncodeToBytes(bool delta)
        {
            float tolerance = 0.0001f;

            Vector3 s;
            Vector3 t;
            Quaternion r;

            bool hasScale;
            bool hasRotation;
            bool hasTranslation;

            if (delta)
            {
                s = Scale - _prevScale;
                t = Translation - _prevTranslation;
                r = Rotation * Quaternion.Inverse(_prevRotation);

                hasScale = s.LengthSquared() > tolerance;
                hasTranslation = t.LengthSquared() > tolerance;
                hasRotation = !XRMath.IsApproximatelyIdentity(r, tolerance);
            }
            else
            {
                s = Scale;
                t = Translation;
                r = Rotation;

                hasScale = s.DistanceSquared(Vector3.One) > tolerance;
                hasTranslation = t.LengthSquared() > tolerance;
                hasRotation = !XRMath.IsApproximatelyIdentity(r, tolerance);
            }

            byte[]? scale = hasScale ? WriteHalves(s) : null;
            byte[]? translation = hasTranslation ? WriteHalves(t) : null;
            byte[]? rotation = hasRotation ? Compression.CompressQuaternionToBytes(r) : null;

            _prevScale = Scale;
            _prevTranslation = Translation;
            _prevRotation = Rotation;

            byte scaleBits = (byte)16;
            byte transBits = (byte)16;
            byte quatBits = (byte)8;

            byte[] all = new byte[4 + scale?.Length ?? 0 + translation?.Length ?? 0 + rotation?.Length ?? 0];

            int offset = 4;
            if (hasScale)
            {
                Buffer.BlockCopy(scale!, 0, all, offset, scale!.Length);
                offset += scale.Length;
            }
            if (hasTranslation)
            {
                Buffer.BlockCopy(translation!, 0, all, offset, translation!.Length);
                offset += translation.Length;
            }
            if (hasRotation)
            {
                Buffer.BlockCopy(rotation!, 0, all, offset, rotation!.Length);
                //offset += rotation.Length;
            }

            all[0] = (byte)((delta ? 1 : 0) | ((byte)Order << 1));
            all[1] = (byte)((hasScale ? 1 : 0) | (scaleBits << 1));
            all[2] = (byte)((hasTranslation ? 1 : 0) | (transBits << 1));
            all[3] = (byte)((hasRotation ? 1 : 0) | (quatBits << 1));

            return all;
        }

        public override void DecodeFromBytes(byte[] arr)
        {
            byte flag1 = arr[0];
            byte flag2 = arr[1];
            byte flag3 = arr[2];
            byte flag4 = arr[3];

            bool delta = (flag1 & 1) == 1;
            Order = (EOrder)(flag1 >> 1);
            bool hasScale = (flag2 & 1) == 1;
            //int scaleBits = flag2 >> 1;
            bool hasTranslation = (flag3 & 1) == 1;
            //int transBits = flag3 >> 1;
            bool hasRotation = (flag4 & 1) == 1;
            byte quatBits = (byte)(flag4 >> 1);

            int offset = 4;
            if (hasScale)
            {
                Vector3 s = ReadHalves(arr, offset);
                if (delta)
                    TargetScale = TargetScale.HasValue ? TargetScale.Value + s : s;
                else
                    TargetScale = s;
                offset += 6;
            }
            if (hasTranslation)
            {
                Vector3 t = ReadHalves(arr, offset);
                if (delta)
                    TargetTranslation = TargetTranslation.HasValue ? TargetTranslation.Value + t : t;
                else
                    TargetTranslation = t;
                offset += 6;
            }
            if (hasRotation)
            {
                Quaternion r = Compression.DecompressQuaternion(arr, offset, quatBits);
                if (delta)
                    TargetRotation = TargetRotation.HasValue ? Quaternion.Normalize(TargetRotation.Value * r) : r;
                else
                    TargetRotation = r;
            }
        }

        public static byte[] WriteHalves(Vector3 value)
        {
            byte[] bytes = new byte[6];
            Buffer.BlockCopy(BitConverter.GetBytes((Half)value.X), 0, bytes, 0, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((Half)value.Y), 0, bytes, 2, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((Half)value.Z), 0, bytes, 4, 2);
            return bytes;
        }

        public static Vector3 ReadHalves(byte[] arr, int offset) => new(
            (float)BitConverter.ToHalf(arr, offset),
            (float)BitConverter.ToHalf(arr, offset + 2),
            (float)BitConverter.ToHalf(arr, offset + 4));

        //public override Vector3 WorldTranslation
        //{
        //    get => base.WorldTranslation;
        //    set
        //    {
        //        Translation = Vector3.Transform(value, ParentInverseWorldMatrix);
        //    }
        //}
        //public override Quaternion WorldRotation
        //{
        //    get => base.WorldRotation;
        //    set
        //    {
        //        Rotation = Quaternion.Normalize(ParentInverseWorldRotation * value);
        //    }
        //}

        public void SetWorldRotation(Quaternion value, bool networkSmoothed = false)
        {
            Quaternion rotation = Quaternion.Normalize(ParentInverseWorldRotation * value);
            if (networkSmoothed)
                TargetRotation = rotation;
            else
                Rotation = rotation;
        }

        public void SetWorldTranslation(Vector3 value, bool networkSmoothed = false)
        {
            var translation = Vector3.Transform(value, ParentInverseWorldMatrix);
            if (networkSmoothed)
                TargetTranslation = translation;
            else
                Translation = translation;
        }

        public Quaternion ParentWorldRotation
            => Parent?.WorldRotation ?? Quaternion.Identity;
        public Vector3 ParentWorldTranslation
            => Parent?.WorldTranslation ?? Vector3.Zero;

        public Quaternion ParentInverseWorldRotation
            => Quaternion.Inverse(ParentWorldRotation);
        public Vector3 ParentInverseWorldTranslation
            => Vector3.Transform(Vector3.Zero, ParentInverseWorldMatrix);
    }
}