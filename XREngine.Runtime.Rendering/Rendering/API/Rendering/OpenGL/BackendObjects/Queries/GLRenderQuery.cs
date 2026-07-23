using Silk.NET.OpenGL;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL;

/// <summary>
/// OpenGL implementation of the backend-neutral render-query contract.
/// </summary>
public sealed class GLRenderQuery(OpenGLRenderer renderer, XRRenderQuery query) : GLObject<XRRenderQuery>(renderer, query)
{
    private bool _active;
    private ulong _epoch;
    private RenderQueryTicket _ticket;

    protected override void LinkData()
    {
    }

    protected override void UnlinkData()
        => _active = false;

    public override EGLObjectType Type => EGLObjectType.Query;

    public RenderQueryTicket Ticket => _ticket;

    public RenderQueryResultLayout ResultLayout => CreateResultLayout(Data.Descriptor);

    public ERenderQueryReadStatus BeginQuery()
    {
        if (_active)
            return ERenderQueryReadStatus.InvalidState;
        if (!TryGetScopeTarget(Data.Descriptor, out GLEnum target))
            return ERenderQueryReadStatus.Unsupported;

        Api.BeginQuery(target, BindingId);
        _active = true;
        BeginEpoch();
        RenderQueryTelemetry.RecordRecording(Data.Descriptor.Kind);
        return ERenderQueryReadStatus.Ready;
    }

    public ERenderQueryReadStatus EndQuery()
    {
        if (!_active || !TryGetScopeTarget(Data.Descriptor, out GLEnum target))
            return ERenderQueryReadStatus.InvalidState;

        Api.EndQuery(target);
        _active = false;
        return ERenderQueryReadStatus.Ready;
    }

    public ERenderQueryReadStatus WriteTimestamp()
    {
        if (_active || Data.Descriptor.Kind != ERenderQueryKind.Timestamp)
            return ERenderQueryReadStatus.InvalidState;

        Api.QueryCounter(BindingId, GLEnum.Timestamp);
        BeginEpoch();
        RenderQueryTelemetry.RecordRecording(Data.Descriptor.Kind);
        return ERenderQueryReadStatus.Ready;
    }

    public RenderQueryReadResult TryReadRaw(Span<ulong> destination, in RenderQueryTicket expectedTicket = default)
    {
        RenderQueryResultLayout layout = ResultLayout;
        if (!_ticket.IsValid)
            return new(ERenderQueryReadStatus.InvalidState, _ticket, layout, 0, "The query has not been recorded.");
        if (expectedTicket.IsValid && expectedTicket != _ticket)
            return new(ERenderQueryReadStatus.StaleTicket, _ticket, layout, 0, "The requested recording epoch no longer owns this query.");
        if (layout.ValueCount == 0u)
            return new(ERenderQueryReadStatus.Unsupported, _ticket, layout, 0, GetUnsupportedReason(Data.Descriptor));
        if (destination.Length < layout.ValueCount)
            return new(ERenderQueryReadStatus.BufferTooSmall, _ticket, layout, 0, "Caller storage is smaller than the typed result layout.");
        if (Api.GetQueryObject(BindingId, GLEnum.QueryResultAvailable) == 0)
        {
            RenderQueryTelemetry.RecordRead(ERenderQueryReadStatus.NotReady);
            return new(ERenderQueryReadStatus.NotReady, _ticket, layout, 0);
        }

        Api.GetQueryObject(BindingId, GLEnum.QueryResult, out ulong value);
        destination[0] = value;
        RenderQueryTelemetry.RecordRead(ERenderQueryReadStatus.Ready);
        RenderQueryTelemetry.RecordHostReadBytes(sizeof(ulong));
        return new(ERenderQueryReadStatus.Ready, _ticket, layout, 1);
    }

    public ERenderQueryReadStatus TryGetAnySamplesPassed(out OcclusionQueryResult result, in RenderQueryTicket expectedTicket = default)
    {
        result = default;
        if (Data.Descriptor.Kind != ERenderQueryKind.Occlusion ||
            Data.Descriptor.OcclusionMode == EOcclusionResultMode.ExactSamplesPassed)
        {
            return ERenderQueryReadStatus.InvalidState;
        }

        Span<ulong> values = stackalloc ulong[1];
        RenderQueryReadResult read = TryReadRaw(values, expectedTicket);
        if (!read.IsReady)
            return read.Status;

        result = new(values[0] != 0ul, values[0] != 0ul ? 1ul : 0ul, 1u);
        return ERenderQueryReadStatus.Ready;
    }

    public ERenderQueryReadStatus TryGetExactSamplesPassed(out OcclusionQueryResult result, in RenderQueryTicket expectedTicket = default)
    {
        result = default;
        if (Data.Descriptor.Kind != ERenderQueryKind.Occlusion ||
            Data.Descriptor.OcclusionMode != EOcclusionResultMode.ExactSamplesPassed)
        {
            return ERenderQueryReadStatus.InvalidState;
        }

        Span<ulong> values = stackalloc ulong[1];
        RenderQueryReadResult read = TryReadRaw(values, expectedTicket);
        if (!read.IsReady)
            return read.Status;

        result = new(values[0] != 0ul, values[0], 1u);
        return ERenderQueryReadStatus.Ready;
    }

