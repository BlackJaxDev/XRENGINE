namespace XREngine.AgentOrchestration;

/// <summary>Identifies the runtime and account route selected for a run.</summary>
public enum AgentExecutionMode
{
    Api,
    CodexAppServer,
    GeminiCli,
    CopilotCli,
    ClaudeCli,
    AntigravityCli,
}
