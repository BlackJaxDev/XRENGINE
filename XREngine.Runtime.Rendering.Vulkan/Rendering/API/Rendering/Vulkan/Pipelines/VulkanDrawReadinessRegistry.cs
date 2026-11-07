using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// The active draw readiness requests of one Vulkan renderer generation. <see cref="VkMeshRenderer.QueryDrawReadiness"/>
/// registers and renews a request; the pre-recording pipeline admission prewarms the pipelines of each active request for
/// the pass contexts of its view and publishes the result. A request that no query renewed for
/// <see cref="RequestLifetimeMilliseconds"/> ends, so a frame with no active query pays one volatile read.
/// </summary>
internal sealed class VulkanDrawReadinessRegistry
{
    /// <summary>The time after the last query when a request ends.</summary>
    internal const long RequestLifetimeMilliseconds = 2000;

    private readonly Lock _sync = new();
    private readonly List<VulkanDrawReadinessRequest> _requests = [];
    private int _count;

    /// <summary>True while at least one request is active. One volatile read; the frame loop checks it first.</summary>
    public bool HasActiveRequests => Volatile.Read(ref _count) > 0;

    /// <summary>
    /// Registers or renews a request and gives the result of its last frame-loop evaluation. A new request allocates one
    /// object; a known request allocates nothing.
    /// </summary>
    public ERenderDrawReadiness Observe(
        VkMeshRenderer renderer,
        XRMaterial material,
        XRRenderPipelineInstance? view,
        bool stereo,
        long nowTicks,
        out string reason)
    {
        lock (_sync)
        {
            VulkanDrawReadinessRequest? request = null;
            for (int i = 0; i < _requests.Count; i++)
            {
                if (_requests[i].Matches(renderer, material, view, stereo))
                {
                    request = _requests[i];
                    break;
                }
            }

            if (request is null)
            {
                request = new VulkanDrawReadinessRequest(renderer, material, view, stereo);
                _requests.Add(request);
                Volatile.Write(ref _count, _requests.Count);
            }

            request.LastQueryTicks = nowTicks;
            reason = request.Reason;
            return request.Evaluations == 0 ? ERenderDrawReadiness.NotStarted : request.State;
        }
    }

    /// <summary>
    /// Removes the requests that no query renewed in time and copies the active ones to <paramref name="destination"/>.
    /// The destination list is reused, so this allocates nothing after it has grown.
    /// </summary>
    public void CopyActive(List<VulkanDrawReadinessRequest> destination, long nowTicks)
    {
        destination.Clear();
        lock (_sync)
        {
            for (int i = _requests.Count - 1; i >= 0; i--)
                if (nowTicks - _requests[i].LastQueryTicks > RequestLifetimeMilliseconds)
                    _requests.RemoveAt(i);

            Volatile.Write(ref _count, _requests.Count);
            for (int i = 0; i < _requests.Count; i++)
                destination.Add(_requests[i]);
        }
    }

    /// <summary>Publishes the result of one frame-loop evaluation of a request.</summary>
    public void Publish(VulkanDrawReadinessRequest request, ERenderDrawReadiness state, string reason)
    {
        lock (_sync)
        {
            request.State = state;
            request.Reason = reason;
            request.Evaluations++;
        }
    }
}
