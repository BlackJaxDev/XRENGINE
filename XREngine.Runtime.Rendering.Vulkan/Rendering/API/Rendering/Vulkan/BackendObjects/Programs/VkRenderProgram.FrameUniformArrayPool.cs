using System.Numerics;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public partial class VkRenderProgram
    {
        private Dictionary<Type, object>? _frameUniformArrayPools;

        /// <summary>
        /// Copies a caller-owned array into storage that remains unique for the
        /// current render frame, while reusing that storage on later frames.
        /// </summary>
        private T[] CaptureUniformArray<T>(T[] values)
            => CaptureUniformArray((ReadOnlySpan<T>)values);

        private T[] CaptureUniformArray<T>(ReadOnlySpan<T> values)
        {
            lock (_bindingLock)
            {
                T[] snapshot = RentFrameUniformArray<T>(values.Length);
                values.CopyTo(snapshot);
                return snapshot;
            }
        }

        private Vector4[] CaptureQuaternionUniformArray(Quaternion[] values)
        {
            lock (_bindingLock)
            {
                Vector4[] snapshot = RentFrameUniformArray<Vector4>(values.Length);
                for (int index = 0; index < values.Length; index++)
                {
                    Quaternion value = values[index];
                    snapshot[index] = new Vector4(value.X, value.Y, value.Z, value.W);
                }

                return snapshot;
            }
        }

        private T[] RentFrameUniformArray<T>(int length)
        {
            if (RuntimeRenderingHostServices.FrameTiming.CurrentRenderPipelineContext is null)
                return new T[length];

            ulong frameId = RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId;
            if (frameId == 0)
                return new T[length];

            Dictionary<Type, object> pools = _frameUniformArrayPools ??= [];
            if (!pools.TryGetValue(typeof(T), out object? untypedPool))
            {
                untypedPool = new FrameUniformArrayPool<T>();
                pools.Add(typeof(T), untypedPool);
            }

            return ((FrameUniformArrayPool<T>)untypedPool).Rent(frameId, length);
        }

        private sealed class FrameUniformArrayPool<T>
        {
            private readonly List<T[]> _buffers = [];
            private ulong _frameId;
            private int _cursor;

            public T[] Rent(ulong frameId, int length)
            {
                if (_frameId != frameId)
                {
                    _frameId = frameId;
                    _cursor = 0;
                }

                int slot = _cursor++;
                if (slot == _buffers.Count)
                {
                    T[] created = new T[length];
                    _buffers.Add(created);
                    return created;
                }

                T[] buffer = _buffers[slot];
                if (buffer.Length == length)
                    return buffer;

                buffer = new T[length];
                _buffers[slot] = buffer;
                return buffer;
            }
        }
    }
}
