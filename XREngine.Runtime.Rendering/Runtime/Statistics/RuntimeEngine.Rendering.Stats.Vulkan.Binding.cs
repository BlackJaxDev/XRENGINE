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
                private static long _vulkanMaterialPayloadCacheHits;
                private static long _vulkanMaterialPayloadCacheMisses;
                private static long _vulkanMaterialPayloadsPacked;
                private static long _vulkanMaterialUniformsPacked;
                private static long _vulkanMaterialParameterEmissions;
                private static long _vulkanMaterialDictionaryWrites;
                private static long _vulkanFrameMaterialSnapshotCacheHits;
                private static long _vulkanFrameMaterialSnapshotCacheMisses;
                private static long _vulkanBindingSnapshotsCaptured;
                private static long _vulkanBindingSnapshotEntries;
                private static long _vulkanFastPathBindingSnapshots;
                private static long _vulkanLegacyBindingSnapshots;
                private static long _vulkanAutoUniformPlanCacheHits;
                private static long _vulkanAutoUniformPlanCacheMisses;
                private static long _vulkanAutoUniformStaticBytesCopied;
                private static long _vulkanAutoUniformDynamicBytesCleared;
                private static long _vulkanAutoUniformDynamicMembersPatched;
                private static long _vulkanAutoUniformReflectedMembersScanned;
                private static long _vulkanAutoUniformLegacyFullBlockBytes;
                private static long _vulkanAutoUniformFastPathDraws;
                private static long _vulkanAutoUniformLegacyFallbackDraws;
                private static long _vulkanFrameDataDrawsVisited;
                private static long _vulkanDescriptorRecordsValidated;
                private static long _vulkanDescriptorRecordsWritten;

                private static long _lastFrameVulkanMaterialPayloadCacheHits;
                private static long _lastFrameVulkanMaterialPayloadCacheMisses;
                private static long _lastFrameVulkanMaterialPayloadsPacked;
                private static long _lastFrameVulkanMaterialUniformsPacked;
                private static long _lastFrameVulkanMaterialParameterEmissions;
                private static long _lastFrameVulkanMaterialDictionaryWrites;
                private static long _lastFrameVulkanFrameMaterialSnapshotCacheHits;
                private static long _lastFrameVulkanFrameMaterialSnapshotCacheMisses;
                private static long _lastFrameVulkanBindingSnapshotsCaptured;
                private static long _lastFrameVulkanBindingSnapshotEntries;
                private static long _lastFrameVulkanFastPathBindingSnapshots;
                private static long _lastFrameVulkanLegacyBindingSnapshots;
                private static long _lastFrameVulkanAutoUniformPlanCacheHits;
                private static long _lastFrameVulkanAutoUniformPlanCacheMisses;
                private static long _lastFrameVulkanAutoUniformStaticBytesCopied;
                private static long _lastFrameVulkanAutoUniformDynamicBytesCleared;
                private static long _lastFrameVulkanAutoUniformDynamicMembersPatched;
                private static long _lastFrameVulkanAutoUniformReflectedMembersScanned;
                private static long _lastFrameVulkanAutoUniformLegacyFullBlockBytes;
                private static long _lastFrameVulkanAutoUniformFastPathDraws;
                private static long _lastFrameVulkanAutoUniformLegacyFallbackDraws;
                private static long _lastFrameVulkanFrameDataDrawsVisited;
                private static long _lastFrameVulkanDescriptorRecordsValidated;
                private static long _lastFrameVulkanDescriptorRecordsWritten;

                public static long VulkanMaterialPayloadCacheHits => _lastFrameVulkanMaterialPayloadCacheHits;
                public static long VulkanMaterialPayloadCacheMisses => _lastFrameVulkanMaterialPayloadCacheMisses;
                public static long VulkanMaterialPayloadsPacked => _lastFrameVulkanMaterialPayloadsPacked;
                public static long VulkanMaterialUniformsPacked => _lastFrameVulkanMaterialUniformsPacked;
                public static long VulkanMaterialParameterEmissions => _lastFrameVulkanMaterialParameterEmissions;
                public static long VulkanMaterialDictionaryWrites => _lastFrameVulkanMaterialDictionaryWrites;
                public static long VulkanFrameMaterialSnapshotCacheHits => _lastFrameVulkanFrameMaterialSnapshotCacheHits;
                public static long VulkanFrameMaterialSnapshotCacheMisses => _lastFrameVulkanFrameMaterialSnapshotCacheMisses;
                public static long VulkanBindingSnapshotsCaptured => _lastFrameVulkanBindingSnapshotsCaptured;
                public static long VulkanBindingSnapshotEntries => _lastFrameVulkanBindingSnapshotEntries;
                public static long VulkanFastPathBindingSnapshots => _lastFrameVulkanFastPathBindingSnapshots;
                public static long VulkanLegacyBindingSnapshots => _lastFrameVulkanLegacyBindingSnapshots;
                public static long VulkanAutoUniformPlanCacheHits => _lastFrameVulkanAutoUniformPlanCacheHits;
                public static long VulkanAutoUniformPlanCacheMisses => _lastFrameVulkanAutoUniformPlanCacheMisses;
                public static long VulkanAutoUniformStaticBytesCopied => _lastFrameVulkanAutoUniformStaticBytesCopied;
                public static long VulkanAutoUniformDynamicBytesCleared => _lastFrameVulkanAutoUniformDynamicBytesCleared;
                public static long VulkanAutoUniformDynamicMembersPatched => _lastFrameVulkanAutoUniformDynamicMembersPatched;
                public static long VulkanAutoUniformReflectedMembersScanned => _lastFrameVulkanAutoUniformReflectedMembersScanned;
                public static long VulkanAutoUniformLegacyFullBlockBytes => _lastFrameVulkanAutoUniformLegacyFullBlockBytes;
                public static long VulkanAutoUniformFastPathDraws => _lastFrameVulkanAutoUniformFastPathDraws;
                public static long VulkanAutoUniformLegacyFallbackDraws => _lastFrameVulkanAutoUniformLegacyFallbackDraws;
                public static long VulkanFrameDataDrawsVisited => _lastFrameVulkanFrameDataDrawsVisited;
                public static long VulkanDescriptorRecordsValidated => _lastFrameVulkanDescriptorRecordsValidated;
                public static long VulkanDescriptorRecordsWritten => _lastFrameVulkanDescriptorRecordsWritten;

                public static void RecordVulkanMaterialPayloadCacheLookup(bool hit)
                {
                    if (!EnableTracking)
                        return;

                    if (hit)
                        Interlocked.Increment(ref _vulkanMaterialPayloadCacheHits);
                    else
                        Interlocked.Increment(ref _vulkanMaterialPayloadCacheMisses);
                }

                public static void RecordVulkanMaterialPayloadPacked(int uniformCount)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(ref _vulkanMaterialPayloadsPacked);
                    if (uniformCount > 0)
                        Interlocked.Add(ref _vulkanMaterialUniformsPacked, uniformCount);
                }

                public static void RecordVulkanMaterialParameterEmission(int parameterCount)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(ref _vulkanMaterialParameterEmissions);
                    if (parameterCount > 0)
                        Interlocked.Add(ref _vulkanMaterialDictionaryWrites, parameterCount);
                }

                public static void RecordVulkanFrameMaterialSnapshotCacheLookup(bool hit)
                {
                    if (!EnableTracking)
                        return;

                    if (hit)
                        Interlocked.Increment(ref _vulkanFrameMaterialSnapshotCacheHits);
                    else
                        Interlocked.Increment(ref _vulkanFrameMaterialSnapshotCacheMisses);
                }

                public static void RecordVulkanBindingSnapshotCaptured(int entryCount, bool fastPath)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(ref _vulkanBindingSnapshotsCaptured);
                    if (fastPath)
                        Interlocked.Increment(ref _vulkanFastPathBindingSnapshots);
                    else
                        Interlocked.Increment(ref _vulkanLegacyBindingSnapshots);
                    if (entryCount > 0)
                        Interlocked.Add(ref _vulkanBindingSnapshotEntries, entryCount);
                }

                public static void RecordVulkanAutoUniformPlanLookup(bool hit)
                {
                    if (!EnableTracking)
                        return;

                    if (hit)
                        Interlocked.Increment(ref _vulkanAutoUniformPlanCacheHits);
                    else
                        Interlocked.Increment(ref _vulkanAutoUniformPlanCacheMisses);
                }

                public static void RecordVulkanAutoUniformFastWrite(
                    int staticBytesCopied,
                    int dynamicBytesCleared,
                    int dynamicMembersPatched)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(ref _vulkanAutoUniformFastPathDraws);
                    if (staticBytesCopied > 0)
                        Interlocked.Add(ref _vulkanAutoUniformStaticBytesCopied, staticBytesCopied);
                    if (dynamicBytesCleared > 0)
                        Interlocked.Add(ref _vulkanAutoUniformDynamicBytesCleared, dynamicBytesCleared);
                    if (dynamicMembersPatched > 0)
                        Interlocked.Add(ref _vulkanAutoUniformDynamicMembersPatched, dynamicMembersPatched);
                }

                public static void RecordVulkanAutoUniformLegacyWrite(
                    int fullBlockBytes,
                    int reflectedMembersScanned)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(ref _vulkanAutoUniformLegacyFallbackDraws);
                    if (fullBlockBytes > 0)
                        Interlocked.Add(ref _vulkanAutoUniformLegacyFullBlockBytes, fullBlockBytes);
                    if (reflectedMembersScanned > 0)
                        Interlocked.Add(ref _vulkanAutoUniformReflectedMembersScanned, reflectedMembersScanned);
                }

                public static void RecordVulkanFrameDataDrawVisited()
                {
                    if (EnableTracking)
                        Interlocked.Increment(ref _vulkanFrameDataDrawsVisited);
                }

                public static void RecordVulkanDescriptorRecordsValidated(int count)
                {
                    if (EnableTracking && count > 0)
                        Interlocked.Add(ref _vulkanDescriptorRecordsValidated, count);
                }

                public static void RecordVulkanDescriptorRecordsWritten(int count)
                {
                    if (EnableTracking && count > 0)
                        Interlocked.Add(ref _vulkanDescriptorRecordsWritten, count);
                }

                private static void SnapshotAndResetBindingTelemetry()
                {
                    _lastFrameVulkanMaterialPayloadCacheHits = Interlocked.Exchange(ref _vulkanMaterialPayloadCacheHits, 0);
                    _lastFrameVulkanMaterialPayloadCacheMisses = Interlocked.Exchange(ref _vulkanMaterialPayloadCacheMisses, 0);
                    _lastFrameVulkanMaterialPayloadsPacked = Interlocked.Exchange(ref _vulkanMaterialPayloadsPacked, 0);
                    _lastFrameVulkanMaterialUniformsPacked = Interlocked.Exchange(ref _vulkanMaterialUniformsPacked, 0);
                    _lastFrameVulkanMaterialParameterEmissions = Interlocked.Exchange(ref _vulkanMaterialParameterEmissions, 0);
                    _lastFrameVulkanMaterialDictionaryWrites = Interlocked.Exchange(ref _vulkanMaterialDictionaryWrites, 0);
                    _lastFrameVulkanFrameMaterialSnapshotCacheHits = Interlocked.Exchange(ref _vulkanFrameMaterialSnapshotCacheHits, 0);
                    _lastFrameVulkanFrameMaterialSnapshotCacheMisses = Interlocked.Exchange(ref _vulkanFrameMaterialSnapshotCacheMisses, 0);
                    _lastFrameVulkanBindingSnapshotsCaptured = Interlocked.Exchange(ref _vulkanBindingSnapshotsCaptured, 0);
                    _lastFrameVulkanBindingSnapshotEntries = Interlocked.Exchange(ref _vulkanBindingSnapshotEntries, 0);
                    _lastFrameVulkanFastPathBindingSnapshots = Interlocked.Exchange(ref _vulkanFastPathBindingSnapshots, 0);
                    _lastFrameVulkanLegacyBindingSnapshots = Interlocked.Exchange(ref _vulkanLegacyBindingSnapshots, 0);
                    _lastFrameVulkanAutoUniformPlanCacheHits = Interlocked.Exchange(ref _vulkanAutoUniformPlanCacheHits, 0);
                    _lastFrameVulkanAutoUniformPlanCacheMisses = Interlocked.Exchange(ref _vulkanAutoUniformPlanCacheMisses, 0);
                    _lastFrameVulkanAutoUniformStaticBytesCopied = Interlocked.Exchange(ref _vulkanAutoUniformStaticBytesCopied, 0);
                    _lastFrameVulkanAutoUniformDynamicBytesCleared = Interlocked.Exchange(ref _vulkanAutoUniformDynamicBytesCleared, 0);
                    _lastFrameVulkanAutoUniformDynamicMembersPatched = Interlocked.Exchange(ref _vulkanAutoUniformDynamicMembersPatched, 0);
                    _lastFrameVulkanAutoUniformReflectedMembersScanned = Interlocked.Exchange(ref _vulkanAutoUniformReflectedMembersScanned, 0);
                    _lastFrameVulkanAutoUniformLegacyFullBlockBytes = Interlocked.Exchange(ref _vulkanAutoUniformLegacyFullBlockBytes, 0);
                    _lastFrameVulkanAutoUniformFastPathDraws = Interlocked.Exchange(ref _vulkanAutoUniformFastPathDraws, 0);
                    _lastFrameVulkanAutoUniformLegacyFallbackDraws = Interlocked.Exchange(ref _vulkanAutoUniformLegacyFallbackDraws, 0);
                    _lastFrameVulkanFrameDataDrawsVisited = Interlocked.Exchange(ref _vulkanFrameDataDrawsVisited, 0);
                    _lastFrameVulkanDescriptorRecordsValidated = Interlocked.Exchange(ref _vulkanDescriptorRecordsValidated, 0);
                    _lastFrameVulkanDescriptorRecordsWritten = Interlocked.Exchange(ref _vulkanDescriptorRecordsWritten, 0);
                }
            }
        }
    }
}
