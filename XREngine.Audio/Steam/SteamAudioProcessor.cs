using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam
{
    /// <summary>
    /// Steam Audio (Phonon) implementation of <see cref="IAudioEffectsProcessor"/>.
    /// <para>
    /// Provides HRTF-based binaural spatialization, direct-path effects (distance attenuation,
    /// air absorption, directivity, occlusion), reflection simulation (convolution reverb or
    /// parametric reverb), and pathing simulation via the Phonon DSP chain.
    /// </para>
    /// <para>
    /// The processing pipeline per source is:
    /// <list type="bullet">
    ///   <item>Direct path: mono input → <c>IPLDirectEffect</c> → <c>IPLBinauralEffect</c> → stereo</item>
    ///   <item>Reflections: mono input → <c>IPLReflectionEffect</c> → ambisonics → <c>IPLAmbisonicsDecodeEffect</c> → stereo (mixed)</item>
    ///   <item>Pathing: mono input → <c>IPLPathEffect</c> → stereo (mixed)</item>
    /// </list>
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
        private SteamAudioScene? _scene;
        private bool _initialized;

        // --- Reflection simulation global state ---
        private IPLReflectionMixer _reflectionMixer;
        private IPLReflectionEffectSettings _reflectionEffectSettings;
        private readonly List<SteamAudioProbeBatch> _probeBatches = [];

        // --- Simulation configuration ---
        /// <summary>Maximum ambisonics order for reflection/pathing IRs. Default: 1 (4 channels).</summary>
        public int MaxAmbisonicsOrder { get; set; } = 1;

        /// <summary>Number of rays for real-time reflection simulation. Default: 4096.</summary>
        public int ReflectionRays { get; set; } = 4096;

        /// <summary>Number of ray bounces for reflections. Default: 8.</summary>
        public int ReflectionBounces { get; set; } = 8;

        /// <summary>Duration in seconds of reflection IRs. Default: 1.0.</summary>
        public float ReflectionDuration { get; set; } = 1.0f;

        /// <summary>Reflection effect algorithm. Default: Convolution.</summary>
        public IPLReflectionEffectType ReflectionType { get; set; } = IPLReflectionEffectType.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION;

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
        /// Per-source effect chain: IPLSource → IPLDirectEffect → IPLBinauralEffect (direct path),
        /// plus IPLReflectionEffect → IPLAmbisonicsDecodeEffect (reflections)
        /// and IPLPathEffect (pathing). All buffers are pre-allocated to avoid per-frame heap allocation.
        /// </summary>
        private sealed class SourceChain : IDisposable
        {
            // --- Direct path ---
            public IPLSource Source;
            public IPLDirectEffect DirectEffect;
            public IPLBinauralEffect BinauralEffect;
            public IPLAudioBuffer InputBuffer;
            public IPLAudioBuffer DirectOutputBuffer;
            public IPLAudioBuffer BinauralOutputBuffer;

            // --- Reflections ---
            public IPLReflectionEffect ReflectionEffect;
            public IPLAmbisonicsDecodeEffect AmbisonicsDecodeEffect;
            public IPLAudioBuffer ReflectionOutputBuffer;  // ambisonics channels
            public IPLAudioBuffer ReflectionDecodedBuffer; // stereo

            // --- Pathing ---
            public IPLPathEffect PathEffect;
            public IPLAudioBuffer PathOutputBuffer; // stereo (when spatialize=true)

            // --- Shared ---
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

                // Direct chain
                if (DirectEffect.Handle != IntPtr.Zero)
                    Phonon.iplDirectEffectRelease(ref DirectEffect);
                if (BinauralEffect.Handle != IntPtr.Zero)
                    Phonon.iplBinauralEffectRelease(ref BinauralEffect);

                // Reflection chain
                if (ReflectionEffect.Handle != IntPtr.Zero)
                    Phonon.iplReflectionEffectRelease(ref ReflectionEffect);
                if (AmbisonicsDecodeEffect.Handle != IntPtr.Zero)
                    Phonon.iplAmbisonicsDecodeEffectRelease(ref AmbisonicsDecodeEffect);

                // Path chain
                if (PathEffect.Handle != IntPtr.Zero)
                    Phonon.iplPathEffectRelease(ref PathEffect);

                // Buffers
                if (InputBuffer.data != IntPtr.Zero)
                    Phonon.iplAudioBufferFree(Context, ref InputBuffer);
                if (DirectOutputBuffer.data != IntPtr.Zero)
                    Phonon.iplAudioBufferFree(Context, ref DirectOutputBuffer);
                if (BinauralOutputBuffer.data != IntPtr.Zero)
                    Phonon.iplAudioBufferFree(Context, ref BinauralOutputBuffer);
                if (ReflectionOutputBuffer.data != IntPtr.Zero)
                    Phonon.iplAudioBufferFree(Context, ref ReflectionOutputBuffer);
                if (ReflectionDecodedBuffer.data != IntPtr.Zero)
                    Phonon.iplAudioBufferFree(Context, ref ReflectionDecodedBuffer);
                if (PathOutputBuffer.data != IntPtr.Zero)
                    Phonon.iplAudioBufferFree(Context, ref PathOutputBuffer);
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
                volume = 1.0f,
                normType = IPLHRTFNormType.IPL_HRTFNORMTYPE_NONE,
            };
            error = Phonon.iplHRTFCreate(_context, ref _audioSettings, ref hrtfSettings, ref _hrtf);
            if (error != IPLerror.IPL_STATUS_SUCCESS)
            {
                Phonon.iplContextRelease(ref _context);
                throw new InvalidOperationException($"Steam Audio: iplHRTFCreate failed with {error}.");
            }

            // Create simulator for direct + reflection + pathing simulation
            var simSettings = new IPLSimulationSettings
            {
                flags = IPLSimulationFlags.IPL_SIMULATIONFLAGS_DIRECT
                      | IPLSimulationFlags.IPL_SIMULATIONFLAGS_REFLECTIONS
                      | IPLSimulationFlags.IPL_SIMULATIONFLAGS_PATHING,
                sceneType = IPLSceneType.IPL_SCENETYPE_DEFAULT,
                reflectionType = ReflectionType,
                maxNumOcclusionSamples = 16,
                maxNumRays = Math.Max(ReflectionRays, 1024),
                numDiffuseSamples = 32,
                maxDuration = Math.Max(ReflectionDuration, 1.0f),
                maxOrder = MaxAmbisonicsOrder,
                maxNumSources = 256,
                numThreads = Math.Max(1, Environment.ProcessorCount / 2),
                rayBatchSize = 64,
                numVisSamples = 8,
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

            // Create reflection effect settings (shared by all per-source reflection effects)
            int ambiChannels = (MaxAmbisonicsOrder + 1) * (MaxAmbisonicsOrder + 1);
            int irSize = (int)(Math.Max(ReflectionDuration, 1.0f) * settings.SampleRate);
            _reflectionEffectSettings = new IPLReflectionEffectSettings
            {
                type = ReflectionType,
                irSize = irSize,
                numChannels = ambiChannels,
            };

            // Create global reflection mixer
            error = Phonon.iplReflectionMixerCreate(_context, ref _audioSettings, ref _reflectionEffectSettings, ref _reflectionMixer);
            if (error != IPLerror.IPL_STATUS_SUCCESS)
            {
                Phonon.iplSimulatorRelease(ref _simulator);
                Phonon.iplHRTFRelease(ref _hrtf);
                Phonon.iplContextRelease(ref _context);
                throw new InvalidOperationException($"Steam Audio: iplReflectionMixerCreate failed with {error}.");
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

            // Detach probe batches
            foreach (var pb in _probeBatches)
            {
                if (_simulator.Handle != IntPtr.Zero)
                    Phonon.iplSimulatorRemoveProbeBatch(_simulator, pb.Handle);
            }
            _probeBatches.Clear();

            // Detach scene before releasing simulator
            _scene = null;

            // Release reflection mixer
            if (_reflectionMixer.Handle != IntPtr.Zero)
                Phonon.iplReflectionMixerRelease(ref _reflectionMixer);

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

            // Determine which simulation types to run.
            // Reflections require a committed scene; pathing additionally
            // requires at least one probe batch.  Running either without
            // the prerequisite causes a native access-violation crash.
            var flags = ActiveSimFlags;
            bool hasScene = _scene != null;
            bool hasProbes = _probeBatches.Count > 0;

            // Update shared listener state for the simulator
            var sharedInputs = new IPLSimulationSharedInputs
            {
                listener = _listenerCoords,
                numRays = ReflectionRays,
                numBounces = ReflectionBounces,
                duration = ReflectionDuration,
                order = MaxAmbisonicsOrder,
                irradianceMinDistance = 1.0f,
            };
            Phonon.iplSimulatorSetSharedInputs(
                _simulator,
                flags,
                ref sharedInputs);

            // Commit pending source changes and run simulation types
            Phonon.iplSimulatorCommit(_simulator);
            Phonon.iplSimulatorRunDirect(_simulator);

            if (hasScene)
                Phonon.iplSimulatorRunReflections(_simulator);
            if (hasScene && hasProbes)
                Phonon.iplSimulatorRunPathing(_simulator);
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
                // Create source with all simulation capabilities
                var simFlags = IPLSimulationFlags.IPL_SIMULATIONFLAGS_DIRECT
                             | IPLSimulationFlags.IPL_SIMULATIONFLAGS_REFLECTIONS
                             | IPLSimulationFlags.IPL_SIMULATIONFLAGS_PATHING;

                // Create IPL source for all simulation types
                var sourceSettings = new IPLSourceSettings
                {
                    flags = simFlags,
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

                SetSourceInputs(chain, ActiveSimFlags);

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

                // Create reflection effect (mono → ambisonics)
                error = Phonon.iplReflectionEffectCreate(_context, ref _audioSettings, ref _reflectionEffectSettings, ref chain.ReflectionEffect);
                if (error != IPLerror.IPL_STATUS_SUCCESS)
                    throw new InvalidOperationException($"iplReflectionEffectCreate failed: {error}");

                // Create ambisonics decode effect (ambisonics → stereo binaural)
                var decodeSettings = new IPLAmbisonicsDecodeEffectSettings
                {
                    speakerLayout = new IPLSpeakerLayout { type = IPLSpeakerLayoutType.IPL_SPEAKERLAYOUTTYPE_STEREO },
                    hrtf = _hrtf,
                    maxOrder = MaxAmbisonicsOrder,
                };
                error = Phonon.iplAmbisonicsDecodeEffectCreate(_context, ref _audioSettings, ref decodeSettings, ref chain.AmbisonicsDecodeEffect);
                if (error != IPLerror.IPL_STATUS_SUCCESS)
                    throw new InvalidOperationException($"iplAmbisonicsDecodeEffectCreate failed: {error}");

                // Create path effect (spatialized binaural output)
                var pathSettings = new IPLPathEffectSettings
                {
                    maxOrder = MaxAmbisonicsOrder,
                    spatialize = IPLbool.IPL_TRUE,
                    speakerLayout = new IPLSpeakerLayout { type = IPLSpeakerLayoutType.IPL_SPEAKERLAYOUTTYPE_STEREO },
                    hrtf = _hrtf,
                };
                error = Phonon.iplPathEffectCreate(_context, ref _audioSettings, ref pathSettings, ref chain.PathEffect);
                if (error != IPLerror.IPL_STATUS_SUCCESS)
                    throw new InvalidOperationException($"iplPathEffectCreate failed: {error}");

                // Pre-allocate audio buffers: avoid per-frame allocation
                int ambiChannels = (MaxAmbisonicsOrder + 1) * (MaxAmbisonicsOrder + 1);

                error = Phonon.iplAudioBufferAllocate(_context, 1, _audioSettings.frameSize, ref chain.InputBuffer);
                if (error != IPLerror.IPL_STATUS_SUCCESS)
                    throw new InvalidOperationException($"iplAudioBufferAllocate (input) failed: {error}");

                error = Phonon.iplAudioBufferAllocate(_context, 1, _audioSettings.frameSize, ref chain.DirectOutputBuffer);
                if (error != IPLerror.IPL_STATUS_SUCCESS)
                    throw new InvalidOperationException($"iplAudioBufferAllocate (direct output) failed: {error}");

                error = Phonon.iplAudioBufferAllocate(_context, 2, _audioSettings.frameSize, ref chain.BinauralOutputBuffer);
                if (error != IPLerror.IPL_STATUS_SUCCESS)
                    throw new InvalidOperationException($"iplAudioBufferAllocate (binaural output) failed: {error}");

                error = Phonon.iplAudioBufferAllocate(_context, ambiChannels, _audioSettings.frameSize, ref chain.ReflectionOutputBuffer);
                if (error != IPLerror.IPL_STATUS_SUCCESS)
                    throw new InvalidOperationException($"iplAudioBufferAllocate (reflection output) failed: {error}");

                error = Phonon.iplAudioBufferAllocate(_context, 2, _audioSettings.frameSize, ref chain.ReflectionDecodedBuffer);
                if (error != IPLerror.IPL_STATUS_SUCCESS)
                    throw new InvalidOperationException($"iplAudioBufferAllocate (reflection decoded) failed: {error}");

                error = Phonon.iplAudioBufferAllocate(_context, 2, _audioSettings.frameSize, ref chain.PathOutputBuffer);
                if (error != IPLerror.IPL_STATUS_SUCCESS)
                    throw new InvalidOperationException($"iplAudioBufferAllocate (path output) failed: {error}");
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

            SetSourceInputs(chain, ActiveSimFlags);
        }

        /// <summary>
        /// Computes the simulation flags that are currently safe to use based on
        /// whether a scene and/or probe batches are present.
        /// </summary>
        private IPLSimulationFlags ActiveSimFlags
        {
            get
            {
                var f = IPLSimulationFlags.IPL_SIMULATIONFLAGS_DIRECT;
                if (_scene != null)
                    f |= IPLSimulationFlags.IPL_SIMULATIONFLAGS_REFLECTIONS;
                if (_scene != null && _probeBatches.Count > 0)
                    f |= IPLSimulationFlags.IPL_SIMULATIONFLAGS_PATHING;
                return f;
            }
        }

        /// <summary>
        /// Sets simulation inputs for a source with the appropriate flags for all active simulation types.
        /// </summary>
        private void SetSourceInputs(SourceChain chain, IPLSimulationFlags flags)
        {
            var inputs = new IPLSimulationInputs
            {
                flags = flags,
                directFlags = IPLDirectSimulationFlags.DistanceAttenuation
                            | IPLDirectSimulationFlags.AirAbsorption
                            | IPLDirectSimulationFlags.Directivity
                            | IPLDirectSimulationFlags.Occlusion,
                source = chain.SourceCoords,
                distanceAttenuationModel = new IPLDistanceAttenuationModel
                {
                    type = IPLDistanceAttenuationModelType.IPL_DISTANCEATTENUATIONTYPE_DEFAULT,
                },
                airAbsorptionModel = new IPLAirAbsorptionModel
                {
                    type = IPLAirAbsorptionModelType.IPL_AIRABSORPTIONTYPE_DEFAULT,
                },
                directivity = new IPLDirectivity
                {
                    dipoleWeight = 0.0f,
                    dipolePower = 1.0f,
                },
                occlusionType = IPLOcclusionType.IPL_OCCLUSIONTYPE_RAYCAST,
                occlusionRadius = 1.0f,
                numOcclusionSamples = 1,
                numTransmissionRays = 1,
                // Reflection inputs
                reverbScale = [1.0f, 1.0f, 1.0f],
                hybridReverbTransitionTime = 1.0f,
                hybridReverbOverlapPercent = 0.25f,
                baked = IPLbool.IPL_FALSE,
                // Pathing inputs
                pathingOrder = MaxAmbisonicsOrder,
                enableValidation = IPLbool.IPL_TRUE,
                findAlternatePaths = IPLbool.IPL_TRUE,
                visRadius = 1.0f,
                visThreshold = 0.1f,
                visRange = 50.0f,
            };

            // If we have a probe batch with pathing data, set it on the source inputs
            if (_probeBatches.Count > 0)
                inputs.pathingProbes = _probeBatches[0].Handle;

            Phonon.iplSourceSetInputs(chain.Source, flags, ref inputs);
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
                float** inputChannels = (float**)chain.InputBuffer.data;
                float* inputData = inputChannels[0];
                int samplesToCopy = Math.Min(input.Length, frameSize);
                input[..samplesToCopy].CopyTo(new Span<float>(inputData, samplesToCopy));
                // Zero-fill remainder if input is shorter than frame
                if (samplesToCopy < frameSize)
                    new Span<float>(inputData + samplesToCopy, frameSize - samplesToCopy).Clear();
            }

            // Get simulation outputs — only request reflection/pathing when prerequisites are met
            bool hasScene = _scene != null;
            bool hasProbes = _probeBatches.Count > 0;
            var allFlags = IPLSimulationFlags.IPL_SIMULATIONFLAGS_DIRECT;
            if (hasScene)
                allFlags |= IPLSimulationFlags.IPL_SIMULATIONFLAGS_REFLECTIONS;
            if (hasScene && hasProbes)
                allFlags |= IPLSimulationFlags.IPL_SIMULATIONFLAGS_PATHING;
            var simOutputs = new IPLSimulationOutputs();
            Phonon.iplSourceGetOutputs(chain.Source, allFlags, ref simOutputs);

            // Apply direct effect (distance attenuation, air absorption, occlusion)
            var directParams = simOutputs.direct;
            directParams.flags = IPLDirectEffectFlags.IPL_DIRECTEFFECTFLAGS_APPLYDISTANCEATTENUATION
                               | IPLDirectEffectFlags.IPL_DIRECTEFFECTFLAGS_APPLYAIRABSORPTION
                               | IPLDirectEffectFlags.IPL_DIRECTEFFECTFLAGS_APPLYOCCLUSION
                               | IPLDirectEffectFlags.IPL_DIRECTEFFECTFLAGS_APPLYTRANSMISSION;

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

            // === Reflection effect (mono → ambisonics → stereo) ===
            // Requires a committed scene for the IR data.
            if (hasScene)
            {
                var reflParams = simOutputs.reflections;
                reflParams.type = ReflectionType;
                int ambiChannels = (MaxAmbisonicsOrder + 1) * (MaxAmbisonicsOrder + 1);
                reflParams.numChannels = ambiChannels;
                reflParams.irSize = _reflectionEffectSettings.irSize;

                Phonon.iplReflectionEffectApply(
                    chain.ReflectionEffect,
                    ref reflParams,
                    ref chain.InputBuffer,
                    ref chain.ReflectionOutputBuffer,
                    _reflectionMixer);

                // Decode ambisonics reflection output to binaural stereo
                var decodeParams = new IPLAmbisonicsDecodeEffectParams
                {
                    order = MaxAmbisonicsOrder,
                    hrtf = _hrtf,
                    orientation = _listenerCoords,
                    binaural = IPLbool.IPL_TRUE,
                };

                Phonon.iplAmbisonicsDecodeEffectApply(
                    chain.AmbisonicsDecodeEffect,
                    ref decodeParams,
                    ref chain.ReflectionOutputBuffer,
                    ref chain.ReflectionDecodedBuffer);
            }

            // === Path effect (mono → stereo spatialized) ===
            // Requires both a scene and at least one probe batch.
            if (hasScene && hasProbes)
            {
                var pathParams = simOutputs.pathing;
                pathParams.order = MaxAmbisonicsOrder;
                pathParams.binaural = IPLbool.IPL_TRUE;
                pathParams.hrtf = _hrtf;
                pathParams.listener = _listenerCoords;

                Phonon.iplPathEffectApply(
                    chain.PathEffect,
                    ref pathParams,
                    ref chain.InputBuffer,
                    ref chain.PathOutputBuffer);
            }

            // === Mix direct + reflection + pathing into output ===
            int outputSamples = Math.Min(output.Length / 2, frameSize);
            unsafe
            {
                float** directPtrs = (float**)chain.BinauralOutputBuffer.data;
                float* directL = directPtrs[0];
                float* directR = directPtrs[1];

                float* reflL = null, reflR = null;
                float* pathL = null, pathR = null;

                if (hasScene)
                {
                    float** reflPtrs = (float**)chain.ReflectionDecodedBuffer.data;
                    reflL = reflPtrs[0];
                    reflR = reflPtrs[1];
                }
                if (hasScene && hasProbes)
                {
                    float** pathPtrs = (float**)chain.PathOutputBuffer.data;
                    pathL = pathPtrs[0];
                    pathR = pathPtrs[1];
                }

                for (int i = 0; i < outputSamples; i++)
                {
                    float l = directL[i];
                    float r = directR[i];
                    if (reflL != null) { l += reflL[i]; r += reflR[i]; }
                    if (pathL != null) { l += pathL[i]; r += pathR[i]; }
                    output[i * 2]     = l;
                    output[i * 2 + 1] = r;
                }
            }
        }

        // --- IAudioEffectsProcessor: Scene ---

        public bool SupportsSceneGeometry => true;

        public void SetScene(IAudioScene? scene)
        {
            if (!_initialized)
                return;

            if (scene is null)
            {
                // Detach scene from simulator — occlusion rays will no longer hit geometry
                _scene = null;
                // Phonon requires a valid scene handle; passing a zeroed handle effectively detaches.
                // The simulator will skip geometry-based effects (occlusion falls back to 1.0).
                return;
            }

            if (scene is not SteamAudioScene steamScene)
                throw new ArgumentException(
                    $"SteamAudioProcessor requires a {nameof(SteamAudioScene)}, got {scene.GetType().Name}.",
                    nameof(scene));

            if (!steamScene.IsCommitted)
                throw new InvalidOperationException(
                    "Scene must be committed before attaching to the processor. Call scene.Commit() first.");

            _scene = steamScene;
            Phonon.iplSimulatorSetScene(_simulator, steamScene.Handle);
            // Re-commit the simulator so it picks up the new scene immediately
            Phonon.iplSimulatorCommit(_simulator);

            Debug.WriteLine("[SteamAudioProcessor] Scene attached to simulator.");
        }

        // --- IAudioEffectsProcessor: Capabilities ---

        public bool SupportsHRTF => true;
        public bool SupportsOcclusion => true;
        public bool SupportsReflections => true;
        public bool SupportsPathing => true;

        // --- Scene factory ---

        /// <summary>
        /// Creates a new <see cref="SteamAudioScene"/> bound to this processor's Phonon context.
        /// The caller owns the returned scene and must dispose it when done.
        /// </summary>
        /// <param name="sceneType">Ray-tracer backend to use (default: built-in).</param>
        public SteamAudioScene CreateScene(IPLSceneType sceneType = IPLSceneType.IPL_SCENETYPE_DEFAULT)
        {
            if (!_initialized)
                throw new InvalidOperationException("Processor must be initialized before creating a scene.");

            return new SteamAudioScene(_context, sceneType);
        }

        /// <summary>The scene currently attached to the simulator, or null.</summary>
        public SteamAudioScene? Scene => _scene;

        // --- Probe batch management ---

        /// <summary>
        /// Creates a new <see cref="SteamAudioProbeBatch"/> bound to this processor's Phonon context.
        /// The caller owns the returned batch and must dispose it when done.
        /// </summary>
        public SteamAudioProbeBatch CreateProbeBatch()
        {
            if (!_initialized)
                throw new InvalidOperationException("Processor must be initialized before creating a probe batch.");

            return new SteamAudioProbeBatch(_context);
        }

        /// <summary>
        /// Attaches a committed probe batch to the simulator. Required for reflection
        /// simulation to use baked data and for pathing simulation to find paths.
        /// </summary>
        public void AddProbeBatch(SteamAudioProbeBatch batch)
        {
            ArgumentNullException.ThrowIfNull(batch);

            if (!_initialized)
                throw new InvalidOperationException("Processor must be initialized before adding probe batches.");
            if (!batch.IsCommitted)
                throw new InvalidOperationException("Probe batch must be committed before attaching to the simulator.");

            Phonon.iplSimulatorAddProbeBatch(_simulator, batch.Handle);
            _probeBatches.Add(batch);

            // Re-commit so the simulator picks up the new batch
            Phonon.iplSimulatorCommit(_simulator);

            Debug.WriteLine($"[SteamAudioProcessor] Probe batch added ({batch.ProbeCount} probes). Total batches: {_probeBatches.Count}.");
        }

        /// <summary>
        /// Detaches a probe batch from the simulator.
        /// </summary>
        public void RemoveProbeBatch(SteamAudioProbeBatch batch)
        {
            ArgumentNullException.ThrowIfNull(batch);

            if (!_initialized)
                return;

            if (_probeBatches.Remove(batch))
            {
                Phonon.iplSimulatorRemoveProbeBatch(_simulator, batch.Handle);
                Phonon.iplSimulatorCommit(_simulator);
                Debug.WriteLine($"[SteamAudioProcessor] Probe batch removed. Total batches: {_probeBatches.Count}.");
            }
        }

        /// <summary>
        /// A read-only view of the probe batches currently attached to the simulator.
        /// </summary>
        public IReadOnlyList<SteamAudioProbeBatch> ProbeBatches => _probeBatches;

        // --- Baker factory ---

        /// <summary>
        /// Creates a <see cref="SteamAudioBaker"/> for offline baking of reflection/pathing data.
        /// </summary>
        public SteamAudioBaker CreateBaker()
        {
            if (!_initialized)
                throw new InvalidOperationException("Processor must be initialized before creating a baker.");

            return new SteamAudioBaker(_context);
        }

        // --- IDisposable ---

        public void Dispose()
        {
            Shutdown();
            GC.SuppressFinalize(this);
        }
    }
}
