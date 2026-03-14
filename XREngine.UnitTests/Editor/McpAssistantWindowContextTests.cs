using NUnit.Framework;
using Shouldly;
using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using XREngine;
using XREngine.Editor.UI.Tools;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public class McpAssistantWindowContextTests
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type WindowType = typeof(McpAssistantWindow);
    private static readonly Type ProviderEnumType = WindowType.GetNestedType("ProviderType", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ProviderType enum not found.");
    private static readonly Type ChatMessageType = WindowType.GetNestedType("ChatMessage", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ChatMessage type not found.");
    private static readonly FieldInfo HistoryField = WindowType.GetField("_history", InstanceFlags)
        ?? throw new InvalidOperationException("_history field not found.");
    private static readonly FieldInfo ConversationSummaryField = WindowType.GetField("_conversationSummary", InstanceFlags)
        ?? throw new InvalidOperationException("_conversationSummary field not found.");
    private static readonly FieldInfo HistoryCompactedThroughField = WindowType.GetField("_historyCompactedThroughExclusive", InstanceFlags)
        ?? throw new InvalidOperationException("_historyCompactedThroughExclusive field not found.");
    private static readonly Type ToolCallEntryType = WindowType.GetNestedType("ToolCallEntry", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ToolCallEntry type not found.");
    private static readonly MethodInfo AutoCompactMethod = WindowType.GetMethod("AutoCompactConversationContextIfNeededAsync", InstanceFlags)
        ?? throw new InvalidOperationException("AutoCompactConversationContextIfNeededAsync method not found.");
    private static readonly MethodInfo BuildContextBlockMethod = WindowType.GetMethod("BuildConversationContextBlock", InstanceFlags)
        ?? throw new InvalidOperationException("BuildConversationContextBlock method not found.");

    private EditorPreferences? _previousGlobalEditorPreferences;
    private EditorPreferencesOverrides? _previousEditorPreferencesOverrides;

    [SetUp]
    public void SetUp()
    {
        _previousGlobalEditorPreferences = Engine.GlobalEditorPreferences;
        _previousEditorPreferencesOverrides = Engine.EditorPreferencesOverrides;

        Engine.GlobalEditorPreferences = new EditorPreferences
        {
            McpAssistantAutoSummarizeNearContextLimit = true,
            McpAssistantMaxTokens = 64_000,
            McpAssistantGitHubModelsModel = "openai/gpt-4.1"
        };
        Engine.EditorPreferencesOverrides = new EditorPreferencesOverrides();
    }

    [TearDown]
    public void TearDown()
    {
        Engine.GlobalEditorPreferences = _previousGlobalEditorPreferences ?? new EditorPreferences();
        Engine.EditorPreferencesOverrides = _previousEditorPreferencesOverrides ?? new EditorPreferencesOverrides();
    }

    [Test]
    public void AutoCompactConversationContextIfNeeded_SummarizesOlderTurnsAndKeepsRecentTail()
    {
        var window = new McpAssistantWindow();
        IList history = (IList)(HistoryField.GetValue(window) ?? throw new InvalidOperationException("History list missing."));

        string droppedPayloadMarker = "RAW_OLDEST_PAYLOAD_SHOULD_NOT_SURVIVE_RAW_CONTEXT";
        string oldRequest = "Older request " + new string('a', 512) + droppedPayloadMarker + new string('a', 90_000);
        string oldResponse = "Older response " + new string('b', 90_000);
        string recentUser = "Recent user KEEP_RECENT_USER_CONTEXT " + new string('c', 90_000);
        string recentAssistant = "Recent assistant KEEP_RECENT_ASSISTANT_CONTEXT " + new string('d', 90_000);

        history.Add(CreateChatMessage("user", oldRequest));
        object oldAssistantMessage = CreateChatMessage("assistant", oldResponse);
        AddToolCall(oldAssistantMessage, "list_scene_nodes", "world=unit-test", "NODE-123 remains available for reference");
        history.Add(oldAssistantMessage);
        history.Add(CreateChatMessage("user", recentUser));
        object activeAssistantMessage = CreateChatMessage("assistant", recentAssistant);
        history.Add(activeAssistantMessage);

        var task = (Task)(AutoCompactMethod.Invoke(window,
        [
            Enum.ToObject(ProviderEnumType, 3),
            "How should we continue?",
            activeAssistantMessage,
            2,
            CancellationToken.None
        ]) ?? throw new InvalidOperationException("Compaction task missing."));
        task.GetAwaiter().GetResult();

        string summary = (string)(ConversationSummaryField.GetValue(window) ?? string.Empty);
        int compactedThrough = (int)(HistoryCompactedThroughField.GetValue(window) ?? 0);
        string context = (string)(BuildContextBlockMethod.Invoke(window, [activeAssistantMessage, 300_000])
            ?? throw new InvalidOperationException("Context block result missing."));

        compactedThrough.ShouldBe(2);
        summary.ShouldContain("Compacted 2 earlier messages:");
        summary.ShouldContain("Older request");
        summary.ShouldNotContain("NODE-123 remains available for reference");
        context.ShouldContain("SUMMARY:");
        context.ShouldContain("Compacted 2 earlier messages:");
        context.ShouldContain("TOOL RESPONSE REFERENCES:");
        context.ShouldContain("NODE-123 remains available for reference");
        context.ShouldContain("KEEP_RECENT_USER_CONTEXT");
        context.ShouldContain("KEEP_RECENT_ASSISTANT_CONTEXT");
        context.ShouldNotContain(droppedPayloadMarker);
    }

    private static void AddToolCall(object message, string toolName, string argsSummary, string contextResultSummary)
    {
        PropertyInfo toolCallsProperty = ChatMessageType.GetProperty("ToolCalls", InstanceFlags)
            ?? throw new InvalidOperationException("ToolCalls property not found.");
        IList toolCalls = (IList)(toolCallsProperty.GetValue(message) ?? throw new InvalidOperationException("ToolCalls list missing."));

        object toolCall = Activator.CreateInstance(ToolCallEntryType)
            ?? throw new InvalidOperationException("Failed to create ToolCallEntry instance.");

        SetAutoPropertyBackingField(toolCall, "ToolName", toolName);
        SetAutoPropertyBackingField(toolCall, "ArgsSummary", argsSummary);
        SetAutoPropertyBackingField(toolCall, "ContextResultSummary", contextResultSummary);
        SetAutoPropertyBackingField(toolCall, "IsComplete", true);

        toolCalls.Add(toolCall);
    }

    private static object CreateChatMessage(string role, string content)
    {
        object message = Activator.CreateInstance(ChatMessageType)
            ?? throw new InvalidOperationException("Failed to create ChatMessage instance.");

        SetAutoPropertyBackingField(message, "Role", role);
        SetAutoPropertyBackingField(message, "Content", content);
        SetAutoPropertyBackingField(message, "Timestamp", new DateTime(2026, 3, 12, 12, 0, 0, DateTimeKind.Local));
        return message;
    }

    private static void SetAutoPropertyBackingField(object target, string propertyName, object value)
    {
        FieldInfo backingField = target.GetType().GetField($"<{propertyName}>k__BackingField", InstanceFlags)
            ?? throw new InvalidOperationException($"Backing field for {propertyName} not found.");
        backingField.SetValue(target, value);
    }
}