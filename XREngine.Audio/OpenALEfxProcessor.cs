using Silk.NET.OpenAL.Extensions.Creative;
using System.Numerics;

namespace XREngine.Audio
{
    /// <summary>
    /// OpenAL Creative EFX implementation of <see cref="IAudioEffectsProcessor"/>.
    /// <para>
    /// Effects are applied <b>in-driver</b> via OpenAL EFX auxiliary send slots.
    /// <see cref="ProcessBuffer"/> is a no-op because OpenAL handles the DSP internally.
    /// Requires an <see cref="OpenALTransport"/> — EFX needs an active OpenAL context.
    /// </para>
    /// </summary>
    public sealed class OpenALEfxProcessor : IAudioEffectsProcessor
    {
        /// <summary>
        /// The underlying OpenAL EFX effect context.
        /// Exposes the full effect pool API (reverb, chorus, echo, etc.)
        /// for code that already uses <see cref="EffectContext"/> directly.
        /// </summary>
        public EffectContext? EffectContext { get; private set; }

        /// <summary>
        /// The EFX extension API. Exposed for <see cref="AudioSource"/> EFX property access.
        /// </summary>
        public EffectExtension? Api => _transport.EffectExtension;

        private readonly OpenALTransport _transport;

        public OpenALEfxProcessor(OpenALTransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        // --- IAudioEffectsProcessor: Lifecycle ---

        public void Initialize(AudioEffectsSettings settings)
        {
            if (_transport.EffectExtension is not null)
            {
                // EffectContext needs a ListenerContext reference — it will be set
                // during ListenerContext construction via SetListenerContext.
            }
        }

        /// <summary>
        /// Called by ListenerContext after construction to wire up the EffectContext
        /// that needs the listener reference for its resource pools.
        /// </summary>
        internal void SetListenerContext(ListenerContext listener)
        {
            if (_transport.EffectExtension is not null && EffectContext is null)
                EffectContext = new EffectContext(listener, _transport.EffectExtension);
        }

        public void Shutdown()
        {
            EffectContext = null;
        }

        // --- IAudioEffectsProcessor: Tick ---

        public void Tick(float deltaTime)
        {
            // OpenAL EFX effects run in-driver; nothing to tick.
        }

        // --- IAudioEffectsProcessor: Listener ---

        public void SetListenerPose(Vector3 position, Vector3 forward, Vector3 up)
        {
            // EFX doesn't need explicit listener pose — OpenAL tracks it via the transport.
        }

        // --- IAudioEffectsProcessor: Per-source ---

        public EffectsSourceHandle AddSource(AudioEffectsSourceSettings settings)
        {
            // EFX effects are applied via auxiliary sends on the OpenAL source.
            // No per-source registration needed at the processor level.
            return EffectsSourceHandle.Invalid;
        }

        public void RemoveSource(EffectsSourceHandle source)
        {
            // No-op for EFX — effect sends are managed at the source level.
        }

        public void SetSourcePose(EffectsSourceHandle source, Vector3 position, Vector3 forward)
        {
            // EFX source spatialization is handled by OpenAL source properties.
        }

        public void ProcessBuffer(EffectsSourceHandle source, ReadOnlySpan<float> input, Span<float> output, int channels, int sampleRate)
        {
            // No-op: OpenAL EFX applies effects in-driver via auxiliary send slots.
            // The transport handles the raw PCM directly.
            if (!input.IsEmpty && !output.IsEmpty)
                input.CopyTo(output);
        }

        // --- IAudioEffectsProcessor: Scene ---

        public bool SupportsSceneGeometry => false;
        public void SetScene(IAudioScene? scene) { }

        // --- IAudioEffectsProcessor: Capabilities ---

        public bool SupportsHRTF => false;
        public bool SupportsOcclusion => false;
        public bool SupportsReflections => true; // EAX Reverb provides environmental reflections
        public bool SupportsPathing => false;

        // --- IDisposable ---

        public void Dispose()
        {
            Shutdown();
            GC.SuppressFinalize(this);
        }
    }
}
