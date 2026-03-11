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

        public AudioEffect(EffectContext parentContext)
        {
            ParentContext = parentContext;
            Handle = ParentContext.Api.GenEffect();
        }

        public EffectType EffectType
        {
            get => (EffectType)GetEffectParameter(EffectInteger.EffectType);
            protected set => SetEffectParameter(EffectInteger.EffectType, (int)value);
        }

        public int GetEffectParameter(EffectInteger param)
            => Api.GetEffectProperty(Handle, param);
        public float GetEffectParameter(EffectFloat param)
            => Api.GetEffectProperty(Handle, param);
        public Vector3 GetEffectParameter(EffectVector3 param)
            => Api.GetEffectProperty(Handle, param);

        public void SetEffectParameter(EffectInteger param, int value)
            => Api.SetEffectProperty(Handle, param, value);
        public void SetEffectParameter(EffectFloat param, float value)
            => Api.SetEffectProperty(Handle, param, value);
        public void SetEffectParameter(EffectVector3 param, Vector3 value)
            => Api.SetEffectProperty(Handle, param, value);
        public void SetEffectParameter(EffectVector3 param, float x, float y, float z)
            => Api.SetEffectProperty(Handle, param, new Vector3(x, y, z));
        public void SetEffectParameter(EffectVector3 param, float value)
            => Api.SetEffectProperty(Handle, param, new Vector3(value));
        public void SetEffectParameter(EffectVector3 param, float x, float y)
            => Api.SetEffectProperty(Handle, param, new Vector3(x, y, 0));

        public void Dispose()
        {
            ParentContext.Api.DeleteEffect(Handle);
            Handle = 0u;
            GC.SuppressFinalize(this);
        }

        void IPoolable.OnPoolableReset()
        {
            Handle = ParentContext.Api.GenEffect();
        }

        void IPoolable.OnPoolableReleased()
        {
            if (Handle == 0u)
                return;

            ParentContext.Api.DeleteEffect(Handle);
            Handle = 0u;
        }

        void IPoolable.OnPoolableDestroyed()
        {
            Dispose();
        }
    }
}