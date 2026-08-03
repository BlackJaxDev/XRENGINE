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
                public const int VulkanBindingFrequencyFrameIndex = 1;
                public const int VulkanBindingFrequencyViewIndex = 2;
                public const int VulkanBindingFrequencyPassIndex = 3;
                public const int VulkanBindingFrequencyMaterialIndex = 4;
                public const int VulkanBindingFrequencyObjectIndex = 5;
                public const int VulkanBindingFrequencyInstanceIndex = 6;
                public const int VulkanBindingFrequencyRuntimeCallbackIndex = 7;
                private const int VulkanBindingFrequencyCount = 8;
                private const int VulkanProgramBindingArtifactFallbackSampleCapacity = 32;
                private const int VulkanAutoUniformSchemaMismatchSampleCapacity = 16;
                private static long _vulkanMaterialPayloadCacheHits;
                private static long _vulkanMaterialPayloadCacheMisses;
                private static long _vulkanMaterialPayloadsPacked;
                private static long _vulkanMaterialUniformsPacked;
                private static long _vulkanMaterialParameterEmissions;
                private static long _vulkanMaterialDictionaryWrites;
                private static long _vulkanFrameMaterialSnapshotCacheHits;
                private static long _vulkanFrameMaterialSnapshotCacheMisses;
                private static long _vulkanProgramBindingArtifactBuilds;
                private static long _vulkanProgramBindingArtifactReuses;
                private static long _vulkanProgramBindingArtifactFallbacks;
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
                private static long _vulkanPreparedPrimaryFrameDataDrawsVisited;
                private static long _vulkanPreparedDynamicUiFrameDataDrawsVisited;
                private static long _vulkanDescriptorRecordsValidated;
                private static long _vulkanDescriptorRecordsWritten;
                private static long _vulkanDescriptorOwnerLookupMisses;
                private static long _vulkanDescriptorOwnerGenerationMisses;
                private static long _vulkanDescriptorFrameSourceGenerationMisses;
                private static long _vulkanBindingSchemasCompiled;
                private static long _vulkanBindingSchemaValueOperations;
                private static long _vulkanBindingSchemaDescriptorEntries;
                private static long _vulkanBindingSchemaFallbackOperations;
                private static long _vulkanAutoUniformTypedOperationsExecuted;
                private static long _vulkanAutoUniformReflectedNameLookups;
                private static long _vulkanAutoUniformGenericConversions;
                private static long _vulkanResetCommandBufferCalls;
                private static long _vulkanResetCommandPoolCalls;
                private static long _vulkanAllocateCommandBufferCalls;
                private static long _vulkanCommandBuffersAllocated;
                private static long _vulkanExecuteSecondaryCommandBufferCalls;
                private static long _vulkanSecondaryCommandBuffersInvoked;
                private static long _vulkanProcessResetCommandBufferCalls;
                private static long _vulkanProcessResetCommandPoolCalls;
                private static long _vulkanProcessAllocateCommandBufferCalls;
                private static long _vulkanProcessCommandBuffersAllocated;
                private static long _vulkanProcessExecuteSecondaryCommandBufferCalls;
                private static long _vulkanProcessSecondaryCommandBuffersInvoked;
                private static long _vulkanProcessWorkerSecondaryCommandBufferResetCalls;
                private static long _vulkanProcessWorkerSecondaryCommandBufferAllocations;
                private static long _vulkanProcessWorkerSecondaryReplacementAllocations;
                private static long _vulkanVisibleMeshDraws;
                private static long _vulkanUniqueVisibleMaterials;
                private static long _vulkanPreparedMeshDraws;
                private static long _vulkanRecordedCommandArtifactRetirements;
                private static readonly long[] _vulkanAutoUniformFallbackReasonCounts =
                    new long[(int)EVulkanAutoUniformFallbackReason.Count];
                private static readonly long[]
                    _vulkanProgramBindingAllocationBytes =
                        new long[(int)
                            EVulkanProgramBindingAllocationSegment.Count];
                private static readonly long[]
                    _vulkanAutoUniformSchemaMismatchSiteCounts =
                        new long[(int)
                            EVulkanAutoUniformSchemaMismatchSite.Count];
                private static readonly VulkanAutoUniformSchemaMismatchSample[]
                    _vulkanAutoUniformSchemaMismatchSamples =
                        new VulkanAutoUniformSchemaMismatchSample[
                            VulkanAutoUniformSchemaMismatchSampleCapacity];
                private static int _vulkanAutoUniformSchemaMismatchSampleCount;
                private static readonly long[] _vulkanAutoUniformFrequencyPublications =
                    new long[VulkanBindingFrequencyCount];
                private static readonly long[] _vulkanAutoUniformFrequencyReuses =
                    new long[VulkanBindingFrequencyCount];
                private static readonly long[] _vulkanAutoUniformFrequencyPublishedBytes =
                    new long[VulkanBindingFrequencyCount];
                private static readonly long[] _vulkanCommandChainWorkerEligibilityCounts =
                    new long[(int)EVulkanCommandChainWorkerEligibility.Count];
                private static readonly long[] _vulkanProgramBindingArtifactFallbackReasonCounts =
                    new long[(int)EVulkanProgramBindingArtifactFallbackReason.Count];
                private static readonly VulkanProgramBindingArtifactFallbackSample[]
                    _vulkanProgramBindingArtifactFallbackSamples =
                        new VulkanProgramBindingArtifactFallbackSample[
                            VulkanProgramBindingArtifactFallbackSampleCapacity];
                private static int _vulkanProgramBindingArtifactFallbackSampleCount;
                private static int _vulkanLastCommandChainWorkerEligibility;

                private static long _lastFrameVulkanMaterialPayloadCacheHits;
                private static long _lastFrameVulkanMaterialPayloadCacheMisses;
                private static long _lastFrameVulkanMaterialPayloadsPacked;
                private static long _lastFrameVulkanMaterialUniformsPacked;
                private static long _lastFrameVulkanMaterialParameterEmissions;
                private static long _lastFrameVulkanMaterialDictionaryWrites;
                private static long _lastFrameVulkanFrameMaterialSnapshotCacheHits;
                private static long _lastFrameVulkanFrameMaterialSnapshotCacheMisses;
                private static long _lastFrameVulkanProgramBindingArtifactBuilds;
                private static long _lastFrameVulkanProgramBindingArtifactReuses;
                private static long _lastFrameVulkanProgramBindingArtifactFallbacks;
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
                private static long _lastFrameVulkanPreparedPrimaryFrameDataDrawsVisited;
                private static long _lastFrameVulkanPreparedDynamicUiFrameDataDrawsVisited;
                private static long _lastFrameVulkanDescriptorRecordsValidated;
                private static long _lastFrameVulkanDescriptorRecordsWritten;
                private static long _lastFrameVulkanDescriptorOwnerLookupMisses;
                private static long _lastFrameVulkanDescriptorOwnerGenerationMisses;
                private static long _lastFrameVulkanDescriptorFrameSourceGenerationMisses;
                private static long _lastFrameVulkanBindingSchemasCompiled;
                private static long _lastFrameVulkanBindingSchemaValueOperations;
                private static long _lastFrameVulkanBindingSchemaDescriptorEntries;
                private static long _lastFrameVulkanBindingSchemaFallbackOperations;
                private static long _lastFrameVulkanAutoUniformTypedOperationsExecuted;
                private static long _lastFrameVulkanAutoUniformReflectedNameLookups;
                private static long _lastFrameVulkanAutoUniformGenericConversions;
                private static long _lastFrameVulkanResetCommandBufferCalls;
                private static long _lastFrameVulkanResetCommandPoolCalls;
                private static long _lastFrameVulkanAllocateCommandBufferCalls;
                private static long _lastFrameVulkanCommandBuffersAllocated;
                private static long _lastFrameVulkanExecuteSecondaryCommandBufferCalls;
                private static long _lastFrameVulkanSecondaryCommandBuffersInvoked;
                private static long _lastFrameVulkanVisibleMeshDraws;
                private static long _lastFrameVulkanUniqueVisibleMaterials;
                private static long _lastFrameVulkanPreparedMeshDraws;
                private static long _lastFrameVulkanRecordedCommandArtifactRetirements;
                private static readonly long[] _lastFrameVulkanAutoUniformFallbackReasonCounts =
                    new long[(int)EVulkanAutoUniformFallbackReason.Count];
                private static readonly long[]
                    _lastFrameVulkanProgramBindingAllocationBytes =
                        new long[(int)
                            EVulkanProgramBindingAllocationSegment.Count];
                private static readonly long[]
                    _lastFrameVulkanAutoUniformSchemaMismatchSiteCounts =
                        new long[(int)
                            EVulkanAutoUniformSchemaMismatchSite.Count];
                private static readonly VulkanAutoUniformSchemaMismatchSample[]
                    _lastFrameVulkanAutoUniformSchemaMismatchSamples =
                        new VulkanAutoUniformSchemaMismatchSample[
                            VulkanAutoUniformSchemaMismatchSampleCapacity];
                private static int
                    _lastFrameVulkanAutoUniformSchemaMismatchSampleCount;
                private static readonly long[] _lastFrameVulkanAutoUniformFrequencyPublications =
                    new long[VulkanBindingFrequencyCount];
                private static readonly long[] _lastFrameVulkanAutoUniformFrequencyReuses =
                    new long[VulkanBindingFrequencyCount];
                private static readonly long[] _lastFrameVulkanAutoUniformFrequencyPublishedBytes =
                    new long[VulkanBindingFrequencyCount];
                private static readonly long[] _lastFrameVulkanCommandChainWorkerEligibilityCounts =
                    new long[(int)EVulkanCommandChainWorkerEligibility.Count];
                private static readonly long[] _lastFrameVulkanProgramBindingArtifactFallbackReasonCounts =
                    new long[(int)EVulkanProgramBindingArtifactFallbackReason.Count];
                private static readonly VulkanProgramBindingArtifactFallbackSample[]
                    _lastFrameVulkanProgramBindingArtifactFallbackSamples =
                        new VulkanProgramBindingArtifactFallbackSample[
                            VulkanProgramBindingArtifactFallbackSampleCapacity];
                private static int _lastFrameVulkanProgramBindingArtifactFallbackSampleCount;
                private static int _lastFrameVulkanLastCommandChainWorkerEligibility;

                public static long VulkanMaterialPayloadCacheHits => _lastFrameVulkanMaterialPayloadCacheHits;
                public static long VulkanMaterialPayloadCacheMisses => _lastFrameVulkanMaterialPayloadCacheMisses;
                public static long VulkanMaterialPayloadsPacked => _lastFrameVulkanMaterialPayloadsPacked;
                public static long VulkanMaterialUniformsPacked => _lastFrameVulkanMaterialUniformsPacked;
                public static long VulkanMaterialParameterEmissions => _lastFrameVulkanMaterialParameterEmissions;
                public static long VulkanMaterialDictionaryWrites => _lastFrameVulkanMaterialDictionaryWrites;
                public static long VulkanFrameMaterialSnapshotCacheHits => _lastFrameVulkanFrameMaterialSnapshotCacheHits;
                public static long VulkanFrameMaterialSnapshotCacheMisses => _lastFrameVulkanFrameMaterialSnapshotCacheMisses;
                public static long VulkanProgramBindingArtifactBuilds => _lastFrameVulkanProgramBindingArtifactBuilds;
                public static long VulkanProgramBindingArtifactReuses => _lastFrameVulkanProgramBindingArtifactReuses;
                public static long VulkanProgramBindingArtifactFallbacks => _lastFrameVulkanProgramBindingArtifactFallbacks;
                public static long GetVulkanProgramBindingAllocationBytes(
                    EVulkanProgramBindingAllocationSegment segment)
                {
                    int segmentIndex = (int)segment;
                    return (uint)segmentIndex <
                        (uint)_lastFrameVulkanProgramBindingAllocationBytes.Length
                            ? Volatile.Read(
                                ref _lastFrameVulkanProgramBindingAllocationBytes[
                                    segmentIndex])
                            : 0;
                }
                public static long GetVulkanProgramBindingArtifactFallbackReasonCount(
                    EVulkanProgramBindingArtifactFallbackReason reason)
                    => (uint)reason <
                       (uint)EVulkanProgramBindingArtifactFallbackReason.Count
                        ? Volatile.Read(
                            ref _lastFrameVulkanProgramBindingArtifactFallbackReasonCounts[
                                (int)reason])
                        : 0;
                public static int VulkanProgramBindingArtifactFallbackSampleCount
                    => Volatile.Read(
                        ref _lastFrameVulkanProgramBindingArtifactFallbackSampleCount);
                public static VulkanProgramBindingArtifactFallbackSample
                    GetVulkanProgramBindingArtifactFallbackSample(int index)
                    => (uint)index <
                       (uint)VulkanProgramBindingArtifactFallbackSampleCount
                        ? _lastFrameVulkanProgramBindingArtifactFallbackSamples[index]
                        : default;
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
                public static long VulkanPreparedPrimaryFrameDataDrawsVisited =>
                    _lastFrameVulkanPreparedPrimaryFrameDataDrawsVisited;
                public static long VulkanPreparedDynamicUiFrameDataDrawsVisited =>
                    _lastFrameVulkanPreparedDynamicUiFrameDataDrawsVisited;
                public static long VulkanDescriptorRecordsValidated => _lastFrameVulkanDescriptorRecordsValidated;
                public static long VulkanDescriptorRecordsWritten => _lastFrameVulkanDescriptorRecordsWritten;
                public static long VulkanDescriptorOwnerLookupMisses => _lastFrameVulkanDescriptorOwnerLookupMisses;
                public static long VulkanDescriptorOwnerGenerationMisses => _lastFrameVulkanDescriptorOwnerGenerationMisses;
                public static long VulkanDescriptorFrameSourceGenerationMisses => _lastFrameVulkanDescriptorFrameSourceGenerationMisses;
                public static long VulkanBindingSchemasCompiled => _lastFrameVulkanBindingSchemasCompiled;
                public static long VulkanBindingSchemaValueOperations => _lastFrameVulkanBindingSchemaValueOperations;
                public static long VulkanBindingSchemaDescriptorEntries => _lastFrameVulkanBindingSchemaDescriptorEntries;
                public static long VulkanBindingSchemaFallbackOperations => _lastFrameVulkanBindingSchemaFallbackOperations;
                public static long VulkanAutoUniformTypedOperationsExecuted => _lastFrameVulkanAutoUniformTypedOperationsExecuted;
                public static long VulkanAutoUniformReflectedNameLookups => _lastFrameVulkanAutoUniformReflectedNameLookups;
                public static long VulkanAutoUniformGenericConversions => _lastFrameVulkanAutoUniformGenericConversions;
                public static long VulkanResetCommandBufferCalls => _lastFrameVulkanResetCommandBufferCalls;
                public static long VulkanResetCommandPoolCalls => _lastFrameVulkanResetCommandPoolCalls;
                public static long VulkanAllocateCommandBufferCalls => _lastFrameVulkanAllocateCommandBufferCalls;
                public static long VulkanCommandBuffersAllocated => _lastFrameVulkanCommandBuffersAllocated;
                public static long VulkanExecuteSecondaryCommandBufferCalls => _lastFrameVulkanExecuteSecondaryCommandBufferCalls;
                public static long VulkanSecondaryCommandBuffersInvoked => _lastFrameVulkanSecondaryCommandBuffersInvoked;
                public static long VulkanProcessResetCommandBufferCalls =>
                    Volatile.Read(ref _vulkanProcessResetCommandBufferCalls);
                public static long VulkanProcessResetCommandPoolCalls =>
                    Volatile.Read(ref _vulkanProcessResetCommandPoolCalls);
                public static long VulkanProcessAllocateCommandBufferCalls =>
                    Volatile.Read(ref _vulkanProcessAllocateCommandBufferCalls);
                public static long VulkanProcessCommandBuffersAllocated =>
                    Volatile.Read(ref _vulkanProcessCommandBuffersAllocated);
                public static long VulkanProcessExecuteSecondaryCommandBufferCalls =>
                    Volatile.Read(ref _vulkanProcessExecuteSecondaryCommandBufferCalls);
                public static long VulkanProcessSecondaryCommandBuffersInvoked =>
                    Volatile.Read(ref _vulkanProcessSecondaryCommandBuffersInvoked);
                public static long VulkanProcessWorkerSecondaryCommandBufferResetCalls =>
                    Volatile.Read(
                        ref _vulkanProcessWorkerSecondaryCommandBufferResetCalls);
                public static long VulkanProcessWorkerSecondaryCommandBufferAllocations =>
                    Volatile.Read(
                        ref _vulkanProcessWorkerSecondaryCommandBufferAllocations);
                public static long VulkanProcessWorkerSecondaryReplacementAllocations =>
                    Volatile.Read(
                        ref _vulkanProcessWorkerSecondaryReplacementAllocations);
                public static long VulkanVisibleMeshDraws => _lastFrameVulkanVisibleMeshDraws;
                public static long VulkanUniqueVisibleMaterials => _lastFrameVulkanUniqueVisibleMaterials;
                public static long VulkanPreparedMeshDraws => _lastFrameVulkanPreparedMeshDraws;
                public static long VulkanRecordedCommandArtifactRetirements => _lastFrameVulkanRecordedCommandArtifactRetirements;

                public static long GetVulkanAutoUniformFallbackReasonCount(
                    EVulkanAutoUniformFallbackReason reason)
                {
                    int reasonIndex = (int)reason;
                    return (uint)reasonIndex <
                        (uint)_lastFrameVulkanAutoUniformFallbackReasonCounts.Length
                            ? Volatile.Read(
                                ref _lastFrameVulkanAutoUniformFallbackReasonCounts[reasonIndex])
                            : 0;
                }

                public static long GetVulkanAutoUniformFrequencyPublicationCount(
                    int frequency)
                    => ReadFrequencyCounter(
                        _lastFrameVulkanAutoUniformFrequencyPublications,
                        frequency);

                public static long GetVulkanAutoUniformFrequencyReuseCount(
                    int frequency)
                    => ReadFrequencyCounter(
                        _lastFrameVulkanAutoUniformFrequencyReuses,
                        frequency);

                public static long GetVulkanAutoUniformFrequencyPublishedBytes(
                    int frequency)
                    => ReadFrequencyCounter(
                        _lastFrameVulkanAutoUniformFrequencyPublishedBytes,
                        frequency);

                public static EVulkanCommandChainWorkerEligibility VulkanLastCommandChainWorkerEligibility
                    => (EVulkanCommandChainWorkerEligibility)
                        Volatile.Read(ref _lastFrameVulkanLastCommandChainWorkerEligibility);

                public static long GetVulkanCommandChainWorkerEligibilityCount(
                    EVulkanCommandChainWorkerEligibility reason)
                {
                    int reasonIndex = (int)reason;
                    return (uint)reasonIndex <
                        (uint)_lastFrameVulkanCommandChainWorkerEligibilityCounts.Length
                            ? Volatile.Read(
                                ref _lastFrameVulkanCommandChainWorkerEligibilityCounts[reasonIndex])
                            : 0;
                }

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

                public static long GetVulkanAutoUniformSchemaMismatchSiteCount(
                    EVulkanAutoUniformSchemaMismatchSite site)
                {
                    int siteIndex = (int)site;
                    return (uint)siteIndex <
                        (uint)_lastFrameVulkanAutoUniformSchemaMismatchSiteCounts
                            .Length
                            ? Volatile.Read(
                                ref _lastFrameVulkanAutoUniformSchemaMismatchSiteCounts[
                                    siteIndex])
                            : 0;
                }

                public static int VulkanAutoUniformSchemaMismatchSampleCount
                    => Volatile.Read(
                        ref _lastFrameVulkanAutoUniformSchemaMismatchSampleCount);

                public static VulkanAutoUniformSchemaMismatchSample
                    GetVulkanAutoUniformSchemaMismatchSample(int index)
                    => (uint)index <
                       (uint)VulkanAutoUniformSchemaMismatchSampleCount
                        ? _lastFrameVulkanAutoUniformSchemaMismatchSamples[index]
                        : default;

                public static void RecordVulkanProgramBindingArtifactBuild()
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(ref _vulkanProgramBindingArtifactBuilds);
                }

                public static void RecordVulkanProgramBindingArtifactReuse()
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(ref _vulkanProgramBindingArtifactReuses);
                }

                public static void RecordVulkanProgramBindingAllocationBreakdown(
                    long setup,
                    long publisherScope,
                    long eligibilityGap,
                    long eligibilityScope,
                    long artifactKeyAndGeneration,
                    long lookupScope,
                    long reusePublication)
                {
                    if (!EnableTracking)
                        return;

                    AddProgramBindingAllocation(
                        EVulkanProgramBindingAllocationSegment.Setup,
                        setup);
                    AddProgramBindingAllocation(
                        EVulkanProgramBindingAllocationSegment.PublisherScope,
                        publisherScope);
                    AddProgramBindingAllocation(
                        EVulkanProgramBindingAllocationSegment.EligibilityGap,
                        eligibilityGap);
                    AddProgramBindingAllocation(
                        EVulkanProgramBindingAllocationSegment.EligibilityScope,
                        eligibilityScope);
                    AddProgramBindingAllocation(
                        EVulkanProgramBindingAllocationSegment
                            .ArtifactKeyAndGeneration,
                        artifactKeyAndGeneration);
                    AddProgramBindingAllocation(
                        EVulkanProgramBindingAllocationSegment.LookupScope,
                        lookupScope);
                    AddProgramBindingAllocation(
                        EVulkanProgramBindingAllocationSegment.ReusePublication,
                        reusePublication);
                }

                private static void AddProgramBindingAllocation(
                    EVulkanProgramBindingAllocationSegment segment,
                    long bytes)
                {
                    if (bytes <= 0)
                        return;

                    Interlocked.Add(
                        ref _vulkanProgramBindingAllocationBytes[(int)segment],
                        bytes);
                }

                public static void RecordVulkanProgramBindingArtifactFallback(
                    EVulkanProgramBindingArtifactFallbackReason reason,
                    string? meshName,
                    string? materialName,
                    string? programName,
                    string? detail = null)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(ref _vulkanProgramBindingArtifactFallbacks);
                    if (reason is >
                            EVulkanProgramBindingArtifactFallbackReason.None and <
                            EVulkanProgramBindingArtifactFallbackReason.Count)
                    {
                        Interlocked.Increment(
                            ref _vulkanProgramBindingArtifactFallbackReasonCounts[
                                (int)reason]);
                    }

                    int sampleIndex = Interlocked.Increment(
                        ref _vulkanProgramBindingArtifactFallbackSampleCount) - 1;
                    if ((uint)sampleIndex <
                        VulkanProgramBindingArtifactFallbackSampleCapacity)
                    {
                        _vulkanProgramBindingArtifactFallbackSamples[sampleIndex] =
                            new VulkanProgramBindingArtifactFallbackSample(
                                reason,
                                 meshName,
                                 materialName,
                                 programName,
                                 detail);
                    }
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

                public static void RecordVulkanPreparedFrameDataDrawVisited(
                    bool dynamicUi)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(
                        ref (dynamicUi
                            ? ref _vulkanPreparedDynamicUiFrameDataDrawsVisited
                            : ref _vulkanPreparedPrimaryFrameDataDrawsVisited));
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

                public static void RecordVulkanDescriptorOwnerGenerationMiss(
                    bool ownerLookupMiss,
                    bool ownerGenerationMiss,
                    bool frameSourceGenerationMiss)
                {
                    if (!EnableTracking)
                        return;

                    if (ownerLookupMiss)
                        Interlocked.Increment(ref _vulkanDescriptorOwnerLookupMisses);
                    if (ownerGenerationMiss)
                        Interlocked.Increment(ref _vulkanDescriptorOwnerGenerationMisses);
                    if (frameSourceGenerationMiss)
                    {
                        Interlocked.Increment(
                            ref _vulkanDescriptorFrameSourceGenerationMisses);
                    }
                }

                public static void RecordVulkanBindingSchemaCompiled(
                    int valueOperationCount,
                    int descriptorEntryCount,
                    int fallbackOperationCount)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(ref _vulkanBindingSchemasCompiled);
                    if (valueOperationCount > 0)
                        Interlocked.Add(ref _vulkanBindingSchemaValueOperations, valueOperationCount);
                    if (descriptorEntryCount > 0)
                        Interlocked.Add(ref _vulkanBindingSchemaDescriptorEntries, descriptorEntryCount);
                    if (fallbackOperationCount > 0)
                        Interlocked.Add(ref _vulkanBindingSchemaFallbackOperations, fallbackOperationCount);
                }

                public static void RecordVulkanAutoUniformTypedOperation()
                {
                    if (EnableTracking)
                        Interlocked.Increment(ref _vulkanAutoUniformTypedOperationsExecuted);
                }

                public static void RecordVulkanAutoUniformReflectedNameLookup()
                {
                    if (EnableTracking)
                        Interlocked.Increment(ref _vulkanAutoUniformReflectedNameLookups);
                }

                public static void RecordVulkanAutoUniformGenericConversion()
                {
                    if (EnableTracking)
                        Interlocked.Increment(ref _vulkanAutoUniformGenericConversions);
                }

                public static void RecordVulkanResetCommandBufferCall()
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(ref _vulkanResetCommandBufferCalls);
                    Interlocked.Increment(ref _vulkanProcessResetCommandBufferCalls);
                }

                public static void RecordVulkanResetCommandPoolCall()
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(ref _vulkanResetCommandPoolCalls);
                    Interlocked.Increment(ref _vulkanProcessResetCommandPoolCalls);
                }

                public static void RecordVulkanAllocateCommandBuffersCall(
                    uint requestedCount,
                    bool succeeded)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(ref _vulkanAllocateCommandBufferCalls);
                    Interlocked.Increment(ref _vulkanProcessAllocateCommandBufferCalls);
                    if (succeeded && requestedCount != 0)
                    {
                        Interlocked.Add(ref _vulkanCommandBuffersAllocated, requestedCount);
                        Interlocked.Add(
                            ref _vulkanProcessCommandBuffersAllocated,
                            requestedCount);
                    }
                }

                public static void RecordVulkanExecuteSecondaryCommandBuffers(uint count)
                {
                    if (!EnableTracking || count == 0)
                        return;

                    Interlocked.Increment(ref _vulkanExecuteSecondaryCommandBufferCalls);
                    Interlocked.Add(ref _vulkanSecondaryCommandBuffersInvoked, count);
                    Interlocked.Increment(
                        ref _vulkanProcessExecuteSecondaryCommandBufferCalls);
                    Interlocked.Add(
                        ref _vulkanProcessSecondaryCommandBuffersInvoked,
                        count);
                }

                public static void RecordVulkanWorkerSecondaryCommandBufferReset()
                {
                    if (EnableTracking)
                    {
                        Interlocked.Increment(
                            ref _vulkanProcessWorkerSecondaryCommandBufferResetCalls);
                    }
                }

                public static void RecordVulkanWorkerSecondaryCommandBufferAllocation(
                    bool replacement)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(
                        ref _vulkanProcessWorkerSecondaryCommandBufferAllocations);
                    if (replacement)
                    {
                        Interlocked.Increment(
                            ref _vulkanProcessWorkerSecondaryReplacementAllocations);
                    }
                }

                public static void RecordVulkanPreparedMeshDraws(int count)
                {
                    if (EnableTracking && count > 0)
                        Interlocked.Add(ref _vulkanPreparedMeshDraws, count);
                }

                public static void RecordVulkanVisibleMeshDrawCohort(
                    int visibleDrawCount,
                    int uniqueMaterialCount)
                {
                    if (!EnableTracking)
                        return;

                    if (visibleDrawCount > 0)
                        Interlocked.Add(
                            ref _vulkanVisibleMeshDraws,
                            visibleDrawCount);
                    if (uniqueMaterialCount > 0)
                        Interlocked.Add(
                            ref _vulkanUniqueVisibleMaterials,
                            uniqueMaterialCount);
                }

                public static void RecordVulkanRecordedCommandArtifactRetirement()
                {
                    if (EnableTracking)
                    {
                        Interlocked.Increment(
                            ref _vulkanRecordedCommandArtifactRetirements);
                    }
                }

                public static void RecordVulkanAutoUniformFallbackReason(
                    EVulkanAutoUniformFallbackReason reason)
                {
                    int reasonIndex = (int)reason;
                    if (!EnableTracking ||
                        reasonIndex <= (int)EVulkanAutoUniformFallbackReason.None ||
                        (uint)reasonIndex >=
                            (uint)_vulkanAutoUniformFallbackReasonCounts.Length)
                    {
                        return;
                    }

                    Interlocked.Increment(
                        ref _vulkanAutoUniformFallbackReasonCounts[reasonIndex]);
                }

                public static void RecordVulkanAutoUniformSchemaMismatch(
                    in VulkanAutoUniformSchemaMismatchSample sample)
                {
                    int siteIndex = (int)sample.Site;
                    if (!EnableTracking ||
                        siteIndex <=
                            (int)EVulkanAutoUniformSchemaMismatchSite.None ||
                        (uint)siteIndex >=
                            (uint)_vulkanAutoUniformSchemaMismatchSiteCounts
                                .Length)
                    {
                        return;
                    }

                    Interlocked.Increment(
                        ref _vulkanAutoUniformSchemaMismatchSiteCounts[
                            siteIndex]);
                    int sampleIndex = Interlocked.Increment(
                        ref _vulkanAutoUniformSchemaMismatchSampleCount) - 1;
                    if ((uint)sampleIndex <
                        VulkanAutoUniformSchemaMismatchSampleCapacity)
                    {
                        _vulkanAutoUniformSchemaMismatchSamples[sampleIndex] =
                            sample;
                    }
                }

                public static void RecordVulkanAutoUniformFrequencyPublication(
                    int frequency,
                    bool published,
                    int publishedBytes)
                {
                    int frequencyIndex = frequency;
                    if (!EnableTracking ||
                        frequencyIndex <= 0 ||
                        (uint)frequencyIndex >=
                            (uint)_vulkanAutoUniformFrequencyPublications.Length)
                    {
                        return;
                    }

                    if (published)
                    {
                        Interlocked.Increment(
                            ref _vulkanAutoUniformFrequencyPublications[frequencyIndex]);
                        if (publishedBytes > 0)
                        {
                            Interlocked.Add(
                                ref _vulkanAutoUniformFrequencyPublishedBytes[frequencyIndex],
                                publishedBytes);
                        }
                    }
                    else
                    {
                        Interlocked.Increment(
                            ref _vulkanAutoUniformFrequencyReuses[frequencyIndex]);
                    }
                }

                public static void RecordVulkanCommandChainWorkerEligibility(
                    EVulkanCommandChainWorkerEligibility reason)
                {
                    int reasonIndex = (int)reason;
                    if (!EnableTracking ||
                        reasonIndex <= (int)EVulkanCommandChainWorkerEligibility.NotEvaluated ||
                        (uint)reasonIndex >=
                            (uint)_vulkanCommandChainWorkerEligibilityCounts.Length)
                    {
                        return;
                    }

                    Interlocked.Increment(
                        ref _vulkanCommandChainWorkerEligibilityCounts[reasonIndex]);
                    Volatile.Write(
                        ref _vulkanLastCommandChainWorkerEligibility,
                        (int)reason);
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
                    _lastFrameVulkanProgramBindingArtifactBuilds = Interlocked.Exchange(ref _vulkanProgramBindingArtifactBuilds, 0);
                    _lastFrameVulkanProgramBindingArtifactReuses = Interlocked.Exchange(ref _vulkanProgramBindingArtifactReuses, 0);
                    _lastFrameVulkanProgramBindingArtifactFallbacks = Interlocked.Exchange(ref _vulkanProgramBindingArtifactFallbacks, 0);
                    for (int segmentIndex = 0;
                         segmentIndex <
                            _vulkanProgramBindingAllocationBytes.Length;
                         segmentIndex++)
                    {
                        _lastFrameVulkanProgramBindingAllocationBytes[
                            segmentIndex] = Interlocked.Exchange(
                                ref _vulkanProgramBindingAllocationBytes[
                                    segmentIndex],
                                0);
                    }
                    for (int reasonIndex = 0;
                         reasonIndex <
                         _vulkanProgramBindingArtifactFallbackReasonCounts.Length;
                         reasonIndex++)
                    {
                        _lastFrameVulkanProgramBindingArtifactFallbackReasonCounts[
                            reasonIndex] = Interlocked.Exchange(
                                ref _vulkanProgramBindingArtifactFallbackReasonCounts[
                                    reasonIndex],
                                0);
                    }
                    int fallbackSampleCount = Math.Min(
                        Interlocked.Exchange(
                            ref _vulkanProgramBindingArtifactFallbackSampleCount,
                            0),
                        VulkanProgramBindingArtifactFallbackSampleCapacity);
                    for (int sampleIndex = 0;
                         sampleIndex < fallbackSampleCount;
                         sampleIndex++)
                    {
                        _lastFrameVulkanProgramBindingArtifactFallbackSamples[
                            sampleIndex] =
                                _vulkanProgramBindingArtifactFallbackSamples[
                                    sampleIndex];
                        _vulkanProgramBindingArtifactFallbackSamples[
                            sampleIndex] = default;
                    }
                    Volatile.Write(
                        ref _lastFrameVulkanProgramBindingArtifactFallbackSampleCount,
                        fallbackSampleCount);
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
                    _lastFrameVulkanPreparedPrimaryFrameDataDrawsVisited =
                        Interlocked.Exchange(
                            ref _vulkanPreparedPrimaryFrameDataDrawsVisited,
                            0);
                    _lastFrameVulkanPreparedDynamicUiFrameDataDrawsVisited =
                        Interlocked.Exchange(
                            ref _vulkanPreparedDynamicUiFrameDataDrawsVisited,
                            0);
                    _lastFrameVulkanDescriptorRecordsValidated = Interlocked.Exchange(ref _vulkanDescriptorRecordsValidated, 0);
                    _lastFrameVulkanDescriptorRecordsWritten = Interlocked.Exchange(ref _vulkanDescriptorRecordsWritten, 0);
                    _lastFrameVulkanDescriptorOwnerLookupMisses = Interlocked.Exchange(ref _vulkanDescriptorOwnerLookupMisses, 0);
                    _lastFrameVulkanDescriptorOwnerGenerationMisses = Interlocked.Exchange(ref _vulkanDescriptorOwnerGenerationMisses, 0);
                    _lastFrameVulkanDescriptorFrameSourceGenerationMisses = Interlocked.Exchange(ref _vulkanDescriptorFrameSourceGenerationMisses, 0);
                    _lastFrameVulkanBindingSchemasCompiled = Interlocked.Exchange(ref _vulkanBindingSchemasCompiled, 0);
                    _lastFrameVulkanBindingSchemaValueOperations = Interlocked.Exchange(ref _vulkanBindingSchemaValueOperations, 0);
                    _lastFrameVulkanBindingSchemaDescriptorEntries = Interlocked.Exchange(ref _vulkanBindingSchemaDescriptorEntries, 0);
                    _lastFrameVulkanBindingSchemaFallbackOperations = Interlocked.Exchange(ref _vulkanBindingSchemaFallbackOperations, 0);
                    _lastFrameVulkanAutoUniformTypedOperationsExecuted = Interlocked.Exchange(ref _vulkanAutoUniformTypedOperationsExecuted, 0);
                    _lastFrameVulkanAutoUniformReflectedNameLookups = Interlocked.Exchange(ref _vulkanAutoUniformReflectedNameLookups, 0);
                    _lastFrameVulkanAutoUniformGenericConversions = Interlocked.Exchange(ref _vulkanAutoUniformGenericConversions, 0);
                    _lastFrameVulkanResetCommandBufferCalls = Interlocked.Exchange(ref _vulkanResetCommandBufferCalls, 0);
                    _lastFrameVulkanResetCommandPoolCalls = Interlocked.Exchange(ref _vulkanResetCommandPoolCalls, 0);
                    _lastFrameVulkanAllocateCommandBufferCalls = Interlocked.Exchange(ref _vulkanAllocateCommandBufferCalls, 0);
                    _lastFrameVulkanCommandBuffersAllocated = Interlocked.Exchange(ref _vulkanCommandBuffersAllocated, 0);
                    _lastFrameVulkanExecuteSecondaryCommandBufferCalls = Interlocked.Exchange(ref _vulkanExecuteSecondaryCommandBufferCalls, 0);
                    _lastFrameVulkanSecondaryCommandBuffersInvoked = Interlocked.Exchange(ref _vulkanSecondaryCommandBuffersInvoked, 0);
                    _lastFrameVulkanVisibleMeshDraws = Interlocked.Exchange(ref _vulkanVisibleMeshDraws, 0);
                    _lastFrameVulkanUniqueVisibleMaterials = Interlocked.Exchange(ref _vulkanUniqueVisibleMaterials, 0);
                    _lastFrameVulkanPreparedMeshDraws = Interlocked.Exchange(ref _vulkanPreparedMeshDraws, 0);
                    _lastFrameVulkanRecordedCommandArtifactRetirements = Interlocked.Exchange(ref _vulkanRecordedCommandArtifactRetirements, 0);
                    for (int reasonIndex = 0;
                         reasonIndex < _vulkanAutoUniformFallbackReasonCounts.Length;
                         reasonIndex++)
                    {
                        _lastFrameVulkanAutoUniformFallbackReasonCounts[reasonIndex] =
                            Interlocked.Exchange(
                                ref _vulkanAutoUniformFallbackReasonCounts[reasonIndex],
                                0);
                    }
                    for (int siteIndex = 0;
                         siteIndex <
                            _vulkanAutoUniformSchemaMismatchSiteCounts.Length;
                         siteIndex++)
                    {
                        _lastFrameVulkanAutoUniformSchemaMismatchSiteCounts[
                            siteIndex] = Interlocked.Exchange(
                                ref _vulkanAutoUniformSchemaMismatchSiteCounts[
                                    siteIndex],
                                0);
                    }
                    int schemaMismatchSampleCount = Math.Min(
                        Interlocked.Exchange(
                            ref _vulkanAutoUniformSchemaMismatchSampleCount,
                            0),
                        VulkanAutoUniformSchemaMismatchSampleCapacity);
                    for (int sampleIndex = 0;
                         sampleIndex < schemaMismatchSampleCount;
                         sampleIndex++)
                    {
                        _lastFrameVulkanAutoUniformSchemaMismatchSamples[
                            sampleIndex] =
                                _vulkanAutoUniformSchemaMismatchSamples[
                                    sampleIndex];
                        _vulkanAutoUniformSchemaMismatchSamples[sampleIndex] =
                            default;
                    }
                    Volatile.Write(
                        ref _lastFrameVulkanAutoUniformSchemaMismatchSampleCount,
                        schemaMismatchSampleCount);
                    for (int frequencyIndex = 0;
                         frequencyIndex <
                            _vulkanAutoUniformFrequencyPublications.Length;
                         frequencyIndex++)
                    {
                        _lastFrameVulkanAutoUniformFrequencyPublications[frequencyIndex] =
                            Interlocked.Exchange(
                                ref _vulkanAutoUniformFrequencyPublications[frequencyIndex],
                                0);
                        _lastFrameVulkanAutoUniformFrequencyReuses[frequencyIndex] =
                            Interlocked.Exchange(
                                ref _vulkanAutoUniformFrequencyReuses[frequencyIndex],
                                0);
                        _lastFrameVulkanAutoUniformFrequencyPublishedBytes[frequencyIndex] =
                            Interlocked.Exchange(
                                ref _vulkanAutoUniformFrequencyPublishedBytes[frequencyIndex],
                                0);
                    }
                    for (int reasonIndex = 0;
                         reasonIndex < _vulkanCommandChainWorkerEligibilityCounts.Length;
                         reasonIndex++)
                    {
                        _lastFrameVulkanCommandChainWorkerEligibilityCounts[reasonIndex] =
                            Interlocked.Exchange(
                                ref _vulkanCommandChainWorkerEligibilityCounts[reasonIndex],
                                0);
                    }
                    _lastFrameVulkanLastCommandChainWorkerEligibility =
                        Interlocked.Exchange(
                            ref _vulkanLastCommandChainWorkerEligibility,
                            (int)EVulkanCommandChainWorkerEligibility.NotEvaluated);
                }

                private static long ReadFrequencyCounter(
                    long[] counters,
                    int frequency)
                {
                    int frequencyIndex = frequency;
                    return frequencyIndex > 0 &&
                        (uint)frequencyIndex < (uint)counters.Length
                            ? Volatile.Read(ref counters[frequencyIndex])
                            : 0;
                }
            }
        }
    }
}
