using System;
using System.Threading;

namespace XREngine.Timers;

public partial class EngineTimer
{
    private int _explicitFrameOwnerThreadId;
    private long _explicitFrameTimestampTicks;

    /// <summary>
    /// Opens one deterministic, single-threaded frame clock while the normal timer is stopped.
    /// The caller must drive the real collect-generation gate in submission order.
    /// </summary>
    public ExplicitFrameScope BeginExplicitFrame(float fixedDelta)
    {
        if (IsRunning || UpdateThreadHandle is { IsAlive: true } ||
            CollectVisibleThreadHandle is { IsAlive: true } ||
            FixedUpdateThreadHandle is { IsAlive: true })
        {
            throw new InvalidOperationException(
                "Explicit frames require a stopped EngineTimer with no prior loop workers.");
        }

        int threadId = Environment.CurrentManagedThreadId;
        if (Interlocked.CompareExchange(ref _explicitFrameOwnerThreadId, threadId, 0) != 0)
            throw new InvalidOperationException("An explicit EngineTimer frame is already active.");

        try
        {
            EnsureExplicitGateReady();
            long deltaTicks = SecondsToStopwatchTicks(Math.Max(0.000001f, fixedDelta));
            Interlocked.Add(ref _explicitFrameTimestampTicks, deltaTicks);
            Update.DeltaTicks = deltaTicks;
            Collect.DeltaTicks = deltaTicks;
            Render.DeltaTicks = deltaTicks;
            FixedUpdateManager.DeltaTicks = deltaTicks;
            Update.ElapsedTicks = deltaTicks;
            Collect.ElapsedTicks = deltaTicks;
            Render.ElapsedTicks = deltaTicks;
            FixedUpdateManager.ElapsedTicks = deltaTicks;
            Update.LastTimestampTicks = _explicitFrameTimestampTicks;
            Collect.LastTimestampTicks = _explicitFrameTimestampTicks;
            Render.LastTimestampTicks = _explicitFrameTimestampTicks;
            FixedUpdateManager.LastTimestampTicks = _explicitFrameTimestampTicks;
            unchecked { ++UpdateFrameId; }
            return new ExplicitFrameScope(this, threadId);
        }
        catch
        {
            Volatile.Write(ref _explicitFrameOwnerThreadId, 0);
            throw;
        }
    }

    public sealed class ExplicitFrameScope : IDisposable
    {
        private readonly EngineTimer _timer;
        private readonly int _ownerThreadId;
        private long _collectGeneration;
        private bool _collectConsumed;
        private ulong _renderFrameId;
        private bool _renderFrameCompleted;
        private bool _renderFrameAborted;
        private bool _presented;
        private bool _disposed;

        internal ExplicitFrameScope(EngineTimer timer, int ownerThreadId)
        {
            _timer = timer;
            _ownerThreadId = ownerThreadId;
        }

        public long RequestCollect()
        {
            EnsureOwner();
            if (_collectGeneration != 0L)
                throw new InvalidOperationException("The explicit frame already owns a collect generation.");
            _collectGeneration = _timer._visibilityGenerationGate.RequestNextCollect();
            unchecked { ++_timer.CollectFrameId; }
            return _collectGeneration;
        }

        public void CompleteCollect()
        {
            EnsureOwner();
            RequireCollectGeneration();
            _timer._visibilityGenerationGate.MarkCollectCompleted(_collectGeneration);
        }

        public void PublishCollect()
        {
            EnsureOwner();
            RequireCollectGeneration();
            _timer._visibilityGenerationGate.Publish(_collectGeneration);
            unchecked { ++_timer.SwapFrameId; }
        }

        public void ConsumePublishedCollect()
        {
            EnsureOwner();
            RequireCollectGeneration();
            if (!_timer._visibilityGenerationGate.TryConsumeFresh(out long consumed) || consumed != _collectGeneration)
                throw new InvalidOperationException("The explicit frame could not consume its published collect generation.");
            _collectConsumed = true;
        }

        /// <summary>Begins the production render identity after collection has been published.</summary>
        public ulong BeginRenderFrame()
        {
            EnsureOwner();
            RequireCollectGeneration();
            if (!_collectConsumed)
                throw new InvalidOperationException("The explicit frame must consume its published collect generation before rendering.");
            if (_renderFrameId != 0UL)
                throw new InvalidOperationException("The explicit frame already owns a render identity.");
            _renderFrameId = RuntimeEngine.Rendering.BeginRenderFrame();
            return _renderFrameId;
        }

        /// <summary>Completes the production render identity using the deterministic frame delta.</summary>
        public void CompleteRenderFrame()
        {
            EnsureOwner();
            if (_renderFrameId == 0UL || _renderFrameCompleted || _renderFrameAborted)
                throw new InvalidOperationException("The explicit render identity is not awaiting completion.");
            RuntimeEngine.Rendering.CompleteRenderFrame(_renderFrameId, _timer.Render.DeltaTicks);
            _renderFrameCompleted = true;
        }

        /// <summary>Settles a failed explicit submission without publishing a completed render sample.</summary>
        public void AbortRenderFrame()
        {
            EnsureOwner();
            if (_renderFrameId != 0UL && !_renderFrameCompleted)
                _renderFrameAborted = true;
        }

        public void MarkPresented()
        {
            EnsureOwner();
            if (!_renderFrameCompleted || _renderFrameAborted || _presented)
                throw new InvalidOperationException("Only one successful CompleteRenderFrame may precede explicit presentation.");
            _timer.PresentFrameId = _renderFrameId;
            _presented = true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            EnsureOwner();
            try
            {
                if (_renderFrameId != 0UL && !_renderFrameCompleted && !_renderFrameAborted)
                    throw new InvalidOperationException("An explicit render identity was abandoned before completion.");
            }
            finally
            {
                _disposed = true;
                Volatile.Write(ref _timer._explicitFrameOwnerThreadId, 0);
            }
        }

        private void RequireCollectGeneration()
        {
            if (_collectGeneration == 0L)
                throw new InvalidOperationException("RequestCollect must precede this explicit frame operation.");
        }

        private void EnsureOwner()
        {
            if (_disposed || Environment.CurrentManagedThreadId != _ownerThreadId ||
                Volatile.Read(ref _timer._explicitFrameOwnerThreadId) != _ownerThreadId)
            {
                throw new InvalidOperationException("The explicit EngineTimer frame is not owned by this thread.");
            }
        }
    }

    private void EnsureExplicitGateReady()
    {
        if (_visibilityGenerationGate.IsTerminated)
        {
            throw new InvalidOperationException(
                "Explicit frames cannot reuse a terminal visibility gate. Start a fresh EngineTimer lifecycle first.");
        }

        if (_visibilityGenerationGate.ConsumedGeneration >= 0L)
            return;
        if (_visibilityGenerationGate.RequestedGeneration != 0L ||
            _visibilityGenerationGate.CompletedGeneration != 0L ||
            _visibilityGenerationGate.PublishedGeneration != 0L ||
            !_visibilityGenerationGate.TryConsumeFresh(out long bootstrapGeneration) ||
            bootstrapGeneration != 0L)
        {
            throw new InvalidOperationException("The explicit frame clock found an inconsistent bootstrap visibility gate.");
        }
    }
}
