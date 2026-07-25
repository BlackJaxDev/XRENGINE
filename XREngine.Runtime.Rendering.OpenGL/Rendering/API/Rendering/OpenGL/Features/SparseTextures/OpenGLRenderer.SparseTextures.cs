using Silk.NET.Core.Contexts;
using Silk.NET.OpenGL;
using System.Collections.Generic;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private const string SparseTextureExtension = "GL_ARB_sparse_texture";
    private const string SparseTexture2Extension = "GL_ARB_sparse_texture2";
    private const GLEnum NumVirtualPageSizesArb = (GLEnum)0x91A8;
    private const GLEnum VirtualPageSizeXArb = (GLEnum)0x9195;
    private const GLEnum VirtualPageSizeYArb = (GLEnum)0x9196;

    private readonly Dictionary<ESizedInternalFormat, SparseTextureStreamingSupport> _sparseTextureSupportByFormat = [];

    private bool _hasArbSparseTexture;
    private bool _hasArbSparseTexture2;
    private nint _glGetInternalformativ;
    private nint _glTexPageCommitmentArb;

    public SparseTextureStreamingSupport GetSparseTextureStreamingSupport(ESizedInternalFormat format)
    {
        if (_sparseTextureSupportByFormat.TryGetValue(format, out SparseTextureStreamingSupport support))
            return support;

        return SparseTextureStreamingSupport.Unsupported($"Sparse texture streaming is not initialized for format {format}.");
    }

    private void InitializeSparseTextureSupport(string[] extensions)
    {
        _hasArbSparseTexture = Array.IndexOf(extensions, SparseTextureExtension) >= 0;
        _hasArbSparseTexture2 = Array.IndexOf(extensions, SparseTexture2Extension) >= 0;

        if (!_hasArbSparseTexture)
        {
            Debug.OpenGL("Sparse textures: GL_ARB_sparse_texture unavailable. Imported texture streaming will use the tiered fallback.");
            _sparseTextureSupportByFormat[ESizedInternalFormat.Rgba8] = SparseTextureStreamingSupport.Unsupported("GL_ARB_sparse_texture is not reported by the current OpenGL context.");
            return;
        }

        LoadSparseTextureDelegates();
        if (_glGetInternalformativ == 0 || _glTexPageCommitmentArb == 0)
        {
            Debug.OpenGLWarning("Sparse textures: required ARB entry points could not be loaded. Imported texture streaming will use the tiered fallback.");
            _sparseTextureSupportByFormat[ESizedInternalFormat.Rgba8] = SparseTextureStreamingSupport.Unsupported("Required sparse texture entry points could not be loaded.");
            return;
        }

        CacheSparseTextureSupportForFormat(ESizedInternalFormat.Rgba8);

        SparseTextureStreamingSupport rgba8Support = GetSparseTextureStreamingSupport(ESizedInternalFormat.Rgba8);
        if (rgba8Support.IsAvailable)
        {
            string tierText = _hasArbSparseTexture2 ? "ARB_sparse_texture + ARB_sparse_texture2" : "ARB_sparse_texture";
            Debug.OpenGL($"Sparse textures: {tierText} available. RGBA8 virtual page size = {rgba8Support.VirtualPageSizeX}x{rgba8Support.VirtualPageSizeY}, pageIndex={rgba8Support.VirtualPageSizeIndex}.");
        }
        else
        {
            Debug.OpenGLWarning($"Sparse textures reported, but RGBA8 sparse streaming is unavailable: {rgba8Support.FailureReason ?? "unknown reason"}");
        }
    }

    private void CacheSparseTextureSupportForFormat(ESizedInternalFormat format)
    {
        if (_glGetInternalformativ == 0)
        {
            _sparseTextureSupportByFormat[format] = SparseTextureStreamingSupport.Unsupported("glGetInternalformativ is not available.");
            return;
        }

        unsafe
        {
            int numVirtualPageSizes = 0;
            InvokeGlGetInternalformativ(
                _glGetInternalformativ,
                GLEnum.Texture2D,
                GLObjectBase.ToGLEnum(format),
                NumVirtualPageSizesArb,
                1,
                &numVirtualPageSizes);

            if (numVirtualPageSizes <= 0)
            {
                _sparseTextureSupportByFormat[format] = SparseTextureStreamingSupport.Unsupported($"Format {format} does not expose any sparse virtual page sizes.");
                return;
            }

            int[] pageSizeXs = new int[numVirtualPageSizes];
            int[] pageSizeYs = new int[numVirtualPageSizes];
            fixed (int* pageSizeXsPtr = pageSizeXs)
            fixed (int* pageSizeYsPtr = pageSizeYs)
            {
                InvokeGlGetInternalformativ(
                    _glGetInternalformativ,
                    GLEnum.Texture2D,
                    GLObjectBase.ToGLEnum(format),
                    VirtualPageSizeXArb,
                    (uint)numVirtualPageSizes,
                    pageSizeXsPtr);
                InvokeGlGetInternalformativ(
                    _glGetInternalformativ,
                    GLEnum.Texture2D,
                    GLObjectBase.ToGLEnum(format),
                    VirtualPageSizeYArb,
                    (uint)numVirtualPageSizes,
                    pageSizeYsPtr);
            }

            int pageIndex = 0;
            uint pageSizeX = 0;
            uint pageSizeY = 0;
            for (int i = 0; i < numVirtualPageSizes; i++)
            {
                if (pageSizeXs[i] <= 0 || pageSizeYs[i] <= 0)
                    continue;

                pageIndex = i;
                pageSizeX = (uint)pageSizeXs[i];
                pageSizeY = (uint)pageSizeYs[i];
                break;
            }

            if (pageSizeX == 0 || pageSizeY == 0)
            {
                _sparseTextureSupportByFormat[format] = SparseTextureStreamingSupport.Unsupported($"Format {format} did not return a usable sparse page size.");
                return;
            }

            _sparseTextureSupportByFormat[format] = new SparseTextureStreamingSupport(
                SupportsSparseTextures: true,
                SupportsSparseTexture2: _hasArbSparseTexture2,
                VirtualPageSizeX: pageSizeX,
                VirtualPageSizeY: pageSizeY,
                VirtualPageSizeIndex: pageIndex);
        }
    }

    private void LoadSparseTextureDelegates()
    {
        if (Window.GLContext is not INativeContext nativeContext)
            return;

        if (_glGetInternalformativ == 0
            && nativeContext.TryGetProcAddress("glGetInternalformativ", out IntPtr getInternalformatProc)
            && getInternalformatProc != IntPtr.Zero)
        {
            _glGetInternalformativ = getInternalformatProc;
        }

        if (_glTexPageCommitmentArb == 0
            && nativeContext.TryGetProcAddress("glTexPageCommitmentARB", out IntPtr texPageCommitmentProc)
            && texPageCommitmentProc != IntPtr.Zero)
        {
            _glTexPageCommitmentArb = texPageCommitmentProc;
        }
    }

    internal bool TryCommitSparseTexturePages(
        GLEnum target,
        int level,
        uint width,
        uint height,
        bool commit)
        => TryCommitSparseTexturePages(target, level, 0, 0, width, height, commit);

    internal bool TryCommitSparseTexturePages(
        GLEnum target,
        int level,
        int xoffset,
        int yoffset,
        uint width,
        uint height,
        bool commit)
    {
        unsafe
        {
            if (_glTexPageCommitmentArb == 0)
                return false;

            InvokeGlTexPageCommitmentArb(
                _glTexPageCommitmentArb,
                target,
                level,
                xoffset,
                yoffset,
                0,
                Math.Max(1u, width),
                Math.Max(1u, height),
                1u,
                commit ? (byte)1 : (byte)0);
            return true;
        }
    }

    private static unsafe void InvokeGlGetInternalformativ(
        nint entryPoint,
        GLEnum target,
        GLEnum internalFormat,
        GLEnum parameterName,
        uint bufferSize,
        int* parameters)
        => ((delegate* unmanaged[Stdcall]<GLEnum, GLEnum, GLEnum, uint, int*, void>)entryPoint)(
            target,
            internalFormat,
            parameterName,
            bufferSize,
            parameters);

    private static unsafe void InvokeGlTexPageCommitmentArb(
        nint entryPoint,
        GLEnum target,
        int level,
        int xOffset,
        int yOffset,
        int zOffset,
        uint width,
        uint height,
        uint depth,
        byte commit)
        => ((delegate* unmanaged[Stdcall]<GLEnum, int, int, int, int, uint, uint, uint, byte, void>)entryPoint)(
            target,
            level,
            xOffset,
            yOffset,
            zOffset,
            width,
            height,
            depth,
            commit);
}
