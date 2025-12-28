using System;
using System.Collections.Generic;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;

namespace XREngine.Rendering.Compute
{
    /// <summary>
    /// Profiles GPU BVH workloads using timestamp queries and exposes per-frame metrics.
    /// </summary>
    public sealed class BvhGpuProfiler
    {
        public static BvhGpuProfiler Instance { get; } = new();

        public enum Stage
        {
            Build = 0,
            Refit = 1,
            Cull = 2,
            Raycast = 3
        }

        public readonly struct Metrics
        {
            public readonly ulong BuildNanoseconds;
            public readonly ulong RefitNanoseconds;
            public readonly ulong CullNanoseconds;
            public readonly ulong RaycastNanoseconds;
            public readonly uint BuildCount;
            public readonly uint RefitCount;
            public readonly uint CullCount;
            public readonly uint RaycastCount;

            public Metrics(
                ulong buildNs,
                ulong refitNs,
                ulong cullNs,
                ulong raycastNs,
                uint buildCount,
                uint refitCount,
                uint cullCount,
                uint raycastCount)
            {
                BuildNanoseconds = buildNs;
                RefitNanoseconds = refitNs;
                CullNanoseconds = cullNs;
                RaycastNanoseconds = raycastNs;
                BuildCount = buildCount;
                RefitCount = refitCount;
                CullCount = cullCount;
                RaycastCount = raycastCount;
            }

            public double BuildMilliseconds => BuildNanoseconds / 1_000_000.0;
            public double RefitMilliseconds => RefitNanoseconds / 1_000_000.0;
            public double CullMilliseconds => CullNanoseconds / 1_000_000.0;
            public double RaycastMilliseconds => RaycastNanoseconds / 1_000_000.0;

            public static Metrics Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);
        }

        public readonly struct TimingScope : IDisposable
        {
            private readonly BvhGpuProfiler _profiler;
            private readonly TimingHandle? _handle;

            internal TimingScope(BvhGpuProfiler profiler, TimingHandle? handle)
            {
                _profiler = profiler;
                _handle = handle;
            }

            public void Dispose()
            {
                if (_handle.HasValue)
                    _profiler.End(_handle.Value);
            }
        }

        private readonly struct TimingHandle
        {
            public TimingHandle(Stage stage, uint workCount, GLRenderQuery startQuery, OpenGLRenderer renderer)
            {
                Stage = stage;
                WorkCount = workCount;
                StartQuery = startQuery;
                Renderer = renderer;
            }

            public Stage Stage { get; }
            public uint WorkCount { get; }
            public GLRenderQuery StartQuery { get; }
            public OpenGLRenderer Renderer { get; }
        }

        private readonly struct PendingQuery
        {
            public PendingQuery(Stage stage, uint workCount, GLRenderQuery start, GLRenderQuery end)
            {
                Stage = stage;
                WorkCount = workCount;
                Start = start;
                End = end;
            }

            public Stage Stage { get; }
            public uint WorkCount { get; }
            public GLRenderQuery Start { get; }
            public GLRenderQuery End { get; }
        }

        private struct Accumulator
        {
            public ulong BuildTime;
            public ulong RefitTime;
            public ulong CullTime;
            public ulong RaycastTime;
            public uint BuildCount;
            public uint RefitCount;
            public uint CullCount;
            public uint RaycastCount;

            public void Reset()
            {
                BuildTime = 0;
                RefitTime = 0;
                CullTime = 0;
                RaycastTime = 0;
                BuildCount = 0;
                RefitCount = 0;
                CullCount = 0;
                RaycastCount = 0;
            }

            public Metrics ToMetrics()
                => new(BuildTime, RefitTime, CullTime, RaycastTime, BuildCount, RefitCount, CullCount, RaycastCount);

            public void Add(Stage stage, ulong ns, uint workCount)
            {
                switch (stage)
                {
                    case Stage.Build:
                        BuildTime += ns;
                        BuildCount += workCount;
                        break;
                    case Stage.Refit:
                        RefitTime += ns;
                        RefitCount += workCount;
                        break;
                    case Stage.Cull:
                        CullTime += ns;
                        CullCount += workCount;
                        break;
                    case Stage.Raycast:
                        RaycastTime += ns;
                        RaycastCount += workCount;
                        break;
                }
            }
        }

        private readonly object _lock = new();
        private readonly Queue<XRRenderQuery> _queryPool = new();
        private readonly List<PendingQuery> _pending = new();
        private Accumulator _frameAccumulator;
        private Metrics _latest = Metrics.Empty;
        private float _currentFrameTimestamp;
        private bool _initializedFrameStamp;

        private BvhGpuProfiler() { }

        public Metrics Latest => _latest;

        public TimingScope Scope(Stage stage, uint workCount = 0)
            => new(this, Begin(stage, workCount));

        public void RecordDispatch(Stage stage, uint workCount)
        {
            lock (_lock)
                _frameAccumulator.Add(stage, 0, workCount);
        }

        public Metrics ResolveAndPublish(float frameTimestamp, XRDataBuffer? statsBuffer)
        {
            lock (_lock)
            {
                if (_initializedFrameStamp && Math.Abs(frameTimestamp - _currentFrameTimestamp) > float.Epsilon)
                    _frameAccumulator.Reset();

                _currentFrameTimestamp = frameTimestamp;
                _initializedFrameStamp = true;

                var gl = AbstractRenderer.Current as OpenGLRenderer;
                if (gl is not null)
                    ResolvePending(gl);

                _latest = _frameAccumulator.ToMetrics();

                Engine.Rendering.BvhStats.Publish(_latest);
                WriteStats(statsBuffer, _latest);

                return _latest;
            }
        }

