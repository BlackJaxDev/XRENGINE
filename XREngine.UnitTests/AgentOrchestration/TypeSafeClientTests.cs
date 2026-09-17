using System.Net;
using System.Text.Json;
using NUnit.Framework;
using Shouldly;
using XREngine.AgentOrchestration.TypeSafe;

namespace XREngine.UnitTests.AgentOrchestration;

[TestFixture]
public class TypeSafeClientTests
{
    [Test]
    public void ReportsNotAvailableWhenApiKeyIsEmpty()
    {
        using var httpClient = new HttpClient(new QueueHttpMessageHandler());
        var client = new TypeSafeClient(httpClient, () => string.Empty);

        client.IsAvailable.ShouldBeFalse();
        client.TargetModel.ShouldBe("jev-latest");
    }

    [Test]
    public async Task EvaluateAsyncReturnsNullWhenApiKeyIsEmpty()
    {
        var handler = new QueueHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var client = new TypeSafeClient(httpClient, () => null);

        var result = await client.EvaluateAsync("test state", new Dictionary<string, TypeSafeQuestion>
        {
            ["test"] = new NoulQuestion { Instructions = "Is this a test?" }
        });

        result.ShouldBeNull();
        handler.RequestBodies.Count.ShouldBe(0);
    }

    [Test]
    public async Task SerializesQuestionsAndParsesAnswersCorrectly()
    {
        var handler = new QueueHttpMessageHandler();
        string mockResponseJson =
            """
            {
              "model": "jev-latest",
              "answers": {
                "dept": {
                  "type": "choice",
                  "choice": "technical",
                  "probabilities": { "billing": 0.1, "technical": 0.85, "sales": 0.05 },
                  "confidence": 0.82
                },
                "urgent": {
                  "type": "noul",
                  "noul": 0.94
                },
                "rating": {
                  "type": "score",
                  "score": 1.7,
                  "legend": { "0": "low", "1": "medium", "2": "high" },
                  "probabilities": { "0": 0.05, "1": 0.2, "2": 0.75 },
                  "confidence": 0.79
                }
              },
              "usage": {
                "input_tokens": 120,
                "output_tokens": 34
              }
            }
            """;

        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(mockResponseJson, System.Text.Encoding.UTF8, "application/json")
        });

        using var httpClient = new HttpClient(handler);
        var client = new TypeSafeClient(httpClient, () => "test-api-key");

        var questions = new Dictionary<string, TypeSafeQuestion>
        {
            ["dept"] = new ChoiceQuestion
            {
                Instructions = "Which team handles this?",
                Criteria = new Dictionary<string, string?>
                {
                    ["billing"] = "Billing",
                    ["technical"] = "Technical",
                    ["sales"] = "Sales"
                }
            },
            ["urgent"] = new NoulQuestion
            {
                Instructions = "Is this urgent?"
            },
            ["rating"] = new ScoreQuestion
            {
                Instructions = "Rate priority",
                Criteria = new[] { "low", "medium", "high" }
            }
        };

        var response = await client.EvaluateAsync("System server is failing", questions);

        response.ShouldNotBeNull();
        response.Model.ShouldBe("jev-latest");
        response.Answers.Count.ShouldBe(3);

        // Choice verification
        response.Answers["dept"].ShouldBeOfType<ChoiceAnswer>();
        var choice = (ChoiceAnswer)response.Answers["dept"];
        choice.Choice.ShouldBe("technical");
        choice.Confidence.ShouldBe(0.82);
        choice.Probabilities["technical"].ShouldBe(0.85);

        // Noul verification
        response.Answers["urgent"].ShouldBeOfType<NoulAnswer>();
        var noul = (NoulAnswer)response.Answers["urgent"];
        noul.Noul.ShouldBe(0.94);

        // Score verification
        response.Answers["rating"].ShouldBeOfType<ScoreAnswer>();
        var score = (ScoreAnswer)response.Answers["rating"];
        score.Score.ShouldBe(1.7);
        score.Confidence.ShouldBe(0.79);
        score.Legend["1"].ShouldBe("medium");

        // Usage verification
        response.Usage.ShouldNotBeNull();
        response.Usage.InputTokens.ShouldBe(120);
        response.Usage.OutputTokens.ShouldBe(34);

        // Verify request payload format
        handler.RequestBodies.Count.ShouldBe(1);
        using var parsedReq = JsonDocument.Parse(handler.RequestBodies[0]);
        parsedReq.RootElement.GetProperty("model").GetString().ShouldBe("jev-latest");
        parsedReq.RootElement.GetProperty("state").GetString().ShouldBe("System server is failing");
        var reqQuestions = parsedReq.RootElement.GetProperty("questions");
        reqQuestions.GetProperty("dept").GetProperty("type").GetString().ShouldBe("choice");
        reqQuestions.GetProperty("urgent").GetProperty("type").GetString().ShouldBe("noul");
        reqQuestions.GetProperty("rating").GetProperty("type").GetString().ShouldBe("score");
    }

    [Test]
    public async Task HandlesUnauthorized401Gracefully()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"unauthorized"}""", System.Text.Encoding.UTF8, "application/json")
        });

        bool warningLogged = false;
        using var httpClient = new HttpClient(handler);
        var client = new TypeSafeClient(
            httpClient,
            () => "invalid-or-waitlist-key",
            logWarning: msg => { warningLogged = true; });

        var result = await client.EvaluateAsync("state", new Dictionary<string, TypeSafeQuestion>
        {
            ["q"] = new NoulQuestion { Instructions = "Test?" }
        });

        result.ShouldBeNull();
        warningLogged.ShouldBeTrue();
    }

    [Test]
    public async Task RetriesOn429TooManyRequests()
    {
        var handler = new QueueHttpMessageHandler();
        // First attempt returns 429
        handler.Enqueue(_ => new HttpResponseMessage((HttpStatusCode)429));
        // Second attempt succeeds
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "model": "jev-latest",
                  "answers": {
                    "ok": { "type": "noul", "noul": 1.0 }
                  }
                }
                """,
                System.Text.Encoding.UTF8,
                "application/json")
        });

        using var httpClient = new HttpClient(handler);
        var client = new TypeSafeClient(httpClient, () => "valid-key");

        var result = await client.EvaluateAsync("state", new Dictionary<string, TypeSafeQuestion>
        {
            ["ok"] = new NoulQuestion { Instructions = "Test?" }
        });

        result.ShouldNotBeNull();
        handler.RequestBodies.Count.ShouldBe(2);
    }
}
