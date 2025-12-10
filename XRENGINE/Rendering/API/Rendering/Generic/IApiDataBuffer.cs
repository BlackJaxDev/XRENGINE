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
    }
}