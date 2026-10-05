namespace XREngine.Rendering
{
    /// <summary>
    /// Marks a bulk write to a buffer's client copy as in progress for its whole
    /// duration, so a client-copy spill neither starts nor completes over it. Use with
    /// <c>using</c> at the top of a method that writes the client copy; it allocates
    /// nothing.
    /// </summary>
    internal readonly ref struct XRDataBufferClientWriteScope
    {
        private readonly XRDataBuffer _buffer;

        public XRDataBufferClientWriteScope(XRDataBuffer buffer)
        {
            _buffer = buffer;
            buffer.BeginClientWriter();
        }

        public void Dispose()
            => _buffer.EndClientWriter();
    }
}