        private TimingHandle? Begin(Stage stage, uint workCount)
        {
            if (!Engine.EffectiveSettings.EnableGpuBvhTimingQueries)
            {
                lock (_lock)
                    _frameAccumulator.Add(stage, 0, workCount);
                return null;
            }

            var gl = AbstractRenderer.Current as OpenGLRenderer;
            if (gl is null)
            {
                lock (_lock)
                    _frameAccumulator.Add(stage, 0, workCount);
                return null;
            }

            GLRenderQuery start = AcquireQuery(gl);
            start.Data.CurrentQuery = EQueryTarget.Timestamp;
            start.QueryCounter();

            return new TimingHandle(stage, workCount, start, gl);
        }

        private void End(TimingHandle handle)
        {
            if (!Engine.EffectiveSettings.EnableGpuBvhTimingQueries)
                return;

            GLRenderQuery end = AcquireQuery(handle.Renderer);
            end.Data.CurrentQuery = EQueryTarget.Timestamp;
            end.QueryCounter();

            lock (_lock)
                _pending.Add(new PendingQuery(handle.Stage, handle.WorkCount, handle.StartQuery, end));
        }

        private void ResolvePending(OpenGLRenderer renderer)
        {
            for (int i = _pending.Count - 1; i >= 0; --i)
            {
                PendingQuery pending = _pending[i];
                if (!TryReadTimestamp(pending.Start, out ulong start) ||
                    !TryReadTimestamp(pending.End, out ulong end))
                {
                    continue;
                }

                ulong duration = end > start ? end - start : 0;
                _frameAccumulator.Add(pending.Stage, duration, pending.WorkCount);

                ReleaseQuery(pending.Start);
                ReleaseQuery(pending.End);
                _pending.RemoveAt(i);
            }
        }

        private GLRenderQuery AcquireQuery(OpenGLRenderer renderer)
        {
            XRRenderQuery query;
            lock (_lock)
                query = _queryPool.Count > 0 ? _queryPool.Dequeue() : new XRRenderQuery();

            query.CurrentQuery = EQueryTarget.Timestamp;
            query.Generate();

            GLRenderQuery? glQuery = renderer.GenericToAPI<GLRenderQuery>(query);
            if (glQuery is null)
                throw new InvalidOperationException("Failed to acquire GLRenderQuery wrapper.");

            glQuery.Data.CurrentQuery = EQueryTarget.Timestamp;
            return glQuery;
        }

        private void ReleaseQuery(GLRenderQuery query)
        {
            query.Data.CurrentQuery = null;
            lock (_lock)
                _queryPool.Enqueue(query.Data);
        }

        private static bool TryReadTimestamp(GLRenderQuery query, out ulong timestamp)
        {
            timestamp = 0;
            long available = query.GetQueryObject(EGetQueryObject.QueryResultAvailable);
            if (available == 0)
                return false;

            timestamp = (ulong)query.GetQueryObject(EGetQueryObject.QueryResult);
            return true;
        }

        private static void WriteStats(XRDataBuffer? buffer, Metrics metrics)
        {
            if (buffer is null)
                return;

            bool wroteAny = false;

            void WriteStage(uint countIndex, uint count, uint timeIndex, ulong nanoseconds)
            {
                buffer.SetDataRawAtIndex(countIndex, count);
                buffer.SetDataRawAtIndex(timeIndex, (uint)(nanoseconds & uint.MaxValue));
                buffer.SetDataRawAtIndex(timeIndex + 1u, (uint)(nanoseconds >> 32));
                wroteAny = true;
            }

            if (metrics.BuildCount != 0 || metrics.BuildNanoseconds != 0)
                WriteStage(GpuStatsLayout.BvhBuildCount, metrics.BuildCount, GpuStatsLayout.BvhBuildTimeLo, metrics.BuildNanoseconds);

            if (metrics.RefitCount != 0 || metrics.RefitNanoseconds != 0)
                WriteStage(GpuStatsLayout.BvhRefitCount, metrics.RefitCount, GpuStatsLayout.BvhRefitTimeLo, metrics.RefitNanoseconds);

            if (metrics.CullCount != 0 || metrics.CullNanoseconds != 0)
                WriteStage(GpuStatsLayout.BvhCullCount, metrics.CullCount, GpuStatsLayout.BvhCullTimeLo, metrics.CullNanoseconds);

            if (metrics.RaycastCount != 0 || metrics.RaycastNanoseconds != 0)
                WriteStage(GpuStatsLayout.BvhRayCount, metrics.RaycastCount, GpuStatsLayout.BvhRayTimeLo, metrics.RaycastNanoseconds);

            if (wroteAny)
            {
                int byteOffset = (int)(GpuStatsLayout.BvhBuildCount * (uint)sizeof(uint));
                uint byteLength = (GpuStatsLayout.FieldCount - GpuStatsLayout.BvhBuildCount) * (uint)sizeof(uint);
                buffer.PushSubData(byteOffset, byteLength);
            }
        }
    }
}
