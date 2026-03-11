using Silk.NET.OpenAL.Extensions.Creative;
using XREngine.Audio.Effects;
using XREngine.Core;
using XREngine.Data.Core;

namespace XREngine.Audio
{
    public sealed class EffectContext : XRBase
    {
        internal ListenerContext Listener { get; }
        internal EffectExtension Api { get; }

        public EventDictionary<uint, EAXReverbEffect> EaxReverbEffects { get; } = [];
        public EventDictionary<uint, ReverbEffect> ReverbEffects { get; } = [];
        public EventDictionary<uint, ChorusEffect> ChorusEffects { get; } = [];
        public EventDictionary<uint, DistortionEffect> DistortionEffects { get; } = [];
        public EventDictionary<uint, EchoEffect> EchoEffects { get; } = [];
        public EventDictionary<uint, FlangerEffect> FlangerEffects { get; } = [];
        public EventDictionary<uint, FrequencyShifterEffect> FrequencyShifterEffects { get; } = [];
        public EventDictionary<uint, VocalMorpherEffect> VocalMorpherEffects { get; } = [];
        public EventDictionary<uint, PitchShifterEffect> PitchShifterEffects { get; } = [];
        public EventDictionary<uint, RingModulatorEffect> RingModulatorEffects { get; } = [];
        public EventDictionary<uint, AutowahEffect> AutowahEffects { get; } = [];
        public EventDictionary<uint, CompressorEffect> CompressorEffects { get; } = [];
        public EventDictionary<uint, EqualizerEffect> EqualizerEffects { get; } = [];

        private ResourcePool<EAXReverbEffect> EaxReverbPool { get; }
        private ResourcePool<ReverbEffect> ReverbPool { get; }
        private ResourcePool<ChorusEffect> ChorusPool { get; }
        private ResourcePool<DistortionEffect> DistortionPool { get; }
        private ResourcePool<EchoEffect> EchoPool { get; }
        private ResourcePool<FlangerEffect> FlangerPool { get; }
        private ResourcePool<FrequencyShifterEffect> FrequencyShifterPool { get; }
        private ResourcePool<VocalMorpherEffect> VocalMorpherPool { get; }
        private ResourcePool<PitchShifterEffect> PitchShifterPool { get; }
        private ResourcePool<RingModulatorEffect> RingModulatorPool { get; }
        private ResourcePool<AutowahEffect> AutowahPool { get; }
        private ResourcePool<CompressorEffect> CompressorPool { get; }
        private ResourcePool<EqualizerEffect> EqualizerPool { get; }

        public EffectContext(ListenerContext listener, EffectExtension effects)
        {
            Listener = listener;
            Api = effects;
            EaxReverbPool = new(() => new EAXReverbEffect(this));
            ReverbPool = new(() => new ReverbEffect(this));
            ChorusPool = new(() => new ChorusEffect(this));
            DistortionPool = new(() => new DistortionEffect(this));
            EchoPool = new(() => new EchoEffect(this));
            FlangerPool = new(() => new FlangerEffect(this));
            FrequencyShifterPool = new(() => new FrequencyShifterEffect(this));
            VocalMorpherPool = new(() => new VocalMorpherEffect(this));
            PitchShifterPool = new(() => new PitchShifterEffect(this));
            RingModulatorPool = new(() => new RingModulatorEffect(this));
            AutowahPool = new(() => new AutowahEffect(this));
            CompressorPool = new(() => new CompressorEffect(this));
            EqualizerPool = new(() => new EqualizerEffect(this));
        }

