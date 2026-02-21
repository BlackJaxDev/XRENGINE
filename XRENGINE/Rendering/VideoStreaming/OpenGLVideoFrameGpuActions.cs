using System;
using System.Linq;
using Silk.NET.OpenGL;
using XREngine.Data.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering.VideoStreaming;

/// <summary>
/// Uploads decoded video frames to an OpenGL texture using double-buffered
/// (ping-pong) Pixel Buffer Objects:
///
///   Each frame uses one of two PBOs in alternation:
///     1. Orphan the current PBO (driver recycles old storage, no sync stall).
///     2. Map the PBO for WRITE_ONLY CPU access.
///     3. memcpy decoded frame data into the mapped pointer.
///     4. Unmap → initiates DMA transfer.
///     5. Bind PBO → glTexSubImage2D(null) → unbind → texture now has new data.
///     6. Advance to the other PBO for the next frame.
///
/// Double buffering ensures the GPU's DMA read of PBO[A] from frame N has a
/// full frame to complete before we touch PBO[A] again in frame N+2.
///
/// After each upload we clear the engine's texture invalidation flag to
/// prevent the engine's own VerifySettings() → PushData() from overwriting
/// our upload with null/stale CPU-side data.
///
/// All GL operations use raw Silk.NET calls to bypass the engine's deferred
/// upload queue.
/// </summary>
internal sealed class OpenGLVideoFrameGpuActions : IVideoFrameGpuActions
{
    private const int PboCount = 2;
    private readonly uint[] _pboIds = new uint[PboCount];
    private uint _pboSizeBytes;
    private int _currentPboIndex;

    private bool _textureAllocated;
    private uint _lastTextureWidth;
    private uint _lastTextureHeight;

    public bool TryPrepareOutput(XRMaterialFrameBuffer frameBuffer, XRMaterial? material, out uint framebufferId, out string? error)
    {
        error = null;
        framebufferId = 0;

        frameBuffer.Material = material;
        frameBuffer.Generate();

        if (frameBuffer.APIWrappers.OfType<GLFrameBuffer>().FirstOrDefault() is not GLFrameBuffer glFbo)
        {
            error = "Unable to locate GL framebuffer wrapper for streaming video output.";
            return false;
        }

        glFbo.Generate();
        framebufferId = glFbo.BindingId;
        if (framebufferId == 0)
        {
            error = "GL framebuffer binding id is zero.";
            return false;
        }

        return true;
    }

    public bool UploadVideoFrame(DecodedVideoFrame frame, XRTexture2D? targetTexture, out string? error)
    {
        error = null;

        if (frame.PixelFormat != VideoPixelFormat.Rgb24)
        {
            error = $"OpenGL uploader only supports {VideoPixelFormat.Rgb24} frames; got {frame.PixelFormat}.";
            return false;
        }

        if (targetTexture is null)
        {
            error = "No target texture is available for OpenGL upload.";
            return false;
        }

        if (AbstractRenderer.Current is not OpenGLRenderer renderer)
        {
            error = "No active OpenGL renderer.";
            return false;
        }

        GL gl = renderer.RawGL;
        ReadOnlyMemory<byte> memory = frame.PackedData;
        if (memory.IsEmpty)
        {
            error = "Decoded video frame contains no pixel data.";
            return false;
        }

        uint w = (uint)frame.Width;
        uint h = (uint)frame.Height;
        uint requiredBytes = (uint)memory.Length;

        // --- Ensure the GL texture object exists and is the right size ---
        var glTex = renderer.GenericToAPI<GLTexture2D>(targetTexture);
        if (glTex is null)
        {
            error = "Failed to obtain GLTexture2D wrapper.";
            return false;
        }

        if (!glTex.IsGenerated)
            glTex.Generate();

        uint texId = glTex.BindingId;
        if (texId == 0)
        {
            error = "GL texture binding id is zero.";
            return false;
        }

        // (Re-)allocate texture storage when dimensions change.
        if (!_textureAllocated || _lastTextureWidth != w || _lastTextureHeight != h)
        {
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            gl.BindTexture(TextureTarget.Texture2D, texId);
            unsafe
            {
                gl.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    InternalFormat.Rgb8,
                    w, h,
                    0,
                    GLEnum.Rgb,
                    GLEnum.UnsignedByte,
                    null);
            }
            gl.BindTexture(TextureTarget.Texture2D, 0);
            _textureAllocated = true;
            _lastTextureWidth = w;
            _lastTextureHeight = h;
        }

