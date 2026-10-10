using System.Text;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private string? _engineDrawPendingSource;
    private string? _engineDrawPendingReason;
    private object? _engineDrawPendingOwner;

    private void ResetEngineDrawPreparationDiagnostic()
    {
        SetField(ref _engineDrawPendingSource, null, publishNotifications: false);
        SetField(ref _engineDrawPendingReason, null, publishNotifications: false);
        SetField(ref _engineDrawPendingOwner, null, publishNotifications: false);
    }

    /// <summary>Formats the first draw deferral and bounded physical-request state only between frames.</summary>
    public string GetPendingEngineDrawStatus()
    {
        RequireEngineFrameStatisticsBoundary();
        if (!_engineDrawPending)
            return "none";
        StringBuilder output = new();
        output.Append(_engineDrawPendingSource ?? "unknown").Append('=')
            .Append(_engineDrawPendingReason ?? "pending");
        AppendPreparationOwner(output, _engineDrawPendingOwner);
        if (_engineDrawPendingOwner is WebGpuResourceRequest blocked)
            AppendPreparationRequest(output, blocked);
        output.Append("; retained requests=").Append(_engineResourceRequests.Count);
        int emitted = 0;
        foreach (WebGpuResourceRequest request in _engineResourceRequests)
        {
            if (emitted++ == 8)
            {
                output.Append(" | more requests omitted");
                break;
            }
            AppendPreparationRequest(output, request);
        }
        return output.ToString();
    }

    private static void AppendPreparationOwner(StringBuilder output, object? owner)
    {
        switch (owner)
        {
            case WebGpuMeshDraw draw:
                draw.AppendPreparationStatus(output);
                break;
            case WebGpuMeshRenderer mesh:
                output.Append(" owner=").Append(mesh.Data.Parent.Name ?? "unnamed mesh renderer")
                    .Append(" mesh=").Append(mesh.Data.Parent.Mesh?.Name ?? "unnamed")
                    .Append(" material=").Append(mesh.Data.Parent.Material?.Name ?? "unnamed")
                    .Append(" lastPrepare=").Append(mesh.LastPrepareDetail);
                break;
            case AbstractRenderAPIObject api:
                output.Append(" owner=").Append(api.GetDescribingName());
                break;
            case WebGpuResourceRequest request:
                AppendPreparationOwner(output, request.Owner);
                break;
            case not null:
                output.Append(" owner=").Append(owner.GetType().Name);
                break;
        }
    }

    private static void AppendPreparationRequest(StringBuilder output, WebGpuResourceRequest request)
    {
        output.Append(" | request=").Append(request.Identity).Append(" kind=").Append(request.Kind)
            .Append(" state=").Append(request.State).Append(" claimed=").Append(request.Claimed);
        AppendPreparationOwner(output, request.Owner);
        output.Append(" descriptor=").Append(request.Json, 0, Math.Min(request.Json.Length, 256));
        if (request.Json.Length > 256)
            output.Append("...");
    }
}
