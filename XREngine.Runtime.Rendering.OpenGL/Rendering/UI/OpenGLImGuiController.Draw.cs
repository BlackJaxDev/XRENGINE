// Draw-device behavior adapted from Silk.NET ImGuiController v2.23.0.
// Copyright The .NET Foundation. Licensed under the MIT license.
// https://github.com/dotnet/Silk.NET/blob/v2.23.0/src/OpenGL/Extensions/Silk.NET.OpenGL.Extensions.ImGui/ImGuiController.cs
using System.Numerics;
using ImGuiNET;
using Silk.NET.OpenGL;

namespace XREngine.Rendering.OpenGL;

internal sealed unsafe partial class OpenGLImGuiController
{
    private readonly object _drawSync = new();
    private readonly Dictionary<nint, uint> _vertexArrays = [];

    private const string VertexShaderSource = """
        #version 330 core
        layout (location = 0) in vec2 Position;
        layout (location = 1) in vec2 UV;
        layout (location = 2) in vec4 Color;
        uniform mat4 ProjMtx;
        out vec2 FragUV;
        out vec4 FragColor;
        void main() {
            FragUV = UV;
            FragColor = Color;
            gl_Position = ProjMtx * vec4(Position, 0.0, 1.0);
        }
        """;

    private const string FragmentShaderSource = """
        #version 330 core
        in vec2 FragUV;
        in vec4 FragColor;
        uniform sampler2D Texture;
        layout (location = 0) out vec4 OutColor;
        void main() { OutColor = FragColor * texture(Texture, FragUV); }
        """;

    private void CreateDeviceResources()
    {
        uint vertex = CompileShader(ShaderType.VertexShader, VertexShaderSource);
        uint fragment = CompileShader(ShaderType.FragmentShader, FragmentShaderSource);
        try
        {
            _program = _gl.CreateProgram();
            _gl.AttachShader(_program, vertex);
            _gl.AttachShader(_program, fragment);
            _gl.LinkProgram(_program);
            _gl.GetProgram(_program, GLEnum.LinkStatus, out int linked);
            if (linked == 0)
                throw new InvalidOperationException($"OpenGL ImGui program link failed: {_gl.GetProgramInfoLog(_program)}");
        }
        finally
        {
            _gl.DetachShader(_program, vertex);
            _gl.DetachShader(_program, fragment);
            _gl.DeleteShader(vertex);
            _gl.DeleteShader(fragment);
        }

        _textureLocation = _gl.GetUniformLocation(_program, "Texture");
        _projectionLocation = _gl.GetUniformLocation(_program, "ProjMtx");
        _positionLocation = _gl.GetAttribLocation(_program, "Position");
        _uvLocation = _gl.GetAttribLocation(_program, "UV");
        _colorLocation = _gl.GetAttribLocation(_program, "Color");
        _vertexBuffer = _gl.GenBuffer();
        _indexBuffer = _gl.GenBuffer();
        RebuildFontTexture();
    }

