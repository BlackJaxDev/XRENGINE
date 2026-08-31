using Silk.NET.OpenGL;

namespace XREngine.Rendering.OpenGL;

/// <summary>
/// Context-local persistent SSBO storage for immutable CPU publications. A range is valid only
/// within its unsealed GPU-use epoch; pages are reused exclusively after a nonblocking signaled fence.
/// </summary>
public unsafe partial class OpenGLRenderer
{
    private const int ReadOnlyStoragePageCount = 3;
    private const int ReadOnlyStoragePageBytes = 4 * 1024 * 1024;
    private const int MaximumReadOnlyStoragePublicationsPerEpoch = 256;
    private const int GlMaxShaderStorageBufferBindings = 0x90DD;
    private const int GlMaxShaderStorageBlockSize = 0x90DE;
    private const int GlShaderStorageBufferOffsetAlignment = 0x90DF;
    private const uint GlMapWriteBit = 0x0002;
    private const uint GlMapPersistentBit = 0x0040;
    private const uint GlMapCoherentBit = 0x0080;

    private readonly ReadOnlyStoragePage[] _readOnlyStoragePages = new ReadOnlyStoragePage[ReadOnlyStoragePageCount];
    private readonly Dictionary<ReadOnlyStoragePublicationKey, ReadOnlyStorageRange> _readOnlyStorageEpochRanges = new(MaximumReadOnlyStoragePublicationsPerEpoch);
    private bool _readOnlyStorageArenaReady;
    private int _readOnlyStorageAlignmentBytes = 16;
    private int _readOnlyStorageMaximumRangeBytes;
    private int _readOnlyStorageBindingLimit;

    private void InitializeReadOnlyStorageArena(GL api)
    {
        ReadOnlyStoragePublication.Prewarm();
        ReadOnlyStorageBindingSet.Prewarm();
        DisposeReadOnlyStorageArena(api);

        try
        {
            // Api is lazy and invokes InitGL. Use the already-current API passed
            // by InitGL here, otherwise bootstrap recursively creates contexts.
            _readOnlyStorageAlignmentBytes = Math.Max(1, api.GetInteger((GLEnum)GlShaderStorageBufferOffsetAlignment));
            _readOnlyStorageMaximumRangeBytes = Math.Max(1, api.GetInteger((GLEnum)GlMaxShaderStorageBlockSize));
            _readOnlyStorageBindingLimit = Math.Max(0, api.GetInteger((GLEnum)GlMaxShaderStorageBufferBindings));
            int pageBytes = Math.Min(ReadOnlyStoragePageBytes, _readOnlyStorageMaximumRangeBytes);
            if (pageBytes <= 0 || _readOnlyStorageBindingLimit <= 0)
                throw new InvalidOperationException("The OpenGL context does not expose usable shader-storage buffer limits.");

            for (int index = 0; index < _readOnlyStoragePages.Length; ++index)
            {
                api.CreateBuffers(1, out uint buffer);
                if (buffer == 0u)
                    throw new InvalidOperationException("glCreateBuffers returned zero for the immutable storage arena.");

                api.NamedBufferStorage(buffer, (nuint)pageBytes, null, GlMapWriteBit | GlMapPersistentBit | GlMapCoherentBit);
                byte* mapped = (byte*)api.MapNamedBufferRange(
                    buffer,
                    IntPtr.Zero,
                    (nuint)pageBytes,
                    GlMapWriteBit | GlMapPersistentBit | GlMapCoherentBit);
                if (mapped is null)
                {
                    api.DeleteBuffer(buffer);
                    throw new InvalidOperationException("glMapNamedBufferRange returned null for the immutable storage arena.");
                }

                _readOnlyStoragePages[index] = new ReadOnlyStoragePage(buffer, mapped, pageBytes);
            }

            _readOnlyStorageArenaReady = true;
        }
        catch (Exception exception)
        {
            DisposeReadOnlyStorageArena(api);
            Debug.OpenGLWarning($"[ReadOnlyStorage] Disabled OpenGL immutable storage arena: {exception.Message}");
        }
    }

    internal void BindReadOnlyStorage(in ReadOnlyStorageBinding binding)
    {
        if (!_readOnlyStorageArenaReady || !binding.IsValid || binding.Length == 0 || binding.Binding >= _readOnlyStorageBindingLimit)
        {
            UnbindReadOnlyStorage(binding.Binding, "invalid binding or unavailable context arena");
            return;
        }
        if (binding.Length > _readOnlyStorageMaximumRangeBytes)
        {
            UnbindReadOnlyStorage(binding.Binding, "publication range exceeds GL_MAX_SHADER_STORAGE_BLOCK_SIZE");
            return;
        }

        ReadOnlyStoragePublicationKey key = new(
            binding.Publication.TokenId,
            binding.Publication.AbiSignature,
            binding.Offset,
            binding.Length);
        if (!_readOnlyStorageEpochRanges.TryGetValue(key, out ReadOnlyStorageRange range))
        {
            if (_readOnlyStorageEpochRanges.Count >= MaximumReadOnlyStoragePublicationsPerEpoch)
            {
                UnbindReadOnlyStorage(binding.Binding, "the bounded epoch publication table is full");
                return;
            }
            if (!TryMaterializeReadOnlyStorage(binding, out range))
            {
                UnbindReadOnlyStorage(binding.Binding, "all persistent arena pages are busy or exhausted");
                return;
            }

            _readOnlyStorageEpochRanges.Add(key, range);
        }

        // This handler is invoked by XRRenderProgram at command execution; do not cache a native
        // range in the logical program because its epoch fence is deliberately transient.
        Api.BindBufferRange(
            GLEnum.ShaderStorageBuffer,
            binding.Binding,
            range.Buffer,
            (nint)range.Offset,
            (nuint)range.Length);
    }

