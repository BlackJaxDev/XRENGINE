using System.Net.Sockets;

namespace XREngine.Networking;

/// <summary>Releases a connected stream and its owning socket together.</summary>
internal sealed class SocketOwnedStream(TcpClient client) : Stream
{
    private readonly NetworkStream _stream = client.GetStream();
    private int _disposed;
    public override bool CanRead => _stream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _stream.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _stream.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _stream.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _stream.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _stream.ReadAsync(buffer, cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _stream.ReadAsync(buffer, offset, count, cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _stream.Write(buffer);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _stream.WriteAsync(buffer, cancellationToken);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _stream.WriteAsync(buffer, offset, count, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stream.Dispose();
            client.Dispose();
        }
        base.Dispose(disposing);
    }
}
