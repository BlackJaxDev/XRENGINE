// Adapted from UltralightNet.OpenGL by SupinePandora43 (MIT License)
// https://github.com/SupinePandora43/UltralightNet
//
// Modified for XREngine:
//  - Removed dependency on UltralightNet.GPUCommon project (inlined Uniforms)
//  - Shaders loaded from embedded resources in this assembly
//  - Uses XREngine.Rendering.UI.Ultralight namespace

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.OpenGL;
using UltralightNet;
using UltralightNet.Platform;

namespace XREngine.Rendering.UI.Ultralight;

/// <summary>
/// Ultralight OpenGL GPU driver that implements <see cref="IGPUDriver"/>.
/// Manages OpenGL textures, render buffers, geometry, and shader command lists
/// to let Ultralight render directly via the GPU rather than CPU software path.
/// </summary>
public unsafe class OpenGLGPUDriver : IGPUDriver
{
    private readonly GL _gl;

    private readonly uint _pathProgram;
    private readonly uint _fillProgram;
    private readonly uint _ubo;

    public readonly List<TextureEntry> Textures = new();
    private readonly List<GeometryEntry> _geometries = new();
    public readonly List<RenderBufferEntry> RenderBuffers = new();

    private readonly Stack<int> _freeTextures = new();
    private readonly Stack<int> _freeGeometry = new();
    private readonly Stack<int> _freeRenderBuffers = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Check(string? operation = null, [CallerMemberName] string? caller = null, [CallerLineNumber] int line = default)
    {
#if DEBUG
        GLEnum error;
        while ((error = _gl.GetError()) is not GLEnum.NoError)
        {
            string op = operation is not null ? $" after {operation}" : "";
            Debug.Out($"[UltralightGL] {caller}:{line}{op} — {error}");
        }
#endif
    }

    private readonly bool _dsa;
    private readonly uint _samples;

    public OpenGLGPUDriver(GL glapi, uint samples = 0)
    {
        _gl = glapi;

        // DSA availability
        _dsa = _gl.GetInteger(GLEnum.MajorVersion) >= 4 && _gl.GetInteger(GLEnum.MinorVersion) >= 5;

        // MSAA samples
        _samples = samples is 0 ? (4 <= _gl.GetInteger(GLEnum.MaxSamples) ? 4u : 1u) : samples;

        Textures.Add(new TextureEntry());
        _geometries.Add(new GeometryEntry());
        RenderBuffers.Add(new RenderBufferEntry());

        // Save state
        uint glProgram;
        _gl.GetInteger(GetPName.CurrentProgram, (int*)&glProgram);

        _pathProgram = BuildProgram("shader_fill_path.vert", "shader_fill_path.frag",
            ("in_Position", 0), ("in_Color", 1), ("in_TexCoord", 2));

        _fillProgram = BuildProgram("shader_fill.vert", "shader_fill.frag",
            ("in_Position", 0), ("in_Color", 1), ("in_TexCoord", 2),
            ("in_ObjCoord", 3), ("in_Data0", 4), ("in_Data1", 5),
            ("in_Data2", 6), ("in_Data3", 7), ("in_Data4", 8),
            ("in_Data5", 9), ("in_Data6", 10));

        _gl.UseProgram(_fillProgram);
        _gl.Uniform1(_gl.GetUniformLocation(_fillProgram, "Texture1"), 0);
        _gl.Uniform1(_gl.GetUniformLocation(_fillProgram, "Texture2"), 1);

        Check();

        if (_dsa)
        {
            _ubo = _gl.CreateBuffer();
            _gl.NamedBufferData(_ubo, 768, null, (GLEnum)BufferUsageARB.DynamicDraw);
        }
        else
        {
            _ubo = _gl.GenBuffer();
            _gl.BindBuffer(GLEnum.UniformBuffer, _ubo);
            _gl.BufferData(GLEnum.UniformBuffer, 768, null, GLEnum.DynamicDraw);
            _gl.BindBuffer(GLEnum.UniformBuffer, 0);
        }
        _gl.BindBufferRange(GLEnum.UniformBuffer, 0, _ubo, 0, 768);

        // Restore state
        _gl.UseProgram(glProgram);
    }

