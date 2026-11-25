using XREngine.Components;
using XREngine.Rendering.Commands;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.Vulkan;

/// <summary>Owns the exact selected world UI image until this frame settles.</summary>
internal sealed partial class VulkanAcceptedFramePlan
{
    private VulkanWorldUiNativeProof? _worldUiProof;
    private UIWorldCollectPacketSet? _worldUiPackets;
    private UIPublication? _worldUiPublication;
    private UICanvasComponent? _worldUiCanvas;
    private XRWindow? _worldUiWindow;
    private XRViewport? _worldUiViewport;
    private RenderCommandCollection? _worldUiCommands;
    private long _worldUiPackageGeneration;
    private long _worldUiPacketGeneration;
    private ulong _worldUiReceiptId;
    private ulong _worldUiDispatchToken;
    private ulong _worldUiEngineFrameId;
    private ulong _worldUiOutputGeneration;
    private uint _worldUiSubmittedImage;
    private ulong _worldUiSubmittedFrame;
    private bool _worldUiRequired;
    private bool _worldUiTerminalized;
    private bool _worldUiSubmitted;

    internal VulkanWorldUiNativeProof? WorldUiProof => _worldUiProof;
    internal bool RequiresWorldUiOutput => _worldUiRequired;
    internal ulong RequiredWorldUiReceiptId => _worldUiReceiptId;

    private void CaptureRequiredWorldUiOutput(VulkanRenderer owner)
    {
        XRWindow window = owner.XRWindow;
        if (!owner.SupportsCheckedWorldUiOutput)
        {
            if (window.HasRequiredVulkanWorldUiImage)
                throw new VulkanPlanPreconditionException(
                    "A selected Vulkan world UI image lost checked output capability.");
            for (int index = 0; index < window.Viewports.Count; index++)
                if (window.Viewports[index].HasRenderingRetainedWorldUiImage)
                    throw new VulkanPlanPreconditionException(
                        "A selected Vulkan world UI image has no checked output capability.");
            return;
        }
        if (!window.HasRequiredVulkanWorldUiImage)
        {
            for (int index = 0; index < window.Viewports.Count; index++)
                if (window.Viewports[index].HasRenderingRetainedWorldUiImage)
                    throw new VulkanPlanPreconditionException(
                        "A Vulkan world UI image appeared after dispatch selection.");
            return;
        }

        _worldUiRequired = true;
        ulong engineFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
        if (window.VulkanWorldUiRenderFrameId != engineFrameId ||
            !window.TryGetRequiredVulkanWorldUiAttempt(
                out XRViewport? viewport,
                out RenderCommandCollection? commands,
                out long packageGeneration,
                out ulong receiptId,
                out ulong nativeToken) ||
            viewport?.World is not RuntimeWorldRenderer world ||
            commands is null || packageGeneration <= 0 ||
            commands.RenderingBackendReadyPackage.PackageGeneration !=
                packageGeneration ||
            !commands.TryRetainRenderingWorldUiImage(world.WorldContext,
                packageGeneration, out UIWorldCollectPacketSet? packets,
                out UIPublication? publication) ||
            packets is null || publication is null)
            throw new VulkanPlanPreconditionException(
                "The selected Vulkan world UI image has no exact retained package.");

        _worldUiPackets = packets;
        _worldUiPublication = publication;
        _worldUiCanvas = null;
        _worldUiWindow = window;
        _worldUiViewport = viewport;
        _worldUiCommands = commands;
        _worldUiPackageGeneration = packageGeneration;
        _worldUiPacketGeneration = packets.Generation;
        _worldUiReceiptId = receiptId;
        _worldUiDispatchToken = window.ActiveWorldUiDispatchToken;
        _worldUiEngineFrameId = engineFrameId;
        if (packets.Failed || !packets.IsSealed || packets.Count != 1 ||
            !packets.TryGetAt(0, out object? key, out UIPublication? selected) ||
            key is not UICanvasComponent canvas ||
            !ReferenceEquals(selected, publication) ||
            !owner.TryClaimPendingUiWorldOutput(engineFrameId, receiptId,
                out VulkanWorldUiNativeProof proof))
        {
            TerminalizeRequiredWorldUiOutput();
            throw new VulkanPlanPreconditionException(
                "The Vulkan world UI proof does not match its selected packet.");
        }
        _worldUiCanvas = canvas;
        _worldUiProof = proof;
        if (
            proof.NativeToken != nativeToken ||
            proof.DispatchToken != _worldUiDispatchToken ||
            proof.BackendGeneration != owner.BackendGeneration ||
            !ReferenceEquals(proof.Publication, publication))
        {
            TerminalizeRequiredWorldUiOutput();
            throw new VulkanPlanPreconditionException(
                "The Vulkan world UI proof does not match its selected packet.");
        }
    }

