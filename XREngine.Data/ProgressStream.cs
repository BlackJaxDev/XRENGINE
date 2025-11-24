namespace XREngine.Core
{
    public class BasicProgress<T>(Action<T> handler) : IProgress<T>
    {
        private readonly Action<T> _handler = handler;

        void IProgress<T>.Report(T value) => _handler(value);
    }
    public class ProgressStream : Stream
    {
        private readonly Stream _stream;
        private IProgress<int> _readProgress;
        private IProgress<int> _writeProgress;

        public void SetReadProgress(IProgress<int> progress) => _readProgress = progress;
        public void SetWriteProgress(IProgress<int> progress) => _writeProgress = progress;

        public ProgressStream(Stream stream, IProgress<int> readProgress, IProgress<int> writeProgress)
        {
            _stream = stream;
            _readProgress = readProgress;
            _writeProgress = writeProgress;
            if (_stream.CanSeek)
                _stream.Seek(0, SeekOrigin.Begin);
        }

        public override bool CanRead => _stream.CanRead;
        public override bool CanSeek => _stream.CanSeek;
        public override bool CanWrite => _stream.CanWrite;
        public override long Length => _stream.Length;
        public override long Position
        {
            get => _stream.Position;
            set => _stream.Position = value;
        }

        public override bool CanTimeout => _stream.CanTimeout;
        public override int ReadTimeout { get => _stream.ReadTimeout; set => _stream.ReadTimeout = value; }
        public override int WriteTimeout { get => _stream.WriteTimeout; set => _stream.WriteTimeout = value; }

        public override void SetLength(long value) => _stream.SetLength(value);
        public override void Close() => _stream.Close();

        public override void Flush() => _stream.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _stream.FlushAsync(cancellationToken);
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) => _stream.CopyToAsync(destination, bufferSize, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin)
        {
            //throw new InvalidOperationException("Cannot seek in a progress stream.");
            return _stream.Seek(offset, origin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int bytesRead = _stream.Read(buffer, offset, count);
            _readProgress?.Report(bytesRead);
            return bytesRead;
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int bytesRead = await _stream.ReadAsync(buffer, offset, count, cancellationToken);
            _readProgress?.Report(bytesRead);
            return bytesRead;
        }
        public override int ReadByte()
        {
            int value = _stream.ReadByte();
            _readProgress?.Report(1);
            return value;
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            _stream.Write(buffer, offset, count);
            _writeProgress?.Report(count);
        }
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await _stream.WriteAsync(buffer, offset, count, cancellationToken);
            _writeProgress?.Report(count);
        }
        public override void WriteByte(byte value)
        {
            _stream.WriteByte(value);
            _writeProgress?.Report(1);
        }

        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        {
            IAsyncResult result = _stream.BeginRead(buffer, offset, count, callback, state);
            return result;
        }
        public override int EndRead(IAsyncResult asyncResult)
        {
            int bytesRead = _stream.EndRead(asyncResult);
            _readProgress?.Report(bytesRead);
            return bytesRead;
        }
        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        {
            IAsyncResult result = _stream.BeginWrite(buffer, offset, count, callback, state);
            _writeProgress?.Report(count);
            return result;
        }
        public override void EndWrite(IAsyncResult asyncResult)
        {
            _stream.EndWrite(asyncResult);
        }
    }
}
