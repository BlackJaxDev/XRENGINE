using System.Numerics;

namespace XREngine.Audio
{
    /// <summary>
    /// No-op implementation of <see cref="IAudioEffectsProcessor"/>.
    /// <para>
    /// <see cref="ProcessBuffer"/> copies input to output unchanged.
    /// All spatial/environmental methods are no-ops. This is useful when the transport
    /// layer already handles spatialization (e.g. OpenAL distance model) and no additional
    /// DSP effects are desired, or as a fallback when no other processor is available.
    /// </para>
    /// </summary>
    public sealed class PassthroughProcessor : IAudioEffectsProcessor
    {
        private uint _nextHandleId = 1;

        // --- IAudioEffectsProcessor: Lifecycle ---

        public void Initialize(AudioEffectsSettings settings)
        {
            // Nothing to initialize for passthrough.
        }

        public void Shutdown()
        {
            // Nothing to tear down.
        }

        // --- IAudioEffectsProcessor: Tick ---

        public void Tick(float deltaTime)
        {
            // No simulation to step.
        }

        // --- IAudioEffectsProcessor: Listener ---

        public void SetListenerPose(Vector3 position, Vector3 forward, Vector3 up)
        {
            // No listener tracking needed for passthrough.
        }

        // --- IAudioEffectsProcessor: Per-source ---

        public EffectsSourceHandle AddSource(AudioEffectsSourceSettings settings)
        {
            // Return a valid handle so callers that track sources still work.
            return new EffectsSourceHandle(_nextHandleId++);
        }

        public void RemoveSource(EffectsSourceHandle source)
        {
            // No per-source state to clean up.
        }

        public void SetSourcePose(EffectsSourceHandle source, Vector3 position, Vector3 forward)
        {
            // No spatial processing.
        }

        public void ProcessBuffer(EffectsSourceHandle source, ReadOnlySpan<float> input, Span<float> output, int channels, int sampleRate)
        {
            // Copy input to output unchanged.
            if (!input.IsEmpty && !output.IsEmpty)
                input.CopyTo(output);
        }

        // --- IAudioEffectsProcessor: Scene ---

        public bool SupportsSceneGeometry => false;
        public void SetScene(IAudioScene? scene) { }

        // --- IAudioEffectsProcessor: Capabilities ---

        public bool SupportsHRTF => false;
        public bool SupportsOcclusion => false;
        public bool SupportsReflections => false;
        public bool SupportsPathing => false;

        // --- IDisposable ---

        public void Dispose()
        {
            Shutdown();
            GC.SuppressFinalize(this);
        }
    }
}
