using System.Net;

namespace XREngine.UnitTests.AgentOrchestration;

internal sealed class QueueHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<string> RequestBodies { get; } = [];

    public void EnqueueSse(string events)
        => _responses.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(events),
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return response;
        });

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> response)
        => _responses.Enqueue(response);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));
        if (_responses.Count == 0)
            throw new InvalidOperationException("No fake HTTP response was queued.");
        return _responses.Dequeue()(request);
    }
}