    private uint CompileShader(ShaderType type, string source)
    {
        uint shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int compiled);
        if (compiled != 0)
            return shader;
        string log = _gl.GetShaderInfoLog(shader);
        _gl.DeleteShader(shader);
        throw new InvalidOperationException($"OpenGL ImGui {type} compilation failed: {log}");
    }

    public void RenderDrawData(ImDrawDataPtr drawData, IRuntimeWindowGlContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        context.AssertOwnerThread();
        int framebufferWidth = (int)(drawData.DisplaySize.X * drawData.FramebufferScale.X);
        int framebufferHeight = (int)(drawData.DisplaySize.Y * drawData.FramebufferScale.Y);
        if (framebufferWidth <= 0 || framebufferHeight <= 0)
            return;

        lock (_drawSync)
        {
            _gl.GetInteger(GLEnum.ActiveTexture, out int oldActiveTexture);
            _gl.ActiveTexture(GLEnum.Texture0);
            _gl.GetInteger(GLEnum.CurrentProgram, out int oldProgram);
            _gl.GetInteger(GLEnum.TextureBinding2D, out int oldTexture);
            _gl.GetInteger(GLEnum.SamplerBinding, out int oldSampler);
            _gl.GetInteger(GLEnum.ArrayBufferBinding, out int oldArrayBuffer);
            _gl.GetInteger(GLEnum.VertexArrayBinding, out int oldVertexArray);
            Span<int> oldViewport = stackalloc int[4];
            Span<int> oldScissor = stackalloc int[4];
            Span<int> oldPolygonMode = stackalloc int[2];
            _gl.GetInteger(GLEnum.Viewport, oldViewport);
            _gl.GetInteger(GLEnum.ScissorBox, oldScissor);
            _gl.GetInteger(GLEnum.PolygonMode, oldPolygonMode);
            _gl.GetInteger(GLEnum.BlendSrcRgb, out int oldBlendSrcRgb);
            _gl.GetInteger(GLEnum.BlendDstRgb, out int oldBlendDstRgb);
            _gl.GetInteger(GLEnum.BlendSrcAlpha, out int oldBlendSrcAlpha);
            _gl.GetInteger(GLEnum.BlendDstAlpha, out int oldBlendDstAlpha);
            _gl.GetInteger(GLEnum.BlendEquationRgb, out int oldBlendEquationRgb);
            _gl.GetInteger(GLEnum.BlendEquationAlpha, out int oldBlendEquationAlpha);
            bool blend = _gl.IsEnabled(GLEnum.Blend);
            bool cull = _gl.IsEnabled(GLEnum.CullFace);
            bool depth = _gl.IsEnabled(GLEnum.DepthTest);
            bool stencil = _gl.IsEnabled(GLEnum.StencilTest);
            bool scissor = _gl.IsEnabled(GLEnum.ScissorTest);
            bool restart = _gl.IsEnabled(GLEnum.PrimitiveRestart);

            try
            {
                SetupRenderState(drawData, framebufferWidth, framebufferHeight, context);
                Vector2 clipOffset = drawData.DisplayPos;
                Vector2 clipScale = drawData.FramebufferScale;
                for (int listIndex = 0; listIndex < drawData.CmdListsCount; listIndex++)
                {
                    ImDrawListPtr list = drawData.CmdLists[listIndex];
                    _gl.BufferData(GLEnum.ArrayBuffer, (nuint)(list.VtxBuffer.Size * sizeof(ImDrawVert)),
                        (void*)list.VtxBuffer.Data, GLEnum.StreamDraw);
                    _gl.BufferData(GLEnum.ElementArrayBuffer, (nuint)(list.IdxBuffer.Size * sizeof(ushort)),
                        (void*)list.IdxBuffer.Data, GLEnum.StreamDraw);

                    for (int commandIndex = 0; commandIndex < list.CmdBuffer.Size; commandIndex++)
                    {
                        ImDrawCmdPtr command = list.CmdBuffer[commandIndex];
                        if (command.UserCallback == unchecked((nint)(-1)))
                        {
                            SetupRenderState(drawData, framebufferWidth, framebufferHeight, context);
                            continue;
                        }
                        if (command.UserCallback != 0)
                        {
                            ((delegate* unmanaged[Cdecl]<void*, void*, void>)command.UserCallback)(list.NativePtr, command.NativePtr);
                            continue;
                        }

                        Vector4 clip = command.ClipRect;
                        float left = (clip.X - clipOffset.X) * clipScale.X;
                        float top = (clip.Y - clipOffset.Y) * clipScale.Y;
                        float right = (clip.Z - clipOffset.X) * clipScale.X;
                        float bottom = (clip.W - clipOffset.Y) * clipScale.Y;
                        left = MathF.Max(0, left);
                        top = MathF.Max(0, top);
                        right = MathF.Min(framebufferWidth, right);
                        bottom = MathF.Min(framebufferHeight, bottom);
                        if (right <= left || bottom <= top)
                            continue;

                        _gl.Scissor((int)left, (int)(framebufferHeight - bottom),
                            (uint)(right - left), (uint)(bottom - top));
                        _gl.BindTexture(GLEnum.Texture2D, (uint)(nuint)command.TextureId);
                        _gl.DrawElementsBaseVertex(GLEnum.Triangles, command.ElemCount, GLEnum.UnsignedShort,
                            (void*)(command.IdxOffset * sizeof(ushort)), (int)command.VtxOffset);
                    }
                }
            }
            finally
            {
                _gl.UseProgram((uint)oldProgram);
                _gl.BindTexture(GLEnum.Texture2D, (uint)oldTexture);
                _gl.BindSampler(0, (uint)oldSampler);
                _gl.ActiveTexture((GLEnum)oldActiveTexture);
                _gl.BindVertexArray((uint)oldVertexArray);
                _gl.BindBuffer(GLEnum.ArrayBuffer, (uint)oldArrayBuffer);
                _gl.BlendEquationSeparate((GLEnum)oldBlendEquationRgb, (GLEnum)oldBlendEquationAlpha);
                _gl.BlendFuncSeparate((GLEnum)oldBlendSrcRgb, (GLEnum)oldBlendDstRgb,
                    (GLEnum)oldBlendSrcAlpha, (GLEnum)oldBlendDstAlpha);
                SetEnabled(GLEnum.Blend, blend);
                SetEnabled(GLEnum.CullFace, cull);
                SetEnabled(GLEnum.DepthTest, depth);
                SetEnabled(GLEnum.StencilTest, stencil);
                SetEnabled(GLEnum.ScissorTest, scissor);
                SetEnabled(GLEnum.PrimitiveRestart, restart);
                _gl.PolygonMode(GLEnum.FrontAndBack, (GLEnum)oldPolygonMode[0]);
                _gl.Viewport(oldViewport[0], oldViewport[1], (uint)oldViewport[2], (uint)oldViewport[3]);
                _gl.Scissor(oldScissor[0], oldScissor[1], (uint)oldScissor[2], (uint)oldScissor[3]);
            }
        }
    }

    private void SetupRenderState(ImDrawDataPtr drawData, int width, int height, IRuntimeWindowGlContext context)
    {
        _gl.Enable(GLEnum.Blend);
        _gl.BlendEquation(GLEnum.FuncAdd);
        _gl.BlendFuncSeparate(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha, GLEnum.One, GLEnum.OneMinusSrcAlpha);
        _gl.Disable(GLEnum.CullFace);
        _gl.Disable(GLEnum.DepthTest);
        _gl.Disable(GLEnum.StencilTest);
        _gl.Enable(GLEnum.ScissorTest);
        _gl.Disable(GLEnum.PrimitiveRestart);
        _gl.PolygonMode(GLEnum.FrontAndBack, GLEnum.Fill);
        _gl.Viewport(0, 0, (uint)width, (uint)height);

        float left = drawData.DisplayPos.X;
        float right = left + drawData.DisplaySize.X;
        float top = drawData.DisplayPos.Y;
        float bottom = top + drawData.DisplaySize.Y;
        Span<float> projection = stackalloc float[]
        {
            2.0f / (right - left), 0, 0, 0,
            0, 2.0f / (top - bottom), 0, 0,
            0, 0, -1, 0,
            (right + left) / (left - right), (top + bottom) / (bottom - top), 0, 1,
        };
        _gl.UseProgram(_program);
        _gl.Uniform1(_textureLocation, 0);
        _gl.UniformMatrix4(_projectionLocation, 1, false, projection);
        _gl.BindSampler(0, 0);

        nint handle = context.ContextHandle;
        if (!_vertexArrays.TryGetValue(handle, out uint vao))
        {
            vao = _gl.GenVertexArray();
            _vertexArrays.Add(handle, vao);
        }
        _gl.BindVertexArray(vao);
        _gl.BindBuffer(GLEnum.ArrayBuffer, _vertexBuffer);
        _gl.BindBuffer(GLEnum.ElementArrayBuffer, _indexBuffer);
        _gl.EnableVertexAttribArray((uint)_positionLocation);
        _gl.EnableVertexAttribArray((uint)_uvLocation);
        _gl.EnableVertexAttribArray((uint)_colorLocation);
        _gl.VertexAttribPointer((uint)_positionLocation, 2, GLEnum.Float, false, (uint)sizeof(ImDrawVert), (void*)0);
        _gl.VertexAttribPointer((uint)_uvLocation, 2, GLEnum.Float, false, (uint)sizeof(ImDrawVert), (void*)8);
        _gl.VertexAttribPointer((uint)_colorLocation, 4, GLEnum.UnsignedByte, true, (uint)sizeof(ImDrawVert), (void*)16);
    }

    private void SetEnabled(GLEnum cap, bool enabled)
    {
        if (enabled) _gl.Enable(cap); else _gl.Disable(cap);
    }

    public void ReleaseContextResources(IRuntimeWindowGlContext context)
    {
        context.AssertOwnerThread();
        lock (_drawSync)
            if (_vertexArrays.Remove(context.ContextHandle, out uint vao))
                _gl.DeleteVertexArray(vao);
    }

    private void DestroyDeviceResources()
    {
        if (_window.DesktopGlContext is { } mainContext)
            ReleaseContextResources(mainContext);
        if (_vertexBuffer != 0) _gl.DeleteBuffer(_vertexBuffer);
        if (_indexBuffer != 0) _gl.DeleteBuffer(_indexBuffer);
        if (_fontTexture != 0) _gl.DeleteTexture(_fontTexture);
        if (_program != 0) _gl.DeleteProgram(_program);
    }
}
