using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class XRMesh
{
    /// <summary>One observable CPU index build for a geometry revision.</summary>
    private sealed class IndexBufferBuildTicket(long geometryRevision)
    {
        public long GeometryRevision { get; } = geometryRevision;
        public TaskCompletionSource<(XRDataBuffer buffer, IndexSize elementSize)> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Fail(Exception error)
        {
            if (Completion.TrySetException(error))
                _ = Completion.Task.Exception; // Nonblocking consumers may never await this ticket.
        }
    }
}
