using NUnit.Framework;
using Shouldly;
using XREngine.AgentOrchestration;

namespace XREngine.UnitTests.AgentOrchestration;

[TestFixture]
public class OpenAiResponsesStreamParserTests
{
    [Test]
    public void ParsesTextFunctionArgumentDeltasMultipleCallsUsageAndContinuation()
    {
        var parser = new OpenAiResponsesStreamParser();
        parser.ProcessData(
            """{"type":"response.created","response":{"id":"resp_1","model":"gpt-5.6-terra"}}""",
            out _);
        parser.ProcessData(
            """{"type":"response.output_text.delta","delta":"hello "}""",
            out string firstDelta);
        parser.ProcessData(
            """{"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","call_id":"call_1","name":"get_node"}}""",
            out _);
        parser.ProcessData(
            """{"type":"response.function_call_arguments.delta","output_index":1,"delta":"{\"id\":"}""",
            out _);
        parser.ProcessData(
            """{"type":"response.function_call_arguments.done","output_index":1,"arguments":"{\"id\":\"a\"}"}""",
            out _);
        parser.ProcessData(
            """{"type":"response.output_item.added","output_index":2,"item":{"type":"function_call","call_id":"call_2","name":"list_nodes","arguments":"{}"}}""",
            out _);
        parser.ProcessData(
            """
            {"type":"response.completed","response":{"id":"resp_1","model":"gpt-5.6-terra","output":[{"type":"reasoning","encrypted_content":"opaque"},{"type":"function_call","call_id":"call_1","name":"get_node","arguments":"{\"id\":\"a\"}"},{"type":"function_call","call_id":"call_2","name":"list_nodes","arguments":"{}"}],"usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}}
            """,
            out _);

        AgentModelTurnResult result = parser.BuildResult(
            """[{"role":"user","content":"test"}]""");

        firstDelta.ShouldBe("hello ");
        result.ResponseId.ShouldBe("resp_1");
        result.ActualModel.ShouldBe("gpt-5.6-terra");
        result.OutputText.ShouldBe("hello ");
        result.ToolCalls.Count.ShouldBe(2);
        result.ToolCalls[0].CallId.ShouldBe("call_1");
        result.ToolCalls[0].ArgumentsJson.ShouldBe("""{"id":"a"}""");
        result.Usage.TotalTokens.ShouldBe(15);
        result.ContinuationJson.ShouldContain("encrypted_content");
        result.ContinuationJson.ShouldContain("call_2");
    }

    [Test]
    public void SkipsMalformedEventsAndCapturesGeneratedImages()
    {
        var parser = new OpenAiResponsesStreamParser();
        parser.ProcessData("{not-json", out _).ShouldBeFalse();
        parser.ProcessData(
            """
            {"type":"response.completed","response":{"id":"resp_image","model":"gpt-5.6-sol","output":[{"type":"image_generation_call","result":"aGVsbG8="}]}}
            """,
            out _);

        AgentModelTurnResult result = parser.BuildResult("[]");

        parser.MalformedEventCount.ShouldBe(1);
        result.OutputItems.ShouldContain(item =>
            item.Kind == AgentOutputItemKind.Image
            && item.DataUri == "data:image/png;base64,aGVsbG8=");
    }

    [Test]
    public void ProviderErrorEventRemainsAnError()
    {
        var parser = new OpenAiResponsesStreamParser();

        AgentModelException exception = Should.Throw<AgentModelException>(() =>
            parser.ProcessData(
                """{"type":"response.failed","response":{"error":{"message":"model unavailable"}}}""",
                out _));

        exception.Category.ShouldBe(AgentFailureCategory.ProviderError);
        exception.Message.ShouldBe("model unavailable");
    }
}
