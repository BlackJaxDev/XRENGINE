using Silk.NET.OpenAL.Extensions.Creative;
using System.Numerics;
using XREngine.Core;
using XREngine.Data.Core;

namespace XREngine.Audio.Effects
{
    public abstract class AudioEffect : XRBase, IDisposable, IPoolable
    {
        public EffectContext ParentContext { get; }
        internal uint Handle { get; set; }
        private EffectExtension Api => ParentContext.Api;
        private EffectType _effectType;

        public AudioEffect(EffectContext parentContext)
        {
            ParentContext = parentContext;
        }

        public EffectType EffectType
        {
            get => _effectType;
            protected set
            {
                _effectType = value;
                if (Handle != 0u)
                    SetEffectParameter(EffectInteger.EffectType, (int)value);
            }
        }

        public int GetEffectParameter(EffectInteger param)
        {
            using var native = ParentContext.EnterNative();
            return Api.GetEffectProperty(Handle, param);
        }
        public float GetEffectParameter(EffectFloat param)
        {
            using var native = ParentContext.EnterNative();
            return Api.GetEffectProperty(Handle, param);
        }
        public Vector3 GetEffectParameter(EffectVector3 param)
        {
            using var native = ParentContext.EnterNative();
            return Api.GetEffectProperty(Handle, param);
        }

        public void SetEffectParameter(EffectInteger param, int value)
        {
            using var native = ParentContext.EnterNative();
            Api.SetEffectProperty(Handle, param, value);
        }
        public void SetEffectParameter(EffectFloat param, float value)
        {
            using var native = ParentContext.EnterNative();
            Api.SetEffectProperty(Handle, param, value);
        }
        public void SetEffectParameter(EffectVector3 param, Vector3 value)
        {
            using var native = ParentContext.EnterNative();
            Api.SetEffectProperty(Handle, param, value);
        }
        public void SetEffectParameter(EffectVector3 param, float x, float y, float z)
            => SetEffectParameter(param, new Vector3(x, y, z));
        public void SetEffectParameter(EffectVector3 param, float value)
            => SetEffectParameter(param, new Vector3(value));
        public void SetEffectParameter(EffectVector3 param, float x, float y)
            => SetEffectParameter(param, new Vector3(x, y, 0));

        public void Dispose()
        {
            using var native = ParentContext.EnterNative();
            if (Handle != 0u)
                ParentContext.Api.DeleteEffect(Handle);
            Handle = 0u;
            GC.SuppressFinalize(this);
        }

        void IPoolable.OnPoolableReset()
        {
            using var native = ParentContext.EnterNative();
            Handle = ParentContext.Api.GenEffect();
            SetEffectParameter(EffectInteger.EffectType, (int)_effectType);
        }

        void IPoolable.OnPoolableReleased()
        {
            if (Handle == 0u)
                return;

            using var native = ParentContext.EnterNative();
            ParentContext.Api.DeleteEffect(Handle);
            Handle = 0u;
        }

        void IPoolable.OnPoolableDestroyed()
        {
            Dispose();
        }
    }
}