    private void BindRequiredWorldUiOutput(
        in RenderOutputRequest output, VulkanRenderer owner)
    {
        if (!_worldUiRequired)
            return;
        if (_worldUiTerminalized || _worldUiProof is not { } proof ||
            _worldUiPackets is not { } packets ||
            packets.Generation != _worldUiPacketGeneration ||
            packets.Failed || !packets.IsSealed ||
            _worldUiCommands is not { } commands ||
            _worldUiWindow is not { } worldWindow ||
            commands.RenderingBackendReadyPackage.PackageGeneration !=
                _worldUiPackageGeneration ||
            !worldWindow.TryGetRequiredVulkanWorldUiAttempt(
                out XRViewport? viewport,
                out RenderCommandCollection? currentCommands,
                out long currentGeneration,
                out ulong currentReceipt,
                out ulong currentNativeToken) ||
            !ReferenceEquals(viewport, _worldUiViewport) ||
            !ReferenceEquals(currentCommands, commands) ||
            currentGeneration != _worldUiPackageGeneration ||
            currentReceipt != _worldUiReceiptId ||
            currentNativeToken != proof.NativeToken ||
            output.FrameId != FrameId ||
            output.OutputKind != EFrameOutputKind.DesktopScene ||
            output.WorkClass != ERenderOutputWorkClass.PresentNow ||
            output.Target.TargetGeneration == 0UL ||
            !IsUiPublicationReceiptBound(_worldUiReceiptId) ||
            !proof.TryBindAcceptedPlan(this, LogicalPlanGeneration,
                owner.BackendGeneration))
            throw new VulkanPlanPreconditionException(
                "The Vulkan world UI attempt lost its accepted output image.");
        _worldUiOutputGeneration = output.Target.TargetGeneration;
    }

    internal bool IsRequiredWorldUiReadyForSubmission(ulong frameId,
        ulong outputGeneration)
        => !_worldUiRequired ||
            !_worldUiTerminalized && _worldUiProof is { } proof &&
            proof.IsRecordingComplete && proof.IsStillValid &&
            FrameId == frameId &&
            _worldUiOutputGeneration == outputGeneration &&
            ReferenceEquals(proof.Publication, _worldUiPublication) &&
            _worldUiPackets?.Generation == _worldUiPacketGeneration &&
            _worldUiCommands?.RenderingBackendReadyPackage.PackageGeneration ==
                _worldUiPackageGeneration &&
            _worldUiWindow is { } worldWindow &&
            worldWindow.VulkanWorldUiRenderFrameId == _worldUiEngineFrameId &&
            worldWindow.TryGetRequiredVulkanWorldUiAttempt(
                out XRViewport? currentViewport,
                out RenderCommandCollection? currentCommands,
                out long currentGeneration,
                out ulong currentReceipt,
                out ulong currentNativeToken) &&
            ReferenceEquals(currentViewport, _worldUiViewport) &&
            ReferenceEquals(currentCommands, _worldUiCommands) &&
            currentGeneration == _worldUiPackageGeneration &&
            currentReceipt == _worldUiReceiptId &&
            currentNativeToken == proof.NativeToken &&
            _worldUiViewport is { } selectedViewport &&
            _worldUiCanvas is { } selectedCanvas &&
            _worldUiPublication is { } selectedPublication &&
            worldWindow.HasStagedVulkanWorldUiPublication(
                _worldUiEngineFrameId, selectedViewport, selectedCanvas,
                selectedPublication, _worldUiReceiptId);

