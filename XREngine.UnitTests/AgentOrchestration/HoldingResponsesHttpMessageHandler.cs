using System.Net;
using System.Text;
using System.Text.Json;
using XREngine.LocalAgentBroker;

namespace XREngine.UnitTests.AgentOrchestration;

/// <summary>Holds fake provider requests open to observe the registry's actual parallel admission.</summary>
internal sealed class HoldingResponsesHttpMessageHandler(int expectedConcurrentRequests) : HttpMessageHandler
{
    private readonly TaskCompletionSource _allEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _requestCount;
    private int _activeRequests;
    private int _peakRequests;

    public int RequestCount => Volatile.Read(ref _requestCount);
    public int PeakRequests => Volatile.Read(ref _peakRequests);
    public Task AllEntered => _allEntered.Task;

    public void Release() => _release.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using JsonDocument body = JsonDocument.Parse(
            await request.Content!.ReadAsStringAsync(cancellationToken));
        JsonElement payload = body.RootElement;
        if (payload.GetProperty("model").GetString() != AgentModelCatalog.Sol61
            || payload.GetProperty("reasoning").GetProperty("effort").GetString() != "max"
            || payload.GetProperty("store").GetBoolean()
            || payload.TryGetProperty("tools", out _))
        {
            throw new InvalidOperationException("The fake run lost its exact model, effort, or tool contract.");
        }

        int activeRequests = Interlocked.Increment(ref _activeRequests);
        InterlockedMax(ref _peakRequests, activeRequests);
        if (Interlocked.Increment(ref _requestCount) == expectedConcurrentRequests)
            _allEntered.TrySetResult();
        try
        {
            await _release.Task.WaitAsync(cancellationToken);
            string responseId = $"resp_{Guid.NewGuid():N}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$$$"""
                    data: {"type":"response.created","response":{"id":"{{{{responseId}}}}","model":"gpt-6.1-sol"}}

                    data: {"type":"response.completed","response":{"id":"{{{{responseId}}}}","model":"gpt-6.1-sol","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"fake complete"}]}],"usage":{"input_tokens":5,"output_tokens":2,"total_tokens":7}}}

                    data: [DONE]

                    """, Encoding.UTF8, "text/event-stream"),
            };
        }
        finally
        {
            Interlocked.Decrement(ref _activeRequests);
        }
    }

    private static void InterlockedMax(ref int location, int candidate)
    {
        int previous = Volatile.Read(ref location);
        while (candidate > previous)
        {
            int observed = Interlocked.CompareExchange(ref location, candidate, previous);
            if (observed == previous)
                return;
            previous = observed;
        }
    }
}
