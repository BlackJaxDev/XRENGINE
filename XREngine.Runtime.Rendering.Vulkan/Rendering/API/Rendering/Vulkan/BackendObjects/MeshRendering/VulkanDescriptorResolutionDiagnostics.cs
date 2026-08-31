using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Captures the first missing directional-shadow storage descriptor with the
/// reflected program identity needed to distinguish descriptor warmup from a
/// queued draw whose immutable binding snapshot was not published.
/// </summary>
internal static class VulkanDescriptorResolutionDiagnostics
{
    private const string DirectionalShadowRecordsBuffer =
        "DirectionalShadowRecordsBuffer";

    private static int s_capturedDirectionalShadowRecordsFailure;
    private static string? s_firstDirectionalShadowRecordsFailure;
    private static XRRenderProgram? s_firstFailedDirectionalShadowProgram;
    private static int s_capturedUnpublishedDirectionalShadowRecords;
    private static string? s_firstUnpublishedDirectionalShadowRecords;
    // Producer capture is intentionally overwritten without formatting so the
    // hot indirect path can leave exact identity evidence for a later failure.
    private static ComputeDispatchSnapshot? s_lastIndirectSnapshot;
    private static XRRenderProgram? s_lastIndirectPendingProgram;
    private static VkRenderProgram? s_lastIndirectPreparedProgram;
    private static string? s_lastIndirectContext;
    private static XRFrameBuffer? s_lastIndirectTarget;
    private static int s_lastIndirectPassIndex;
    private static int s_lastIndirectSnapshotHadStorage;
    private static int s_capturedDirectionalShadowPublication;
    private static string? s_firstDirectionalShadowPublication;
    private static int s_capturedDroppedDirectionalShadowPublication;
    private static string? s_firstDroppedDirectionalShadowPublication;

    /// <summary>
    /// Gets the first directional-shadow descriptor-resolution failure captured
    /// by this process. Kept as a static reflection getter so Release builds
    /// retain the evidence even when diagnostic logging is compiled out.
    /// </summary>
    public static string? GetFirstFailure()
        => Volatile.Read(ref s_firstDirectionalShadowRecordsFailure);

    /// <summary>
    /// Gets the first indirect snapshot that declared the directional-shadow
    /// descriptor but carried no immutable storage publication.
    /// </summary>
    public static string? GetFirstUnpublishedIndirectSnapshot()
        => Volatile.Read(ref s_firstUnpublishedDirectionalShadowRecords);

    /// <summary>
    /// Gets the first binding-39 publication attempt for a program whose
    /// reflected descriptor is the directional-shadow record buffer.
    /// </summary>
    public static string? GetFirstDirectionalShadowPublication()
        => Volatile.Read(ref s_firstDirectionalShadowPublication);

    /// <summary>
    /// Gets the first binding-39 write that was rejected because a different
    /// program owned the active capture.
    /// </summary>
    public static string? GetFirstDroppedDirectionalShadowPublication()
        => Volatile.Read(ref s_firstDroppedDirectionalShadowPublication);

    internal static void CaptureIndirectSnapshot(
        XRRenderProgram pendingStateProgram,
        VkRenderProgram preparedProgram,
        ComputeDispatchSnapshot bindingSnapshot,
        string contextName,
        XRFrameBuffer? target,
        int passIndex)
    {
        Volatile.Write(ref s_lastIndirectPendingProgram, pendingStateProgram);
        Volatile.Write(ref s_lastIndirectPreparedProgram, preparedProgram);
        Volatile.Write(ref s_lastIndirectContext, contextName);
        Volatile.Write(ref s_lastIndirectTarget, target);
        Volatile.Write(ref s_lastIndirectPassIndex, passIndex);
        Volatile.Write(
            ref s_lastIndirectSnapshotHadStorage,
            bindingSnapshot.HasReadOnlyStorageBindings ? 1 : 0);
        Volatile.Write(ref s_lastIndirectSnapshot, bindingSnapshot);
    }

