using System.Text;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        internal sealed partial class CommandBufferRecordingScratch
        {
            public VulkanRenderScopeController RenderScope { get; } = new();
            public Dictionary<int, VulkanSecondaryRecordingBucket> SecondaryBucketByStart { get; } = new();
            public List<VulkanSecondaryRecordingBucket> SecondaryRecordingBuckets { get; } = new(32);
            public Dictionary<int, int> SwapchainWritesByPipeline { get; } = new();
            public Dictionary<int, string> SwapchainWriterLabelByPipeline { get; } = new();
            public Dictionary<int, string> SwapchainWriterDetailByPipeline { get; } = new();
            public Dictionary<int, FrameOp> SwapchainWriterOpByPipeline { get; } = new();
            public Dictionary<int, int> SwapchainWriterDynamicUiDrawCountByPipeline { get; } = new();
            public HashSet<nint> ExecutedCommandChainSecondaryHandles { get; } = new();
            public VulkanPrimarySecondaryArtifactSequence ExecutedCommandChainSecondaryArtifactSequence { get; } = new();
            public HashSet<FrameOp> PipelineDeferredOps { get; } =
                new(ReferenceEqualityComparer.Instance);
            public HashSet<int> PipelineDeferredRequirementIndices { get; } = [];
            public ulong PipelineDeferredManifestIdentity { get; set; }
            public ulong PipelineDeferredActivityGeneration { get; set; }
            public ulong PipelineDeferredSharedPipelineGeneration { get; set; }
            public HashSet<VkRenderQuery> PreparedInlineQueries { get; } = new(ReferenceEqualityComparer.Instance);
            public HashSet<VkRenderQuery> BegunInlineQueries { get; } = new(ReferenceEqualityComparer.Instance);
            public HashSet<object> VisitedResourceRegistries { get; } = new(ReferenceEqualityComparer.Instance);
            public HashSet<object> VisibleMaterialIdentities { get; } =
                new(ReferenceEqualityComparer.Instance);
            public Dictionary<int, int> SwapchainWriterPassByPipeline { get; } = new();
            public Dictionary<int, int> SwapchainWriterOpIndexByPipeline { get; } = new();
            public Dictionary<int, string> PipelineNameByIdentity { get; } = new();
            public Dictionary<VkMeshRenderer, int> MeshDrawSlotsByRenderer { get; } = new(ReferenceEqualityComparer.Instance);
            public Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> MeshDrawSlotsByRendererFamily { get; } =
                new(VulkanMeshFrameDataRendererFamilyKeyComparer.Instance);
            public Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> PrimaryMeshDrawSlotsByRendererFamily { get; } =
                new(VulkanMeshFrameDataRendererFamilyKeyComparer.Instance);
            public Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> OpenXrMeshDrawSlotsByRendererFamily { get; } =
                new(VulkanMeshFrameDataRendererFamilyKeyComparer.Instance);
            public int OpenXrMeshDrawSlotCapacityHint { get; set; } = 1;
            public Dictionary<VulkanMeshFrameDataFamilyKey, int> MeshFrameDataFamilyStrides { get; } = [];
            public Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> ReusableMeshFrameDataFamilyBases { get; } =
                new(VulkanMeshFrameDataRendererFamilyKeyComparer.Instance);
            // Reuse refresh may run concurrently for separate outputs. Keep its ordinal
            // allocation state on the recorder thread so one output cannot redirect
            // another output's dynamic UBO slot assignments.
            public Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> ReusableMeshDrawSlotsByRendererFamily { get; } =
                new(VulkanMeshFrameDataRendererFamilyKeyComparer.Instance);
            public int ReusableMeshDrawSlotCapacityHint { get; set; } = 1;
            public Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> PrimaryMeshFrameDataFamilyBases { get; } =
                new(VulkanMeshFrameDataRendererFamilyKeyComparer.Instance);
            public Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> DynamicUiMeshFrameDataFamilyBases { get; } =
                new(VulkanMeshFrameDataRendererFamilyKeyComparer.Instance);
            public Dictionary<VkMeshRenderer, int> DynamicUiMeshDrawSlotsByRenderer { get; } =
                new(ReferenceEqualityComparer.Instance);
            public Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> DynamicUiMeshDrawSlotsByRendererFamily { get; } =
                new(VulkanMeshFrameDataRendererFamilyKeyComparer.Instance);
            public int DynamicUiMeshDrawSlotCapacityHint { get; set; } = 1;
            public Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> OpenXrMeshFrameDataFamilyBases { get; } =
                new(VulkanMeshFrameDataRendererFamilyKeyComparer.Instance);
            public VulkanMeshFrameDataReservationManifest MeshFrameDataManifest { get; } = new();
            public Dictionary<XRFrameBuffer, ImageLayout[]> FboLayoutTracking { get; } = new(ReferenceEqualityComparer.Instance);
            public Dictionary<XRFrameBuffer, FboAttachmentLayoutScratch> FboAttachmentLayouts { get; } =
                new(ReferenceEqualityComparer.Instance);
            public CommandChainKey[] ScheduledCommandChainKeysByOpIndex { get; set; } = [];
            public List<KeyValuePair<int, int>> SwapchainWriterCountSort { get; } = new();
            public Dictionary<VulkanTrackedImageSubresource, VulkanImageAccessState> SecondaryDescriptorImageRequirementMap { get; } =
                new(64);
            public StringBuilder SwapchainWriterSummaryBuilder { get; } = new(256);
            public int SecondaryBucketByStartCapacityHint { get; set; } = 1;
            public int RecordSwapchainWriterCapacityHint { get; set; } = 1;
            public int RecordPipelineNameCapacityHint { get; set; } = 1;
            public int RecordMeshDrawSlotCapacityHint { get; set; } = 1;
            public int RecordFboLayoutCapacityHint { get; set; } = 1;
            public VulkanPreparedComputePayload? PreparedComputePayload { get; set; }
            private int[] _primaryMeshDrawUniformSlotsByOpIndex = [];
            private bool[]
                _primaryScheduledCommandChainFrameDataRefreshedByOpIndex = [];
            private VulkanReusableFrameDataRefreshRequest[]
                _primaryReusableFrameDataRefreshRequests = [];
            private VulkanReusableFrameDataRefreshRequest[]
                _dynamicUiReusableFrameDataRefreshRequests = [];
            private VulkanReusableFrameDataRefreshRequest[]
                _primaryReusableFrameDataOwnerWorkRequests = [];
            private VulkanReusableFrameDataRefreshRequest[]
                _dynamicUiReusableFrameDataOwnerWorkRequests = [];
            private VulkanReusableFrameDataRefreshRequest[]
                _scheduledCommandChainFrameDataRefreshRequests = [];
            private VulkanReusableFrameDataRefreshRequest[]
                _scheduledCommandChainFrameDataOwnerWorkRequests = [];
            private int _primaryReusableFrameDataRefreshRequestCount;
            private int _dynamicUiReusableFrameDataRefreshRequestCount;
            private int _primaryReusableFrameDataOwnerWorkRequestCount;
            private int _dynamicUiReusableFrameDataOwnerWorkRequestCount;
            private int _scheduledCommandChainFrameDataRefreshRequestCount;
            private int _scheduledCommandChainFrameDataOwnerWorkRequestCount;
            private readonly HashSet<VulkanReusableFrameOwnerKey>
                _primaryReusableFrameOwners = [];
            private readonly HashSet<VulkanReusableFrameOwnerKey>
                _dynamicUiReusableFrameOwners = [];
            private readonly HashSet<VulkanReusableFrameOwnerKey>
                _scheduledCommandChainFrameDataOwners = [];

            public VulkanReusableFrameDataRefreshBatchInfo
                PrimaryReusableFrameDataRefreshBatchInfo { get; private set; }

            public VulkanReusableFrameDataRefreshBatchInfo
                DynamicUiReusableFrameDataRefreshBatchInfo { get; private set; }

            public VulkanReusableFrameDataRefreshBatchInfo
                ScheduledCommandChainFrameDataRefreshBatchInfo
                { get; private set; }

            public VulkanReusableFrameDataRefreshState
                ScheduledCommandChainFrameDataRefreshState { get; } = new();

            public ReadOnlySpan<VulkanReusableFrameDataRefreshRequest>
                PrimaryReusableFrameDataRefreshRequests
                => _primaryReusableFrameDataRefreshRequests.AsSpan(
                    0,
                    _primaryReusableFrameDataRefreshRequestCount);

            public ReadOnlySpan<VulkanReusableFrameDataRefreshRequest>
                DynamicUiReusableFrameDataRefreshRequests
                => _dynamicUiReusableFrameDataRefreshRequests.AsSpan(
                    0,
                    _dynamicUiReusableFrameDataRefreshRequestCount);

            public ReadOnlySpan<VulkanReusableFrameDataRefreshRequest>
                PrimaryReusableFrameDataOwnerWorkRequests
                => _primaryReusableFrameDataOwnerWorkRequests.AsSpan(
                    0,
                    _primaryReusableFrameDataOwnerWorkRequestCount);

            public ReadOnlySpan<VulkanReusableFrameDataRefreshRequest>
                DynamicUiReusableFrameDataOwnerWorkRequests
                => _dynamicUiReusableFrameDataOwnerWorkRequests.AsSpan(
                    0,
                    _dynamicUiReusableFrameDataOwnerWorkRequestCount);

            public ReadOnlySpan<VulkanReusableFrameDataRefreshRequest>
                ScheduledCommandChainFrameDataRefreshRequests
                => _scheduledCommandChainFrameDataRefreshRequests.AsSpan(
                    0,
                    _scheduledCommandChainFrameDataRefreshRequestCount);

            public ReadOnlySpan<VulkanReusableFrameDataRefreshRequest>
                ScheduledCommandChainFrameDataOwnerWorkRequests
                => _scheduledCommandChainFrameDataOwnerWorkRequests.AsSpan(
                    0,
                    _scheduledCommandChainFrameDataOwnerWorkRequestCount);

            public void BeginReusableFrameDataRefreshRequests()
            {
                Array.Clear(
                    _primaryReusableFrameDataRefreshRequests,
                    0,
                    _primaryReusableFrameDataRefreshRequestCount);
                Array.Clear(
                    _dynamicUiReusableFrameDataRefreshRequests,
                    0,
                    _dynamicUiReusableFrameDataRefreshRequestCount);
                Array.Clear(
                    _primaryReusableFrameDataOwnerWorkRequests,
                    0,
                    _primaryReusableFrameDataOwnerWorkRequestCount);
                Array.Clear(
                    _dynamicUiReusableFrameDataOwnerWorkRequests,
                    0,
                    _dynamicUiReusableFrameDataOwnerWorkRequestCount);
                _primaryReusableFrameDataRefreshRequestCount = 0;
                _dynamicUiReusableFrameDataRefreshRequestCount = 0;
                _primaryReusableFrameDataOwnerWorkRequestCount = 0;
                _dynamicUiReusableFrameDataOwnerWorkRequestCount = 0;
                _primaryReusableFrameOwners.Clear();
                _dynamicUiReusableFrameOwners.Clear();
                PrimaryReusableFrameDataRefreshBatchInfo = default;
                DynamicUiReusableFrameDataRefreshBatchInfo = default;
            }

            public void BeginScheduledCommandChainFrameDataRefreshRequests()
            {
                Array.Clear(
                    _scheduledCommandChainFrameDataRefreshRequests,
                    0,
                    _scheduledCommandChainFrameDataRefreshRequestCount);
                Array.Clear(
                    _scheduledCommandChainFrameDataOwnerWorkRequests,
                    0,
                    _scheduledCommandChainFrameDataOwnerWorkRequestCount);
                _scheduledCommandChainFrameDataRefreshRequestCount = 0;
                _scheduledCommandChainFrameDataOwnerWorkRequestCount = 0;
                _scheduledCommandChainFrameDataOwners.Clear();
                ScheduledCommandChainFrameDataRefreshBatchInfo = default;
            }

            public void AddReusableFrameDataRefreshRequest(
                bool dynamicUi,
                in VulkanReusableFrameDataRefreshRequest request)
            {
                if (dynamicUi)
                {
                    EnsureReusableFrameDataRefreshRequestCapacity(
                        ref _dynamicUiReusableFrameDataRefreshRequests,
                        _dynamicUiReusableFrameDataRefreshRequestCount + 1);
                    _dynamicUiReusableFrameDataRefreshRequests[
                        _dynamicUiReusableFrameDataRefreshRequestCount++] =
                        request;
                    return;
                }

                EnsureReusableFrameDataRefreshRequestCapacity(
                    ref _primaryReusableFrameDataRefreshRequests,
                    _primaryReusableFrameDataRefreshRequestCount + 1);
                _primaryReusableFrameDataRefreshRequests[
                    _primaryReusableFrameDataRefreshRequestCount++] = request;
            }

            public bool TryAddReusableFrameDataOwnerWorkRequest(
                bool dynamicUi,
                in VulkanReusableFrameOwnerKey ownerKey,
                in VulkanReusableFrameDataRefreshRequest request)
            {
                HashSet<VulkanReusableFrameOwnerKey> owners =
                    dynamicUi
                        ? _dynamicUiReusableFrameOwners
                        : _primaryReusableFrameOwners;
                if (!owners.Add(ownerKey))
                    return false;

                AddReusableFrameDataOwnerWorkRequest(dynamicUi, request);
                return true;
            }

            public void AddReusableFrameDataOwnerWorkRequest(
                bool dynamicUi,
                in VulkanReusableFrameDataRefreshRequest request)
            {
                if (dynamicUi)
                {
                    EnsureReusableFrameDataRefreshRequestCapacity(
                        ref _dynamicUiReusableFrameDataOwnerWorkRequests,
                        _dynamicUiReusableFrameDataOwnerWorkRequestCount + 1);
                    _dynamicUiReusableFrameDataOwnerWorkRequests[
                        _dynamicUiReusableFrameDataOwnerWorkRequestCount++] =
                        request;
                    return;
                }

                EnsureReusableFrameDataRefreshRequestCapacity(
                    ref _primaryReusableFrameDataOwnerWorkRequests,
                    _primaryReusableFrameDataOwnerWorkRequestCount + 1);
                _primaryReusableFrameDataOwnerWorkRequests[
                    _primaryReusableFrameDataOwnerWorkRequestCount++] = request;
            }

            public void SetReusableFrameDataRefreshBatchInfo(
                bool dynamicUi,
                in VulkanReusableFrameDataRefreshBatchInfo batchInfo)
            {
                if (dynamicUi)
                    DynamicUiReusableFrameDataRefreshBatchInfo = batchInfo;
                else
                    PrimaryReusableFrameDataRefreshBatchInfo = batchInfo;
            }

            public void AddScheduledCommandChainFrameDataRefreshRequest(
                in VulkanReusableFrameDataRefreshRequest request)
            {
                EnsureReusableFrameDataRefreshRequestCapacity(
                    ref _scheduledCommandChainFrameDataRefreshRequests,
                    _scheduledCommandChainFrameDataRefreshRequestCount + 1);
                _scheduledCommandChainFrameDataRefreshRequests[
                    _scheduledCommandChainFrameDataRefreshRequestCount++] =
                    request;
            }

            public bool TryAddScheduledCommandChainFrameDataOwnerWorkRequest(
                in VulkanReusableFrameOwnerKey ownerKey,
                in VulkanReusableFrameDataRefreshRequest request)
            {
                if (!_scheduledCommandChainFrameDataOwners.Add(ownerKey))
                    return false;

                EnsureReusableFrameDataRefreshRequestCapacity(
                    ref _scheduledCommandChainFrameDataOwnerWorkRequests,
                    _scheduledCommandChainFrameDataOwnerWorkRequestCount + 1);
                _scheduledCommandChainFrameDataOwnerWorkRequests[
                    _scheduledCommandChainFrameDataOwnerWorkRequestCount++] =
                    request;
                return true;
            }

            public void SetScheduledCommandChainFrameDataRefreshBatchInfo(
                in VulkanReusableFrameDataRefreshBatchInfo batchInfo)
                => ScheduledCommandChainFrameDataRefreshBatchInfo = batchInfo;

            public int[] PreparePrimaryMeshDrawUniformSlots(int opCount)
            {
                if (_primaryMeshDrawUniformSlotsByOpIndex.Length < opCount)
                {
                    int capacity = Math.Max(
                        opCount,
                        Math.Max(4, _primaryMeshDrawUniformSlotsByOpIndex.Length * 2));
                    Array.Resize(ref _primaryMeshDrawUniformSlotsByOpIndex, capacity);
                }

                Array.Fill(_primaryMeshDrawUniformSlotsByOpIndex, -1, 0, opCount);
                return _primaryMeshDrawUniformSlotsByOpIndex;
            }

            public bool[]
                PreparePrimaryScheduledCommandChainFrameDataRefreshFlags(
                    int opCount)
            {
                if (_primaryScheduledCommandChainFrameDataRefreshedByOpIndex
                        .Length < opCount)
                {
                    int capacity = Math.Max(
                        opCount,
                        Math.Max(
                            4,
                            _primaryScheduledCommandChainFrameDataRefreshedByOpIndex
                                .Length * 2));
                    Array.Resize(
                        ref _primaryScheduledCommandChainFrameDataRefreshedByOpIndex,
                        capacity);
                }

                Array.Fill(
                    _primaryScheduledCommandChainFrameDataRefreshedByOpIndex,
                    false,
                    0,
                    opCount);
                return _primaryScheduledCommandChainFrameDataRefreshedByOpIndex;
            }

            private static void EnsureReusableFrameDataRefreshRequestCapacity(
                ref VulkanReusableFrameDataRefreshRequest[] requests,
                int required)
            {
                if (requests.Length >= required)
                    return;

                int capacity = Math.Max(
                    required,
                    Math.Max(16, requests.Length * 2));
                Array.Resize(ref requests, capacity);
            }

        }

    }
}