    private uint BuildProgram(string vertName, string fragName, params (string name, uint loc)[] attribs)
    {
        uint vert = _gl.CreateShader(ShaderType.VertexShader);
        uint frag = _gl.CreateShader(ShaderType.FragmentShader);

        _gl.ShaderSource(vert, GetShader(vertName));
        _gl.ShaderSource(frag, GetShader(fragName));

        _gl.CompileShader(vert);
        _gl.CompileShader(frag);

#if DEBUG
        string vertLog = _gl.GetShaderInfoLog(vert);
        if (!string.IsNullOrWhiteSpace(vertLog))
            Debug.Out($"[UltralightGL] Vertex shader ({vertName}) log: {vertLog}");
        string fragLog = _gl.GetShaderInfoLog(frag);
        if (!string.IsNullOrWhiteSpace(fragLog))
            Debug.Out($"[UltralightGL] Fragment shader ({fragName}) log: {fragLog}");
#endif

        uint program = _gl.CreateProgram();
        _gl.AttachShader(program, vert);
        _gl.AttachShader(program, frag);

        foreach (var (name, loc) in attribs)
            _gl.BindAttribLocation(program, loc, name);

        _gl.LinkProgram(program);
        _gl.GetProgram(program, GLEnum.LinkStatus, out var status);
        if (status == 0)
            throw new Exception($"[UltralightGL] Program failed to link: {_gl.GetProgramInfoLog(program)}");

        _gl.DetachShader(program, vert);
        _gl.DetachShader(program, frag);
        _gl.DeleteShader(vert);
        _gl.DeleteShader(frag);

        _gl.UniformBlockBinding(program, _gl.GetUniformBlockIndex(program, "Uniforms"), 0);
        return program;
    }

    private static string GetShader(string name)
    {
        Assembly assembly = typeof(OpenGLGPUDriver).Assembly;
        string resourceName = "XREngine.Rendering.UI.Ultralight.Shaders." + name;
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            throw new FileNotFoundException($"Embedded shader resource not found: {resourceName}");
        using StreamReader reader = new(stream, Encoding.UTF8, false, 4096, true);
        return reader.ReadToEnd();
    }

    // ─── IGPUDriver: Texture ───

    public uint NextTextureId()
    {
        if (_freeTextures.TryPop(out int freeId))
        {
            Textures[freeId] = new TextureEntry();
            return (uint)freeId;
        }
        Textures.Add(new TextureEntry());
        return (uint)Textures.Count - 1;
    }

