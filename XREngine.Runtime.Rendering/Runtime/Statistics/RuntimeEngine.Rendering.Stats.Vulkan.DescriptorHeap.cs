using System.Threading;

namespace XREngine;

public static partial class RuntimeEngine
{
    public static partial class Rendering
    {
        public static partial class Stats
        {
            public static partial class Vulkan
            {
                private static long _descriptorHeapSamplerBinds;
                private static long _descriptorHeapResourceBinds;
                private static long _descriptorHeapPushes;
                private static long _descriptorHeapSamplerWrites;
                private static long _descriptorHeapResourceWrites;
                private static long _descriptorHeapPayloadAllocations;

                /// <summary>Process-wide native sampler-heap bind calls, including discarded recordings.</summary>
                public static long DescriptorHeapSamplerBinds => Interlocked.Read(ref _descriptorHeapSamplerBinds);
                /// <summary>Process-wide native resource-heap bind calls, including discarded recordings.</summary>
                public static long DescriptorHeapResourceBinds => Interlocked.Read(ref _descriptorHeapResourceBinds);
                /// <summary>Process-wide native push-data calls, not GPU execution receipts.</summary>
                public static long DescriptorHeapPushes => Interlocked.Read(ref _descriptorHeapPushes);
                /// <summary>Number of sampler descriptors successfully written through the native heap API.</summary>
                public static long DescriptorHeapSamplerWrites => Interlocked.Read(ref _descriptorHeapSamplerWrites);
                /// <summary>Number of resource descriptors successfully written through the native heap API.</summary>
                public static long DescriptorHeapResourceWrites => Interlocked.Read(ref _descriptorHeapResourceWrites);
                /// <summary>Number of heap payload objects created; compare deltas after warmup.</summary>
                public static long DescriptorHeapPayloadAllocations => Interlocked.Read(ref _descriptorHeapPayloadAllocations);

                public static void RecordDescriptorHeapSamplerBind() => Interlocked.Increment(ref _descriptorHeapSamplerBinds);
                public static void RecordDescriptorHeapResourceBind() => Interlocked.Increment(ref _descriptorHeapResourceBinds);
                public static void RecordDescriptorHeapPush() => Interlocked.Increment(ref _descriptorHeapPushes);
                public static void RecordDescriptorHeapSamplerWrites(uint count) => Interlocked.Add(ref _descriptorHeapSamplerWrites, count);
                public static void RecordDescriptorHeapResourceWrites(uint count) => Interlocked.Add(ref _descriptorHeapResourceWrites, count);
                public static void RecordDescriptorHeapPayloadAllocation() => Interlocked.Increment(ref _descriptorHeapPayloadAllocations);
            }
        }
    }
}
