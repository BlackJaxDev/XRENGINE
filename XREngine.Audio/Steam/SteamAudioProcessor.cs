using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam
{
    /// <summary>
    /// Steam Audio (Phonon) implementation of <see cref="IAudioEffectsProcessor"/>.
    /// <para>
    /// Provides HRTF-based binaural spatialization and direct-path effects (distance
    /// attenuation, air absorption, directivity, occlusion) via the Phonon DSP chain.
    /// The processing pipeline per source is: mono input → <c>IPLDirectEffect</c> →
    /// <c>IPLBinauralEffect</c> → stereo output.
    /// </para>
    /// <para>
    /// This processor is designed to be composed with an <see cref="IAudioTransport"/>
    /// (typically <see cref="OpenALTransport"/>) via <see cref="ListenerContext"/>.
    /// Select it by setting <see cref="AudioManager.DefaultEffects"/> to
    /// <see cref="AudioEffectsType.SteamAudio"/> with <c>AudioArchitectureV2</c> enabled.
    /// </para>
    /// </summary>
    public sealed class SteamAudioProcessor : IAudioEffectsProcessor
    {
        // --- Global Phonon state ---
        private IPLContext _context;
        private IPLHRTF _hrtf;
        private IPLSimulator _simulator;
        private IPLAudioSettings _audioSettings;
        private bool _initialized;

        // --- Listener state ---
        private IPLCoordinateSpace3 _listenerCoords;

        // --- Per-source tracking ---
        private readonly Dictionary<uint, SourceChain> _sources = [];
        private uint _nextHandleId = 1;

        /// <summary>
        /// Returns true if the Steam Audio native library (phonon.dll) is loadable.
        /// </summary>
        public static bool IsNativeLibraryAvailable()
        {
            if (NativeLibrary.TryLoad("phonon", typeof(Phonon).Assembly, null, out var handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
            return false;
        }

        public SteamAudioProcessor()
        {
            // Probe for the native library immediately so that callers with try/catch
            // around construction (AudioManager.CreateSteamAudioProcessor) can fall back
            // before the processor is passed into ListenerContext.
            if (!IsNativeLibraryAvailable())
                throw new DllNotFoundException(
                    "Steam Audio native library (phonon.dll) is not available. " +
                    "Ensure phonon.dll is in the application directory or on the system PATH.");
        }

        /// <summary>
        /// Per-source effect chain: IPLSource → IPLDirectEffect → IPLBinauralEffect.
        /// Buffers are pre-allocated at initialization to avoid per-frame heap allocation.
        /// </summary>
        private sealed class SourceChain : IDisposable
        {
            public IPLSource Source;
            public IPLDirectEffect DirectEffect;
            public IPLBinauralEffect BinauralEffect;
            public IPLAudioBuffer InputBuffer;
            public IPLAudioBuffer DirectOutputBuffer;
            public IPLAudioBuffer BinauralOutputBuffer;
            public IPLCoordinateSpace3 SourceCoords;
            public IPLContext Context;
            public IPLSimulator Simulator;
            public bool Disposed;

            public void Dispose()
            {
                if (Disposed) return;
                Disposed = true;

                if (Source.Handle != IntPtr.Zero)
                {
                    Phonon.iplSourceRemove(Source, Simulator);
                    Phonon.iplSourceRelease(ref Source);
                }
                if (DirectEffect.Handle != IntPtr.Zero)
                    Phonon.iplDirectEffectRelease(ref DirectEffect);
                if (BinauralEffect.Handle != IntPtr.Zero)
                    Phonon.iplBinauralEffectRelease(ref BinauralEffect);

                if (InputBuffer.data != IntPtr.Zero)
                    Phonon.iplAudioBufferFree(Context, ref InputBuffer);
                if (DirectOutputBuffer.data != IntPtr.Zero)
                    Phonon.iplAudioBufferFree(Context, ref DirectOutputBuffer);
                if (BinauralOutputBuffer.data != IntPtr.Zero)
                    Phonon.iplAudioBufferFree(Context, ref BinauralOutputBuffer);
            }
        }

        // --- IAudioEffectsProcessor: Lifecycle ---

        public void Initialize(AudioEffectsSettings settings)
        {
            if (_initialized)
                return;

            // Create IPL context
            var contextSettings = new IPLContextSettings();
            var error = Phonon.iplContextCreate(ref contextSettings, ref _context);
            if (error != IPLerror.IPL_STATUS_SUCCESS)
                throw new InvalidOperationException($"Steam Audio: iplContextCreate failed with {error}. Is phonon.dll available?");

            // Audio settings — match engine sample rate and frame size
            _audioSettings = new IPLAudioSettings
            {
                samplingRate = settings.SampleRate,
                frameSize = settings.FrameSize,
            };

            // Create default HRTF
            var hrtfSettings = new IPLHRTFSettings
            {
                type = IPLHRTFType.IPL_HRTFTYPE_DEFAULT,
            };
            error = Phonon.iplHRTFCreate(_context, ref _audioSettings, ref hrtfSettings, ref _hrtf);
            if (error != IPLerror.IPL_STATUS_SUCCESS)
            {
                Phonon.iplContextRelease(ref _context);
                throw new InvalidOperationException($"Steam Audio: iplHRTFCreate failed with {error}.");
            }

            // Create simulator for direct sound simulation
            var simSettings = new IPLSimulationSettings
            {
                flags = IPLSimulationFlags.IPL_SIMULATIONFLAGS_DIRECT,
                sceneType = IPLSceneType.IPL_SCENETYPE_DEFAULT,
                maxNumSources = 256,
                samplingRate = settings.SampleRate,
                frameSize = settings.FrameSize,
            };
            error = Phonon.iplSimulatorCreate(_context, ref simSettings, ref _simulator);
            if (error != IPLerror.IPL_STATUS_SUCCESS)
            {
                Phonon.iplHRTFRelease(ref _hrtf);
                Phonon.iplContextRelease(ref _context);
                throw new InvalidOperationException($"Steam Audio: iplSimulatorCreate failed with {error}.");
            }

            _listenerCoords = new IPLCoordinateSpace3
            {
                ahead = new IPLVector3(0, 0, -1),
                up = new IPLVector3(0, 1, 0),
                right = new IPLVector3(1, 0, 0),
                origin = default,
            };

            _initialized = true;
            Debug.WriteLine($"[SteamAudioProcessor] Initialized (sampleRate={settings.SampleRate}, frameSize={settings.FrameSize}).");
        }

        public void Shutdown()
        {
            if (!_initialized)
                return;

            // Dispose all remaining source chains
            foreach (var chain in _sources.Values)
                chain.Dispose();
            _sources.Clear();

            if (_simulator.Handle != IntPtr.Zero)
                Phonon.iplSimulatorRelease(ref _simulator);
            if (_hrtf.Handle != IntPtr.Zero)
                Phonon.iplHRTFRelease(ref _hrtf);
            if (_context.Handle != IntPtr.Zero)
                Phonon.iplContextRelease(ref _context);

            _initialized = false;
            Debug.WriteLine("[SteamAudioProcessor] Shut down.");
        }

        // --- IAudioEffectsProcessor: Tick ---

        public void Tick(float deltaTime)
        {
            if (!_initialized)
                return;

            // Update shared listener state for the simulator
            var sharedInputs = new IPLSimulationSharedInputs
            {
                listener = _listenerCoords,
            };
            Phonon.iplSimulatorSetSharedInputs(
                _simulator,
                IPLSimulationFlags.IPL_SIMULATIONFLAGS_DIRECT,
                ref sharedInputs);

            // Commit pending source changes and run direct simulation
            Phonon.iplSimulatorCommit(_simulator);
            Phonon.iplSimulatorRunDirect(_simulator);
        }

        // --- IAudioEffectsProcessor: Listener ---

        public void SetListenerPose(Vector3 position, Vector3 forward, Vector3 up)
        {
            // Compute right from forward × up
            var right = Vector3.Cross(forward, up);

            _listenerCoords = new IPLCoordinateSpace3
            {
                ahead = forward,
                up = up,
                right = right,
                origin = position,
            };
        }

        // --- IAudioEffectsProcessor: Per-source ---

        public EffectsSourceHandle AddSource(AudioEffectsSourceSettings settings)
        {
            if (!_initialized)
                return EffectsSourceHandle.Invalid;

            var handleId = _nextHandleId++;
            var chain = new SourceChain { Context = _context, Simulator = _simulator };

            try
            {
                // Create IPL source for simulation
                var sourceSettings = new IPLSourceSettings
                {
                    flags = IPLSimulationFlags.IPL_SIMULATIONFLAGS_DIRECT,
                };
                var error = Phonon.iplSourceCreate(_simulator, ref sourceSettings, ref chain.Source);
                if (error != IPLerror.IPL_STATUS_SUCCESS)
                    throw new InvalidOperationException($"iplSourceCreate failed: {error}");

                Phonon.iplSourceAdd(chain.Source, _simulator);

                // Set initial source inputs
                chain.SourceCoords = new IPLCoordinateSpace3
                {
                    ahead = settings.Forward,
                    up = new IPLVector3(0, 1, 0),
                    right = Vector3.Cross(settings.Forward, Vector3.UnitY),
                    origin = settings.Position,
                };

                var inputs = new IPLSimulationInputs
                {
                    flags = IPLSimulationFlags.IPL_SIMULATIONFLAGS_DIRECT,
                    directFlags = IPLDirectSimulationFlags.DistanceAttenuation
                                | IPLDirectSimulationFlags.AirAbsorption
                                | IPLDirectSimulationFlags.Directivity
                                | IPLDirectSimulationFlags.Occlusion,
                    source = chain.SourceCoords,
                };
                Phonon.iplSourceSetInputs(chain.Source, IPLSimulationFlags.IPL_SIMULATIONFLAGS_DIRECT, ref inputs);

                // Create direct effect (mono → mono)
                var directSettings = new IPLDirectEffectSettings { numChannels = 1 };
                error = Phonon.iplDirectEffectCreate(_context, ref _audioSettings, ref directSettings, ref chain.DirectEffect);
                if (error != IPLerror.IPL_STATUS_SUCCESS)
                    throw new InvalidOperationException($"iplDirectEffectCreate failed: {error}");

                // Create binaural effect (mono → stereo HRTF)
                var binauralSettings = new IPLBinauralEffectSettings { hrtf = _hrtf };
                error = Phonon.iplBinauralEffectCreate(_context, ref _audioSettings, ref binauralSettings, ref chain.BinauralEffect);
                if (error != IPLerror.IPL_STATUS_SUCCESS)
                    throw new InvalidOperationException($"iplBinauralEffectCreate failed: {error}");

                // Pre-allocate audio buffers: avoid per-frame allocation
                error = Phonon.iplAudioBufferAllocate(_context, 1, _audioSettings.frameSize, ref chain.InputBuffer);
                if (error != IPLerror.IPL_STATUS_SUCCESS)
                    throw new InvalidOperationException($"iplAudioBufferAllocate (input) failed: {error}");

                error = Phonon.iplAudioBufferAllocate(_context, 1, _audioSettings.frameSize, ref chain.DirectOutputBuffer);
                if (error != IPLerror.IPL_STATUS_SUCCESS)
                    throw new InvalidOperationException($"iplAudioBufferAllocate (direct output) failed: {error}");

                error = Phonon.iplAudioBufferAllocate(_context, 2, _audioSettings.frameSize, ref chain.BinauralOutputBuffer);
                if (error != IPLerror.IPL_STATUS_SUCCESS)
                    throw new InvalidOperationException($"iplAudioBufferAllocate (binaural output) failed: {error}");
            }
            catch
            {
                chain.Dispose();
                throw;
            }

            _sources[handleId] = chain;
            return new EffectsSourceHandle(handleId);
        }

        public void RemoveSource(EffectsSourceHandle source)
        {
            if (!source.IsValid || !_sources.Remove(source.Id, out var chain))
                return;

            chain.Dispose();
        }

        public void SetSourcePose(EffectsSourceHandle source, Vector3 position, Vector3 forward)
        {
            if (!_initialized || !source.IsValid || !_sources.TryGetValue(source.Id, out var chain))
                return;

            var up = Vector3.UnitY;
            var right = Vector3.Cross(forward, up);
            if (right.LengthSquared() < 1e-6f)
            {
                // forward is nearly parallel to up — pick an alternate up
                up = Vector3.UnitX;
                right = Vector3.Cross(forward, up);
            }
            right = Vector3.Normalize(right);

            chain.SourceCoords = new IPLCoordinateSpace3
            {
                ahead = forward,
                up = up,
                right = right,
                origin = position,
            };

            var inputs = new IPLSimulationInputs
            {
                flags = IPLSimulationFlags.IPL_SIMULATIONFLAGS_DIRECT,
                directFlags = IPLDirectSimulationFlags.DistanceAttenuation
                            | IPLDirectSimulationFlags.AirAbsorption
                            | IPLDirectSimulationFlags.Directivity
                            | IPLDirectSimulationFlags.Occlusion,
                source = chain.SourceCoords,
            };
            Phonon.iplSourceSetInputs(chain.Source, IPLSimulationFlags.IPL_SIMULATIONFLAGS_DIRECT, ref inputs);
        }

        // --- IAudioEffectsProcessor: ProcessBuffer ---

        public void ProcessBuffer(
            EffectsSourceHandle source,
            ReadOnlySpan<float> input,
            Span<float> output,
            int channels,
            int sampleRate)
        {
            // Fallback: pass through if not initialized, invalid handle, or empty buffers
            if (!_initialized || !source.IsValid || input.IsEmpty || output.IsEmpty)
            {
                if (!input.IsEmpty && !output.IsEmpty)
                    input.CopyTo(output);
                return;
            }

            if (!_sources.TryGetValue(source.Id, out var chain))
            {
                input.CopyTo(output);
                return;
            }

            int frameSize = _audioSettings.frameSize;

            // Deinterleave mono input into the IPL input buffer
            unsafe
            {
                float* inputData = (float*)chain.InputBuffer.data;
                int samplesToCopy = Math.Min(input.Length, frameSize);
                input[..samplesToCopy].CopyTo(new Span<float>(inputData, samplesToCopy));
                // Zero-fill remainder if input is shorter than frame
                if (samplesToCopy < frameSize)
                    new Span<float>(inputData + samplesToCopy, frameSize - samplesToCopy).Clear();
            }

            // Get simulation outputs for this source
            var simOutputs = new IPLSimulationOutputs();
            Phonon.iplSourceGetOutputs(chain.Source, IPLSimulationFlags.IPL_SIMULATIONFLAGS_DIRECT, ref simOutputs);

            // Apply direct effect (distance attenuation, air absorption, occlusion)
            var directParams = simOutputs.direct;
            directParams.flags = IPLDirectEffectFlags.IPL_DIRECTEFFECTFLAGS_APPLYDISTANCEATTENUATION
                               | IPLDirectEffectFlags.IPL_DIRECTEFFECTFLAGS_APPLYAIRABSORPTION;

            Phonon.iplDirectEffectApply(
                chain.DirectEffect,
                ref directParams,
                ref chain.InputBuffer,
                ref chain.DirectOutputBuffer);

            // Compute direction from listener to source for HRTF
            var direction = _context.CalculateRelativeDirection(
                chain.SourceCoords.origin,
                _listenerCoords.origin,
                _listenerCoords.ahead,
                _listenerCoords.up);

            // Apply binaural HRTF effect (mono → stereo)
            var binauralParams = new IPLBinauralEffectParams
            {
                direction = direction,
                interpolation = IPLHRTFInterpolation.IPL_HRTFINTERPOLATION_BILINEAR,
                spatialBlend = 1.0f,
                hrtf = _hrtf,
                peakDelays = IntPtr.Zero,
            };

            Phonon.iplBinauralEffectApply(
                chain.BinauralEffect,
                ref binauralParams,
                ref chain.DirectOutputBuffer,
                ref chain.BinauralOutputBuffer);

            // Interleave stereo output back into the output span
            int outputSamples = Math.Min(output.Length / 2, frameSize);
            unsafe
            {
                // IPLAudioBuffer stores channels as separate contiguous arrays.
                // data is a pointer to an array of float* (one per channel).
                float** channelPtrs = (float**)chain.BinauralOutputBuffer.data;
                float* left = channelPtrs[0];
                float* right = channelPtrs[1];

                for (int i = 0; i < outputSamples; i++)
                {
                    output[i * 2] = left[i];
                    output[i * 2 + 1] = right[i];
                }
            }
        }

        // --- IAudioEffectsProcessor: Scene ---

        public bool SupportsSceneGeometry => true;

        public void SetScene(IAudioScene? scene)
        {
            // Scene geometry integration is Phase 4. For now, we support the capability
            // flag but do not wire scene data into the simulator.
            // When Phase 4 lands, this will call iplSimulatorSetScene with the IPLScene handle.
        }

        // --- IAudioEffectsProcessor: Capabilities ---

        public bool SupportsHRTF => true;
        public bool SupportsOcclusion => true;
        public bool SupportsReflections => false; // Phase 5: reflection simulation
        public bool SupportsPathing => false;      // Phase 5: pathing simulation

        // --- IDisposable ---

        public void Dispose()
        {
            Shutdown();
            GC.SuppressFinalize(this);
        }
    }
}
