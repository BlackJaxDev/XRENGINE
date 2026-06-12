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

        bool TryGetGpuAddress(out ulong address, out string downgradeReason)
        {
            address = 0ul;
            downgradeReason = "This backend does not expose buffer device addresses.";
            return false;
        }
    }
}
