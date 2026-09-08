using Silk.NET.OpenGL;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering.OpenGL;

/// <summary>One fixed local SSBO binding image for a fenced Advanced frame.</summary>
internal sealed class OpenGLAdvancedVisibilitySlot
{
    internal OpenGLAdvancedNativeBufferStorage? NativeBuffers { get; set; }
    internal OpenGLAdvancedMonoTextureArrayAliasOwner? MonoTextureArrayAliases { get; set; }
    internal OpenGLAdvancedSceneTableUploader? SceneUploader { get; set; }
    private readonly uint[] _buffers = new uint[30];
    private readonly uint[] _uniformBuffers = new uint[3];
    private readonly uint[] _storageSizes = new uint[30];
    private nint _fence;
    private bool _initialized;
    private bool _quarantined;

    internal uint Buffer(uint localBinding) => _buffers[checked((int)localBinding - 48)];

    internal void Bind(OpenGLRenderer renderer)
    {
        for (uint index = 0; index < _buffers.Length; index++)
            renderer.RawGL.BindBufferBase(GLEnum.ShaderStorageBuffer, 48u + index, _buffers[index]);
    }

    internal unsafe void UploadUniform(OpenGLRenderer renderer, uint binding, ReadOnlySpan<uint> words)
    {
        if (binding >= _uniformBuffers.Length) throw new ArgumentOutOfRangeException(nameof(binding));
        ref uint buffer = ref _uniformBuffers[binding];
        if (buffer == 0) buffer = renderer.RawGL.CreateBuffer();
        fixed (uint* data = words)
            renderer.RawGL.NamedBufferData(buffer, checked((nuint)words.Length * sizeof(uint)), data, GLEnum.StreamDraw);
        renderer.RawGL.BindBufferBase(GLEnum.UniformBuffer, binding, buffer);
    }

    internal unsafe void EnsureStorage(OpenGLRenderer renderer, uint binding, uint bytes, bool clear)
    {
        int index = checked((int)binding - 48);
        bytes = Math.Max(4u, bytes);
        if (_storageSizes[index] < bytes)
        {
            renderer.RawGL.NamedBufferData(_buffers[index], bytes, null, GLEnum.DynamicDraw);
            _storageSizes[index] = bytes;
            clear = true;
        }
        if (!clear) return;
        uint zero = 0;
        renderer.RawGL.ClearNamedBufferData(_buffers[index], GLEnum.R32ui, GLEnum.RedInteger, GLEnum.UnsignedInt, &zero);
    }

    internal bool TryAcquire(OpenGLRenderer renderer)
    {
        if (!TryRetireCompletedPublication(renderer)) return false;
        if (!_initialized)
        {
            for (int index = 0; index < _buffers.Length; ++index)
            {
                _buffers[index] = renderer.RawGL.CreateBuffer();
                uint zero = 0u;
                unsafe { renderer.RawGL.NamedBufferData(_buffers[index], sizeof(uint), &zero, GLEnum.DynamicDraw); }
            }
            _initialized = true;
        }
        Bind(renderer);
        return true;
    }

    /// <summary>Releases completed scene and bindless leases without requiring
    /// another submission to reuse this slot. An unfinished family must exclude
    /// its retained slot from this poll.</summary>
    internal bool TryRetireCompletedPublication(OpenGLRenderer renderer)
    {
        if (!IsComplete(renderer)) return false;
        SceneUploader?.ReleaseCompletedSlotPublication();
        return true;
    }

    internal bool IsComplete(OpenGLRenderer renderer)
    {
        if (_quarantined) return false;
        if (_fence == 0) return true;
        GLEnum result = renderer.RawGL.ClientWaitSync(_fence, 0u, 0u);
        if (result is GLEnum.TimeoutExpired or GLEnum.WaitFailed) return false;
        renderer.RawGL.DeleteSync(_fence);
        _fence = 0;
        return true;
    }

    internal void MarkSubmitted(OpenGLRenderer renderer)
    {
        if (_fence != 0) throw new InvalidOperationException("OpenGL Advanced frame slot submitted twice without completion.");
        _fence = renderer.RawGL.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
        if (_fence != 0) return;
        _quarantined = true;
        throw new InvalidOperationException("OpenGL could not fence Advanced frame-slot submission; the slot is quarantined.");
    }

    internal unsafe void Upload<T>(OpenGLRenderer renderer, uint localBinding, ReadOnlySpan<T> values) where T : unmanaged
    {
        if (localBinding is < 48u or > 74u) throw new ArgumentOutOfRangeException(nameof(localBinding));
        uint buffer = _buffers[(int)(localBinding - 48u)];
        if (values.IsEmpty)
        {
            uint zero = 0u;
            renderer.RawGL.NamedBufferData(buffer, sizeof(uint), &zero, GLEnum.DynamicDraw);
            return;
        }
        fixed (T* value = values)
            renderer.RawGL.NamedBufferData(buffer, checked((nuint)(values.Length * Unsafe.SizeOf<T>())), value, GLEnum.DynamicDraw);
    }

    internal void Dispose(OpenGLRenderer renderer)
    {
        NativeBuffers?.Dispose();
        MonoTextureArrayAliases?.Dispose();
        SceneUploader?.Dispose();
        if (_fence != 0) renderer.RawGL.DeleteSync(_fence);
        _fence = 0;
        foreach (uint buffer in _buffers) if (buffer != 0u) renderer.RawGL.DeleteBuffer(buffer);
        foreach (uint buffer in _uniformBuffers) if (buffer != 0u) renderer.RawGL.DeleteBuffer(buffer);
    }
}
