namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Mutable debounce and resize-settle state for desktop swapchain recreation.
/// The policy remains renderer-executed because swapchain recreation and
/// framebuffer invalidation are still renderer-owned in this cut.
/// </summary>
internal sealed class VulkanDesktopSwapchainPolicyState
{
    private const int SceneViewportCapacity = 8;
    private const int ScreenSpaceUserInterfaceCapacity = 16;

    private readonly XRViewport[] _requiredSceneViewports = new XRViewport[SceneViewportCapacity];
    private readonly IRuntimeScreenSpaceUserInterface[] _requiredScreenSpaceUserInterfaces =
        new IRuntimeScreenSpaceUserInterface[ScreenSpaceUserInterfaceCapacity];
    private readonly XRViewport[] _requiredScreenSpaceUserInterfaceViewports =
        new XRViewport[ScreenSpaceUserInterfaceCapacity];
    private VulkanResidentTemplateDependencyLease? _heldPresentationSourceLease;

    internal bool FrameBufferInvalidated;
    internal long RecreateRequestedAt;
    internal long ResizeLastChangedAt;
    internal uint PendingSurfaceWidth;
    internal uint PendingSurfaceHeight;

    internal VulkanResizeReleaseHandoffState ResizeReleaseHandoffState { get; private set; }
    internal uint ResizeReleaseTargetWidth { get; private set; }
    internal uint ResizeReleaseTargetHeight { get; private set; }
    internal ulong ResizeReleaseSourceSwapchainGeneration { get; private set; }
    internal ulong ResizeReleaseSuccessorSwapchainGeneration { get; private set; }
    internal long ResizeReleaseArmedAt { get; private set; }
    internal bool RequiresSceneContributor { get; private set; }
    internal bool RequiresScreenSpaceUserInterfaceContributor { get; private set; }
    internal bool RequiresImGuiContributor { get; private set; }
    internal bool HasSuccessfulHeldPresent { get; private set; }
    internal int RequiredSceneViewportCount { get; private set; }
    internal int RequiredScreenSpaceUserInterfaceCount { get; private set; }
    internal bool ContributorCaptureOverflowed { get; private set; }
    internal VulkanPresentationSourceTuple HeldPresentationSource { get; private set; }

    internal bool HasActiveResizeReleaseHandoff
        => ResizeReleaseHandoffState != VulkanResizeReleaseHandoffState.Inactive;

    internal ReadOnlySpan<XRViewport> RequiredSceneViewports
        => _requiredSceneViewports.AsSpan(0, RequiredSceneViewportCount);

    internal ReadOnlySpan<IRuntimeScreenSpaceUserInterface> RequiredScreenSpaceUserInterfaces
        => _requiredScreenSpaceUserInterfaces.AsSpan(0, RequiredScreenSpaceUserInterfaceCount);

    internal ReadOnlySpan<XRViewport> RequiredScreenSpaceUserInterfaceViewports
        => _requiredScreenSpaceUserInterfaceViewports.AsSpan(
            0,
            RequiredScreenSpaceUserInterfaceCount);

    /// <summary>
    /// Starts collecting the contributors that authored one successfully presented held-resize frame.
    /// </summary>
    internal void BeginSuccessfulHeldPresentCapture()
    {
        Array.Clear(
            _requiredSceneViewports,
            0,
            RequiredSceneViewportCount);
        Array.Clear(
            _requiredScreenSpaceUserInterfaces,
            0,
            RequiredScreenSpaceUserInterfaceCount);
        Array.Clear(
            _requiredScreenSpaceUserInterfaceViewports,
            0,
            RequiredScreenSpaceUserInterfaceCount);
        RequiredSceneViewportCount = 0;
        RequiredScreenSpaceUserInterfaceCount = 0;
        ContributorCaptureOverflowed = false;
    }

    internal bool TryAddRequiredSceneViewport(XRViewport viewport, out string reason)
    {
        if (Contains(_requiredSceneViewports, RequiredSceneViewportCount, viewport))
        {
            reason = string.Empty;
            return true;
        }

        if (RequiredSceneViewportCount == _requiredSceneViewports.Length)
        {
            ContributorCaptureOverflowed = true;
            reason = $"The resize-release handoff supports at most {SceneViewportCapacity} scene viewports.";
            return false;
        }

        _requiredSceneViewports[RequiredSceneViewportCount++] = viewport;
        reason = string.Empty;
        return true;
    }