    private bool TryMaterializeReadOnlyStorage(in ReadOnlyStorageBinding binding, out ReadOnlyStorageRange range)
    {
        for (int index = 0; index < _readOnlyStoragePages.Length; ++index)
        {
            ref ReadOnlyStoragePage page = ref _readOnlyStoragePages[index];
            if (!TryPrepareReadOnlyStoragePage(ref page))
                continue;

            int offset = AlignReadOnlyStorage(page.Cursor, _readOnlyStorageAlignmentBytes);
            if (offset > page.CapacityBytes || binding.Length > page.CapacityBytes - offset)
                continue;

            binding.Publication.CopyRangeTo(
                binding.Offset,
                new Span<byte>(page.Mapped + offset, binding.Length));
            page.Cursor = offset + binding.Length;
            page.Touched = true;
            range = new ReadOnlyStorageRange(page.Buffer, offset, binding.Length);
            return true;
        }

        range = default;
        return false;
    }

    private bool TryPrepareReadOnlyStoragePage(ref ReadOnlyStoragePage page)
    {
        if (page.Poisoned || page.Buffer == 0u || page.Mapped is null)
            return false;
        if (page.Fence == IntPtr.Zero)
            return true;

        GLEnum status = Api.ClientWaitSync(page.Fence, 0u, 0u);
        if (status is GLEnum.AlreadySignaled or GLEnum.ConditionSatisfied)
        {
            Api.DeleteSync(page.Fence);
            page.Fence = IntPtr.Zero;
            page.Cursor = 0;
            page.Touched = false;
            return true;
        }
        if (status == GLEnum.WaitFailed)
        {
            page.Poisoned = true;
            Debug.OpenGLError("[ReadOnlyStorage] glClientWaitSync failed; the persistent storage page is permanently retired for this context.");
        }

        return false;
    }

    private void SealReadOnlyStorageEpoch()
    {
        if (!_readOnlyStorageArenaReady)
            return;

        for (int index = 0; index < _readOnlyStoragePages.Length; ++index)
        {
            ref ReadOnlyStoragePage page = ref _readOnlyStoragePages[index];
            if (!page.Touched || page.Fence != IntPtr.Zero || page.Poisoned)
                continue;

            page.Fence = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
            if (page.Fence == IntPtr.Zero)
            {
                page.Poisoned = true;
                Debug.OpenGLError("[ReadOnlyStorage] glFenceSync returned zero; the persistent storage page is permanently retired for this context.");
            }
        }

        _readOnlyStorageEpochRanges.Clear();
    }

    private void UnbindReadOnlyStorage(uint binding, string reason)
    {
        if (_readOnlyStorageArenaReady && binding < _readOnlyStorageBindingLimit)
            Api.BindBufferBase(GLEnum.ShaderStorageBuffer, binding, 0u);
        Debug.OpenGLWarning($"[ReadOnlyStorage] Binding {binding} was not published: {reason}.");
    }

    private void DisposeReadOnlyStorageArena(GL? currentApi = null)
    {
        _readOnlyStorageEpochRanges.Clear();
        _readOnlyStorageArenaReady = false;
        for (int index = 0; index < _readOnlyStoragePages.Length; ++index)
        {
            ref ReadOnlyStoragePage page = ref _readOnlyStoragePages[index];
            if (page.Fence != IntPtr.Zero && !ShouldOrphanGLHandlesForShutdown)
                (currentApi ?? Api).DeleteSync(page.Fence);
            if (page.Buffer != 0u && !ShouldOrphanGLHandlesForShutdown)
                (currentApi ?? Api).DeleteBuffer(page.Buffer);
            page = default;
        }
    }

    private static int AlignReadOnlyStorage(int value, int alignment)
        => checked((value + alignment - 1) / alignment * alignment);

    private readonly record struct ReadOnlyStoragePublicationKey(
        ulong PublicationTokenId,
        ulong AbiSignature,
        int Offset,
        int Length);

    private readonly record struct ReadOnlyStorageRange(uint Buffer, int Offset, int Length);

    private struct ReadOnlyStoragePage(uint buffer, byte* mapped, int capacityBytes)
    {
        public uint Buffer = buffer;
        public byte* Mapped = mapped;
        public int CapacityBytes = capacityBytes;
        public int Cursor;
        public IntPtr Fence;
        public bool Touched;
        public bool Poisoned;
    }
}
