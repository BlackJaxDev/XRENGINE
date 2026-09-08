using Silk.NET.OpenGL;

namespace XREngine.Rendering.OpenGL;

/// <summary>
/// VAO over the immutable canonical uint-index atlas. Vertices are pulled from
/// the selected static or deformed SSBO using gl_VertexID. It owns no
/// geometry; scene-publication fencing remains with the table uploader.
/// </summary>
internal sealed class OpenGLAdvancedVisibilityAtlasVao : IDisposable
{
    private readonly OpenGLRenderer _renderer;
    private uint _vao;
    private uint _indices;

    internal OpenGLAdvancedVisibilityAtlasVao(OpenGLRenderer renderer)
        => _renderer = renderer;

    internal bool TryBind(uint indices, out string reason)
    {
        if (indices == 0u)
        {
            reason = "The canonical Advanced geometry atlas has no uint-index buffer.";
            return false;
        }
        if (_vao == 0u)
        {
            _vao = _renderer.RawGL.GenVertexArray();
            if (_vao == 0u)
            {
                reason = "OpenGL could not create the canonical Advanced visibility VAO.";
                return false;
            }
        }

        _renderer.RawGL.BindVertexArray(_vao);
        if (_indices != indices)
        {
            _renderer.RawGL.BindBuffer(GLEnum.ElementArrayBuffer, indices);
            _indices = indices;
        }
        reason = "Ready";
        return true;
    }

    public void Dispose()
    {
        if (_vao != 0u && RuntimeEngine.IsRenderThread)
            _renderer.RawGL.DeleteVertexArray(_vao);
        _vao = 0u;
        _indices = 0u;
    }
}