    public void CreateTexture(uint entryId, ULBitmap bitmap)
    {
        bool isRT = bitmap.IsEmpty;
        uint textureId;
        uint multisampledTextureId = 0;
        uint width = bitmap.Width;
        uint height = bitmap.Height;
        uint rowBytes = bitmap.RowBytes;
        uint bpp = bitmap.Bpp;

        if (_dsa)
        {
            if (isRT)
            {
                _gl.CreateTextures(TextureTarget.Texture2D, 1, &textureId);
                _gl.TextureStorage2D(textureId, 1, SizedInternalFormat.Rgba8, width, height);

                if (_samples is not 1)
                {
                    _gl.CreateTextures(TextureTarget.Texture2DMultisample, 1, &multisampledTextureId);
                    _gl.TextureStorage2DMultisample(multisampledTextureId, _samples, SizedInternalFormat.Rgba8, width, height, true);
                }
            }
            else
            {
                _gl.CreateTextures(TextureTarget.Texture2D, 1, &textureId);
                if (rowBytes != width * bpp)
                {
                    _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
                    _gl.PixelStore(PixelStoreParameter.UnpackRowLength, (int)(rowBytes / bpp));
                }
                void* pixels = (void*)bitmap.LockPixels();
                if (bitmap.Format is ULBitmapFormat.BGRA8_UNORM_SRGB)
                {
                    _gl.TextureStorage2D(textureId, 1, SizedInternalFormat.Rgba8, width, height);
                    _gl.TextureSubImage2D(textureId, 0, 0, 0, width, height, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
                }
                else
                {
                    _gl.TextureStorage2D(textureId, 1, SizedInternalFormat.R8, width, height);
                    _gl.TextureSubImage2D(textureId, 0, 0, 0, width, height, PixelFormat.Red, PixelType.UnsignedByte, pixels);
                }
                bitmap.UnlockPixels();

                _gl.TextureParameterI(textureId, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
                _gl.TextureParameterI(textureId, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
                _gl.TextureParameterI(textureId, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
                _gl.TextureParameterI(textureId, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
                _gl.TextureParameterI(textureId, TextureParameterName.TextureWrapR, (int)GLEnum.Repeat);
            }
        }
        else
        {
            textureId = _gl.GenTexture();
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, textureId);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

            if (isRT)
            {
                _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8, width, height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, null);
                if (_samples is not 1)
                {
                    _gl.GenTextures(1, &multisampledTextureId);
                    _gl.BindTexture(TextureTarget.Texture2DMultisample, multisampledTextureId);
                    _gl.TexImage2DMultisample(TextureTarget.Texture2DMultisample, _samples, GLEnum.Rgba8, width, height, true);
                }
            }
            else
            {
                if (rowBytes != width * bpp)
                {
                    _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
                    _gl.PixelStore(PixelStoreParameter.UnpackRowLength, (int)(rowBytes / bpp));
                }
                void* pixels = (void*)bitmap.LockPixels();
                if (bitmap.Format is ULBitmapFormat.BGRA8_UNORM_SRGB)
                    _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8, width, height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
                else
                    _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.R8, width, height, 0, PixelFormat.Red, PixelType.UnsignedByte, pixels);
                bitmap.UnlockPixels();
            }
        }

        Textures[(int)entryId].TextureId = textureId;
        Textures[(int)entryId].MultisampledTextureId = multisampledTextureId;
        Textures[(int)entryId].Width = width;
        Textures[(int)entryId].Height = height;
    }

    public void UpdateTexture(uint entryId, ULBitmap bitmap)
    {
        Check();
        uint textureId = Textures[(int)entryId].TextureId;
        uint width = bitmap.Width;
        uint height = bitmap.Height;
        uint rowBytes = bitmap.RowBytes;
        uint bpp = bitmap.Bpp;

        if (_dsa)
        {
            if (rowBytes != width * bpp)
            {
                _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
                _gl.PixelStore(PixelStoreParameter.UnpackRowLength, (int)(rowBytes / bpp));
            }
            void* pixels = (void*)bitmap.LockPixels();
            if (bitmap.Format is ULBitmapFormat.BGRA8_UNORM_SRGB)
                _gl.TextureSubImage2D(textureId, 0, 0, 0, width, height, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
            else
                _gl.TextureSubImage2D(textureId, 0, 0, 0, width, height, PixelFormat.Red, PixelType.UnsignedByte, pixels);
            bitmap.UnlockPixels();
        }
        else
        {
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, textureId);
            if (rowBytes != width * bpp)
            {
                _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
                _gl.PixelStore(PixelStoreParameter.UnpackRowLength, (int)(rowBytes / bpp));
            }
            void* pixels = (void*)bitmap.LockPixels();
            if (bitmap.Format is ULBitmapFormat.BGRA8_UNORM_SRGB)
                _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgb8, width, height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
            else
                _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.R8, width, height, 0, PixelFormat.Red, PixelType.UnsignedByte, pixels);
            bitmap.UnlockPixels();
        }
        Check();
    }

    public void DestroyTexture(uint id)
    {
        var entry = Textures[(int)id];
        _gl.DeleteTexture(entry.TextureId);
        if (entry.MultisampledTextureId is not 0)
            _gl.DeleteTexture(entry.MultisampledTextureId);
        _freeTextures.Push((int)id);
    }

    // ─── IGPUDriver: RenderBuffer ───

    public uint NextRenderBufferId()
    {
        if (_freeRenderBuffers.TryPop(out int freeId))
        {
            RenderBuffers[freeId] = new RenderBufferEntry();
            return (uint)freeId;
        }
        RenderBuffers.Add(new RenderBufferEntry());
        return (uint)RenderBuffers.Count - 1;
    }

    public void CreateRenderBuffer(uint id, ULRenderBuffer renderBuffer)
    {
        var entry = RenderBuffers[(int)id];
        entry.TexEntry = Textures[(int)renderBuffer.TextureId];

        uint framebuffer;
        uint multisampledFramebuffer = 0;
        uint textureGLId = entry.TexEntry.TextureId;
        uint multisampledTextureId = entry.TexEntry.MultisampledTextureId;

        if (_dsa)
        {
            framebuffer = _gl.CreateFramebuffer();
            _gl.NamedFramebufferTexture(framebuffer, FramebufferAttachment.ColorAttachment0, textureGLId, 0);
            _gl.NamedFramebufferDrawBuffer(framebuffer, ColorBuffer.ColorAttachment0);

            if (multisampledTextureId is not 0)
            {
                multisampledFramebuffer = _gl.CreateFramebuffer();
                _gl.NamedFramebufferTexture(multisampledFramebuffer, FramebufferAttachment.ColorAttachment0, multisampledTextureId, 0);
                _gl.NamedFramebufferDrawBuffer(multisampledFramebuffer, ColorBuffer.ColorAttachment0);
            }
        }
        else
        {
            framebuffer = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            _gl.BindTexture(TextureTarget.Texture2D, textureGLId);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, textureGLId, 0);
            _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);

            if (multisampledTextureId is not 0)
            {
                multisampledFramebuffer = _gl.GenFramebuffer();
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, multisampledFramebuffer);
                _gl.BindTexture(TextureTarget.Texture2DMultisample, multisampledTextureId);
                _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2DMultisample, multisampledTextureId, 0);
                _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
            }
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        entry.TexEntry.Framebuffer = framebuffer;
        entry.TexEntry.MultisampledFramebuffer = multisampledFramebuffer;
    }

    public void DestroyRenderBuffer(uint id)
    {
        var entry = RenderBuffers[(int)id];
        _gl.DeleteFramebuffer(entry.TexEntry.Framebuffer);
        if (entry.TexEntry.MultisampledFramebuffer is not 0)
            _gl.DeleteFramebuffer(entry.TexEntry.MultisampledFramebuffer);
        _freeRenderBuffers.Push((int)id);
    }

    // ─── IGPUDriver: Geometry ───

    public uint NextGeometryId()
    {
        if (_freeGeometry.TryPop(out int freeId))
        {
            _geometries[freeId] = new GeometryEntry();
            return (uint)freeId;
        }
        _geometries.Add(new GeometryEntry());
        return (uint)_geometries.Count - 1;
    }

    public void CreateGeometry(uint id, ULVertexBuffer vb, ULIndexBuffer ib)
    {
        var entry = _geometries[(int)id];
        uint vao, vbo, ebo;

        if (_dsa)
        {
            vao = _gl.CreateVertexArray();
            vbo = _gl.CreateBuffer();
            ebo = _gl.CreateBuffer();

            _gl.NamedBufferData(vbo, vb.size, vb.data, GLEnum.DynamicDraw);
            _gl.NamedBufferData(ebo, ib.size, ib.data, GLEnum.DynamicDraw);

            if (vb.Format is ULVertexBufferFormat.VBF_2f_4ub_2f_2f_28f)
            {
                SetupVertexAttribDSA(vao, vbo, 140,
                    (0, 2, GLEnum.Float, false, 0),
                    (1, 4, GLEnum.UnsignedByte, true, 8),
                    (2, 2, GLEnum.Float, false, 12),
                    (3, 2, GLEnum.Float, false, 20),
                    (4, 4, GLEnum.Float, false, 28),
                    (5, 4, GLEnum.Float, false, 44),
                    (6, 4, GLEnum.Float, false, 60),
                    (7, 4, GLEnum.Float, false, 76),
                    (8, 4, GLEnum.Float, false, 92),
                    (9, 4, GLEnum.Float, false, 108),
                    (10, 4, GLEnum.Float, false, 124));
            }
            else
            {
                SetupVertexAttribDSA(vao, vbo, 20,
                    (0, 2, GLEnum.Float, false, 0),
                    (1, 4, GLEnum.UnsignedByte, true, 8),
                    (2, 2, GLEnum.Float, false, 12));
            }
            _gl.VertexArrayElementBuffer(vao, ebo);
        }
        else
        {
            vao = _gl.GenVertexArray();
            _gl.BindVertexArray(vao);

            vbo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, vb.size, vb.data, BufferUsageARB.DynamicDraw);

            if (vb.Format is ULVertexBufferFormat.VBF_2f_4ub_2f_2f_28f)
            {
                const uint stride = 140;
                _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
                _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.UnsignedByte, true, stride, (void*)8);
                _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)12);
                _gl.VertexAttribPointer(3, 2, VertexAttribPointerType.Float, false, stride, (void*)20);
                _gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, (void*)28);
                _gl.VertexAttribPointer(5, 4, VertexAttribPointerType.Float, false, stride, (void*)44);
                _gl.VertexAttribPointer(6, 4, VertexAttribPointerType.Float, false, stride, (void*)60);
                _gl.VertexAttribPointer(7, 4, VertexAttribPointerType.Float, false, stride, (void*)76);
                _gl.VertexAttribPointer(8, 4, VertexAttribPointerType.Float, false, stride, (void*)92);
                _gl.VertexAttribPointer(9, 4, VertexAttribPointerType.Float, false, stride, (void*)108);
                _gl.VertexAttribPointer(10, 4, VertexAttribPointerType.Float, false, stride, (void*)124);
                for (uint i = 0; i <= 10; i++) _gl.EnableVertexAttribArray(i);
            }
            else
            {
                const uint stride = 20;
                _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
                _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.UnsignedByte, true, stride, (void*)8);
                _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)12);
                for (uint i = 0; i <= 2; i++) _gl.EnableVertexAttribArray(i);
            }

            ebo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, ib.size, ib.data, BufferUsageARB.DynamicDraw);
            _gl.BindVertexArray(0);
        }

        entry.Vao = vao;
        entry.Vbo = vbo;
        entry.Ebo = ebo;
        entry.VboSize = (nuint)vb.size;
        entry.EboSize = (nuint)ib.size;
    }

    private void SetupVertexAttribDSA(uint vao, uint vbo, uint stride, params (uint index, int size, GLEnum type, bool normalized, uint offset)[] attribs)
    {
        foreach (var (index, size, type, normalized, offset) in attribs)
        {
            _gl.EnableVertexArrayAttrib(vao, index);
            _gl.VertexArrayAttribBinding(vao, index, 0);
            _gl.VertexArrayAttribFormat(vao, index, size, type, normalized, offset);
        }
        _gl.VertexArrayVertexBuffer(vao, 0, vbo, 0, stride);
    }

    public void UpdateGeometry(uint id, ULVertexBuffer vb, ULIndexBuffer ib)
    {
        var entry = _geometries[(int)id];
        if (entry.Vao is 0 || entry.Vbo is 0 || entry.Ebo is 0)
        {
            CreateGeometry(id, vb, ib);
            return;
        }

        // If sizes changed, the buffer must be reallocated — destroy and recreate.
        if ((nuint)vb.size != entry.VboSize || (nuint)ib.size != entry.EboSize)
        {
            DestroyGeometry(id);
            CreateGeometry(id, vb, ib);
            return;
        }

        // Same size — use SubData to update in-place (avoids DSA realloc driver bugs).
        if (_dsa)
        {
            _gl.NamedBufferSubData(entry.Vbo, 0, vb.size, vb.data);
            Check($"NamedBufferSubData(VBO={entry.Vbo}, size={vb.size})");
            _gl.NamedBufferSubData(entry.Ebo, 0, ib.size, ib.data);
            Check($"NamedBufferSubData(EBO={entry.Ebo}, size={ib.size})");
        }
        else
        {
            _gl.BindVertexArray(entry.Vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, entry.Vbo);
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)vb.size, vb.data);
            Check($"BufferSubData(VBO={entry.Vbo}, size={vb.size})");
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, entry.Ebo);
            _gl.BufferSubData(BufferTargetARB.ElementArrayBuffer, 0, ib.size, ib.data);
            Check($"BufferSubData(EBO={entry.Ebo}, size={ib.size})");
            _gl.BindVertexArray(0);
        }
    }

    public void DestroyGeometry(uint id)
    {
        var entry = _geometries[(int)id];
        _gl.DeleteBuffer(entry.Ebo);
        _gl.DeleteBuffer(entry.Vbo);
        _gl.DeleteVertexArray(entry.Vao);
        _freeGeometry.Push((int)id);
    }

    // ─── IGPUDriver: Command List ───

    public void UpdateCommandList(ULCommandList commandList)
    {
        Check("entry (residual)");
        uint glLastProgram = default;
        uint glProgram = default;
        uint glViewportWidth = unchecked((uint)-1);
        uint glViewportHeight = unchecked((uint)-1);

        if (commandList.size is 0)
            return;

        _gl.GetInteger(GetPName.CurrentProgram, (int*)&glLastProgram);
        glProgram = glLastProgram;
        _gl.Disable(EnableCap.ScissorTest);
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Never);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        Check("initial state setup");

        if (!_dsa)
        {
            _gl.BindBuffer(BufferTargetARB.UniformBuffer, _ubo);
            Check($"BindBuffer(UBO={_ubo})");
        }

        uint currentFramebuffer = uint.MaxValue;
        ULIntRect? currentScissors = null;
        bool currentBlend = true;

        UltralightUniforms uniforms = default;

        foreach (var command in commandList.AsSpan())
        {
            var gpuState = command.GPUState;
            var renderBufferEntry = RenderBuffers[(int)gpuState.RenderBufferId];
            var rtTextureEntry = renderBufferEntry.TexEntry;

            var framebufferToUse = rtTextureEntry.MultisampledFramebuffer is not 0
                ? rtTextureEntry.MultisampledFramebuffer
                : rtTextureEntry.Framebuffer;

            if (currentFramebuffer != framebufferToUse)
            {
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebufferToUse);
                Check($"BindFramebuffer({framebufferToUse})");
                currentFramebuffer = framebufferToUse;
                renderBufferEntry.Dirty = true;
                if (rtTextureEntry.MultisampledFramebuffer != rtTextureEntry.Framebuffer &&
                    rtTextureEntry.MultisampledFramebuffer is not 0)
                    rtTextureEntry.NeedsConversion = true;
            }

            if (command.CommandType is ULCommandType.DrawGeometry)
            {
                if (glViewportWidth != gpuState.ViewportWidth || glViewportHeight != gpuState.ViewportHeight)
                {
                    _gl.Viewport(0, 0, gpuState.ViewportWidth, gpuState.ViewportHeight);
                    glViewportWidth = gpuState.ViewportWidth;
                    glViewportHeight = gpuState.ViewportHeight;
                }

                uint program = gpuState.ShaderType is ULShaderType.Fill ? _fillProgram : _pathProgram;
                if (glProgram != program) _gl.UseProgram(program);
                glProgram = program;

                var geometryEntry = _geometries[(int)command.GeometryId];
                if (geometryEntry.Vao is 0 || geometryEntry.Ebo is 0)
                    continue;

                // Uniforms
                uniforms.State.X = gpuState.ViewportWidth;
                uniforms.State.Y = gpuState.ViewportHeight;
                uniforms.Transform = gpuState.Transform.ApplyProjection(gpuState.ViewportWidth, gpuState.ViewportHeight, true);
                gpuState.Scalar.CopyTo(new Span<float>(&uniforms.Scalar4_0.W, 8));
                gpuState.Vector.CopyTo(new Span<Vector4>(&uniforms.Vector_0, 8));
                gpuState.Clip.CopyTo(new Span<Matrix4x4>(&uniforms.Clip_0.M11, 8));
                uniforms.ClipSize = (uint)gpuState.ClipSize;

                if (_dsa)
                {
                    _gl.NamedBufferData(_ubo, 768, &uniforms, GLEnum.DynamicDraw);
                    Check($"NamedBufferData(UBO={_ubo})");
                }
                else
                {
                    _gl.BufferData(BufferTargetARB.UniformBuffer, 768, &uniforms, BufferUsageARB.DynamicDraw);
                    Check("BufferData(UBO)");
                }

                _gl.BindVertexArray(geometryEntry.Vao);
                Check($"BindVertexArray(VAO={geometryEntry.Vao})");
                bool rebindFramebuffer = false;

                if (gpuState.ShaderType is ULShaderType.Fill)
                {
                    if ((uint)Textures.Count > gpuState.Texture1Id)
                    {
                        TextureEntry textureEntry = Textures[(int)gpuState.Texture1Id];
                        if (textureEntry.NeedsConversion)
                        {
                            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, textureEntry.MultisampledFramebuffer);
                            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, textureEntry.Framebuffer);
                            _gl.BlitFramebuffer(0, 0, (int)textureEntry.Width, (int)textureEntry.Height,
                                0, 0, (int)textureEntry.Width, (int)textureEntry.Height,
                                ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
                            rebindFramebuffer = true;
                        }
                        if (_dsa)
                            _gl.BindTextureUnit(0, textureEntry.TextureId);
                        else
                        {
                            _gl.ActiveTexture(GLEnum.Texture0);
                            _gl.BindTexture(GLEnum.Texture2D, textureEntry.TextureId);
                        }
                    }

                    if (Textures.Count > gpuState.Texture2Id && gpuState.Texture2Id is not 0)
                    {
                        TextureEntry textureEntry = Textures[(int)gpuState.Texture2Id];
                        if (textureEntry.NeedsConversion)
                        {
                            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, textureEntry.MultisampledFramebuffer);
                            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, textureEntry.Framebuffer);
                            _gl.BlitFramebuffer(0, 0, (int)textureEntry.Width, (int)textureEntry.Height,
                                0, 0, (int)textureEntry.Width, (int)textureEntry.Height,
                                ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
                            rebindFramebuffer = true;
                        }
                        if (_dsa)
                            _gl.BindTextureUnit(1, textureEntry.TextureId);
                        else
                        {
                            _gl.ActiveTexture(GLEnum.Texture1);
                            _gl.BindTexture(GLEnum.Texture2D, textureEntry.TextureId);
                        }
                    }
                }

                if (rebindFramebuffer)
                    _gl.BindFramebuffer(FramebufferTarget.Framebuffer, currentFramebuffer);

                if (currentScissors is not null != gpuState.EnableScissor)
                {
                    if (gpuState.EnableScissor)
                    {
                        if (currentScissors is null) _gl.Enable(EnableCap.ScissorTest);
                        _gl.Scissor(gpuState.ScissorRect.Left, gpuState.ScissorRect.Top,
                            (uint)(gpuState.ScissorRect.Right - gpuState.ScissorRect.Left),
                            (uint)(gpuState.ScissorRect.Bottom - gpuState.ScissorRect.Top));
                        currentScissors = gpuState.ScissorRect;
                    }
                    else
                    {
                        _gl.Disable(EnableCap.ScissorTest);
                        currentScissors = null;
                    }
                }

                if (currentBlend != gpuState.EnableBlend)
                {
                    if (gpuState.EnableBlend) _gl.Enable(EnableCap.Blend);
                    else _gl.Disable(EnableCap.Blend);
                    currentBlend = gpuState.EnableBlend;
                }

                Check("pre-DrawElements");
                _gl.DrawElements(PrimitiveType.Triangles, command.IndicesCount, DrawElementsType.UnsignedInt,
                    (void*)(command.IndicesOffset * sizeof(uint)));
                Check($"DrawElements(count={command.IndicesCount}, offset={command.IndicesOffset})");
            }
            else if (command.CommandType is ULCommandType.ClearRenderBuffer)
            {
                _gl.Disable(EnableCap.ScissorTest);
                currentScissors = null;
                _gl.ClearColor(0, 0, 0, 0);
                _gl.Clear((uint)GLEnum.ColorBufferBit);
            }
            else
            {
                throw new Exception($"Invalid {nameof(ULCommandType)} value.");
            }
        }

        _gl.UseProgram(glLastProgram);
        Check($"UseProgram({glLastProgram})");
        _gl.BindVertexArray(0);
        Check("BindVertexArray(0)");
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        Check("BindFramebuffer(0)");
    }

    public void Dispose() { }

    // ─── Entry types ───

    public class TextureEntry
    {
        public uint TextureId;
        public uint Framebuffer;
        public uint MultisampledTextureId;
        public uint MultisampledFramebuffer;
        public bool NeedsConversion;
        public uint Width;
        public uint Height;
    }

    private class GeometryEntry
    {
        public uint Vao;
        public uint Vbo;
        public uint Ebo;
        public nuint VboSize;
        public nuint EboSize;
    }

    public class RenderBufferEntry
    {
        public TextureEntry TexEntry = new();
        public bool Dirty;
    }
}
