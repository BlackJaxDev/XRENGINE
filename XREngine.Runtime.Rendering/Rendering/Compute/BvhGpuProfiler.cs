using System;
using System.Collections.Generic;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;

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
            Raycast = 3,
            Morton = 4,
            Sort = 5,
            HierarchyBuild = 6,
            Refinement = 7,
            RefitClear = 8,
            LeafRefit = 9,
            Traversal = 10,
            CommandEmission = 11
        }

        public readonly struct Metrics(
            ulong buildNs,
            ulong refitNs,
            ulong cullNs,
            ulong raycastNs,
            uint buildCount,
            uint refitCount,
            uint cullCount,
            uint raycastCount,
            ulong mortonNs,
            ulong sortNs,
            ulong hierarchyBuildNs,
            ulong refinementNs,
            ulong refitClearNs,
            ulong leafRefitNs,
            ulong traversalNs,
            ulong commandEmissionNs,
            ulong buildSubmissionNs,
            ulong refitSubmissionNs,
            ulong commandEmissionSubmissionNs)
        {
            public readonly ulong BuildNanoseconds = buildNs;
            public readonly ulong RefitNanoseconds = refitNs;
            public readonly ulong CullNanoseconds = cullNs;
            public readonly ulong RaycastNanoseconds = raycastNs;
            public readonly uint BuildCount = buildCount;
            public readonly uint RefitCount = refitCount;
            public readonly uint CullCount = cullCount;
            public readonly uint RaycastCount = raycastCount;
            public readonly ulong MortonNanoseconds = mortonNs;
            public readonly ulong SortNanoseconds = sortNs;
            public readonly ulong HierarchyBuildNanoseconds = hierarchyBuildNs;
            public readonly ulong RefinementNanoseconds = refinementNs;
            public readonly ulong RefitClearNanoseconds = refitClearNs;
            public readonly ulong LeafRefitNanoseconds = leafRefitNs;
            public readonly ulong TraversalNanoseconds = traversalNs;
            public readonly ulong CommandEmissionNanoseconds = commandEmissionNs;
            public readonly ulong BuildSubmissionNanoseconds = buildSubmissionNs;
            public readonly ulong RefitSubmissionNanoseconds = refitSubmissionNs;
            public readonly ulong CommandEmissionSubmissionNanoseconds = commandEmissionSubmissionNs;

            public double BuildMilliseconds => BuildNanoseconds / 1_000_000.0;
            public double RefitMilliseconds => RefitNanoseconds / 1_000_000.0;
            public double CullMilliseconds => CullNanoseconds / 1_000_000.0;
            public double RaycastMilliseconds => RaycastNanoseconds / 1_000_000.0;
            public double TraversalMilliseconds => TraversalNanoseconds / 1_000_000.0;
            public double CommandEmissionMilliseconds => CommandEmissionNanoseconds / 1_000_000.0;
            public double CommandEmissionSubmissionMilliseconds => CommandEmissionSubmissionNanoseconds / 1_000_000.0;

            public static Metrics Empty { get; } = new(
                0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
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

        public readonly struct CpuSubmissionScope(BvhGpuProfiler profiler, Stage stage) : IDisposable
        {
            private readonly long _started = System.Diagnostics.Stopwatch.GetTimestamp();

            public void Dispose()
                => profiler.RecordCpuSubmission(stage, System.Diagnostics.Stopwatch.GetTimestamp() - _started);
        }

        public readonly struct TimingHandle(BvhGpuProfiler.Stage stage, uint workCount, XRRenderQuery startQuery)
        {
            public Stage Stage { get; } = stage;
            public uint WorkCount { get; } = workCount;
            public XRRenderQuery StartQuery { get; } = startQuery;
        }

        private struct PendingQuery(BvhGpuProfiler.Stage stage, uint workCount, XRRenderQuery start, XRRenderQuery end)
        {
            public Stage Stage { get; } = stage;
            public uint WorkCount { get; } = workCount;
            public XRRenderQuery Start { get; } = start;
            public XRRenderQuery End { get; } = end;
            public bool StartResolved;
            public bool EndResolved;
            public ulong StartNanoseconds;
            public ulong EndNanoseconds;
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
            public ulong MortonTime;
            public ulong SortTime;
            public ulong HierarchyBuildTime;
            public ulong RefinementTime;
            public ulong RefitClearTime;
            public ulong LeafRefitTime;
            public ulong TraversalTime;
            public ulong CommandEmissionTime;
            public ulong BuildSubmissionTime;
            public ulong RefitSubmissionTime;
            public ulong CommandEmissionSubmissionTime;

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
                MortonTime = 0;
                SortTime = 0;
                HierarchyBuildTime = 0;
                RefinementTime = 0;
                RefitClearTime = 0;
                LeafRefitTime = 0;
                TraversalTime = 0;
                CommandEmissionTime = 0;
                BuildSubmissionTime = 0;
                RefitSubmissionTime = 0;
                CommandEmissionSubmissionTime = 0;
            }

            public readonly Metrics ToMetrics()
                => new(
                    BuildTime, RefitTime, CullTime, RaycastTime,
                    BuildCount, RefitCount, CullCount, RaycastCount,
                    MortonTime, SortTime, HierarchyBuildTime, RefinementTime,
                    RefitClearTime, LeafRefitTime, TraversalTime, CommandEmissionTime,
                    BuildSubmissionTime, RefitSubmissionTime, CommandEmissionSubmissionTime);

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
                    case Stage.Morton:
                        MortonTime += ns;
                        break;
                    case Stage.Sort:
                        SortTime += ns;
                        break;
                    case Stage.HierarchyBuild:
                        HierarchyBuildTime += ns;
                        break;
                    case Stage.Refinement:
                        RefinementTime += ns;
                        break;
                    case Stage.RefitClear:
                        RefitClearTime += ns;
                        break;
                    case Stage.LeafRefit:
                        LeafRefitTime += ns;
                        break;
                    case Stage.Traversal:
                        TraversalTime += ns;
                        break;
                    case Stage.CommandEmission:
                        CommandEmissionTime += ns;
                        break;
                }
            }

            public void AddCpuSubmission(Stage stage, ulong ns)
            {
                if (stage == Stage.Build)
                    BuildSubmissionTime += ns;
                else if (stage == Stage.Refit)
                    RefitSubmissionTime += ns;
                else if (stage == Stage.CommandEmission)
                    CommandEmissionSubmissionTime += ns;
            }
        }

        private readonly object _lock = new();
        private const int MaxPendingScopes = 1024;
        private readonly Queue<XRRenderQuery> _queryPool = new(MaxPendingScopes * 2);
        private readonly List<PendingQuery> _pending = new(MaxPendingScopes);
        private readonly List<XRRenderQuery> _abandonedPending = new(MaxPendingScopes);
        private int _reservedScopes;
        private Accumulator _frameAccumulator;
        private Metrics _latest = Metrics.Empty;
        private long _currentFrameTimestampTicks;
        private bool _initializedFrameStamp;

        internal static bool ShouldResetFrameAccumulator(bool initializedFrameStamp, long currentFrameTimestampTicks, long nextFrameTimestampTicks)
            => initializedFrameStamp && nextFrameTimestampTicks != currentFrameTimestampTicks;

        internal static bool HasPendingCapacity(int pending, int abandoned, int reserved)
            => pending >= 0 && abandoned >= 0 && reserved >= 0 &&
                pending + abandoned + reserved < MaxPendingScopes;

        private BvhGpuProfiler() { }

        public Metrics Latest => _latest;

        public TimingScope Scope(Stage stage, uint workCount = 0)
            => new(this, Begin(stage, workCount));

        public CpuSubmissionScope SubmissionScope(Stage stage)
            => new(this, stage);

        public void RecordDispatch(Stage stage, uint workCount)
        {
            lock (_lock)
                _frameAccumulator.Add(stage, 0, workCount);
        }

        public void RecordCpuSubmission(Stage stage, long elapsedStopwatchTicks)
        {
            if (elapsedStopwatchTicks <= 0)
                return;

            ulong nanoseconds = (ulong)((double)elapsedStopwatchTicks * 1_000_000_000.0 / System.Diagnostics.Stopwatch.Frequency);
            lock (_lock)
                _frameAccumulator.AddCpuSubmission(stage, nanoseconds);
        }

        public Metrics ResolveAndPublish(long frameTimestampTicks, XRDataBuffer? statsBuffer)
        {
            lock (_lock)
            {
                if (ShouldResetFrameAccumulator(_initializedFrameStamp, _currentFrameTimestampTicks, frameTimestampTicks))
                    _frameAccumulator.Reset();

                _currentFrameTimestampTicks = frameTimestampTicks;
                _initializedFrameStamp = true;

                if (AbstractRenderer.Current is OpenGLRenderer or VulkanRenderer)
                    ResolvePending();

                _latest = _frameAccumulator.ToMetrics();

                RuntimeEngine.Rendering.BvhStats.Publish(_latest);
                WriteStats(statsBuffer, _latest);

                return _latest;
            }
        }

        private TimingHandle? Begin(Stage stage, uint workCount)
        {
            if (!RuntimeEngine.EffectiveSettings.EnableGpuBvhTimingQueries)
            {
                lock (_lock)
                    _frameAccumulator.Add(stage, 0, workCount);
                return null;
            }

            if (AbstractRenderer.Current is not (OpenGLRenderer or VulkanRenderer))
            {
                lock (_lock)
                    _frameAccumulator.Add(stage, 0, workCount);
                return null;
            }

            lock (_lock)
            {
                if (!HasPendingCapacity(_pending.Count, _abandonedPending.Count, _reservedScopes))
                {
                    _frameAccumulator.Add(stage, 0, workCount);
                    return null;
                }
                _reservedScopes++;
            }

            XRRenderQuery start = AcquireQuery();
            if (!TryWriteTimestamp(start))
            {
                ReleaseQuery(start);
                lock (_lock)
                    _reservedScopes--;
                return null;
            }

            return new TimingHandle(stage, workCount, start);
        }

        private void End(TimingHandle handle)
        {
            XRRenderQuery end = AcquireQuery();
            if (!TryWriteTimestamp(end))
            {
                ReleaseQuery(end);
                lock (_lock)
                {
                    _reservedScopes--;
                    _abandonedPending.Add(handle.StartQuery);
                }
                return;
            }

            lock (_lock)
            {
                _reservedScopes--;
                _pending.Add(new PendingQuery(handle.Stage, handle.WorkCount, handle.StartQuery, end));
            }
        }

        private void ResolvePending()
        {
            for (int i = _abandonedPending.Count - 1; i >= 0; --i)
            {
                ERenderQueryReadStatus status = TryReadTimestamp(_abandonedPending[i], out _);
                if (status == ERenderQueryReadStatus.NotReady)
                    continue;

                ReleaseQuery(_abandonedPending[i]);
                _abandonedPending.RemoveAt(i);
            }

            for (int i = _pending.Count - 1; i >= 0; --i)
            {
                PendingQuery pending = _pending[i];
                if (!pending.StartResolved && TryReadTimestamp(pending.Start, out ulong start) == ERenderQueryReadStatus.Ready)
                {
                    pending.StartNanoseconds = start;
                    pending.StartResolved = true;
                }
                if (!pending.EndResolved && TryReadTimestamp(pending.End, out ulong end) == ERenderQueryReadStatus.Ready)
                {
                    pending.EndNanoseconds = end;
                    pending.EndResolved = true;
                }
                if (!pending.StartResolved || !pending.EndResolved)
                {
                    _pending[i] = pending;
                    continue;
                }

                ulong duration = pending.EndNanoseconds > pending.StartNanoseconds
                    ? pending.EndNanoseconds - pending.StartNanoseconds
                    : 0;
                _frameAccumulator.Add(pending.Stage, duration, pending.WorkCount);

                ReleaseQuery(pending.Start);
                ReleaseQuery(pending.End);
                _pending.RemoveAt(i);
            }
        }

        private XRRenderQuery AcquireQuery()
        {
            XRRenderQuery query;
            bool created;
            lock (_lock)
            {
                created = _queryPool.Count == 0;
                query = created ? new XRRenderQuery(RenderQueryDescriptor.Timestamp) : _queryPool.Dequeue();
            }
            if (created)
                query.Generate();
            return query;
        }

        private void ReleaseQuery(XRRenderQuery query)
        {
            lock (_lock)
                _queryPool.Enqueue(query);
        }

        private static bool TryWriteTimestamp(XRRenderQuery query)
            => AbstractRenderer.Current switch
            {
                OpenGLRenderer gl => gl.GenericToAPI<GLRenderQuery>(query)?.WriteTimestamp() == ERenderQueryReadStatus.Ready,
                VulkanRenderer vk => vk.EnqueueTimestampQuery(query),
                _ => false,
            };

        private static ERenderQueryReadStatus TryReadTimestamp(XRRenderQuery query, out ulong timestamp)
        {
            timestamp = 0;
            TimestampQueryResult result = default;
            ERenderQueryReadStatus status = AbstractRenderer.Current switch
            {
                OpenGLRenderer gl => gl.GenericToAPI<GLRenderQuery>(query)?.TryGetTimestamp(out result) ?? ERenderQueryReadStatus.InvalidState,
                VulkanRenderer vk => vk.GenericToAPI<VulkanRenderer.VkRenderQuery>(query)?.TryGetTimestamp(out result) ?? ERenderQueryReadStatus.InvalidState,
                _ => ERenderQueryReadStatus.Unsupported,
            };
            timestamp = result.Nanoseconds;
            return status;
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
                uint byteLength = (GpuStatsLayout.StatsTriangleCount - GpuStatsLayout.BvhBuildCount) * (uint)sizeof(uint);
                buffer.PushSubData(byteOffset, byteLength);
            }
        }
    }
}