    public ERenderQueryReadStatus TryGetTimestamp(out TimestampQueryResult result, in RenderQueryTicket expectedTicket = default)
    {
        result = default;
        if (Data.Descriptor.Kind != ERenderQueryKind.Timestamp)
            return ERenderQueryReadStatus.InvalidState;

        Span<ulong> values = stackalloc ulong[1];
        RenderQueryReadResult read = TryReadRaw(values, expectedTicket);
        if (!read.IsReady)
            return read.Status;

        // OpenGL timestamp query results are already nanoseconds.
        result = new(values[0], values[0]);
        return ERenderQueryReadStatus.Ready;
    }

    public ERenderQueryReadStatus TryGetElapsedTime(out ElapsedTimeQueryResult result, in RenderQueryTicket expectedTicket = default)
    {
        result = default;
        if (Data.Descriptor.Kind != ERenderQueryKind.ElapsedTime)
            return ERenderQueryReadStatus.InvalidState;

        Span<ulong> values = stackalloc ulong[1];
        RenderQueryReadResult read = TryReadRaw(values, expectedTicket);
        if (!read.IsReady)
            return read.Status;

        // GL_TIME_ELAPSED directly returns nanoseconds; absolute endpoints are not exposed.
        result = new(0ul, 0ul, values[0]);
        return ERenderQueryReadStatus.Ready;
    }

    public ERenderQueryReadStatus TryGetPrimitivesGenerated(
        out PrimitivesGeneratedQueryResult result,
        in RenderQueryTicket expectedTicket = default)
    {
        result = default;
        if (Data.Descriptor.Kind != ERenderQueryKind.PrimitivesGenerated)
            return ERenderQueryReadStatus.InvalidState;

        Span<ulong> values = stackalloc ulong[1];
        RenderQueryReadResult read = TryReadRaw(values, expectedTicket);
        if (!read.IsReady)
            return read.Status;

        result = new(values[0]);
        return ERenderQueryReadStatus.Ready;
    }

    public static RenderQueryResultLayout CreateResultLayout(in RenderQueryDescriptor descriptor)
        => descriptor.Kind switch
        {
            ERenderQueryKind.Occlusion => new(
                descriptor.Kind,
                1u,
                1u,
                1u,
                -1,
                ERenderQueryIntegerWidth.UInt64,
                descriptor.OcclusionMode == EOcclusionResultMode.ExactSamplesPassed
                    ? ERenderQueryAggregation.Sum
                    : ERenderQueryAggregation.AnyNonZero),
            ERenderQueryKind.Timestamp => new(
                descriptor.Kind,
                1u,
                1u,
                1u,
                -1,
                ERenderQueryIntegerWidth.UInt64,
                ERenderQueryAggregation.Scalar),
            ERenderQueryKind.ElapsedTime => new(
                descriptor.Kind,
                1u,
                1u,
                1u,
                -1,
                ERenderQueryIntegerWidth.UInt64,
                ERenderQueryAggregation.Scalar),
            ERenderQueryKind.PrimitivesGenerated => new(
                descriptor.Kind,
                1u,
                1u,
                1u,
                -1,
                ERenderQueryIntegerWidth.UInt64,
                ERenderQueryAggregation.Scalar),
            _ => new(
                descriptor.Kind,
                0u,
                0u,
                0u,
                -1,
                ERenderQueryIntegerWidth.UInt64,
                ERenderQueryAggregation.ProviderDefined,
                descriptor.Statistics,
                descriptor.Property),
        };

    private void BeginEpoch()
    {
        ulong epoch = ++_epoch;
        if (epoch == 0ul)
            epoch = ++_epoch;
        _ticket = new(epoch, BindingId, 0u, 1u, epoch);
    }

    private static bool TryGetScopeTarget(in RenderQueryDescriptor descriptor, out GLEnum target)
    {
        switch (descriptor.Kind)
        {
            case ERenderQueryKind.Occlusion:
                target = descriptor.OcclusionMode switch
                {
                    EOcclusionResultMode.ExactSamplesPassed => GLEnum.SamplesPassed,
                    EOcclusionResultMode.AnySamplesPassed => GLEnum.AnySamplesPassed,
                    EOcclusionResultMode.AnySamplesPassedConservative => GLEnum.AnySamplesPassedConservative,
                    _ => GLEnum.None,
                };
                return target != GLEnum.None;
            case ERenderQueryKind.ElapsedTime:
                target = GLEnum.TimeElapsed;
                return true;
            case ERenderQueryKind.PrimitivesGenerated:
                target = GLEnum.PrimitivesGenerated;
                return true;
            default:
                target = GLEnum.None;
                return false;
        }
    }

    private static string GetUnsupportedReason(in RenderQueryDescriptor descriptor)
        => descriptor.Kind switch
        {
            ERenderQueryKind.TransformFeedback => "The portable transform-feedback contract requires written and needed counts; this OpenGL path exposes only the written count.",
            ERenderQueryKind.PipelineStatistics => "OpenGL pipeline statistics require one native query per counter and are not enabled by this backend.",
            ERenderQueryKind.MeshPrimitivesGenerated => "No portable OpenGL mesh-primitives query provider is enabled.",
            ERenderQueryKind.AccelerationStructureProperty or ERenderQueryKind.MicromapProperty => "The selected OpenGL renderer has no property-query provider for this subsystem.",
            ERenderQueryKind.PerformanceCounter => "OpenGL performance counters require a vendor provider and explicit ownership.",
            ERenderQueryKind.VideoResultStatus => "Video status queries require an active video subsystem owner.",
            _ => "The descriptor is not supported by the OpenGL query backend.",
        };
}
