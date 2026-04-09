using Silk.NET.Core.Contexts;
using Silk.NET.OpenGL;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private const string SparseTextureExtension = "GL_ARB_sparse_texture";
    private const string SparseTexture2Extension = "GL_ARB_sparse_texture2";

    private readonly Dictionary<ESizedInternalFormat, SparseTextureStreamingSupport> _sparseTextureSupportByFormat = [];

    private bool _hasArbSparseTexture;
    private bool _hasArbSparseTexture2;
    private unsafe GlGetInternalformativDelegate? _glGetInternalformativ;
    private unsafe GlTexPageCommitmentArbDelegate? _glTexPageCommitmentArb;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private unsafe delegate void GlGetInternalformativDelegate(GLEnum target, GLEnum internalFormat, GLEnum pname, uint bufSize, int* parameters);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private unsafe delegate void GlTexPageCommitmentArbDelegate(GLEnum target, int level, int xoffset, int yoffset, int zoffset, uint width, uint height, uint depth, byte commit);

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
        if (_glGetInternalformativ is null || _glTexPageCommitmentArb is null)
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
        if (_glGetInternalformativ is null)
        {
            _sparseTextureSupportByFormat[format] = SparseTextureStreamingSupport.Unsupported("glGetInternalformativ is not available.");
            return;
        }

        unsafe
        {
            int numVirtualPageSizes = 0;
            _glGetInternalformativ(
                GLEnum.Texture2D,
                ToGLEnum(format),
                GLEnum.NumVirtualPageSizesArb,
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
                _glGetInternalformativ(
                    GLEnum.Texture2D,
                    ToGLEnum(format),
                    GLEnum.VirtualPageSizeXArb,
                    (uint)numVirtualPageSizes,
                    pageSizeXsPtr);
                _glGetInternalformativ(
                    GLEnum.Texture2D,
                    ToGLEnum(format),
                    GLEnum.VirtualPageSizeYArb,
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

        if (_glGetInternalformativ is null
            && nativeContext.TryGetProcAddress("glGetInternalformativ", out IntPtr getInternalformatProc)
            && getInternalformatProc != IntPtr.Zero)
        {
            _glGetInternalformativ = Marshal.GetDelegateForFunctionPointer<GlGetInternalformativDelegate>(getInternalformatProc);
        }

        if (_glTexPageCommitmentArb is null
            && nativeContext.TryGetProcAddress("glTexPageCommitmentARB", out IntPtr texPageCommitmentProc)
            && texPageCommitmentProc != IntPtr.Zero)
        {
            _glTexPageCommitmentArb = Marshal.GetDelegateForFunctionPointer<GlTexPageCommitmentArbDelegate>(texPageCommitmentProc);
        }
    }

    internal bool TryCommitSparseTexturePages(
        GLEnum target,
        int level,
        uint width,
        uint height,
        bool commit)
    {
        unsafe
        {
            if (_glTexPageCommitmentArb is null)
                return false;

            _glTexPageCommitmentArb(
                target,
                level,
                0,
                0,
                0,
                Math.Max(1u, width),
                Math.Max(1u, height),
                1u,
                commit ? (byte)1 : (byte)0);
            return true;
        }
    }
}
