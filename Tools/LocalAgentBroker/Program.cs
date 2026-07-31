using System.Text;

namespace XREngine.LocalAgentBroker;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        try
        {
            BrokerConfiguration configuration = BrokerConfiguration.Parse(args);
            using var httpClient = new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            await using var registry = new AgentRunRegistry(configuration, httpClient);
            var server = new McpStdioServer(registry, Console.In, Console.Out, Console.Error);
            await server.RunAsync(CancellationToken.None);
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"Local agent broker failed: {exception.Message}");
            return 1;
        }
    }
}
