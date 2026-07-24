using Silk.NET.OpenGL;

namespace XREngine.Rendering.OpenGL;

public unsafe partial class OpenGLRenderer : IBufferDiagnosticReadbackBackendCapability
{
    bool IBufferDiagnosticReadbackBackendCapability.TryGetBufferByteSize(
        XRDataBuffer buffer,
        out ulong byteSize)
    {
        if (!TryResolveBuffer(buffer, out GLDataBuffer glBuffer, out uint binding))
        {
            byteSize = 0;
            return false;
        }

        byteSize = glBuffer.BackendAllocatedByteSize;
        if (byteSize != 0)
            return true;

        RawGL.BindBuffer(GLEnum.ShaderStorageBuffer, binding);
        RawGL.GetBufferParameter(GLEnum.ShaderStorageBuffer, GLEnum.BufferSize, out int allocatedBytes);
        RawGL.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
        byteSize = (ulong)Math.Max(allocatedBytes, 0);
        return allocatedBytes >= 0;
    }

    bool IBufferDiagnosticReadbackBackendCapability.TryReadBufferBytes(
        XRDataBuffer buffer,
        uint byteOffset,
        Span<byte> destination,
        out string route)
    {
        if (!TryResolveBuffer(buffer, out _, out uint binding))
        {
            route = "OpenGL buffer wrapper is not generated or has no binding.";
            return false;
        }

        RawGL.MemoryBarrier((uint)(GLEnum.BufferUpdateBarrierBit | GLEnum.ShaderStorageBarrierBit));
        RawGL.BindBuffer(GLEnum.ShaderStorageBuffer, binding);
        fixed (byte* destinationPointer = destination)
            RawGL.GetBufferSubData(
                GLEnum.ShaderStorageBuffer,
                (nint)byteOffset,
                (nuint)destination.Length,
                destinationPointer);
        RawGL.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
        route = "OpenGL GetBufferSubData";
        return true;
    }

    private bool TryResolveBuffer(
        XRDataBuffer buffer,
        out GLDataBuffer glBuffer,
        out uint binding)
    {
        GLDataBuffer? resolved = GetOrCreateAPIRenderObject(buffer, generateNow: true) as GLDataBuffer;
        if (resolved is null || !resolved.TryGetBindingId(out binding))
        {
            glBuffer = null!;
            binding = 0;
            return false;
        }

        glBuffer = resolved;
        return binding != 0 && binding != GLObjectBase.InvalidBindingId;
    }
}