    internal bool TryAddRequiredScreenSpaceUserInterface(
        XRViewport viewport,
        IRuntimeScreenSpaceUserInterface userInterface,
        out string reason)
    {
        for (int index = 0; index < RequiredScreenSpaceUserInterfaceCount; index++)
            if (ReferenceEquals(_requiredScreenSpaceUserInterfaceViewports[index], viewport) &&
                ReferenceEquals(_requiredScreenSpaceUserInterfaces[index], userInterface))
            {
                reason = string.Empty;
                return true;
            }

        if (RequiredScreenSpaceUserInterfaceCount == _requiredScreenSpaceUserInterfaces.Length)
        {
            ContributorCaptureOverflowed = true;
            reason = $"The resize-release handoff supports at most {ScreenSpaceUserInterfaceCapacity} screen-space UI contributors.";
            return false;
        }

        int insertionIndex = RequiredScreenSpaceUserInterfaceCount++;
        _requiredScreenSpaceUserInterfaceViewports[insertionIndex] = viewport;
        _requiredScreenSpaceUserInterfaces[insertionIndex] = userInterface;
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Arms a handoff from a complete held-resize presentation. The captured contributors are the
    /// exact producers that must be ready before the replacement swapchain may be exposed.
    /// </summary>
    internal bool TryArmFromSuccessfulHeldPresent(
        uint targetWidth,
        uint targetHeight,
        ulong sourceSwapchainGeneration,
        in VulkanPresentationSourceTuple heldPresentationSource,
        VulkanResidentTemplateDependencyLease heldPresentationSourceLease,
        bool requiresSceneContributor,
        bool requiresScreenSpaceUserInterfaceContributor,
        bool requiresImGuiContributor,
        long armedAt,
        out string reason)
    {
        if (targetWidth == 0 || targetHeight == 0)
        {
            reason = "The resize-release target extent is empty.";
            return false;
        }

        if (sourceSwapchainGeneration == 0)
        {
            reason = "The source swapchain generation is not published.";
            return false;
        }

        if (!heldPresentationSource.IsComplete ||
            heldPresentationSourceLease is null ||
            !heldPresentationSourceLease.IsActive)
        {
            reason = "The held-resize presentation source is not pinned and replayable.";
            return false;
        }

        if (ContributorCaptureOverflowed)
        {
            reason = "The held-resize contributor capture exceeded its fixed capacity.";
            return false;
        }

        if (requiresSceneContributor && RequiredSceneViewportCount == 0)
        {
            reason = "The held-resize frame requires a scene contributor, but none was captured.";
            return false;
        }

        if (requiresScreenSpaceUserInterfaceContributor &&
            RequiredScreenSpaceUserInterfaceCount == 0)
        {
            reason = "The held-resize frame requires screen-space UI, but none was captured.";
            return false;
        }

        ResizeReleaseHandoffState = VulkanResizeReleaseHandoffState.AwaitingReadyToRecreate;
        ResizeReleaseTargetWidth = targetWidth;
        ResizeReleaseTargetHeight = targetHeight;
        ResizeReleaseSourceSwapchainGeneration = sourceSwapchainGeneration;
        ResizeReleaseSuccessorSwapchainGeneration = 0;
        ResizeReleaseArmedAt = armedAt;
        RequiresSceneContributor = requiresSceneContributor;
        RequiresScreenSpaceUserInterfaceContributor = requiresScreenSpaceUserInterfaceContributor;
        RequiresImGuiContributor = requiresImGuiContributor;
        HasSuccessfulHeldPresent = true;
        if (!ReferenceEquals(
                _heldPresentationSourceLease,
                heldPresentationSourceLease))
        {
            _heldPresentationSourceLease?.Dispose();
            _heldPresentationSourceLease = heldPresentationSourceLease;
        }
        HeldPresentationSource = heldPresentationSource;
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Updates the release target while the user continues a held resize. A new target always
    /// requires a successor-ready check, even when the source generation has not changed.
    /// </summary>
    internal bool TryRebaseForInteractiveResize(
        uint targetWidth,
        uint targetHeight,
        ulong sourceSwapchainGeneration,
        long rebasedAt,
        out string reason)
    {
        if (targetWidth == 0 || targetHeight == 0 || sourceSwapchainGeneration == 0)
        {
            reason = "Cannot rebase the resize-release handoff to an unpublished target.";
            return false;
        }

        if (!HasSuccessfulHeldPresent)
        {
            reason = "Cannot rebase a resize-release handoff before a successful held presentation.";
            return false;
        }

        ResizeReleaseHandoffState = VulkanResizeReleaseHandoffState.AwaitingReadyToRecreate;
        ResizeReleaseTargetWidth = targetWidth;
        ResizeReleaseTargetHeight = targetHeight;
        ResizeReleaseSourceSwapchainGeneration = sourceSwapchainGeneration;
        ResizeReleaseSuccessorSwapchainGeneration = 0;
        ResizeReleaseArmedAt = rebasedAt;
        reason = string.Empty;
        return true;
    }

    internal bool TryTransitionAfterSuccessfulRecreate(
        ulong successorSwapchainGeneration,
        out string reason)
    {
        if (ResizeReleaseHandoffState != VulkanResizeReleaseHandoffState.AwaitingReadyToRecreate)
        {
            reason = "The resize-release handoff is not awaiting swapchain recreation.";
            return false;
        }

        if (successorSwapchainGeneration == 0 ||
            successorSwapchainGeneration == ResizeReleaseSourceSwapchainGeneration)
        {
            reason = "The recreated swapchain did not publish a distinct successor generation.";
            return false;
        }

        ResizeReleaseSuccessorSwapchainGeneration = successorSwapchainGeneration;
        ResizeReleaseHandoffState = VulkanResizeReleaseHandoffState.AwaitingSuccessorPresent;
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Moves an active successor-present handoff to a newer swapchain generation after an
    /// intervening successful recreation. The release target remains exact; only the unpublished
    /// successor generation may advance.
    /// </summary>
    internal bool TryRebaseSuccessorAfterSuccessfulRecreate(
        ulong successorSwapchainGeneration,
        uint successorWidth,
        uint successorHeight,
        out string reason)
    {
        if (ResizeReleaseHandoffState != VulkanResizeReleaseHandoffState.AwaitingSuccessorPresent)
        {
            reason = "The resize-release handoff is not awaiting a successor presentation.";
            return false;
        }

        if (successorWidth != ResizeReleaseTargetWidth ||
            successorHeight != ResizeReleaseTargetHeight)
        {
            reason = "The recreated swapchain extent does not match the resize-release target.";
            return false;
        }

        if (successorSwapchainGeneration == 0 ||
            successorSwapchainGeneration == ResizeReleaseSourceSwapchainGeneration ||
            successorSwapchainGeneration == ResizeReleaseSuccessorSwapchainGeneration)
        {
            reason = "The recreated swapchain did not publish a distinct successor generation.";
            return false;
        }

        ResizeReleaseSuccessorSwapchainGeneration = successorSwapchainGeneration;
        reason = string.Empty;
        return true;
    }

    internal bool TryCompleteAfterSuccessorPresent(
        ulong presentedSwapchainGeneration,
        out string reason)
    {
        if (ResizeReleaseHandoffState != VulkanResizeReleaseHandoffState.AwaitingSuccessorPresent)
        {
            reason = "The resize-release handoff is not awaiting a successor presentation.";
            return false;
        }

        if (presentedSwapchainGeneration != ResizeReleaseSuccessorSwapchainGeneration)
        {
            reason = "The completed presentation did not use the resize-release successor generation.";
            return false;
        }

        CancelResizeReleaseHandoff();
        reason = string.Empty;
        return true;
    }

    internal bool TryGetHeldPresentationSource(
        out VulkanPresentationSourceTuple source,
        out VulkanResidentTemplateDependencyLease? lease)
    {
        source = HeldPresentationSource;
        lease = _heldPresentationSourceLease;
        return HasActiveResizeReleaseHandoff &&
            lease is { IsActive: true } &&
            source.IsComplete;
    }

    /// <summary>Cancels the active handoff when its retained source can no longer be trusted.</summary>
    internal void CancelResizeReleaseHandoff()
    {
        _heldPresentationSourceLease?.Dispose();
        _heldPresentationSourceLease = null;
        HeldPresentationSource = default;
        ResizeReleaseHandoffState = VulkanResizeReleaseHandoffState.Inactive;
        ResizeReleaseTargetWidth = 0;
        ResizeReleaseTargetHeight = 0;
        ResizeReleaseSourceSwapchainGeneration = 0;
        ResizeReleaseSuccessorSwapchainGeneration = 0;
        ResizeReleaseArmedAt = 0;
        RequiresSceneContributor = false;
        RequiresScreenSpaceUserInterfaceContributor = false;
        RequiresImGuiContributor = false;
        HasSuccessfulHeldPresent = false;
        BeginSuccessfulHeldPresentCapture();
    }

    /// <summary>
    /// Resets ordinary recreate debounce state after a successful swapchain recreation. An active
    /// resize-release handoff is intentionally retained until its successor is actually presented.
    /// </summary>
    internal void ResetAfterRecreate()
    {
        FrameBufferInvalidated = false;
        RecreateRequestedAt = 0;
        ResizeLastChangedAt = 0;
        PendingSurfaceWidth = 0;
        PendingSurfaceHeight = 0;
    }

    private static bool Contains<T>(T[] values, int count, T value)
        where T : class
    {
        for (int index = 0; index < count; index++)
        {
            if (ReferenceEquals(values[index], value))
                return true;
        }

        return false;
    }
}
