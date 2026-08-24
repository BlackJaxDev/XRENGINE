using System.Collections.Concurrent;
using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

internal static partial class VulkanCpuSpanProfiler
{
    internal sealed class ThreadBuffer(int capacity, int threadId)
    {
        private readonly VulkanCpuSpanRecord[] _records = new VulkanCpuSpanRecord[capacity];
        private int _nextIndex;
        private int _count;
        public int ThreadId { get; } = threadId;
        public long NextSpanId;
        public long ActiveSpanId;

        public void Write(in VulkanCpuSpanRecord record)
        {
            int index = _nextIndex++ % _records.Length;
            _records[index] = record;
            if (_count < _records.Length)
                _count++;
        }

        public void CopyTo(List<VulkanCpuSpanRecord> destination)
        {
            int count = _count;
            int first = _nextIndex >= _records.Length 
                ? _nextIndex % _records.Length 
                : 0;
            
            for (int i = 0; i < count; i++)
                destination.Add(_records[(first + i) % _records.Length]);
        }
    }
}

