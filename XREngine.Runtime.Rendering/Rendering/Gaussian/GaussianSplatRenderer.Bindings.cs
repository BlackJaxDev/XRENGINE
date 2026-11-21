using System;
using System.Threading;

namespace XREngine.Rendering.Gaussian;

public sealed partial class GaussianSplatRenderer
{
    private GaussianSplatQuadBindingPublisher ResolveQuadPublisher(XRMeshRenderer quad)
    {
        if (ReferenceEquals(_cachedQuad, quad) && _cachedPublisher is not null)
            return _cachedPublisher;

        IRenderBindingPublisher[] existing = quad.BindingPublishers.CaptureSnapshot();
        GaussianSplatQuadBindingPublisher? publisher = null;
        for (int index = 0; index < existing.Length; index++)
        {
            if (existing[index] is GaussianSplatQuadBindingPublisher candidate)
            {
                publisher = candidate;
                break;
            }
        }

        if (publisher is null)
        {
            publisher = new GaussianSplatQuadBindingPublisher();
            quad.BindingPublishers.Add(publisher);
        }

        _cachedQuad = quad;
        _cachedPublisher = publisher;
        return publisher;
    }

    /// <summary>
    /// Publishes the splat storage buffers and <see cref="SplatScale"/> of one <see cref="GaussianSplatRenderer"/>
    /// draw for both immediate OpenGL submission and deferred Vulkan submission without per-frame heap allocations.
    /// </summary>
    private sealed class GaussianSplatQuadBindingPublisher :
        IRenderResourceBindingPublisher,
        IDeferredRenderBindingPublisher
    {
        private const int DeferredPublicationCapacity = 256;

        private readonly object _generationSync = new();
        private readonly object _activationSync = new();
        private readonly DeferredDrawEntry[] _deferredEntries = new DeferredDrawEntry[DeferredPublicationCapacity];
        private readonly ulong[] _deferredTokens = new ulong[DeferredPublicationCapacity];

        private GaussianSplatRenderer? _currentRenderer;
        private GaussianSplatRenderer? _activeDeferredRenderer;
        private float _activeDeferredSplatScale = 1f;
        private bool _hasActiveDeferredPublication;
        private ulong _activeDeferredToken;
        private long _nextDeferredToken;

        private GaussianSplatRenderer? _lastUniformRenderer;
        private float _lastUniformSplatScale;
        private bool _hasLastUniformState;
        private ulong _uniformGeneration = 1UL;

        private GaussianSplatRenderer? _lastResourceRenderer;
        private ulong _lastResourceRevision;
        private bool _hasLastResourceState;
        private ulong _resourceGeneration = 1UL;

        public ERenderBindingFrequency Frequency => ERenderBindingFrequency.Object;

        public bool RequiresReadyDescriptorResources => true;

        public ulong Generation
        {
            get
            {
                ResolveActiveState(out GaussianSplatRenderer? renderer, out float splatScale);
                if (renderer is null || renderer._disposed)
                    return _uniformGeneration;

                lock (_generationSync)
                {
                    if (_hasLastUniformState &&
                        ReferenceEquals(_lastUniformRenderer, renderer) &&
                        BitConverter.SingleToInt32Bits(_lastUniformSplatScale) == BitConverter.SingleToInt32Bits(splatScale))
                    {
                        return _uniformGeneration;
                    }

                    _lastUniformRenderer = renderer;
                    _lastUniformSplatScale = splatScale;
                    _hasLastUniformState = true;
                    _uniformGeneration = unchecked(_uniformGeneration + 1UL);
                    if (_uniformGeneration == 0UL)
                        _uniformGeneration = 1UL;
                    return _uniformGeneration;
                }
            }
        }

        public ulong ResourceGeneration
        {
            get
            {
                ResolveActiveState(out GaussianSplatRenderer? renderer, out _);
                if (renderer is null || renderer._disposed)
                    return _resourceGeneration;

                ulong revision = renderer._resourceRevision;
                lock (_generationSync)
                {
                    if (_hasLastResourceState &&
                        ReferenceEquals(_lastResourceRenderer, renderer) &&
                        _lastResourceRevision == revision)
                    {
                        return _resourceGeneration;
                    }

                    _lastResourceRenderer = renderer;
                    _lastResourceRevision = revision;
                    _hasLastResourceState = true;
                    _resourceGeneration = unchecked(_resourceGeneration + 1UL);
                    if (_resourceGeneration == 0UL)
                        _resourceGeneration = 1UL;
                    return _resourceGeneration;
                }
            }
        }

        public GaussianSplatRenderer? SetCurrentRenderer(GaussianSplatRenderer? renderer)
        {
            GaussianSplatRenderer? previous = _currentRenderer;
            _currentRenderer = renderer;
            return previous;
        }

        public void ReleaseRendererReferences(GaussianSplatRenderer renderer)
        {
            lock (_generationSync)
            {
                if (ReferenceEquals(_lastUniformRenderer, renderer))
                {
                    _lastUniformRenderer = null;
                    _hasLastUniformState = false;
                }

                if (ReferenceEquals(_lastResourceRenderer, renderer))
                {
                    _lastResourceRenderer = null;
                    _hasLastResourceState = false;
                }
            }

            for (int slot = 0; slot < DeferredPublicationCapacity; slot++)
            {
                if (ReferenceEquals(_deferredEntries[slot].Renderer, renderer))
                {
                    Volatile.Write(ref _deferredTokens[slot], 0UL);
                    _deferredEntries[slot] = default;
                }
            }
        }

        public void PublishUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
        {
            ResolveActiveState(out GaussianSplatRenderer? renderer, out float splatScale);
            if (renderer is null || renderer._disposed)
                return;

            vertexProgram.Uniform(SplatScaleUniform, splatScale);
        }

        public void PublishResources(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
        {
            ResolveActiveState(out GaussianSplatRenderer? renderer, out _);
            if (renderer is null || renderer._disposed)
                return;

            vertexProgram.BindBuffer(renderer.Splats, SplatsBinding);
            vertexProgram.BindBuffer(renderer._order, OrderBinding);
            vertexProgram.BindBuffer(renderer.VisibleCount, VisibleCountBinding);
        }

        public ulong CaptureDeferredPublication()
        {
            GaussianSplatRenderer? renderer = _currentRenderer;
            if (renderer is null || renderer._disposed)
                return 0UL;

            ulong token = unchecked((ulong)Interlocked.Increment(ref _nextDeferredToken));
            if (token == 0UL)
                token = unchecked((ulong)Interlocked.Increment(ref _nextDeferredToken));

            int slot = (int)(token % DeferredPublicationCapacity);
            _deferredEntries[slot] = new DeferredDrawEntry(renderer, renderer.SplatScale);
            Volatile.Write(ref _deferredTokens[slot], token);
            return token;
        }

        public bool TryActivateDeferredPublication(ulong token)
        {
            Monitor.Enter(_activationSync);
            int slot = (int)(token % DeferredPublicationCapacity);
            if (Volatile.Read(ref _deferredTokens[slot]) != token)
            {
                Monitor.Exit(_activationSync);
                return false;
            }

            DeferredDrawEntry entry = _deferredEntries[slot];
            if (entry.Renderer is null || entry.Renderer._disposed)
            {
                Monitor.Exit(_activationSync);
                return false;
            }

            _activeDeferredRenderer = entry.Renderer;
            _activeDeferredSplatScale = entry.SplatScale;
            _hasActiveDeferredPublication = true;
            _activeDeferredToken = token;
            return true;
        }

        public void DeactivateDeferredPublication(ulong token)
        {
            if (_activeDeferredToken != token)
                throw new InvalidOperationException("Deferred gaussian splat binding publication deactivated out of order.");

            _activeDeferredRenderer = null;
            _activeDeferredSplatScale = 1f;
            _hasActiveDeferredPublication = false;
            _activeDeferredToken = 0UL;
            Monitor.Exit(_activationSync);
        }

        private void ResolveActiveState(out GaussianSplatRenderer? renderer, out float splatScale)
        {
            if (_hasActiveDeferredPublication)
            {
                renderer = _activeDeferredRenderer;
                splatScale = _activeDeferredSplatScale;
                return;
            }

            renderer = _currentRenderer;
            splatScale = renderer?.SplatScale ?? 1f;
        }

        private readonly record struct DeferredDrawEntry(
            GaussianSplatRenderer? Renderer,
            float SplatScale);
    }
}
