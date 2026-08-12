namespace XREngine.Rendering
{
    using XREngine.Data;

    /// <summary>Consumes mapped bytes synchronously; the span is invalid after the callback returns.</summary>
    public delegate bool DataBufferMappedReadCallback(scoped ReadOnlySpan<byte> bytes);

    /// <summary>Consumes writable mapped bytes synchronously; the span is invalid after the callback returns.</summary>
    public delegate bool DataBufferMappedWriteCallback(scoped Span<byte> bytes);

    /// <summary>Allocation-free scoped read callback with caller-owned state.</summary>
    public delegate bool DataBufferMappedReadCallback<TState>(scoped ReadOnlySpan<byte> bytes, ref TState state)
        where TState : allows ref struct;

    /// <summary>Allocation-free scoped write callback with caller-owned state.</summary>
    public delegate bool DataBufferMappedWriteCallback<TState>(scoped Span<byte> bytes, ref TState state)
        where TState : allows ref struct;

    public interface IApiDataBuffer
    {
        void PushData();
        void PushSubData();
        void PushSubData(int offset, uint length);
        void Flush();
        void FlushRange(int offset, uint length);
        void SetUniformBlockName(XRRenderProgram program, string blockName);
        void SetBlockIndex(uint blockIndex);
        void Bind();
        void Unbind();
        /// <summary>Reads mapped bytes without allowing the native address to escape the mapping scope.</summary>
        bool TryReadMapped(DataBufferMappedReadCallback callback);

        /// <summary>Writes mapped bytes without allowing the native address to escape the mapping scope.</summary>
        bool TryWriteMapped(DataBufferMappedWriteCallback callback);

        bool TryReadMapped<TState>(ref TState state, DataBufferMappedReadCallback<TState> callback)
            where TState : allows ref struct;

        bool TryWriteMapped<TState>(ref TState state, DataBufferMappedWriteCallback<TState> callback)
            where TState : allows ref struct;

        ulong BackendAllocatedByteSize => 0ul;
        ulong BackendUploadedByteCount => 0ul;
        bool BackendHasPendingUpload => false;
        bool BackendIsReadyForGpuUse => false;
        bool BackendIsPersistentlyMapped => false;
        XRBufferResolvedRoute BackendResolvedRoute => XRBufferResolvedRoute.Unknown;

        /// <summary>
        /// Ensures the backend allocation needed for GPU use has been requested.
        /// Implementations must preserve their normal render-thread scheduling rules.
        /// </summary>
        void EnsureStorageAllocatedForGpuUse()
        {
        }

        /// <summary>
        /// Tries to expose the backend binding identifier used by diagnostics.
        /// Backends without integer binding identifiers return <see langword="false"/>.
        /// </summary>
        bool TryGetBindingId(out uint bindingId)
        {
            bindingId = 0u;
            return false;
        }

        bool TryGetGpuAddress(out ulong address, out string downgradeReason)
        {
            address = 0ul;
            downgradeReason = "This backend does not expose buffer device addresses.";
            return false;
        }
    }
}