        // --- Ensure PBOs exist and are the right size ---
        EnsurePbos(gl, requiredBytes);

        int idx = _currentPboIndex;

        // ---- Step 1: Orphan → Map → memcpy → Unmap (CPU side) ----
        gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, _pboIds[idx]);

        // Orphan: hand back old storage so driver can recycle it while the
        // DMA from the PREVIOUS use of this PBO (2 frames ago) may still
        // be in flight. The driver gives us a fresh allocation.
        unsafe
        {
            gl.BufferData(BufferTargetARB.PixelUnpackBuffer, requiredBytes, null, BufferUsageARB.StreamDraw);
        }

        unsafe
        {
            void* mapped = gl.MapBuffer(BufferTargetARB.PixelUnpackBuffer, BufferAccessARB.WriteOnly);
            if (mapped is null)
            {
                gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
                error = "glMapBuffer returned null.";
                return false;
            }

            ReadOnlySpan<byte> src = memory.Span;
            fixed (byte* srcPtr = src)
            {
                System.Buffer.MemoryCopy(srcPtr, mapped, requiredBytes, requiredBytes);
            }

            gl.UnmapBuffer(BufferTargetARB.PixelUnpackBuffer);
        }

        // PBO is still bound as GL_PIXEL_UNPACK_BUFFER.

        // ---- Step 2: Upload from this PBO to the texture (GPU side) ----
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        gl.BindTexture(TextureTarget.Texture2D, texId);
        unsafe
        {
            // With a PBO bound the last parameter is treated as a byte offset.
            gl.TexSubImage2D(
                TextureTarget.Texture2D,
                0,
                0, 0,
                w, h,
                GLEnum.Rgb,
                GLEnum.UnsignedByte,
                null);   // offset 0 into the bound PBO
        }

        gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
        gl.BindTexture(TextureTarget.Texture2D, 0);

        // ---- Step 3: Prevent engine from overwriting our upload ----
        // A Resize() on the XRTexture2D triggers Invalidate() on the
        // GLTexture2D wrapper.  On the next Bind() → VerifySettings(),
        // the engine would call PushData() which does glTexImage2D(null)
        // — wiping our data.  Clear both flags so that doesn't happen.
        glTex.ClearInvalidation();

        // Advance to the other PBO for next frame.
        _currentPboIndex = idx ^ 1;

        return true;
    }

    private void EnsurePbos(GL gl, uint requiredBytes)
    {
        if (_pboIds[0] != 0 && _pboSizeBytes == requiredBytes)
            return;

        // Delete old PBOs if they exist.
        for (int i = 0; i < PboCount; i++)
        {
            if (_pboIds[i] != 0)
            {
                gl.DeleteBuffer(_pboIds[i]);
                _pboIds[i] = 0;
            }
        }

        // Create fresh PBOs.
        for (int i = 0; i < PboCount; i++)
        {
            _pboIds[i] = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, _pboIds[i]);
            unsafe
            {
                gl.BufferData(BufferTargetARB.PixelUnpackBuffer, requiredBytes, null, BufferUsageARB.StreamDraw);
            }
            gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
        }

        _pboSizeBytes = requiredBytes;
        _currentPboIndex = 0;
    }

    public void Present(IMediaStreamSession session, uint framebufferId)
    {
        session.SetTargetFramebuffer(framebufferId);
        session.Present();
    }

    public void Dispose()
    {
        GL? gl = null;
        if (AbstractRenderer.Current is OpenGLRenderer renderer)
            gl = renderer.RawGL;

        for (int i = 0; i < PboCount; i++)
        {
            if (_pboIds[i] != 0)
            {
                try { gl?.DeleteBuffer(_pboIds[i]); } catch { }
                _pboIds[i] = 0;
            }
        }
        _pboSizeBytes = 0;
        _textureAllocated = false;
    }
}