    internal bool TryAttachRequiredWorldUiSubmission(uint imageIndex,
        ulong outputGeneration, ulong sourceFrameId)
    {
        if (!_worldUiRequired)
            return true;
        if (_worldUiSubmitted ||
            !IsRequiredWorldUiReadyForSubmission(sourceFrameId,
                outputGeneration) ||
            !VulkanUiPublicationReceiptLedger.TryAttachSubmission(
                _worldUiReceiptId, imageIndex, outputGeneration,
                sourceFrameId))
            return false;
        _worldUiSubmitted = true;
        _worldUiSubmittedImage = imageIndex;
        _worldUiSubmittedFrame = sourceFrameId;
        return true;
    }

    internal bool IsRequiredWorldUiReadyForPresent(uint imageIndex,
        ulong outputGeneration, ulong sourceFrameId)
        => !_worldUiRequired ||
            _worldUiSubmitted &&
            _worldUiSubmittedImage == imageIndex &&
            _worldUiSubmittedFrame == sourceFrameId &&
            IsRequiredWorldUiReadyForSubmission(sourceFrameId,
                outputGeneration) &&
            _worldUiWindow is { } worldWindow &&
            worldWindow.VulkanWorldUiRenderFrameId == _worldUiEngineFrameId &&
            worldWindow.TryGetRequiredVulkanWorldUiAttempt(
                out _, out _, out long packageGeneration,
                out ulong receiptId, out ulong nativeToken) &&
            packageGeneration == _worldUiPackageGeneration &&
            receiptId == _worldUiReceiptId &&
            nativeToken == _worldUiProof?.NativeToken;

    internal void FailRequiredWorldUiOutput()
    {
        if (_worldUiRequired)
            _worldUiProof?.Fail();
    }

    internal bool TryGetRequiredWorldUiPublication(
        out XRViewport? viewport, out UICanvasComponent? canvas,
        out UIPublication? publication, out ulong receiptId)
    {
        viewport = _worldUiViewport;
        canvas = _worldUiCanvas;
        publication = _worldUiPublication;
        receiptId = _worldUiReceiptId;
        return _worldUiRequired && !_worldUiTerminalized &&
            viewport is not null && canvas is not null &&
            publication is not null && receiptId != 0UL;
    }

    private void TerminalizeRequiredWorldUiOutput()
    {
        VulkanWorldUiNativeProof? proof = _worldUiProof;
        UIWorldCollectPacketSet? packets = _worldUiPackets;
        _worldUiProof = null;
        _worldUiPackets = null;
        _worldUiTerminalized |= _worldUiRequired;
        Exception? failure = null;
        if (proof is not null)
        {
            ulong nativeToken = proof.NativeToken;
            try { proof.Fail(); }
            catch (Exception error) { failure = error; }
            try { proof.ReleasePlanOwnership(nativeToken); }
            catch (Exception error) { failure ??= error; }
        }
        try { packets?.Release(); }
        catch (Exception error) { failure ??= error; }
        if (failure is not null)
            throw failure;
    }

    private void ClearRequiredWorldUiOutput()
    {
        try
        {
            TerminalizeRequiredWorldUiOutput();
        }
        finally
        {
            _worldUiPublication = null;
            _worldUiCanvas = null;
            _worldUiWindow = null;
            _worldUiViewport = null;
            _worldUiCommands = null;
            _worldUiPackageGeneration = _worldUiPacketGeneration = 0;
            _worldUiReceiptId = _worldUiDispatchToken = 0UL;
            _worldUiEngineFrameId = 0UL;
            _worldUiOutputGeneration = _worldUiSubmittedFrame = 0UL;
            _worldUiSubmittedImage = 0U;
            _worldUiRequired = _worldUiTerminalized = _worldUiSubmitted = false;
        }
    }
}