        public EAXReverbEffect TakeEaxReverbEffect()
        {
            EAXReverbEffect effect = EaxReverbPool.Take();
            EaxReverbEffects.Add(effect.Handle, effect);
            return effect;
        }
        public ReverbEffect TakeReverbEffect()
        {
            ReverbEffect effect = ReverbPool.Take();
            ReverbEffects.Add(effect.Handle, effect);
            return effect;
        }
        public ChorusEffect TakeChorusEffect()
        {
            ChorusEffect effect = ChorusPool.Take();
            ChorusEffects.Add(effect.Handle, effect);
            return effect;
        }
        public DistortionEffect TakeDistortionEffect()
        {
            DistortionEffect effect = DistortionPool.Take();
            DistortionEffects.Add(effect.Handle, effect);
            return effect;
        }
        public EchoEffect TakeEchoEffect()
        {
            EchoEffect effect = EchoPool.Take();
            EchoEffects.Add(effect.Handle, effect);
            return effect;
        }
        public FlangerEffect TakeFlangerEffect()
        {
            FlangerEffect effect = FlangerPool.Take();
            FlangerEffects.Add(effect.Handle, effect);
            return effect;
        }
        public FrequencyShifterEffect TakeFrequencyShifterEffect()
        {
            FrequencyShifterEffect effect = FrequencyShifterPool.Take();
            FrequencyShifterEffects.Add(effect.Handle, effect);
            return effect;
        }
        public VocalMorpherEffect TakeVocalMorpherEffect()
        {
            VocalMorpherEffect effect = VocalMorpherPool.Take();
            VocalMorpherEffects.Add(effect.Handle, effect);
            return effect;
        }
        public PitchShifterEffect TakePitchShifterEffect()
        {
            PitchShifterEffect effect = PitchShifterPool.Take();
            PitchShifterEffects.Add(effect.Handle, effect);
            return effect;
        }
        public RingModulatorEffect TakeRingModulatorEffect()
        {
            RingModulatorEffect effect = RingModulatorPool.Take();
            RingModulatorEffects.Add(effect.Handle, effect);
            return effect;
        }
        public AutowahEffect TakeAutowahEffect()
        {
            AutowahEffect effect = AutowahPool.Take();
            AutowahEffects.Add(effect.Handle, effect);
            return effect;
        }
        public CompressorEffect TakeCompressorEffect()
        {
            CompressorEffect effect = CompressorPool.Take();
            CompressorEffects.Add(effect.Handle, effect);
            return effect;
        }
        public EqualizerEffect TakeEqualizerEffect()
        {
            EqualizerEffect effect = EqualizerPool.Take();
            EqualizerEffects.Add(effect.Handle, effect);
            return effect;
        }

        public void ReleaseEaxReverbEffect(EAXReverbEffect effect)
        {
            EaxReverbEffects.Remove(effect.Handle);
            EaxReverbPool.Release(effect);
        }
        public void ReleaseReverbEffect(ReverbEffect effect)
        {
            ReverbEffects.Remove(effect.Handle);
            ReverbPool.Release(effect);
        }
        public void ReleaseChorusEffect(ChorusEffect effect)
        {
            ChorusEffects.Remove(effect.Handle);
            ChorusPool.Release(effect);
        }
        public void ReleaseDistortionEffect(DistortionEffect effect)
        {
            DistortionEffects.Remove(effect.Handle);
            DistortionPool.Release(effect);
        }
        public void ReleaseEchoEffect(EchoEffect effect)
        {
            EchoEffects.Remove(effect.Handle);
            EchoPool.Release(effect);
        }
        public void ReleaseFlangerEffect(FlangerEffect effect)
        {
            FlangerEffects.Remove(effect.Handle);
            FlangerPool.Release(effect);
        }
        public void ReleaseFrequencyShifterEffect(FrequencyShifterEffect effect)
        {
            FrequencyShifterEffects.Remove(effect.Handle);
            FrequencyShifterPool.Release(effect);
        }
        public void ReleaseVocalMorpherEffect(VocalMorpherEffect effect)
        {
            VocalMorpherEffects.Remove(effect.Handle);
            VocalMorpherPool.Release(effect);
        }
        public void ReleasePitchShifterEffect(PitchShifterEffect effect)
        {
            PitchShifterEffects.Remove(effect.Handle);
            PitchShifterPool.Release(effect);
        }
        public void ReleaseRingModulatorEffect(RingModulatorEffect effect)
        {
            RingModulatorEffects.Remove(effect.Handle);
            RingModulatorPool.Release(effect);
        }
        public void ReleaseAutowahEffect(AutowahEffect effect)
        {
            AutowahEffects.Remove(effect.Handle);
            AutowahPool.Release(effect);
        }
        public void ReleaseCompressorEffect(CompressorEffect effect)
        {
            CompressorEffects.Remove(effect.Handle);
            CompressorPool.Release(effect);
        }
        public void ReleaseEqualizerEffect(EqualizerEffect effect)
        {
            EqualizerEffects.Remove(effect.Handle);
            EqualizerPool.Release(effect);
        }

