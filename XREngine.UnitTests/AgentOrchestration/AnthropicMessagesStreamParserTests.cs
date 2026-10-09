using System.Text.Json.Nodes;
using NUnit.Framework;
using Shouldly;
using XREngine.AgentOrchestration;

namespace XREngine.UnitTests.AgentOrchestration;

[TestFixture]
public class AnthropicMessagesStreamParserTests
{
    [Test]
    public void PreservesSignedThinkingToolInputAndCumulativeUsage()
    {
        var parser = new AnthropicMessagesStreamParser();
        Feed(parser, """{"type":"message_start","message":{"id":"msg_1","model":"claude-haiku-5-5","usage":{"input_tokens":4,"cache_read_input_tokens":3,"output_tokens":1}}}""");
        Feed(parser, """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""");
        Feed(parser, """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"check"}}""");
        Feed(parser, """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"signed"}}""");
        Feed(parser, """{"type":"content_block_stop","index":0}""");
        Feed(parser, """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"tool_1","name":"ping","input":{}}}""");
        Feed(parser, """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"value\":1}"}}""");
        Feed(parser, """{"type":"content_block_stop","index":1}""");
        Feed(parser, """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":5}}""");
        Feed(parser, """{"type":"message_stop"}""");

        AgentModelTurnResult result = parser.BuildResult(
            [new JsonObject { ["role"] = "user", ["content"] = "test" }]);

        result.ToolCalls.Single().ArgumentsJson.ShouldBe("""{"value":1}""");
        result.Usage.InputTokens.ShouldBe(7);
        result.Usage.OutputTokens.ShouldBe(5);
        result.Usage.TotalTokens.ShouldBe(12);
        result.ContinuationJson.ShouldContain("signed");
        result.ContinuationJson.ShouldContain("\"thinking\":\"check\"");
    }

    [Test]
    public void RejectsDuplicateBlockIndexAndWrongDeltaType()
    {
        var duplicate = StartParser();
        Feed(duplicate, """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""");
        Feed(duplicate, """{"type":"content_block_stop","index":0}""");
        Should.Throw<AgentModelException>(() => Feed(duplicate,
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""));

        var wrongDelta = StartParser();
        Feed(wrongDelta, """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""");
        Should.Throw<AgentModelException>(() => Feed(wrongDelta,
            """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{}"}}"""));
    }

    [Test]
    public void RejectsNonObjectToolInputAndUnfinishedMessage()
    {
        var parser = StartParser();
        Feed(parser, """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"tool_1","name":"ping","input":{}}}""");
        Feed(parser, """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"[]"}}""");
        Should.Throw<AgentModelException>(() => Feed(parser,
            """{"type":"content_block_stop","index":0}"""));

        var unfinished = StartParser();
        Feed(unfinished, """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""");
        Should.Throw<AgentModelException>(() => Feed(unfinished, """{"type":"message_stop"}"""));
    }

    [TestCase("max_tokens", AgentFailureCategory.BudgetExceeded)]
    [TestCase("refusal", AgentFailureCategory.ProviderError)]
    [TestCase("pause_turn", AgentFailureCategory.ProviderError)]
    public void RejectsNonTerminalSuccessStopReasons(string reason, AgentFailureCategory category)
    {
        var parser = StartParser();
        Feed(parser, """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""");
        Feed(parser, """{"type":"content_block_stop","index":0}""");
        Feed(parser, """{"type":"message_delta","delta":{"stop_reason":"STOP_REASON"}}"""
            .Replace("STOP_REASON", reason, StringComparison.Ordinal));
        Feed(parser, """{"type":"message_stop"}""");

        AgentModelException error = Should.Throw<AgentModelException>(() => parser.BuildResult([]));
        error.Category.ShouldBe(category);
    }

    private static AnthropicMessagesStreamParser StartParser()
    {
        var parser = new AnthropicMessagesStreamParser();
        Feed(parser, """{"type":"message_start","message":{"id":"msg_1","model":"claude-haiku-5-5"}}""");
        return parser;
    }

    private static void Feed(AnthropicMessagesStreamParser parser, string data)
        => parser.ProcessData(data, out _);
}
