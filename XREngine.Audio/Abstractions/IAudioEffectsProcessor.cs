using System.Numerics;

namespace XREngine.Audio
{
    /// <summary>
    /// Abstraction over the audio DSP pipeline. Processes audio buffers
    /// before they reach the transport layer for final output.
    /// <para>
    /// Implementations: <c>OpenALEfxProcessor</c> (EFX applied in-driver, ProcessBuffer is no-op),
    /// <c>SteamAudioProcessor</c> (Phonon DSP chain), <c>PassthroughProcessor</c> (no-op).
    /// </para>
    /// </summary>
    public interface IAudioEffectsProcessor : IDisposable
    {
        // --- Lifecycle ---

        void Initialize(AudioEffectsSettings settings);
        void Shutdown();

        // --- Per-frame tick (run simulation, gather results) ---

        void Tick(float deltaTime);

        // --- Listener state (for processors that need listener pose) ---

        void SetListenerPose(Vector3 position, Vector3 forward, Vector3 up);

        // --- Per-source processing ---

        /// <summary>
        /// Register a source for processing. Returns an opaque handle the processor tracks.
        /// </summary>
        EffectsSourceHandle AddSource(AudioEffectsSourceSettings settings);

        /// <summary>
        /// Unregister a source. The handle becomes invalid after this call.
        /// </summary>
        void RemoveSource(EffectsSourceHandle source);

        /// <summary>
        /// Update source spatial state for simulation.
        /// </summary>
        void SetSourcePose(EffectsSourceHandle source, Vector3 position, Vector3 forward);

        /// <summary>
        /// Process a mono/stereo input buffer through the effect chain.
        /// <para>
        /// For <c>PassthroughProcessor</c>, copies input to output unchanged.
        /// For <c>OpenALEfxProcessor</c>, this is a no-op (effects applied in-transport via EFX slots).
        /// For <c>SteamAudioProcessor</c>, runs the full Phonon DSP chain.
        /// </para>
        /// </summary>
        void ProcessBuffer(EffectsSourceHandle source, ReadOnlySpan<float> input, Span<float> output, int channels, int sampleRate);

        // --- Scene geometry (only relevant for physics-based processors) ---

        bool SupportsSceneGeometry { get; }
        void SetScene(IAudioScene? scene);

        // --- Capabilities ---

        bool SupportsHRTF { get; }
        bool SupportsOcclusion { get; }
        bool SupportsReflections { get; }
        bool SupportsPathing { get; }
    }
}
