using XREngine.Data;

namespace XREngine.Rendering
{
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
        VoidPtr? GetMappedAddress();

        ulong BackendAllocatedByteSize => 0ul;
        ulong BackendUploadedByteCount => 0ul;
        bool BackendHasPendingUpload => false;
        bool BackendIsReadyForGpuUse => false;
        bool BackendIsPersistentlyMapped => GetMappedAddress().HasValue;
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