        public void DestroyUnusedEaxReverbEffects(int count)
            => EaxReverbPool.Destroy(count);
        public void DestroyUnusedReverbEffects(int count)
            => ReverbPool.Destroy(count);
        public void DestroyUnusedChorusEffects(int count)
            => ChorusPool.Destroy(count);
        public void DestroyUnusedDistortionEffects(int count)
            => DistortionPool.Destroy(count);
        public void DestroyUnusedEchoEffects(int count)
            => EchoPool.Destroy(count);
        public void DestroyUnusedFlangerEffects(int count)
            => FlangerPool.Destroy(count);
        public void DestroyUnusedFrequencyShifterEffects(int count)
            => FrequencyShifterPool.Destroy(count);
        public void DestroyUnusedVocalMorpherEffects(int count)
            => VocalMorpherPool.Destroy(count);
        public void DestroyUnusedPitchShifterEffects(int count)
            => PitchShifterPool.Destroy(count);
        public void DestroyUnusedRingModulatorEffects(int count)
            => RingModulatorPool.Destroy(count);
        public void DestroyUnusedAutowahEffects(int count)
            => AutowahPool.Destroy(count);
        public void DestroyUnusedCompressorEffects(int count)
            => CompressorPool.Destroy(count);
        public void DestroyUnusedEqualizerEffects(int count)
            => EqualizerPool.Destroy(count);

        public EAXReverbEffect? GetEaxReverbEffectByHandle(uint handle)
            => EaxReverbEffects.TryGetValue(handle, out EAXReverbEffect? effect) ? effect : null;
        public ReverbEffect? GetReverbEffectByHandle(uint handle)
            => ReverbEffects.TryGetValue(handle, out ReverbEffect? effect) ? effect : null;
        public ChorusEffect? GetChorusEffectByHandle(uint handle)
            => ChorusEffects.TryGetValue(handle, out ChorusEffect? effect) ? effect : null;
        public DistortionEffect? GetDistortionEffectByHandle(uint handle)
            => DistortionEffects.TryGetValue(handle, out DistortionEffect? effect) ? effect : null;
        public EchoEffect? GetEchoEffectByHandle(uint handle)
            => EchoEffects.TryGetValue(handle, out EchoEffect? effect) ? effect : null;
        public FlangerEffect? GetFlangerEffectByHandle(uint handle)
            => FlangerEffects.TryGetValue(handle, out FlangerEffect? effect) ? effect : null;
        public FrequencyShifterEffect? GetFrequencyShifterEffectByHandle(uint handle)
            => FrequencyShifterEffects.TryGetValue(handle, out FrequencyShifterEffect? effect) ? effect : null;
        public VocalMorpherEffect? GetVocalMorpherEffectByHandle(uint handle)
            => VocalMorpherEffects.TryGetValue(handle, out VocalMorpherEffect? effect) ? effect : null;
        public PitchShifterEffect? GetPitchShifterEffectByHandle(uint handle)
            => PitchShifterEffects.TryGetValue(handle, out PitchShifterEffect? effect) ? effect : null;
        public RingModulatorEffect? GetRingModulatorEffectByHandle(uint handle)
            => RingModulatorEffects.TryGetValue(handle, out RingModulatorEffect? effect) ? effect : null;
        public AutowahEffect? GetAutowahEffectByHandle(uint handle)
            => AutowahEffects.TryGetValue(handle, out AutowahEffect? effect) ? effect : null;
        public CompressorEffect? GetCompressorEffectByHandle(uint handle)
            => CompressorEffects.TryGetValue(handle, out CompressorEffect? effect) ? effect : null;
        public EqualizerEffect? GetEqualizerEffectByHandle(uint handle)
            => EqualizerEffects.TryGetValue(handle, out EqualizerEffect? effect) ? effect : null;
    }
}