    internal static void CaptureFirstDirectionalShadowPublication(
        VkRenderProgram program,
        ReadOnlyStorageBinding binding,
        bool accepted,
        VkRenderProgram? activeCaptureOwner)
    {
        XRRenderProgram? failedProgram =
            Volatile.Read(ref s_firstFailedDirectionalShadowProgram);
        if (binding.Binding != 39u ||
            !ReferenceEquals(program.Data, failedProgram))
        {
            return;
        }

        if (!accepted)
        {
            CaptureFirstDroppedDirectionalShadowPublication(
                program,
                binding,
                activeCaptureOwner);
            return;
        }

        if (Interlocked.CompareExchange(
                ref s_capturedDirectionalShadowPublication,
                1,
                0) != 0)
        {
            return;
        }

        string publication = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "[Vulkan.DirectionalShadowPublication] first binding-39 write: " +
            "program='{0}' hash={1} accepted={2} activeCaptureOwner='{3}' " +
            "activeCaptureMatches={4} publicationToken={5}. Stack:{6}{7}",
            program.Data.Name ?? "<unnamed>",
            RuntimeHelpers.GetHashCode(program.Data),
            accepted,
            activeCaptureOwner?.Data.Name ?? "<none>",
            ReferenceEquals(program, activeCaptureOwner),
            binding.Publication.TokenId,
            Environment.NewLine,
            Environment.StackTrace);
        Volatile.Write(ref s_firstDirectionalShadowPublication, publication);
        Debug.VulkanWarning("{0}", publication);
    }

    internal static void CaptureFirstUnpublishedDirectionalShadowRecords(
        XRRenderProgram pendingStateProgram,
        VkRenderProgram program,
        ComputeDispatchSnapshot bindingSnapshot,
        string contextName,
        XRFrameBuffer? target,
        int passIndex)
    {
        XRRenderProgram? failedProgram =
            Volatile.Read(ref s_firstFailedDirectionalShadowProgram);
        if (!ReferenceEquals(pendingStateProgram, failedProgram) ||
            bindingSnapshot.HasReadOnlyStorageBindings ||
            Volatile.Read(ref s_capturedUnpublishedDirectionalShadowRecords) != 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(
                ref s_capturedUnpublishedDirectionalShadowRecords,
                1,
                0) != 0)
        {
            return;
        }

        string failure = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "[Vulkan.IndirectSnapshot] first snapshot for the failed " +
            "directional-shadow program without immutable storage: " +
            "context='{0}' pass={1} target='{2}' snapshotId={3} " +
            "pendingProgram='{4}' hash={5} backendProgram='{6}' hash={7} " +
            "sourceIdentityMatches={8} " +
            "descriptorContract=previously-failed-DirectionalShadowRecordsBuffer. " +
            "Stack:{9}{10}",
            contextName,
            passIndex,
            target?.Name ?? "<swapchain>",
            RuntimeHelpers.GetHashCode(bindingSnapshot),
            pendingStateProgram.Name ?? "<unnamed>",
            RuntimeHelpers.GetHashCode(pendingStateProgram),
            program.Data.Name ?? "<unnamed>",
            RuntimeHelpers.GetHashCode(program.Data),
            ReferenceEquals(pendingStateProgram, program.Data),
            Environment.NewLine,
            Environment.StackTrace);
        Volatile.Write(ref s_firstUnpublishedDirectionalShadowRecords, failure);
        Debug.VulkanWarning("{0}", failure);
    }

    internal static void CaptureFirstDirectionalShadowRecordsFailure(
        DescriptorBindingInfo binding,
        VkRenderProgram? program,
        XRMesh? mesh,
        XRMaterial material,
        ComputeDispatchSnapshot? bindingSnapshot)
    {
        if (!string.Equals(
                binding.Name,
                DirectionalShadowRecordsBuffer,
                StringComparison.Ordinal))
        {
            return;
        }

        XRRenderProgram? programData = program?.Data;
        if (programData is not null)
        {
            Interlocked.CompareExchange(
                ref s_firstFailedDirectionalShadowProgram,
                programData,
                null);
        }

        if (Interlocked.CompareExchange(
                ref s_capturedDirectionalShadowRecordsFailure,
                1,
                0) != 0)
        {
            return;
        }

        string failure = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "[Vulkan.DescriptorResolution] first missing reflected " +
            "DirectionalShadowRecordsBuffer: set={0} binding={1} type={2} " +
            "currentProgram='{3}' currentProgramHash={4} shaders=[{5}] " +
            "mesh='{6}' material='{7}' bindingSnapshot={8} snapshotId={9} " +
            "capturedStorage={10} lastIndirectSnapshotId={11} " +
            "sameAsLastIndirectSnapshot={12} captureTimeStorage={13} " +
            "pendingProgram='{14}' pendingProgramHash={15} " +
            "preparedProgram='{16}' preparedProgramHash={17} " +
            "captureContext='{18}' capturePass={19} captureTarget='{20}'. " +
            "Stack:{21}{22}",
            binding.Set,
            binding.Binding,
            binding.DescriptorType,
            programData?.Name ?? "<null>",
            programData is null ? 0 : RuntimeHelpers.GetHashCode(programData),
            GetReflectedShaderPaths(programData),
            mesh?.Name ?? "<null>",
            material.Name ?? "<null>",
            bindingSnapshot is null ? "null" : "present",
            bindingSnapshot is null ? 0 : RuntimeHelpers.GetHashCode(bindingSnapshot),
            bindingSnapshot?.HasReadOnlyStorageBindings == true,
            GetLastIndirectSnapshotIdentity(),
            ReferenceEquals(
                bindingSnapshot,
                Volatile.Read(ref s_lastIndirectSnapshot)),
            Volatile.Read(ref s_lastIndirectSnapshotHadStorage) != 0,
            Volatile.Read(ref s_lastIndirectPendingProgram)?.Name ?? "<none>",
            GetLastIndirectPendingProgramIdentity(),
            Volatile.Read(ref s_lastIndirectPreparedProgram)?.Data?.Name ?? "<none>",
            GetLastIndirectPreparedProgramIdentity(),
            Volatile.Read(ref s_lastIndirectContext) ?? "<none>",
            Volatile.Read(ref s_lastIndirectPassIndex),
            Volatile.Read(ref s_lastIndirectTarget)?.Name ?? "<swapchain>",
            Environment.NewLine,
            Environment.StackTrace);
        Volatile.Write(ref s_firstDirectionalShadowRecordsFailure, failure);
        Debug.VulkanWarning("{0}", failure);
    }

    private static int GetLastIndirectSnapshotIdentity()
    {
        ComputeDispatchSnapshot? snapshot =
            Volatile.Read(ref s_lastIndirectSnapshot);
        return snapshot is null ? 0 : RuntimeHelpers.GetHashCode(snapshot);
    }

    private static int GetLastIndirectPendingProgramIdentity()
    {
        XRRenderProgram? program =
            Volatile.Read(ref s_lastIndirectPendingProgram);
        return program is null ? 0 : RuntimeHelpers.GetHashCode(program);
    }

    private static int GetLastIndirectPreparedProgramIdentity()
    {
        VkRenderProgram? program =
            Volatile.Read(ref s_lastIndirectPreparedProgram);
        return program?.Data is null ? 0 : RuntimeHelpers.GetHashCode(program.Data);
    }

    private static string GetReflectedShaderPaths(XRRenderProgram? program)
    {
        if (program is null || program.Shaders.Count == 0)
            return "<none>";

        StringBuilder paths = new();
        for (int index = 0; index < program.Shaders.Count; index++)
        {
            if (index != 0)
                paths.Append(", ");

            XRShader shader = program.Shaders[index];
            paths.Append(shader.Type)
                .Append(':')
                .Append(shader.Source?.FilePath ?? shader.FilePath ?? "<memory>");
        }

        return paths.ToString();
    }

    private static void CaptureFirstDroppedDirectionalShadowPublication(
        VkRenderProgram program,
        ReadOnlyStorageBinding binding,
        VkRenderProgram? activeCaptureOwner)
    {
        if (Interlocked.CompareExchange(
                ref s_capturedDroppedDirectionalShadowPublication,
                1,
                0) != 0)
        {
            return;
        }

        string publication = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "[Vulkan.DirectionalShadowPublication] failed-program binding-39 " +
            "write was dropped: program='{0}' hash={1} activeCaptureOwner='{2}' " +
            "activeCaptureMatches={3} publicationToken={4}. Stack:{5}{6}",
            program.Data.Name ?? "<unnamed>",
            RuntimeHelpers.GetHashCode(program.Data),
            activeCaptureOwner?.Data.Name ?? "<none>",
            ReferenceEquals(program, activeCaptureOwner),
            binding.Publication.TokenId,
            Environment.NewLine,
            Environment.StackTrace);
        Volatile.Write(ref s_firstDroppedDirectionalShadowPublication, publication);
        Debug.VulkanWarning("{0}", publication);
    }
}
