using System.Threading;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// The active draw readiness requests of one Vulkan renderer generation. <see cref="VkMeshRenderer.QueryDrawReadiness"/>
/// registers and renews a request; the pre-recording pipeline admission prewarms the pipelines of each active request for
/// the pass contexts of its view and publishes the result with the program it measured. A request ends when the open ends
/// it (<see cref="RemoveRenderer"/>), when its renderer is deleted or unlinked, or when no query renewed it for
/// <see cref="RequestLifetimeMilliseconds"/>. A frame with no active request pays one volatile read.
/// </summary>
internal sealed class VulkanDrawReadinessRegistry
{
    /// <summary>The time after the last query when a request ends.</summary>
    internal const long RequestLifetimeMilliseconds = 2000;

    private const string ProgramChangedReason = "the program changed after the last pipeline check";

    private readonly Lock _sync = new();
    private readonly List<VulkanDrawReadinessRequest> _requests = [];
    private int _count;

    /// <summary>True while at least one request is active. One volatile read; the frame loop checks it first.</summary>
    public bool HasActiveRequests => Volatile.Read(ref _count) > 0;

    /// <summary>
    /// Registers or renews a request and gives the result of its last frame-loop evaluation, with the program and link
    /// generation that the evaluation measured. A new request allocates one object; a known request allocates nothing.
    /// </summary>
    public ERenderDrawReadiness Observe(
        VkMeshRenderer renderer,
        XRMaterial material,
        RenderingParameters? renderOptionsOverride,
        XRRenderPipelineInstance? view,
        bool stereo,
        long nowTicks,
        out string reason,
        out object? publishedProgram,
        out ulong publishedLinkGeneration)
    {
        lock (_sync)
        {
            VulkanDrawReadinessRequest? request = null;
            for (int i = 0; i < _requests.Count; i++)
            {
                if (_requests[i].Matches(renderer, material, renderOptionsOverride, view, stereo))
                {
                    request = _requests[i];
                    break;
                }
            }

            if (request is null)
            {
                request = new VulkanDrawReadinessRequest(renderer, material, renderOptionsOverride, view, stereo);
                _requests.Add(request);
                Volatile.Write(ref _count, _requests.Count);
            }

            request.LastQueryTicks = nowTicks;
            reason = request.Reason;
            publishedProgram = request.PublishedProgram;
            publishedLinkGeneration = request.PublishedLinkGeneration;
            return request.Evaluations == 0 ? ERenderDrawReadiness.NotStarted : request.State;
        }
    }

    /// <summary>
    /// Checks a published result against the current program of the renderer. A ready result that another program or link
    /// generation measured is stale, so it gives <see cref="ERenderDrawReadiness.Compiling"/>: the new program needs its own
    /// pipelines.
    /// </summary>
    public static ERenderDrawReadiness ResolvePublishedState(
        ERenderDrawReadiness publishedState,
        string publishedReason,
        object? publishedProgram,
        ulong publishedLinkGeneration,
        object? currentProgram,
        ulong currentLinkGeneration,
        out string reason)
    {
        if (publishedState == ERenderDrawReadiness.Ready &&
            (!ReferenceEquals(publishedProgram, currentProgram) || publishedLinkGeneration != currentLinkGeneration))
        {
            reason = ProgramChangedReason;
            return ERenderDrawReadiness.Compiling;
        }

        reason = publishedReason;
        return publishedState;
    }

    /// <summary>
    /// Removes the requests that no query renewed in time, or whose renderer cannot draw any more, and copies the active ones
    /// to <paramref name="destination"/>. The destination list is reused, so this allocates nothing after it has grown.
    /// </summary>
    public void CopyActive(List<VulkanDrawReadinessRequest> destination, long nowTicks)
    {
        destination.Clear();
        lock (_sync)
        {
            for (int i = _requests.Count - 1; i >= 0; i--)
            {
                VulkanDrawReadinessRequest request = _requests[i];
                if (nowTicks - request.LastQueryTicks > RequestLifetimeMilliseconds ||
                    (request.Renderer is { } renderer && !renderer.CanServeDrawReadiness))
                    _requests.RemoveAt(i);
            }

            Volatile.Write(ref _count, _requests.Count);
            for (int i = 0; i < _requests.Count; i++)
                destination.Add(_requests[i]);
        }
    }

    /// <summary>Ends every request of a renderer. The renderer calls it when it is deleted or unlinked, and the open at its end.</summary>
    public void RemoveRenderer(VkMeshRenderer renderer)
    {
        lock (_sync)
        {
            for (int i = _requests.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_requests[i].Renderer, renderer))
                    _requests.RemoveAt(i);
            Volatile.Write(ref _count, _requests.Count);
        }
    }

    /// <summary>
    /// Records that a frame plan of the view had no pass context for the request. It changes only the reason, and only while
    /// no frame measured the request, so a plan with no context never replaces a measured result.
    /// </summary>
    public void NoteNoContext(VulkanDrawReadinessRequest request, string reason)
    {
        lock (_sync)
        {
            if (request.Evaluations == 0)
                request.Reason = reason;
        }
    }

    /// <summary>Publishes the result of one frame-loop evaluation of a request, with the program it measured.</summary>
    public void Publish(
        VulkanDrawReadinessRequest request,
        ERenderDrawReadiness state,
        string reason,
        object? program,
        ulong linkGeneration)
    {
        lock (_sync)
        {
            request.State = state;
            request.Reason = reason;
            request.PublishedProgram = program;
            request.PublishedLinkGeneration = linkGeneration;
            request.Evaluations++;
        }
    }
}
