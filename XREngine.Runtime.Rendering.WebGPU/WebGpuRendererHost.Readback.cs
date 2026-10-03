namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    public Task<byte[]> ReadBufferAsync(BrowserBufferReadbackDescription description, CancellationToken cancellationToken = default)
    {
        RequireReady();
        description.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        RequireReadbackResource(description.BufferHandle);
        int session = _session;
        int ticket = WebGpuImports.BeginBufferReadback(session, description.BufferHandle, description.Offset, description.ByteLength);
        return FinishReadbackAsync(session, ticket, description.ByteLength, cancellationToken);
    }

    public Task<byte[]> ReadTextureAsync(BrowserTextureReadbackDescription description, CancellationToken cancellationToken = default)
    {
        RequireReady();
        description.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        RequireReadbackResource(description.TextureHandle);
        int session = _session;
        int ticket = WebGpuImports.BeginTextureReadback(session, description.TextureHandle, description.MipLevel,
            description.X, description.Y, description.Width, description.Height);
        return FinishReadbackAsync(session, ticket, description.ByteLength, cancellationToken);
    }

    private void RequireReadbackResource(int handle)
    {
        if (!_resources.Contains(handle))
            throw new InvalidOperationException("Readback resource must belong to this WebGPU renderer.");
    }

    private async Task<byte[]> FinishReadbackAsync(int session, int ticket, int byteLength, CancellationToken cancellationToken)
    {
        try
        {
            await WebGpuImports.WaitReadbackAsync(session, ticket).WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            RequireReadbackSession(session);
            byte[] result = new byte[byteLength];
            // The only managed memory borrow occurs after completion and ends within this import.
            WebGpuImports.CopyReadback(session, ticket, result);
            return result;
        }
        finally
        {
            WebGpuImports.ReleaseReadback(session, ticket);
        }
    }

    private void RequireReadbackSession(int session)
    {
        if (State != BrowserRendererState.Ready || _session != session)
            throw new InvalidOperationException("The WebGPU renderer session ended before the asynchronous GPU request completed.");
    }

    private async Task CompleteSubmittedWorkTicketAsync(CancellationToken cancellationToken)
    {
        RequireReady();
        cancellationToken.ThrowIfCancellationRequested();
        int session = _session;
        int ticket = WebGpuImports.BeginCompletion(session);
        try
        {
            await WebGpuImports.WaitReadbackAsync(session, ticket).WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            RequireReadbackSession(session);
        }
        finally
        {
            WebGpuImports.ReleaseReadback(session, ticket);
        }
    }
}
