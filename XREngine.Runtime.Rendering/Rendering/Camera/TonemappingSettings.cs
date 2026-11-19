using System;
using System.Threading;

namespace XREngine.Rendering
{
    public sealed class TonemappingSettings : PostProcessSettings
    {
        public const float DefaultMobiusTransition = 0.6f;
        public const float MinMobiusTransition = 0.01f;
        public const float MaxMobiusTransition = 4.0f;

        private static readonly Lock HostDefaultSync = new();
        private static ETonemappingType _hostDefaultTonemapping = ETonemappingType.Mobius;

        private ETonemappingType _tonemapping = HostDefaultTonemapping;
        private float _mobiusTransition = DefaultMobiusTransition;

        /// <summary>
        /// The operator that new tonemapping settings and new post-process schemas start with.
        /// The shared value is Mobius, because the cooked web Default output route supports only Mobius.
        /// Desktop hosts install GT7 with <see cref="InstallHostDefault"/> before they create cameras,
        /// so editor, desktop and VR eye cameras start with GT7. Authored camera values still win.
        /// </summary>
        public static ETonemappingType HostDefaultTonemapping
        {
            get
            {
                lock (HostDefaultSync)
                    return _hostDefaultTonemapping;
            }
        }

        /// <summary>
        /// Sets <see cref="HostDefaultTonemapping"/> for this process. Install it before the host
        /// creates render pipelines, because each pipeline copies the value into its post-process
        /// schema when it builds the schema. Disposing the returned lease sets the previous value again.
        /// </summary>
        public static IDisposable InstallHostDefault(ETonemappingType tonemapping)
        {
            lock (HostDefaultSync)
            {
                ETonemappingType previous = _hostDefaultTonemapping;
                _hostDefaultTonemapping = tonemapping;
                return new HostDefaultLease(tonemapping, previous);
            }
        }

        public ETonemappingType Tonemapping
        {
            get => _tonemapping;
            set => SetField(ref _tonemapping, value);
        }

        public float MobiusTransition
        {
            get => _mobiusTransition;
            set => SetField(ref _mobiusTransition, Math.Clamp(value, MinMobiusTransition, MaxMobiusTransition));
        }

        public override void SetUniforms(XRRenderProgram program)
        {
            program.Uniform("TonemapType", (int)Tonemapping);
            program.Uniform("MobiusTransition", MobiusTransition);
        }

        private sealed class HostDefaultLease(ETonemappingType installed, ETonemappingType previous) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;
                lock (HostDefaultSync)
                    if (_hostDefaultTonemapping == installed)
                        _hostDefaultTonemapping = previous;
            }
        }
    }
}